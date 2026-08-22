using Vulkan = Silk.NET.Vulkan;
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;

using Semaphore = Silk.NET.Vulkan.Semaphore;
using Buffer = Silk.NET.Vulkan.Buffer;
namespace ShanyuEngine.RHI.Vulkan
{
    // ════════════════════════════════════════════════════════════════
    // Win32 Surface P/Invoke (VK_KHR_win32_surface)
    // ════════════════════════════════════════════════════════════════

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct Win32SurfaceCreateInfoKHR
    {
        public StructureType SType;
        public void* PNext;
        public uint Flags;
        public IntPtr Hinstance;
        public IntPtr Hwnd;
    }

    // ════════════════════════════════════════════════════════════════
    // Vulkan Swapchain
    // Implements RHISwapChain with VK_KHR_swapchain and VK_KHR_win32_surface
    // ════════════════════════════════════════════════════════════════

    public sealed unsafe class VulkanSwapChain : RHISwapChain
    {
        internal readonly VulkanDevice Device;
        internal SurfaceKHR Surface;
        internal SwapchainKHR Swapchain;
        internal SwapchainApi SwapchainApi;
        internal Image[] SwapchainImages;
        internal ImageView[] SwapchainImageViews;
        internal Format SwapchainFormat;
        internal Extent2D SwapchainExtent;
        internal PresentModeKHR PresentMode;

        // 配置缓存（从 RHISwapChainDesc 中提取）
        internal int Width;
        internal int Height;
        internal int BufferCount;
        internal RHIFormat Format;
        internal bool VSync;

        // 后缓冲纹理包装器缓存（避免重复创建）
        private VulkanTexture[]? _backBufferTextures;

        // Synchronization
        internal Semaphore[] ImageAvailableSemaphores;
        internal Semaphore[] RenderFinishedSemaphores;
        internal uint CurrentFrameIndex;
        internal uint CurrentImageIndex;
        internal uint ImageCount;

        // Configuration
        private const int MaxFramesInFlight = 2;
        private bool _disposed;

        // Present queue
        internal Queue PresentQueue;
        private const uint QueueFamilyIgnored = ~0u;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string? lpModuleName);

        [DllImport("vulkan-1.dll", EntryPoint = "vkCreateWin32SurfaceKHR", CallingConvention = CallingConvention.Winapi)]
        private static extern Result CreateWin32SurfaceKHRNative(
            IntPtr instance,
            ref Win32SurfaceCreateInfoKHR createInfo,
            IntPtr allocator,
            out IntPtr surface);

        public VulkanSwapChain(VulkanDevice device, IntPtr hwnd, RHISwapChainDesc desc)
        {
            Device = device;
            Desc = desc;
            Width = desc.Width;
            Height = desc.Height;
            BufferCount = desc.BufferCount;
            Format = desc.Format;
            VSync = desc.VSync;

            // Load swapchain extension
            if (!device.Vk.TryGetDeviceExtension(device.VkInstance, device.VkDevice, out SwapchainApi))
                throw new InvalidOperationException("VK_KHR_swapchain extension not supported");

            // Create Win32 surface
            CreateSurface(hwnd);

            // Find present queue family
            uint presentQueueFamily = FindPresentQueueFamily();
            device.Vk.GetDeviceQueue(device.VkDevice, presentQueueFamily, 0, out PresentQueue);

            // Create swapchain
            CreateSwapchain(desc);

            // Create synchronization objects
            CreateSyncObjects();
        }

        private void CreateSurface(IntPtr hwnd)
        {
            IntPtr hinstance = GetModuleHandle(null);

            Win32SurfaceCreateInfoKHR createInfo = new()
            {
                SType = StructureType.Win32SurfaceCreateInfoKhr,
                PNext = null,
                Flags = 0,
                Hinstance = hinstance,
                Hwnd = hwnd
            };

            Result result = CreateWin32SurfaceKHRNative(
                Device.VkInstance.Handle,
                ref createInfo,
                IntPtr.Zero,
                out IntPtr surfaceHandle);

            VkHelper.Check(result, "Failed to create Win32 surface");
            Surface = new SurfaceKHR(surfaceHandle);
        }

        private uint FindPresentQueueFamily()
        {
            // Get queue family properties
            uint queueFamilyCount = 0;
            Device.Vk.GetPhysicalDeviceQueueFamilyProperties(Device.VkPhysicalDevice, &queueFamilyCount, null);

            var queueFamilies = new QueueFamilyProperties[queueFamilyCount];
            fixed (QueueFamilyProperties* pFamilies = queueFamilies)
            {
                Device.Vk.GetPhysicalDeviceQueueFamilyProperties(Device.VkPhysicalDevice, &queueFamilyCount, pFamilies);
            }

            for (uint i = 0; i < queueFamilyCount; i++)
            {
                // Check for presentation support
                Bool32 supported = Device.Vk.GetPhysicalDeviceWin32PresentationSupportKHR(Device.VkPhysicalDevice, i);
                if (supported)
                    return i;
            }

            throw new InvalidOperationException("No queue family supports presentation");
        }

        private void CreateSwapchain(RHISwapChainDesc desc)
        {
            // Query surface capabilities
            VkHelper.Check(SwapchainApi.GetPhysicalDeviceSurfaceCapabilitiesKHR(
                Device.VkPhysicalDevice, Surface, out SurfaceCapabilitiesKHR capabilities),
                "Failed to query surface capabilities");

            // Query surface formats
            uint formatCount = 0;
            SwapchainApi.GetPhysicalDeviceSurfaceFormatsKHR(Device.VkPhysicalDevice, Surface, &formatCount, null);
            var formats = new SurfaceFormatKHR[formatCount];
            fixed (SurfaceFormatKHR* pFormats = formats)
            {
                SwapchainApi.GetPhysicalDeviceSurfaceFormatsKHR(Device.VkPhysicalDevice, Surface, &formatCount, pFormats);
            }

            // Select surface format
            SurfaceFormatKHR surfaceFormat = formats[0];
            for (int i = 0; i < formats.Length; i++)
            {
                if (formats[i].Format == Format.B8G8R8A8Srgb &&
                    formats[i].ColorSpace == ColorSpaceKHR.SpaceSrgbNonlinearKhr)
                {
                    surfaceFormat = formats[i];
                    break;
                }
            }
            SwapchainFormat = surfaceFormat.Format;

            // Select extent
            if (capabilities.CurrentExtent.Width != uint.MaxValue)
            {
                SwapchainExtent = capabilities.CurrentExtent;
            }
            else
            {
                SwapchainExtent = new Extent2D(
                    Math.Clamp((uint)desc.Width, capabilities.MinImageExtent.Width, capabilities.MaxImageExtent.Width),
                    Math.Clamp((uint)desc.Height, capabilities.MinImageExtent.Height, capabilities.MaxImageExtent.Height));
            }

            // Select present mode
            uint presentModeCount = 0;
            SwapchainApi.GetPhysicalDeviceSurfacePresentModesKHR(Device.VkPhysicalDevice, Surface, &presentModeCount, null);
            var presentModes = new PresentModeKHR[presentModeCount];
            fixed (PresentModeKHR* pModes = presentModes)
            {
                SwapchainApi.GetPhysicalDeviceSurfacePresentModesKHR(Device.VkPhysicalDevice, Surface, &presentModeCount, pModes);
            }

            PresentMode = PresentModeKHR.FifoKhr; // Default: VSync on
            if (!desc.VSync)
            {
                // Try MAILBOX (tear-free, low latency) or IMMEDIATE (lowest latency)
                for (int i = 0; i < presentModes.Length; i++)
                {
                    if (presentModes[i] == PresentModeKHR.MailboxKhr)
                    {
                        PresentMode = PresentModeKHR.MailboxKhr;
                        break;
                    }
                    if (presentModes[i] == PresentModeKHR.ImmediateKhr)
                        PresentMode = PresentModeKHR.ImmediateKhr;
                }
            }

            // Determine image count
            ImageCount = Math.Max((uint)desc.BufferCount, capabilities.MinImageCount);
            if (capabilities.MaxImageCount > 0 && ImageCount > capabilities.MaxImageCount)
                ImageCount = capabilities.MaxImageCount;

            // Create swapchain
            SwapchainCreateInfoKHR createInfo = new()
            {
                SType = StructureType.SwapchainCreateInfoKhr,
                Surface = Surface,
                MinImageCount = ImageCount,
                ImageFormat = surfaceFormat.Format,
                ImageColorSpace = surfaceFormat.ColorSpace,
                ImageExtent = SwapchainExtent,
                ImageArrayLayers = 1,
                ImageUsage = ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.TransferDstBit,
                ImageSharingMode = SharingMode.Exclusive,
                PreTransform = capabilities.CurrentTransform,
                CompositeAlpha = CompositeAlphaFlags.OpaqueBitKhr,
                PresentMode = PresentMode,
                Clipped = true,
                OldSwapchain = default
            };

            VkHelper.Check(SwapchainApi.CreateSwapchain(Device.VkDevice, &createInfo, null, out Swapchain),
                "Failed to create swapchain");

            // Get swapchain images
            uint imageCount = 0;
            SwapchainApi.GetSwapchainImages(Device.VkDevice, Swapchain, &imageCount, null);
            SwapchainImages = new Image[imageCount];
            ImageCount = imageCount;
            fixed (Image* pImages = SwapchainImages)
            {
                SwapchainApi.GetSwapchainImages(Device.VkDevice, Swapchain, &imageCount, pImages);
            }

            // Create image views
            SwapchainImageViews = new ImageView[imageCount];
            for (int i = 0; i < imageCount; i++)
            {
                ImageSubresourceRange range = new()
                {
                    AspectMask = ImageAspectFlags.ColorBit,
                    BaseMipLevel = 0,
                    LevelCount = 1,
                    BaseArrayLayer = 0,
                    LayerCount = 1
                };

                ImageViewCreateInfo viewInfo = new()
                {
                    SType = StructureType.ImageViewCreateInfo,
                    Image = SwapchainImages[i],
                    ViewType = ImageViewType.Type2D,
                    Format = SwapchainFormat,
                    SubresourceRange = range
                };

                VkHelper.Check(Device.Vk.CreateImageView(Device.VkDevice, &viewInfo, null, out SwapchainImageViews[i]),
                    "Failed to create swapchain image view");
            }

            CurrentImageIndex = 0;
            CurrentFrameIndex = 0;
        }

        private void CreateSyncObjects()
        {
            ImageAvailableSemaphores = new Semaphore[MaxFramesInFlight];
            RenderFinishedSemaphores = new Semaphore[MaxFramesInFlight];

            for (int i = 0; i < MaxFramesInFlight; i++)
            {
                SemaphoreCreateInfo createInfo = new()
                {
                    SType = StructureType.SemaphoreCreateInfo
                };

                VkHelper.Check(Device.Vk.CreateSemaphore(Device.VkDevice, &createInfo, null, out ImageAvailableSemaphores[i]),
                    "Failed to create image available semaphore");
                VkHelper.Check(Device.Vk.CreateSemaphore(Device.VkDevice, &createInfo, null, out RenderFinishedSemaphores[i]),
                    "Failed to create render finished semaphore");
            }
        }

        // ── Acquire Next Image ──
        public uint AcquireNextImage(ulong timeout = ulong.MaxValue)
        {
            uint frameIndex = CurrentFrameIndex;

            VkHelper.Check(
                SwapchainApi.AcquireNextImage(Device.VkDevice, Swapchain, timeout,
                    ImageAvailableSemaphores[frameIndex], default, out uint imageIndex),
                "Failed to acquire next swapchain image");

            CurrentImageIndex = imageIndex;
            return imageIndex;
        }

        // ── Present ──
        public void Present(VulkanCommandQueue queue, VulkanCommandList? cmdList = null)
        {
            uint frameIndex = CurrentFrameIndex;
            Semaphore waitSemaphore = RenderFinishedSemaphores[frameIndex];
            Semaphore signalSemaphore = ImageAvailableSemaphores[frameIndex];

            // If we have a command list, submit it with proper semaphores
            if (cmdList != null)
            {
                CommandBuffer cmdBuffer = cmdList.CmdBuffer;
                PipelineStageFlags waitStage = PipelineStageFlags.ColorAttachmentOutputBit;

                SubmitInfo submitInfo = new()
                {
                    SType = StructureType.SubmitInfo,
                    WaitSemaphoreCount = 1,
                    PWaitSemaphores = &signalSemaphore,
                    PWaitDstStageMask = &waitStage,
                    CommandBufferCount = 1,
                    PCommandBuffers = &cmdBuffer,
                    SignalSemaphoreCount = 1,
                    PSignalSemaphores = &waitSemaphore
                };

                VkHelper.Check(Device.Vk.QueueSubmit(queue.VkQueue, 1, &submitInfo, default),
                    "Failed to submit swapchain command buffer");
            }

            // Present
            uint imageIndex = CurrentImageIndex;
            SwapchainKHR swapchain = Swapchain;

            PresentInfoKHR presentInfo = new()
            {
                SType = StructureType.PresentInfoKhr,
                WaitSemaphoreCount = 1,
                PWaitSemaphores = &waitSemaphore,
                SwapchainCount = 1,
                PSwapchains = &swapchain,
                PImageIndices = &imageIndex
            };

            VkHelper.Check(SwapchainApi.QueuePresent(queue.VkQueue, &presentInfo),
                "Failed to present swapchain image");

            CurrentFrameIndex = (CurrentFrameIndex + 1) % MaxFramesInFlight;
        }

        // ── Get Current Image View ──
        public ImageView GetCurrentImageView()
        {
            return SwapchainImageViews[CurrentImageIndex];
        }

        public Image GetCurrentImage()
        {
            return SwapchainImages[CurrentImageIndex];
        }

        // ── Resize / Recreate ──
        public void Resize(uint width, uint height)
        {
            // Wait for device idle
            Device.Vk.DeviceWaitIdle(Device.VkDevice);

            // 清理旧的后缓冲纹理包装器
            if (_backBufferTextures != null)
            {
                for (int i = 0; i < _backBufferTextures.Length; i++)
                    _backBufferTextures[i]?.Dispose();
                _backBufferTextures = null;
            }

            // Destroy old views
            for (int i = 0; i < SwapchainImageViews.Length; i++)
            {
                if (SwapchainImageViews[i].Handle != 0)
                    Device.Vk.DestroyImageView(Device.VkDevice, SwapchainImageViews[i], null);
            }

            // Destroy old swapchain
            if (Swapchain.Handle != 0)
                SwapchainApi.DestroySwapchain(Device.VkDevice, Swapchain, null);

            Width = width;
            Height = height;

            // 更新 Desc 属性
            var desc = new RHISwapChainDesc
            {
                Width = (int)width,
                Height = (int)height,
                BufferCount = BufferCount,
                Format = Format,
                VSync = VSync,
                OutputWindow = Desc.OutputWindow,
                Windowed = Desc.Windowed,
                SwapEffect = Desc.SwapEffect,
                SampleCount = Desc.SampleCount
            };
            Desc = desc;

            CreateSwapchain(desc);
        }

        // ── Transition Layout ──
        public void TransitionToPresentLayout(VulkanCommandList cmdList)
        {
            ImageSubresourceRange range = new()
            {
                AspectMask = ImageAspectFlags.ColorBit,
                BaseMipLevel = 0,
                LevelCount = 1,
                BaseArrayLayer = 0,
                LayerCount = 1
            };

            ImageMemoryBarrier barrier = new()
            {
                SType = StructureType.ImageMemoryBarrier,
                SrcAccessMask = AccessFlags.ColorAttachmentWriteBit,
                DstAccessMask = 0,
                OldLayout = ImageLayout.ColorAttachmentOptimal,
                NewLayout = ImageLayout.PresentSrcKhr,
                SrcQueueFamilyIndex = QueueFamilyIgnored,
                DstQueueFamilyIndex = QueueFamilyIgnored,
                Image = SwapchainImages[CurrentImageIndex],
                SubresourceRange = range
            };

            Device.Vk.CmdPipelineBarrier(cmdList.CmdBuffer,
                PipelineStageFlags.ColorAttachmentOutputBit,
                PipelineStageFlags.BottomOfPipeBit,
                DependencyFlags.None,
                0, null, 0, null, 1, &barrier);
        }

        // ── RHISwapChain 抽象成员实现 ──

        /// <summary>当前后缓冲索引</summary>
        public override int CurrentBackBufferIndex => (int)CurrentImageIndex;

        /// <summary>
        /// 获取指定索引的后缓冲纹理
        /// 创建 VulkanTexture 包装器封装 Swapchain Image
        /// </summary>
        public override RHITexture GetBackBuffer(int index)
        {
            if (index < 0 || index >= SwapchainImages.Length)
                throw new ArgumentOutOfRangeException(nameof(index));

            // 懒初始化后缓冲纹理包装器数组
            if (_backBufferTextures == null || _backBufferTextures.Length != SwapchainImages.Length)
            {
                _backBufferTextures = new VulkanTexture[SwapchainImages.Length];
            }

            if (_backBufferTextures[index] == null)
            {
                var texDesc = new RHITextureDesc
                {
                    Width = (int)SwapchainExtent.Width,
                    Height = (int)SwapchainExtent.Height,
                    DepthOrArraySize = 1,
                    MipLevels = 1,
                    Format = Format,
                    Dimension = RHIResourceDimension.Texture2D,
                    Flags = RHITextureFlags.RenderTarget
                };

                _backBufferTextures[index] = new VulkanTexture(Device, SwapchainImages[index], texDesc, SwapchainFormat);
            }

            return _backBufferTextures[index];
        }

        /// <summary>
        /// 获取指定索引后缓冲的 RTV 描述符句柄
        /// </summary>
        public override RHIDescriptorHandle GetBackBufferRTV(int index)
        {
            var texture = (VulkanTexture)GetBackBuffer(index);
            ImageView rtv = texture.GetRTV(Device);
            return new RHIDescriptorHandle
            {
                CpuHandle = (IntPtr)rtv.Handle,
                GpuHandle = IntPtr.Zero,
                Index = (int)rtv.Handle
            };
        }

        /// <summary>
        /// 获取当前后缓冲纹理
        /// </summary>
        public override RHITexture GetCurrentBackBuffer()
        {
            return GetBackBuffer((int)CurrentImageIndex);
        }

        /// <summary>
        /// 获取当前后缓冲的 RTV 描述符句柄
        /// </summary>
        public override RHIDescriptorHandle GetCurrentBackBufferRTV()
        {
            return GetBackBufferRTV((int)CurrentImageIndex);
        }

        /// <summary>
        /// 抽象方法实现：呈现当前帧到屏幕 (bool vsync)
        /// </summary>
        public override void Present(bool vsync)
        {
            Present(vsync ? 1 : 0);
        }

        /// <summary>
        /// 呈现当前帧到屏幕
        /// </summary>
        /// <param name="vsync">是否启用垂直同步（1=启用, 0=禁用）</param>
        public override void Present(int vsync)
        {
            uint frameIndex = CurrentFrameIndex;
            // 使用 ImageAvailable 信号量确保图像已就绪
            Semaphore waitSemaphore = ImageAvailableSemaphores[frameIndex];

            uint imageIndex = CurrentImageIndex;
            SwapchainKHR swapchain = Swapchain;

            PresentInfoKHR presentInfo = new()
            {
                SType = StructureType.PresentInfoKhr,
                WaitSemaphoreCount = 1,
                PWaitSemaphores = &waitSemaphore,
                SwapchainCount = 1,
                PSwapchains = &swapchain,
                PImageIndices = &imageIndex
            };

            SwapchainApi.QueuePresent(PresentQueue, &presentInfo);

            CurrentFrameIndex = (CurrentFrameIndex + 1) % MaxFramesInFlight;
        }

        /// <summary>
        /// 调整交换链大小
        /// </summary>
        public override void Resize(int width, int height)
        {
            Resize((uint)width, (uint)height);
        }

        // ── Dispose ──
        public override void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            Device.Vk.DeviceWaitIdle(Device.VkDevice);

            // 清理后缓冲纹理包装器（销毁它们创建的 ImageView）
            if (_backBufferTextures != null)
            {
                for (int i = 0; i < _backBufferTextures.Length; i++)
                    _backBufferTextures[i]?.Dispose();
                _backBufferTextures = null;
            }

            for (int i = 0; i < SwapchainImageViews?.Length; i++)
            {
                if (SwapchainImageViews[i].Handle != 0)
                    Device.Vk.DestroyImageView(Device.VkDevice, SwapchainImageViews[i], null);
            }

            if (Swapchain.Handle != 0)
                SwapchainApi.DestroySwapchain(Device.VkDevice, Swapchain, null);

            for (int i = 0; i < ImageAvailableSemaphores?.Length; i++)
            {
                if (ImageAvailableSemaphores[i].Handle != 0)
                    Device.Vk.DestroySemaphore(Device.VkDevice, ImageAvailableSemaphores[i], null);
                if (RenderFinishedSemaphores[i].Handle != 0)
                    Device.Vk.DestroySemaphore(Device.VkDevice, RenderFinishedSemaphores[i], null);
            }

            if (Surface.Handle != 0)
                Device.Vk.DestroySurfaceKHR(Device.VkInstance, Surface, null);
        }
    }
}
