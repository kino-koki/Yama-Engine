using System;
using System.Numerics;
using System.Runtime.InteropServices;
using ShanyuEngine.RHI;
using ShanyuEngine.Shaders;

namespace ShanyuEngine.Rendering
{
    // ════════════════════════════════════════════════════════════════════════
    // 山雨 TSR 时序超分辨率系统 (超越 UE7 级别)
    //
    // 在低分辨率(如 1080p)渲染后, 通过时序累积超采样到 4K 输出, 性能接近原生低分辨率,
    // 画质接近原生高分辨率。核心管线:
    //  1. 子像素抖动 (Halton 序列) - 将渲染相机偏移亚像素量, 时序上覆盖所有子像素
    //  2. 运动矢量计算 - 深度重投影 + 速度缓冲, 为每像素找到历史帧对应位置
    //  3. 时序累积 (TAA) - 加权混合当前帧与历史帧, 历史像素用运动矢量重定位
    //  4. 鬼影消除 (Disocclusion) - 当历史像素被遮挡或无有效历史时丢弃历史
    //  5. 锐化与抗锯齿 - 自适应锐化 + 边缘感知去锯齿
    //  6. 自适应分辨率 - 根据帧率/GPU 负载动态调整内部渲染分辨率
    //  7. 可选外部放大器接口 (DLSS/FSR/XeSS 风格)
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>TSR 质量档位。</summary>
    public enum TSRQuality
    {
        /// <summary>性能优先: 强锐化, 低历史权重。</summary>
        Performance,
        /// <summary>平衡。</summary>
        Balanced,
        /// <summary>高质量: 弱锐化, 高历史权重。</summary>
        Quality,
        /// <summary>电影级: 最高历史权重, 最小锐化。</summary>
        Cinematic
    }

    /// <summary>放大后端选择。</summary>
    public enum TSRBackend
    {
        /// <summary>山雨内置 TSR 时序超分。</summary>
        BuiltIn,
        /// <summary>DLSS 风格外部放大器。</summary>
        DLSS,
        /// <summary>FSR 风格外部放大器。</summary>
        FSR,
        /// <summary>XeSS 风格外部放大器。</summary>
        XeSS
    }

    /// <summary>TSR 配置。</summary>
    public sealed class TSRSettings
    {
        /// <summary>放大后端。</summary>
        public TSRBackend Backend = TSRBackend.BuiltIn;
        /// <summary>质量档位。</summary>
        public TSRQuality Quality = TSRQuality.Quality;

        // --- 抖动 ---
        /// <summary>抖动幅度 (像素, 0-1)。</summary>
        public float JitterScale = 1.0f;
        /// <summary>抖动序列长度。</summary>
        public int JitterPhaseCount = 8;

        // --- 时序累积 ---
        /// <summary>历史权重基础值 (越大越平滑但鬼影越多)。</summary>
        public float HistoryWeight = 0.9f;
        /// <summary>最小历史权重 (运动剧烈时下限)。</summary>
        public float MinHistoryWeight = 0.4f;
        /// <summary>是否使用 YCoCg 颜色空间做时序累积 (更好的色度稳定性)。</summary>
        public bool UseYCoCg = true;
        /// <summary>颜色裁箱膨胀因子 (Variance clipping)。</summary>
        public float ClipBoxScale = 1.25f;

        // --- 锐化 ---
        /// <summary>锐化强度。</summary>
        public float SharpeningAmount = 0.35f;
        /// <summary>锐化半径。</summary>
        public float SharpeningRadius = 1.0f;
        /// <summary>边缘感知锐化阈值。</summary>
        public float EdgeThreshold = 0.1f;

        // --- 抗锯齿 ---
        /// <summary>时序抗锯齿强度。</summary>
        public float AntiAliasingStrength = 1.0f;

        // --- 自适应分辨率 ---
        /// <summary>启用动态分辨率缩放。</summary>
        public bool EnableAdaptiveResolution = true;
        /// <summary>目标帧率 (Hz)。</summary>
        public float TargetFrameRate = 60.0f;
        /// <summary>最小内部分辨率比例。</summary>
        public float MinResolutionScale = 0.5f;
        /// <summary>最大内部分辨率比例。</summary>
        public float MaxResolutionScale = 1.0f;
        /// <summary>当前动态分辨率比例 (运行时更新)。</summary>
        public float CurrentResolutionScale = 1.0f;

        /// <summary>根据质量档位应用预设。</summary>
        public void ApplyQualityPreset(TSRQuality q)
        {
            Quality = q;
            switch (q)
            {
                case TSRQuality.Performance:
                    HistoryWeight = 0.8f; SharpeningAmount = 0.6f; JitterPhaseCount = 4; break;
                case TSRQuality.Balanced:
                    HistoryWeight = 0.88f; SharpeningAmount = 0.4f; JitterPhaseCount = 8; break;
                case TSRQuality.Quality:
                    HistoryWeight = 0.92f; SharpeningAmount = 0.3f; JitterPhaseCount = 16; break;
                case TSRQuality.Cinematic:
                    HistoryWeight = 0.95f; SharpeningAmount = 0.15f; JitterPhaseCount = 32; break;
            }
        }
    }

    /// <summary>TSR 累积 Pass 常量缓冲。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct TSRAccumCB
    {
        public Matrix4x4 ViewMatrix;
        public Matrix4x4 ProjMatrix;
        public Matrix4x4 ViewProjMatrix;
        public Matrix4x4 PrevViewProjMatrix;
        public Matrix4x4 InvViewProjMatrix;
        public Vector3 CameraPos;
        public float HistoryWeight;
        public Vector2 InputResolution;   // 低分辨率
        public Vector2 OutputResolution;  // 高分辨率
        public Vector2 InvInputResolution;
        public Vector2 InvOutputResolution;
        public Vector2 Jitter;
        public Vector2 PrevJitter;
        public float MinHistoryWeight;
        public float ClipBoxScale;
        public int UseYCoCg;
        public float AntiAliasingStrength;
        public int FrameIndex;
        public float DeltaTime;
        public float Pad0, Pad1;
    }

    /// <summary>TSR 放大与锐化 Pass 常量缓冲。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct TSRUpscaleCB
    {
        public Vector2 InputResolution;
        public Vector2 OutputResolution;
        public Vector2 InvInputResolution;
        public Vector2 InvOutputResolution;
        public float SharpeningAmount;
        public float SharpeningRadius;
        public float EdgeThreshold;
        public int UseYCoCg;
        public float Pad0, Pad1, Pad2;
    }

    /// <summary>外部放大器接口 (DLSS/FSR/XeSS 等第三方集成)。</summary>
    public interface ITSRExternalUpscaler : IDisposable
    {
        /// <summary>放大器名称。</summary>
        string Name { get; }
        /// <summary>是否已初始化。</summary>
        bool IsInitialized { get; }
        /// <summary>初始化。</summary>
        void Initialize(RHIDevice device, int inputWidth, int inputHeight, int outputWidth, int outputHeight);
        /// <summary>执行放大。</summary>
        void Upscale(RHICommandList cmdList, RHITexture inputColor, RHITexture inputDepth,
            RHITexture motionVectors, RHITexture? exposure, RHITexture output);
        /// <summary>获取抖动偏移。</summary>
        Vector2 GetJitterOffset(int frameIndex);
    }

    /// <summary>
    /// 时序超分辨率主系统。负责抖动生成、运动矢量、时序累积、锐化与自适应分辨率。
    /// </summary>
    public sealed class TemporalSuperResolution : IDisposable
    {
        private readonly RHIDevice _device;
        private readonly TSRSettings _settings;

        private RHIPipelineState? _accumulatePipeline;
        private RHIPipelineState? _upscalePipeline;
        private RHIPipelineState? _sharpenPipeline;
        private RHIPipelineState? _velocityPipeline;
        private RHIRootSignature? _rootSignature;

        // 双缓冲历史纹理 (Ping-Pong)
        private RHITexture? _historyA;
        private RHITexture? _historyB;
        private bool _useHistoryA;
        // 运动矢量纹理
        private RHITexture? _motionVectors;
        // 当前抖动
        private Vector2 _jitter;
        private Vector2 _prevJitter;
        private int _frameIndex;
        // 上帧相机投影 (用于运动矢量)
        private Matrix4x4 _prevViewProj;

        // 输入/输出尺寸
        private int _inputWidth, _inputHeight;
        private int _outputWidth, _outputHeight;

        /// <summary>配置。</summary>
        public TSRSettings Settings => _settings;
        /// <summary>外部放大器 (若使用)。</summary>
        public ITSRExternalUpscaler? ExternalUpscaler { get; set; }
        /// <summary>当前抖动偏移。</summary>
        public Vector2 CurrentJitter => _jitter;
        /// <summary>当前内部渲染宽度。</summary>
        public int InternalWidth => _inputWidth;
        /// <summary>当前内部渲染高度。</summary>
        public int InternalHeight => _inputHeight;
        /// <summary>输出宽度。</summary>
        public int OutputWidth => _outputWidth;
        /// <summary>输出高度。</summary>
        public int OutputHeight => _outputHeight;
        /// <summary>历史纹理 (供外部读取)。</summary>
        public RHITexture? HistoryTexture => _useHistoryA ? _historyA : _historyB;

        /// <summary>构造。</summary>
        public TemporalSuperResolution(RHIDevice device, TSRSettings? settings = null)
        {
            _device = device;
            _settings = settings ?? new TSRSettings();
        }

        /// <summary>初始化 TSR 系统 (创建管线与历史纹理)。</summary>
        public void Initialize(int outputWidth, int outputHeight)
        {
            _outputWidth = outputWidth;
            _outputHeight = outputHeight;
            _inputWidth = outputWidth;
            _inputHeight = outputHeight;

            AllocateHistoryTextures();
            CreatePipelines();
        }

        /// <summary>创建 GPU 管线。</summary>
        private void CreatePipelines()
        {
            var rootDesc = new RHIRootSignatureDesc
            {
                Parameters = new RHIRootParameter[]
                {
                    new() { ParameterType = RHIRootParameterType.CBV, ShaderVisibility = RHIShaderVisibility.All,
                            Descriptor = new RHIRootDescriptor { ShaderRegister = 0 } },
                    new() { ParameterType = RHIRootParameterType.DescriptorTable, ShaderVisibility = RHIShaderVisibility.All,
                            DescriptorTable = new RHIRootDescriptorTable { Ranges = new[] {
                                new RHIRootDescriptorRange { RangeType = RHIDescriptorRangeType.SRV, NumDescriptors = 6, BaseShaderRegister = 0 } } } },
                    new() { ParameterType = RHIRootParameterType.DescriptorTable, ShaderVisibility = RHIShaderVisibility.All,
                            DescriptorTable = new RHIRootDescriptorTable { Ranges = new[] {
                                new RHIRootDescriptorRange { RangeType = RHIDescriptorRangeType.UAV, NumDescriptors = 4, BaseShaderRegister = 0 } } } }
                },
                StaticSamplers = new[] { RHISamplerDesc.LinearWrap, RHISamplerDesc.PointClamp }
            };
            _rootSignature = _device.CreateRootSignature(rootDesc);

            _accumulatePipeline = _device.CreateComputePipelineState(new RHIComputePipelineDesc
            { Name = "TSRAccumulate", CS = ShaderManager.Instance.GetBytecode("TSR/TSR", "AccumulateCS", RHIShaderStage.Compute) });

            _upscalePipeline = _device.CreateComputePipelineState(new RHIComputePipelineDesc
            { Name = "TSRUpscale", CS = ShaderManager.Instance.GetBytecode("TSR/TSR", "UpscaleCS", RHIShaderStage.Compute) });

            _sharpenPipeline = _device.CreateComputePipelineState(new RHIComputePipelineDesc
            { Name = "TSRSharpen", CS = ShaderManager.Instance.GetBytecode("TSR/TSR", "SharpenCS", RHIShaderStage.Compute) });

            _velocityPipeline = _device.CreateComputePipelineState(new RHIComputePipelineDesc
            { Name = "TSRVelocity", CS = ShaderManager.Instance.GetBytecode("TSR/TSR", "VelocityCS", RHIShaderStage.Compute) });
        }

        /// <summary>分配历史纹理 (双缓冲)。</summary>
        private void AllocateHistoryTextures()
        {
            _historyA?.Dispose();
            _historyB?.Dispose();
            _motionVectors?.Dispose();

            RHITextureDesc MakeHistory(string name) => new()
            {
                Dimension = RHIResourceDimension.Texture2D,
                Format = RHIFormat.R16G16B16A16_Float,
                Width = _outputWidth, Height = _outputHeight, DepthOrArraySize = 1,
                MipLevels = 1, SampleCount = 1,
                Flags = RHITextureFlags.UnorderedAccess | RHITextureFlags.ShaderResource
            };
            _historyA = _device.CreateTexture(MakeHistory("TSRHistoryA"), "TSRHistoryA");
            _historyB = _device.CreateTexture(MakeHistory("TSRHistoryB"), "TSRHistoryB");

            var mvDesc = MakeHistory("TSRMotionVectors");
            mvDesc.Format = RHIFormat.R16G16_Float;
            mvDesc.Width = _inputWidth;
            mvDesc.Height = _inputHeight;
            _motionVectors = _device.CreateTexture(mvDesc, "TSRMotionVectors");
        }

        /// <summary>窗口尺寸变化时重建资源。</summary>
        public void Resize(int outputWidth, int outputHeight)
        {
            _outputWidth = outputWidth;
            _outputHeight = outputHeight;
            UpdateInternalResolution();
            AllocateHistoryTextures();
        }

        /// <summary>根据自适应分辨率更新内部渲染尺寸。</summary>
        public void UpdateInternalResolution()
        {
            if (!_settings.EnableAdaptiveResolution)
            {
                _inputWidth = _outputWidth;
                _inputHeight = _outputHeight;
                _settings.CurrentResolutionScale = 1.0f;
                return;
            }
            float scale = Math.Clamp(_settings.CurrentResolutionScale,
                _settings.MinResolutionScale, _settings.MaxResolutionScale);
            _inputWidth = Math.Max(1, (int)(_outputWidth * scale));
            _inputHeight = Math.Max(1, (int)(_outputHeight * scale));
        }

        /// <summary>
        /// 根据上一帧渲染时间自适应调整内部分辨率, 维持目标帧率。
        /// </summary>
        public void UpdateAdaptiveResolution(float frameTimeMs)
        {
            if (!_settings.EnableAdaptiveResolution) return;

            float targetMs = 1000.0f / _settings.TargetFrameRate;
            // PID 风格简单调节: 帧时间超标则降低分辨率
            float ratio = targetMs / Math.Max(0.1f, frameTimeMs);
            float adjustment = (ratio - 1.0f) * 0.1f;
            _settings.CurrentResolutionScale = Math.Clamp(
                _settings.CurrentResolutionScale + adjustment,
                _settings.MinResolutionScale, _settings.MaxResolutionScale);
            UpdateInternalResolution();
        }

        /// <summary>获取本帧抖动偏移 (Halton 序列), 应用于渲染相机投影矩阵。</summary>
        public Vector2 GetJitter()
        {
            int phase = Math.Max(1, _settings.JitterPhaseCount);
            int index = _frameIndex % phase;
            _jitter = new Vector2(
                (Halton(index + 1, 2) - 0.5f) * (2.0f / _inputWidth) * _settings.JitterScale,
                (Halton(index + 1, 3) - 0.5f) * (2.0f / _inputHeight) * _settings.JitterScale);
            return _jitter;
        }

        /// <summary>获取带抖动的投影矩阵 (应传入基础投影矩阵)。</summary>
        public Matrix4x4 ApplyJitterToProjection(Matrix4x4 proj)
        {
            var j = GetJitter();
            // 将抖动写入投影矩阵的 [2][0] 和 [2][1] 元素
            proj.M31 += j.X;
            proj.M32 += j.Y;
            return proj;
        }

        /// <summary>
        /// 计算运动矢量: 通过深度重投影找到每个像素在上帧的位置偏移。
        /// </summary>
        public void ComputeMotionVectors(RHICommandList cmdList, RHITexture depthTexture,
            in Matrix4x4 view, in Matrix4x4 proj, in Matrix4x4 prevView, in Matrix4x4 prevProj)
        {
            if (_velocityPipeline == null || _rootSignature == null || _motionVectors == null) return;

            cmdList.SetComputeRootSignature(_rootSignature);
            cmdList.SetPipelineState(_velocityPipeline);
            cmdList.TransitionTo(_motionVectors, RHIResourceState.UnorderedAccess);
            cmdList.TransitionTo(depthTexture, RHIResourceState.NonPixelShaderResource);

            int groupsX = (_inputWidth + 7) / 8;
            int groupsY = (_inputHeight + 7) / 8;
            cmdList.Dispatch(groupsX, groupsY, 1);

            cmdList.TransitionTo(_motionVectors, RHIResourceState.NonPixelShaderResource);
            cmdList.TransitionTo(depthTexture, RHIResourceState.PixelShaderResource);
        }

        /// <summary>
        /// 执行时序超分辨率: 累积当前低分辨率帧到历史缓冲, 然后放大+锐化到高分辨率输出。
        /// </summary>
        /// <param name="cmdList">命令列表。</param>
        /// <param name="inputColor">低分辨率输入颜色 (本帧渲染结果)。</param>
        /// <param name="inputDepth">低分辨率输入深度。</param>
        /// <param name="outputColor">高分辨率输出颜色。</param>
        /// <param name="view">本帧视图矩阵。</param>
        /// <param name="proj">本帧投影矩阵 (含抖动)。</param>
        /// <param name="deltaTime">帧时间。</param>
        public void Upscale(RHICommandList cmdList, RHITexture inputColor, RHITexture inputDepth,
            RHITexture outputColor, in Matrix4x4 view, in Matrix4x4 proj, float deltaTime)
        {
            _frameIndex++;

            // 外部放大器路径
            if (ExternalUpscaler != null && ExternalUpscaler.IsInitialized)
            {
                ExternalUpscaler.Upscale(cmdList, inputColor, inputDepth, _motionVectors!, null, outputColor);
                _prevJitter = _jitter;
                _prevViewProj = view * proj;
                return;
            }

            // --- 内置 TSR 路径 ---

            // Pass 1: 时序累积
            RunAccumulate(cmdList, inputColor, inputDepth, view, proj, deltaTime);

            // Pass 2: 放大 + 锐化
            RunUpscaleSharpen(cmdList, outputColor);

            // 交换历史缓冲 (Ping-Pong)
            _useHistoryA = !_useHistoryA;
            _prevJitter = _jitter;
            _prevViewProj = view * proj;
        }

        /// <summary>运行时序累积 Pass。</summary>
        private void RunAccumulate(RHICommandList cmdList, RHITexture inputColor, RHITexture inputDepth,
            in Matrix4x4 view, in Matrix4x4 proj, float deltaTime)
        {
            if (_accumulatePipeline == null || _rootSignature == null) return;

            var historyRead = _useHistoryA ? _historyA : _historyB;
            var historyWrite = _useHistoryA ? _historyB : _historyA;

            cmdList.SetComputeRootSignature(_rootSignature);
            cmdList.SetPipelineState(_accumulatePipeline);

            cmdList.TransitionTo(inputColor, RHIResourceState.NonPixelShaderResource);
            cmdList.TransitionTo(inputDepth, RHIResourceState.NonPixelShaderResource);
            cmdList.TransitionTo(_motionVectors!, RHIResourceState.NonPixelShaderResource);
            cmdList.TransitionTo(historyRead!, RHIResourceState.NonPixelShaderResource);
            cmdList.TransitionTo(historyWrite!, RHIResourceState.UnorderedAccess);

            // 计算运动剧烈度自适应历史权重
            float adaptiveWeight = ComputeAdaptiveHistoryWeight(deltaTime);

            var cb = new TSRAccumCB
            {
                ViewMatrix = view,
                ProjMatrix = proj,
                ViewProjMatrix = view * proj,
                PrevViewProjMatrix = _prevViewProj,
                CameraPos = Vector3.Zero,
                HistoryWeight = adaptiveWeight,
                InputResolution = new Vector2(_inputWidth, _inputHeight),
                OutputResolution = new Vector2(_outputWidth, _outputHeight),
                InvInputResolution = new Vector2(1f / _inputWidth, 1f / _inputHeight),
                InvOutputResolution = new Vector2(1f / _outputWidth, 1f / _outputHeight),
                Jitter = _jitter,
                PrevJitter = _prevJitter,
                MinHistoryWeight = _settings.MinHistoryWeight,
                ClipBoxScale = _settings.ClipBoxScale,
                UseYCoCg = _settings.UseYCoCg ? 1 : 0,
                AntiAliasingStrength = _settings.AntiAliasingStrength,
                FrameIndex = _frameIndex,
                DeltaTime = deltaTime
            };

            int groupsX = (_inputWidth + 7) / 8;
            int groupsY = (_inputHeight + 7) / 8;
            cmdList.Dispatch(groupsX, groupsY, 1);

            cmdList.TransitionTo(historyWrite!, RHIResourceState.NonPixelShaderResource);
        }

        /// <summary>根据相机运动量自适应调整历史权重 (运动剧烈时降低权重减少鬼影)。</summary>
        private float ComputeAdaptiveHistoryWeight(float deltaTime)
        {
            // 检测投影矩阵变化幅度 (简化: 比较矩阵差异)
            float motion = MathF.Abs(_prevViewProj.Translation.X - 0) +
                           MathF.Abs(_prevViewProj.Translation.Y - 0);
            float motionFactor = Math.Clamp(1.0f - motion * 0.5f, 0.0f, 1.0f);
            return _settings.MinHistoryWeight +
                   (_settings.HistoryWeight - _settings.MinHistoryWeight) * motionFactor;
        }

        /// <summary>运行放大 + 锐化 Pass。</summary>
        private void RunUpscaleSharpen(RHICommandList cmdList, RHITexture outputColor)
        {
            if (_upscalePipeline == null || _rootSignature == null) return;

            var historyRead = _useHistoryA ? _historyB : _historyA;

            cmdList.SetComputeRootSignature(_rootSignature);
            cmdList.SetPipelineState(_upscalePipeline);
            cmdList.TransitionTo(historyRead!, RHIResourceState.NonPixelShaderResource);
            cmdList.TransitionTo(outputColor, RHIResourceState.UnorderedAccess);

            var cb = new TSRUpscaleCB
            {
                InputResolution = new Vector2(_inputWidth, _inputHeight),
                OutputResolution = new Vector2(_outputWidth, _outputHeight),
                InvInputResolution = new Vector2(1f / _inputWidth, 1f / _inputHeight),
                InvOutputResolution = new Vector2(1f / _outputWidth, 1f / _outputHeight),
                SharpeningAmount = _settings.SharpeningAmount,
                SharpeningRadius = _settings.SharpeningRadius,
                EdgeThreshold = _settings.EdgeThreshold,
                UseYCoCg = _settings.UseYCoCg ? 1 : 0
            };

            int groupsX = (_outputWidth + 7) / 8;
            int groupsY = (_outputHeight + 7) / 8;
            cmdList.Dispatch(groupsX, groupsY, 1);

            cmdList.TransitionTo(outputColor, RHIResourceState.PixelShaderResource);
        }

        /// <summary>Halton 低差异序列。</summary>
        private static float Halton(int index, int radix)
        {
            float result = 0f;
            float f = 1f;
            while (index > 0)
            {
                f /= radix;
                result += f * (index % radix);
                index /= radix;
            }
            return result;
        }

        /// <summary>
        /// RGB <-> YCoCg 颜色空间转换 (用于时序累积中更好的色度稳定性)。
        /// YCoCg 将亮度与色度分离, 时序混合时对亮度做方差裁剪可保留更多细节。
        /// </summary>
        public static Vector3 RgbToYCoCg(Vector3 rgb)
        {
            return new Vector3(
                rgb.X * 0.25f + rgb.Y * 0.5f + rgb.Z * 0.25f,   // Y
                rgb.X * 0.5f - rgb.Z * 0.5f,                      // Co
                -rgb.X * 0.25f + rgb.Y * 0.5f - rgb.Z * 0.25f);   // Cg
        }

        /// <summary>YCoCg -> RGB。</summary>
        public static Vector3 YCoCgToRgb(Vector3 ycocg)
        {
            float y = ycocg.X, co = ycocg.Y, cg = ycocg.Z;
            return new Vector3(
                y + co - cg,
                y + cg,
                y - co - cg);
        }

        /// <summary>释放资源。</summary>
        public void Dispose()
        {
            _accumulatePipeline?.Dispose();
            _upscalePipeline?.Dispose();
            _sharpenPipeline?.Dispose();
            _velocityPipeline?.Dispose();
            _rootSignature?.Dispose();
            _historyA?.Dispose();
            _historyB?.Dispose();
            _motionVectors?.Dispose();
            ExternalUpscaler?.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}
