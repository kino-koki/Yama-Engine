// ============================================================================================
//  VulkanRenderDevice.cs — yama引擎 Vulkan 渲染设备适配器
//
//  本文件实现 VulkanRenderDevice 类，作为简单 IRenderDevice 接口（由 RuntimeGameLoop 使用）
//  与完整 RHI 系统（RHIDevice / VulkanDevice）之间的桥梁。这是启用真实 GPU 渲染的关键适配器。
//
//  职责:
//    1. 封装 RHIDevice 的创建与生命周期管理
//    2. 管理交换链、深度缓冲、命令列表、围栏等帧同步资源
//    3. 编译内建 GLSL 着色器并创建图形管线状态对象 (PSO)
//    4. 将 RenderSceneSnapshot 中的网格数据上传至 GPU 并执行绘制调用
//    5. 提供与 NullRenderDevice 一致的降级回退行为（初始化失败或无交换链时）
//
//  作者: yama引擎团队
// ============================================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using YamaEngine.Modeling;
using YamaEngine.RHI;

namespace YamaEngine.Rendering
{
    /// <summary>
    ///   Vulkan 渲染设备适配器 —— 将简单的 <see cref="IRenderDevice"/> 接口桥接至完整的
    ///   <see cref="RHIDevice"/> / <see cref="RHI.Vulkan.VulkanDevice"/> RHI 系统。
    ///   <para>
    ///   该类负责管理交换链、深度缓冲、命令列表、围栏、着色器编译、管线状态以及网格几何缓存，
    ///   使上层游戏循环 (<c>RuntimeGameLoop</c>) 无需直接接触底层 RHI 即可完成真实 GPU 渲染。
    ///   </para>
    ///   <para>
    ///   当设备未初始化或渲染过程中发生异常时，自动降级为类似 <see cref="NullRenderDevice"/> 的
    ///   行为（仅统计网格数量，不执行实际绘制），保证引擎在无 GPU 环境下仍可运行。
    ///   </para>
    /// </summary>
    public sealed class VulkanRenderDevice : IResizableRenderDevice, IDisposable
    {
        // ──────────────────────────────────────────────────────────────
        // 常量
        // ──────────────────────────────────────────────────────────────

        /// <summary>交错顶点布局步长（字节）：Position(12) + Normal(12) + UV(8) = 32。</summary>
        private const int VertexStride = 32;

        /// <summary>帧常量缓冲区大小（字节），向上对齐至 256 以满足 Vulkan uniform buffer 对齐要求。</summary>
        private const int FrameConstantsBufferSize = 256;

        /// <summary>物体常量缓冲区大小（字节），向上对齐至 256 以满足 Vulkan uniform buffer 对齐要求。</summary>
        private const int ObjectConstantsBufferSize = 256;

        /// <summary>交换链后台缓冲区数量（双缓冲）。</summary>
        private const int SwapChainBufferCount = 2;

        /// <summary>后台缓冲区清除颜色（深蓝色）。</summary>
        private static readonly Vector4 ClearColor = new(0.015f, 0.015f, 0.05f, 1.0f);

        /// <summary>默认环境光颜色 (RGBA: 低强度冷色调)。</summary>
        private static readonly Vector4 DefaultAmbient = new(0.15f, 0.18f, 0.25f, 0.35f);

        /// <summary>默认方向光方向（从左上前方照射）。</summary>
        private static readonly Vector3 DefaultLightDirection =
            Vector3.Normalize(new Vector3(-0.5f, -1.0f, -0.3f));

        // ──────────────────────────────────────────────────────────────
        // 字段: 设备与队列
        // ──────────────────────────────────────────────────────────────

        private readonly RHIDevice? _device;
        private readonly bool _ownsDevice;
        private RHICommandQueue? _graphicsQueue;

        // ──────────────────────────────────────────────────────────────
        // 字段: 交换链与帧同步
        // ──────────────────────────────────────────────────────────────

        private RHISwapChain? _swapChain;
        private RHICommandList? _commandList;
        private RHIFence? _fence;
        private ulong _fenceValue;

        // ──────────────────────────────────────────────────────────────
        // 字段: 渲染目标与深度
        // ──────────────────────────────────────────────────────────────

        private RHITexture? _depthBuffer;
        private RHIDescriptorHandle _depthDSV;

        /// <summary>无头模式下的离屏渲染目标（用于测试，无交换链）。</summary>
        private RHITexture? _offscreenRT;
        private RHIDescriptorHandle _offscreenRTV;

        // ──────────────────────────────────────────────────────────────
        // 字段: 管线状态
        // ──────────────────────────────────────────────────────────────

        private RHIRootSignature? _rootSignature;
        private RHIPipelineState? _pipelineState;
        private RHIShaderBytecode _vsBytecode;
        private RHIShaderBytecode _psBytecode;

        // ──────────────────────────────────────────────────────────────
        // 字段: 常量缓冲区
        // ──────────────────────────────────────────────────────────────

        private RHIBuffer? _frameConstantsBuffer;
        private RHIBuffer? _objectConstantsBuffer;

        // ──────────────────────────────────────────────────────────────
        // 字段: 网格缓存
        // ──────────────────────────────────────────────────────────────

        /// <summary>按网格资产路径缓存的 GPU 几何数据（顶点/索引缓冲）。</summary>
        private readonly Dictionary<string, MeshGeometry> _meshGeometryCache = new();

        /// <summary>按网格资产路径缓存的 EditableMesh 数据（避免重复磁盘读取）。</summary>
        private readonly Dictionary<string, EditableMesh> _editableMeshCache = new();

        // ──────────────────────────────────────────────────────────────
        // 字段: 状态
        // ──────────────────────────────────────────────────────────────

        private int _width;
        private int _height;
        private bool _initialized;
        private bool _headless;
        private bool _disposed;

        // ══════════════════════════════════════════════════════════════
        // 嵌套类型
        // ══════════════════════════════════════════════════════════════

        /// <summary>
        ///   缓存的 GPU 网格几何数据，包含顶点缓冲、索引缓冲及对应的顶点/索引数量。
        /// </summary>
        private sealed class MeshGeometry
        {
            /// <summary>顶点缓冲（交错布局: Position + Normal + UV）。</summary>
            public RHIBuffer? VertexBuffer;

            /// <summary>索引缓冲（R32_UInt 格式）。</summary>
            public RHIBuffer? IndexBuffer;

            /// <summary>顶点数量。</summary>
            public int VertexCount;

            /// <summary>索引数量（= 面数 × 3）。</summary>
            public int IndexCount;
        }

        /// <summary>
        ///   交错顶点布局结构体，与 GLSL 着色器中的 location 0/1/2 输入对应。
        ///   <para>内存布局: Position(12字节) + Normal(12字节) + UV(8字节) = 32字节步长。</para>
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct MeshVertex
        {
            public Vector3 Position;
            public Vector3 Normal;
            public Vector2 UV;
        }

        // ══════════════════════════════════════════════════════════════
        // 构造函数
        // ══════════════════════════════════════════════════════════════

        /// <summary>
        ///   初始化 <see cref="VulkanRenderDevice"/> 的新实例。
        ///   <para>
        ///   当无法创建底层 RHI 设备（如无 GPU 或驱动缺失）时，构造不会抛出异常，
        ///   而是进入降级模式：<see cref="Render"/> 调用将退化为仅统计网格数量的空渲染，
        ///   行为与 <see cref="NullRenderDevice"/> 一致。这确保引擎在无 GPU 环境下仍可运行。
        ///   </para>
        /// </summary>
        /// <param name="existingDevice">
        ///   可选的已有 <see cref="RHIDevice"/> 实例。若为 <c>null</c>，则通过
        ///   <see cref="RHIDeviceFactory.Create()"/> 自动创建 Vulkan 设备。
        ///   传入已有设备时，调用方负责其生命周期；本实例不会在 Dispose 时释放它。
        /// </param>
        public VulkanRenderDevice(RHIDevice? existingDevice = null)
        {
            if (existingDevice != null)
            {
                _device = existingDevice;
                _ownsDevice = false;
            }
            else
            {
                try
                {
                    _device = RHIDeviceFactory.Create();
                    _ownsDevice = true;
                }
                catch (Exception ex)
                {
                    // 不抛出异常，进入降级模式（无 GPU 环境仍可运行）
                    Console.WriteLine($"[VulkanRenderDevice] RHIDevice 创建失败，进入降级模式: {ex.Message}");
                    _device = null;
                    _ownsDevice = false;
                }
            }
        }

        /// <summary>
        ///   获取一个值，指示底层 RHI 设备是否可用（即非降级模式）。
        /// </summary>
        public bool IsDeviceAvailable => _device != null;

        // ══════════════════════════════════════════════════════════════
        // IRenderDevice 属性
        // ══════════════════════════════════════════════════════════════

        /// <summary>
        ///   获取渲染设备名称。
        /// </summary>
        public string Name => $"Yama Vulkan Render Device ({_device?.AdapterName ?? "Uninitialized"})";

        /// <summary>
        ///   获取渲染设备能力描述。Vulkan 后端支持 PBR 材质、阴影和实例化渲染。
        /// </summary>
        public RenderDeviceCapabilities Capabilities { get; } = new()
        {
            Backend = "Vulkan",
            SupportsPbrMaterials = true,
            SupportsShadows = true,
            SupportsInstancing = true
        };

        // ══════════════════════════════════════════════════════════════
        // 初始化方法
        // ══════════════════════════════════════════════════════════════

        /// <summary>
        ///   使用窗口句柄初始化渲染设备，创建交换链及所有必要的 GPU 资源。
        /// </summary>
        /// <param name="hwnd">原生窗口句柄（HWND / VkSurface 创建所需）。</param>
        /// <param name="width">渲染目标宽度（像素）。</param>
        /// <param name="height">渲染目标高度（像素）。</param>
        /// <exception cref="ObjectDisposedException">设备已被释放。</exception>
        public void Initialize(IntPtr hwnd, int width, int height)
        {
            ThrowIfDisposed();
            if (_device == null)
            {
                Console.WriteLine("[VulkanRenderDevice] 无法初始化: RHI 设备不可用（降级模式）");
                _initialized = false;
                return;
            }
            _headless = false;
            _width = Math.Max(1, width);
            _height = Math.Max(1, height);

            try
            {
                CreateCommandResources();
                CreateSwapChain(hwnd, _width, _height);
                CreateDepthBuffer(_width, _height);
                CompileShadersAndPipeline(RHIFormat.B8G8R8A8_UNorm);
                CreateConstantBuffers();
                _initialized = true;
                Console.WriteLine($"[VulkanRenderDevice] 初始化完成 (窗口模式 {_width}x{_height})");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[VulkanRenderDevice] Initialize 失败: {ex.Message}");
                _initialized = false;
            }
        }

        /// <summary>
        ///   以无头模式初始化渲染设备（无交换链），创建离屏渲染目标。
        ///   <para>主要用于单元测试和离线渲染场景。</para>
        /// </summary>
        /// <param name="width">离屏渲染目标宽度（像素）。</param>
        /// <param name="height">离屏渲染目标高度（像素）。</param>
        /// <exception cref="ObjectDisposedException">设备已被释放。</exception>
        public void InitializeHeadless(int width, int height)
        {
            ThrowIfDisposed();
            if (_device == null)
            {
                Console.WriteLine("[VulkanRenderDevice] 无法初始化无头模式: RHI 设备不可用（降级模式）");
                _initialized = false;
                return;
            }
            _headless = true;
            _width = Math.Max(1, width);
            _height = Math.Max(1, height);

            try
            {
                CreateCommandResources();
                CreateOffscreenRenderTarget(_width, _height);
                CreateDepthBuffer(_width, _height);
                CompileShadersAndPipeline(RHIFormat.R8G8B8A8_UNorm);
                CreateConstantBuffers();
                _initialized = true;
                Console.WriteLine($"[VulkanRenderDevice] 初始化完成 (无头模式 {_width}x{_height})");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[VulkanRenderDevice] InitializeHeadless 失败: {ex.Message}");
                _initialized = false;
            }
        }

        // ══════════════════════════════════════════════════════════════
        // 渲染主方法
        // ══════════════════════════════════════════════════════════════

        /// <summary>
        ///   渲染一帧场景。这是 <see cref="IRenderDevice"/> 接口的核心方法。
        ///   <para>
        ///   流程: 获取相机 → 构建视图/投影矩阵 → 更新帧常量 → 录制命令列表
        ///   → 清除颜色/深度 → 设置管线 → 遍历网格并绘制 → 呈现 → 等待 GPU 完成。
        ///   </para>
        /// </summary>
        /// <param name="scene">要渲染的场景快照。</param>
        /// <param name="deltaTime">本帧增量时间（秒）。</param>
        /// <returns>本帧的统计信息（网格数、绘制调用数等）。</returns>
        public FrameStats Render(IRenderScene scene, float deltaTime)
        {
            if (scene == null) throw new ArgumentNullException(nameof(scene));

            var sw = Stopwatch.StartNew();

            // 未初始化或无交换链时降级为空渲染（仅统计）
            if (!_initialized || _commandList == null || _graphicsQueue == null)
            {
                return FallbackRender(scene, deltaTime, sw);
            }

            int drawCalls = 0;
            int visibleMeshCount = 0;

            try
            {
                // 1. 获取主相机（优先 IsMain=true，否则取第一个）
                var camera = GetMainCamera(scene);

                // 2. 构建视图/投影矩阵
                var view = BuildViewMatrix(camera);
                var proj = BuildProjectionMatrix(camera);
                var viewProj = view * proj;
                var camPos = GetTranslation(camera.WorldMatrix);

                // 3. 获取场景光源
                var light = scene.Lights.Count > 0 ? scene.Lights[0] : null;
                var lightDir = light != null
                    ? Vector3.Normalize(GetForward(light.WorldMatrix))
                    : DefaultLightDirection;
                var lightColor = light?.Color ?? Vector3.One;
                var lightIntensity = light?.Intensity ?? 1.0f;

                // 4. 更新帧常量缓冲区
                UpdateFrameConstants(view, proj, viewProj, camPos, lightDir, lightColor, lightIntensity);

                // 5. 获取当前渲染目标
                RHITexture renderTarget;
                RHIDescriptorHandle rtvHandle;
                if (_headless && _offscreenRT != null)
                {
                    renderTarget = _offscreenRT;
                    rtvHandle = _offscreenRTV;
                }
                else if (_swapChain != null)
                {
                    renderTarget = _swapChain.GetCurrentBackBuffer();
                    rtvHandle = _swapChain.GetCurrentBackBufferRTV();
                }
                else
                {
                    return FallbackRender(scene, deltaTime, sw);
                }

                // 6. 打开并重置命令列表
                _commandList.Open();
                _commandList.Reset(_pipelineState);

                // 7. 转换渲染目标至 RenderTarget 状态
                _commandList.TransitionTo(renderTarget, RHIResourceState.RenderTarget);

                // 8. 转换深度缓冲至 DepthWrite 状态
                if (_depthBuffer != null)
                {
                    _commandList.TransitionTo(_depthBuffer, RHIResourceState.DepthWrite);
                }

                // 9. 清除颜色和深度
                _commandList.ClearRenderTargetView(rtvHandle, ClearColor);
                if (_depthBuffer != null)
                {
                    _commandList.ClearDepthStencilView(_depthDSV, true, false, 1.0f, 0);
                }

                // 10. 设置渲染目标和视口
                _commandList.OMSetRenderTargets(new[] { rtvHandle }, _depthDSV.IsValid ? _depthDSV : null);
                _commandList.RSSetViewport(0, 0, _width, _height, 0f, 1f);
                _commandList.RSSetScissorRect(0, 0, _width, _height);

                // 11. 设置管线状态和根签名
                if (_pipelineState != null)
                    _commandList.SetPipelineState(_pipelineState);
                if (_rootSignature != null)
                    _commandList.SetGraphicsRootSignature(_rootSignature);

                // 12. 设置图元拓扑
                _commandList.IASetPrimitiveTopology(RHIPrimitiveTopology.TriangleList);

                // 13. 绑定帧常量缓冲区（根参数 0）
                if (_frameConstantsBuffer != null)
                    _commandList.SetGraphicsRootConstantBufferView(0, _frameConstantsBuffer);

                // 14. 遍历可见网格并绘制
                foreach (var mesh in scene.Meshes)
                {
                    if (mesh == null || !mesh.IsVisible) continue;

                    var geometry = GetOrCreateMeshGeometry(mesh.MeshAssetPath);
                    if (geometry?.VertexBuffer == null || geometry.IndexBuffer == null)
                        continue;

                    visibleMeshCount++;

                    // 更新物体常量缓冲区
                    UpdateObjectConstants(mesh);

                    // 绑定物体常量缓冲区（根参数 1）
                    if (_objectConstantsBuffer != null)
                        _commandList.SetGraphicsRootConstantBufferView(1, _objectConstantsBuffer);

                    // 设置顶点/索引缓冲
                    _commandList.IASetVertexBuffers(0, geometry.VertexBuffer, 0,
                        (uint)VertexStride, (uint)geometry.VertexBuffer.AllocatedBytes);
                    _commandList.IASetIndexBuffer(geometry.IndexBuffer, 0,
                        (uint)geometry.IndexBuffer.AllocatedBytes, RHIFormat.R32_UInt);

                    // 绘制
                    _commandList.DrawIndexedInstanced((uint)geometry.IndexCount, 1, 0, 0, 0);
                    drawCalls++;
                }

                // 15. 转换渲染目标至 Present 状态
                if (!_headless)
                {
                    _commandList.TransitionTo(renderTarget, RHIResourceState.Present);
                }

                // 16. 关闭命令列表
                _commandList.Close();

                // 17. 执行命令列表
                _graphicsQueue.ExecuteCommandLists(_commandList);

                // 18. 发出围栏信号
                _fenceValue++;
                if (_fence != null)
                {
                    _graphicsQueue.Signal(_fence, _fenceValue);
                }

                // 19. 呈现（启用垂直同步）
                if (_swapChain != null)
                {
                    _swapChain.Present(true);
                }

                // 20. 等待 GPU 完成
                if (_fence != null)
                {
                    _fence.Wait(_fenceValue);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[VulkanRenderDevice] Render 异常: {ex.Message}");
                return FallbackRender(scene, deltaTime, sw);
            }

            sw.Stop();

            return new FrameStats
            {
                MeshCount = scene.Meshes.Count,
                VisibleMeshCount = visibleMeshCount,
                DrawCallCount = drawCalls,
                CameraCount = scene.Cameras.Count,
                LightCount = scene.Lights.Count,
                FrameTimeMilliseconds = MathF.Max(0f, (float)sw.Elapsed.TotalMilliseconds),
                DeltaTimeSeconds = MathF.Max(0f, deltaTime)
            };
        }

        // ══════════════════════════════════════════════════════════════
        // 调整大小
        // ══════════════════════════════════════════════════════════════

        /// <summary>
        ///   调整渲染目标大小，重建交换链和深度缓冲。
        /// </summary>
        /// <param name="width">新的宽度（像素）。</param>
        /// <param name="height">新的高度（像素）。</param>
        public void Resize(int width, int height)
        {
            if (!_initialized) return;

            _width = Math.Max(1, width);
            _height = Math.Max(1, height);

            try
            {
                // 等待 GPU 空闲
                if (_graphicsQueue != null)
                    _graphicsQueue.Flush();

                // 重建交换链
                if (_swapChain != null)
                {
                    _swapChain.Resize(_width, _height);
                }

                // 重建离屏渲染目标
                if (_headless)
                {
                    DisposeOffscreenRT();
                    CreateOffscreenRenderTarget(_width, _height);
                }

                // 重建深度缓冲
                DisposeDepthBuffer();
                CreateDepthBuffer(_width, _height);

                Console.WriteLine($"[VulkanRenderDevice] 调整大小至 {_width}x{_height}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[VulkanRenderDevice] Resize 失败: {ex.Message}");
            }
        }

        // ══════════════════════════════════════════════════════════════
        // 资源创建 (私有)
        // ══════════════════════════════════════════════════════════════

        /// <summary>
        ///   获取非空 RHI 设备。仅在已确认 <see cref="_device"/> 不为 null 的上下文中使用
        ///   （即 Initialize / InitializeHeadless / Render 等方法已做过 null 检查后）。
        /// </summary>
        private RHIDevice Device
        {
            get
            {
                if (_device == null)
                    throw new InvalidOperationException("RHI 设备不可用，无法执行 GPU 操作");
                return _device;
            }
        }

        /// <summary>
        ///   创建命令队列、命令列表和围栏。
        /// </summary>
        private void CreateCommandResources()
        {
            var device = Device;
            _graphicsQueue = device.GetCommandQueue(RHIQueueType.Graphics);
            _commandList = device.CreateCommandList(RHIQueueType.Graphics, "VulkanRenderDevice.MainCmdList");
            _fence = device.CreateFence(0, "VulkanRenderDevice.FrameFence");
            _fenceValue = 0;
        }

        /// <summary>
        ///   创建交换链。
        /// </summary>
        /// <param name="hwnd">窗口句柄。</param>
        /// <param name="width">宽度。</param>
        /// <param name="height">高度。</param>
        private void CreateSwapChain(IntPtr hwnd, int width, int height)
        {
            _swapChain = Device.CreateSwapChain(hwnd, width, height,
                RHIFormat.B8G8R8A8_UNorm, SwapChainBufferCount, true,
                "VulkanRenderDevice.SwapChain");
        }

        /// <summary>
        ///   创建离屏渲染目标（无头模式）。
        /// </summary>
        private void CreateOffscreenRenderTarget(int width, int height)
        {
            var desc = new RHITextureDesc
            {
                Dimension = RHIResourceDimension.Texture2D,
                Format = RHIFormat.R8G8B8A8_UNorm,
                Width = width,
                Height = height,
                DepthOrArraySize = 1,
                MipLevels = 1,
                SampleCount = 1,
                SampleQuality = 0,
                Flags = RHITextureFlags.RenderTarget | RHITextureFlags.ShaderResource,
                OptimizedClearValue = RHIClearValueHelper.Color(RHIFormat.R8G8B8A8_UNorm, ClearColor)
            };
            _offscreenRT = Device.CreateTexture(desc, "VulkanRenderDevice.OffscreenRT");
            _offscreenRT.CurrentState = RHIResourceState.Common;
            _offscreenRTV = Device.CreateRTV(_offscreenRT);
        }

        /// <summary>
        ///   创建深度缓冲区及深度模板视图。
        /// </summary>
        private void CreateDepthBuffer(int width, int height)
        {
            var desc = new RHITextureDesc
            {
                Dimension = RHIResourceDimension.Texture2D,
                Format = RHIFormat.D32_Float,
                Width = width,
                Height = height,
                DepthOrArraySize = 1,
                MipLevels = 1,
                SampleCount = 1,
                SampleQuality = 0,
                Flags = RHITextureFlags.DepthStencil,
                OptimizedClearValue = RHIClearValueHelper.Depth(1.0f, 0, RHIFormat.D32_Float)
            };
            _depthBuffer = Device.CreateTexture(desc, "VulkanRenderDevice.DepthBuffer");
            _depthBuffer.CurrentState = RHIResourceState.Common;
            _depthDSV = Device.CreateDSV(_depthBuffer);
        }

        /// <summary>
        ///   编译内建 GLSL 着色器并创建根签名与图形管线状态对象。
        /// </summary>
        /// <param name="rtvFormat">渲染目标格式。</param>
        private void CompileShadersAndPipeline(RHIFormat rtvFormat)
        {
            // 编译顶点着色器 (GLSL → SPIR-V)
            _vsBytecode = Device.CompileShader(
                BuiltinShadersGLSL.MeshVertexShader, "main",
                RHIShaderStage.Vertex, RHIShaderModel.SM_6_0,
                "MeshVertexShader.glsl", false);

            // 编译片段着色器 (GLSL → SPIR-V)
            _psBytecode = Device.CompileShader(
                BuiltinShadersGLSL.MeshFragmentShader, "main",
                RHIShaderStage.Pixel, RHIShaderModel.SM_6_0,
                "MeshFragmentShader.glsl", false);

            // 创建根签名: 2 个 CBV 参数 (register 0 = FrameConstants, register 1 = ObjectConstants)
            var rootDesc = new RHIRootSignatureDesc
            {
                Parameters = new RHIRootParameter[]
                {
                    new()
                    {
                        ParameterType = RHIRootParameterType.CBV,
                        ShaderVisibility = RHIShaderVisibility.All,
                        Descriptor = new RHIRootDescriptor { ShaderRegister = 0, RegisterSpace = 0 }
                    },
                    new()
                    {
                        ParameterType = RHIRootParameterType.CBV,
                        ShaderVisibility = RHIShaderVisibility.All,
                        Descriptor = new RHIRootDescriptor { ShaderRegister = 1, RegisterSpace = 0 }
                    }
                },
                StaticSamplers = new[] { RHISamplerDesc.AnisotropicWrap },
                AllowInputAssembler = true
            };
            _rootSignature = Device.CreateRootSignature(rootDesc, "VulkanRenderDevice.RootSig");

            // 创建图形管线状态对象
            var pipelineDesc = new RHIGraphicsPipelineDesc
            {
                Name = "VulkanRenderDevice.MeshPSO",
                VS = _vsBytecode,
                PS = _psBytecode,
                InputLayout = new RHIInputElement[]
                {
                    // location 0: vec3 position (偏移 0)
                    new("POSITION", RHIFormat.R32G32B32_Float, 0, 0),
                    // location 1: vec3 normal (偏移 12)
                    new("NORMAL", RHIFormat.R32G32B32_Float, 0, 12),
                    // location 2: vec2 uv (偏移 24)
                    new("TEXCOORD", RHIFormat.R32G32_Float, 0, 24)
                },
                PrimitiveTopology = RHIPrimitiveTopology.TriangleList,
                BlendState = RHIBlendDesc.Default,
                DepthStencilState = RHIDepthDesc.Default,
                RasterizerState = RHIRasterizerDesc.Default,
                RTVFormats = new[] { rtvFormat },
                DSVFormat = RHIFormat.D32_Float,
                SampleCount = 1,
                RootSignature = _rootSignature
            };
            _pipelineState = Device.CreateGraphicsPipelineState(pipelineDesc, "VulkanRenderDevice.MeshPSO");
        }

        /// <summary>
        ///   创建帧常量和物体常量缓冲区。
        /// </summary>
        private void CreateConstantBuffers()
        {
            var frameDesc = new RHIBufferDesc
            {
                SizeInBytes = FrameConstantsBufferSize,
                Format = RHIFormat.Unknown,
                HeapType = RHIResourceHeapType.Upload,
                Flags = RHIBufferFlags.ConstantBuffer,
                Stride = 0
            };
            _frameConstantsBuffer = Device.CreateBuffer(frameDesc, "VulkanRenderDevice.FrameCB");

            var objDesc = new RHIBufferDesc
            {
                SizeInBytes = ObjectConstantsBufferSize,
                Format = RHIFormat.Unknown,
                HeapType = RHIResourceHeapType.Upload,
                Flags = RHIBufferFlags.ConstantBuffer,
                Stride = 0
            };
            _objectConstantsBuffer = Device.CreateBuffer(objDesc, "VulkanRenderDevice.ObjectCB");
        }

        // ══════════════════════════════════════════════════════════════
        // 常量缓冲区更新 (私有)
        // ══════════════════════════════════════════════════════════════

        /// <summary>
        ///   更新帧常量缓冲区（视图、投影、相机位置、光源信息）。
        /// </summary>
        private void UpdateFrameConstants(
            Matrix4x4 view, Matrix4x4 proj, Matrix4x4 viewProj,
            Vector3 camPos, Vector3 lightDir, Vector3 lightColor, float lightIntensity)
        {
            if (_frameConstantsBuffer == null) return;

            var fc = new BuiltinShadersGLSL.FrameConstants
            {
                ViewMatrix = view,
                ProjectionMatrix = proj,
                ViewProjectionMatrix = viewProj,
                CameraPosition = camPos,
                LightDirection = lightDir,
                LightColor = new Vector4(lightColor * lightIntensity, 1.0f),
                AmbientColor = DefaultAmbient
            };
            Device.UploadToBuffer(_frameConstantsBuffer,
                MemoryMarshal.CreateReadOnlySpan(ref fc, 1));
        }

        /// <summary>
        ///   更新物体常量缓冲区（世界矩阵、逆转置矩阵、材质参数）。
        /// </summary>
        private void UpdateObjectConstants(RenderMesh mesh)
        {
            if (_objectConstantsBuffer == null) return;

            var world = mesh.WorldMatrix;
            Matrix4x4.Invert(world, out var worldInverse);
            var worldInverseTranspose = Matrix4x4.Transpose(worldInverse);

            var oc = new BuiltinShadersGLSL.ObjectConstants
            {
                WorldMatrix = world,
                WorldInverseTranspose = worldInverseTranspose,
                BaseColor = new Vector4(mesh.Material.BaseColor, 1.0f),
                Metallic = mesh.Material.Metallic,
                Roughness = mesh.Material.Roughness,
                UseTexture = 0.0f  // 暂不使用纹理
            };
            Device.UploadToBuffer(_objectConstantsBuffer,
                MemoryMarshal.CreateReadOnlySpan(ref oc, 1));
        }

        // ══════════════════════════════════════════════════════════════
        // 网格几何缓存 (私有)
        // ══════════════════════════════════════════════════════════════

        /// <summary>
        ///   获取或创建网格的 GPU 几何数据。若缓存中不存在，则从磁盘加载 EditableMesh
        ///   并上传顶点/索引数据至 GPU 缓冲区。
        /// </summary>
        /// <param name="meshAssetPath">网格资产文件路径。</param>
        /// <returns>缓存的 <see cref="MeshGeometry"/>；若加载失败则返回 <c>null</c>。</returns>
        private MeshGeometry? GetOrCreateMeshGeometry(string meshAssetPath)
        {
            if (string.IsNullOrWhiteSpace(meshAssetPath))
                return null;

            if (_meshGeometryCache.TryGetValue(meshAssetPath, out var cached))
                return cached;

            // 从磁盘加载 EditableMesh（带缓存）
            if (!_editableMeshCache.TryGetValue(meshAssetPath, out var mesh))
            {
                try
                {
                    if (!File.Exists(meshAssetPath))
                    {
                        Console.WriteLine($"[VulkanRenderDevice] 网格文件不存在: {meshAssetPath}");
                        return null;
                    }
                    mesh = EditableMesh.Read(meshAssetPath);
                    _editableMeshCache[meshAssetPath] = mesh;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[VulkanRenderDevice] 加载网格失败 {meshAssetPath}: {ex.Message}");
                    return null;
                }
            }

            if (mesh == null || mesh.Vertices.Count == 0 || mesh.Faces.Count == 0)
                return null;

            // 构建交错顶点缓冲
            var vertices = new MeshVertex[mesh.Vertices.Count];
            var hasNormals = mesh.Normals.Count == mesh.Vertices.Count;
            var hasUVs = mesh.UV0.Count == mesh.Vertices.Count;
            for (int i = 0; i < mesh.Vertices.Count; i++)
            {
                vertices[i] = new MeshVertex
                {
                    Position = mesh.Vertices[i],
                    Normal = hasNormals ? mesh.Normals[i] : Vector3.Zero,
                    UV = hasUVs ? mesh.UV0[i] : Vector2.Zero
                };
            }

            // 构建索引缓冲（每个面 3 个 uint32 索引）
            var indices = new uint[mesh.Faces.Count * 3];
            for (int i = 0; i < mesh.Faces.Count; i++)
            {
                var face = mesh.Faces[i];
                indices[i * 3 + 0] = (uint)face.A;
                indices[i * 3 + 1] = (uint)face.B;
                indices[i * 3 + 2] = (uint)face.C;
            }

            // 创建并上传顶点缓冲
            var vbDesc = new RHIBufferDesc
            {
                SizeInBytes = (ulong)(vertices.Length * VertexStride),
                Format = RHIFormat.Unknown,
                HeapType = RHIResourceHeapType.Default,
                Flags = RHIBufferFlags.VertexBuffer,
                Stride = (ulong)VertexStride
            };
            var vertexBuffer = Device.CreateBuffer(vbDesc, $"VB:{Path.GetFileName(meshAssetPath)}");
            vertexBuffer.CurrentState = RHIResourceState.Common;
            Device.UploadToBuffer(vertexBuffer, vertices.AsSpan());
            vertexBuffer.CurrentState = RHIResourceState.VertexAndConstantBuffer;

            // 创建并上传索引缓冲
            var ibDesc = new RHIBufferDesc
            {
                SizeInBytes = (ulong)(indices.Length * sizeof(uint)),
                Format = RHIFormat.R32_UInt,
                HeapType = RHIResourceHeapType.Default,
                Flags = RHIBufferFlags.IndexBuffer,
                Stride = 4
            };
            var indexBuffer = Device.CreateBuffer(ibDesc, $"IB:{Path.GetFileName(meshAssetPath)}");
            indexBuffer.CurrentState = RHIResourceState.Common;
            Device.UploadToBuffer(indexBuffer, indices.AsSpan());
            indexBuffer.CurrentState = RHIResourceState.IndexBuffer;

            // 缓存几何数据
            var geometry = new MeshGeometry
            {
                VertexBuffer = vertexBuffer,
                IndexBuffer = indexBuffer,
                VertexCount = vertices.Length,
                IndexCount = indices.Length
            };
            _meshGeometryCache[meshAssetPath] = geometry;

            return geometry;
        }

        // ══════════════════════════════════════════════════════════════
        // 辅助方法 (私有)
        // ══════════════════════════════════════════════════════════════

        /// <summary>
        ///   获取场景主相机（优先选择 <see cref="RenderCamera.IsMain"/> 为 true 的相机，
        ///   若无则取第一个相机；若场景无相机则返回默认相机）。
        /// </summary>
        private RenderCamera GetMainCamera(IRenderScene scene)
        {
            if (scene.Cameras.Count == 0)
            {
                return new RenderCamera
                {
                    FieldOfView = 60f,
                    NearClip = 0.1f,
                    FarClip = 1000f,
                    AspectRatio = _height > 0 ? (float)_width / _height : 1.777f,
                    IsMain = true
                };
            }

            foreach (var cam in scene.Cameras)
            {
                if (cam.IsMain) return cam;
            }
            return scene.Cameras[0];
        }

        /// <summary>
        ///   从相机世界矩阵构建视图矩阵（世界矩阵的逆矩阵）。
        /// </summary>
        private static Matrix4x4 BuildViewMatrix(RenderCamera camera)
        {
            if (Matrix4x4.Invert(camera.WorldMatrix, out var view))
                return view;
            return Matrix4x4.Identity;
        }

        /// <summary>
        ///   从相机参数构建透视投影矩阵。
        /// </summary>
        private Matrix4x4 BuildProjectionMatrix(RenderCamera camera)
        {
            var fovRadians = camera.FieldOfView * (MathF.PI / 180f);
            var aspect = camera.AspectRatio > 0
                ? camera.AspectRatio
                : (_height > 0 ? (float)_width / _height : 1.777f);
            return Matrix4x4.CreatePerspectiveFieldOfView(fovRadians, aspect, camera.NearClip, camera.FarClip);
        }

        /// <summary>
        ///   提取矩阵的平移分量（相机/光源位置）。
        /// </summary>
        private static Vector3 GetTranslation(Matrix4x4 m) => new(m.M41, m.M42, m.M43);

        /// <summary>
        ///   提取矩阵的前向向量（第三基向量，用于方向光方向）。
        /// </summary>
        private static Vector3 GetForward(Matrix4x4 m) => new(m.M31, m.M32, m.M33);

        /// <summary>
        ///   降级渲染回退：与 <see cref="NullRenderDevice"/> 行为一致，仅统计网格数量。
        /// </summary>
        private static FrameStats FallbackRender(IRenderScene scene, float deltaTime, Stopwatch sw)
        {
            var visibleMeshes = scene.Meshes.Count(m => m.IsVisible);
            sw.Stop();
            return new FrameStats
            {
                MeshCount = scene.Meshes.Count,
                VisibleMeshCount = visibleMeshes,
                DrawCallCount = visibleMeshes,
                CameraCount = scene.Cameras.Count,
                LightCount = scene.Lights.Count,
                FrameTimeMilliseconds = MathF.Max(0f, (float)sw.Elapsed.TotalMilliseconds),
                DeltaTimeSeconds = MathF.Max(0f, deltaTime)
            };
        }

        /// <summary>
        ///   若设备已被释放则抛出 <see cref="ObjectDisposedException"/>。
        /// </summary>
        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(VulkanRenderDevice));
        }

        // ══════════════════════════════════════════════════════════════
        // 资源释放 (私有)
        // ══════════════════════════════════════════════════════════════

        /// <summary>释放深度缓冲资源。</summary>
        private void DisposeDepthBuffer()
        {
            _depthBuffer?.Dispose();
            _depthBuffer = null;
            _depthDSV = default;
        }

        /// <summary>释放离屏渲染目标资源。</summary>
        private void DisposeOffscreenRT()
        {
            _offscreenRT?.Dispose();
            _offscreenRT = null;
            _offscreenRTV = default;
        }

        /// <summary>释放所有缓存的网格几何资源。</summary>
        private void DisposeMeshGeometryCache()
        {
            foreach (var geo in _meshGeometryCache.Values)
            {
                geo.VertexBuffer?.Dispose();
                geo.IndexBuffer?.Dispose();
            }
            _meshGeometryCache.Clear();
            _editableMeshCache.Clear();
        }

        // ══════════════════════════════════════════════════════════════
        // IDisposable
        // ══════════════════════════════════════════════════════════════

        /// <summary>
        ///   释放所有非托管 GPU 资源。
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _initialized = false;

            // 等待 GPU 完成所有工作
            try
            {
                _graphicsQueue?.Flush();
            }
            catch { /* 忽略释放时的异常 */ }

            // 释放网格缓存
            DisposeMeshGeometryCache();

            // 释放常量缓冲区
            _frameConstantsBuffer?.Dispose();
            _frameConstantsBuffer = null;
            _objectConstantsBuffer?.Dispose();
            _objectConstantsBuffer = null;

            // 释放管线资源
            _pipelineState?.Dispose();
            _pipelineState = null;
            _rootSignature?.Dispose();
            _rootSignature = null;

            // 释放渲染目标
            DisposeDepthBuffer();
            DisposeOffscreenRT();

            // 释放交换链
            _swapChain?.Dispose();
            _swapChain = null;

            // 释放命令资源
            _commandList?.Dispose();
            _commandList = null;
            _fence?.Dispose();
            _fence = null;
            _graphicsQueue?.Dispose();
            _graphicsQueue = null;

            // 释放设备（仅在自有情况下）
            if (_ownsDevice)
            {
                _device?.Dispose();
            }

            Console.WriteLine("[VulkanRenderDevice] 资源已释放");
        }
    }
}
