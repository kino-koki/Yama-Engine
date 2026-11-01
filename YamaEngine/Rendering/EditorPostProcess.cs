using System;
using System.Numerics;
using YamaEngine.RHI;

namespace YamaEngine.Rendering
{
    // ════════════════════════════════════════════════════════════════════════
    // 编辑器后处理控制器 (Editor PostProcess Controller)
    //
    // 为编辑器视口提供简化的后处理集成：
    //   1. 持有 PostProcessStack 实例并管理中间 RenderTarget
    //   2. 提供一键启用/禁用各特效的简化 API
    //   3. 管理 SceneColor (R16G16B16A16_Float) 和 SceneDepth (D32_Float) 纹理
    //   4. 连接 SimpleSceneRenderer 的输出与 PostProcessStack 的输入
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 编辑器后处理控制器：桥接 SimpleSceneRenderer 和 PostProcessStack，
    /// 为编辑器视口提供即开即用的后处理流程。
    /// </summary>
    public sealed class EditorPostProcessController : IDisposable
    {
        private readonly RHIDevice _device;
        private readonly PostProcessStack _stack;

        // ── 中间渲染目标 ──
        private RHITexture? _sceneColorTexture;
        private RHIDescriptorHandle _sceneColorRtv;
        private RHITexture? _sceneDepthTexture;
        private RHIDescriptorHandle _sceneDepthDsv;

        private int _width;
        private int _height;
        private bool _disposed;

        // ── 属性 ──

        /// <summary>后处理全局开关。</summary>
        public bool Enabled
        {
            get => _stack.Enabled;
            set => _stack.Enabled = value;
        }

        /// <summary>内部后处理栈（供高级配置使用）。</summary>
        public PostProcessStack Stack => _stack;

        /// <summary>场景颜色中间纹理（R16G16B16A16_Float）。</summary>
        public RHITexture? SceneColorTexture => _sceneColorTexture;

        /// <summary>场景颜色 RTV。</summary>
        public RHIDescriptorHandle SceneColorRTV => _sceneColorRtv;

        /// <summary>场景深度中间纹理（D32_Float）。</summary>
        public RHITexture? SceneDepthTexture => _sceneDepthTexture;

        /// <summary>场景深度 DSV。</summary>
        public RHIDescriptorHandle SceneDepthDSV => _sceneDepthDsv;

        // ── 构造 ──

        /// <summary>创建编辑器后处理控制器。</summary>
        public EditorPostProcessController(RHIDevice device, int width, int height)
        {
            _device = device ?? throw new ArgumentNullException(nameof(device));
            _stack = new PostProcessStack(device);
            _stack.Initialize(width, height);
            Resize(width, height);
        }

        // ── 尺寸管理 ──

        /// <summary>调整中间纹理尺寸（SwapChain resize 时调用）。</summary>
        public void Resize(int width, int height)
        {
            if (width <= 0 || height <= 0) return;
            if (width == _width && height == _height) return;

            _width = width;
            _height = height;

            // 释放旧资源
            _sceneColorTexture?.Dispose();
            _sceneDepthTexture?.Dispose();

            // 创建场景颜色纹理 (HDR)
            var colorDesc = new RHITextureDesc
            {
                Dimension = RHIResourceDimension.Texture2D,
                Format = RHIFormat.R16G16B16A16_Float,
                Width = width,
                Height = height,
                DepthOrArraySize = 1,
                MipLevels = 1,
                SampleCount = 1,
                Flags = RHITextureFlags.RenderTarget | RHITextureFlags.ShaderResource
            };
            _sceneColorTexture = _device.CreateTexture(colorDesc, "EditorSceneColor");
            _sceneColorRtv = _device.CreateRTV(_sceneColorTexture);

            // 创建场景深度纹理
            var depthDesc = new RHITextureDesc
            {
                Dimension = RHIResourceDimension.Texture2D,
                Format = RHIFormat.D32_Float,
                Width = width,
                Height = height,
                DepthOrArraySize = 1,
                MipLevels = 1,
                SampleCount = 1,
                Flags = RHITextureFlags.DepthStencil,
                OptimizedClearValue = RHIClearValueHelper.Depth(1.0f, 0, RHIFormat.D32_Float)
            };
            _sceneDepthTexture = _device.CreateTexture(depthDesc, "EditorSceneDepth");
            _sceneDepthDsv = _device.CreateDSV(_sceneDepthTexture);
            _sceneDepthTexture.CurrentState = RHIResourceState.DepthWrite;

            // 同步调整后处理栈尺寸
            _stack.Resize(width, height);
        }

        // ── 简化启用方法 ──

        /// <summary>启用泛光。</summary>
        public void EnableBloom(float intensity)
        {
            _stack.GlobalProfile.Bloom.Enabled = true;
            _stack.GlobalProfile.Bloom.Intensity = intensity;
            if (!_stack.ActiveEffects.Contains(PostProcessEffectType.Bloom))
                _stack.ToggleEffect(PostProcessEffectType.Bloom);
            _stack.MarkDirty();
        }

        /// <summary>启用景深。</summary>
        public void EnableDOF(float focusDistance)
        {
            _stack.GlobalProfile.DepthOfField.Enabled = true;
            _stack.GlobalProfile.DepthOfField.FocusDistance = focusDistance;
            if (!_stack.ActiveEffects.Contains(PostProcessEffectType.DOF))
                _stack.ToggleEffect(PostProcessEffectType.DOF);
            _stack.MarkDirty();
        }

        /// <summary>启用运动模糊。</summary>
        public void EnableMotionBlur(float intensity)
        {
            _stack.GlobalProfile.MotionBlur.Enabled = true;
            _stack.GlobalProfile.MotionBlur.Intensity = intensity;
            if (!_stack.ActiveEffects.Contains(PostProcessEffectType.MotionBlur))
                _stack.ToggleEffect(PostProcessEffectType.MotionBlur);
            _stack.MarkDirty();
        }

        /// <summary>启用颜色分级。</summary>
        public void EnableColorGrading(float contrast, float saturation)
        {
            _stack.GlobalProfile.ColorGrading.Enabled = true;
            _stack.GlobalProfile.ColorGrading.Contrast = contrast;
            _stack.GlobalProfile.ColorGrading.Saturation = saturation;
            if (!_stack.ActiveEffects.Contains(PostProcessEffectType.ColorGrading))
                _stack.ToggleEffect(PostProcessEffectType.ColorGrading);
            _stack.MarkDirty();
        }

        /// <summary>启用暗角。</summary>
        public void EnableVignette(float intensity)
        {
            _stack.GlobalProfile.Vignette.Enabled = true;
            _stack.GlobalProfile.Vignette.Intensity = intensity;
            if (!_stack.ActiveEffects.Contains(PostProcessEffectType.Vignette))
                _stack.ToggleEffect(PostProcessEffectType.Vignette);
            _stack.MarkDirty();
        }

        /// <summary>启用 FXAA 抗锯齿。</summary>
        public void EnableFXAA()
        {
            _stack.GlobalProfile.FXAA.Enabled = true;
            if (!_stack.ActiveEffects.Contains(PostProcessEffectType.FXAA))
                _stack.ToggleEffect(PostProcessEffectType.FXAA);
            _stack.MarkDirty();
        }

        /// <summary>禁用所有后处理特效。</summary>
        public void DisableAll()
        {
            _stack.ClearActiveEffects();
            _stack.GlobalProfile.ResetToDefaults();
            _stack.MarkDirty();
        }

        // ── 参数调节 ──

        /// <summary>设置泛光强度。</summary>
        public void SetBloomIntensity(float intensity)
        {
            _stack.GlobalProfile.Bloom.Intensity = intensity;
            _stack.MarkDirty();
        }

        /// <summary>设置景深对焦距离。</summary>
        public void SetDOFFocusDistance(float focusDistance)
        {
            _stack.GlobalProfile.DepthOfField.FocusDistance = focusDistance;
            _stack.MarkDirty();
        }

        /// <summary>设置运动模糊强度。</summary>
        public void SetMotionBlurIntensity(float intensity)
        {
            _stack.GlobalProfile.MotionBlur.Intensity = intensity;
            _stack.MarkDirty();
        }

        /// <summary>切换指定特效的启用状态。</summary>
        public void ToggleEffect(PostProcessEffectType effect) => _stack.ToggleEffect(effect);

        // ── 渲染 ──

        /// <summary>
        /// 执行后处理：从场景颜色纹理渲染到目标纹理或 RTV。
        /// </summary>
        public void RenderPostProcess(
            RHICommandList commandList,
            float deltaTime,
            RHITexture? targetTexture = null,
            RHIDescriptorHandle? targetRtv = null,
            in Matrix4x4? invProjMatrix = null)
        {
            if (!Enabled || _sceneColorTexture == null) return;

            _stack.RenderPostProcess(
                _sceneColorTexture,
                targetTexture,
                commandList,
                deltaTime,
                _sceneDepthTexture,
                null,
                invProjMatrix,
                targetRtv);
        }

        // ── 释放 ──

        /// <summary>释放所有资源。</summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _sceneColorTexture?.Dispose();
            _sceneDepthTexture?.Dispose();
            _stack.Dispose();

            GC.SuppressFinalize(this);
        }
    }
}
