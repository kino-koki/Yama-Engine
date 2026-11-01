// ════════════════════════════════════════════════════════════════════════
// yama引擎 - 真实场景渲染器 (SceneRendererReal)
//
// 替代 NullRenderDevice 的真实渲染器，将 3D 网格通过 RHI 绘制命令渲染到屏幕。
//
// 核心功能:
//   1. 懒初始化: 编译内建 HLSL 着色器、创建根签名、创建 PSO
//   2. 常量缓冲区管理: FrameConstants(b0) / ObjectConstants(b1) / LineConstants(b0)
//   3. 网格 GPU 资源缓存: 按 meshPath 缓存顶点/索引缓冲区
//   4. RenderScene: 遍历场景网格，录制真实 DrawIndexedInstanced 命令
//   5. RenderGrid / RenderLines: 线段叠加层渲染
//   6. 内置默认立方体: 空场景时也能看到 3D 内容
//
// 对接系统: RHI (RHIDevice) / Rendering (IRenderScene) / Modeling (EditableMesh)
// ════════════════════════════════════════════════════════════════════════

using System;
using System.Collections.Generic;
using System.Numerics;
using YamaEngine.Modeling;
using YamaEngine.RHI;
using YamaEngine.Particles;
using YamaEngine.VFX;

namespace YamaEngine.Rendering
{
    // ──────────────────────────────────────────────────────────────
    // 相机数据
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 渲染用相机数据。包含视图/投影矩阵及相机参数，
    /// 由外部 (如 EditorViewport) 填充后传入渲染器。
    /// </summary>

    // ──────────────────────────────────────────────────────────────
    // 网格 GPU 资源
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 已上传到 GPU 的网格资源缓存。
    /// 包含顶点缓冲区、索引缓冲区及其尺寸信息。
    /// </summary>
    public sealed class MeshGPUResources : IDisposable
    {
        /// <summary>顶点缓冲区 (Default 堆)</summary>
        public RHIBuffer VertexBuffer;
        /// <summary>索引缓冲区 (Default 堆)</summary>
        public RHIBuffer IndexBuffer;
        /// <summary>顶点数量</summary>
        public int VertexCount;
        /// <summary>索引数量 (面数 * 3)</summary>
        public int IndexCount;
        /// <summary>顶点缓冲区字节数</summary>
        public ulong VertexBufferSizeInBytes;
        /// <summary>索引缓冲区字节数</summary>
        public ulong IndexBufferSizeInBytes;

        public void Dispose()
        {
            VertexBuffer?.Dispose();
            IndexBuffer?.Dispose();
            VertexBuffer = null;
            IndexBuffer = null;
        }
    }

    // ──────────────────────────────────────────────────────────────
    // 真实场景渲染器
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 真实场景渲染器: 通过 RHI API 录制 GPU 绘制命令，将 3D 网格渲染到渲染目标。
    /// 替代 NullRenderDevice 中仅统计 mesh 数量不发出任何 GPU 命令的 stub 行为。
    /// </summary>
    public class SceneRendererReal : IDisposable
    {
        // ── 设备引用 ──
        private readonly RHIDevice _device;

        // ── 网格管线资源 (懒初始化) ──
        private bool _meshPipelineInitialized;
        private RHIRootSignature _rootSignature;
        private RHIPipelineState _meshPSO;
        private RHIShaderBytecode _meshVS;
        private RHIShaderBytecode _meshPS;

        // ── 线段管线资源 (懒初始化) ──
        private bool _linePipelineInitialized;
        private RHIRootSignature _lineRootSignature;
        private RHIPipelineState _linePSO;
        private RHIShaderBytecode _lineVS;
        private RHIShaderBytecode _linePS;

        // ── 常量缓冲区 ──
        private RHIBuffer _frameCB;   // b0: FrameConstants
        private RHIBuffer _objectCB;  // b1: ObjectConstants
        private RHIBuffer _lineCB;    // b0: LineConstants (线段管线)

        // ── 可复用的常量数据数组 (避免每帧分配) ──
        private readonly BuiltinShaders.FrameConstants[] _frameDataArray = new BuiltinShaders.FrameConstants[1];
        private readonly BuiltinShaders.ObjectConstants[] _objectDataArray = new BuiltinShaders.ObjectConstants[1];
        private readonly BuiltinShaders.LineConstants[] _lineDataArray = new BuiltinShaders.LineConstants[1];

        // ── 网格 GPU 资源缓存 ──
        private readonly Dictionary<string, MeshGPUResources> _meshCache = new();

        // ── 骨骼蒙皮管线资源 (懒初始化) ──
        private bool _skinnedMeshPipelineInitialized;
        private RHIRootSignature _skinnedRootSignature;
        private RHIPipelineState _skinnedMeshPSO;
        private RHIShaderBytecode _skinnedMeshVS;

        // ── 骨骼矩阵上传缓冲区 ──
        private RHIBuffer? _boneMatrixBuffer;
        private const int MaxBones = 256;
        private const int BoneMatrixBufferSize = MaxBones * 64; // 256 * sizeof(float4x4)
        private readonly Matrix4x4[] _boneMatrixUploadArray = new Matrix4x4[MaxBones];

        // ── 骨骼蒙皮网格 GPU 资源缓存 ──
        private readonly Dictionary<string, MeshGPUResources> _skinnedMeshCache = new();

        // ── 默认立方体 (空场景时使用) ──
        private MeshGPUResources _defaultCube;

        // ── 动态线段顶点缓冲区 ──
        private RHIBuffer _lineVB;
        private uint _lineVBSize;

        // ── 默认纹理（简化版：1x1 白色占位，确保纹理采样器管道通） ──
        private RHITexture _defaultTexture;
        private RHIDescriptorHandle _defaultTextureSRV;

        // ── GPU 粒子渲染 ──
        private ParticleRenderer? _particleRenderer;
        private FlowVFXRenderBridge? _particleBridge;

        /// <summary>
        /// 当前活跃的 FlowVFX 粒子模拟（由外部设置，用于每帧渲染）。
        /// </summary>
        public FlowVFXParticleSimulationReal? ActiveParticleSimulation { get; set; }

        /// <summary>
        /// 设置 GPU 粒子渲染器。
        /// </summary>
        public void SetParticleRenderer(ParticleRenderer renderer)
        {
            _particleRenderer = renderer;
        }

        /// <summary>
        /// 设置 FlowVFX → 渲染桥接器。
        /// </summary>
        public void SetParticleBridge(FlowVFXRenderBridge bridge)
        {
            _particleBridge = bridge;
        }

        // ── 纹理管理 ──
        private TextureManager? _textureManager;
        private readonly Dictionary<string, RHIDescriptorHandle> _textureSRVCache = new();
        private RHIDescriptorHandle _lastBoundTextureSRV;

        // ── 后处理系统 ──
        private PostProcessSystem? _postProcess;

        // ── 阴影系统 ──
        private ShadowMapSystem? _shadowMapSystem;
        private RHIBuffer? _shadowCB;   // b3: ShadowConstants
        private readonly BuiltinShaders.ShadowConstants[] _shadowDataArray = new BuiltinShaders.ShadowConstants[1];
        private Vector3 _currentLightDirection = new Vector3(-0.5f, -0.8f, -0.3f);

        // ── D3D12 常量缓冲区对齐要求 ──
        private const uint ConstantBufferAlignment = 256;

        // ════════════════════════════════════════════════════════════
        //  构造与释放
        // ════════════════════════════════════════════════════════════

        /// <summary>创建真实场景渲染器。</summary>
        /// <param name="device">RHI 设备 (不能为 null)。</param>
        public SceneRendererReal(RHIDevice device)
        {
            _device = device ?? throw new ArgumentNullException(nameof(device));
            _shadowMapSystem = new ShadowMapSystem(_device);
        }

        public SceneRendererReal(RHIDevice device, RenderGraph graph) : this(device)
        {
            _renderGraph = graph;
        }

        private RenderGraph? _renderGraph;
        public RenderGraph? RenderGraph => _renderGraph;

        /// <summary>设置视口尺寸 (像素)。</summary>
        public void SetViewport(float width, float height)
        {
            _viewportWidth = width;
            _viewportHeight = height;
        }

        private float _viewportWidth;
        private float _viewportHeight;

        // ════════════════════════════════════════════════════════════
        //  场景集成接口 (供 WorldIntegrationHelper 注册外部子系统)
        // ════════════════════════════════════════════════════════════

        /// <summary>当前场景名称。</summary>
        public string SceneName { get; set; } = "YamaScene";

        /// <summary>场景资源管理器: 管理外部子系统注册的 GPU 缓冲 / 纹理。</summary>
        public SceneResourceManager ResourceManager { get; } = new SceneResourceManager();

        // ── 帧生命周期事件 ──
        /// <summary>场景帧开始时触发 (MicroCluster / ProbeField 用于 BeginFrame)。</summary>
        public event Action? OnSceneBeginFrame;
        /// <summary>场景帧结束时触发 (MicroCluster / ProbeField 用于 EndFrame)。</summary>
        public event Action? OnSceneEndFrame;

        // ── 图元变更事件 (ProbeField 用于触发 GI 更新) ──
        /// <summary>场景图元移动 / 变换变化时触发。</summary>
        public event Action<int, Matrix4x4>? OnPrimitiveTransformChanged;
        /// <summary>场景图元被添加时触发。</summary>
        public event Action<RenderPrimitive>? OnPrimitiveAdded;
        /// <summary>场景图元被移除时触发。</summary>
        public event Action<int>? OnPrimitiveRemoved;

        // ── 已注册子系统提供商 ──
        private readonly Dictionary<string, CullProviderDelegate> _cullProviders = new();
        private readonly Dictionary<string, RenderProviderDelegate> _renderProviders = new();
        private readonly Dictionary<string, StreamingHandlerDelegate> _streamingHandlers = new();

        /// <summary>注册剔除提供商 (视锥剔除 / 遮挡剔除阶段调用)。</summary>
        public void RegisterCullProvider(string name, CullProviderDelegate provider)
        {
            if (!string.IsNullOrEmpty(name) && provider != null)
                _cullProviders[name] = provider;
        }

        /// <summary>注册渲染提供商 (在对应渲染通道执行时调用)。</summary>
        public void RegisterRenderProvider(string name, RenderProviderDelegate provider)
        {
            if (!string.IsNullOrEmpty(name) && provider != null)
                _renderProviders[name] = provider;
        }

        /// <summary>注册流式加载处理器 (LOD 资源的流式加载 / 卸载)。</summary>
        public void RegisterStreamingHandler(string name, StreamingHandlerDelegate handler)
        {
            if (!string.IsNullOrEmpty(name) && handler != null)
                _streamingHandlers[name] = handler;
        }

        /// <summary>触发场景帧开始事件。</summary>
        public void NotifySceneBeginFrame() => OnSceneBeginFrame?.Invoke();

        /// <summary>触发场景帧结束事件。</summary>
        public void NotifySceneEndFrame() => OnSceneEndFrame?.Invoke();

        /// <summary>设置纹理管理器（由 EditorViewport 在创建时调用）。</summary>
        public void SetTextureManager(TextureManager texManager)
        {
            _textureManager = texManager;
        }

        /// <summary>设置后处理系统（由 EditorViewport 在创建时调用）。</summary>
        public void SetPostProcessSystem(PostProcessSystem? postProcess)
        {
            _postProcess = postProcess;
        }

        /// <summary>获取或加载纹理 SRV。缓存已加载的纹理 SRV，避免重复创建。</summary>
        private RHIDescriptorHandle GetOrLoadTextureSRV(string textureAssetPath)
        {
            if (string.IsNullOrEmpty(textureAssetPath))
                return _defaultTextureSRV;

            if (_textureSRVCache.TryGetValue(textureAssetPath, out var cached))
                return cached;

            if (_textureManager == null || _device == null)
                return _defaultTextureSRV;

            // 通过 TextureManager 加载纹理
            var texture = _textureManager.LoadFromFile(textureAssetPath);
            if (texture == null)
                return _defaultTextureSRV;

            // 优先使用 TextureManager 内部缓存的 SRV
            var srv = _textureManager.GetCachedSRV(textureAssetPath) ?? _device.CreateSRV(texture);
            if (!srv.IsValid)
                return _defaultTextureSRV;

            _textureSRVCache[textureAssetPath] = srv;
            return srv;
        }

        /// <summary>释放所有 GPU 资源。</summary>
        public void Dispose()
        {
            foreach (var kvp in _meshCache)
                kvp.Value?.Dispose();
            _meshCache.Clear();

            foreach (var kvp in _skinnedMeshCache)
                kvp.Value?.Dispose();
            _skinnedMeshCache.Clear();

            _defaultCube?.Dispose();
            _defaultCube = null;

            _lineVB?.Dispose();
            _lineVB = null;
            _lineVBSize = 0;

            _defaultTexture?.Dispose();
            _defaultTexture = null;

            _frameCB?.Dispose();
            _objectCB?.Dispose();
            _lineCB?.Dispose();
            _boneMatrixBuffer?.Dispose();
            _frameCB = null;
            _objectCB = null;
            _lineCB = null;
            _boneMatrixBuffer = null;

            _meshPSO?.Dispose();
            _linePSO?.Dispose();
            _skinnedMeshPSO?.Dispose();
            _rootSignature?.Dispose();
            _lineRootSignature?.Dispose();
            _skinnedRootSignature?.Dispose();
            _meshPSO = null;
            _linePSO = null;
            _skinnedMeshPSO = null;
            _rootSignature = null;
            _lineRootSignature = null;
            _skinnedRootSignature = null;

            _meshPipelineInitialized = false;
            _linePipelineInitialized = false;
            _skinnedMeshPipelineInitialized = false;

            _shadowMapSystem?.Dispose();
            _shadowMapSystem = null;
            _shadowCB?.Dispose();
            _shadowCB = null;
        }

        // ════════════════════════════════════════════════════════════
        //  懒初始化: 着色器 / 根签名 / PSO / 常量缓冲区
        // ════════════════════════════════════════════════════════════

        /// <summary>
        /// 懒初始化网格渲染管线: 编译着色器、创建根签名、创建 PSO、创建常量缓冲区。
        /// 首次调用 RenderScene 时触发。
        /// </summary>
        private void EnsureMeshPipelineInitialized()
        {
            if (_meshPipelineInitialized) return;
            _meshPipelineInitialized = true;

            // ── 编译着色器 ──
            // 注: RHIShaderModel 枚举最低为 SM_5_1，着色器使用 SM 5.0 语法，与 5.1 完全兼容。
            _meshVS = _device.CompileShader(
                BuiltinShaders.MeshVertexShader, "main",
                RHIShaderStage.Vertex, RHIShaderModel.SM_5_1, "MeshVS");

            _meshPS = _device.CompileShader(
                BuiltinShaders.MeshPixelShader, "main",
                RHIShaderStage.Pixel, RHIShaderModel.SM_5_1, "MeshPS");

            // ── 创建根签名 ──
            // 参数0: CBV(b0) — FrameConstants (视图/投影矩阵/光照)
            // 参数1: CBV(b1) — ObjectConstants (世界矩阵/材质)
            // 参数2: DescriptorTable(SRV t0) — AlbedoTexture
            // 参数3: CBV(b3) — ShadowConstants (光源VP矩阵/阴影参数)
            // 参数4: DescriptorTable(SRV t1) — ShadowMap
            var rootSigDesc = new RHIRootSignatureDesc
            {
                Parameters = new[]
                {
                    new RHIRootParameter
                    {
                        ParameterType = RHIRootParameterType.CBV,
                        ShaderVisibility = RHIShaderVisibility.All,
                        Descriptor = new RHIRootDescriptor { ShaderRegister = 0, RegisterSpace = 0 }
                    },
                    new RHIRootParameter
                    {
                        ParameterType = RHIRootParameterType.CBV,
                        ShaderVisibility = RHIShaderVisibility.All,
                        Descriptor = new RHIRootDescriptor { ShaderRegister = 1, RegisterSpace = 0 }
                    },
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
                                    BaseShaderRegister = 0,
                                    RegisterSpace = 0,
                                    OffsetInDescriptorsFromTableStart = 0
                                }
                            }
                        }
                    },
                    new RHIRootParameter
                    {
                        ParameterType = RHIRootParameterType.CBV,
                        ShaderVisibility = RHIShaderVisibility.All,
                        Descriptor = new RHIRootDescriptor { ShaderRegister = 3, RegisterSpace = 0 }
                    },
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
                                    BaseShaderRegister = 1,
                                    RegisterSpace = 0,
                                    OffsetInDescriptorsFromTableStart = 0
                                }
                            }
                        }
                    }
                },
                StaticSamplers = new[]
                {
                    new RHISamplerDesc
                    {
                        Filter = RHIFilter.Linear,
                        AddressU = RHITextureAddressMode.Wrap,
                        AddressV = RHITextureAddressMode.Wrap,
                        AddressW = RHITextureAddressMode.Wrap,
                        ShaderRegister = 0,
                        RegisterSpace = 0,
                        ShaderVisibility = RHIShaderVisibility.Pixel
                    },
                    new RHISamplerDesc
                    {
                        Filter = RHIFilter.ComparisonMinMagMipLinear,
                        AddressU = RHITextureAddressMode.Clamp,
                        AddressV = RHITextureAddressMode.Clamp,
                        AddressW = RHITextureAddressMode.Clamp,
                        ComparisonFunc = RHIComparison.LessEqual,
                        ShaderRegister = 1,
                        RegisterSpace = 0,
                        ShaderVisibility = RHIShaderVisibility.Pixel
                    }
                },
                AllowInputAssembler = true,
                Flags = RHIRootSignatureFlags.AllowInputAssemblerInputLayout
            };
            _rootSignature = _device.CreateRootSignature(rootSigDesc, "MeshRootSignature");

            // ── 创建 PSO ──
            var psoDesc = new RHIGraphicsPipelineDesc
            {
                Name = "MeshPSO",
                VS = _meshVS,
                PS = _meshPS,
                InputLayout = new[]
                {
                    new RHIInputElement("POSITION", RHIFormat.R32G32B32_Float, 0, 0),
                    new RHIInputElement("NORMAL",   RHIFormat.R32G32B32_Float, 0, 12),
                    new RHIInputElement("TEXCOORD", RHIFormat.R32G32_Float,    0, 24)
                },
                PrimitiveTopology = RHIPrimitiveTopology.TriangleList,
                BlendState = RHIBlendDesc.Default,
                DepthStencilState = new RHIDepthDesc
                {
                    DepthEnable = true,
                    DepthWriteEnable = true,
                    DepthFunc = RHIComparison.LessEqual,
                    StencilEnable = false
                },
                RasterizerState = RHIRasterizerDesc.Default,
                RTVFormats = new[] { RHIFormat.R8G8B8A8_UNorm },
                DSVFormat = RHIFormat.D32_Float,
                RootSignature = _rootSignature
            };
            _meshPSO = _device.CreateGraphicsPipelineState(psoDesc, "MeshPSO");

            // ── 创建常量缓冲区 (Default 堆, 256 字节对齐) ──
            var cbDesc = new RHIBufferDesc
            {
                SizeInBytes = ConstantBufferAlignment,
                HeapType = RHIResourceHeapType.Default,
                Flags = RHIBufferFlags.ConstantBuffer,
                Stride = ConstantBufferAlignment
            };
            _frameCB = _device.CreateBuffer(cbDesc, "FrameConstantsCB");
            _frameCB.CurrentState = RHIResourceState.Common;

            _objectCB = _device.CreateBuffer(cbDesc, "ObjectConstantsCB");
            _objectCB.CurrentState = RHIResourceState.Common;

            _shadowCB = _device.CreateBuffer(cbDesc, "ShadowConstantsCB");
            _shadowCB.CurrentState = RHIResourceState.Common;
        }

        /// <summary>
        /// 懒初始化线段渲染管线: 编译线段着色器、创建根签名、创建 PSO、创建线段常量缓冲区。
        /// 首次调用 RenderGrid / RenderLines 时触发。
        /// </summary>
        private void EnsureLinePipelineInitialized()
        {
            if (_linePipelineInitialized) return;
            _linePipelineInitialized = true;

            // ── 编译线段着色器 ──
            _lineVS = _device.CompileShader(
                BuiltinShaders.LineVertexShader, "main",
                RHIShaderStage.Vertex, RHIShaderModel.SM_5_1, "LineVS");

            _linePS = _device.CompileShader(
                BuiltinShaders.LinePixelShader, "main",
                RHIShaderStage.Pixel, RHIShaderModel.SM_5_1, "LinePS");

            // ── 创建根签名 (仅一个 CBV: b0 = LineConstants) ──
            var rootSigDesc = new RHIRootSignatureDesc
            {
                Parameters = new[]
                {
                    new RHIRootParameter
                    {
                        ParameterType = RHIRootParameterType.CBV,
                        ShaderVisibility = RHIShaderVisibility.All,
                        Descriptor = new RHIRootDescriptor { ShaderRegister = 0, RegisterSpace = 0 }
                    }
                },
                AllowInputAssembler = true,
                Flags = RHIRootSignatureFlags.AllowInputAssemblerInputLayout
            };
            _lineRootSignature = _device.CreateRootSignature(rootSigDesc, "LineRootSignature");

            // ── 创建线段 PSO ──
            var psoDesc = new RHIGraphicsPipelineDesc
            {
                Name = "LinePSO",
                VS = _lineVS,
                PS = _linePS,
                InputLayout = new[]
                {
                    new RHIInputElement("POSITION", RHIFormat.R32G32B32_Float,      0, 0),
                    new RHIInputElement("COLOR",    RHIFormat.R32G32B32A32_Float,   0, 12)
                },
                PrimitiveTopology = RHIPrimitiveTopology.LineList,
                BlendState = RHIBlendDesc.AlphaBlend,
                DepthStencilState = RHIDepthDesc.DepthRead,
                RasterizerState = RHIRasterizerDesc.NoCull,
                RTVFormats = new[] { RHIFormat.R8G8B8A8_UNorm },
                DSVFormat = RHIFormat.D32_Float,
                RootSignature = _lineRootSignature
            };
            _linePSO = _device.CreateGraphicsPipelineState(psoDesc, "LinePSO");

            // ── 创建线段常量缓冲区 ──
            var cbDesc = new RHIBufferDesc
            {
                SizeInBytes = ConstantBufferAlignment,
                HeapType = RHIResourceHeapType.Default,
                Flags = RHIBufferFlags.ConstantBuffer,
                Stride = ConstantBufferAlignment
            };
            _lineCB = _device.CreateBuffer(cbDesc, "LineConstantsCB");
            _lineCB.CurrentState = RHIResourceState.Common;
        }

        /// <summary>
        /// 懒初始化默认立方体网格。空场景时也能看到 3D 内容。
        /// </summary>
        private void EnsureDefaultCube()
        {
            if (_defaultCube != null) return;

            var cube = EditableMesh.CreateCube("DefaultCube", 1.0f);
            _defaultCube = UploadMeshInternal(cube);
        }

        /// <summary>
        /// 懒初始化默认纹理（1x1 白色）。简化版占位，确保纹理采样器管道通。
        /// </summary>
        public void EnsureDefaultTexture()
        {
            if (_defaultTexture != null) return;

            var texDesc = new RHITextureDesc
            {
                Dimension = RHIResourceDimension.Texture2D,
                Format = RHIFormat.R8G8B8A8_UNorm,
                Width = 1,
                Height = 1,
                DepthOrArraySize = 1,
                MipLevels = 1,
                SampleCount = 1,
                Flags = RHITextureFlags.ShaderResource
            };
            _defaultTexture = _device.CreateTexture(texDesc, "DefaultWhiteTexture");
            _defaultTexture.CurrentState = RHIResourceState.CopyDest;

            // 1x1 白色像素 (RGBA8)
            byte[] whitePixel = new byte[] { 255, 255, 255, 255 };
            _device.UploadToTexture(_defaultTexture, whitePixel, 1, 1);

            _defaultTexture.CurrentState = RHIResourceState.PixelShaderResource;
            _defaultTextureSRV = _device.CreateSRV(_defaultTexture, 0, 1);
        }

        /// <summary>
        /// 确保动态线段顶点缓冲区足够大，不够则重建。
        /// </summary>
        private void EnsureLineVB(uint requiredSize)
        {
            if (_lineVB != null && _lineVBSize >= requiredSize) return;

            _lineVB?.Dispose();

            // 最小分配 4KB 以减少频繁重建
            _lineVBSize = Math.Max(requiredSize, 4096);
            var desc = new RHIBufferDesc
            {
                SizeInBytes = _lineVBSize,
                HeapType = RHIResourceHeapType.Default,
                Flags = RHIBufferFlags.VertexBuffer,
                Stride = (ulong)BuiltinShaders.LineVertexStride
            };
            _lineVB = _device.CreateBuffer(desc, "LineVB_Dynamic");
            _lineVB.CurrentState = RHIResourceState.Common;
        }

        // ════════════════════════════════════════════════════════════
        //  骨骼蒙皮管线初始化
        // ════════════════════════════════════════════════════════════

        /// <summary>
        /// 懒初始化骨骼蒙皮渲染管线: 编译蒙皮 VS、创建根签名 (含 b2 骨骼矩阵)、创建 PSO、创建骨骼矩阵 CB。
        /// </summary>
        private void EnsureSkinnedPipelineInitialized()
        {
            if (_skinnedMeshPipelineInitialized) return;
            _skinnedMeshPipelineInitialized = true;

            // 编译骨骼蒙皮顶点着色器
            _skinnedMeshVS = _device.CompileShader(
                SkinnedMeshShaders.SkinnedMeshVertexShader, "main",
                RHIShaderStage.Vertex, RHIShaderModel.SM_5_1, "SkinnedMeshVS");

            // 像素着色器复用常规 MeshPS
            if (!_meshPS.IsValid)
            {
                _meshPS = _device.CompileShader(
                    BuiltinShaders.MeshPixelShader, "main",
                    RHIShaderStage.Pixel, RHIShaderModel.SM_5_1, "MeshPS");
            }

            // 创建根签名: CBV(b0 Frame) + CBV(b1 Object) + CBV(b2 BoneMatrices) + DescriptorTable(SRV t0) + CBV(b3 Shadow) + DescriptorTable(SRV t1 ShadowMap)
            var rootSigDesc = new RHIRootSignatureDesc
            {
                Parameters = new[]
                {
                    new RHIRootParameter
                    {
                        ParameterType = RHIRootParameterType.CBV,
                        ShaderVisibility = RHIShaderVisibility.All,
                        Descriptor = new RHIRootDescriptor { ShaderRegister = 0, RegisterSpace = 0 }
                    },
                    new RHIRootParameter
                    {
                        ParameterType = RHIRootParameterType.CBV,
                        ShaderVisibility = RHIShaderVisibility.All,
                        Descriptor = new RHIRootDescriptor { ShaderRegister = 1, RegisterSpace = 0 }
                    },
                    new RHIRootParameter
                    {
                        ParameterType = RHIRootParameterType.CBV,
                        ShaderVisibility = RHIShaderVisibility.All,
                        Descriptor = new RHIRootDescriptor { ShaderRegister = 2, RegisterSpace = 0 }
                    },
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
                                    BaseShaderRegister = 0,
                                    RegisterSpace = 0,
                                    OffsetInDescriptorsFromTableStart = 0
                                }
                            }
                        }
                    },
                    new RHIRootParameter
                    {
                        ParameterType = RHIRootParameterType.CBV,
                        ShaderVisibility = RHIShaderVisibility.All,
                        Descriptor = new RHIRootDescriptor { ShaderRegister = 3, RegisterSpace = 0 }
                    },
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
                                    BaseShaderRegister = 1,
                                    RegisterSpace = 0,
                                    OffsetInDescriptorsFromTableStart = 0
                                }
                            }
                        }
                    }
                },
                StaticSamplers = new[]
                {
                    new RHISamplerDesc
                    {
                        Filter = RHIFilter.Linear,
                        AddressU = RHITextureAddressMode.Wrap,
                        AddressV = RHITextureAddressMode.Wrap,
                        AddressW = RHITextureAddressMode.Wrap,
                        ShaderRegister = 0,
                        RegisterSpace = 0,
                        ShaderVisibility = RHIShaderVisibility.Pixel
                    },
                    new RHISamplerDesc
                    {
                        Filter = RHIFilter.ComparisonMinMagMipLinear,
                        AddressU = RHITextureAddressMode.Clamp,
                        AddressV = RHITextureAddressMode.Clamp,
                        AddressW = RHITextureAddressMode.Clamp,
                        ComparisonFunc = RHIComparison.LessEqual,
                        ShaderRegister = 1,
                        RegisterSpace = 0,
                        ShaderVisibility = RHIShaderVisibility.Pixel
                    }
                },
                AllowInputAssembler = true,
                Flags = RHIRootSignatureFlags.AllowInputAssemblerInputLayout
            };
            _skinnedRootSignature = _device.CreateRootSignature(rootSigDesc, "SkinnedMeshRootSignature");

            // 创建骨骼蒙皮 PSO
            var psoDesc = new RHIGraphicsPipelineDesc
            {
                Name = "SkinnedMeshPSO",
                VS = _skinnedMeshVS,
                PS = _meshPS,
                InputLayout = new[]
                {
                    new RHIInputElement("POSITION",     RHIFormat.R32G32B32_Float,    0, 0),
                    new RHIInputElement("NORMAL",       RHIFormat.R32G32B32_Float,    0, 12),
                    new RHIInputElement("TEXCOORD",     RHIFormat.R32G32_Float,       0, 24),
                    new RHIInputElement("BLENDWEIGHTS", RHIFormat.R32G32B32A32_Float, 0, 32),
                    new RHIInputElement("BLENDINDICES", RHIFormat.R8G8B8A8_UInt,      0, 48)
                },
                PrimitiveTopology = RHIPrimitiveTopology.TriangleList,
                BlendState = RHIBlendDesc.Default,
                DepthStencilState = new RHIDepthDesc
                {
                    DepthEnable = true,
                    DepthWriteEnable = true,
                    DepthFunc = RHIComparison.LessEqual,
                    StencilEnable = false
                },
                RasterizerState = RHIRasterizerDesc.Default,
                RTVFormats = new[] { RHIFormat.R8G8B8A8_UNorm },
                DSVFormat = RHIFormat.D32_Float,
                RootSignature = _skinnedRootSignature
            };
            _skinnedMeshPSO = _device.CreateGraphicsPipelineState(psoDesc, "SkinnedMeshPSO");

            // 创建骨骼矩阵常量缓冲区 (256-byte 对齐, 实际占用 16384 字节 = 64 * 256)
            var boneCBDesc = new RHIBufferDesc
            {
                SizeInBytes = BoneMatrixBufferSize,
                HeapType = RHIResourceHeapType.Default,
                Flags = RHIBufferFlags.ConstantBuffer,
                Stride = ConstantBufferAlignment
            };
            _boneMatrixBuffer = _device.CreateBuffer(boneCBDesc, "BoneMatricesCB");
            _boneMatrixBuffer.CurrentState = RHIResourceState.Common;
        }

        /// <summary>
        /// 上传骨骼矩阵数组到 GPU 常量缓冲区。
        /// </summary>
        private void UploadBoneMatrices(Matrix4x4[] boneMatrices)
        {
            if (_boneMatrixBuffer == null) return;
            int count = Math.Min(boneMatrices.Length, MaxBones);
            for (int i = 0; i < count; i++)
                _boneMatrixUploadArray[i] = boneMatrices[i];
            // 剩余槽位保持零矩阵
            for (int i = count; i < MaxBones; i++)
                _boneMatrixUploadArray[i] = Matrix4x4.Identity;
            _device.UploadToBuffer(_boneMatrixBuffer, _boneMatrixUploadArray, 0);
        }

        // ════════════════════════════════════════════════════════════
        //  网格上传
        // ════════════════════════════════════════════════════════════

        /// <summary>
        /// 将 EditableMesh 数据上传到 GPU 并缓存。
        /// 如果 meshPath 已缓存则跳过重复上传。
        /// </summary>
        /// <param name="meshPath">网格资源路径 (作为缓存键)。</param>
        /// <param name="mesh">可编辑网格数据。</param>
        public void UploadMesh(string meshPath, EditableMesh mesh)
        {
            if (string.IsNullOrEmpty(meshPath) || mesh == null) return;
            if (_meshCache.ContainsKey(meshPath)) return;

            var gpu = UploadMeshInternal(mesh);
            _meshCache[meshPath] = gpu;
        }

        /// <summary>
        /// 将骨骼蒙皮 EditableMesh 上传到 GPU 并缓存。
        /// 使用带骨骼权重/索引的顶点布局。
        /// </summary>
        public void UploadSkinnedMesh(string meshPath, EditableMesh mesh)
        {
            if (string.IsNullOrEmpty(meshPath) || mesh == null) return;
            if (_skinnedMeshCache.ContainsKey(meshPath)) return;

            var gpu = UploadSkinnedMeshInternal(mesh);
            _skinnedMeshCache[meshPath] = gpu;
        }

        /// <summary>
        /// 内部方法: 将 EditableMesh 上传到 GPU, 返回 MeshGPUResources。
        /// 构建顶点/索引数据 → 创建 Default 堆缓冲区 → UploadToBuffer 拷贝。
        /// </summary>
        private MeshGPUResources UploadMeshInternal(EditableMesh mesh)
        {
            // ── 构建顶点数据 (Position12 + Normal12 + UV8 = 32B/vertex) ──
            byte[] vertexData = BuiltinShaders.BuildMeshVertexBuffer(
                mesh.Vertices, mesh.Normals, mesh.UV0);

            // ── 构建索引数据 (uint32, 每面3个索引) ──
            var faces = new List<(int A, int B, int C)>(mesh.Faces.Count);
            foreach (var f in mesh.Faces)
                faces.Add((f.A, f.B, f.C));
            byte[] indexData = BuiltinShaders.BuildMeshIndexBuffer(faces);

            // ── 创建顶点缓冲区 (Default 堆) ──
            var vbDesc = new RHIBufferDesc
            {
                SizeInBytes = (ulong)vertexData.Length,
                HeapType = RHIResourceHeapType.Default,
                Flags = RHIBufferFlags.VertexBuffer,
                Stride = (ulong)BuiltinShaders.MeshVertexStride
            };
            RHIBuffer vb = _device.CreateBuffer(vbDesc, $"VB_{mesh.Name}");
            vb.CurrentState = RHIResourceState.Common;
            _device.UploadToBuffer(vb, vertexData, 0);

            // ── 创建索引缓冲区 (Default 堆) ──
            var ibDesc = new RHIBufferDesc
            {
                SizeInBytes = (ulong)indexData.Length,
                HeapType = RHIResourceHeapType.Default,
                Flags = RHIBufferFlags.IndexBuffer,
                Stride = (uint)sizeof(uint)
            };
            RHIBuffer ib = _device.CreateBuffer(ibDesc, $"IB_{mesh.Name}");
            ib.CurrentState = RHIResourceState.Common;
            _device.UploadToBuffer(ib, indexData, 0);

            return new MeshGPUResources
            {
                VertexBuffer = vb,
                IndexBuffer = ib,
                VertexCount = mesh.Vertices.Count,
                IndexCount = mesh.Faces.Count * 3,
                VertexBufferSizeInBytes = (ulong)vertexData.Length,
                IndexBufferSizeInBytes = (ulong)indexData.Length
            };
        }

        /// <summary>
        /// 内部方法: 将骨骼蒙皮 EditableMesh 上传到 GPU。
        /// 构建带骨骼权重/索引的顶点缓冲区。
        /// </summary>
        private MeshGPUResources UploadSkinnedMeshInternal(EditableMesh mesh)
        {
            byte[] vertexData = SkinnedMeshShaders.BuildSkinnedMeshVertexBuffer(
                mesh.Vertices, mesh.Normals, mesh.UV0, mesh.BoneWeights ?? new List<MeshBoneWeight>());

            var faces = new List<(int A, int B, int C)>(mesh.Faces.Count);
            foreach (var f in mesh.Faces)
                faces.Add((f.A, f.B, f.C));
            byte[] indexData = BuiltinShaders.BuildMeshIndexBuffer(faces);

            var vbDesc = new RHIBufferDesc
            {
                SizeInBytes = (ulong)vertexData.Length,
                HeapType = RHIResourceHeapType.Default,
                Flags = RHIBufferFlags.VertexBuffer,
                Stride = (ulong)SkinnedMeshShaders.SkinnedVertexStride
            };
            RHIBuffer vb = _device.CreateBuffer(vbDesc, $"VB_Skinned_{mesh.Name}");
            vb.CurrentState = RHIResourceState.Common;
            _device.UploadToBuffer(vb, vertexData, 0);

            var ibDesc = new RHIBufferDesc
            {
                SizeInBytes = (ulong)indexData.Length,
                HeapType = RHIResourceHeapType.Default,
                Flags = RHIBufferFlags.IndexBuffer,
                Stride = (uint)sizeof(uint)
            };
            RHIBuffer ib = _device.CreateBuffer(ibDesc, $"IB_Skinned_{mesh.Name}");
            ib.CurrentState = RHIResourceState.Common;
            _device.UploadToBuffer(ib, indexData, 0);

            return new MeshGPUResources
            {
                VertexBuffer = vb,
                IndexBuffer = ib,
                VertexCount = mesh.Vertices.Count,
                IndexCount = mesh.Faces.Count * 3,
                VertexBufferSizeInBytes = (ulong)vertexData.Length,
                IndexBufferSizeInBytes = (ulong)indexData.Length
            };
        }

        // ════════════════════════════════════════════════════════════
        //  场景渲染
        // ════════════════════════════════════════════════════════════

        /// <summary>
        /// 主渲染方法: 接收命令列表和场景数据，录制真实 GPU 绘制命令。
        /// 流程:
        ///   1. 确保管线已初始化 (懒初始化)
        ///   2. 更新 FrameConstants (相机矩阵 + 光照)
        ///   3. 渲染 ShadowMap (从光源视角渲染深度)
        ///   4. 设置 PSO / 根签名 / 拓扑
        ///   5. 遍历场景网格, 逐个绑定 VB/IB 并 DrawIndexedInstanced
        ///   6. 无可见网格时渲染默认立方体
        ///   7. 如有后处理，执行后处理输出到屏幕
        /// </summary>
        /// <param name="cmd">已打开的命令列表。</param>
        /// <param name="scene">渲染场景数据 (可为 null)。</param>
        /// <param name="camera">相机数据。</param>
        /// <param name="dt">帧时间 (秒)。</param>
        public void RenderScene(RHICommandList cmd, IRenderScene scene, in CameraData camera, float dt)
        {
            RenderScene(cmd, scene, camera, dt, default);
        }

        /// <summary>
        /// 主渲染方法 (带后处理输出): 渲染场景并可选择通过后处理系统输出到指定 RTV。
        /// 如果 outputRTV 有效且后处理系统已设置，则场景渲染到中间纹理后经 Bloom+色调映射输出到 outputRTV。
        /// </summary>
        /// <param name="cmd">已打开的命令列表。</param>
        /// <param name="scene">渲染场景数据 (可为 null)。</param>
        /// <param name="camera">相机数据。</param>
        /// <param name="dt">帧时间 (秒)。</param>
        /// <param name="outputRTV">最终输出渲染目标 (后处理启用时有效)。</param>
        public void RenderScene(RHICommandList cmd, IRenderScene scene, in CameraData camera, float dt, RHIDescriptorHandle outputRTV)
        {
            if (cmd == null) return;

            // 1. 确保网格管线已初始化
            EnsureMeshPipelineInitialized();

            // 2. 更新 FrameConstants 上传缓冲区
            UpdateFrameConstants(camera, scene);

            // 3. 渲染 ShadowMap
            RenderShadowMap(cmd, scene);

            // 4. 设置主渲染 PSO / 根签名 / 常量缓冲区 / 拓扑
            cmd.SetPipelineState(_meshPSO);
            cmd.SetGraphicsRootSignature(_rootSignature);
            cmd.SetGraphicsRootConstantBufferView(0, _frameCB);
            cmd.IASetPrimitiveTopology(RHIPrimitiveTopology.TriangleList);

            // 4b. 确保默认纹理已创建
            EnsureDefaultTexture();

            // 4c. 绑定 ShadowMap SRV (t1) 和 ShadowConstants (b3)
            BindShadowResources(cmd);

            // 5. 遍历场景网格
            bool anyMeshDrawn = false;
            _lastBoundTextureSRV = default;

            if (scene?.Meshes != null)
            {
                foreach (var renderMesh in scene.Meshes)
                {
                    if (renderMesh == null || !renderMesh.IsVisible) continue;

                    // 查找已缓存的 GPU 资源
                    MeshGPUResources gpuMesh = null;
                    if (!string.IsNullOrEmpty(renderMesh.MeshAssetPath) &&
                        _meshCache.TryGetValue(renderMesh.MeshAssetPath, out var cached))
                    {
                        gpuMesh = cached;
                    }

                    // 未缓存则使用默认立方体
                    if (gpuMesh == null)
                    {
                        EnsureDefaultCube();
                        gpuMesh = _defaultCube;
                    }

                    if (gpuMesh == null) continue;

                    // ── 纹理绑定：按材质的 TextureAssetPath 加载真实纹理 ──
                    string texPath = renderMesh.Material?.TextureAssetPath;
                    var textureSRV = GetOrLoadTextureSRV(texPath);
                    bool useRealTexture = textureSRV.IsValid &&
                                          (!_lastBoundTextureSRV.IsValid ||
                                           !textureSRV.Equals(_lastBoundTextureSRV));

                    if (useRealTexture)
                    {
                        // 绑定 Albedo 纹理 (t0)
                        cmd.SetGraphicsRootDescriptorTable(2, textureSRV);
                        _lastBoundTextureSRV = textureSRV;
                    }
                    else if (!_lastBoundTextureSRV.IsValid && _defaultTextureSRV.IsValid)
                    {
                        // 首次绑定默认纹理 (t0)
                        cmd.SetGraphicsRootDescriptorTable(2, _defaultTextureSRV);
                        _lastBoundTextureSRV = _defaultTextureSRV;
                    }

                    // 更新 ObjectConstants (世界矩阵 + 材质 + UseTexture 标志)
                    UpdateObjectConstants(renderMesh, textureSRV.IsValid && textureSRV.Equals(_lastBoundTextureSRV) && !textureSRV.Equals(_defaultTextureSRV) ? 1.0f : 0.0f);
                    cmd.SetGraphicsRootConstantBufferView(1, _objectCB);

                    // 绑定顶点缓冲区
                    cmd.IASetVertexBuffers(
                        0,
                        gpuMesh.VertexBuffer,
                        0,
                        (uint)BuiltinShaders.MeshVertexStride,
                        (uint)gpuMesh.VertexBufferSizeInBytes);

                    // 绑定索引缓冲区
                    cmd.IASetIndexBuffer(
                        gpuMesh.IndexBuffer,
                        0,
                        (uint)gpuMesh.IndexBufferSizeInBytes,
                        RHIFormat.R32_UInt);

                    // 绘制
                    cmd.DrawIndexedInstanced(
                        (uint)gpuMesh.IndexCount, 1, 0, 0, 0);

                    anyMeshDrawn = true;
                }
            }

            // 6. 渲染骨骼蒙皮网格
            if (scene is RenderSceneSnapshot snapshot && snapshot.SkinnedMeshes != null)
            {
                foreach (var skinnedMesh in snapshot.SkinnedMeshes)
                {
                    if (skinnedMesh == null || !skinnedMesh.IsVisible) continue;
                    if (skinnedMesh.Mesh == null) continue;

                    RenderSkinnedMesh(cmd, skinnedMesh.Mesh, skinnedMesh.WorldMatrix,
                        skinnedMesh.BoneMatrices, camera, dt);
                    anyMeshDrawn = true;
                }
            }

            // 7. 空场景时渲染默认立方体
            if (!anyMeshDrawn)
            {
                EnsureDefaultCube();
                if (_defaultCube != null)
                {
                    // 确保默认纹理已绑定
                    if (_defaultTextureSRV.IsValid)
                    {
                        cmd.SetGraphicsRootDescriptorTable(2, _defaultTextureSRV);
                        _lastBoundTextureSRV = _defaultTextureSRV;
                    }

                    UpdateObjectConstants(null, 0.0f);
                    cmd.SetGraphicsRootConstantBufferView(1, _objectCB);

                    cmd.IASetVertexBuffers(
                        0,
                        _defaultCube.VertexBuffer,
                        0,
                        (uint)BuiltinShaders.MeshVertexStride,
                        (uint)_defaultCube.VertexBufferSizeInBytes);

                    cmd.IASetIndexBuffer(
                        _defaultCube.IndexBuffer,
                        0,
                        (uint)_defaultCube.IndexBufferSizeInBytes,
                        RHIFormat.R32_UInt);

                    cmd.DrawIndexedInstanced(
                        (uint)_defaultCube.IndexCount, 1, 0, 0, 0);
                }
            }

            // 8. 粒子渲染
            RenderParticles(cmd, scene, camera);

            // 9. 后处理：如有后处理系统且指定了输出目标，执行 Bloom + 色调映射
            if (_postProcess != null && outputRTV.IsValid)
            {
                _postProcess.RenderPostProcess(cmd, outputRTV, _postProcess.Width, _postProcess.Height);
            }
        }

        /// <summary>
        /// 渲染 FlowVFX GPU 粒子。
        /// </summary>
        private void RenderParticles(RHICommandList cmd, IRenderScene scene, in CameraData camera)
        {
            if (_particleRenderer == null || _particleBridge == null || ActiveParticleSimulation == null)
                return;

            _particleBridge.UpdateAndRender(cmd, ActiveParticleSimulation, camera);
        }

        /// <summary>
        /// 渲染 ShadowMap：从光源视角渲染所有可见物体的深度。
        /// </summary>
        private void RenderShadowMap(RHICommandList cmd, IRenderScene scene)
        {
            if (cmd == null || _shadowMapSystem == null) return;

            // 开始阴影通道
            var lightVP = _shadowMapSystem.BeginShadowPass(cmd, _currentLightDirection, scene);

            // 遍历场景网格绘制深度
            if (scene?.Meshes != null)
            {
                foreach (var renderMesh in scene.Meshes)
                {
                    if (renderMesh == null || !renderMesh.IsVisible || !renderMesh.CastShadow) continue;

                    // 查找已缓存的 GPU 资源
                    MeshGPUResources gpuMesh = null;
                    if (!string.IsNullOrEmpty(renderMesh.MeshAssetPath) &&
                        _meshCache.TryGetValue(renderMesh.MeshAssetPath, out var cached))
                    {
                        gpuMesh = cached;
                    }

                    if (gpuMesh == null)
                    {
                        EnsureDefaultCube();
                        gpuMesh = _defaultCube;
                    }

                    if (gpuMesh == null) continue;

                    // 上传 WVP 矩阵到阴影常量缓冲区
                    _shadowMapSystem.SetShadowObjectConstants(cmd, renderMesh.WorldMatrix);

                    // 绑定顶点/索引缓冲区并绘制
                    cmd.IASetVertexBuffers(
                        0,
                        gpuMesh.VertexBuffer,
                        0,
                        (uint)BuiltinShaders.MeshVertexStride,
                        (uint)gpuMesh.VertexBufferSizeInBytes);

                    cmd.IASetIndexBuffer(
                        gpuMesh.IndexBuffer,
                        0,
                        (uint)gpuMesh.IndexBufferSizeInBytes,
                        RHIFormat.R32_UInt);

                    cmd.DrawIndexedInstanced(
                        (uint)gpuMesh.IndexCount, 1, 0, 0, 0);
                }
            }

            // 结束阴影通道，转换 ShadowMap 到 ShaderResource
            _shadowMapSystem.EndShadowPass(cmd);
        }

        /// <summary>
        /// 绑定 ShadowMap SRV 和 ShadowConstants 到主渲染通道。
        /// </summary>
        private void BindShadowResources(RHICommandList cmd)
        {
            if (cmd == null || _shadowMapSystem == null || _shadowCB == null) return;

            // 更新 ShadowConstants
            ref var sc = ref _shadowDataArray[0];
            sc.LightViewProj = _shadowMapSystem.GetLightViewProjectionMatrix();
            sc.ShadowMapSize = ShadowMapSystem.ShadowMapSize;
            sc.ShadowBias = 0.005f;
            sc._pad = Vector2.Zero;
            _device.UploadToBuffer(_shadowCB, _shadowDataArray, 0);

            // 绑定 ShadowConstants 到 b3 (根参数 3)
            cmd.SetGraphicsRootConstantBufferView(3, _shadowCB);

            // 绑定 ShadowMap SRV (t1) 到根参数 4
            var shadowSRV = _shadowMapSystem.GetShadowMapSRV();
            if (shadowSRV.IsValid)
            {
                cmd.SetGraphicsRootDescriptorTable(4, shadowSRV);
            }
        }

        /// <summary>
        /// 渲染单个骨骼蒙皮网格。
        /// 上传骨骼矩阵、设置蒙皮 PSO、绑定顶点/索引缓冲区并绘制。
        /// </summary>
        public void RenderSkinnedMesh(RHICommandList cmd, EditableMesh mesh,
            Matrix4x4 worldMatrix, Matrix4x4[] boneMatrices,
            in CameraData camera, float dt)
        {
            if (cmd == null || mesh == null || boneMatrices == null || boneMatrices.Length == 0) return;

            // 1. 确保骨骼蒙皮管线已初始化
            EnsureSkinnedPipelineInitialized();

            // 2. 上传骨骼矩阵到 GPU
            UploadBoneMatrices(boneMatrices);

            // 3. 设置 PSO、根签名
            cmd.SetPipelineState(_skinnedMeshPSO);
            cmd.SetGraphicsRootSignature(_skinnedRootSignature);

            // 4. 绑定常量缓冲区
            cmd.SetGraphicsRootConstantBufferView(0, _frameCB);
            cmd.SetGraphicsRootConstantBufferView(2, _boneMatrixBuffer);

            // 5. 绑定阴影资源 (b3 ShadowConstants + t1 ShadowMap)
            if (_shadowCB != null)
                cmd.SetGraphicsRootConstantBufferView(4, _shadowCB);
            var shadowSRV = _shadowMapSystem?.GetShadowMapSRV() ?? default;
            if (shadowSRV.IsValid)
                cmd.SetGraphicsRootDescriptorTable(5, shadowSRV);

            // 6. 确保默认纹理并绑定
            EnsureDefaultTexture();
            cmd.SetGraphicsRootDescriptorTable(3, _defaultTextureSRV);

            // 6. 确保皮肤网格 GPU 资源已上传
            string cacheKey = $"skinned_{mesh.Guid}";
            if (!_skinnedMeshCache.TryGetValue(cacheKey, out var gpuMesh))
            {
                gpuMesh = UploadSkinnedMeshInternal(mesh);
                _skinnedMeshCache[cacheKey] = gpuMesh;
            }

            // 7. 更新 ObjectConstants
            ref var oc = ref _objectDataArray[0];
            oc.WorldMatrix = worldMatrix;
            if (Matrix4x4.Invert(worldMatrix, out var worldInv))
                oc.WorldInverseTranspose = Matrix4x4.Transpose(worldInv);
            else
                oc.WorldInverseTranspose = Matrix4x4.Identity;
            oc.BaseColor = new Vector4(Vector3.One, 1.0f);
            oc.Metallic = 0.0f;
            oc.Roughness = 0.5f;
            oc.UseTexture = 0.0f;
            _device.UploadToBuffer(_objectCB, _objectDataArray, 0);
            cmd.SetGraphicsRootConstantBufferView(1, _objectCB);

            // 8. 绑定顶点/索引缓冲区并绘制
            cmd.IASetPrimitiveTopology(RHIPrimitiveTopology.TriangleList);
            cmd.IASetVertexBuffers(
                0,
                gpuMesh.VertexBuffer,
                0,
                (uint)SkinnedMeshShaders.SkinnedVertexStride,
                (uint)gpuMesh.VertexBufferSizeInBytes);
            cmd.IASetIndexBuffer(
                gpuMesh.IndexBuffer,
                0,
                (uint)gpuMesh.IndexBufferSizeInBytes,
                RHIFormat.R32_UInt);

            cmd.DrawIndexedInstanced((uint)gpuMesh.IndexCount, 1, 0, 0, 0);
        }

        // ════════════════════════════════════════════════════════════
        //  线段渲染: 网格 / 任意线段
        // ════════════════════════════════════════════════════════════

        /// <summary>
        /// 渲染地面网格线段 (XZ 平面)。
        /// 生成 gridSize × gridDivisions 的线段顶点, 上传并绘制。
        /// </summary>
        /// <param name="cmd">已打开的命令列表。</param>
        /// <param name="camera">相机数据。</param>
        /// <param name="gridSize">每个网格单元的大小。</param>
        /// <param name="gridDivisions">网格半边单元数 (总范围 = gridSize × gridDivisions × 2)。</param>
        public void RenderGrid(RHICommandList cmd, in CameraData camera, float gridSize, int gridDivisions)
        {
            if (cmd == null || gridDivisions <= 0 || gridSize <= 0f) return;

            EnsureLinePipelineInitialized();

            // ── 生成网格线段顶点 ──
            float halfExtent = gridSize * gridDivisions;
            var lineVertices = new List<(Vector3 Pos, Vector4 Color)>();

            Vector4 minorColor = new Vector4(0.30f, 0.30f, 0.32f, 0.60f);
            Vector4 majorColor = new Vector4(0.45f, 0.45f, 0.50f, 0.80f);
            Vector4 axisColorX = new Vector4(0.60f, 0.25f, 0.25f, 0.90f);
            Vector4 axisColorZ = new Vector4(0.25f, 0.40f, 0.60f, 0.90f);

            for (int i = -gridDivisions; i <= gridDivisions; i++)
            {
                float offset = i * gridSize;
                bool isCenter = i == 0;
                bool isMajor = i % 10 == 0;

                Vector4 colorX = isCenter ? axisColorX : (isMajor ? majorColor : minorColor);
                Vector4 colorZ = isCenter ? axisColorZ : (isMajor ? majorColor : minorColor);

                // 沿 X 轴的线 (固定 Z)
                lineVertices.Add((new Vector3(-halfExtent, 0, offset), colorX));
                lineVertices.Add((new Vector3( halfExtent, 0, offset), colorX));

                // 沿 Z 轴的线 (固定 X)
                lineVertices.Add((new Vector3(offset, 0, -halfExtent), colorZ));
                lineVertices.Add((new Vector3(offset, 0,  halfExtent), colorZ));
            }

            // ── 构建顶点缓冲区数据 ──
            byte[] lineData = BuiltinShaders.BuildLineVertexBuffer(lineVertices);

            // ── 确保线段 VB 足够大并上传 ──
            EnsureLineVB((uint)lineData.Length);
            _device.UploadToBuffer(_lineVB, lineData, 0);

            // ── 更新线段常量缓冲区 ──
            _lineDataArray[0] = new BuiltinShaders.LineConstants
            {
                ViewProjectionMatrix = camera.ViewProjectionMatrix,
                Color = Vector4.One
            };
            _device.UploadToBuffer(_lineCB, _lineDataArray, 0);

            // ── 录制绘制命令 ──
            cmd.SetPipelineState(_linePSO);
            cmd.SetGraphicsRootSignature(_lineRootSignature);
            cmd.SetGraphicsRootConstantBufferView(0, _lineCB);
            cmd.IASetPrimitiveTopology(RHIPrimitiveTopology.LineList);
            cmd.IASetVertexBuffers(
                0,
                _lineVB,
                0,
                (uint)BuiltinShaders.LineVertexStride,
                (uint)lineData.Length);
            cmd.DrawInstanced((uint)lineVertices.Count, 1, 0, 0);
        }

        /// <summary>
        /// 渲染任意线段。将位置和颜色数组动态上传到顶点缓冲区并绘制。
        /// 每两个顶点构成一条线段 (LineList 拓扑)。
        /// </summary>
        /// <param name="cmd">已打开的命令列表。</param>
        /// <param name="positions">线段顶点位置数组。</param>
        /// <param name="colors">线段顶点颜色数组 (与 positions 等长)。</param>
        /// <param name="camera">相机数据。</param>
        public void RenderLines(RHICommandList cmd, Vector3[] positions, Vector4[] colors, in CameraData camera)
        {
            if (cmd == null || positions == null || positions.Length < 2) return;
            if (colors == null || colors.Length < 2) return;

            EnsureLinePipelineInitialized();

            int count = Math.Min(positions.Length, colors.Length);
            if (count < 2) return;

            // ── 构建线段顶点数据 ──
            var lineVertices = new List<(Vector3 Pos, Vector4 Color)>(count);
            for (int i = 0; i < count; i++)
                lineVertices.Add((positions[i], colors[i]));

            byte[] lineData = BuiltinShaders.BuildLineVertexBuffer(lineVertices);

            // ── 确保线段 VB 足够大并上传 ──
            EnsureLineVB((uint)lineData.Length);
            _device.UploadToBuffer(_lineVB, lineData, 0);

            // ── 更新线段常量缓冲区 ──
            _lineDataArray[0] = new BuiltinShaders.LineConstants
            {
                ViewProjectionMatrix = camera.ViewProjectionMatrix,
                Color = Vector4.One
            };
            _device.UploadToBuffer(_lineCB, _lineDataArray, 0);

            // ── 录制绘制命令 ──
            cmd.SetPipelineState(_linePSO);
            cmd.SetGraphicsRootSignature(_lineRootSignature);
            cmd.SetGraphicsRootConstantBufferView(0, _lineCB);
            cmd.IASetPrimitiveTopology(RHIPrimitiveTopology.LineList);
            cmd.IASetVertexBuffers(
                0,
                _lineVB,
                0,
                (uint)BuiltinShaders.LineVertexStride,
                (uint)lineData.Length);
            cmd.DrawInstanced((uint)count, 1, 0, 0);
        }

        // ════════════════════════════════════════════════════════════
        //  常量缓冲区更新
        // ════════════════════════════════════════════════════════════

        /// <summary>
        /// 更新 FrameConstants (b0): 视图/投影矩阵、相机位置、光照参数。
        /// 如果场景中存在光源, 使用第一个光源的数据。
        /// </summary>
        private void UpdateFrameConstants(in CameraData camera, IRenderScene scene)
        {
            ref var fc = ref _frameDataArray[0];
            fc.ViewMatrix = camera.ViewMatrix;
            fc.ProjectionMatrix = camera.ProjectionMatrix;
            fc.ViewProjectionMatrix = camera.ViewProjectionMatrix;
            fc.CameraPosition = camera.Position;

            // 光照: 优先使用 camera 传入的光源方向，其次使用场景光源，最后使用默认方向光
            if (camera.LightDirection.LengthSquared() > 1e-6f)
            {
                fc.LightDirection = camera.LightDirection;
                _currentLightDirection = camera.LightDirection;
                fc.LightColor = new Vector4(Vector3.One, 1.0f);
            }
            else if (scene?.Lights != null && scene.Lights.Count > 0)
            {
                var light = scene.Lights[0];
                // 从光源世界矩阵提取前向方向 (System.Numerics 行主序: M31/M32/M33 = 第三行)
                Vector3 lightForward = new Vector3(
                    light.WorldMatrix.M31,
                    light.WorldMatrix.M32,
                    light.WorldMatrix.M33);

                if (lightForward.LengthSquared() < 1e-6f)
                    lightForward = new Vector3(-0.5f, -0.8f, -0.3f);

                fc.LightDirection = lightForward;
                _currentLightDirection = lightForward;
                fc.LightColor = new Vector4(light.Color, light.Intensity);
            }
            else
            {
                // 默认方向光: 从左上方照射
                fc.LightDirection = new Vector3(-0.5f, -0.8f, -0.3f);
                _currentLightDirection = fc.LightDirection;
                fc.LightColor = new Vector4(Vector3.One, 1.0f);
            }

            // 环境光
            fc.AmbientColor = new Vector4(0.20f, 0.22f, 0.28f, 1.0f);

            // 上传到 GPU
            _device.UploadToBuffer(_frameCB, _frameDataArray, 0);
        }

        /// <summary>
        /// 更新 ObjectConstants (b1): 世界矩阵、世界逆转置矩阵、材质参数。
        /// </summary>
        /// <param name="mesh">渲染网格 (null 时使用单位矩阵和默认材质)。</param>
        /// <param name="useTexture">是否使用真实纹理 (>0 表示使用纹理采样)。</param>
        private void UpdateObjectConstants(RenderMesh mesh, float useTexture = 0.0f)
        {
            ref var oc = ref _objectDataArray[0];

            // 世界矩阵
            oc.WorldMatrix = mesh?.WorldMatrix ?? Matrix4x4.Identity;

            // 世界逆转置矩阵 (用于法线变换)
            if (Matrix4x4.Invert(oc.WorldMatrix, out var worldInv))
                oc.WorldInverseTranspose = Matrix4x4.Transpose(worldInv);
            else
                oc.WorldInverseTranspose = Matrix4x4.Identity;

            // 材质参数
            Vector3 baseColor = mesh?.Material?.BaseColor ?? Vector3.One;
            oc.BaseColor = new Vector4(baseColor, 1.0f);
            oc.Metallic = mesh?.Material?.Metallic ?? 0.0f;
            oc.Roughness = mesh?.Material?.Roughness ?? 0.5f;
            oc.UseTexture = useTexture;

            // 上传到 GPU
            _device.UploadToBuffer(_objectCB, _objectDataArray, 0);
        }
    }
}
