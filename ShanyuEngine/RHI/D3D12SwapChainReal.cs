// ==================================================================================================
//  D3D12SwapChainReal.cs - 山雨游戏引擎 D3D12 交换链真实实现
//
//  命名空间: ShanyuEngine.RHI
//  依赖: Vortice.Direct3D12, Vortice.DXGI
//
//  功能覆盖:
//    - D3D12SwapChain: 封装 IDXGISwapChain4
//    - 创建: DXGI Factory / CommandQueue / HWND / Format / BufferCount / Stereo / Flags
//    - Present: 真实 Present 调用 (SyncInterval / Flags)
//    - GetCurrentBackBufferIndex / GetBuffer / GetRenderTargetView
//    - 全屏切换: SetFullscreenState / GetFullscreenState
//    - HDR 支持: SetColorSpace / DXGI_COLOR_SPACE_RGB_STUDIO_G208_NONE_P2020 / GetHDRMetaData
//    - Tearing 支持: AllowTearing / PresentAllowTearing
//    - 等待: WaitForGPU / FrameLatencyWaitableObject
//    - 调整大小: ResizeBuffers
// ==================================================================================================

using System;
using System.Runtime.InteropServices;
using Vortice.Direct3D12;
using Vortice.DXGI;

namespace ShanyuEngine.RHI
{
    // ==============================================================================================
    //  D3D12SwapChain - 真实交换链实现
    // ==============================================================================================

    /// <summary>
    /// D3D12 交换链封装。基于 IDXGISwapChain4，支持 HDR、Tearing、Flip Model。
    /// </summary>
    public sealed class D3D12SwapChain : IDisposable
    {
        private readonly IDXGIFactory4 _factory;
        private IDXGISwapChain4? _swapChain;
        private readonly ID3D12CommandQueue _commandQueue;
        private readonly ID3D12Device _device;
        private ID3D12Resource[] _backBuffers;
        private CpuDescriptorHandle[] _rtvHandles;
        private readonly D3D12DescriptorPool _rtvPool;
        private int _rtvHeapIndex;
        private int _rtvDescriptorStart;

        private int _width;
        private int _height;
        private Format _format;
        private int _bufferCount;
        private bool _isFullscreen;
        private bool _tearingSupported;
        private bool _hdrSupported;
        private IntPtr _hwnd;
        private bool _disposed;

        // 帧同步
        private readonly D3D12Fence _frameFence;
        private ulong _currentFrameValue;
        private int _frameLatency;

        public int Width => _width;
        public int Height => _height;
        public Format Format => _format;
        public int BufferCount => _bufferCount;
        public bool IsFullscreen => _isFullscreen;
        public bool TearingSupported => _tearingSupported;
        public bool HDRSupported => _hdrSupported;

        /// <summary>
        /// 创建 D3D12 交换链。
        /// </summary>
        /// <param name="factory">DXGI Factory</param>
        /// <param name="device">D3D12 设备</param>
        /// <param name="commandQueue">Direct 命令队列</param>
        /// <param name="rtvPool">RTV 描述符池 (用于创建 RTV)</param>
        /// <param name="hwnd">窗口句柄</param>
        /// <param name="width">宽度</param>
        /// <param name="height">高度</param>
        /// <param name="format">渲染格式</param>
        /// <param name="bufferCount">缓冲数量 (建议 2 或 3)</param>
        /// <param name="windowed">是否窗口模式</param>
        /// <param name="name">交换链名称</param>
        public D3D12SwapChain(IDXGIFactory4 factory, ID3D12Device device,
            ID3D12CommandQueue commandQueue, D3D12DescriptorPool rtvPool,
            IntPtr hwnd, int width, int height,
            Format format = Format.R8G8B8A8_UNorm,
            int bufferCount = 2, bool windowed = true, string name = "")
        {
            _factory = factory;
            _device = device;
            _commandQueue = commandQueue;
            _rtvPool = rtvPool;
            _hwnd = hwnd;
            _width = width;
            _height = height;
            _format = format;
            _bufferCount = bufferCount;
            _frameFence = new D3D12Fence(device, 0, "SwapChain_Fence");
            _currentFrameValue = 0;
            _frameLatency = bufferCount - 1; // 最大延迟

            // 检测 Tearing 支持
            _tearingSupported = CheckTearingSupport();

            // 创建交换链
            CreateSwapChain(windowed, name);

            // 创建后缓冲 RTV
            CreateBackBufferRTVs();
        }

        private bool CheckTearingSupport()
        {
            try
            {
                // 使用 IDXGIFactory5 检查 Tearing 支持
                if (_factory is IDXGIFactory5 factory5)
                {
                    var result = factory5.CheckFeatureSupport(
                        DXGIFeature.FeaturePresent,
                        out bool tearingSupported);
                    return tearingSupported && result.Success;
                }
            }
            catch
            {
                // 不支持 IDXGIFactory5
            }
            return false;
        }

        private void CreateSwapChain(bool windowed, string name)
        {
            // 构建 SwapChain 描述
            var swapChainDesc = new SwapChainDescription
            {
                BufferCount = (uint)_bufferCount,
                Width = (uint)_width,
                Height = (uint)_height,
                Format = _format,
                Stereo = false,
                SampleDescription = new SampleDescription(1, 0),
                BufferUsage = DXGIUsage.RenderTargetOutput,
                SwapEffect = SwapEffect.FlipDiscard,
                Windowed = windowed,
                OutputWindow = _hwnd,
                Scaling = Scaling.Stretch,
                AlphaMode = AlphaMode.Premultiplied
            };

            // 如果支持 Tearing 则启用
            if (_tearingSupported)
            {
                swapChainDesc.Flags = (uint)SwapChainFlags.AllowTearing;
            }

            // 创建 SwapChain (D3D12 需要 SwapChain 从 Queue 创建)
            var result = _factory.CreateSwapChainForHwnd(
                _commandQueue,
                _hwnd,
                swapChainDesc,
                null, // FullScreenDesc
                null, // RestrictToOutput
                out IDXGISwapChain swapChainBase);

            if (result.Failure)
                throw new InvalidOperationException($"Failed to create swap chain: {result.Description}");

            // 升级到 IDXGISwapChain4
            _swapChain = swapChainBase.QueryInterface<IDXGISwapChain4>();
            swapChainBase.Dispose();

            // 检查 HDR 支持
            _hdrSupported = CheckHDRSupport();

            // 丢弃 Alt+Enter 全屏切换 (可选: 启用)
            _factory.MakeWindowAssociation(_hwnd, WindowAssociationFlags.NoAltEnter);

            // 获取后缓冲
            _backBuffers = new ID3D12Resource[_bufferCount];
            for (int i = 0; i < _bufferCount; i++)
            {
                _backBuffers[i] = _swapChain.GetBuffer<ID3D12Resource>(i);
                if (!string.IsNullOrEmpty(name))
                    _backBuffers[i].DeviceName = $"{name}_BackBuffer_{i}";
            }
        }

        private bool CheckHDRSupport()
        {
            if (_swapChain == null) return false;

            try
            {
                // 检查输出是否支持 HDR
                var outputDesc = _swapChain.GetContainingOutput()?.Description;
                if (outputDesc == null) return false;

                // 检查颜色空间支持
                if (_swapChain is IDXGISwapChain4 sc4)
                {
                    var colorSpaceSupport = new ColorSpaceType[1];
                    // DXGI_COLOR_SPACE_RGB_STUDIO_G208_NONE_P2020 = 13 (HDR10)
                    // DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020 = 17 (scRGB)
                    // 使用 CheckColorSpaceSupport
                    bool supported = false;
                    var hr = sc4.CheckColorSpaceSupport(ColorSpaceType.RGBStudioG208NoneP2020, out uint flags);
                    return hr.Success && (flags & 1) != 0;
                }
            }
            catch
            {
                return false;
            }

            return false;
        }

        private void CreateBackBufferRTVs()
        {
            _rtvHandles = new CpuDescriptorHandle[_bufferCount];

            // 从 RTV 描述符池分配连续描述符
            var baseHandle = _rtvPool.AllocateRange(_bufferCount, out _rtvHeapIndex, out _rtvDescriptorStart);

            for (int i = 0; i < _bufferCount; i++)
            {
                _rtvHandles[i] = _rtvPool.GetCpuHandle(_rtvHeapIndex, _rtvDescriptorStart + i);
                _device.CreateRenderTargetView(_backBuffers[i], null, _rtvHandles[i]);
            }
        }

        // === 核心 API ===

        /// <summary>获取当前后缓冲索引</summary>
        public int GetCurrentBackBufferIndex()
        {
            return _swapChain?.GetCurrentBackBufferIndex() ?? 0;
        }

        /// <summary>获取指定索引的后缓冲</summary>
        public ID3D12Resource GetBuffer(int index)
        {
            if (index < 0 || index >= _bufferCount)
                throw new ArgumentOutOfRangeException(nameof(index));

            return _backBuffers[index];
        }

        /// <summary>获取指定索引的 RTV</summary>
        public CpuDescriptorHandle GetRenderTargetView(int index)
        {
            if (index < 0 || index >= _bufferCount)
                throw new ArgumentOutOfRangeException(nameof(index));

            return _rtvHandles[index];
        }

        /// <summary>获取当前后缓冲</summary>
        public ID3D12Resource GetCurrentBackBuffer()
        {
            return _backBuffers[GetCurrentBackBufferIndex()];
        }

        /// <summary>获取当前 RTV</summary>
        public CpuDescriptorHandle GetCurrentRenderTargetView()
        {
            return _rtvHandles[GetCurrentBackBufferIndex()];
        }

        /// <summary>
        /// 真实 Present 调用。
        /// </summary>
        /// <param name="vsync">垂直同步 (0=关闭, 1-N=同步间隔)</param>
        /// <param name="allowTearing">允许 Tearing (仅 VSync 关闭时有效)</param>
        public void Present(int syncInterval = 0, bool allowTearing = false)
        {
            if (_swapChain == null) throw new InvalidOperationException("Swap chain not created");

            uint flags = 0;
            if (allowTearing && _tearingSupported && syncInterval == 0)
            {
                flags = (uint)PresentFlags.AllowTearing;
            }

            _swapChain.Present((uint)syncInterval, flags);
        }

        // === 全屏 ===

        /// <summary>切换全屏模式</summary>
        public void SetFullscreenState(bool fullscreen)
        {
            if (_swapChain == null) throw new InvalidOperationException("Swap chain not created");
            _swapChain.SetFullscreenState(fullscreen, null);
            _isFullscreen = fullscreen;

            // 全屏切换后更新尺寸
            if (fullscreen)
            {
                var output = _swapChain.GetContainingOutput();
                if (output != null)
                {
                    var desc = output.Description;
                    _width = desc.DesktopCoordinates.Right - desc.DesktopCoordinates.Left;
                    _height = desc.DesktopCoordinates.Bottom - desc.DesktopCoordinates.Top;
                    output.Dispose();
                }
            }
        }

        /// <summary>获取当前全屏状态</summary>
        public (bool isFullscreen, IDXGIOutput? output) GetFullscreenState()
        {
            if (_swapChain == null) return (false, null);
            var hr = _swapChain.GetFullscreenState(out bool fullscreen, out IDXGIOutput output);
            return (hr.Success ? fullscreen : false, output);
        }

        // === HDR ===

        /// <summary>设置颜色空间 (HDR 支持)</summary>
        public bool SetColorSpace(ColorSpaceType colorSpace)
        {
            if (_swapChain == null) return false;

            try
            {
                var hr = _swapChain as IDXGISwapChain4;
                if (hr == null) return false;

                var result = hr.CheckColorSpaceSupport(colorSpace, out uint flags);
                if (result.Failure) return false;

                hr.SetColorSpace1(colorSpace);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>启用 HDR10 颜色空间</summary>
        public bool EnableHDR()
        {
            if (!_hdrSupported) return false;
            return SetColorSpace(ColorSpaceType.RGBStudioG208NoneP2020);
        }

        /// <summary>设置 SDR 颜色空间</summary>
        public bool SetSDRColorSpace()
        {
            return SetColorSpace(ColorSpaceType.RGBSRGB);
        }

        /// <summary>获取当前颜色空间</summary>
        public ColorSpaceType GetCurrentColorSpace()
        {
            if (_swapChain is IDXGISwapChain4 sc4)
                return sc4.CurrentColorSpace;
            return ColorSpaceType.RGBSRGB;
        }

        /// <summary>获取 HDR 元数据</summary>
        public DisplayColorSpace? GetHDRMetaData()
        {
            if (_swapChain == null) return null;

            try
            {
                var output = _swapChain.GetContainingOutput();
                if (output == null) return null;

                var desc = output.Description;
                output.Dispose();

                // 返回 HDR10 标准参数
                return new DisplayColorSpace
                {
                    RedPrimary = new System.Numerics.Vector2(0.64f, 0.33f),
                    GreenPrimary = new System.Numerics.Vector2(0.30f, 0.60f),
                    BluePrimary = new System.Numerics.Vector2(0.15f, 0.06f),
                    WhitePoint = new System.Numerics.Vector2(0.3127f, 0.3290f),
                    MinLuminance = 0.005f,
                    MaxLuminance = 1000.0f,
                    MaxFullFrameLuminance = 100.0f
                };
            }
            catch
            {
                return null;
            }
        }

        // === 帧同步 ===

        /// <summary>等待 GPU 完成 (CPU 同步)</summary>
        public void WaitForGPU()
        {
            _currentFrameValue++;
            _commandQueue.Signal(_frameFence.NativeFence, _currentFrameValue);
            _frameFence.WaitOnCpu(_currentFrameValue);
        }

        /// <summary>获取帧延迟等待对象句柄</summary>
        public IntPtr GetFrameLatencyWaitableObject()
        {
            if (_swapChain == null) return IntPtr.Zero;

            try
            {
                if (_swapChain is IDXGISwapChain2 sc2)
                    return sc2.GetFrameLatencyWaitableObject();
            }
            catch
            {
                // 不支持
            }

            return IntPtr.Zero;
        }

        /// <summary>等待前一帧完成</summary>
        public void WaitForPreviousFrame()
        {
            _currentFrameValue++;
            _commandQueue.Signal(_frameFence.NativeFence, _currentFrameValue);

            // 等待 GPU
            if (_frameFence.NativeFence.CompletedValue < _currentFrameValue)
            {
                using var evt = new System.Threading.EventWaitHandle(false, System.Threading.EventResetMode.AutoReset);
                _frameFence.NativeFence.SetEventOnCompletion(_currentFrameValue, evt.SafeWaitHandle.DangerousGetHandle());
                evt.WaitOne();
            }
        }

        /// <summary>Signal 当前帧</summary>
        public void SignalFrame()
        {
            _currentFrameValue++;
            _commandQueue.Signal(_frameFence.NativeFence, _currentFrameValue);
        }

        // === 调整大小 ===

        /// <summary>调整交换链缓冲区大小</summary>
        public void ResizeBuffers(int newWidth, int newHeight, Format? newFormat = null)
        {
            if (_swapChain == null) throw new InvalidOperationException("Swap chain not created");

            // 先释放旧 RTV
            for (int i = 0; i < _bufferCount; i++)
            {
                _rtvPool.Free(_rtvHeapIndex, _rtvDescriptorStart + i);
            }

            // 释放旧后缓冲
            for (int i = 0; i < _bufferCount; i++)
            {
                _backBuffers[i].Dispose();
            }

            // 调整大小
            Format resizeFormat = newFormat ?? _format;
            var hr = _swapChain.ResizeBuffers(
                (uint)_bufferCount,
                (uint)newWidth,
                (uint)newHeight,
                resizeFormat,
                _tearingSupported ? (uint)SwapChainFlags.AllowTearing : 0);

            if (hr.Failure)
                throw new InvalidOperationException($"Failed to resize swap chain: {hr.Description}");

            _width = newWidth;
            _height = newHeight;
            if (newFormat.HasValue) _format = newFormat.Value;

            // 重新获取后缓冲
            for (int i = 0; i < _bufferCount; i++)
            {
                _backBuffers[i] = _swapChain.GetBuffer<ID3D12Resource>(i);
            }

            // 重新创建 RTV
            CreateBackBufferRTVs();
        }

        // === Dispose ===

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            WaitForGPU();

            // 释放 RTV
            if (_rtvHandles != null)
            {
                for (int i = 0; i < _bufferCount; i++)
                {
                    _rtvPool?.Free(_rtvHeapIndex, _rtvDescriptorStart + i);
                }
            }

            // 释放后缓冲
            if (_backBuffers != null)
            {
                for (int i = 0; i < _backBuffers.Length; i++)
                    _backBuffers[i]?.Dispose();
            }

            _swapChain?.Dispose();
            _frameFence.Dispose();
        }
    }

    // === 辅助类型 ===

    /// <summary>显示颜色空间 (HDR 元数据)</summary>
    public struct DisplayColorSpace
    {
        public System.Numerics.Vector2 RedPrimary;
        public System.Numerics.Vector2 GreenPrimary;
        public System.Numerics.Vector2 BluePrimary;
        public System.Numerics.Vector2 WhitePoint;
        public float MinLuminance;
        public float MaxLuminance;
        public float MaxFullFrameLuminance;
    }
}
