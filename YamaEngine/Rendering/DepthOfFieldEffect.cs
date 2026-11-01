using System;
using System.Numerics;
using System.Runtime.InteropServices;
using YamaEngine.RHI;
using YamaEngine.Shaders;

namespace YamaEngine.Rendering
{
    // ════════════════════════════════════════════════════════════════════════
    // yama 景深效果 (Depth of Field Effect)
    //
    // 模拟真实镜头散焦的后处理效果，包含：
    //   1. CoC (Circle of Confusion) 计算：近处/远处分别计算散焦圆半径
    //   2. 近场模糊：前景物体（depth < focusDistance）的可变半径模糊
    //   3. 远场模糊：背景物体的可变半径模糊
    //   4. 前景散焦：Depth Peel + Alpha 遮罩的前景物体覆盖
    //   5. BokehKernel：三种光圈形状（圆形/六边形/叶片）
    // ════════════════════════════════════════════════════════════════════════

    // ────────────────────────────────────────────────────────────────────────
    // BokehShape — 散景光圈形状
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>散景光圈形状类型。</summary>
    public enum BokehShape
    {
        /// <summary>标准圆形散景（高斯核）。</summary>
        Circular = 0,
        /// <summary>六边形散景（6方向采样模式）。</summary>
        Hexagonal = 1,
        /// <summary>叶片散景（多边形近似，模拟光圈叶片）。</summary>
        Blade = 2,
        /// <summary>八边形散景。</summary>
        Octagonal = 3,
        /// <summary>折返（catadioptric）散景。</summary>
        Catadioptric = 4
    }

    // ────────────────────────────────────────────────────────────────────────
    // DepthOfFieldSettings — 景深配置
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 景深效果设置（值类型，可序列化）。
    /// </summary>
    [Serializable]
    public struct DepthOfFieldSettings
    {
        /// <summary>是否启用景深。</summary>
        public bool Enabled;

        /// <summary>
        /// 对焦距离（世界空间单位）。
        /// 距离相机此距离的物体完全清晰。
        /// 范围 [0.1, 1000]，默认 10.0。
        /// </summary>
        public float FocusDistance;

        /// <summary>
        /// 近场焦点范围：从对焦距离向近处延伸的距离。
        /// 在此范围内物体逐渐从清晰变为模糊。
        /// 范围 [0, 50]，默认 2.0。
        /// </summary>
        public float NearFocusRange;

        /// <summary>
        /// 远场焦点范围：从对焦距离向远处延伸的距离。
        /// 在此范围内物体逐渐从清晰变为模糊。
        /// 范围 [0, 200]，默认 20.0。
        /// </summary>
        public float FarFocusRange;

        /// <summary>
        /// 近场最大模糊尺寸（像素）。
        /// 近处物体的模糊半径上限。
        /// 范围 [0, 50]，默认 8.0。
        /// </summary>
        public float NearBlurSize;

        /// <summary>
        /// 远场最大模糊尺寸（像素）。
        /// 远处物体的模糊半径上限。
        /// 范围 [0, 50]，默认 12.0。
        /// </summary>
        public float FarBlurSize;

        /// <summary>散景光圈形状。</summary>
        public BokehShape BokehShape;

        /// <summary>光圈叶片数（仅 Blade 模式使用）。</summary>
        public int BladeCount;

        /// <summary>工厂方法：创建默认设置。</summary>
        public static DepthOfFieldSettings Default => new()
        {
            Enabled = true,
            FocusDistance = 10.0f,
            NearFocusRange = 2.0f,
            FarFocusRange = 20.0f,
            NearBlurSize = 8.0f,
            FarBlurSize = 12.0f,
            BokehShape = BokehShape.Circular,
            BladeCount = 7
        };

        /// <summary>工厂方法：浅景深预设（人像模式）。</summary>
        public static DepthOfFieldSettings Portrait => new()
        {
            Enabled = true,
            FocusDistance = 5.0f,
            NearFocusRange = 0.5f,
            FarFocusRange = 3.0f,
            NearBlurSize = 15.0f,
            FarBlurSize = 25.0f,
            BokehShape = BokehShape.Hexagonal,
            BladeCount = 6
        };

        /// <summary>工厂方法：远景预设。</summary>
        public static DepthOfFieldSettings Landscape => new()
        {
            Enabled = true,
            FocusDistance = 100.0f,
            NearFocusRange = 5.0f,
            FarFocusRange = 200.0f,
            NearBlurSize = 2.0f,
            FarBlurSize = 5.0f,
            BokehShape = BokehShape.Circular,
            BladeCount = 7
        };

        /// <summary>工厂方法：禁用景深。</summary>
        public static DepthOfFieldSettings Disabled => new() { Enabled = false };
    }

    // ────────────────────────────────────────────────────────────────────────
    // DOF 常量缓冲
    // ────────────────────────────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    internal struct DOFCoCCB
    {
        public Matrix4x4 InvProjMatrix;
        public float FocusDistance;
        public float NearFocusRange;
        public float FarFocusRange;
        public float FocalLength;
        public float NearBlurSize;
        public float FarBlurSize;
        public float Pad0, Pad1;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DOFBlurCB
    {
        public Vector2 InvResolution;
        public float MaxBlurRadius;
        public int BokehShape;
        public float Pad0, Pad1, Pad2;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DOFCompositeCB
    {
        public Vector2 InvResolution;
        public float Pad0, Pad1;
    }

    // ────────────────────────────────────────────────────────────────────────
    // BokehKernel — 散焦光圈形状采样核
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 散焦光圈采样核生成器。
    /// 根据光圈形状生成采样偏移和权重，用于 Compute Shader 的 Gather 采样。
    /// </summary>
    public static class BokehKernel
    {
        /// <summary>
        /// 生成指定形状的采样点集合。
        /// </summary>
        /// <param name="shape">光圈形状。</param>
        /// <param name="bladeCount">叶片数（仅 Blade 模式）。</param>
        /// <param name="maxRadius">最大采样半径。</param>
        /// <param name="sampleCount">采样点数量。</param>
        /// <returns>采样点列表（偏移 + 权重）。</returns>
        public static (Vector2 Offset, float Weight)[] GenerateSamples(
            BokehShape shape, int bladeCount, float maxRadius, int sampleCount)
        {
            return shape switch
            {
                BokehShape.Circular => GenerateCircularSamples(maxRadius, sampleCount),
                BokehShape.Hexagonal => GenerateHexagonalSamples(maxRadius, sampleCount),
                BokehShape.Blade => GenerateBladeSamples(bladeCount, maxRadius, sampleCount),
                _ => GenerateCircularSamples(maxRadius, sampleCount)
            };
        }

        /// <summary>
        /// 圆形散景采样：标准高斯分布。
        /// 采样点沿同心圆分布，权重随半径衰减。
        /// </summary>
        public static (Vector2 Offset, float Weight)[] GenerateCircularSamples(float maxRadius, int sampleCount)
        {
            var samples = new (Vector2 Offset, float Weight)[sampleCount];
            float goldenAngle = 2.39996323f; // 黄金角度 (弧度)

            for (int i = 0; i < sampleCount; i++)
            {
                // 黄金螺旋分布
                float r = maxRadius * MathF.Sqrt((float)i / sampleCount);
                float theta = i * goldenAngle;

                float x = r * MathF.Cos(theta);
                float y = r * MathF.Sin(theta);

                // 高斯权重衰减
                float weight = MathF.Exp(-0.5f * (r * r) / (maxRadius * maxRadius * 0.25f));

                samples[i] = (new Vector2(x, y), weight);
            }

            // 归一化权重
            float totalWeight = 0;
            for (int i = 0; i < samples.Length; i++) totalWeight += samples[i].Weight;
            for (int i = 0; i < samples.Length; i++)
                samples[i] = (samples[i].Offset, samples[i].Weight / totalWeight);

            return samples;
        }

        /// <summary>
        /// 六边形散景采样：6个方向 + 中心。
        /// 在六边形的6个顶点方向上均匀分布采样点，
        /// 产生六角形的散景光斑。
        /// </summary>
        public static (Vector2 Offset, float Weight)[] GenerateHexagonalSamples(float maxRadius, int sampleCount)
        {
            var samples = new (Vector2 Offset, float Weight)[sampleCount];

            // 六边形的6个方向角（60度间隔）
            float[] hexAngles = new float[6];
            for (int h = 0; h < 6; h++)
                hexAngles[h] = h * MathF.PI / 3.0f;

            int samplesPerRing = Math.Max(1, sampleCount / 4); // 多环分布
            int centerSamples = Math.Max(1, sampleCount - samplesPerRing * 3);
            int idx = 0;

            // 中心点
            for (int i = 0; i < centerSamples && idx < sampleCount; i++, idx++)
            {
                samples[idx] = (Vector2.Zero, 1.0f);
            }

            // 内环
            for (int r = 1; r <= 3 && idx < sampleCount; r++)
            {
                float radius = maxRadius * r / 3.0f;
                for (int h = 0; h < 6 && idx < sampleCount; h++)
                {
                    float angle = hexAngles[h];
                    float x = radius * MathF.Cos(angle);
                    float y = radius * MathF.Sin(angle);
                    float weight = 1.0f / (r + 1);
                    samples[idx++] = (new Vector2(x, y), weight);
                }
            }

            // 中间填充
            for (int r = 1; r <= 3 && idx < sampleCount; r++)
            {
                float radius = maxRadius * r / 4.0f;
                for (int h = 0; h < 12 && idx < sampleCount; h++)
                {
                    float angle = h * MathF.PI / 6.0f + MathF.PI / 12.0f; // 偏移30度
                    float x = radius * MathF.Cos(angle);
                    float y = radius * MathF.Sin(angle);
                    float weight = 0.5f / (r + 1);
                    samples[idx++] = (new Vector2(x, y), weight);
                }
            }

            // 归一化权重
            float totalWeight = 0;
            for (int i = 0; i < idx; i++) totalWeight += samples[i].Weight;
            for (int i = 0; i < idx; i++)
                samples[i] = (samples[i].Offset, samples[i].Weight / totalWeight);

            return samples;
        }

        /// <summary>
        /// 叶片散景采样：多边形近似，模拟光圈叶片。
        /// 在 N 边形内分布采样点，产生多边形散景光斑。
        /// </summary>
        public static (Vector2 Offset, float Weight)[] GenerateBladeSamples(
            int bladeCount, float maxRadius, int sampleCount)
        {
            var samples = new (Vector2 Offset, float Weight)[sampleCount];
            float goldenAngle = 2.39996323f;
            int blades = Math.Clamp(bladeCount, 3, 12);

            // 多边形顶点
            Vector2[] vertices = new Vector2[blades];
            for (int b = 0; b < blades; b++)
            {
                float angle = b * 2.0f * MathF.PI / blades - MathF.PI / 2.0f;
                vertices[b] = new Vector2(maxRadius * MathF.Cos(angle), maxRadius * MathF.Sin(angle));
            }

            // 判断点是否在多边形内
            bool InsidePolygon(float px, float py)
            {
                bool inside = false;
                for (int i = 0, j = blades - 1; i < blades; j = i++)
                {
                    float xi = vertices[i].X, yi = vertices[i].Y;
                    float xj = vertices[j].X, yj = vertices[j].Y;
                    if (((yi > py) != (yj > py)) &&
                        (px < (xj - xi) * (py - yi) / (yj - yi) + xi))
                    {
                        inside = !inside;
                    }
                }
                return inside;
            }

            int validCount = 0;
            for (int i = 0; i < sampleCount * 2 && validCount < sampleCount; i++)
            {
                float r = maxRadius * MathF.Sqrt((float)i / (sampleCount * 2));
                float theta = i * goldenAngle;
                float px = r * MathF.Cos(theta);
                float py = r * MathF.Sin(theta);

                // 只保留多边形内的采样点
                if (!InsidePolygon(px, py)) continue;

                float weight = MathF.Exp(-0.5f * (r * r) / (maxRadius * maxRadius * 0.25f));
                samples[validCount++] = (new Vector2(px, py), weight);
            }

            // 如果有效点不足，用圆形采样补齐
            for (int i = validCount; i < sampleCount; i++)
            {
                float r = maxRadius * MathF.Sqrt((float)i / sampleCount) * 0.9f;
                float theta = i * goldenAngle;
                samples[i] = (new Vector2(r * MathF.Cos(theta), r * MathF.Sin(theta)), 0.001f);
            }

            // 归一化
            float totalWeight = 0;
            for (int i = 0; i < sampleCount; i++) totalWeight += samples[i].Weight;
            if (totalWeight > 0)
                for (int i = 0; i < sampleCount; i++)
                    samples[i] = (samples[i].Offset, samples[i].Weight / totalWeight);

            return samples;
        }

        // ── CPU 侧 CoC 计算 ──

        /// <summary>
        /// 计算 Circle of Confusion（散焦圆）半径。
        /// 基于薄镜头模型：CoC = |f * (d - D) / (d * (D - f))|
        /// 简化版使用 smoothstep 过渡。
        /// </summary>
        /// <param name="depth">像素的线性深度值。</param>
        /// <param name="focusDistance">对焦距离。</param>
        /// <param name="nearFocusStart">近场模糊开始距离。</param>
        /// <param name="farFocusStart">远场模糊开始距离。</param>
        /// <param name="nearBlurSize">近场最大模糊半径。</param>
        /// <param name="farBlurSize">远场最大模糊半径。</param>
        /// <param name="focalLength">焦距（通常 = nearFocusStart * 0.5）。</param>
        /// <returns>CoC 半径（像素）。</returns>
        public static float ComputeCoC(
            float depth, float focusDistance, float nearFocusStart,
            float farFocusStart, float nearBlurSize, float farBlurSize,
            float focalLength)
        {
            if (depth <= 0.0f) return 0.0f;

            float nearCoC = 0.0f;
            float farCoC = 0.0f;

            // 近场 CoC：depth < focusDistance
            // NearCoC = smoothstep(focusNear, focusNear - nearRange, depth) * nearBlurSize
            if (depth < focusDistance)
            {
                float t = (focusDistance - depth) / Math.Max(nearFocusStart, 0.001f);
                t = Math.Clamp(t, 0.0f, 1.0f);
                // smoothstep
                t = t * t * (3.0f - 2.0f * t);
                nearCoC = t * nearBlurSize;
            }

            // 远场 CoC：depth > focusDistance
            // FarCoC = smoothstep(focusFar, focusFar + farRange, depth) * farBlurSize
            if (depth > focusDistance)
            {
                float t = (depth - focusDistance) / Math.Max(farFocusStart, 0.001f);
                t = Math.Clamp(t, 0.0f, 1.0f);
                // smoothstep
                t = t * t * (3.0f - 2.0f * t);
                farCoC = t * farBlurSize;
            }

            // 完整的薄镜头公式参考：
            // CoC = |focalLength * (focusDistance - depth) / (depth * (focusDistance - focalLength))|
            // 这里简化为 smoothstep 过渡以获得更好的视觉控制

            return Math.Max(nearCoC, farCoC);
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // DepthOfFieldPipeline — 景深管线
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 景深管线：完整的 DoF 后处理效果实现。
    /// 执行流程：CoC 计算 → 近场模糊 → 远场模糊 → 前景散焦 → 合成
    /// </summary>
    public sealed class DepthOfFieldPipeline : IDisposable
    {
        private readonly RHIDevice _device;
        private DepthOfFieldSettings _settings;

        // ── 计算着色器管线 ──
        private RHIPipelineState? _cocPipeline;
        private RHIPipelineState? _nearBlurPipeline;
        private RHIPipelineState? _farBlurPipeline;
        private RHIPipelineState? _foregroundPipeline;
        private RHIPipelineState? _compositePipeline;

        // ── 渲染资源 ──
        private RHITexture? _cocTexture;         // CoC 半径纹理 (R16_Float)
        private RHITexture? _nearFieldTexture;   // 近场模糊结果
        private RHITexture? _farFieldTexture;    // 远场模糊结果
        private RHITexture? _foregroundAlpha;   // 前景 Alpha 遮罩
        private RHITexture? _tempTexture;        // 临时纹理（Ping-Pong）

        private int _width, _height;
        private bool _initialized;

        /// <summary>当前设置。</summary>
        public DepthOfFieldSettings Settings => _settings;

        /// <summary>CoC 纹理（供其他系统读取）。</summary>
        public RHITexture? CoCTexture => _cocTexture;

        /// <summary>是否已初始化。</summary>
        public bool IsInitialized => _initialized;

        // ── 构造 ──

        public DepthOfFieldPipeline(RHIDevice device, DepthOfFieldSettings settings)
        {
            _device = device ?? throw new ArgumentNullException(nameof(device));
            _settings = settings;
        }

        /// <summary>初始化。</summary>
        public void Initialize(int width, int height)
        {
            _width = width;
            _height = height;
            AllocateResources();
            CreatePipelines();
            _initialized = true;
        }

        /// <summary>分配渲染资源。</summary>
        private void AllocateResources()
        {
            RHITextureDesc MakeRT(string name, RHIFormat fmt, int w, int h) => new()
            {
                Dimension = RHIResourceDimension.Texture2D,
                Format = fmt,
                Width = Math.Max(1, w),
                Height = Math.Max(1, h),
                DepthOrArraySize = 1,
                MipLevels = 1,
                SampleCount = 1,
                Flags = RHITextureFlags.UnorderedAccess | RHITextureFlags.ShaderResource
            };

            _cocTexture = _device.CreateTexture(MakeRT("DoF_CoC", RHIFormat.R16_Float, _width, _height), "DoF_CoC");
            _nearFieldTexture = _device.CreateTexture(MakeRT("DoF_NearField", RHIFormat.R16G16B16A16_Float, _width, _height), "DoF_NearField");
            _farFieldTexture = _device.CreateTexture(MakeRT("DoF_FarField", RHIFormat.R16G16B16A16_Float, _width, _height), "DoF_FarField");
            _foregroundAlpha = _device.CreateTexture(MakeRT("DoF_FGAlpha", RHIFormat.R8_UNorm, _width, _height), "DoF_FGAlpha");
            _tempTexture = _device.CreateTexture(MakeRT("DoF_Temp", RHIFormat.R16G16B16A16_Float, _width, _height), "DoF_Temp");
        }

        /// <summary>创建计算管线。</summary>
        private void CreatePipelines()
        {
            _cocPipeline = _device.CreateComputePipelineState(new RHIComputePipelineDesc
            {
                Name = "DoF_CoC",
                CS = ShaderManager.Instance.GetBytecode("PostProcess/DoF", "DoFCoCCS", RHIShaderStage.Compute)
            });
            _nearBlurPipeline = _device.CreateComputePipelineState(new RHIComputePipelineDesc
            {
                Name = "DoF_NearBlur",
                CS = ShaderManager.Instance.GetBytecode("PostProcess/DoF", "DoFNearBlurCS", RHIShaderStage.Compute)
            });
            _farBlurPipeline = _device.CreateComputePipelineState(new RHIComputePipelineDesc
            {
                Name = "DoF_FarBlur",
                CS = ShaderManager.Instance.GetBytecode("PostProcess/DoF", "DoFFarBlurCS", RHIShaderStage.Compute)
            });
            _foregroundPipeline = _device.CreateComputePipelineState(new RHIComputePipelineDesc
            {
                Name = "DoF_Foreground",
                CS = ShaderManager.Instance.GetBytecode("PostProcess/DoF", "DoFForegroundCS", RHIShaderStage.Compute)
            });
            _compositePipeline = _device.CreateComputePipelineState(new RHIComputePipelineDesc
            {
                Name = "DoF_Composite",
                CS = ShaderManager.Instance.GetBytecode("PostProcess/DoF", "DoFCompositeCS", RHIShaderStage.Compute)
            });
        }

        /// <summary>
        /// 执行完整景深管线。
        /// </summary>
        /// <param name="cmdList">命令列表。</param>
        /// <param name="sceneColor">场景颜色（HDR）。</param>
        /// <param name="sceneDepth">场景深度。</param>
        /// <param name="output">输出纹理。</param>
        /// <param name="invProjMatrix">逆投影矩阵。</param>
        public void Execute(RHICommandList cmdList, RHITexture sceneColor, RHITexture sceneDepth,
            RHITexture output, in Matrix4x4 invProjMatrix)
        {
            if (!_initialized || !_settings.Enabled) return;

            float focalLength = _settings.NearFocusRange * 0.5f;

            // Phase 1: CoC Calculation
            ExecuteCoCCalculation(cmdList, sceneDepth, invProjMatrix, focalLength);

            // Phase 2: 近场模糊
            ExecuteNearFieldBlur(cmdList, sceneColor);

            // Phase 3: 远场模糊
            ExecuteFarFieldBlur(cmdList, sceneColor);

            // Phase 4: 前景散焦
            ExecuteForegroundScatter(cmdList, sceneColor, sceneDepth);

            // Phase 5: 合成
            ExecuteComposite(cmdList, sceneColor, output);
        }

        /// <summary>
        /// Phase 1: CoC (Circle of Confusion) 计算。
        /// 对每个像素计算散焦圆半径：
        ///   CoC = abs(focalLength * (focusDistance - depth) / (depth * (focusDistance - focalLength)))
        /// 近处和远处使用不同过渡曲线：
        ///   NearCoC = smoothstep(focusNear, focusNear - nearRange, depth) * nearBlurSize
        ///   FarCoC  = smoothstep(focusFar, focusFar + farRange, depth) * farBlurSize
        /// </summary>
        private void ExecuteCoCCalculation(RHICommandList cmdList, RHITexture depth, in Matrix4x4 invProj, float focalLength)
        {
            if (_cocPipeline == null) return;

            cmdList.SetPipelineState(_cocPipeline);
            cmdList.TransitionTo(depth, RHIResourceState.NonPixelShaderResource);
            cmdList.TransitionTo(_cocTexture!, RHIResourceState.UnorderedAccess);

            var cb = new DOFCoCCB
            {
                InvProjMatrix = invProj,
                FocusDistance = _settings.FocusDistance,
                NearFocusRange = _settings.NearFocusRange,
                FarFocusRange = _settings.FarFocusRange,
                FocalLength = focalLength,
                NearBlurSize = _settings.NearBlurSize,
                FarBlurSize = _settings.FarBlurSize,
                Pad0 = 0.0f, Pad1 = 0.0f
            };

            cmdList.Dispatch((_width + 7) / 8, (_height + 7) / 8, 1);
            cmdList.TransitionTo(_cocTexture, RHIResourceState.NonPixelShaderResource);
        }

        /// <summary>
        /// Phase 2: 近场模糊。
        /// 对前景物体（depth &lt; focusDistance）进行模糊。
        /// VariableRadiusGather：每个像素根据 CoC 收集邻居样本。
        /// 模糊半径 = CoC * nearBlurSize / maxBlurRadius
        /// 使用 BokehKernel 生成的采样模式。
        /// </summary>
        private void ExecuteNearFieldBlur(RHICommandList cmdList, RHITexture source)
        {
            if (_nearBlurPipeline == null) return;

            cmdList.SetPipelineState(_nearBlurPipeline);
            cmdList.TransitionTo(source, RHIResourceState.NonPixelShaderResource);
            cmdList.TransitionTo(_cocTexture!, RHIResourceState.NonPixelShaderResource);
            cmdList.TransitionTo(_nearFieldTexture!, RHIResourceState.UnorderedAccess);

            var cb = new DOFBlurCB
            {
                InvResolution = new Vector2(1.0f / _width, 1.0f / _height),
                MaxBlurRadius = _settings.NearBlurSize,
                BokehShape = (int)_settings.BokehShape,
                Pad0 = 0.0f, Pad1 = 0.0f, Pad2 = 0.0f
            };

            cmdList.Dispatch((_width + 7) / 8, (_height + 7) / 8, 1);
            cmdList.TransitionTo(_nearFieldTexture, RHIResourceState.NonPixelShaderResource);
        }

        /// <summary>
        /// Phase 3: 远场模糊。
        /// 类似近场，但使用 FarCoC 作为模糊半径。
        /// 背景物体通常需要更大的模糊半径。
        /// </summary>
        private void ExecuteFarFieldBlur(RHICommandList cmdList, RHITexture source)
        {
            if (_farBlurPipeline == null) return;

            cmdList.SetPipelineState(_farBlurPipeline);
            cmdList.TransitionTo(source, RHIResourceState.NonPixelShaderResource);
            cmdList.TransitionTo(_cocTexture!, RHIResourceState.NonPixelShaderResource);
            cmdList.TransitionTo(_farFieldTexture!, RHIResourceState.UnorderedAccess);

            var cb = new DOFBlurCB
            {
                InvResolution = new Vector2(1.0f / _width, 1.0f / _height),
                MaxBlurRadius = _settings.FarBlurSize,
                BokehShape = (int)_settings.BokehShape,
                Pad0 = 0.0f, Pad1 = 0.0f, Pad2 = 0.0f
            };

            cmdList.Dispatch((_width + 7) / 8, (_height + 7) / 8, 1);
            cmdList.TransitionTo(_farFieldTexture, RHIResourceState.NonPixelShaderResource);
        }

        /// <summary>
        /// Phase 4: 前景散焦。
        /// 前景物体需要覆盖背景。
        /// 使用 Depth Peel 或前景 Alpha 遮罩：
        ///   1. 从深度缓冲提取前景物体的 Alpha 遮罩
        ///   2. 前景物体边缘 CoC 很大，模糊后叠加到背景上
        ///   3. 使用 Alpha 混合确保前景正确覆盖
        /// </summary>
        private void ExecuteForegroundScatter(RHICommandList cmdList, RHITexture source, RHITexture depth)
        {
            if (_foregroundPipeline == null) return;

            cmdList.SetPipelineState(_foregroundPipeline);
            cmdList.TransitionTo(source, RHIResourceState.NonPixelShaderResource);
            cmdList.TransitionTo(depth, RHIResourceState.NonPixelShaderResource);
            cmdList.TransitionTo(_cocTexture!, RHIResourceState.NonPixelShaderResource);
            cmdList.TransitionTo(_foregroundAlpha!, RHIResourceState.UnorderedAccess);
            cmdList.TransitionTo(_tempTexture!, RHIResourceState.UnorderedAccess);

            var cb = new DOFBlurCB
            {
                InvResolution = new Vector2(1.0f / _width, 1.0f / _height),
                MaxBlurRadius = _settings.NearBlurSize,
                BokehShape = (int)_settings.BokehShape,
                Pad0 = 0.0f, Pad1 = 0.0f, Pad2 = 0.0f
            };

            cmdList.Dispatch((_width + 7) / 8, (_height + 7) / 8, 1);
            cmdList.TransitionTo(_foregroundAlpha, RHIResourceState.NonPixelShaderResource);
            cmdList.TransitionTo(_tempTexture, RHIResourceState.NonPixelShaderResource);
        }

        /// <summary>
        /// Phase 5: 合成。
        /// 合并近场/远场/焦内区域：
        ///   - CoC 接近0的区域：使用原始场景颜色（焦内）
        ///   - 近场CoC > 0：混合近场模糊结果
        ///   - 远场CoC > 0：混合远场模糊结果
        ///   - 前景散焦叠加
        /// </summary>
        private void ExecuteComposite(RHICommandList cmdList, RHITexture sceneColor, RHITexture output)
        {
            if (_compositePipeline == null) return;

            cmdList.SetPipelineState(_compositePipeline);
            cmdList.TransitionTo(sceneColor, RHIResourceState.NonPixelShaderResource);
            cmdList.TransitionTo(_nearFieldTexture!, RHIResourceState.NonPixelShaderResource);
            cmdList.TransitionTo(_farFieldTexture!, RHIResourceState.NonPixelShaderResource);
            cmdList.TransitionTo(_cocTexture!, RHIResourceState.NonPixelShaderResource);
            cmdList.TransitionTo(_foregroundAlpha!, RHIResourceState.NonPixelShaderResource);
            cmdList.TransitionTo(_tempTexture!, RHIResourceState.NonPixelShaderResource);
            cmdList.TransitionTo(output, RHIResourceState.UnorderedAccess);

            var cb = new DOFCompositeCB
            {
                InvResolution = new Vector2(1.0f / _width, 1.0f / _height),
                Pad0 = 0.0f, Pad1 = 0.0f
            };

            cmdList.Dispatch((_width + 7) / 8, (_height + 7) / 8, 1);
            cmdList.TransitionTo(output, RHIResourceState.NonPixelShaderResource);
        }

        /// <summary>更新设置。</summary>
        public void UpdateSettings(DepthOfFieldSettings settings) => _settings = settings;

        /// <summary>释放资源。</summary>
        public void Dispose()
        {
            _cocPipeline?.Dispose();
            _nearBlurPipeline?.Dispose();
            _farBlurPipeline?.Dispose();
            _foregroundPipeline?.Dispose();
            _compositePipeline?.Dispose();
            _cocTexture?.Dispose();
            _nearFieldTexture?.Dispose();
            _farFieldTexture?.Dispose();
            _foregroundAlpha?.Dispose();
            _tempTexture?.Dispose();
            _initialized = false;
            GC.SuppressFinalize(this);
        }
    }
}
