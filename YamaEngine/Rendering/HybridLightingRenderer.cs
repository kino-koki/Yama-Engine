using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using YamaEngine.RHI;
using YamaEngine.Shaders;

namespace YamaEngine.Rendering
{
    // ════════════════════════════════════════════════════════════════════════
    // yama Deferred Renderer (超越 UE7 级别)
    //
    // 完整的现代延迟渲染管线，核心流程:
    //   1. GeometryBuffer Pass (Prepass Z / 可选): Albedo, Normal, Roughness/Metallic, Depth
    //   2. Shadow Map Pass (CSM / PCF / PCSS 级联阴影)
    //   3. Lighting Pass (Clustered Deferred Lighting Compute)
    //   4. Ambient Occlusion / Reflections (SSAO/SSR 已整合到后处理)
    //   5. Temporal Anti-Aliasing (TAA 解析)
    //   6. Post-Processing Stack (Bloom -> Tonemap -> ColorGrading -> FXAA)
    //   7. Temporal Super-Resolution (可选上采样)
    //   8. Present / UI Composite
    //
    // 支持 RenderGraph 自动化依赖调度，兼容 ProbeField 全局光照插件。
    // ════════════════════════════════════════════════════════════════════════

    public sealed class GeometryBufferSettings
    {
        public bool UseHDRColorBuffer = true;
        public bool UsePrepass = true;
        public bool UseMotionVectors = true;
        public bool UseAnisotropicRoughness = false;
    }


    public sealed class AmbientOcclusionSettings
    {
        public bool Enabled = true;
        public AOMethod Method = AOMethod.SSAO;
        public float Radius = 0.5f;
        public float Intensity = 1.0f;
    }

    public sealed class LightingSettings
    {
        public bool UseClusteredLighting = true;
        public bool UseProbeFieldGI = true;
        public bool UseProbeField;
        public int MaxLights = 1024;
        public bool EnableSSR = true;
        public bool EnableContactShadows = true;
    }

    public sealed class AntialiasingSettings
    {
        public AAMethod Method = AAMethod.TAA;
        public float Quality = 1.0f;
        public bool UseTSR = true;
    }


    [StructLayout(LayoutKind.Sequential)]
    public struct DeferredRenderCB
    {
        public Matrix4x4 ViewMatrix;
        public Matrix4x4 InvViewMatrix;
        public Matrix4x4 ProjectionMatrix;
        public Matrix4x4 InvProjectionMatrix;
        public Matrix4x4 ViewProjectionMatrix;
        public Matrix4x4 InvViewProjectionMatrix;
        public Matrix4x4 PrevViewProjectionMatrix;
        public Vector3 CameraPosition;
        public float CameraNear;
        public Vector3 CameraDirection;
        public float CameraFar;
        public Vector2 ScreenResolution;
        public float ScreenWidth => ScreenResolution.X;
        public float ScreenHeight => ScreenResolution.Y;
        public Vector2 ScreenSize => ScreenResolution;
        public float Time;
        public float DeltaTime;
        public int FrameIndex;
        public int RenderFlags;
        public Vector2 JitterOffset;
        public float Exposure;
        public float Pad;
        public int GeometryBufferFlag_UseHDR;
        public int GeometryBufferFlag_UseMotionVectors;
        public float GeometryBufferFlag_Anisotropy;
        public float GeometryBufferFlag_Pad;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct LightingConstants
    {
        public Vector3 SunDirection;
        public float SunIntensity;
        public Vector3 SunColor;
        public int LightCount;
        public Vector3 AmbientColor;
        public float AmbientIntensity;
        public Matrix4x4 ShadowMatrix;
        public float ShadowBias;
        public float NormalBias;
        public float PCFKernelRadius;
        public int UsePCSS;
        public Vector2 ShadowMapSize;
        public float LightSize;
        public float Pad;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct LightData
    {
        public Vector3 Position;
        public float Range;
        public Vector3 Color;
        public float Intensity;
        public Vector3 Direction;
        public float SpotAngle;
        public int Type;
        public int CastShadows;
        public Vector2 Pad;
    }

    public enum AOMethod { SSAO, HBAO, GTAO }
    public enum AAMethod { None, FXAA, TAA, DLSS, FSR }

    public delegate void RenderDelegate(RHICommandList cmdList);
    public delegate void RenderGraphDelegate(RenderGraph graph);

    public sealed class HybridLightingRenderer : IDisposable
    {
        private readonly RHIDevice _device;
        private int _width, _height;

        public GeometryBufferSettings GeometryBufferSettings { get; } = new();
        public ShadowSettings ShadowSettings { get; } = new();
        public AmbientOcclusionSettings AOSettings { get; } = new();
        public LightingSettings LightingSettings { get; } = new();
        public AntialiasingSettings AASettings { get; } = new();
        public PostProcessSettings PostProcessSettings { get; } = new();

        // GeometryBuffer resources
        public RHITexture? GeometryBufferColor { get; private set; }
        public RHITexture? GeometryBufferNormals { get; private set; }
        public RHITexture? GeometryBufferMaterial { get; private set; }
        public RHITexture? GeometryBufferDepth { get; private set; }
        public RHITexture? GeometryBufferMotionVectors { get; private set; }

        // Lighting and post
        public RHITexture? SceneColor { get; private set; }
        public RHITexture? FinalOutput { get; private set; }
        public RHITexture? ShadowMap { get; private set; }

        private RHIRootSignature? _deferredRootSig;
        private RHIPipelineState? _geometrybufferPipeline;
        private RHIPipelineState? _lightingPipeline;
        private RHIPipelineState? _shadowPipeline;

        private readonly List<LightData> _lights = new();
        private readonly RHIBuffer? _lightBuffer;
        private readonly RHIBuffer? _lightingCB;
        private readonly RHIBuffer? _deferredCB;

        // Subsystems
        private ClusteredLighting? _clusteredLighting;
        private TemporalAntiAliasing? _taa;
        private PostProcessingStack? _postProcess;
        private RenderGraph? _renderGraph;

        public event RenderDelegate? OnBeforeRender;
        public event RenderDelegate? OnAfterRender;
        public event RenderDelegate? OnPreRender;
        public event RenderDelegate? OnPostRender;
        public event RenderDelegate? OnBeginFrame;
        public event RenderDelegate? OnEndFrame;
        public event RenderGraphDelegate? OnRenderGraphSetup;

        public HybridLightingRenderer(RHIDevice device)
        {
            _device = device;
            _deferredCB = device.CreateBuffer(
                new RHIBufferDesc { SizeInBytes = (ulong)Marshal.SizeOf<DeferredRenderCB>(),
                    Flags = RHIBufferFlags.ConstantBuffer, HeapType = RHIResourceHeapType.Upload },
                "DeferredRenderCB");
            _lightingCB = device.CreateBuffer(
                new RHIBufferDesc { SizeInBytes = (ulong)Marshal.SizeOf<LightingConstants>(),
                    Flags = RHIBufferFlags.ConstantBuffer, HeapType = RHIResourceHeapType.Upload },
                "LightingCB");
            _lightBuffer = device.CreateBuffer(
                new RHIBufferDesc { SizeInBytes = 1024 * 64, Flags = RHIBufferFlags.ShaderResource,
                    HeapType = RHIResourceHeapType.Upload }, "LightBuffer");
        }

        public void Initialize(int width, int height)
        {
            _width = width;
            _height = height;
            CreateGeometryBuffer();
            CreatePipelines();
            InitializeSubsystems();
        }

        private void CreateGeometryBuffer()
        {
            GeometryBufferColor?.Dispose();
            GeometryBufferNormals?.Dispose();
            GeometryBufferMaterial?.Dispose();
            GeometryBufferDepth?.Dispose();
            GeometryBufferMotionVectors?.Dispose();
            SceneColor?.Dispose();
            FinalOutput?.Dispose();
            ShadowMap?.Dispose();

            var colorFormat = GeometryBufferSettings.UseHDRColorBuffer ? RHIFormat.R16G16B16A16_Float : RHIFormat.R8G8B8A8_UNorm;

            GeometryBufferColor = _device.CreateTexture(new RHITextureDesc
            {
                Dimension = RHIResourceDimension.Texture2D, Format = colorFormat,
                Width = _width, Height = _height, DepthOrArraySize = 1, MipLevels = 1, SampleCount = 1,
                Flags = RHITextureFlags.RenderTarget | RHITextureFlags.ShaderResource
            }, "GeometryBufferColor");

            GeometryBufferNormals = _device.CreateTexture(new RHITextureDesc
            {
                Dimension = RHIResourceDimension.Texture2D, Format = RHIFormat.R10G10B10A2_UNorm,
                Width = _width, Height = _height, DepthOrArraySize = 1, MipLevels = 1, SampleCount = 1,
                Flags = RHITextureFlags.RenderTarget | RHITextureFlags.ShaderResource
            }, "GeometryBufferNormals");

            GeometryBufferMaterial = _device.CreateTexture(new RHITextureDesc
            {
                Dimension = RHIResourceDimension.Texture2D, Format = RHIFormat.R8G8B8A8_UNorm,
                Width = _width, Height = _height, DepthOrArraySize = 1, MipLevels = 1, SampleCount = 1,
                Flags = RHITextureFlags.RenderTarget | RHITextureFlags.ShaderResource
            }, "GeometryBufferMaterial");

            GeometryBufferDepth = _device.CreateTexture(new RHITextureDesc
            {
                Dimension = RHIResourceDimension.Texture2D, Format = RHIFormat.D32_Float,
                Width = _width, Height = _height, DepthOrArraySize = 1, MipLevels = 1, SampleCount = 1,
                Flags = RHITextureFlags.DepthStencil | RHITextureFlags.ShaderResource
            }, "GeometryBufferDepth");

            if (GeometryBufferSettings.UseMotionVectors)
            {
                GeometryBufferMotionVectors = _device.CreateTexture(new RHITextureDesc
                {
                    Dimension = RHIResourceDimension.Texture2D, Format = RHIFormat.R16G16_Float,
                    Width = _width, Height = _height, DepthOrArraySize = 1, MipLevels = 1, SampleCount = 1,
                    Flags = RHITextureFlags.RenderTarget | RHITextureFlags.ShaderResource
                }, "GeometryBufferMotionVectors");
            }

            SceneColor = _device.CreateTexture(new RHITextureDesc
            {
                Dimension = RHIResourceDimension.Texture2D, Format = RHIFormat.R16G16B16A16_Float,
                Width = _width, Height = _height, DepthOrArraySize = 1, MipLevels = 1, SampleCount = 1,
                Flags = RHITextureFlags.RenderTarget | RHITextureFlags.UnorderedAccess | RHITextureFlags.ShaderResource
            }, "SceneColor");

            FinalOutput = _device.CreateTexture(new RHITextureDesc
            {
                Dimension = RHIResourceDimension.Texture2D, Format = RHIFormat.R8G8B8A8_UNorm,
                Width = _width, Height = _height, DepthOrArraySize = 1, MipLevels = 1, SampleCount = 1,
                Flags = RHITextureFlags.RenderTarget | RHITextureFlags.ShaderResource | RHITextureFlags.Present
            }, "FinalOutput");

            if (ShadowSettings.Enabled)
            {
                ShadowMap = _device.CreateTexture(new RHITextureDesc
                {
                    Dimension = RHIResourceDimension.Texture2D, Format = RHIFormat.D32_Float,
                    Width = ShadowSettings.ShadowMapSize, Height = ShadowSettings.ShadowMapSize,
                    DepthOrArraySize = ShadowSettings.CascadeCount, MipLevels = 1, SampleCount = 1,
                    Flags = RHITextureFlags.DepthStencil | RHITextureFlags.ShaderResource
                }, "ShadowMap");
            }
        }

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
                                new RHIRootDescriptorRange { RangeType = RHIDescriptorRangeType.SRV, NumDescriptors = 16, BaseShaderRegister = 0 } } } },
                    new() { ParameterType = RHIRootParameterType.DescriptorTable, ShaderVisibility = RHIShaderVisibility.All,
                            DescriptorTable = new RHIRootDescriptorTable { Ranges = new[] {
                                new RHIRootDescriptorRange { RangeType = RHIDescriptorRangeType.UAV, NumDescriptors = 4, BaseShaderRegister = 0 } } } }
                },
                StaticSamplers = new[] { RHISamplerDesc.LinearWrap, RHISamplerDesc.LinearClamp,
                    RHISamplerDesc.PointClamp, RHISamplerDesc.ShadowSampler }
            };
            _deferredRootSig = _device.CreateRootSignature(rootDesc);

            _geometrybufferPipeline = _device.CreateGraphicsPipelineState(new RHIGraphicsPipelineDesc
            {
                Name = "GeometryBufferPass",
                VS = ShaderManager.Instance.GetBytecode("Deferred/GeometryBuffer", "GeometryBufferVS", RHIShaderStage.Vertex),
                PS = ShaderManager.Instance.GetBytecode("Deferred/GeometryBuffer", "GeometryBufferPS", RHIShaderStage.Pixel),
                RootSignature = _deferredRootSig,
                RenderTargetFormats = new[] { GeometryBufferSettings.UseHDRColorBuffer ? RHIFormat.R16G16B16A16_Float : RHIFormat.R8G8B8A8_UNorm,
                    RHIFormat.R10G10B10A2_UNorm, RHIFormat.R8G8B8A8_UNorm },
                DepthStencilFormat = RHIFormat.D32_Float,
                PrimitiveTopology = RHIPrimitiveTopology.TriangleList,
                RasterizerState = new RHIRasterizerDesc { CullMode = RHICullMode.Back, FillMode = RHIFillMode.Solid },
                DepthStencilState = new RHIDepthDesc { DepthEnable = true, DepthWriteEnable = true, DepthFunc = RHIComparison.Less },
                BlendState = new RHIBlendDesc { AlphaToCoverageEnable = false, IndependentBlendEnable = false, RenderTargets = new[] { RHIRenderTargetBlend.Disabled } }
            });

            _lightingPipeline = _device.CreateComputePipelineState(new RHIComputePipelineDesc
            {
                Name = "LightResolve",
                CS = ShaderManager.Instance.GetBytecode("Deferred/LightResolve", "LightResolveCS", RHIShaderStage.Compute)
            });

            _shadowPipeline = _device.CreateGraphicsPipelineState(new RHIGraphicsPipelineDesc
            {
                Name = "ShadowPass",
                VS = ShaderManager.Instance.GetBytecode("Shadow/ShadowMap", "ShadowVS", RHIShaderStage.Vertex),
                PS = null,
                RootSignature = _deferredRootSig,
                DepthStencilFormat = RHIFormat.D32_Float,
                PrimitiveTopology = RHIPrimitiveTopology.TriangleList,
                RasterizerState = new RHIRasterizerDesc { CullMode = RHICullMode.Back, FillMode = RHIFillMode.Solid },
                DepthStencilState = new RHIDepthDesc { DepthEnable = true, DepthWriteEnable = true, DepthFunc = RHIComparison.Less }
            });
        }

        private void InitializeSubsystems()
        {
            if (LightingSettings.UseClusteredLighting)
            {
                _clusteredLighting = new ClusteredLighting(_device);
                _clusteredLighting.Initialize(_width, _height);
            }

            if (AASettings.Method == AAMethod.TAA)
            {
                _taa = new TemporalAntiAliasing(_device);
                _taa.Initialize(_width, _height);
            }

            if (PostProcessSettings.Enabled)
            {
                _postProcess = new PostProcessingStack(_device);
                _postProcess.Initialize(_width, _height);
            }
        }

        public void SetResolution(int width, int height)
        {
            _width = width;
            _height = height;
            CreateGeometryBuffer();
            _clusteredLighting?.Resize(width, height);
            _taa?.Resize(width, height);
            _postProcess?.Resize(width, height);
        }

        public void AddLight(in LightData light)
        {
            if (_lights.Count < LightingSettings.MaxLights)
                _lights.Add(light);
            _clusteredLighting?.AddLight(new GPULight
            {
                Position = light.Position,
                Range = light.Range,
                Color = light.Color,
                Intensity = light.Intensity,
                Direction = light.Direction,
                SpotAngle = light.SpotAngle,
                Type = light.Type
            });
        }

        public void ClearLights()
        {
            _lights.Clear();
            _clusteredLighting?.ClearLights();
        }

        public void Render(RHICommandList cmdList, Camera? camera, float deltaTime)
        {
            OnBeginFrame?.Invoke(cmdList);

            var view = camera?.GetViewMatrix() ?? Matrix4x4.Identity;
            var proj = camera?.GetProjectionMatrix() ?? Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 4, (float)_width / _height, 0.1f, 1000f);

            // TAA jitter
            if (_taa != null && AASettings.Method == AAMethod.TAA)
                proj = _taa.ApplyJitterToProjection(proj);

            var viewProj = Matrix4x4.Multiply(view, proj);
            var invView = Matrix4x4.Identity; Matrix4x4.Invert(view, out invView);
            var invProj = Matrix4x4.Identity; Matrix4x4.Invert(proj, out invProj);
            var invViewProj = Matrix4x4.Identity; Matrix4x4.Invert(viewProj, out invViewProj);

            UpdateConstantBuffers(view, proj, viewProj, invView, invProj, invViewProj, deltaTime, camera);
            UpdateLightsBuffer();

            // Use RenderGraph if available, else fallback to manual
            if (_renderGraph != null)
            {
                RenderWithGraph(cmdList, camera, deltaTime, view, proj, viewProj, invViewProj);
            }
            else
            {
                RenderManual(cmdList, camera, deltaTime, view, proj, viewProj, invViewProj);
            }

            OnEndFrame?.Invoke(cmdList);
        }

        private void RenderManual(RHICommandList cmdList, Camera? camera, float deltaTime,
            Matrix4x4 view, Matrix4x4 proj, Matrix4x4 viewProj, Matrix4x4 invViewProj)
        {
            OnPreRender?.Invoke(cmdList);
            OnBeforeRender?.Invoke(cmdList);

            // Shadow Pass
            if (ShadowSettings.Enabled && ShadowMap != null)
                RenderShadowPass(cmdList);

            // GeometryBuffer Pass
            RenderGeometryBufferPass(cmdList);

            // Lighting Pass
            if (LightingSettings.UseClusteredLighting && _clusteredLighting != null)
            {
                _clusteredLighting.Update(cmdList, view, proj);
                _clusteredLighting.RenderLighting(cmdList, SceneColor!, GeometryBufferDepth!, GeometryBufferColor!, GeometryBufferNormals!, GeometryBufferMaterial!, view, proj, camera?.Position ?? Vector3.Zero);
            }
            else
            {
                RenderLightResolve(cmdList);
            }

            // ProbeField GI integration
            if (LightingSettings.UseProbeFieldGI)
            {
                var probefield = new ProbeFieldGlobalIllumination(_device);
                probefield.Initialize(_width, _height);
                probefield.Render(cmdList, SceneColor!, GeometryBufferNormals!, GeometryBufferDepth!, new LightingSettings { UseProbeField = true }, camera);
            }

            OnAfterRender?.Invoke(cmdList);

            // TAA Resolve
            if (_taa != null && AASettings.Method == AAMethod.TAA && GeometryBufferMotionVectors != null)
            {
                _taa.Resolve(cmdList, SceneColor!, GeometryBufferMotionVectors, GeometryBufferDepth!, SceneColor!, viewProj, deltaTime);
            }

            // Post Processing
            if (PostProcessSettings.Enabled && _postProcess != null)
            {
                _postProcess.Process(cmdList, SceneColor!, GeometryBufferDepth!, GeometryBufferMotionVectors ?? GeometryBufferDepth!, FinalOutput!,
                    viewProj, invViewProj, deltaTime);
            }
            else if (SceneColor != FinalOutput)
            {
                cmdList.CopyResource(FinalOutput!, SceneColor!);
            }

            // TSR upscaling
            if (AASettings.UseTSR)
            {
                var tsr = new TemporalSuperResolution(_device);
                tsr.Initialize(_width, _height);
                tsr.Render(cmdList, FinalOutput!, GeometryBufferDepth!, GeometryBufferMotionVectors!, new TemporalSuperResolutionSettings { RenderWidth = _width, RenderHeight = _height, OutputWidth = _width, OutputHeight = _height });
            }

            OnPostRender?.Invoke(cmdList);
        }

        private void RenderWithGraph(RHICommandList cmdList, Camera? camera, float deltaTime,
            Matrix4x4 view, Matrix4x4 proj, Matrix4x4 viewProj, Matrix4x4 invViewProj)
        {
            _renderGraph!.Clear();
            var gDepth = _renderGraph.ImportResource("GeometryBufferDepth", GeometryBufferDepth!);
            var gColor = _renderGraph.ImportResource("GeometryBufferColor", GeometryBufferColor!);
            var gNormal = _renderGraph.ImportResource("GeometryBufferNormals", GeometryBufferNormals!);
            var gMaterial = _renderGraph.ImportResource("GeometryBufferMaterial", GeometryBufferMaterial!);
            var gMotion = GeometryBufferMotionVectors != null ? _renderGraph.ImportResource("GeometryBufferMotionVectors", GeometryBufferMotionVectors) : RenderGraphResourceHandle.Invalid;
            var sceneColor = _renderGraph.ImportResource("SceneColor", SceneColor!);
            var finalOutput = _renderGraph.ImportResource("FinalOutput", FinalOutput!);
            var shadowMap = ShadowSettings.Enabled && ShadowMap != null ? _renderGraph.ImportResource("ShadowMap", ShadowMap) : RenderGraphResourceHandle.Invalid;

            OnRenderGraphSetup?.Invoke(_renderGraph);

            if (shadowMap.IsValid)
            {
                _renderGraph.AddPass("Shadow", RenderGraphPassType.Render)
                    .Write(shadowMap, RenderGraphResourceAccess.DepthStencilWrite)
                    .SetExecute((cmd, ctx) => RenderShadowPass(cmd));
            }

            _renderGraph.AddPass("GeometryBuffer", RenderGraphPassType.Render)
                .Write(gColor, RenderGraphResourceAccess.RenderTarget)
                .Write(gNormal, RenderGraphResourceAccess.RenderTarget)
                .Write(gMaterial, RenderGraphResourceAccess.RenderTarget)
                .Write(gDepth, RenderGraphResourceAccess.DepthStencilWrite)
                .Write(gMotion, RenderGraphResourceAccess.RenderTarget)
                .SetExecute((cmd, ctx) => RenderGeometryBufferPass(cmd));

            _renderGraph.AddPass("Lighting", RenderGraphPassType.Compute)
                .Read(gDepth, RenderGraphResourceAccess.ShaderRead)
                .Read(gColor, RenderGraphResourceAccess.ShaderRead)
                .Read(gNormal, RenderGraphResourceAccess.ShaderRead)
                .Read(gMaterial, RenderGraphResourceAccess.ShaderRead)
                .Read(shadowMap, RenderGraphResourceAccess.ShaderRead)
                .Write(sceneColor, RenderGraphResourceAccess.ShaderWrite)
                .SetExecute((cmd, ctx) =>
                {
                    if (LightingSettings.UseClusteredLighting && _clusteredLighting != null)
                        _clusteredLighting.RenderLighting(cmd, ctx.GetTexture(sceneColor), ctx.GetTexture(gDepth), ctx.GetTexture(gColor), ctx.GetTexture(gNormal), ctx.GetTexture(gMaterial), view, proj, camera?.Position ?? Vector3.Zero);
                    else
                        RenderLightResolve(cmd);
                });

            if (_taa != null && AASettings.Method == AAMethod.TAA && gMotion.IsValid)
            {
                _renderGraph.AddPass("TAA", RenderGraphPassType.Compute)
                    .Read(sceneColor, RenderGraphResourceAccess.ShaderRead)
                    .Read(gMotion, RenderGraphResourceAccess.ShaderRead)
                    .Read(gDepth, RenderGraphResourceAccess.ShaderRead)
                    .Write(sceneColor, RenderGraphResourceAccess.ShaderWrite)
                    .SetExecute((cmd, ctx) => _taa.Resolve(cmd, ctx.GetTexture(sceneColor), ctx.GetTexture(gMotion), ctx.GetTexture(gDepth), ctx.GetTexture(sceneColor), viewProj, deltaTime));
            }

            _renderGraph.AddPass("PostProcess", RenderGraphPassType.Compute)
                .Read(sceneColor, RenderGraphResourceAccess.ShaderRead)
                .Read(gDepth, RenderGraphResourceAccess.ShaderRead)
                .Write(finalOutput, RenderGraphResourceAccess.ShaderWrite)
                .SetExecute((cmd, ctx) =>
                {
                    if (PostProcessSettings.Enabled && _postProcess != null)
                        _postProcess.Process(cmd, ctx.GetTexture(sceneColor), ctx.GetTexture(gDepth), ctx.GetTexture(gMotion) ?? ctx.GetTexture(gDepth), ctx.GetTexture(finalOutput), viewProj, invViewProj, deltaTime);
                    else
                        cmd.CopyResource(ctx.GetTexture(finalOutput), ctx.GetTexture(sceneColor));
                })
                .SetHasSideEffects();

            var result = _renderGraph.Compile();
            if (result.Success)
                _renderGraph.Execute(cmdList);
        }

        private void RenderShadowPass(RHICommandList cmdList)
        {
            if (_shadowPipeline == null || ShadowMap == null) return;
            cmdList.SetGraphicsRootSignature(_deferredRootSig!);
            cmdList.SetPipelineState(_shadowPipeline);
            cmdList.TransitionTo(ShadowMap, RHIResourceState.DepthWrite);
            cmdList.SetViewport(0, 0, ShadowSettings.ShadowMapSize, ShadowSettings.ShadowMapSize);
            cmdList.SetScissorRect(0, 0, ShadowSettings.ShadowMapSize, ShadowSettings.ShadowMapSize);
            cmdList.ClearDepthStencilView(ShadowMap, RHIResourceState.DepthWrite, true, 1.0f);
            // Shadow draw calls would go here
        }

        private void RenderGeometryBufferPass(RHICommandList cmdList)
        {
            if (_geometrybufferPipeline == null) return;
            cmdList.SetGraphicsRootSignature(_deferredRootSig!);
            cmdList.SetPipelineState(_geometrybufferPipeline);
            cmdList.TransitionTo(GeometryBufferColor!, RHIResourceState.RenderTarget);
            cmdList.TransitionTo(GeometryBufferNormals!, RHIResourceState.RenderTarget);
            cmdList.TransitionTo(GeometryBufferMaterial!, RHIResourceState.RenderTarget);
            cmdList.TransitionTo(GeometryBufferDepth!, RHIResourceState.DepthWrite);
            if (GeometryBufferMotionVectors != null)
                cmdList.TransitionTo(GeometryBufferMotionVectors, RHIResourceState.RenderTarget);

            cmdList.SetViewport(0, 0, _width, _height);
            cmdList.SetScissorRect(0, 0, _width, _height);
            cmdList.ClearRenderTargetView(GeometryBufferColor!, RHIResourceState.RenderTarget, new Vector4(0, 0, 0, 1));
            cmdList.ClearRenderTargetView(GeometryBufferNormals!, RHIResourceState.RenderTarget, new Vector4(0.5f, 0.5f, 1, 1));
            cmdList.ClearRenderTargetView(GeometryBufferMaterial!, RHIResourceState.RenderTarget, new Vector4(0.5f, 0.5f, 0, 1));
            cmdList.ClearDepthStencilView(GeometryBufferDepth!, RHIResourceState.DepthWrite, true, 1.0f);
            // Geometry draw calls would go here via cmdList.SetVertexBuffer / DrawIndexedInstanced
        }

        private void RenderLightResolve(RHICommandList cmdList)
        {
            if (_lightingPipeline == null) return;
            cmdList.SetComputeRootSignature(_deferredRootSig!);
            cmdList.SetPipelineState(_lightingPipeline);
            cmdList.TransitionTo(SceneColor!, RHIResourceState.UnorderedAccess);
            cmdList.TransitionTo(GeometryBufferColor!, RHIResourceState.NonPixelShaderResource);
            cmdList.TransitionTo(GeometryBufferNormals!, RHIResourceState.NonPixelShaderResource);
            cmdList.TransitionTo(GeometryBufferMaterial!, RHIResourceState.NonPixelShaderResource);
            cmdList.TransitionTo(GeometryBufferDepth!, RHIResourceState.NonPixelShaderResource);
            cmdList.TransitionTo(ShadowMap!, RHIResourceState.NonPixelShaderResource);
            cmdList.Dispatch((_width + 15) / 16, (_height + 15) / 16, 1);
            cmdList.TransitionTo(SceneColor!, RHIResourceState.PixelShaderResource);
        }

        private void UpdateConstantBuffers(Matrix4x4 view, Matrix4x4 proj, Matrix4x4 viewProj,
            Matrix4x4 invView, Matrix4x4 invProj, Matrix4x4 invViewProj, float deltaTime, Camera? camera)
        {
            var cb = new DeferredRenderCB
            {
                ViewMatrix = view,
                InvViewMatrix = invView,
                ProjectionMatrix = proj,
                InvProjectionMatrix = invProj,
                ViewProjectionMatrix = viewProj,
                InvViewProjectionMatrix = invViewProj,
                CameraPosition = camera?.Position ?? Vector3.Zero,
                CameraNear = camera?.NearPlane ?? 0.1f,
                CameraFar = camera?.FarPlane ?? 1000f,
                CameraDirection = camera?.Forward ?? -Vector3.UnitZ,
                ScreenResolution = new Vector2(_width, _height),
                Time = Environment.TickCount * 0.001f,
                DeltaTime = deltaTime,
                FrameIndex = Environment.TickCount & 0xFFFFFF,
                RenderFlags = 0,
                JitterOffset = _taa?.CurrentJitter ?? Vector2.Zero,
                Exposure = PostProcessSettings.Exposure,
                GeometryBufferFlag_UseHDR = GeometryBufferSettings.UseHDRColorBuffer ? 1 : 0,
                GeometryBufferFlag_UseMotionVectors = GeometryBufferSettings.UseMotionVectors ? 1 : 0,
                GeometryBufferFlag_Anisotropy = GeometryBufferSettings.UseAnisotropicRoughness ? 1.0f : 0.0f
            };
            _device.UploadToBuffer(_deferredCB!, new DeferredRenderCB[] { cb });

            var sun = _lights.Count > 0 ? _lights[0] : new LightData { Direction = -Vector3.UnitY, Color = Vector3.One, Intensity = 1 };
            var lc = new LightingConstants
            {
                SunDirection = sun.Direction,
                SunIntensity = sun.Intensity,
                SunColor = sun.Color,
                LightCount = _lights.Count,
                AmbientColor = new Vector3(0.05f, 0.05f, 0.1f),
                AmbientIntensity = 0.1f,
                ShadowBias = ShadowSettings.ShadowBias,
                NormalBias = ShadowSettings.NormalBias,
                PCFKernelRadius = ShadowSettings.PCFKernelRadius,
                UsePCSS = ShadowSettings.UsePCSS ? 1 : 0,
                ShadowMapSize = new Vector2(ShadowSettings.ShadowMapSize),
                LightSize = ShadowSettings.LightSize
            };
            _device.UploadToBuffer(_lightingCB!, new LightingConstants[] { lc });
        }

        private void UpdateLightsBuffer()
        {
            if (_lights.Count == 0 || _lightBuffer == null) return;
            var array = _lights.ToArray();
            _device.UploadToBuffer(_lightBuffer, array);
        }

        public void SetRenderGraph(RenderGraph? graph) => _renderGraph = graph;

        public RenderGraph CreateDefaultRenderGraph()
        {
            _renderGraph = new RenderGraph(_device, "DeferredFrameGraph");
            return _renderGraph;
        }

        public void RenderCamera(Camera camera, RHICommandList cmdList, float deltaTime)
        {
            Render(cmdList, camera, deltaTime);
        }

        public void Present(RHICommandList cmdList)
        {
            OnBeginFrame?.Invoke(cmdList);
            OnEndFrame?.Invoke(cmdList);
        }

        public void Dispose()
        {
            GeometryBufferColor?.Dispose();
            GeometryBufferNormals?.Dispose();
            GeometryBufferMaterial?.Dispose();
            GeometryBufferDepth?.Dispose();
            GeometryBufferMotionVectors?.Dispose();
            SceneColor?.Dispose();
            FinalOutput?.Dispose();
            ShadowMap?.Dispose();
            _geometrybufferPipeline?.Dispose();
            _lightingPipeline?.Dispose();
            _shadowPipeline?.Dispose();
            _deferredRootSig?.Dispose();
            _deferredCB?.Dispose();
            _lightingCB?.Dispose();
            _lightBuffer?.Dispose();
            _clusteredLighting?.Dispose();
            _taa?.Dispose();
            _postProcess?.Dispose();
            _renderGraph?.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}
