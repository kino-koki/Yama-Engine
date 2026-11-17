using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using YamaEngine.RHI;
using YamaEngine.Shaders;

namespace YamaEngine.Rendering
{
    // ════════════════════════════════════════════════════════════════════════
    // yama 后处理栈 (超越 UE7 级别)
    //
    // 完整的电影级后处理管线, 按 HDR 线性空间执行, 最终输出到 LDR 交换链:
    //  自动曝光(直方图) -> 景深DoF -> 运动模糊 -> Bloom -> 镜头光晕 -> 色差
    //  -> 暗角 -> 胶片颗粒 -> 色调映射(ACES) -> 颜色分级(LUT) -> 输出
    // 全部以计算着色器实现, 支持半分辨率优化与分块(tile)处理。
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>后处理栈配置。</summary>
    public sealed class PostProcessingSettings
    {
        // --- 自动曝光 ---
        /// <summary>启用自动曝光。</summary>
        public bool EnableAutoExposure = true;
        /// <summary>目标平均亮度 (关键值)。</summary>
        public float ExposureKey = 0.18f;
        /// <summary>曝光补偿 (EV)。</summary>
        public float ExposureCompensation = 0.0f;
        /// <summary>曝光适应速度 (每秒)。</summary>
        public float ExposureAdaptationSpeed = 1.5f;
        /// <summary>最小曝光值。</summary>
        public float MinExposure = -8.0f;
        /// <summary>最大曝光值。</summary>
        public float MaxExposure = 8.0f;
        /// <summary>手动曝光值 (关闭自动时使用)。</summary>
        public float ManualExposure = 0.0f;

        // --- 景深 (DoF) ---
        /// <summary>启用景深。</summary>
        public bool EnableDoF = true;
        /// <summary>对焦距离 (世界单位)。</summary>
        public float FocusDistance = 10.0f;
        /// <summary>光圈大小 (f值越小景深越浅)。</summary>
        public float Aperture = 2.8f;
        /// <summary>散景形状。</summary>
        public BokehShape BokehShape = BokehShape.Circular;
        /// <summary>散景最大半径 (像素)。</summary>
        public float MaxBokehRadius = 24.0f;
        /// <summary>近模糊开始距离。</summary>
        public float NearFocusStart = 2.0f;
        /// <summary>远模糊开始距离。</summary>
        public float FarFocusStart = 20.0f;

        // --- 运动模糊 ---
        /// <summary>启用运动模糊。</summary>
        public bool EnableMotionBlur = true;
        /// <summary>运动模糊强度 (最大速度像素)。</summary>
        public float MotionBlurStrength = 16.0f;
        /// <summary>运动模糊采样数。</summary>
        public int MotionBlurSamples = 16;

        // --- Bloom (泛光) ---
        /// <summary>启用泛光。</summary>
        public bool EnableBloom = true;
        /// <summary>泛光阈值 (亮度)。</summary>
        public float BloomThreshold = 1.0f;
        /// <summary>泛光强度。</summary>
        public float BloomIntensity = 0.8f;
        /// <summary>泛光半径。</summary>
        public float BloomRadius = 0.85f;
        /// <summary>泛光弥散颜色。</summary>
        public Vector3 BloomTint = new(1.0f, 0.95f, 0.9f);

        // --- 镜头光晕 (Lens Flare) ---
        /// <summary>启用镜头光晕。</summary>
        public bool EnableLensFlare = true;
        /// <summary>光晕强度。</summary>
        public float LensFlareIntensity = 0.5f;
        /// <summary>光晕光线数。</summary>
        public int LensFlareRayCount = 6;

        // --- 色差 (Chromatic Aberration) ---
        /// <summary>启用色差。</summary>
        public bool EnableChromaticAberration = true;
        /// <summary>色差强度。</summary>
        public float ChromaticAberrationAmount = 0.002f;
        /// <summary>色差径向衰减开始。</summary>
        public float ChromaticAberrationRadialStart = 0.5f;

        // --- 暗角 (Vignette) ---
        /// <summary>启用暗角。</summary>
        public bool EnableVignette = true;
        /// <summary>暗角强度。</summary>
        public float VignetteIntensity = 0.5f;
        /// <summary>暗角半径。</summary>
        public float VignetteRadius = 0.75f;
        /// <summary>暗角柔和度。</summary>
        public float VignetteSoftness = 0.3f;
        /// <summary>暗角颜色。</summary>
        public Vector3 VignetteColor = new(0.0f, 0.0f, 0.0f);

        // --- 胶片颗粒 (Film Grain) ---
        /// <summary>启用胶片颗粒。</summary>
        public bool EnableFilmGrain = true;
        /// <summary>颗粒强度。</summary>
        public float FilmGrainIntensity = 0.05f;
        /// <summary>颗粒大小。</summary>
        public float FilmGrainSize = 1.5f;
        /// <summary>颗粒动画速度。</summary>
        public float FilmGrainSpeed = 1.0f;

        // --- 色调映射 ---
        /// <summary>色调映射算子。</summary>
        public TonemapOperator TonemapOperator = TonemapOperator.ACES;
        /// <summary>色调映射后是否应用 OETF (sRGB)。</summary>
        public bool ApplySRGBCurve = true;

        // --- 颜色分级 ---
        /// <summary>启用颜色分级。</summary>
        public bool EnableColorGrading = true;
        /// <summary>白平衡温度 (-1 暖, +1 冷)。</summary>
        public float WhiteBalanceTemp = 0.0f;
        /// <summary>白平衡色调。</summary>
        public float WhiteBalanceTint = 0.0f;
        /// <summary>全局饱和度。</summary>
        public float Saturation = 1.0f;
        /// <summary>全局对比度。</summary>
        public float Contrast = 1.0f;
        /// <summary>全局增益。</summary>
        public float Gain = 1.0f;
        /// <summary>全局偏移。</summary>
        public Vector3 ColorOffset = Vector3.Zero;
        /// <summary>LUT 强度 (0=无, 1=完全)。</summary>
        public float LutIntensity = 1.0f;
        /// <summary>LUT 纹理 (32x32x32)。</summary>
        public RHITexture? LutTexture;

        // --- 后缩放 ---
        /// <summary>后处理输出缩放 (用于超分后)。</summary>
        public float OutputScale = 1.0f;
    }

    /// <summary>后处理共享常量缓冲 (b0, 与 HLSL PostProcessCB 一致, 16 字节)。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct PostProcessCB
    {
        public float Time;
        public float DeltaTime;
        public int FrameIndex;
        public float Pad;
    }

    /// <summary>Bloom 常量缓冲 (b2, 与 HLSL PostProcessBloomCB 一致)。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct PostProcessBloomCB
    {
        public float Threshold;
        public float Intensity;
        public float Radius;
        public Vector3 Tint;
        public float Pad;
    }

    /// <summary>景深常量缓冲 (b2, 与 HLSL PostProcessDoFCB 一致)。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct PostProcessDoFCB
    {
        public Matrix4x4 ProjMatrix;
        public float FocusDistance;
        public float Aperture;
        public float MaxBokehRadius;
        public float NearFocusStart;
        public float FarFocusStart;
        public int BokehShape;
        public float Pad0, Pad1, Pad2;
    }

    /// <summary>运动模糊常量缓冲 (b2, 与 HLSL PostProcessMotionBlurCB 一致)。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct PostProcessMotionBlurCB
    {
        public float Strength;
        public float MaxVelocity;
        public int SampleCount;
        public float Pad0;
        public Matrix4x4 PrevViewProj;
        public Matrix4x4 InvViewProj;
        public float Pad1;
        public float Pad2;
    }

    /// <summary>色调映射常量缓冲 (b2, 与 HLSL PostProcessTonemapCB 一致)。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct PostProcessTonemapCB
    {
        public float Exposure;
        public int TonemapOperator;
        public int ApplySRGB;
        public int EnableColorGrading;
        public float Saturation;
        public float Contrast;
        public float Gain;
        public float Pad0;
        public Vector3 ColorOffset;
        public float WhiteBalanceTemp;
        public float WhiteBalanceTint;
        public float LutIntensity;
    }

    /// <summary>自动曝光常量缓冲 (b2, 与 HLSL PostProcessExposureCB 一致)。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct PostProcessExposureCB
    {
        public float Key;
        public float Compensation;
        public float AdaptationSpeed;
        public float MinExposure;
        public float MaxExposure;
        public float ManualExposure;
        public int UseAutoExposure;
        public float Pad0;
    }

    /// <summary>
    /// 后处理栈: 编排所有后处理 Pass 的执行顺序与资源管理。
    /// 采用 Ping-Pong 中间纹理在 Pass 间传递。
    /// </summary>
    public sealed class PostProcessingStack : IDisposable
    {
        private readonly RHIDevice _device;
        private readonly PostProcessingSettings _settings;

        private RHIRootSignature? _rootSignature;
        private RHIPipelineState? _exposurePipeline;
        private RHIPipelineState? _dofPipeline;
        private RHIPipelineState? _motionBlurPipeline;
        private RHIPipelineState? _bloomExtractPipeline;
        private RHIPipelineState? _bloomBlurPipeline;
        private RHIPipelineState? _bloomCombinePipeline;
        private RHIPipelineState? _lensPipeline;
        private RHIPipelineState? _tonemapPipeline;

        // Ping-Pong 中间纹理
        private RHITexture? _rtA, _rtB;
        // Bloom 金字塔 (降采样)
        private RHITexture? _bloomPyramid;
        // 自动曝光历史
        private RHIBuffer? _exposureBuffer;
        private RHIBuffer? _exposureReadback;
        private float _currentExposure;

        // 常量上传缓冲 + 资源描述符
        private RHIBuffer? _sharedCBUpload;
        private RHIBuffer? _passCBUpload;
        private RHIDescriptorHandle _rtASRV, _rtBSRV, _rtAUAV, _rtBUAV;
        private RHIDescriptorHandle _bloomPyramidSRV, _bloomPyramidUAV;
        private RHIDescriptorHandle _exposureBufferUAV;
        private RHITexture? _lensDirt;
        private RHIDescriptorHandle _lensDirtSRV;

        // 自动曝光 GPU→CPU 回读 (拷贝引擎 + 回读管理器)
        private RHICopyEngine _copyEngine;
        private RHIReadbackManager _readbackManager;
        private readonly Queue<long> _pendingReadbackIds = new();
        private ulong _frameCounter;
        private bool _exposureInCopySource;

        private int _width, _height;

        /// <summary>配置。</summary>
        public PostProcessingSettings Settings => _settings;
        /// <summary>当前曝光值。</summary>
        public float CurrentExposure => _currentExposure;

        /// <summary>构造。</summary>
        public PostProcessingStack(RHIDevice device, PostProcessingSettings? settings = null)
        {
            _device = device;
            _settings = settings ?? new PostProcessingSettings();
        }

        /// <summary>初始化管线与资源。</summary>
        public void Initialize(int width, int height)
        {
            _width = width;
            _height = height;
            CreatePipelines();
            AllocateResources();
        }

        /// <summary>创建所有计算管线。</summary>
        private void CreatePipelines()
        {
            var rootDesc = new RHIRootSignatureDesc
            {
                Parameters = new RHIRootParameter[]
                {
                    new() { ParameterType = RHIRootParameterType.CBV, ShaderVisibility = RHIShaderVisibility.All,
                            Descriptor = new RHIRootDescriptor { ShaderRegister = 0 } },   // 0:  b0 共享 (时间/帧号)
                    new() { ParameterType = RHIRootParameterType.CBV, ShaderVisibility = RHIShaderVisibility.All,
                            Descriptor = new RHIRootDescriptor { ShaderRegister = 2 } },   // 1:  b2 per-pass CB
                    new() { ParameterType = RHIRootParameterType.DescriptorTable, ShaderVisibility = RHIShaderVisibility.All,
                            DescriptorTable = new RHIRootDescriptorTable { Ranges = new[] {
                                new RHIRootDescriptorRange { RangeType = RHIDescriptorRangeType.SRV, NumDescriptors = 1, BaseShaderRegister = 0 } } } }, // 2: t0 场景色
                    new() { ParameterType = RHIRootParameterType.DescriptorTable, ShaderVisibility = RHIShaderVisibility.All,
                            DescriptorTable = new RHIRootDescriptorTable { Ranges = new[] {
                                new RHIRootDescriptorRange { RangeType = RHIDescriptorRangeType.SRV, NumDescriptors = 1, BaseShaderRegister = 1 } } } }, // 3: t1 深度
                    new() { ParameterType = RHIRootParameterType.DescriptorTable, ShaderVisibility = RHIShaderVisibility.All,
                            DescriptorTable = new RHIRootDescriptorTable { Ranges = new[] {
                                new RHIRootDescriptorRange { RangeType = RHIDescriptorRangeType.SRV, NumDescriptors = 1, BaseShaderRegister = 2 } } } }, // 4: t2 运动矢量
                    new() { ParameterType = RHIRootParameterType.DescriptorTable, ShaderVisibility = RHIShaderVisibility.All,
                            DescriptorTable = new RHIRootDescriptorTable { Ranges = new[] {
                                new RHIRootDescriptorRange { RangeType = RHIDescriptorRangeType.SRV, NumDescriptors = 1, BaseShaderRegister = 3 } } } }, // 5: t3 上一 pass 输出
                    new() { ParameterType = RHIRootParameterType.DescriptorTable, ShaderVisibility = RHIShaderVisibility.All,
                            DescriptorTable = new RHIRootDescriptorTable { Ranges = new[] {
                                new RHIRootDescriptorRange { RangeType = RHIDescriptorRangeType.SRV, NumDescriptors = 1, BaseShaderRegister = 4 } } } }, // 6: t4 Bloom 金字塔
                    new() { ParameterType = RHIRootParameterType.DescriptorTable, ShaderVisibility = RHIShaderVisibility.All,
                            DescriptorTable = new RHIRootDescriptorTable { Ranges = new[] {
                                new RHIRootDescriptorRange { RangeType = RHIDescriptorRangeType.SRV, NumDescriptors = 1, BaseShaderRegister = 5 } } } }, // 7: t5 LUT
                    new() { ParameterType = RHIRootParameterType.DescriptorTable, ShaderVisibility = RHIShaderVisibility.All,
                            DescriptorTable = new RHIRootDescriptorTable { Ranges = new[] {
                                new RHIRootDescriptorRange { RangeType = RHIDescriptorRangeType.SRV, NumDescriptors = 1, BaseShaderRegister = 6 } } } }, // 8: t6 镜头污渍
                    new() { ParameterType = RHIRootParameterType.DescriptorTable, ShaderVisibility = RHIShaderVisibility.All,
                            DescriptorTable = new RHIRootDescriptorTable { Ranges = new[] {
                                new RHIRootDescriptorRange { RangeType = RHIDescriptorRangeType.UAV, NumDescriptors = 1, BaseShaderRegister = 0 } } } }, // 9: u0 输出/曝光缓冲
                    new() { ParameterType = RHIRootParameterType.DescriptorTable, ShaderVisibility = RHIShaderVisibility.All,
                            DescriptorTable = new RHIRootDescriptorTable { Ranges = new[] {
                                new RHIRootDescriptorRange { RangeType = RHIDescriptorRangeType.UAV, NumDescriptors = 1, BaseShaderRegister = 1 } } } }, // 10: u1 临时 (预留)
                    new() { ParameterType = RHIRootParameterType.DescriptorTable, ShaderVisibility = RHIShaderVisibility.All,
                            DescriptorTable = new RHIRootDescriptorTable { Ranges = new[] {
                                new RHIRootDescriptorRange { RangeType = RHIDescriptorRangeType.UAV, NumDescriptors = 1, BaseShaderRegister = 2 } } } }  // 11: u2 Bloom 金字塔 UAV
                },
                StaticSamplers = new[] { RHISamplerDesc.LinearWrap, RHISamplerDesc.PointClamp }
            };
            _rootSignature = _device.CreateRootSignature(rootDesc);

            _exposurePipeline = MakeCS("PPExposure", "ExposureCS");
            _dofPipeline = MakeCS("PPDoF", "DoFCS");
            _motionBlurPipeline = MakeCS("PPMotionBlur", "MotionBlurCS");
            _bloomExtractPipeline = MakeCS("PPBloomExtract", "BloomExtractCS");
            _bloomBlurPipeline = MakeCS("PPBloomBlur", "BloomBlurCS");
            _bloomCombinePipeline = MakeCS("PPBloomCombine", "BloomCombineCS");
            _lensPipeline = MakeCS("PPLens", "LensCS");
            _tonemapPipeline = MakeCS("PPTonemap", "TonemapCS");
        }

        private RHIPipelineState MakeCS(string name, string entry) =>
            _device.CreateComputePipelineState(new RHIComputePipelineDesc
            { Name = name, CS = ShaderManager.Instance.GetBytecode("PostProcess/PostProcess", entry, RHIShaderStage.Compute) });

        /// <summary>分配中间纹理资源。</summary>
        private void AllocateResources()
        {
            RHITextureDesc MakeRT(string name, RHIFormat fmt = RHIFormat.R16G16B16A16_Float) => new()
            {
                Dimension = RHIResourceDimension.Texture2D,
                Format = fmt,
                Width = _width, Height = _height, DepthOrArraySize = 1,
                MipLevels = 1, SampleCount = 1,
                Flags = RHITextureFlags.UnorderedAccess | RHITextureFlags.ShaderResource
            };
            _rtA = _device.CreateTexture(MakeRT("PPRTA"), "PPRTA");
            _rtB = _device.CreateTexture(MakeRT("PPRTB"), "PPRTB");

            // Bloom 金字塔 (降采样到 1/16)
            int bloomW = Math.Max(1, _width / 2);
            int bloomH = Math.Max(1, _height / 2);
            var bloomDesc = MakeRT("PPBloomPyramid");
            bloomDesc.Width = bloomW;
            bloomDesc.Height = bloomH;
            bloomDesc.MipLevels = 6;
            _bloomPyramid = _device.CreateTexture(bloomDesc, "PPBloomPyramid");

            // 曝光缓冲 (1 像素)
            var expDesc = new RHIBufferDesc
            {
                SizeInBytes = 16,
                Flags = RHIBufferFlags.UnorderedAccess | RHIBufferFlags.Structured,
                Stride = 16,
                HeapType = RHIResourceHeapType.Default
            };
            _exposureBuffer = _device.CreateBuffer(expDesc, "PPExposure");

            // 常量上传缓冲 + 资源描述符
            _sharedCBUpload = _device.CreateUploadBuffer(64, "PP_SharedCB");
            _passCBUpload = _device.CreateUploadBuffer(256, "PP_PassCB");
            _rtASRV = _device.CreateSRV(_rtA);
            _rtBSRV = _device.CreateSRV(_rtB);
            _rtAUAV = _device.CreateUAV(_rtA);
            _rtBUAV = _device.CreateUAV(_rtB);
            _bloomPyramidSRV = _device.CreateSRV(_bloomPyramid);
            _bloomPyramidUAV = _device.CreateUAV(_bloomPyramid);
            _exposureBufferUAV = _device.CreateUAV(_exposureBuffer, RHIFormat.R32_Float, true, 0, 4, 4);

            // 1x1 黑色镜头污渍 (无污渍 → LensCS 的 dirt 项为无操作)
            _lensDirt = _device.CreateTexture(new RHITextureDesc
            {
                Dimension = RHIResourceDimension.Texture2D,
                Format = RHIFormat.R8G8B8A8_UNorm,
                Width = 1, Height = 1, DepthOrArraySize = 1,
                MipLevels = 1, SampleCount = 1,
                Flags = RHITextureFlags.ShaderResource
            }, "PP_LensDirt");
            _device.UploadToTexture(_lensDirt, new byte[4], 1, 1);
            _lensDirtSRV = _device.CreateSRV(_lensDirt);

            // 自动曝光回读: 拷贝引擎 + 回读管理器 (仅回读 16B 曝光值, 小缓冲即可)
            _copyEngine = new RHICopyEngine(_device, uploadBufferSize: 4 * 1024 * 1024);
            _readbackManager = new RHIReadbackManager(_device, _copyEngine, bufferSize: 1024 * 1024);
        }

        /// <summary>窗口尺寸变化。</summary>
        public void Resize(int width, int height)
        {
            _width = width;
            _height = height;
            _rtA?.Dispose(); _rtB?.Dispose(); _bloomPyramid?.Dispose();
            AllocateResources();
        }

        /// <summary>
        /// 执行完整后处理管线。
        /// </summary>
        /// <param name="cmdList">命令列表。</param>
        /// <param name="sceneColor">HDR 场景颜色输入。</param>
        /// <param name="sceneDepth">场景深度。</param>
        /// <param name="motionVectors">运动矢量纹理。</param>
        /// <param name="output">最终 LDR 输出。</param>
        /// <param name="prevViewProj">上一帧视图投影 (运动模糊)。</param>
        /// <param name="invViewProj">逆视图投影。</param>
        /// <param name="deltaTime">帧时间。</param>
        public void Process(RHICommandList cmdList, RHITexture sceneColor, RHITexture sceneDepth,
            RHITexture motionVectors, RHITexture output, in Matrix4x4 prevViewProj,
            in Matrix4x4 invViewProj, float deltaTime)
        {
            _frameCounter++;
            _copyEngine?.BeginFrame(_frameCounter);
            _readbackManager?.BeginFrame(_frameCounter);

            // 自动曝光回读: 请求读取上一帧写入的曝光值 (16B), 并收割已就绪的历史结果
            if (_exposureBuffer != null && _readbackManager != null)
            {
                _pendingReadbackIds.Enqueue(_readbackManager.RequestBufferReadback(_exposureBuffer, 0, 16));
                while (_pendingReadbackIds.Count > 0 && _readbackManager.IsReady(_pendingReadbackIds.Peek()))
                {
                    long rid = _pendingReadbackIds.Dequeue();
                    byte[] data = _readbackManager.GetResultData(rid);
                    _readbackManager.ReleaseResult(rid);
                    if (data != null && data.Length >= 4)
                    {
                        float exposure = BitConverter.ToSingle(data, 0);
                        if (float.IsFinite(exposure))
                            _currentExposure = Math.Clamp(exposure, _settings.MinExposure, _settings.MaxExposure);
                    }
                }
                // 上一帧回读后缓冲处于 CopySource 状态, 本帧写入前转回 UAV
                if (_exposureInCopySource && _exposureBuffer != null)
                    cmdList.TransitionTo(_exposureBuffer, RHIResourceState.UnorderedAccess);
                _exposureInCopySource = false;
            }

            cmdList.SetComputeRootSignature(_rootSignature!);

            // 上传共享常量 (b0: 时间/帧号, LensCS 颗粒动画使用)
            var sharedCB = new PostProcessCB
            {
                Time = (float)Environment.TickCount * 0.001f,
                DeltaTime = deltaTime,
                FrameIndex = Environment.TickCount & 0xFFFFFF
            };
            _device.UploadToBuffer(_sharedCBUpload!, MemoryMarshal.CreateReadOnlySpan(ref sharedCB, 1));
            cmdList.SetComputeRootConstantBufferView(0, _sharedCBUpload!);

            // 当前源/目标纹理指针
            var src = sceneColor;
            var dst = _rtA!;

            // 1. 自动曝光 (计算场景平均亮度, 更新曝光值)
            if (_settings.EnableAutoExposure)
            {
                RunExposure(cmdList, src, deltaTime);
            }

            // 2. 景深 DoF
            if (_settings.EnableDoF)
            {
                RunDoF(cmdList, src, sceneDepth, dst, invViewProj);
                (src, dst) = (dst, dst == _rtA ? _rtB! : _rtA!);
            }

            // 3. 运动模糊
            if (_settings.EnableMotionBlur)
            {
                RunMotionBlur(cmdList, src, motionVectors, dst, prevViewProj, invViewProj);
                (src, dst) = (dst, dst == _rtA ? _rtB! : _rtA!);
            }

            // 4. Bloom (提取 -> 降采样模糊 -> 合并)
            if (_settings.EnableBloom)
            {
                RunBloom(cmdList, src, dst);
                (src, dst) = (dst, dst == _rtA ? _rtB! : _rtA!);
            }

            // 5. 镜头特效 (光晕 + 色差 + 暗角 + 胶片颗粒)
            if (_settings.EnableChromaticAberration || _settings.EnableVignette ||
                _settings.EnableFilmGrain || _settings.EnableLensFlare)
            {
                RunLensEffects(cmdList, src, dst);
                (src, dst) = (dst, dst == _rtA ? _rtB! : _rtA!);
            }

            // 6. 色调映射 + 颜色分级 + LUT -> 输出 LDR
            RunTonemap(cmdList, src, output);

            // 帧结束: 提交本帧的回读拷贝 (读取上一帧曝光), 推进帧索引
            _readbackManager?.EndFrame();
            _copyEngine?.EndFrame();
        }

        /// <summary>上传 per-pass 常量到 b2 (各 pass 复用同一上传缓冲)。</summary>
        private void UploadPassCB<T>(in T cb) where T : struct
        {
            var tmp = cb;
            _device.UploadToBuffer(_passCBUpload!, MemoryMarshal.CreateReadOnlySpan(ref tmp, 1));
        }

        /// <summary>运行自动曝光 Pass (降采样对数平均亮度 + 适应)。</summary>
        private void RunExposure(RHICommandList cmdList, RHITexture sceneColor, float deltaTime)
        {
            cmdList.SetPipelineState(_exposurePipeline!);
            cmdList.TransitionTo(sceneColor, RHIResourceState.NonPixelShaderResource);

            var cb = new PostProcessExposureCB
            {
                Key = _settings.ExposureKey,
                Compensation = _settings.ExposureCompensation,
                AdaptationSpeed = _settings.ExposureAdaptationSpeed,
                MinExposure = _settings.MinExposure,
                MaxExposure = _settings.MaxExposure,
                ManualExposure = _settings.ManualExposure,
                UseAutoExposure = _settings.EnableAutoExposure ? 1 : 0
            };
            UploadPassCB(cb);
            cmdList.SetComputeRootConstantBufferView(1, _passCBUpload!);
            cmdList.SetComputeRootDescriptorTable(2, _device.CreateSRV(sceneColor));
            cmdList.SetComputeRootDescriptorTable(9, _exposureBufferUAV);
            cmdList.Dispatch(1, 1, 1);
            // 转入 CopySource 供帧末回读拷贝读取 (下一帧 Process 开头转回 UAV)
            cmdList.TransitionTo(_exposureBuffer!, RHIResourceState.CopySource);
            _exposureInCopySource = true;
        }

        /// <summary>运行景深 Pass (散景模糊)。</summary>
        private void RunDoF(RHICommandList cmdList, RHITexture src, RHITexture depth, RHITexture dst, in Matrix4x4 invProj)
        {
            cmdList.SetPipelineState(_dofPipeline!);
            cmdList.TransitionTo(src, RHIResourceState.NonPixelShaderResource);
            cmdList.TransitionTo(depth, RHIResourceState.NonPixelShaderResource);
            cmdList.TransitionTo(dst, RHIResourceState.UnorderedAccess);

            var cb = new PostProcessDoFCB
            {
                ProjMatrix = invProj,
                FocusDistance = _settings.FocusDistance,
                Aperture = _settings.Aperture,
                MaxBokehRadius = _settings.MaxBokehRadius,
                NearFocusStart = _settings.NearFocusStart,
                FarFocusStart = _settings.FarFocusStart,
                BokehShape = (int)_settings.BokehShape
            };
            UploadPassCB(cb);
            cmdList.SetComputeRootConstantBufferView(1, _passCBUpload!);
            cmdList.SetComputeRootDescriptorTable(3, _device.CreateSRV(depth));
            cmdList.SetComputeRootDescriptorTable(5, _device.CreateSRV(src));
            cmdList.SetComputeRootDescriptorTable(9, _device.CreateUAV(dst));
            cmdList.Dispatch((_width + 7) / 8, (_height + 7) / 8, 1);
            cmdList.TransitionTo(dst, RHIResourceState.NonPixelShaderResource);
        }

        /// <summary>运行运动模糊 Pass。</summary>
        private void RunMotionBlur(RHICommandList cmdList, RHITexture src, RHITexture motionVectors,
            RHITexture dst, in Matrix4x4 prevViewProj, in Matrix4x4 invViewProj)
        {
            cmdList.SetPipelineState(_motionBlurPipeline!);
            cmdList.TransitionTo(src, RHIResourceState.NonPixelShaderResource);
            cmdList.TransitionTo(motionVectors, RHIResourceState.NonPixelShaderResource);
            cmdList.TransitionTo(dst, RHIResourceState.UnorderedAccess);

            var cb = new PostProcessMotionBlurCB
            {
                Strength = _settings.MotionBlurStrength,
                MaxVelocity = _settings.MotionBlurStrength,
                SampleCount = _settings.MotionBlurSamples,
                PrevViewProj = prevViewProj,
                InvViewProj = invViewProj
            };
            UploadPassCB(cb);
            cmdList.SetComputeRootConstantBufferView(1, _passCBUpload!);
            cmdList.SetComputeRootDescriptorTable(4, _device.CreateSRV(motionVectors));
            cmdList.SetComputeRootDescriptorTable(5, _device.CreateSRV(src));
            cmdList.SetComputeRootDescriptorTable(9, _device.CreateUAV(dst));
            cmdList.Dispatch((_width + 7) / 8, (_height + 7) / 8, 1);
            cmdList.TransitionTo(dst, RHIResourceState.NonPixelShaderResource);
        }

        /// <summary>运行 Bloom (提取亮区 -> 多级降采样模糊 -> 合并)。</summary>
        private void RunBloom(RHICommandList cmdList, RHITexture src, RHITexture dst)
        {
            var bloomCB = new PostProcessBloomCB
            {
                Threshold = _settings.BloomThreshold,
                Intensity = _settings.BloomIntensity,
                Radius = _settings.BloomRadius,
                Tint = _settings.BloomTint
            };
            UploadPassCB(bloomCB);
            int bw = Math.Max(1, _width / 2);
            int bh = Math.Max(1, _height / 2);

            // Pass 1: 提取亮区到 bloom 金字塔 mip 0
            cmdList.SetPipelineState(_bloomExtractPipeline!);
            cmdList.TransitionTo(src, RHIResourceState.NonPixelShaderResource);
            cmdList.TransitionTo(_bloomPyramid!, RHIResourceState.UnorderedAccess);
            cmdList.SetComputeRootConstantBufferView(1, _passCBUpload!);
            cmdList.SetComputeRootDescriptorTable(5, _device.CreateSRV(src));
            cmdList.SetComputeRootDescriptorTable(11, _bloomPyramidUAV);
            cmdList.Dispatch((bw + 7) / 8, (bh + 7) / 8, 1);

            // Pass 2: 多级高斯模糊 (降采样金字塔; 逐 mip dispatch, mip 寻址由金字塔 UAV 承载)
            cmdList.SetPipelineState(_bloomBlurPipeline!);
            for (int mip = 1; mip < 6; mip++)
            {
                int mw = Math.Max(1, bw >> mip);
                int mh = Math.Max(1, bh >> mip);
                cmdList.Dispatch((mw + 7) / 8, (mh + 7) / 8, 1);
            }
            cmdList.TransitionTo(_bloomPyramid, RHIResourceState.NonPixelShaderResource);

            // Pass 3: 合并回主纹理
            cmdList.SetPipelineState(_bloomCombinePipeline!);
            cmdList.TransitionTo(src, RHIResourceState.NonPixelShaderResource);
            cmdList.TransitionTo(dst, RHIResourceState.UnorderedAccess);
            cmdList.SetComputeRootConstantBufferView(1, _passCBUpload!);
            cmdList.SetComputeRootDescriptorTable(5, _device.CreateSRV(src));
            cmdList.SetComputeRootDescriptorTable(6, _bloomPyramidSRV);
            cmdList.SetComputeRootDescriptorTable(9, _device.CreateUAV(dst));
            cmdList.Dispatch((_width + 7) / 8, (_height + 7) / 8, 1);
            cmdList.TransitionTo(dst, RHIResourceState.NonPixelShaderResource);
        }

        /// <summary>运行镜头特效 (色散/暗角/镜头污渍/胶片颗粒)。</summary>
        private void RunLensEffects(RHICommandList cmdList, RHITexture src, RHITexture dst)
        {
            cmdList.SetPipelineState(_lensPipeline!);
            cmdList.TransitionTo(src, RHIResourceState.NonPixelShaderResource);
            cmdList.TransitionTo(dst, RHIResourceState.UnorderedAccess);

            // LensCS 读取 g_SceneColorSRV (t0) / g_LensDirtSRV (t6) / g_PP_Time (b0)
            cmdList.SetComputeRootDescriptorTable(2, _device.CreateSRV(src));
            cmdList.SetComputeRootDescriptorTable(8, _lensDirtSRV);
            cmdList.SetComputeRootDescriptorTable(9, _device.CreateUAV(dst));
            cmdList.Dispatch((_width + 7) / 8, (_height + 7) / 8, 1);
            cmdList.TransitionTo(dst, RHIResourceState.NonPixelShaderResource);
        }

        /// <summary>运行色调映射 + 颜色分级 + LUT。</summary>
        private void RunTonemap(RHICommandList cmdList, RHITexture src, RHITexture output)
        {
            cmdList.SetPipelineState(_tonemapPipeline!);
            cmdList.TransitionTo(src, RHIResourceState.NonPixelShaderResource);
            cmdList.TransitionTo(output, RHIResourceState.UnorderedAccess);

            var cb = new PostProcessTonemapCB
            {
                Exposure = _settings.EnableAutoExposure ? _currentExposure : _settings.ManualExposure,
                TonemapOperator = (int)_settings.TonemapOperator,
                ApplySRGB = _settings.ApplySRGBCurve ? 1 : 0,
                EnableColorGrading = _settings.EnableColorGrading ? 1 : 0,
                Saturation = _settings.Saturation,
                Contrast = _settings.Contrast,
                Gain = _settings.Gain,
                ColorOffset = _settings.ColorOffset,
                WhiteBalanceTemp = _settings.WhiteBalanceTemp,
                WhiteBalanceTint = _settings.WhiteBalanceTint,
                // 无 LUT 纹理时强度归零, 避免未绑定资源读取影响输出
                LutIntensity = _settings.LutTexture != null ? _settings.LutIntensity : 0f
            };
            UploadPassCB(cb);
            cmdList.SetComputeRootConstantBufferView(1, _passCBUpload!);
            cmdList.SetComputeRootDescriptorTable(5, _device.CreateSRV(src));
            if (_settings.LutTexture != null)
                cmdList.SetComputeRootDescriptorTable(7, _device.CreateSRV(_settings.LutTexture));
            cmdList.SetComputeRootDescriptorTable(9, _device.CreateUAV(output));
            cmdList.Dispatch((_width + 7) / 8, (_height + 7) / 8, 1);
            cmdList.TransitionTo(output, RHIResourceState.PixelShaderResource);
        }

        /// <summary>
        /// ACES 色调映射 (CPU 侧参考实现, 用于验证)。
        /// 将 HDR 线性颜色映射到 [0,1] 显示范围。
        /// </summary>
        public static Vector3 AcesTonemap(Vector3 color)
        {
            // ACES RRT + ODT 简化近似 (Narkowicz 2015)
            float a = 2.51f, b = 0.03f, c = 2.43f, d = 0.59f, e = 0.14f;
            return Vector3.Clamp(
                (color * (a * color + new Vector3(b))) / (color * (c * color + new Vector3(d)) + new Vector3(e)),
                Vector3.Zero, Vector3.One);
        }

        /// <summary>AgX 色调映射 (更中性的高动态范围保留)。</summary>
        public static Vector3 AgXTonemap(Vector3 color)
        {
            // AgX 近似: 对数压缩 + 反斜率
            var l = Vector3.One * 0.18f;
            var logColor = new Vector3(
                MathF.Log2(color.X / l.X + 1),
                MathF.Log2(color.Y / l.Y + 1),
                MathF.Log2(color.Z / l.Z + 1));
            var compressed = Vector3.Clamp(logColor * 0.4f, Vector3.Zero, Vector3.One);
            return compressed;
        }

        /// <summary>Reinhard 色调映射。</summary>
        public static Vector3 ReinhardTonemap(Vector3 color) => color / (color + Vector3.One);

        /// <summary>线性 -> sRGB OETF。</summary>
        public static Vector3 LinearToSRGB(Vector3 color)
        {
            return new Vector3(
                color.X <= 0.0031308f ? 12.92f * color.X : 1.055f * MathF.Pow(color.X, 1.0f / 2.4f) - 0.055f,
                color.Y <= 0.0031308f ? 12.92f * color.Y : 1.055f * MathF.Pow(color.Y, 1.0f / 2.4f) - 0.055f,
                color.Z <= 0.0031308f ? 12.92f * color.Z : 1.055f * MathF.Pow(color.Z, 1.0f / 2.4f) - 0.055f);
        }

        /// <summary>释放资源。</summary>
        public void Dispose()
        {
            _rootSignature?.Dispose();
            _exposurePipeline?.Dispose();
            _dofPipeline?.Dispose();
            _motionBlurPipeline?.Dispose();
            _bloomExtractPipeline?.Dispose();
            _bloomBlurPipeline?.Dispose();
            _bloomCombinePipeline?.Dispose();
            _lensPipeline?.Dispose();
            _tonemapPipeline?.Dispose();
            _rtA?.Dispose();
            _rtB?.Dispose();
            _bloomPyramid?.Dispose();
            _exposureBuffer?.Dispose();
            _exposureReadback?.Dispose();
            _sharedCBUpload?.Dispose();
            _passCBUpload?.Dispose();
            _lensDirt?.Dispose();
            _copyEngine?.Dispose();
            _readbackManager?.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}
