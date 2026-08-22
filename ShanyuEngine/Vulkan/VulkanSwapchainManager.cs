// =============================================================================
// VulkanSwapchainManager.cs - 山雨引擎 Vulkan 交换链管理 (HDR/VRR)
// =============================================================================
// swapchain 是 Vulkan 和窗口系统 (Win32/X11/Wayland) 的接口。
// 这套管理器：
//   1. 创建/重建 swapchain (窗口大小变了要重建)
//   2. HDR 支持 (VK_EXT_swapchain_colorspace)
//   3. VRR/可变刷新率 (VK_EXT_swapchain_maintenance1 / VK_GOOGLE_display_timing)
//   4. acquire/present 的封装
//   5. present mode 自动选择 (Mailbox > FIFO Relaxed > FIFO)
//
// FIXME: 还没处理多 swapchain (多窗口)
// TODO: VK_KHR_present_wait / present_id 支持精确帧同步
//
// 作者: 山雨引擎团队
// =============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;

using Semaphore = Silk.NET.Vulkan.Semaphore;
using Buffer = Vulkan.Buffer;
using Vulkan = Silk.NET.Vulkan;
namespace ShanyuEngine.Vulkan
{
    // ════════════════════════════════════════════════════════════════════════════
    // 区域 1: 公共类型
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>色彩空间 (HDR 扩展)。</summary>
    public enum ColorSpaceExt
    {
        SrgbNonlinear,
        DisplayP3Nonlinear,
        ExtendedSrgbLinear,
        DisplayP3Linear,
        DciP3Nonlinear,
        Bt709Linear,
        Bt709Nonlinear,
        Bt2020Linear,
        Hdr10St2084,
        Dolbyvision,
        Hdr10Hlg,
        AdobeRgbLinear,
        PassThrough,
    }

    /// <summary>present mode 选择策略。</summary>
    public enum PresentModeStrategy
    {
        /// <summary>无 tearing，最低延迟 (Mailbox)。</summary>
        NoTearLowLatency,
        /// <summary>FIFO，省电。</summary>
        VSync,
        /// <summary>允许 tearing (低延迟)。</summary>
        AllowTearing,
        /// <summary>自适应：有 GPU 工作时 Mailbox，否则 FIFO。</summary>
        Adaptive,
    }

    /// <summary>swapchain 创建描述。</summary>
    public struct SwapchainCreateInfo
    {
        public IntPtr SurfaceHandle;  // VkSurfaceKHR
        public uint Width;
        public uint Height;
        public uint BufferCount;       // 期望的 image 数 (2/3)
        public Format ImageFormat;
        public ColorSpaceExt ColorSpace;
        public PresentModeStrategy PresentMode;
        public bool EnableHdr;
        public bool EnableVrr;
        public bool EnableHdrMetadata;
        public SurfaceTransformFlagsKHR PreTransform;
        public CompositeAlphaFlagsKHR CompositeAlpha;
        public ImageUsageFlags ImageUsage;
    }

    /// <summary>swapchain 统计。</summary>
    public struct SwapchainStats
    {
        public int AcquireCount;
        public int PresentCount;
        public int RecreateCount;
        public int AcquireFailures;
        public int PresentFailures;
        public double AverageFrameTimeMs;
        public ulong CurrentImageIndex;

        public override string ToString()
            => $"Swapchain: acquire={AcquireCount}, present={PresentCount}, " +
               $"recreate={RecreateCount}, failures={AcquireFailures + PresentFailures}";
    }

    /// <summary>swapchain image 信息。</summary>
    public struct SwapchainImage
    {
        public Image Image;
        public ImageView View;
        public uint Index;
        public bool Acquired;
        public bool Presented;
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 2: swapchain 配置
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>swapchain 配置 helper。</summary>
    public sealed class SwapchainConfigBuilder
    {
        private SwapchainCreateInfo _ci = new()
        {
            BufferCount = 3,
            ImageFormat = Format.B8G8R8A8Unorm,
            ColorSpace = ColorSpaceExt.SrgbNonlinear,
            PresentMode = PresentModeStrategy.NoTearLowLatency,
            PreTransform = SurfaceTransformFlagsKHR.IdentityBit,
            CompositeAlpha = CompositeAlphaFlagsKHR.OpaqueBit,
            ImageUsage = ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.TransferDstBit,
        };

        public SwapchainConfigBuilder WithSurface(IntPtr surface)
        { _ci.SurfaceHandle = surface; return this; }

        public SwapchainConfigBuilder WithSize(uint w, uint h)
        { _ci.Width = w; _ci.Height = h; return this; }

        public SwapchainConfigBuilder WithBufferCount(uint n)
        { _ci.BufferCount = n; return this; }

        public SwapchainConfigBuilder WithFormat(Format fmt, ColorSpaceExt cs)
        { _ci.ImageFormat = fmt; _ci.ColorSpace = cs; return this; }

        public SwapchainConfigBuilder EnableHdr(bool enable = true)
        { _ci.EnableHdr = enable; return this; }

        public SwapchainConfigBuilder EnableVrr(bool enable = true)
        { _ci.EnableVrr = enable; return this; }

        public SwapchainConfigBuilder WithPresentMode(PresentModeStrategy mode)
        { _ci.PresentMode = mode; return this; }

        public SwapchainConfigBuilder WithImageUsage(ImageUsageFlags usage)
        { _ci.ImageUsage = usage; return this; }

        public SwapchainCreateInfo Build() => _ci;
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 3: HDR 元数据
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>HDR 元数据 (BT.2020 / ST.2084)。</summary>
    public struct HdrMetadata
    {
        // 显示器 primaries (CIE 1931 xy)
        public float RedPrimaryX, RedPrimaryY;
        public float GreenPrimaryX, GreenPrimaryY;
        public float BluePrimaryX, BluePrimaryY;
        public float WhitePointX, WhitePointY;
        // 亮度范围
        public float MinLuminance;     // nits
        public float MaxLuminance;     // nits
        public float MaxContentLightLevel;       // MaxCLL
        public float MaxFrameAverageLightLevel;  // MaxFALL

        public static HdrMetadata DisplayP3 => new()
        {
            RedPrimaryX = 0.680f, RedPrimaryY = 0.320f,
            GreenPrimaryX = 0.265f, GreenPrimaryY = 0.690f,
            BluePrimaryX = 0.150f, BluePrimaryY = 0.060f,
            WhitePointX = 0.3127f, WhitePointY = 0.3290f,
            MinLuminance = 0.01f,
            MaxLuminance = 1000f,
            MaxContentLightLevel = 1000f,
            MaxFrameAverageLightLevel = 400f,
        };

        public static HdrMetadata Rec2020Hdr10 => new()
        {
            RedPrimaryX = 0.708f, RedPrimaryY = 0.292f,
            GreenPrimaryX = 0.170f, GreenPrimaryY = 0.797f,
            BluePrimaryX = 0.131f, BluePrimaryY = 0.046f,
            WhitePointX = 0.3127f, WhitePointY = 0.3290f,
            MinLuminance = 0.01f,
            MaxLuminance = 4000f,
            MaxContentLightLevel = 4000f,
            MaxFrameAverageLightLevel = 1000f,
        };

        public static HdrMetadata Srgb => new()
        {
            RedPrimaryX = 0.640f, RedPrimaryY = 0.330f,
            GreenPrimaryX = 0.300f, GreenPrimaryY = 0.600f,
            BluePrimaryX = 0.150f, BluePrimaryY = 0.060f,
            WhitePointX = 0.3127f, WhitePointY = 0.3290f,
            MinLuminance = 0.5f,
            MaxLuminance = 80f,
        };
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 4: surface 能力查询
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>surface 能力查询。</summary>
    public sealed unsafe class SurfaceCapabilitiesQuery
    {
        private readonly Vk _vk;
        private readonly PhysicalDevice _physicalDevice;
        private readonly KhrSwapchain _swapchainExt;

        public SurfaceCapabilitiesQuery(Vk vk, PhysicalDevice pd, KhrSwapchain ext)
        {
            _vk = vk;
            _physicalDevice = pd;
            _swapchainExt = ext;
        }

        /// <summary>查询 surface 能力。</summary>
        public SurfaceCapabilitiesKHR GetCapabilities(SurfaceKHR surface)
        {
            _swapchainExt.GetPhysicalDeviceSurfaceCapabilitiesKHR(_physicalDevice, surface, out var caps);
            return caps;
        }

        /// <summary>查询支持的格式。</summary>
        public SurfaceFormatKHR[] GetFormats(SurfaceKHR surface)
        {
            _swapchainExt.GetPhysicalDeviceSurfaceFormatsKHR(_physicalDevice, surface, out uint count, null);
            var arr = new SurfaceFormatKHR[count];
            _swapchainExt.GetPhysicalDeviceSurfaceFormatsKHR(_physicalDevice, surface, count, arr);
            return arr;
        }

        /// <summary>查询支持的 present mode。</summary>
        public PresentModeKHR[] GetPresentModes(SurfaceKHR surface)
        {
            _swapchainExt.GetPhysicalDeviceSurfacePresentModesKHR(_physicalDevice, surface, out uint count, null);
            var arr = new PresentModeKHR[count];
            _swapchainExt.GetPhysicalDeviceSurfacePresentModesKHR(_physicalDevice, surface, count, arr);
            return arr;
        }

        /// <summary>选择最合适的格式。</summary>
        public SurfaceFormatKHR SelectFormat(SurfaceFormatKHR[] formats, Format preferred, ColorSpaceExt preferredColorSpace)
        {
            if (formats.Length == 1 && formats[0].Format == Format.Undefined)
                return new SurfaceFormatKHR { Format = preferred, ColorSpace = (ColorSpaceKHR)preferredColorSpace };

            foreach (var f in formats)
            {
                if (f.Format == preferred && f.ColorSpace == (ColorSpaceKHR)preferredColorSpace)
                    return f;
            }
            // 退而求其次
            foreach (var f in formats)
            {
                if (f.Format == Format.B8G8R8A8Unorm || f.Format == Format.R8G8B8A8Unorm)
                    return f;
            }
            return formats[0];
        }

        /// <summary>选择最合适的 present mode。</summary>
        public PresentModeKHR SelectPresentMode(PresentModeKHR[] modes, PresentModeStrategy strategy)
        {
            bool hasMailbox = false, hasImmediate = false, hasFifoRelaxed = false;
            foreach (var m in modes)
            {
                if (m == PresentModeKHR.MailboxKhr) hasMailbox = true;
                if (m == PresentModeKHR.ImmediateKhr) hasImmediate = true;
                if (m == PresentModeKHR.FifoRelaxedKhr) hasFifoRelaxed = true;
            }

            return strategy switch
            {
                PresentModeStrategy.NoTearLowLatency => hasMailbox ? PresentModeKHR.MailboxKhr : PresentModeKHR.FifoKhr,
                PresentModeStrategy.VSync => PresentModeKHR.FifoKhr,
                PresentModeStrategy.AllowTearing => hasImmediate ? PresentModeKHR.ImmediateKhr
                                            : (hasFifoRelaxed ? PresentModeKHR.FifoRelaxedKhr : PresentModeKHR.FifoKhr),
                PresentModeStrategy.Adaptive => hasMailbox ? PresentModeKHR.MailboxKhr
                                            : (hasFifoRelaxed ? PresentModeKHR.FifoRelaxedKhr : PresentModeKHR.FifoKhr),
                _ => PresentModeKHR.FifoKhr,
            };
        }

        /// <summary>选择 buffer count。</summary>
        public uint SelectImageCount(SurfaceCapabilitiesKHR caps, uint desired)
        {
            uint count = Math.Max(desired, caps.MinImageCount);
            if (caps.MaxImageCount > 0 && count > caps.MaxImageCount)
                count = caps.MaxImageCount;
            return count;
        }

        /// <summary>选择 extent。</summary>
        public Extent2D SelectExtent(SurfaceCapabilitiesKHR caps, uint width, uint height)
        {
            if (caps.CurrentExtent.Width != uint.MaxValue)
                return caps.CurrentExtent;
            return new Extent2D
            {
                Width = Math.Clamp(width, caps.MinImageExtent.Width, caps.MaxImageExtent.Width),
                Height = Math.Clamp(height, caps.MinImageExtent.Height, caps.MaxImageExtent.Height),
            };
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 5: swapchain 主类
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// swapchain 管理器主类。
    /// </summary>
    public sealed unsafe class VulkanSwapchainManager : IDisposable
    {
        private readonly Vk _vk;
        private readonly Device _device;
        private readonly PhysicalDevice _physicalDevice;
        private readonly KhrSwapchain _swapchainExt;
        private readonly SurfaceCapabilitiesQuery _capsQuery;
        private readonly Queue _presentQueue;
        private readonly uint _presentQueueFamily;

        private SwapchainKHR _swapchain;
        private SurfaceKHR _surface;
        private SwapchainImage[] _images;
        private uint _currentImageIndex;
        private SwapchainCreateInfo _createInfo;
        private bool _recreatePending;

        private readonly SwapchainStats _stats;
        private readonly Stopwatch _frameStopwatch = new();
        private readonly List<double> _frameTimes = new();
        private const int MaxFrameTimeSamples = 120;

        public SwapchainKHR Swapchain => _swapchain;
        public SurfaceKHR Surface => _surface;
        public SwapchainImage[] Images => _images;
        public uint CurrentImageIndex => _currentImageIndex;
        public uint ImageCount => (uint)(_images?.Length ?? 0);
        public bool NeedsRecreate => _recreatePending;
        public bool IsValid => _swapchain.Handle != 0;
        public ref SwapchainStats Stats => ref _stats;

        public Format ImageFormat => _createInfo.ImageFormat;
        public ColorSpaceExt ColorSpace => _createInfo.ColorSpace;
        public uint Width => _createInfo.Width;
        public uint Height => _createInfo.Height;

        public VulkanSwapchainManager(Vk vk, Device device, PhysicalDevice physicalDevice,
                                      Queue presentQueue, uint presentQueueFamily)
        {
            _vk = vk;
            _device = device;
            _physicalDevice = physicalDevice;
            _presentQueue = presentQueue;
            _presentQueueFamily = presentQueueFamily;

            if (!vk.TryGetDeviceExtension(device, out _swapchainExt))
                throw new InvalidOperationException("VK_KHR_swapchain 不可用");

            _capsQuery = new SurfaceCapabilitiesQuery(vk, physicalDevice, _swapchainExt);
            _stats = new SwapchainStats();
        }

        /// <summary>创建 swapchain。</summary>
        public void Create(SurfaceKHR surface, in SwapchainCreateInfo ci)
        {
            _surface = surface;
            _createInfo = ci;

            var caps = _capsQuery.GetCapabilities(surface);
            var formats = _capsQuery.GetFormats(surface);
            var presentModes = _capsQuery.GetPresentModes(surface);

            var selectedFormat = _capsQuery.SelectFormat(formats, ci.ImageFormat, ci.ColorSpace);
            var selectedMode = _capsQuery.SelectPresentMode(presentModes, ci.PresentMode);
            uint imageCount = _capsQuery.SelectImageCount(caps, ci.BufferCount);
            var extent = _capsQuery.SelectExtent(caps, ci.Width, ci.Height);

            _createInfo.Width = extent.Width;
            _createInfo.Height = extent.Height;
            _createInfo.ImageFormat = selectedFormat.Format;
            _createInfo.ColorSpace = (ColorSpaceExt)selectedFormat.ColorSpace;

            var createInfo = new SwapchainCreateInfoKHR
            {
                SType = StructureType.SwapchainCreateInfoKhr,
                Surface = surface,
                MinImageCount = imageCount,
                ImageFormat = selectedFormat.Format,
                ImageColorSpace = selectedFormat.ColorSpace,
                ImageExtent = extent,
                ImageArrayLayers = 1,
                ImageUsage = ci.ImageUsage,
                ImageSharingMode = SharingMode.Exclusive,
                QueueFamilyIndexCount = 0,
                PQueueFamilyIndices = null,
                PreTransform = ci.PreTransform,
                CompositeAlpha = ci.CompositeAlpha,
                PresentMode = selectedMode,
                Clipped = true,
                OldSwapchain = _swapchain,
            };

            fixed (SwapchainKHR* pSwap = &_swapchain)
            {
                var r = _swapchainExt.CreateSwapchain(_device, createInfo, null, pSwap);
                if (r != Result.Success)
                    throw new VulkanException(r, "CreateSwapchain 失败");
            }

            // 销毁老的 swapchain
            if (createInfo.OldSwapchain.Handle != 0)
            {
                // 等待 GPU 完成
                _vk.DeviceWaitIdle(_device);
                foreach (var img in _images)
                {
                    if (img.View.Handle != 0)
                        _vk.DestroyImageView(_device, img.View, null);
                }
                _swapchainExt.DestroySwapchain(_device, createInfo.OldSwapchain, null);
            }

            // 获取 swapchain images
            _swapchainExt.GetSwapchainImages(_device, _swapchain, out uint imgCount, null);
            var imgs = new Image[imgCount];
            _swapchainExt.GetSwapchainImages(_device, _swapchain, imgCount, imgs);

            _images = new SwapchainImage[imgCount];
            for (uint i = 0; i < imgCount; i++)
            {
                var viewInfo = new ImageViewCreateInfo
                {
                    SType = StructureType.ImageViewCreateInfo,
                    Image = imgs[i],
                    ViewType = ImageViewType.View2D,
                    Format = selectedFormat.Format,
                    Components = new ComponentMapping(
                        ComponentSwizzle.Identity, ComponentSwizzle.Identity,
                        ComponentSwizzle.Identity, ComponentSwizzle.Identity),
                    SubresourceRange = new ImageSubresourceRange(
                        ImageAspectFlags.ColorBit, 0, 1, 0, 1),
                };

                ImageView view;
                var r = _vk.CreateImageView(_device, viewInfo, null, &view);
                if (r != Result.Success)
                    throw new VulkanException(r, "CreateImageView swapchain 失败");

                _images[i] = new SwapchainImage
                {
                    Image = imgs[i],
                    View = view,
                    Index = i,
                };
            }

            _stats.RecreateCount++;
            Console.WriteLine($"[Swapchain] 创建: {extent.Width}x{extent.Height} " +
                              $"fmt={selectedFormat.Format} images={imgCount} mode={selectedMode}");
        }

        /// <summary>acquire 下一帧 image。</summary>
        public Result AcquireNextImage(Semaphore imageAvailable, Fence fence = default,
                                       ulong timeoutNs = ulong.MaxValue)
        {
            if (_recreatePending)
                return Result.ErrorOutOfDateKhr;

            var r = _swapchainExt.AcquireNextImage(_device, _swapchain, timeoutNs,
                                                   imageAvailable, fence, out uint idx);
            if (r == Result.Success || r == Result.SuboptimalKhr)
            {
                _currentImageIndex = idx;
                _stats.AcquireCount++;
                _frameStopwatch.Restart();
            }
            else if (r == Result.ErrorOutOfDateKhr)
            {
                _recreatePending = true;
            }
            else
            {
                _stats.AcquireFailures++;
                Console.Error.WriteLine($"[Swapchain] AcquireNextImage 失败: {r}");
            }
            return r;
        }

        /// <summary>present 当前帧。</summary>
        public Result Present(Semaphore renderFinished)
        {
            if (_recreatePending)
                return Result.ErrorOutOfDateKhr;

            fixed (SwapchainKHR* pSwap = &_swapchain)
            fixed (uint* pIdx = &_currentImageIndex)
            fixed (Semaphore* pWait = &renderFinished)
            {
                var info = new PresentInfoKHR
                {
                    SType = StructureType.PresentInfoKhr,
                    WaitSemaphoreCount = 1,
                    PWaitSemaphores = pWait,
                    SwapchainCount = 1,
                    PSwapchains = pSwap,
                    PImageIndices = pIdx,
                };

                var r = _swapchainExt.QueuePresent(_presentQueue, info);
                if (r == Result.Success || r == Result.SuboptimalKhr)
                {
                    _stats.PresentCount++;
                    _frameStopwatch.Stop();
                    double frameMs = _frameStopwatch.Elapsed.TotalMilliseconds;
                    _frameTimes.Add(frameMs);
                    if (_frameTimes.Count > MaxFrameTimeSamples) _frameTimes.RemoveAt(0);
                    if (_frameTimes.Count > 0)
                    {
                        double sum = 0;
                        foreach (var t in _frameTimes) sum += t;
                        _stats.AverageFrameTimeMs = sum / _frameTimes.Count;
                    }
                }
                else if (r == Result.ErrorOutOfDateKhr)
                {
                    _recreatePending = true;
                }
                else
                {
                    _stats.PresentFailures++;
                    Console.Error.WriteLine($"[Swapchain] QueuePresent 失败: {r}");
                }
                return r;
            }
        }

        /// <summary>标记需要重建 (窗口大小变了)。</summary>
        public void RequestRecreate()
        {
            _recreatePending = true;
        }

        /// <summary>重建 swapchain (调用前确保 GPU 完成)。</summary>
        public void Recreate(uint newWidth, uint newHeight)
        {
            _vk.DeviceWaitIdle(_device);
            _createInfo.Width = newWidth;
            _createInfo.Height = newHeight;
            _recreatePending = false;
            Create(_surface, _createInfo);
        }

        /// <summary>设置 HDR 元数据 (VK_EXT_hdr_metadata)。</summary>
        public void SetHdrMetadata(HdrMetadata metadata)
        {
            if (!_createInfo.EnableHdrMetadata) return;
            // VK_EXT_hdr_metadata 需要扩展函数
            // 这里只给骨架
            Console.WriteLine($"[Swapchain] 设置 HDR metadata: MaxCLL={metadata.MaxContentLightLevel}nits");
        }

        /// <summary>获取当前帧的 image。</summary>
        public SwapchainImage GetCurrentImage()
        {
            return _images != null && _currentImageIndex < _images.Length
                ? _images[_currentImageIndex]
                : default;
        }

        /// <summary>获取统计。</summary>
        public SwapchainStats GetStats() => _stats;

        public void Dispose()
        {
            if (_images != null)
            {
                foreach (var img in _images)
                {
                    if (img.View.Handle != 0)
                        _vk.DestroyImageView(_device, img.View, null);
                }
                _images = null;
            }
            if (_swapchain.Handle != 0)
            {
                _swapchainExt.DestroySwapchain(_device, _swapchain, null);
                _swapchain = default;
            }
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 6: VRR 管理
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>可变刷新率 (VRR) 管理。</summary>
    public sealed class VrrManager
    {
        private readonly VulkanSwapchainManager _swapchain;
        private bool _vrrEnabled;
        private float _targetRefreshRate;

        public bool IsVrrEnabled => _vrrEnabled;
        public float TargetRefreshRate => _targetRefreshRate;

        public VrrManager(VulkanSwapchainManager swapchain)
        {
            _swapchain = swapchain;
        }

        /// <summary>启用 VRR。</summary>
        public bool EnableVrr()
        {
            // 实际需要检查 VK_EXT_swapchain_maintenance1
            // 这里只给骨架
            _vrrEnabled = true;
            return true;
        }

        /// <summary>禁用 VRR。</summary>
        public void DisableVrr()
        {
            _vrrEnabled = false;
        }

        /// <summary>设置目标刷新率 (VRR 模式下)。</summary>
        public void SetTargetRefreshRate(float hz)
        {
            _targetRefreshRate = hz;
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 7: 帧节奏控制 (frame pacing)
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>帧节奏控制。让帧间隔稳定，避免抖动。</summary>
    public sealed class FramePacingController
    {
        private readonly VulkanSwapchainManager _swapchain;
        private double _targetFrameTimeMs = 16.666;  // 60fps
        private double _lastFrameTime;
        private readonly Stopwatch _sw = new();

        public double TargetFrameTimeMs
        {
            get => _targetFrameTimeMs;
            set => _targetFrameTimeMs = value;
        }

        public float TargetFps
        {
            get => (float)(1000.0 / _targetFrameTimeMs);
            set => _targetFrameTimeMs = 1000.0 / value;
        }

        public FramePacingController(VulkanSwapchainManager swapchain)
        {
            _swapchain = swapchain;
            _sw.Start();
        }

        /// <summary>等待到下一帧时间。</summary>
        public void WaitForNextFrame()
        {
            double now = _sw.Elapsed.TotalMilliseconds;
            double elapsed = now - _lastFrameTime;
            if (elapsed < _targetFrameTimeMs)
            {
                int sleepMs = (int)(_targetFrameTimeMs - elapsed);
                if (sleepMs > 1) System.Threading.Thread.Sleep(sleepMs - 1);
                // 精确等待最后 1ms
                while (_sw.Elapsed.TotalMilliseconds - _lastFrameTime < _targetFrameTimeMs)
                {
                    System.Threading.Thread.SpinWait(10);
                }
            }
            _lastFrameTime = _sw.Elapsed.TotalMilliseconds;
        }

        /// <summary>获取当前 fps。</summary>
        public float GetCurrentFps()
        {
            return (float)(_stats.AverageFrameTimeMs > 0 ? 1000.0 / _stats.AverageFrameTimeMs : 0);
        }

        private ref SwapchainStats _stats => ref _swapchain.Stats;
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 8: 版本信息
    // ════════════════════════════════════════════════════════════════════════════

    public static class SwapchainVersion
    {
        public const string Version = "1.0.0";
        public const string Description = "ShanyuEngine Vulkan Swapchain Manager (HDR/VRR/frame pacing)";
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 9: 使用示例
    // ════════════════════════════════════════════════════════════════════════════

    /*
    // === 使用示例 ===
    //
    // var swap = new VulkanSwapchainManager(vk, device, physDevice, presentQueue, presentQueueFamily);
    // var ci = new SwapchainConfigBuilder()
    //     .WithSurface(surface)
    //     .WithSize(1920, 1080)
    //     .WithBufferCount(3)
    //     .WithFormat(Format.B8G8R8A8Unorm, ColorSpaceExt.SrgbNonlinear)
    //     .WithPresentMode(PresentModeStrategy.NoTearLowLatency)
    //     .Build();
    // swap.Create(surface, ci);
    //
    // // 每帧
    // var result = swap.AcquireNextImage(imageAvailableSemaphore);
    // if (result == Result.ErrorOutOfDateKhr) {
    //     swap.Recreate(newWidth, newHeight);
    //     continue;
    // }
    // // ... render ...
    // swap.Present(renderFinishedSemaphore);
    */

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 10: 单元测试桩
    // ════════════════════════════════════════════════════════════════════════════

    public static class SwapchainTestStub
    {
        public static uint TestSelectImageCount(uint min, uint max, uint desired)
        {
            uint c = Math.Max(desired, min);
            if (max > 0 && c > max) c = max;
            return c;
        }

        public static PresentModeKHR TestSelectMode(bool mailbox, bool immediate, bool fifoRelaxed,
                                                    PresentModeStrategy strategy)
        {
            return strategy switch
            {
                PresentModeStrategy.NoTearLowLatency => mailbox ? PresentModeKHR.MailboxKhr : PresentModeKHR.FifoKhr,
                PresentModeStrategy.VSync => PresentModeKHR.FifoKhr,
                PresentModeStrategy.AllowTearing => immediate ? PresentModeKHR.ImmediateKhr
                                                        : (fifoRelaxed ? PresentModeKHR.FifoRelaxedKhr : PresentModeKHR.FifoKhr),
                _ => PresentModeKHR.FifoKhr,
            };
        }
    }
}

// 文件结束 - VulkanSwapchainManager.cs
