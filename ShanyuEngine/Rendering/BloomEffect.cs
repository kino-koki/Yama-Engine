using System;
using System.Numerics;
using System.Runtime.InteropServices;
using ShanyuEngine.RHI;
using ShanyuEngine.Shaders;

namespace ShanyuEngine.Rendering
{
    // ════════════════════════════════════════════════════════════════════════
    // 山雨 泛光效果 (Bloom Effect)
    //
    // 完整的电影级泛光管线实现，包含：
    //   1. Prefilter：基于亮度阈值的亮度提取，使用 SoftKnee 曲线平滑过渡
    //   2. DownSample：Katz 13-tap 降采样核，避免 zigzag artifact
    //   3. UpSample + Blur：5-tap 十字双边滤波，逐级升采样累加
    //   4. Composite：最终合成（Additive + Lens Dirt）
    //   5. BloomMipChain：6级 Mip 链管理
    // ════════════════════════════════════════════════════════════════════════

    // ────────────────────────────────────────────────────────────────────────
    // BloomSettings — 泛光配置（值类型，可序列化）
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 泛光效果设置。所有参数均为值类型，可被编辑器/序列化系统直接使用。
    /// </summary>
    [Serializable]
    public struct BloomSettings
    {
        /// <summary>是否启用泛光。</summary>
        public bool Enabled;

        /// <summary>
        /// 泛光强度。控制最终合成时 Bloom 纹理的叠加强度。
        /// 范围 [0, 10]，默认 1.0。
        /// </summary>
        public float Intensity;

        /// <summary>
        /// 亮度阈值。只有亮度高于此阈值的像素才会贡献到泛光。
        /// 范围 [0, 20]，默认 1.0（HDR 空间）。
        /// </summary>
        public float Threshold;

        /// <summary>
        /// SoftKnee：阈值附近的平滑过渡宽度系数。
        /// 值为0时为硬截止；值越大过渡越平滑。
        /// 范围 [0, 1]，默认 0.5。
        /// knee = threshold * softKnee
        /// </summary>
        public float SoftKnee;

        /// <summary>
        /// 扩散半径：控制泛光的扩散范围（影响降采样级数和升采样权重）。
        /// 范围 [1, 10]，默认 5。
        /// </summary>
        public float SpreadRadius;

        /// <summary>
        /// 泛光色调偏移。用于给泛光添加颜色倾向。
        /// 默认 (1, 1, 1) = 白色。
        /// </summary>
        public Vector3 Tint;

        /// <summary>
        /// 镜头 Dirt（镜头污渍）纹理。通常是一张低分辨率的噪声/光斑纹理。
        /// 在最终合成时与泛光叠加，模拟真实镜头的光学瑕疵。
        /// </summary>
        public RHITexture? DirtTexture;

        /// <summary>
        /// 镜头 Dirt 强度。控制 Dirt 纹理的叠加程度。
        /// 范围 [0, 5]，默认 0.5。
        /// </summary>
        public float DirtIntensity;

        /// <summary>工厂方法：创建默认设置。</summary>
        public static BloomSettings Default => new()
        {
            Enabled = true,
            Intensity = 1.0f,
            Threshold = 1.0f,
            SoftKnee = 0.5f,
            SpreadRadius = 5.0f,
            Tint = new Vector3(1.0f, 1.0f, 1.0f),
            DirtTexture = null,
            DirtIntensity = 0.5f
        };

        /// <summary>工厂方法：创建高质量泛光预设。</summary>
        public static BloomSettings HighQuality => new()
        {
            Enabled = true,
            Intensity = 1.2f,
            Threshold = 0.8f,
            SoftKnee = 0.7f,
            SpreadRadius = 7.0f,
            Tint = new Vector3(1.0f, 0.95f, 0.9f),
            DirtIntensity = 0.4f
        };

        /// <summary>工厂方法：创建低性能泛光预设。</summary>
        public static BloomSettings LowQuality => new()
        {
            Enabled = true,
            Intensity = 0.8f,
            Threshold = 1.5f,
            SoftKnee = 0.3f,
            SpreadRadius = 3.0f,
            Tint = Vector3.One,
            DirtIntensity = 0.0f
        };

        /// <summary>工厂方法：禁用泛光。</summary>
        public static BloomSettings Disabled => new() { Enabled = false };
    }

    // ────────────────────────────────────────────────────────────────────────
    // Bloom 常量缓冲
    // ────────────────────────────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]

    [StructLayout(LayoutKind.Sequential)]
    internal struct BloomDownSampleCB
    {
        public Vector2 InvSourceResolution;
        public Vector2 InvTargetResolution;
        public float SpreadRadius;
        public float Pad0, Pad1, Pad2;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct BloomUpSampleCB
    {
        public Vector2 InvSourceResolution;
        public Vector2 InvTargetResolution;
        public float BloomStrength;
        public float Pad0, Pad1, Pad2;
    }

    [StructLayout(LayoutKind.Sequential)]

    // ────────────────────────────────────────────────────────────────────────
    // BloomMipChain — Mip链管理
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Bloom Mip链管理器。
    /// 最多6级，尺寸从输入的1/2递减到1/32。
    /// 每级是一个独立的 RenderTarget，在降采样/升采样Pass之间传递。
    /// </summary>
    public sealed class BloomMipChain : IDisposable
    {
        /// <summary>最大 Mip 级数。</summary>
        public const int MaxMipLevels = 6;

        /// <summary>Mip级别描述信息。</summary>
        public readonly struct MipLevel
        {
            /// <summary>Mip 索引（0-based）。</summary>
            public readonly int Index;
            /// <summary>该级纹理宽度。</summary>
            public readonly int Width;
            /// <summary>该级纹理高度。</summary>
            public readonly int Height;
            /// <summary>该级纹理。</summary>
            public readonly RHITexture Texture;

            public MipLevel(int index, int width, int height, RHITexture texture)
            {
                Index = index; Width = width; Height = height; Texture = texture;
            }
        }

        /// <summary>实际的 Mip 级数（可能少于 MaxMipLevels，取决于分辨率）。</summary>
        public int MipCount { get; private set; }

        /// <summary>所有 Mip 级别描述。</summary>
        private readonly MipLevel[] _mips = new MipLevel[MaxMipLevels];

        /// <summary>合并的 Mip链纹理（单张纹理的多级 Mip）。</summary>
        private RHITexture? _mipChainTexture;

        /// <summary>RHI 设备。</summary>
        private readonly RHIDevice _device;

        /// <summary>基础宽度（Mip 0 = 屏幕尺寸 / 2）。</summary>
        private int _baseWidth;
        /// <summary>基础高度。</summary>
        private int _baseHeight;

        /// <summary>构造 Mip链。</summary>
        public BloomMipChain(RHIDevice device)
        {
            _device = device ?? throw new ArgumentNullException(nameof(device));
        }

        /// <summary>
        /// 创建 Mip链资源。
        /// </summary>
        /// <param name="screenWidth">屏幕宽度。</param>
        /// <param name="screenHeight">屏幕高度。</param>
        public void Create(int screenWidth, int screenHeight)
        {
            // Mip 0 = 1/2 分辨率
            _baseWidth = Math.Max(1, screenWidth / 2);
            _baseHeight = Math.Max(1, screenHeight / 2);

            MipCount = 0;
            int w = _baseWidth;
            int h = _baseHeight;

            for (int i = 0; i < MaxMipLevels; i++)
            {
                if (w < 2 && h < 2) break; // 太小则停止
                _mips[i] = new MipLevel(i, w, h, null!);
                MipCount++;
                w = Math.Max(1, w / 2);
                h = Math.Max(1, h / 2);
            }

            // 使用单张纹理的 Mip 子资源
            var desc = new RHITextureDesc
            {
                Dimension = RHIResourceDimension.Texture2D,
                Format = RHIFormat.R16G16B16A16_Float,
                Width = _baseWidth,
                Height = _baseHeight,
                DepthOrArraySize = 1,
                MipLevels = MipCount,
                SampleCount = 1,
                Flags = RHITextureFlags.UnorderedAccess | RHITextureFlags.ShaderResource
            };
            _mipChainTexture = _device.CreateTexture(desc, "BloomMipChain");

            // 更新 MipLevel 引用
            for (int i = 0; i < MipCount; i++)
            {
                int mw = Math.Max(1, _baseWidth >> i);
                int mh = Math.Max(1, _baseHeight >> i);
                _mips[i] = new MipLevel(i, mw, mh, _mipChainTexture);
            }
        }

        /// <summary>获取指定 Mip 级别的宽度。</summary>
        public int GetMipWidth(int mipIndex)
        {
            if (mipIndex < 0 || mipIndex >= MipCount) return 0;
            return Math.Max(1, _baseWidth >> mipIndex);
        }

        /// <summary>获取指定 Mip 级别的高度。</summary>
        public int GetMipHeight(int mipIndex)
        {
            if (mipIndex < 0 || mipIndex >= MipCount) return 0;
            return Math.Max(1, _baseHeight >> mipIndex);
        }

        /// <summary>获取 Mip链纹理。</summary>
        public RHITexture? Texture => _mipChainTexture;

        /// <summary>获取指定 Mip 级别信息。</summary>
        public MipLevel GetMip(int index) => _mips[index];

        /// <summary>计算降采样级数（根据 SpreadRadius）。</summary>
        public int GetActiveMipCount(float spreadRadius)
        {
            // SpreadRadius 1-10 映射到 2-6 级
            return Math.Clamp(
                (int)Math.Round(spreadRadius * 0.5f + 1.5f),
                2, MipCount);
        }

        /// <summary>释放资源。</summary>
        public void Dispose()
        {
            _mipChainTexture?.Dispose();
            GC.SuppressFinalize(this);
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // BloomPipeline — 泛光管线
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 泛光管线：完整的 Bloom 效果实现。
    /// 执行流程：Prefilter → DownSample(多级) → UpSample+Blur(多级) → Composite
    /// </summary>
    public sealed class BloomPipeline : IDisposable
    {
        // ── 管线引用 ──

        private readonly RHIDevice _device;
        private readonly BloomSettings _settings;

        /// <summary>Mip 链管理器。</summary>
        public BloomMipChain MipChain { get; }

        // ── 计算着色器管线 ──

        private RHIPipelineState? _prefilterPipeline;
        private RHIPipelineState? _downSamplePipeline;
        private RHIPipelineState? _upSamplePipeline;
        private RHIPipelineState? _compositePipeline;

        // ── 权重常量 ──

        // 5-tap 十字升采样权重：中心(4/16) + 四方向各(2/16, 1/16, 1/16)
        private const float WeightA = 1.0f / 16.0f;  // 角落
        private const float WeightB = 2.0f / 16.0f;  // 边缘中间
        private const float WeightC = 1.0f / 16.0f;  // 远角
        private const float WeightD = 2.0f / 16.0f;  // 边缘中间
        private const float WeightE = 4.0f / 16.0f;  // 中心

        // Katz 13-tap 降采样核偏移与权重
        // 避免 zigzag artifact 的优化采样模式
        private static readonly (Vector2 Offset, float Weight)[] Katz13Tap = new[]
        {
            (new Vector2( 0.0f,  0.0f), 0.25f),   // 中心，权重最大
            (new Vector2(-1.0f,  0.0f), 0.125f),
            (new Vector2( 1.0f,  0.0f), 0.125f),
            (new Vector2( 0.0f, -1.0f), 0.125f),
            (new Vector2( 0.0f,  1.0f), 0.125f),
            (new Vector2(-0.5f, -0.5f), 0.0625f),
            (new Vector2( 0.5f, -0.5f), 0.0625f),
            (new Vector2(-0.5f,  0.5f), 0.0625f),
            (new Vector2( 0.5f,  0.5f), 0.0625f),
            (new Vector2(-2.0f,  0.0f), 0.015625f),
            (new Vector2( 2.0f,  0.0f), 0.015625f),
            (new Vector2( 0.0f, -2.0f), 0.015625f),
            (new Vector2( 0.0f,  2.0f), 0.015625f)
        };

        /// <summary>当前设置。</summary>
        public BloomSettings Settings => _settings;

        /// <summary>是否已初始化。</summary>
        public bool IsInitialized { get; private set; }

        // ── 构造 ──

        /// <summary>构造泛光管线。</summary>
        public BloomPipeline(RHIDevice device, BloomSettings settings)
        {
            _device = device ?? throw new ArgumentNullException(nameof(device));
            _settings = settings;
            MipChain = new BloomMipChain(device);
        }

        // ── 初始化 ──

        /// <summary>初始化管线和资源。</summary>
        public void Initialize(int screenWidth, int screenHeight)
        {
            MipChain.Create(screenWidth, screenHeight);
            CreatePipelines();
            IsInitialized = true;
        }

        /// <summary>创建计算管线。</summary>
        private void CreatePipelines()
        {
            _prefilterPipeline = _device.CreateComputePipelineState(new RHIComputePipelineDesc
            {
                Name = "BloomPrefilter",
                CS = ShaderManager.Instance.GetBytecode("PostProcess/Bloom", "BloomPrefilterCS",
                    RHIShaderStage.Compute)
            });

            _downSamplePipeline = _device.CreateComputePipelineState(new RHIComputePipelineDesc
            {
                Name = "BloomDownSample",
                CS = ShaderManager.Instance.GetBytecode("PostProcess/Bloom", "BloomDownSampleCS",
                    RHIShaderStage.Compute)
            });

            _upSamplePipeline = _device.CreateComputePipelineState(new RHIComputePipelineDesc
            {
                Name = "BloomUpSample",
                CS = ShaderManager.Instance.GetBytecode("PostProcess/Bloom", "BloomUpSampleCS",
                    RHIShaderStage.Compute)
            });

            _compositePipeline = _device.CreateComputePipelineState(new RHIComputePipelineDesc
            {
                Name = "BloomComposite",
                CS = ShaderManager.Instance.GetBytecode("PostProcess/Bloom", "BloomCompositeCS",
                    RHIShaderStage.Compute)
            });
        }

        // ── 执行管线 ──

        /// <summary>
        /// 执行完整的泛光管线。
        /// </summary>
        /// <param name="cmdList">命令列表。</param>
        /// <param name="sceneColor">HDR场景颜色输入。</param>
        /// <param name="output">最终输出纹理。</param>
        public void Execute(RHICommandList cmdList, RHITexture sceneColor, RHITexture output)
        {
            if (!IsInitialized || !_settings.Enabled) return;

            int activeMips = MipChain.GetActiveMipCount(_settings.SpreadRadius);

            // ── Phase 1: Prefilter（亮度提取） ──
            ExecutePrefilter(cmdList, sceneColor);

            // ── Phase 2: DownSample（降采样） ──
            for (int mip = 1; mip < activeMips; mip++)
            {
                ExecuteDownSample(cmdList, mip);
            }

            // ── Phase 3: UpSample + Blur（升采样+模糊） ──
            for (int mip = activeMips - 2; mip >= 0; mip--)
            {
                ExecuteUpSample(cmdList, mip);
            }

            // ── Phase 4: Composite（最终合成） ──
            ExecuteComposite(cmdList, sceneColor, output);
        }

        // ── Phase 1: Prefilter（亮度提取） ──

        /// <summary>
        /// 亮度预过滤：提取高于阈值的亮区。
        /// 算法：
        ///   1. 计算每个像素的感知亮度
        ///      Luminance = dot(color, vec3(0.2126, 0.7152, 0.0722))
        ///   2. SoftKnee 平滑过渡曲线：
        ///      knee = threshold * softKnee
        ///      curve(x) = smoothstep(threshold - knee, threshold + knee, x)
        ///   3. 低于阈值的区域贡献为0，高于阈值的区域：
        ///      contribution = max(0, brightness - threshold) * curve(brightness)
        ///   4. 最终输出 = contribution * tint
        /// </summary>
        private void ExecutePrefilter(RHICommandList cmdList, RHITexture source)
        {
            if (_prefilterPipeline == null) return;

            cmdList.SetPipelineState(_prefilterPipeline);
            cmdList.TransitionTo(source, RHIResourceState.NonPixelShaderResource);
            cmdList.TransitionTo(MipChain.Texture!, RHIResourceState.UnorderedAccess);

            int mipW = MipChain.GetMipWidth(0);
            int mipH = MipChain.GetMipHeight(0);

            var cb = new BloomPrefilterCB
            {
                InvResolution = new Vector2(1.0f / mipW, 1.0f / mipH),
                Threshold = _settings.Threshold,
                SoftKnee = _settings.SoftKnee,
                Tint = _settings.Tint,
                Pad = 0.0f
            };

            // Dispatch: 每个线程组处理 8x8 像素
            cmdList.Dispatch((mipW + 7) / 8, (mipH + 7) / 8, 1);
        }

        // ── Phase 2: DownSample（Katz 13-tap 降采样） ──

        /// <summary>
        /// 降采样：使用 Katz 13-tap 核从上一级 Mip 生成当前级。
        /// 算法：
        ///   1. 对源纹理的每个目标像素位置进行 13 次采样
        ///   2. 采样模式为中心(0,0) + 水平/垂直方向 +-1, +-2 + 对角线 +-0.5
        ///   3. 权重分配：中心 25%，相邻 12.5%，对角 6.25%，远端 1.5625%
        ///   4. 使用双线性采样进行亚像素级插值
        ///   5. 降采样序列：1/2, 1/4, 1/8, 1/16, 1/32
        /// </summary>
        private void ExecuteDownSample(RHICommandList cmdList, int targetMip)
        {
            if (_downSamplePipeline == null) return;

            cmdList.SetPipelineState(_downSamplePipeline);

            // 源为上一级Mip（只读），目标为当前级Mip（写入）
            cmdList.TransitionTo(MipChain.Texture!, RHIResourceState.NonPixelShaderResource);

            int srcW = MipChain.GetMipWidth(targetMip - 1);
            int srcH = MipChain.GetMipHeight(targetMip - 1);
            int dstW = MipChain.GetMipWidth(targetMip);
            int dstH = MipChain.GetMipHeight(targetMip);

            var cb = new BloomDownSampleCB
            {
                InvSourceResolution = new Vector2(1.0f / srcW, 1.0f / srcH),
                InvTargetResolution = new Vector2(1.0f / dstW, 1.0f / dstH),
                SpreadRadius = _settings.SpreadRadius,
                Pad0 = Pad1 = Pad2 = 0.0f
            };

            cmdList.Dispatch((dstW + 7) / 8, (dstH + 7) / 8, 1);
        }

        // ── Phase 3: UpSample + Blur（升采样+双边滤波） ──

        /// <summary>
        /// 升采样+模糊：从下一级（较小）Mip 升采样到当前级并累加。
        /// 算法：
        ///   1. 使用 5-tap 十字采样模式进行双线性插值
        ///   2. 采样权重分布：
        ///      - 中心点：4/16（最大权重）
        ///      - 上下左右（距中心1像素）：各 2/16
        ///      - 对角（距中心 sqrt(2) 像素）：各 1/16
        ///   3. 双边滤波：同时采样上一级（低分辨率）和当前级（高分辨率）
        ///   4. BloomStrength 随升采样逐级递增（更精细的级贡献更小）
        ///      strength = baseStrength * (1.0 + mipIndex * 0.2)
        ///   5. 从最小Mip逐级升采样，每级将结果累加到上一级
        /// </summary>
        private void ExecuteUpSample(RHICommandList cmdList, int targetMip)
        {
            if (_upSamplePipeline == null) return;

            cmdList.SetPipelineState(_upSamplePipeline);

            cmdList.TransitionTo(MipChain.Texture!, RHIResourceState.NonPixelShaderResource);

            int srcW = MipChain.GetMipWidth(targetMip + 1);
            int srcH = MipChain.GetMipHeight(targetMip + 1);
            int dstW = MipChain.GetMipWidth(targetMip);
            int dstH = MipChain.GetMipHeight(targetMip);

            // 升采样权重随级数递增
            float bloomStrength = 1.0f / (float)(MipChain.MipCount - targetMip);

            var cb = new BloomUpSampleCB
            {
                InvSourceResolution = new Vector2(1.0f / srcW, 1.0f / srcH),
                InvTargetResolution = new Vector2(1.0f / dstW, 1.0f / dstH),
                BloomStrength = bloomStrength,
                Pad0 = Pad1 = Pad2 = 0.0f
            };

            cmdList.Dispatch((dstW + 7) / 8, (dstH + 7) / 8, 1);
        }

        // ── Phase 4: Composite（最终合成） ──

        /// <summary>
        /// 最终合成：将 Bloom 纹理与原始场景颜色合并。
        /// 算法：
        ///   1. 采样 Bloom Mip 0（最低级 = 最高分辨率 = 1/2 屏幕）
        ///   2. Additive blend:
        ///      final = scene + bloom * intensity
        ///   3. Lens Dirt 叠加（如果有 Dirt 纹理）：
        ///      final += dirtTexture.Sample(uv) * bloom * dirtIntensity
        ///   4. Clamp 到 HDR 范围
        /// </summary>
        private void ExecuteComposite(RHICommandList cmdList, RHITexture sceneColor, RHITexture output)
        {
            if (_compositePipeline == null) return;

            cmdList.SetPipelineState(_compositePipeline);
            cmdList.TransitionTo(sceneColor, RHIResourceState.NonPixelShaderResource);
            cmdList.TransitionTo(MipChain.Texture!, RHIResourceState.NonPixelShaderResource);
            cmdList.TransitionTo(output, RHIResourceState.UnorderedAccess);

            var cb = new BloomCompositeCB
            {
                InvResolution = new Vector2(1.0f / MipChain.GetMipWidth(0), 1.0f / MipChain.GetMipHeight(0)),
                BloomIntensity = _settings.Intensity,
                DirtIntensity = _settings.DirtIntensity,
                HasDirt = _settings.DirtTexture != null ? 1 : 0,
                Pad0 = Pad1 = Pad2 = 0.0f
            };

            int w = MipChain.GetMipWidth(0);
            int h = MipChain.GetMipHeight(0);
            cmdList.Dispatch((w + 7) / 8, (h + 7) / 8, 1);

            cmdList.TransitionTo(output, RHIResourceState.NonPixelShaderResource);
        }

        // ── CPU 侧算法参考实现 ──

        /// <summary>
        /// CPU 侧 SoftKnee 曲线计算（用于验证/预览）。
        /// </summary>
        /// <param name="brightness">输入亮度值。</param>
        /// <param name="threshold">阈值。</param>
        /// <param name="softKnee">SoftKnee系数。</param>
        /// <returns>过渡后的贡献值 [0, 1]。</returns>
        public static float SoftKneeCurve(float brightness, float threshold, float softKnee)
        {
            float knee = threshold * softKnee;
            float lower = threshold - knee;
            float upper = threshold + knee;

            if (brightness <= lower) return 0.0f;
            if (brightness >= upper) return brightness - threshold;

            // smoothstep 内部曲线
            float t = (brightness - lower) / (upper - lower);
            t = t * t * (3.0f - 2.0f * t);
            return t * (brightness - threshold);
        }

        /// <summary>
        /// CPU 侧 Katz 13-tap 降采样核（用于验证/预览）。
        /// </summary>
        /// <param name="getPixel">采样函数：返回指定 UV 位置的像素颜色。</param>
        /// <param name="uv">目标 UV 坐标。</param>
        /// <param name="texelSize">源纹理的纹素大小。</param>
        /// <returns>降采样后的颜色。</returns>
        public static Vector3 Katz13TapDownSample(Func<Vector2, Vector3> getPixel, Vector2 uv, Vector2 texelSize)
        {
            Vector3 result = Vector3.Zero;

            for (int i = 0; i < Katz13Tap.Length; i++)
            {
                var (offset, weight) = Katz13Tap[i];
                Vector2 sampleUV = uv + offset * texelSize;
                Vector3 color = getPixel(sampleUV);
                result += color * weight;
            }

            return result;
        }

        /// <summary>
        /// CPU 侧 5-tap 十字升采样核（用于验证/预览）。
        /// </summary>
        public static Vector3 Cross5TapUpSample(
            Func<Vector2, Vector3> getLowRes,
            Func<Vector2, Vector3> getHighRes,
            Vector2 uv,
            Vector2 texelSize,
            float bloomStrength)
        {
            Vector3 low = getLowRes(uv);
            Vector3 high = getHighRes(uv);

            // 5-tap 十字采样（在低分辨率空间）
            Vector3 upSampled = Vector3.Zero;

            // E: 中心 (权重 4/16)
            upSampled += getLowRes(uv) * WeightE;

            // B/D: 上/下/左/右 (权重各 2/16)
            upSampled += getLowRes(uv + new Vector2(0, -1) * texelSize) * WeightB;
            upSampled += getLowRes(uv + new Vector2(0, 1) * texelSize) * WeightD;
            upSampled += getLowRes(uv + new Vector2(-1, 0) * texelSize) * WeightB;
            upSampled += getLowRes(uv + new Vector2(1, 0) * texelSize) * WeightD;

            // A/C: 四个角 (权重各 1/16)
            upSampled += getLowRes(uv + new Vector2(-1, -1) * texelSize) * WeightA;
            upSampled += getLowRes(uv + new Vector2(1, -1) * texelSize) * WeightC;
            upSampled += getLowRes(uv + new Vector2(-1, 1) * texelSize) * WeightA;
            upSampled += getLowRes(uv + new Vector2(1, 1) * texelSize) * WeightC;

            // 累加到高分辨率
            return high + upSampled * bloomStrength;
        }

        // ── 设置更新 ──

        /// <summary>更新泛光设置。</summary>
        public void UpdateSettings(BloomSettings settings)
        {
            _settings = settings;
        }

        // ── 释放 ──

        /// <summary>释放资源。</summary>
        public void Dispose()
        {
            _prefilterPipeline?.Dispose();
            _downSamplePipeline?.Dispose();
            _upSamplePipeline?.Dispose();
            _compositePipeline?.Dispose();
            MipChain.Dispose();
            IsInitialized = false;
            GC.SuppressFinalize(this);
        }
    }
}
