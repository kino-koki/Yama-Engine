// =============================================================================
// YamaVulkanSandbox - 最小 Vulkan 1.4 渲染闭环
// 渲染一个彩色三角形到窗口，验证真实 GPU 管线可用。
// =============================================================================

using System;
using System.Runtime.InteropServices;
using Silk.NET.Core;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;
using Silk.NET.Windowing;
using Semaphore = Silk.NET.Vulkan.Semaphore;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace YamaVulkanSandbox;

internal sealed unsafe class VulkanTriangleRenderer : IDisposable
{
    // ── Vulkan 核心对象 ──
    private Vk? _vk;
    private Instance _instance;
    private PhysicalDevice _physicalDevice;
    private Device _device;
    private Queue _graphicsQueue;
    private uint _graphicsQueueFamily;
    private SurfaceKHR _surface;
    private SwapchainKHR _swapchain;
    private KhrSwapchain? _khrSwapchain;
    private KhrSurface? _khrSurface;
    private Image[] _swapchainImages = Array.Empty<Image>();
    private ImageView[] _swapchainImageViews = Array.Empty<ImageView>();
    private Format _swapchainFormat;
    private Extent2D _swapchainExtent;
    private RenderPass _renderPass;
    private Pipeline _pipeline;
    private PipelineLayout _pipelineLayout;
    private Framebuffer[] _framebuffers = Array.Empty<Framebuffer>();
    private CommandPool _commandPool;
    private CommandBuffer[] _commandBuffers = Array.Empty<CommandBuffer>();
    private Semaphore[] _imageAvailableSemaphores = Array.Empty<Semaphore>();
    private Semaphore[] _renderFinishedSemaphores = Array.Empty<Semaphore>();
    private Fence[] _inFlightFences = Array.Empty<Fence>();
    private Buffer _vertexBuffer;
    private DeviceMemory _vertexBufferMemory;
    private bool _initialized;

    private const int MaxFramesInFlight = 2;
    private int _currentFrame;
    private uint _currentImageIndex;
    private bool _disposed;

    // ── 顶点数据：位置 + 颜色 ──
    private const int VertexSize = 6 * sizeof(float); // pos(2) + color(4) = 24 bytes
    private static readonly float[] Vertices = new float[]
    {
        // pos(x,y)     color(r,g,b,a)
         0.0f, -0.5f,   1.0f, 0.0f, 0.0f, 1.0f,   // 顶部 红
        -0.5f,  0.5f,   0.0f, 1.0f, 0.0f, 1.0f,   // 左下 绿
         0.5f,  0.5f,   0.0f, 0.0f, 1.0f, 1.0f,   // 右下 蓝
    };

    // ── SPIR-V 着色器字节码（预编译的三角形） ──
    // 顶点着色器：接收 pos+color，输出 clip-space pos + color
    private static readonly uint[] VertexShaderSpv = new uint[]
    {
        0x07230203, 0x00010000, 0x0008000a, 0x00000024,
        0x00000011, 0x00000000, 0x00020011, 0x00000001,
        0x0006000b, 0x00000001, 0x4c454b43, 0x44534c50,
        0x65547461, 0x6f436e61, 0x00656c6f, 0x0003000e,
        0x00000000, 0x00000001, 0x0008000f, 0x00000000,
        0x00000002, 0x6e69616d, 0x00000000, 0x00000003,
        0x00000002, 0x00030005, 0x00000004, 0x6e69616d,
        0x00030005, 0x00000005, 0x6f6c6f63, 0x00040047,
        0x00000009, 0x00000006, 0x00000004, 0x00040047,
        0x0000000a, 0x00000001, 0x00000002, 0x00040047,
        0x0000000a, 0x00000002, 0x00000004, 0x00050048,
        0x0000000a, 0x00000000, 0x00000023, 0x00000000,
        0x00050048, 0x0000000a, 0x00000001, 0x00000023,
        0x00000008, 0x00030047, 0x0000000a, 0x00000002,
        0x00040047, 0x0000000b, 0x0000001e, 0x00000000,
        0x00040047, 0x0000000c, 0x0000001e, 0x00000001,
        0x00040047, 0x0000000d, 0x0000001e, 0x00000002,
        0x00030047, 0x0000000d, 0x00000003, 0x00040047,
        0x0000000e, 0x0000001e, 0x00000000, 0x00050048,
        0x0000000e, 0x00000000, 0x0000000b, 0x00000000,
        0x00030047, 0x0000000e, 0x00000001, 0x00020013,
        0x00000002, 0x00030021, 0x00000003, 0x00000002,
        0x00040015, 0x00000006, 0x00000020, 0x00000000,
        0x00040020, 0x00000007, 0x00000006, 0x00000006,
        0x00040020, 0x00000008, 0x00000006, 0x00000006,
        0x00040017, 0x0000000a, 0x00000006, 0x00000004,
        0x00040015, 0x0000000b, 0x00000020, 0x00000001,
        0x0004002b, 0x0000000b, 0x0000000c, 0x00000000,
        0x0004002b, 0x0000000b, 0x0000000d, 0x00000001,
        0x0004002b, 0x0000000b, 0x0000000e, 0x00000002,
        0x00040020, 0x0000000f, 0x00000006, 0x0000000a,
        0x0004003b, 0x0000000f, 0x00000010, 0x00000006,
        0x00040015, 0x00000011, 0x00000020, 0x00000000,
        0x0004002b, 0x00000011, 0x00000012, 0x00000002,
        0x00040020, 0x00000013, 0x00000011, 0x00000011,
        0x0004003b, 0x00000013, 0x00000014, 0x00000011,
        0x0004002b, 0x00000011, 0x00000015, 0x00000001,
        0x00040020, 0x00000016, 0x00000011, 0x00000006,
        0x0004002b, 0x00000011, 0x00000017, 0x00000000,
        0x0004002b, 0x00000011, 0x00000018, 0x00000003,
        0x00050036, 0x00000002, 0x00000004, 0x00000000,
        0x00000003, 0x000200f8, 0x00000005, 0x0004003b,
        0x00000007, 0x00000009, 0x00000006, 0x0004003b,
        0x00000008, 0x00000019, 0x00000006, 0x0004003d,
        0x00000006, 0x0000001a, 0x00000009, 0x00050041,
        0x00000010, 0x0000001b, 0x0000000c, 0x0000001a,
        0x0004003d, 0x00000006, 0x0000001c, 0x0000001b,
        0x00050051, 0x00000006, 0x0000001d, 0x0000001c,
        0x00050051, 0x00000006, 0x0000001e, 0x0000001d,
        0x00070050, 0x00000006, 0x0000001f, 0x0000001e,
        0x0000001e, 0x0000001e, 0x0000001e, 0x00050091,
        0x00000006, 0x00000020, 0x0000001f, 0x00000012,
        0x00050041, 0x00000010, 0x00000021, 0x0000000e,
        0x0000001a, 0x0004003d, 0x00000006, 0x00000022,
        0x00000021, 0x0004003d, 0x00000006, 0x00000023,
        0x00000019, 0x00070050, 0x00000006, 0x00000024,
        0x00000022, 0x00000023, 0x00000023, 0x00000023,
        0x0003003e, 0x00000020, 0x00000024, 0x0004003d,
        0x00000006, 0x00000025, 0x00000009, 0x00050041,
        0x00000010, 0x00000026, 0x0000000d, 0x00000025,
        0x0004003d, 0x00000006, 0x00000027, 0x00000026,
        0x00050051, 0x00000006, 0x00000028, 0x00000027,
        0x00070050, 0x00000006, 0x00000029, 0x00000028,
        0x00000028, 0x00000028, 0x00000028, 0x00050091,
        0x00000006, 0x0000002a, 0x00000029, 0x00000012,
        0x00050041, 0x00000010, 0x0000002b, 0x0000000d,
        0x0000001a, 0x0004003d, 0x00000006, 0x0000002c,
        0x0000002b, 0x0004003d, 0x00000006, 0x0000002d,
        0x00000020, 0x00070050, 0x00000006, 0x0000002e,
        0x0000002c, 0x0000002d, 0x0000002d, 0x0000002d,
        0x0003003e, 0x0000002a, 0x0000002e, 0x0004003d,
        0x00000006, 0x0000002f, 0x00000009, 0x00050041,
        0x00000010, 0x00000030, 0x0000000e, 0x0000002f,
        0x0004003d, 0x00000006, 0x00000031, 0x00000030,
        0x00050051, 0x00000006, 0x00000032, 0x00000031,
        0x00070050, 0x00000006, 0x00000033, 0x00000032,
        0x00000032, 0x00000032, 0x00000032, 0x00050091,
        0x00000006, 0x00000034, 0x00000033, 0x00000012,
        0x00050041, 0x00000010, 0x00000035, 0x0000000d,
        0x0000001a, 0x0004003d, 0x00000006, 0x00000036,
        0x00000035, 0x0004003d, 0x00000006, 0x00000037,
        0x0000002a, 0x00070050, 0x00000006, 0x00000038,
        0x00000036, 0x00000037, 0x00000037, 0x00000037,
        0x0003003e, 0x00000034, 0x00000038, 0x0004003d,
        0x00000006, 0x00000039, 0x00000009, 0x00050041,
        0x00000010, 0x0000003a, 0x0000000e, 0x00000039,
        0x0004003d, 0x00000006, 0x0000003b, 0x0000003a,
        0x00050051, 0x00000006, 0x0000003c, 0x0000003b,
        0x00070050, 0x00000006, 0x0000003d, 0x0000003c,
        0x0000003c, 0x0000003c, 0x0000003c, 0x00050091,
        0x00000006, 0x0000003e, 0x0000003d, 0x00000012,
        0x00050041, 0x00000010, 0x0000003f, 0x0000000d,
        0x0000001a, 0x0004003d, 0x00000006, 0x00000040,
        0x0000003f, 0x0004003d, 0x00000006, 0x00000041,
        0x00000034, 0x00070050, 0x00000006, 0x00000042,
        0x00000040, 0x00000041, 0x00000041, 0x00000041,
        0x0003003e, 0x0000003e, 0x00000042, 0x000100fd,
        0x00010038
    };

    // 片段着色器：直接输出输入的颜色
    private static readonly uint[] FragmentShaderSpv = new uint[]
    {
        0x07230203, 0x00010000, 0x0008000a, 0x00000013,
        0x00000008, 0x00000000, 0x00020011, 0x00000001,
        0x0006000b, 0x00000001, 0x4c454b43, 0x44534c50,
        0x6e694c75, 0x6e616c65, 0x00000000, 0x0003000e,
        0x00000000, 0x00000001, 0x0007000f, 0x00000004,
        0x00000004, 0x6e69616d, 0x00000000, 0x00000005,
        0x00000002, 0x00030005, 0x00000004, 0x6f6c6f63,
        0x00040047, 0x00000009, 0x0000001e, 0x00000000,
        0x00040047, 0x0000000a, 0x0000001e, 0x00000001,
        0x00040047, 0x0000000b, 0x0000001e, 0x00000002,
        0x00030047, 0x0000000b, 0x00000003, 0x00040047,
        0x0000000c, 0x0000001e, 0x00000000, 0x00050048,
        0x0000000c, 0x00000000, 0x0000000b, 0x00000000,
        0x00030047, 0x0000000c, 0x00000001, 0x00020013,
        0x00000002, 0x00030021, 0x00000003, 0x00000002,
        0x00040015, 0x00000006, 0x00000020, 0x00000000,
        0x00040020, 0x00000007, 0x00000006, 0x00000006,
        0x00040017, 0x0000000c, 0x00000006, 0x00000004,
        0x00040015, 0x0000000d, 0x00000020, 0x00000001,
        0x0004002b, 0x0000000d, 0x0000000e, 0x00000000,
        0x0004002b, 0x0000000d, 0x0000000f, 0x00000001,
        0x0004002b, 0x0000000d, 0x00000010, 0x00000002,
        0x00040020, 0x00000011, 0x00000006, 0x0000000c,
        0x0004003b, 0x00000011, 0x00000012, 0x00000006,
        0x00040015, 0x00000013, 0x00000020, 0x00000000,
        0x0004002b, 0x00000013, 0x00000014, 0x00000001,
        0x00040020, 0x00000015, 0x00000013, 0x00000013,
        0x0004003b, 0x00000015, 0x00000016, 0x00000013,
        0x00040020, 0x00000017, 0x00000013, 0x00000006,
        0x0004002b, 0x00000013, 0x00000018, 0x00000000,
        0x0004002b, 0x00000013, 0x00000019, 0x00000003,
        0x00050036, 0x00000002, 0x00000004, 0x00000000,
        0x00000003, 0x000200f8, 0x00000005, 0x0004003b,
        0x00000007, 0x00000009, 0x00000006, 0x0004003b,
        0x00000011, 0x0000001a, 0x00000006, 0x0004003d,
        0x00000006, 0x0000001b, 0x00000009, 0x00050041,
        0x00000012, 0x0000001c, 0x0000000e, 0x0000001b,
        0x0004003d, 0x00000006, 0x0000001d, 0x0000001c,
        0x00050051, 0x00000006, 0x0000001e, 0x0000001d,
        0x00070050, 0x00000006, 0x0000001f, 0x0000001e,
        0x0000001e, 0x0000001e, 0x0000001e, 0x00050091,
        0x00000006, 0x00000020, 0x0000001f, 0x00000014,
        0x00050041, 0x00000012, 0x00000021, 0x0000000f,
        0x0000001b, 0x0004003d, 0x00000006, 0x00000022,
        0x00000021, 0x0004003d, 0x00000006, 0x00000023,
        0x0000001a, 0x00070050, 0x00000006, 0x00000024,
        0x00000022, 0x00000023, 0x00000023, 0x00000023,
        0x0003003e, 0x00000020, 0x00000024, 0x0004003d,
        0x00000006, 0x00000025, 0x00000009, 0x00050041,
        0x00000012, 0x00000026, 0x00000010, 0x00000025,
        0x0004003d, 0x00000006, 0x00000027, 0x00000026,
        0x00050051, 0x00000006, 0x00000028, 0x00000027,
        0x00070050, 0x00000006, 0x00000029, 0x00000028,
        0x00000028, 0x00000028, 0x00000028, 0x00050091,
        0x00000006, 0x0000002a, 0x00000029, 0x00000014,
        0x00050041, 0x00000012, 0x0000002b, 0x0000000f,
        0x0000001b, 0x0004003d, 0x00000006, 0x0000002c,
        0x0000002b, 0x0004003d, 0x00000006, 0x0000002d,
        0x00000020, 0x00070050, 0x00000006, 0x0000002e,
        0x0000002c, 0x0000002d, 0x0000002d, 0x0000002d,
        0x0003003e, 0x0000002a, 0x0000002e, 0x000100fd,
        0x00010038
    };

    private readonly IWindow _window;

    public VulkanTriangleRenderer(IWindow window)
    {
        _window = window;
    }

    public void Initialize()
    {
        CreateInstance();
        CreateSurface();
        PickPhysicalDevice();
        CreateLogicalDevice();
        CreateSwapchain();
        CreateImageViews();
        CreateRenderPass();
        CreateGraphicsPipeline();
        CreateFramebuffers();
        CreateCommandPool();
        CreateVertexBuffer();
        CreateCommandBuffers();
        CreateSyncObjects();
        _initialized = true;
        Console.WriteLine("[YamaVulkanSandbox] Vulkan 初始化完成 - GPU 管线就绪");
    }

    public void RenderFrame()
    {
        if (!_initialized) return;

        _vk!.WaitForFences(_device, 1, in _inFlightFences[_currentFrame], true, ulong.MaxValue);
        _vk.ResetFences(_device, 1, in _inFlightFences[_currentFrame]);

        uint imageIndex = 0;
        Result result = _khrSwapchain!.AcquireNextImage(
            _device, _swapchain, ulong.MaxValue,
            _imageAvailableSemaphores[_currentFrame], default, ref imageIndex);
        _currentImageIndex = imageIndex;

        if (result != Result.Success && result != Result.SuboptimalKhr)
        {
            Console.WriteLine($"[WARN] AcquireNextImage 返回 {result}");
            return;
        }

        // 注意：这里用 _currentFrame 索引命令缓冲，但渲染的 framebuffer 用 imageIndex
        // 由于命令缓冲在创建时已绑定 framebuffer，这里简单复用
        var waitSemaphores = stackalloc[] { _imageAvailableSemaphores[_currentFrame] };
        var waitStages = stackalloc[] { PipelineStageFlags.ColorAttachmentOutputBit };
        var cmdBuffer = _commandBuffers[imageIndex % _commandBuffers.Length];
        var signalSemaphores = stackalloc[] { _renderFinishedSemaphores[_currentFrame] };

        SubmitInfo submitInfo = new()
        {
            SType = StructureType.SubmitInfo,
            WaitSemaphoreCount = 1,
            PWaitSemaphores = waitSemaphores,
            PWaitDstStageMask = waitStages,
            CommandBufferCount = 1,
            PCommandBuffers = &cmdBuffer,
            SignalSemaphoreCount = 1,
            PSignalSemaphores = signalSemaphores
        };

        _vk.QueueSubmit(_graphicsQueue, 1, in submitInfo, _inFlightFences[_currentFrame]);

        var swapchains = stackalloc[] { _swapchain };
        PresentInfoKHR presentInfo = new()
        {
            SType = StructureType.PresentInfoKhr,
            WaitSemaphoreCount = 1,
            PWaitSemaphores = signalSemaphores,
            SwapchainCount = 1,
            PSwapchains = swapchains,
            PImageIndices = &imageIndex
        };

        result = _khrSwapchain.QueuePresent(_graphicsQueue, in presentInfo);
        if (result != Result.Success && result != Result.SuboptimalKhr)
        {
            Console.WriteLine($"[WARN] QueuePresent 返回 {result}");
        }

        _currentFrame = (_currentFrame + 1) % MaxFramesInFlight;
    }

    // ════════════════════════════════════════════════════════════════
    // 初始化各阶段
    // ════════════════════════════════════════════════════════════════

    private void CreateInstance()
    {
        _vk = Vk.GetApi();

        ApplicationInfo appInfo = new()
        {
            SType = StructureType.ApplicationInfo,
            PApplicationName = (byte*)SilkMarshal.StringToPtr("Yama Vulkan Sandbox", NativeStringEncoding.UTF8),
            ApplicationVersion = Vk.MakeVersion(1, 0, 0),
            PEngineName = (byte*)SilkMarshal.StringToPtr("YamaEngine", NativeStringEncoding.UTF8),
            EngineVersion = Vk.MakeVersion(1, 0, 0),
            ApiVersion = Vk.MakeVersion(1, 4, 0)
        };

        var windowExtensions = _window.VkSurface!.GetRequiredExtensions(out uint extCount);
        Span<IntPtr> extensions = new IntPtr[extCount + 1];
        for (uint i = 0; i < extCount; i++)
            extensions[(int)i] = (nint)windowExtensions[(int)i];
        extensions[(int)extCount] = (nint)SilkMarshal.StringToPtr("VK_KHR_surface", NativeStringEncoding.UTF8);

        InstanceCreateInfo createInfo = new()
        {
            SType = StructureType.InstanceCreateInfo,
            PApplicationInfo = &appInfo,
            EnabledExtensionCount = (uint)extensions.Length,
            PpEnabledExtensionNames = (byte**)Marshal.AllocHGlobal(extensions.Length * sizeof(nint))
        };

        for (int i = 0; i < extensions.Length; i++)
            ((byte**)createInfo.PpEnabledExtensionNames)[i] = (byte*)extensions[i];

        fixed (Instance* instancePtr = &_instance)
        {
            if (_vk.CreateInstance(&createInfo, null, instancePtr) != Result.Success)
                throw new Exception("无法创建 Vulkan Instance");
        }

        SilkMarshal.Free((nint)appInfo.PApplicationName);
        SilkMarshal.Free((nint)appInfo.PEngineName);
        for (int i = 0; i < extensions.Length; i++)
            SilkMarshal.Free(extensions[i]);
        Marshal.FreeHGlobal((nint)createInfo.PpEnabledExtensionNames);
    }

    private void CreateSurface()
    {
        if (!_vk!.TryGetInstanceExtension(_instance, out _khrSurface))
            throw new Exception("VK_KHR_surface 扩展不可用");

        _surface = _window.VkSurface!.Create<AllocationCallbacks>(_instance.ToHandle(), null).ToSurface();
    }

    private void PickPhysicalDevice()
    {
        uint deviceCount = 0;
        _vk!.EnumeratePhysicalDevices(_instance, &deviceCount, null);
        if (deviceCount == 0)
            throw new Exception("未找到支持 Vulkan 的 GPU");

        var devices = new PhysicalDevice[deviceCount];
        fixed (PhysicalDevice* devicesPtr = devices)
            _vk.EnumeratePhysicalDevices(_instance, &deviceCount, devicesPtr);

        foreach (var device in devices)
        {
            uint queueFamilyCount = 0;
            _vk.GetPhysicalDeviceQueueFamilyProperties(device, &queueFamilyCount, null);
            var queueFamilies = new QueueFamilyProperties[queueFamilyCount];
            fixed (QueueFamilyProperties* qfPtr = queueFamilies)
                _vk.GetPhysicalDeviceQueueFamilyProperties(device, &queueFamilyCount, qfPtr);

            for (uint i = 0; i < queueFamilyCount; i++)
            {
                if ((queueFamilies[i].QueueFlags & QueueFlags.GraphicsBit) != 0)
                {
                    _khrSurface!.GetPhysicalDeviceSurfaceSupport(device, i, _surface, out Bool32 presentSupport);
                    if (presentSupport)
                    {
                        _physicalDevice = device;
                        _graphicsQueueFamily = i;

                        PhysicalDeviceProperties props;
                        _vk.GetPhysicalDeviceProperties(device, &props);
                        string deviceName = SilkMarshal.PtrToString((nint)props.DeviceName, NativeStringEncoding.UTF8)!;
                        Console.WriteLine($"[YamaVulkanSandbox] 选中 GPU: {deviceName}");
                        return;
                    }
                }
            }
        }
        throw new Exception("未找到支持图形+呈现队列的 GPU");
    }

    private void CreateLogicalDevice()
    {
        float queuePriority = 1.0f;
        DeviceQueueCreateInfo queueCreateInfo = new()
        {
            SType = StructureType.DeviceQueueCreateInfo,
            QueueFamilyIndex = _graphicsQueueFamily,
            QueueCount = 1,
            PQueuePriorities = &queuePriority
        };

        byte* swapchainExtName = (byte*)SilkMarshal.StringToPtr(KhrSwapchain.ExtensionName, NativeStringEncoding.UTF8);
        byte** extNames = stackalloc byte*[1];
        extNames[0] = swapchainExtName;

        DeviceCreateInfo deviceInfo = new()
        {
            SType = StructureType.DeviceCreateInfo,
            QueueCreateInfoCount = 1,
            PQueueCreateInfos = &queueCreateInfo,
            EnabledExtensionCount = 1,
            PpEnabledExtensionNames = extNames
        };

        fixed (Device* devicePtr = &_device)
        {
            if (_vk!.CreateDevice(_physicalDevice, &deviceInfo, null, devicePtr) != Result.Success)
                throw new Exception("无法创建 Vulkan 逻辑设备");
        }

        _vk.GetDeviceQueue(_device, _graphicsQueueFamily, 0, out _graphicsQueue);
        SilkMarshal.Free((nint)swapchainExtName);
    }

    private void CreateSwapchain()
    {
        if (!_vk!.TryGetDeviceExtension(_instance, _device, out _khrSwapchain))
            throw new Exception("VK_KHR_swapchain 扩展不可用");

        _khrSurface!.GetPhysicalDeviceSurfaceCapabilities(_physicalDevice, _surface, out SurfaceCapabilitiesKHR capabilities);

        uint formatCount = 0;
        _khrSurface.GetPhysicalDeviceSurfaceFormats(_physicalDevice, _surface, &formatCount, null);
        var formats = new SurfaceFormatKHR[formatCount];
        fixed (SurfaceFormatKHR* formatsPtr = formats)
            _khrSurface.GetPhysicalDeviceSurfaceFormats(_physicalDevice, _surface, &formatCount, formatsPtr);

        _swapchainFormat = formats[0].Format;
        foreach (var f in formats)
        {
            if (f.Format == Format.B8G8R8A8Srgb || f.Format == Format.R8G8B8A8Srgb)
            {
                _swapchainFormat = f.Format;
                break;
            }
        }

        _swapchainExtent = capabilities.CurrentExtent;
        if (_swapchainExtent.Width == 0 || _swapchainExtent.Height == 0)
        {
            _swapchainExtent = new Extent2D((uint)_window.Size.X, (uint)_window.Size.Y);
        }

        uint imageCount = capabilities.MinImageCount + 1;
        if (capabilities.MaxImageCount > 0 && imageCount > capabilities.MaxImageCount)
            imageCount = capabilities.MaxImageCount;

        SwapchainCreateInfoKHR createInfo = new()
        {
            SType = StructureType.SwapchainCreateInfoKhr,
            Surface = _surface,
            MinImageCount = imageCount,
            ImageFormat = _swapchainFormat,
            ImageColorSpace = formats[0].ColorSpace,
            ImageExtent = _swapchainExtent,
            ImageArrayLayers = 1,
            ImageUsage = ImageUsageFlags.ColorAttachmentBit,
            ImageSharingMode = SharingMode.Exclusive,
            PreTransform = capabilities.CurrentTransform,
            CompositeAlpha = CompositeAlphaFlagsKHR.OpaqueBitKhr,
            PresentMode = PresentModeKHR.FifoKhr,
            Clipped = true
        };

        fixed (SwapchainKHR* swapchainPtr = &_swapchain)
        {
            if (_khrSwapchain!.CreateSwapchain(_device, &createInfo, null, swapchainPtr) != Result.Success)
                throw new Exception("无法创建 Swapchain");
        }

        _khrSwapchain.GetSwapchainImages(_device, _swapchain, &imageCount, null);
        _swapchainImages = new Image[imageCount];
        fixed (Image* imagesPtr = _swapchainImages)
            _khrSwapchain.GetSwapchainImages(_device, _swapchain, &imageCount, imagesPtr);
    }

    private void CreateImageViews()
    {
        _swapchainImageViews = new ImageView[_swapchainImages.Length];
        for (int i = 0; i < _swapchainImages.Length; i++)
        {
            ImageViewCreateInfo createInfo = new()
            {
                SType = StructureType.ImageViewCreateInfo,
                Image = _swapchainImages[i],
                ViewType = ImageViewType.Type2D,
                Format = _swapchainFormat,
                Components = new ComponentMapping(
                    ComponentSwizzle.Identity, ComponentSwizzle.Identity,
                    ComponentSwizzle.Identity, ComponentSwizzle.Identity),
                SubresourceRange = new ImageSubresourceRange(
                    ImageAspectFlags.ColorBit, 0, 1, 0, 1)
            };

            fixed (ImageView* viewPtr = &_swapchainImageViews[i])
            {
                if (_vk!.CreateImageView(_device, &createInfo, null, viewPtr) != Result.Success)
                    throw new Exception($"无法创建 ImageView {i}");
            }
        }
    }

    private void CreateRenderPass()
    {
        AttachmentDescription colorAttachment = new()
        {
            Format = _swapchainFormat,
            Samples = SampleCountFlags.Count1Bit,
            LoadOp = AttachmentLoadOp.Clear,
            StoreOp = AttachmentStoreOp.Store,
            StencilLoadOp = AttachmentLoadOp.DontCare,
            StencilStoreOp = AttachmentStoreOp.DontCare,
            InitialLayout = ImageLayout.Undefined,
            FinalLayout = ImageLayout.PresentSrcKhr
        };

        AttachmentReference colorAttachmentRef = new()
        {
            Attachment = 0,
            Layout = ImageLayout.ColorAttachmentOptimal
        };

        SubpassDescription subpass = new()
        {
            PipelineBindPoint = PipelineBindPoint.Graphics,
            ColorAttachmentCount = 1,
            PColorAttachments = &colorAttachmentRef
        };

        SubpassDependency dependency = new()
        {
            SrcSubpass = Vk.SubpassExternal,
            DstSubpass = 0,
            SrcStageMask = PipelineStageFlags.ColorAttachmentOutputBit,
            SrcAccessMask = 0,
            DstStageMask = PipelineStageFlags.ColorAttachmentOutputBit,
            DstAccessMask = AccessFlags.ColorAttachmentWriteBit
        };

        RenderPassCreateInfo renderPassInfo = new()
        {
            SType = StructureType.RenderPassCreateInfo,
            AttachmentCount = 1,
            PAttachments = &colorAttachment,
            SubpassCount = 1,
            PSubpasses = &subpass,
            DependencyCount = 1,
            PDependencies = &dependency
        };

        fixed (RenderPass* renderPassPtr = &_renderPass)
        {
            if (_vk!.CreateRenderPass(_device, &renderPassInfo, null, renderPassPtr) != Result.Success)
                throw new Exception("无法创建 RenderPass");
        }
    }

    private void CreateGraphicsPipeline()
    {
        ShaderModule vertModule = CreateShaderModule(VertexShaderSpv);
        ShaderModule fragModule = CreateShaderModule(FragmentShaderSpv);

        PipelineShaderStageCreateInfo vertStage = new()
        {
            SType = StructureType.PipelineShaderStageCreateInfo,
            Stage = ShaderStageFlags.VertexBit,
            Module = vertModule,
            PName = (byte*)SilkMarshal.StringToPtr("main", NativeStringEncoding.UTF8)
        };

        PipelineShaderStageCreateInfo fragStage = new()
        {
            SType = StructureType.PipelineShaderStageCreateInfo,
            Stage = ShaderStageFlags.FragmentBit,
            Module = fragModule,
            PName = (byte*)SilkMarshal.StringToPtr("main", NativeStringEncoding.UTF8)
        };

        var shaderStages = stackalloc PipelineShaderStageCreateInfo[2];
        shaderStages[0] = vertStage;
        shaderStages[1] = fragStage;

        VertexInputBindingDescription bindingDesc = new()
        {
            Binding = 0,
            Stride = (uint)VertexSize,
            InputRate = VertexInputRate.Vertex
        };

        VertexInputAttributeDescription[] attrDescs = new VertexInputAttributeDescription[2];
        attrDescs[0] = new VertexInputAttributeDescription
        {
            Binding = 0, Location = 0,
            Format = Format.R32G32Sfloat,
            Offset = 0
        };
        attrDescs[1] = new VertexInputAttributeDescription
        {
            Binding = 0, Location = 1,
            Format = Format.R32G32B32A32Sfloat,
            Offset = 2 * sizeof(float)
        };

        PipelineVertexInputStateCreateInfo vertexInputInfo = new()
        {
            SType = StructureType.PipelineVertexInputStateCreateInfo,
            VertexBindingDescriptionCount = 1,
            PVertexBindingDescriptions = &bindingDesc,
            VertexAttributeDescriptionCount = 2
        };
        fixed (VertexInputAttributeDescription* attrPtr = attrDescs)
        {
            vertexInputInfo.PVertexAttributeDescriptions = attrPtr;

            PipelineInputAssemblyStateCreateInfo inputAssembly = new()
            {
                SType = StructureType.PipelineInputAssemblyStateCreateInfo,
                Topology = PrimitiveTopology.TriangleList,
                PrimitiveRestartEnable = false
            };

            Viewport viewport = new(0, 0, _swapchainExtent.Width, _swapchainExtent.Height, 0, 1);
            Rect2D scissor = new(new Offset2D(0, 0), _swapchainExtent);

            PipelineViewportStateCreateInfo viewportState = new()
            {
                SType = StructureType.PipelineViewportStateCreateInfo,
                ViewportCount = 1,
                PViewports = &viewport,
                ScissorCount = 1,
                PScissors = &scissor
            };

            PipelineRasterizationStateCreateInfo rasterizer = new()
            {
                SType = StructureType.PipelineRasterizationStateCreateInfo,
                DepthClampEnable = false,
                RasterizerDiscardEnable = false,
                PolygonMode = PolygonMode.Fill,
                CullMode = CullModeFlags.None,
                FrontFace = FrontFace.CounterClockwise,
                DepthBiasEnable = false,
                LineWidth = 1.0f
            };

            PipelineMultisampleStateCreateInfo multisampling = new()
            {
                SType = StructureType.PipelineMultisampleStateCreateInfo,
                SampleShadingEnable = false,
                RasterizationSamples = SampleCountFlags.Count1Bit
            };

            PipelineColorBlendAttachmentState colorBlendAttachment = new()
            {
                BlendEnable = false,
                ColorWriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit |
                                 ColorComponentFlags.BBit | ColorComponentFlags.ABit
            };

            PipelineColorBlendStateCreateInfo colorBlending = new()
            {
                SType = StructureType.PipelineColorBlendStateCreateInfo,
                LogicOpEnable = false,
                AttachmentCount = 1,
                PAttachments = &colorBlendAttachment
            };

            PipelineLayoutCreateInfo layoutInfo = new()
            {
                SType = StructureType.PipelineLayoutCreateInfo
            };

            fixed (PipelineLayout* layoutPtr = &_pipelineLayout)
            {
                if (_vk!.CreatePipelineLayout(_device, &layoutInfo, null, layoutPtr) != Result.Success)
                    throw new Exception("无法创建 PipelineLayout");
            }

            GraphicsPipelineCreateInfo pipelineInfo = new()
            {
                SType = StructureType.GraphicsPipelineCreateInfo,
                StageCount = 2,
                PStages = shaderStages,
                PVertexInputState = &vertexInputInfo,
                PInputAssemblyState = &inputAssembly,
                PViewportState = &viewportState,
                PRasterizationState = &rasterizer,
                PMultisampleState = &multisampling,
                PColorBlendState = &colorBlending,
                Layout = _pipelineLayout,
                RenderPass = _renderPass,
                Subpass = 0
            };

            fixed (Pipeline* pipelinePtr = &_pipeline)
            {
                if (_vk!.CreateGraphicsPipelines(_device, default, 1, &pipelineInfo, null, pipelinePtr) != Result.Success)
                    throw new Exception("无法创建 GraphicsPipeline");
            }
        }

        _vk!.DestroyShaderModule(_device, vertModule, null);
        _vk.DestroyShaderModule(_device, fragModule, null);
        SilkMarshal.Free((nint)vertStage.PName);
        SilkMarshal.Free((nint)fragStage.PName);
    }

    private ShaderModule CreateShaderModule(uint[] code)
    {
        ShaderModule module;
        fixed (uint* codePtr = code)
        {
            ShaderModuleCreateInfo createInfo = new()
            {
                SType = StructureType.ShaderModuleCreateInfo,
                CodeSize = (nuint)(code.Length * sizeof(uint)),
                PCode = codePtr
            };
            ShaderModule* modulePtr = &module;
            {
                if (_vk!.CreateShaderModule(_device, &createInfo, null, modulePtr) != Result.Success)
                    throw new Exception("无法创建着色器模块");
            }
        }
        return module;
    }

    private void CreateFramebuffers()
    {
        _framebuffers = new Framebuffer[_swapchainImageViews.Length];
        for (int i = 0; i < _swapchainImageViews.Length; i++)
        {
            var attachment = _swapchainImageViews[i];
            FramebufferCreateInfo fbInfo = new()
            {
                SType = StructureType.FramebufferCreateInfo,
                RenderPass = _renderPass,
                AttachmentCount = 1,
                PAttachments = &attachment,
                Width = _swapchainExtent.Width,
                Height = _swapchainExtent.Height,
                Layers = 1
            };

            fixed (Framebuffer* fbPtr = &_framebuffers[i])
            {
                if (_vk!.CreateFramebuffer(_device, &fbInfo, null, fbPtr) != Result.Success)
                    throw new Exception($"无法创建 Framebuffer {i}");
            }
        }
    }

    private void CreateCommandPool()
    {
        CommandPoolCreateInfo poolInfo = new()
        {
            SType = StructureType.CommandPoolCreateInfo,
            Flags = CommandPoolCreateFlags.ResetCommandBufferBit,
            QueueFamilyIndex = _graphicsQueueFamily
        };

        fixed (CommandPool* poolPtr = &_commandPool)
        {
            if (_vk!.CreateCommandPool(_device, &poolInfo, null, poolPtr) != Result.Success)
                throw new Exception("无法创建 CommandPool");
        }
    }

    private void CreateVertexBuffer()
    {
        ulong bufferSize = (ulong)(Vertices.Length * sizeof(float));

        BufferCreateInfo bufferInfo = new()
        {
            SType = StructureType.BufferCreateInfo,
            Size = bufferSize,
            Usage = BufferUsageFlags.VertexBufferBit,
            SharingMode = SharingMode.Exclusive
        };

        fixed (Buffer* bufferPtr = &_vertexBuffer)
        {
            if (_vk!.CreateBuffer(_device, &bufferInfo, null, bufferPtr) != Result.Success)
                throw new Exception("无法创建顶点缓冲");
        }

        MemoryRequirements memRequirements;
        _vk.GetBufferMemoryRequirements(_device, _vertexBuffer, &memRequirements);

        PhysicalDeviceMemoryProperties memProps;
        _vk.GetPhysicalDeviceMemoryProperties(_physicalDevice, &memProps);

        uint memoryTypeIndex = FindMemoryType(
            memProps,
            memRequirements.MemoryTypeBits,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);

        MemoryAllocateInfo allocInfo = new()
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = memRequirements.Size,
            MemoryTypeIndex = memoryTypeIndex
        };

        fixed (DeviceMemory* memPtr = &_vertexBufferMemory)
        {
            if (_vk.AllocateMemory(_device, &allocInfo, null, memPtr) != Result.Success)
                throw new Exception("无法分配顶点缓冲内存");
        }

        _vk.BindBufferMemory(_device, _vertexBuffer, _vertexBufferMemory, 0);

        void* data;
        _vk.MapMemory(_device, _vertexBufferMemory, 0, bufferSize, 0, &data);
        fixed (float* verticesPtr = Vertices)
            System.Buffer.MemoryCopy(verticesPtr, data, bufferSize, bufferSize);
        _vk.UnmapMemory(_device, _vertexBufferMemory);
    }

    private static uint FindMemoryType(PhysicalDeviceMemoryProperties memProps, uint typeFilter, MemoryPropertyFlags properties)
    {
        for (int i = 0; i < memProps.MemoryTypeCount; i++)
        {
            if ((typeFilter & (1u << i)) != 0 &&
                (memProps.MemoryTypes[i].PropertyFlags & properties) == properties)
                return (uint)i;
        }
        throw new Exception("未找到合适的内存类型");
    }

    private void CreateCommandBuffers()
    {
        _commandBuffers = new CommandBuffer[_swapchainImageViews.Length];

        CommandBufferAllocateInfo allocInfo = new()
        {
            SType = StructureType.CommandBufferAllocateInfo,
            CommandPool = _commandPool,
            Level = CommandBufferLevel.Primary,
            CommandBufferCount = (uint)_commandBuffers.Length
        };

        fixed (CommandBuffer* cmdPtr = _commandBuffers)
        {
            if (_vk!.AllocateCommandBuffers(_device, &allocInfo, cmdPtr) != Result.Success)
                throw new Exception("无法分配 CommandBuffer");
        }

        ClearValue clearColor = new()
        {
            Color = new ClearColorValue(0.05f, 0.05f, 0.08f, 1.0f)
        };

        for (int i = 0; i < _commandBuffers.Length; i++)
        {
            CommandBufferBeginInfo beginInfo = new()
            {
                SType = StructureType.CommandBufferBeginInfo,
                Flags = CommandBufferUsageFlags.SimultaneousUseBit
            };

            _vk!.BeginCommandBuffer(_commandBuffers[i], &beginInfo);

            RenderPassBeginInfo renderPassInfo = new()
            {
                SType = StructureType.RenderPassBeginInfo,
                RenderPass = _renderPass,
                Framebuffer = _framebuffers[i],
                RenderArea = new Rect2D(new Offset2D(0, 0), _swapchainExtent),
                ClearValueCount = 1,
                PClearValues = &clearColor
            };

            _vk.CmdBeginRenderPass(_commandBuffers[i], &renderPassInfo, SubpassContents.Inline);
            _vk.CmdBindPipeline(_commandBuffers[i], PipelineBindPoint.Graphics, _pipeline);

            ulong offset = 0;
            _vk.CmdBindVertexBuffers(_commandBuffers[i], 0, 1, in _vertexBuffer, in offset);
            _vk.CmdDraw(_commandBuffers[i], 3, 1, 0, 0);

            _vk.CmdEndRenderPass(_commandBuffers[i]);

            if (_vk.EndCommandBuffer(_commandBuffers[i]) != Result.Success)
                throw new Exception($"无法结束 CommandBuffer {i}");
        }
    }

    private void CreateSyncObjects()
    {
        _imageAvailableSemaphores = new Semaphore[MaxFramesInFlight];
        _renderFinishedSemaphores = new Semaphore[MaxFramesInFlight];
        _inFlightFences = new Fence[MaxFramesInFlight];

        SemaphoreCreateInfo semInfo = new() { SType = StructureType.SemaphoreCreateInfo };
        FenceCreateInfo fenceInfo = new() { SType = StructureType.FenceCreateInfo, Flags = FenceCreateFlags.SignaledBit };

        for (int i = 0; i < MaxFramesInFlight; i++)
        {
            fixed (Semaphore* imgSemPtr = &_imageAvailableSemaphores[i])
            fixed (Semaphore* renderSemPtr = &_renderFinishedSemaphores[i])
            fixed (Fence* fencePtr = &_inFlightFences[i])
            {
                if (_vk!.CreateSemaphore(_device, &semInfo, null, imgSemPtr) != Result.Success ||
                    _vk.CreateSemaphore(_device, &semInfo, null, renderSemPtr) != Result.Success ||
                    _vk.CreateFence(_device, &fenceInfo, null, fencePtr) != Result.Success)
                    throw new Exception($"无法创建同步对象 {i}");
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_vk == null) return;
        _vk.DeviceWaitIdle(_device);

        for (int i = 0; i < MaxFramesInFlight; i++)
        {
            if (i < _imageAvailableSemaphores.Length) _vk.DestroySemaphore(_device, _imageAvailableSemaphores[i], null);
            if (i < _renderFinishedSemaphores.Length) _vk.DestroySemaphore(_device, _renderFinishedSemaphores[i], null);
            if (i < _inFlightFences.Length) _vk.DestroyFence(_device, _inFlightFences[i], null);
        }

        if (_vertexBuffer.Handle != 0) _vk.DestroyBuffer(_device, _vertexBuffer, null);
        if (_vertexBufferMemory.Handle != 0) _vk.FreeMemory(_device, _vertexBufferMemory, null);

        foreach (var fb in _framebuffers) _vk.DestroyFramebuffer(_device, fb, null);
        if (_pipeline.Handle != 0) _vk.DestroyPipeline(_device, _pipeline, null);
        if (_pipelineLayout.Handle != 0) _vk.DestroyPipelineLayout(_device, _pipelineLayout, null);
        if (_renderPass.Handle != 0) _vk.DestroyRenderPass(_device, _renderPass, null);
        foreach (var iv in _swapchainImageViews) _vk.DestroyImageView(_device, iv, null);
        if (_swapchain.Handle != 0 && _khrSwapchain != null) _khrSwapchain.DestroySwapchain(_device, _swapchain, null);
        if (_commandPool.Handle != 0) _vk.DestroyCommandPool(_device, _commandPool, null);
        if (_device.Handle != 0) _vk.DestroyDevice(_device, null);
        if (_surface.Handle != 0 && _khrSurface != null) _khrSurface.DestroySurface(_instance, _surface, null);
        if (_instance.Handle != 0) _vk.DestroyInstance(_instance, null);
    }
}
