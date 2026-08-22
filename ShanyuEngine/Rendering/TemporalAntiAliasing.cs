using System;
using System.Numerics;
using System.Runtime.InteropServices;
using ShanyuEngine.RHI;
using ShanyuEngine.Shaders;

namespace ShanyuEngine.Rendering
{
    // ════════════════════════════════════════════════════════════════════════
    // 山雨 Temporal Anti-Aliasing (TAA) (超越 UE7 级别)
    //
    // 高质量时序抗锯齿系统:
    //   1. 历史帧混合: 通过运动矢量将历史颜色重投影到当前帧,
    //      自适应混合系数根据历史可信度调整。
    //   2. 运动矢量: 基于深度缓冲的重投影, 或使用 G-Buffer 速度通道。
    //   3. 邻域裁剪 (Neighborhood Clipping): 在当前像素 3x3 邻域内
    //      计算颜色 AABB/方差, 将历史颜色裁剪到该范围内, 消除鬼影。
    //   4. YCoCg 颜色空间: 在更感知均匀的颜色空间执行裁剪与混合。
    //   5. 锐化: 可选的对比度自适应锐化 (CAS) 补偿时序模糊。
    //   6. 子像素抖动: Halton 序列 jitter 提供超采样分布。
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>TAA 配置。</summary>
    public sealed class TAASettings
    {
        /// <summary>历史帧混合权重 (0~1, 越大越平滑)。</summary>
        public float HistoryWeight = 0.92f;
        /// <summary>最小混合权重 (运动时保留下限)。</summary>
        public float MinHistoryWeight = 0.35f;
        /// <summary>是否使用 YCoCg 空间裁剪。</summary>
        public bool UseYCoCg = true;
        /// <summary>邻域裁剪模式: 0=None, 1=Clamp, 2=VarianceClip。</summary>
        public int ClipMode = 2;
        /// <summary>方差裁剪膨胀系数 (越大允许历史偏离越多)。</summary>
        public float ClipBoxScale = 1.25f;
        /// <summary>运动适应速度 (历史权重对运动速度的反应)。</summary>
        public float MotionAdaptationSpeed = 2.0f;
        /// <summary>启用对比度自适应锐化。</summary>
        public bool EnableSharpening = true;
        /// <summary>锐化强度。</summary>
        public float SharpenIntensity = 0.25f;
        /// <summary>启用静态像素加强 (对静止场景增加历史权重)。</summary>
        public bool EnableStaticBoost = true;
        /// <summary>静态像素历史权重加成。</summary>
        public float StaticBoost = 0.05f;
        /// <summary>运动矢量可信度阈值 (像素)。</summary>
        public float VelocityRejectionThreshold = 16.0f;
        /// <summary>低差异序列相位数。</summary>
        public int JitterPhaseCount = 16;
        /// <summary>抖动幅度 (子像素, 0.5=半像素)。</summary>
        public float JitterScale = 0.5f;
    }

    /// <summary>TAA 常量缓冲。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct TAACB
    {
        public Matrix4x4 ViewProj;
        public Matrix4x4 PrevViewProj;
        public Matrix4x4 InvViewProj;
        public Vector2 JitterOffset;
        public Vector2 PrevJitterOffset;
        public Vector2 Resolution;
        public Vector2 InvResolution;
        public float HistoryWeight;
        public float MinHistoryWeight;
        public float ClipBoxScale;
        public int UseYCoCg;
        public int ClipMode;
        public float MotionAdaptationSpeed;
        public float VelocityRejectionThreshold;
        public int EnableSharpening;
        public float SharpenIntensity;
        public int EnableStaticBoost;
        public float StaticBoost;
        public int FrameIndex;
        public float Pad0, Pad1;
    }

    /// <summary>
    /// Temporal Anti-Aliasing 主系统。
    /// 接收当前帧颜色 + 运动矢量, 输出抗锯齿后的颜色。
    /// </summary>
    public sealed class TemporalAntiAliasing : IDisposable
    {
        private readonly RHIDevice _device;
        private readonly TAASettings _settings;

        // GPU 管线
        private RHIRootSignature? _rootSignature;
        private RHIPipelineState? _taaResolvePipeline;
        private RHIPipelineState? _sharpenPipeline;

        // 双缓冲历史颜色 (Ping-Pong)
        private RHITexture? _historyA;
        private RHITexture? _historyB;
        private bool _useHistoryA;

        // 当前帧输出 (已解析的 TAA 结果)
        private RHITexture? _resolvedOutput;

        // 内部状态
        private Vector2 _jitter;
        private Vector2 _prevJitter;
        private Matrix4x4 _prevViewProj;
        private int _frameIndex;
        private int _width, _height;

        public TAASettings Settings => _settings;
        public Vector2 CurrentJitter => _jitter;
        public int Width => _width;
        public int Height => _height;

        public TemporalAntiAliasing(RHIDevice device, TAASettings? settings = null)
        {
            _device = device;
            _settings = settings ?? new TAASettings();
        }

        /// <summary>初始化 TAA 资源与管线。</summary>
        public void Initialize(int width, int height)
        {
            _width = width;
            _height = height;
            CreatePipelines();
            AllocateHistoryTextures();
        }

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
                                new RHIRootDescriptorRange { RangeType = RHIDescriptorRangeType.SRV, NumDescriptors = 8, BaseShaderRegister = 0 } } } },
                    new() { ParameterType = RHIRootParameterType.DescriptorTable, ShaderVisibility = RHIShaderVisibility.All,
                            DescriptorTable = new RHIRootDescriptorTable { Ranges = new[] {
                                new RHIRootDescriptorRange { RangeType = RHIDescriptorRangeType.UAV, NumDescriptors = 4, BaseShaderRegister = 0 } } } }
                },
                StaticSamplers = new[] { RHISamplerDesc.LinearClamp, RHISamplerDesc.PointClamp }
            };
            _rootSignature = _device.CreateRootSignature(rootDesc);

            _taaResolvePipeline = _device.CreateComputePipelineState(new RHIComputePipelineDesc
            { Name = "TAAResolve", CS = ShaderManager.Instance.GetBytecode("TAA/TAA", "TAAResolveCS", RHIShaderStage.Compute) });

            _sharpenPipeline = _device.CreateComputePipelineState(new RHIComputePipelineDesc
            { Name = "TAASharpen", CS = ShaderManager.Instance.GetBytecode("TAA/TAA", "TAASharpenCS", RHIShaderStage.Compute) });
        }

        private void AllocateHistoryTextures()
        {
            _historyA?.Dispose();
            _historyB?.Dispose();
            _resolvedOutput?.Dispose();

            RHITextureDesc MakeTex(RHIFormat fmt, string name) => new()
            {
                Dimension = RHIResourceDimension.Texture2D,
                Format = fmt,
                Width = _width,
                Height = _height,
                DepthOrArraySize = 1,
                MipLevels = 1,
                SampleCount = 1,
                Flags = RHITextureFlags.UnorderedAccess | RHITextureFlags.ShaderResource
            };

            _historyA = _device.CreateTexture(MakeTex(RHIFormat.R16G16B16A16_Float, "TAAHistoryA"), "TAAHistoryA");
            _historyB = _device.CreateTexture(MakeTex(RHIFormat.R16G16B16A16_Float, "TAAHistoryB"), "TAAHistoryB");
            _resolvedOutput = _device.CreateTexture(MakeTex(RHIFormat.R16G16B16A16_Float, "TAAResolved"), "TAAResolved");
        }

        /// <summary>窗口尺寸变化。</summary>
        public void Resize(int width, int height)
        {
            _width = width;
            _height = height;
            AllocateHistoryTextures();
        }

        /// <summary>获取本帧 Halton 抖动偏移 (用于投影矩阵)。</summary>
        public Vector2 GetJitter()
        {
            int phase = Math.Max(1, _settings.JitterPhaseCount);
            int index = _frameIndex % phase;
            float jx = (Halton(index + 1, 2) - 0.5f) * _settings.JitterScale;
            float jy = (Halton(index + 1, 3) - 0.5f) * _settings.JitterScale;
            _jitter = new Vector2(jx, jy);
            return _jitter;
        }

        /// <summary>应用抖动到投影矩阵。</summary>
        public Matrix4x4 ApplyJitterToProjection(Matrix4x4 proj)
        {
            var j = GetJitter();
            proj.M31 += j.X * 2.0f / _width;
            proj.M32 += j.Y * 2.0f / _height;
            return proj;
        }

        /// <summary>
        /// 执行 TAA 解析: 混合当前帧与重投影后的历史帧。
        /// </summary>
        /// <param name="cmdList">命令列表。</param>
        /// <param name="currentColor">当前帧颜色 (HDR)。</param>
        /// <param name="motionVectors">运动矢量纹理 (RG16F, 像素空间)。</param>
        /// <param name="depth">深度缓冲 (用于重投影验证)。</param>
        /// <param name="output">TAA 输出 (可与 currentColor 相同, 用于 in-place)。</param>
        /// <param name="viewProj">当前帧 ViewProj。</param>
        /// <param name="deltaTime">帧时间 (秒)。</param>
        public void Resolve(RHICommandList cmdList, RHITexture currentColor, RHITexture motionVectors,
            RHITexture depth, RHITexture output, in Matrix4x4 viewProj, float deltaTime)
        {
            if (_taaResolvePipeline == null || _rootSignature == null) return;

            _frameIndex++;

            var historyRead = _useHistoryA ? _historyA : _historyB;
            var historyWrite = _useHistoryA ? _historyB : _historyA;

            // 计算自适应历史权重
            float adaptiveWeight = ComputeAdaptiveWeight(deltaTime, viewProj);

            var cb = new TAACB
            {
                ViewProj = viewProj,
                PrevViewProj = _prevViewProj,
                InvViewProj = Matrix4x4.Identity,
                JitterOffset = _jitter,
                PrevJitterOffset = _prevJitter,
                Resolution = new Vector2(_width, _height),
                InvResolution = new Vector2(1.0f / _width, 1.0f / _height),
                HistoryWeight = adaptiveWeight,
                MinHistoryWeight = _settings.MinHistoryWeight,
                ClipBoxScale = _settings.ClipBoxScale,
                UseYCoCg = _settings.UseYCoCg ? 1 : 0,
                ClipMode = _settings.ClipMode,
                MotionAdaptationSpeed = _settings.MotionAdaptationSpeed,
                VelocityRejectionThreshold = _settings.VelocityRejectionThreshold,
                EnableSharpening = _settings.EnableSharpening ? 1 : 0,
                SharpenIntensity = _settings.SharpenIntensity,
                EnableStaticBoost = _settings.EnableStaticBoost ? 1 : 0,
                StaticBoost = _settings.StaticBoost,
                FrameIndex = _frameIndex
            };

            cmdList.SetComputeRootSignature(_rootSignature);
            cmdList.SetPipelineState(_taaResolvePipeline);

            cmdList.TransitionTo(currentColor, RHIResourceState.NonPixelShaderResource);
            cmdList.TransitionTo(motionVectors, RHIResourceState.NonPixelShaderResource);
            cmdList.TransitionTo(depth, RHIResourceState.NonPixelShaderResource);
            cmdList.TransitionTo(historyRead!, RHIResourceState.NonPixelShaderResource);
            cmdList.TransitionTo(historyWrite!, RHIResourceState.UnorderedAccess);

            cmdList.Dispatch((_width + 7) / 8, (_height + 7) / 8, 1);

            cmdList.TransitionTo(historyWrite!, RHIResourceState.NonPixelShaderResource);

            // 可选: 锐化 Pass
            if (_settings.EnableSharpening && _sharpenPipeline != null)
            {
                cmdList.SetPipelineState(_sharpenPipeline);
                cmdList.TransitionTo(historyWrite!, RHIResourceState.NonPixelShaderResource);
                cmdList.TransitionTo(output, RHIResourceState.UnorderedAccess);
                cmdList.Dispatch((_width + 7) / 8, (_height + 7) / 8, 1);
                cmdList.TransitionTo(output, RHIResourceState.PixelShaderResource);
            }
            else
            {
                // 复制到输出
                cmdList.CopyResource(output, historyWrite!);
                cmdList.TransitionTo(output, RHIResourceState.PixelShaderResource);
            }

            // 交换历史缓冲
            _useHistoryA = !_useHistoryA;
            _prevJitter = _jitter;
            _prevViewProj = viewProj;
        }

        /// <summary>计算自适应历史权重: 运动剧烈时降低权重, 静止时提高。</summary>
        private float ComputeAdaptiveWeight(float deltaTime, Matrix4x4 currentViewProj)
        {
            float motion = 0f;
            // 简化: 比较当前与上一帧 ViewProj 的平移差异
            motion += MathF.Abs(currentViewProj.M41 - _prevViewProj.M41);
            motion += MathF.Abs(currentViewProj.M42 - _prevViewProj.M42);
            motion += MathF.Abs(currentViewProj.M43 - _prevViewProj.M43);

            float motionFactor = Math.Clamp(1.0f - motion * _settings.MotionAdaptationSpeed, 0.0f, 1.0f);
            float weight = _settings.MinHistoryWeight +
                           (_settings.HistoryWeight - _settings.MinHistoryWeight) * motionFactor;

            if (_settings.EnableStaticBoost && motion < 0.001f)
                weight = Math.Min(0.98f, weight + _settings.StaticBoost);

            return weight;
        }

        /// <summary>Halton 低差异序列。</summary>
        private static float Halton(int index, int baseNum)
        {
            float result = 0f;
            float f = 1f;
            while (index > 0)
            {
                f /= baseNum;
                result += f * (index % baseNum);
                index /= baseNum;
            }
            return result;
        }

        /// <summary>RGB -> YCoCg 颜色空间转换。</summary>
        public static Vector3 RgbToYCoCg(Vector3 rgb)
        {
            return new Vector3(
                rgb.X * 0.25f + rgb.Y * 0.5f + rgb.Z * 0.25f,
                rgb.X * 0.5f - rgb.Z * 0.5f,
                -rgb.X * 0.25f + rgb.Y * 0.5f - rgb.Z * 0.25f);
        }

        /// <summary>YCoCg -> RGB。</summary>
        public static Vector3 YCoCgToRgb(Vector3 ycocg)
        {
            float y = ycocg.X, co = ycocg.Y, cg = ycocg.Z;
            return new Vector3(y + co - cg, y + cg, y - co - cg);
        }

        /// <summary>重置历史缓冲 (场景切换或剧烈变化时调用)。</summary>
        public void ResetHistory(RHICommandList cmdList)
        {
            if (_historyA != null)
                cmdList.TransitionTo(_historyA, RHIResourceState.UnorderedAccess);
            if (_historyB != null)
                cmdList.TransitionTo(_historyB, RHIResourceState.UnorderedAccess);
            _prevViewProj = Matrix4x4.Identity;
            _prevJitter = Vector2.Zero;
        }

        /// <summary>释放资源。</summary>
        public void Dispose()
        {
            _taaResolvePipeline?.Dispose();
            _sharpenPipeline?.Dispose();
            _rootSignature?.Dispose();
            _historyA?.Dispose();
            _historyB?.Dispose();
            _resolvedOutput?.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}
