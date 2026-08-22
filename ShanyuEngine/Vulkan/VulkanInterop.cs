// ════════════════════════════════════════════════════════════════════
// VulkanInterop.cs — Minimal Vulkan interop layer (replaces Silk.NET.Vulkan)
// Provides handle types, enums, structs, and P/Invoke wrappers for vulkan-1.dll.
// All types live in ShanyuEngine.RHI.Vulkan to avoid external dependencies.
// ════════════════════════════════════════════════════════════════════

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace ShanyuEngine.RHI.Vulkan
{
    // ── Boolean type ──
    [StructLayout(LayoutKind.Sequential)]
    unsafe internal struct Bool32
    {
        public uint Value;
        public Bool32(bool b) => Value = b ? 1u : 0u;
        public static implicit operator bool(Bool32 b) => b.Value != 0;
        public static implicit operator Bool32(bool b) => new Bool32(b);
    }

    // ── Version helper ──
    unsafe internal static class VkVersion
    {
        public static uint Make(uint major, uint minor, uint patch)
            => (major << 22) | (minor << 12) | patch;
        public static void Unpack(uint version, out uint major, out uint minor, out uint patch)
        {
            major = version >> 22;
            minor = (version >> 12) & 0x3FF;
            patch = version & 0xFFF;
        }
    }

    // ════════════════════════════════════════════════════════════════
    // Handle types — Vulkan non-dispatchable handles are uint64_t on 64-bit.
    // Dispatchable handles (Instance, Device, Queue, CommandBuffer) are pointers.
    // We unify them all as ulong for simplicity.
    // ════════════════════════════════════════════════════════════════

    #region Handle Types

    unsafe internal readonly struct VkInstance  { public readonly ulong Handle; public VkInstance(ulong h) => Handle = h; public static implicit operator ulong(VkInstance v) => v.Handle; public static readonly VkInstance Null = default; }
    unsafe internal readonly struct VkPhysicalDevice { public readonly ulong Handle; public VkPhysicalDevice(ulong h) => Handle = h; public static implicit operator ulong(VkPhysicalDevice v) => v.Handle; public static readonly VkPhysicalDevice Null = default; }
    unsafe internal readonly struct VkDevice    { public readonly ulong Handle; public VkDevice(ulong h) => Handle = h; public static implicit operator ulong(VkDevice v) => v.Handle; public static readonly VkDevice Null = default; }
    unsafe internal readonly struct VkQueue     { public readonly ulong Handle; public VkQueue(ulong h) => Handle = h; public static implicit operator ulong(VkQueue v) => v.Handle; public static readonly VkQueue Null = default; }
    unsafe internal readonly struct VkCommandBuffer { public readonly ulong Handle; public VkCommandBuffer(ulong h) => Handle = h; public static implicit operator ulong(VkCommandBuffer v) => v.Handle; public static readonly VkCommandBuffer Null = default; }
    unsafe internal readonly struct VkCommandPool { public readonly ulong Handle; public VkCommandPool(ulong h) => Handle = h; public static implicit operator ulong(VkCommandPool v) => v.Handle; public static readonly VkCommandPool Null = default; }
    unsafe internal readonly struct VkBuffer   { public readonly ulong Handle; public VkBuffer(ulong h) => Handle = h; public static implicit operator ulong(VkBuffer v) => v.Handle; public static readonly VkBuffer Null = default; }
    unsafe internal readonly struct VkImage    { public readonly ulong Handle; public VkImage(ulong h) => Handle = h; public static implicit operator ulong(VkImage v) => v.Handle; public static readonly VkImage Null = default; }
    unsafe internal readonly struct VkImageView { public readonly ulong Handle; public VkImageView(ulong h) => Handle = h; public static implicit operator ulong(VkImageView v) => v.Handle; public static readonly VkImageView Null = default; }
    unsafe internal readonly struct VkDeviceMemory { public readonly ulong Handle; public VkDeviceMemory(ulong h) => Handle = h; public static implicit operator ulong(VkDeviceMemory v) => v.Handle; public static readonly VkDeviceMemory Null = default; }
    unsafe internal readonly struct VkSampler  { public readonly ulong Handle; public VkSampler(ulong h) => Handle = h; public static implicit operator ulong(VkSampler v) => v.Handle; public static readonly VkSampler Null = default; }
    unsafe internal readonly struct VkShaderModule { public readonly ulong Handle; public VkShaderModule(ulong h) => Handle = h; public static implicit operator ulong(VkShaderModule v) => v.Handle; public static readonly VkShaderModule Null = default; }
    unsafe internal readonly struct VkPipeline { public readonly ulong Handle; public VkPipeline(ulong h) => Handle = h; public static implicit operator ulong(VkPipeline v) => v.Handle; public static readonly VkPipeline Null = default; }
    unsafe internal readonly struct VkPipelineLayout { public readonly ulong Handle; public VkPipelineLayout(ulong h) => Handle = h; public static implicit operator ulong(VkPipelineLayout v) => v.Handle; public static readonly VkPipelineLayout Null = default; }
    unsafe internal readonly struct VkDescriptorSet { public readonly ulong Handle; public VkDescriptorSet(ulong h) => Handle = h; public static implicit operator ulong(VkDescriptorSet v) => v.Handle; public static readonly VkDescriptorSet Null = default; }
    unsafe internal readonly struct VkDescriptorSetLayout { public readonly ulong Handle; public VkDescriptorSetLayout(ulong h) => Handle = h; public static implicit operator ulong(VkDescriptorSetLayout v) => v.Handle; public static readonly VkDescriptorSetLayout Null = default; }
    unsafe internal readonly struct VkDescriptorPool { public readonly ulong Handle; public VkDescriptorPool(ulong h) => Handle = h; public static implicit operator ulong(VkDescriptorPool v) => v.Handle; public static readonly VkDescriptorPool Null = default; }
    unsafe internal readonly struct VkFence    { public readonly ulong Handle; public VkFence(ulong h) => Handle = h; public static implicit operator ulong(VkFence v) => v.Handle; public static readonly VkFence Null = default; }
    unsafe internal readonly struct VkSemaphore { public readonly ulong Handle; public VkSemaphore(ulong h) => Handle = h; public static implicit operator ulong(VkSemaphore v) => v.Handle; public static readonly VkSemaphore Null = default; }
    unsafe internal readonly struct VkSurfaceKHR { public readonly ulong Handle; public VkSurfaceKHR(ulong h) => Handle = h; public static implicit operator ulong(VkSurfaceKHR v) => v.Handle; public static readonly VkSurfaceKHR Null = default; }
    unsafe internal readonly struct VkSwapchainKHR { public readonly ulong Handle; public VkSwapchainKHR(ulong h) => Handle = h; public static implicit operator ulong(VkSwapchainKHR v) => v.Handle; public static readonly VkSwapchainKHR Null = default; }
    unsafe internal readonly struct VkRenderPass { public readonly ulong Handle; public VkRenderPass(ulong h) => Handle = h; public static implicit operator ulong(VkRenderPass v) => v.Handle; public static readonly VkRenderPass Null = default; }
    unsafe internal readonly struct VkFramebuffer { public readonly ulong Handle; public VkFramebuffer(ulong h) => Handle = h; public static implicit operator ulong(VkFramebuffer v) => v.Handle; public static readonly VkFramebuffer Null = default; }
    unsafe internal readonly struct VkBufferView { public readonly ulong Handle; public VkBufferView(ulong h) => Handle = h; public static implicit operator ulong(VkBufferView v) => v.Handle; public static readonly VkBufferView Null = default; }
    unsafe internal readonly struct VkDebugUtilsMessengerEXT { public readonly ulong Handle; public VkDebugUtilsMessengerEXT(ulong h) => Handle = h; public static implicit operator ulong(VkDebugUtilsMessengerEXT v) => v.Handle; }

    #endregion

    // ════════════════════════════════════════════════════════════════
    // Enums
    // ════════════════════════════════════════════════════════════════

    #region Enums

    internal enum VkResult : int
    {
        Success = 0,
        NotReady = 1,
        Timeout = 2,
        EventSet = 3,
        EventReset = 4,
        Incomplete = 5,
        ErrorOutOfHostMemory = -1,
        ErrorOutOfDeviceMemory = -2,
        ErrorInitializationFailed = -3,
        ErrorDeviceLost = -4,
        ErrorMemoryMapFailed = -5,
        ErrorLayerNotPresent = -6,
        ErrorExtensionNotPresent = -7,
        ErrorFeatureNotPresent = -8,
        ErrorIncompatibleDriver = -9,
        ErrorTooManyObjects = -10,
        ErrorFormatNotSupported = -11,
        SuboptimalKhr = 1000001003,
        ErrorOutOfDateKhr = -1000001004,
        ErrorSurfaceLostKhr = -1000000000,
        ErrorNativeWindowInUseKhr = -1000000001,
    }

    internal enum VkStructureType : int
    {
        ApplicationInfo = 0,
        InstanceCreateInfo = 1,
        DeviceQueueCreateInfo = 2,
        DeviceCreateInfo = 3,
        SubmitInfo = 4,
        MemoryAllocateInfo = 5,
        CommandPoolCreateInfo = 12,
        CommandBufferAllocateInfo = 13,
        CommandBufferBeginInfo = 14,
        BufferCreateInfo = 15,
        BufferViewCreateInfo = 16,
        ImageCreateInfo = 17,
        ImageViewCreateInfo = 18,
        ShaderModuleCreateInfo = 19,
        PipelineCacheCreateInfo = 20,
        PipelineShaderStageCreateInfo = 21,
        PipelineVertexInputStateCreateInfo = 22,
        PipelineInputAssemblyStateCreateInfo = 23,
        PipelineTessellationStateCreateInfo = 24,
        PipelineViewportStateCreateInfo = 25,
        PipelineRasterizationStateCreateInfo = 26,
        PipelineMultisampleStateCreateInfo = 27,
        PipelineDepthStencilStateCreateInfo = 28,
        PipelineColorBlendStateCreateInfo = 29,
        PipelineDynamicStateCreateInfo = 30,
        GraphicsPipelineCreateInfo = 31,
        ComputePipelineCreateInfo = 32,
        PipelineLayoutCreateInfo = 33,
        SamplerCreateInfo = 34,
        DescriptorSetLayoutCreateInfo = 35,
        DescriptorPoolCreateInfo = 36,
        DescriptorSetAllocateInfo = 37,
        WriteDescriptorSet = 38,
        CopyDescriptorSet = 39,
        FenceCreateInfo = 41,
        SemaphoreCreateInfo = 42,
        EventCreateInfo = 43,
        QueryPoolCreateInfo = 44,
        MemoryBarrier = 45,
        BufferMemoryBarrier = 46,
        ImageMemoryBarrier = 47,
        RenderPassBeginInfo = 48,
        Win32SurfaceCreateInfoKhr = 1000009000,
        SwapchainCreateInfoKhr = 1000001000,
        PresentInfoKhr = 1000001001,
        PhysicalDeviceFeatures2 = 1000059000,
        PhysicalDeviceVulkan13Features = 1000137000,
        DebugUtilsMessengerCreateInfoExt = 1000128004,
        PipelineRenderingCreateInfo = 1000084000,
    }

    internal enum VkFormat : int
    {
        Undefined = 0,
        R4G4UnormPack8 = 1,
        R4G4B4A4UnormPack16 = 2,
        B4G4R4A4UnormPack16 = 3,
        R5G6B5UnormPack16 = 4,
        B5G6R5UnormPack16 = 5,
        R5G5B5A1UnormPack16 = 6,
        B5G5R5A1UnormPack16 = 7,
        A1R5G5B5UnormPack16 = 8,
        R8Unorm = 9, R8Snorm = 10, R8Uscaled = 11, R8Sscaled = 12, R8Uint = 13, R8Sint = 14, R8Srgb = 15,
        R8G8Unorm = 16, R8G8Snorm = 17, R8G8Uscaled = 18, R8G8Sscaled = 19, R8G8Uint = 20, R8G8Sint = 21, R8G8Srgb = 22,
        R8G8B8Unorm = 23, R8G8B8Snorm = 24, R8G8B8Srgb = 29,
        B8G8R8Unorm = 30, B8G8R8Srgb = 36,
        R8G8B8A8Unorm = 37, R8G8B8A8Snorm = 38, R8G8B8A8Uint = 40, R8G8B8A8Sint = 41, R8G8B8A8Srgb = 43,
        B8G8R8A8Unorm = 44, B8G8R8A8Snorm = 45, B8G8R8A8Uint = 47, B8G8R8A8Sint = 48, B8G8R8A8Srgb = 50,
        R16Unorm = 70, R16Snorm = 71, R16Uint = 74, R16Sint = 75, R16Sfloat = 76,
        R16G16Unorm = 77, R16G16Snorm = 78, R16G16Uint = 81, R16G16Sint = 82, R16G16Sfloat = 83,
        R16G16B16Unorm = 84, R16G16B16Sfloat = 90,
        R16G16B16A16Unorm = 91, R16G16B16A16Snorm = 92, R16G16B16A16Uint = 95, R16G16B16A16Sint = 96, R16G16B16A16Sfloat = 97,
        R32Uint = 98, R32Sint = 99, R32Sfloat = 100,
        R32G32Uint = 101, R32G32Sint = 102, R32G32Sfloat = 103,
        R32G32B32Sfloat = 106,
        R32G32B32A32Uint = 108, R32G32B32A32Sint = 109, R32G32B32A32Sfloat = 110,
        R10X6G10X6B10X6A10X6Unorm4Pack16 = 121,
        R10G10B10A2UnormPack32 = 64, R10G10B10A2UintPack32 = 65,
        B10G11R11UfloatPack32 = 76 + 0, // placeholder fix
        B10G11R11UfloatPack32C = 122,
        E5B9G9R9UfloatPack32 = 123,
        D16Unorm = 124, X8D24UnormPack32 = 125, D32Sfloat = 126,
        D24UnormS8Uint = 129, D32SfloatS8Uint = 130,
        Bc1RgbaUnormBlock = 132, Bc1RgbaSrgbBlock = 133,
        Bc2UnormBlock = 135, Bc3UnormBlock = 137,
    }

    // Flags enums (as uint for bitwise operations)
    [Flags] internal enum VkQueueFlags : uint { Graphics = 1, Compute = 2, Transfer = 4, SparseBinding = 8 }
    [Flags] internal enum VkBufferUsageFlags : uint { TransferSrc = 1, TransferDst = 2, UniformTexel = 4, StorageTexel = 8, Uniform = 16, Storage = 32, Index = 64, Vertex = 128, Indirect = 256, ShaderDeviceAddress = 0x400, VideoDecodeSrc = 0x4000, VideoDecodeDst = 0x8000 }
    [Flags] internal enum VkImageUsageFlags : uint { TransferSrc = 1, TransferDst = 2, Sampled = 4, Storage = 8, ColorAttachment = 16, DepthStencilAttachment = 32, TransientAttachment = 64, InputAttachment = 128, FragmentDensityMap = 0x2000000 }
    [Flags] internal enum VkImageCreateFlags : uint { SparseBinding = 1, SparseResidency = 2, SparseAliased = 4, MutableFormat = 8, CubeCompatible = 16, Array2DCompatible = 0x40, BlockTexelViewCompatible = 0x80, ExtendedUsage = 0x10000, Protected = 0x800 }
    [Flags] internal enum VkMemoryPropertyFlags : uint { DeviceLocal = 1, HostVisible = 2, HostCoherent = 4, HostCached = 8, LazilyAllocated = 16, Protected = 32, DeviceCoherent = 64, DeviceUncached = 128 }
    [Flags] internal enum VkPipelineStageFlags : uint
    {
        TopOfPipe = 1, DrawIndirect = 2, VertexInput = 4, VertexShader = 8, TessellationControlShader = 16, TessellationEvaluationShader = 32, GeometryShader = 64, FragmentShader = 128,
        EarlyFragmentTests = 256, LateFragmentTests = 512, ColorAttachmentOutput = 1024, ComputeShader = 2048, Transfer = 4096, BottomOfPipe = 8192, Host = 16384, AllGraphics = 0x4000 | 0x2000 | 0x1000,
        AllCommands = 0x7FFF, TaskShaderExt = 0x800000, MeshShaderExt = 0x1000000,
    }
    [Flags] internal enum VkAccessFlags : uint
    {
        IndirectCommandRead = 1, IndexRead = 2, VertexAttributeRead = 4, UniformRead = 8, InputAttachmentRead = 16, ShaderRead = 32, ShaderWrite = 64, ColorAttachmentRead = 128, ColorAttachmentWrite = 256,
        DepthStencilAttachmentRead = 512, DepthStencilAttachmentWrite = 1024, TransferRead = 2048, TransferWrite = 4096, HostRead = 8192, HostWrite = 16384, MemoryRead = 32768, MemoryWrite = 65536,
    }
    [Flags] internal enum VkImageAspectFlags : uint { Color = 1, Depth = 2, Stencil = 4, Metadata = 8, Plane0 = 16, Plane1 = 32, Plane2 = 64 }
    [Flags] internal enum VkDescriptorSetLayoutCreateFlags : uint { None = 0, UpdateAfterBindPool = 2, PushDescriptor = 0x80 }
    [Flags] internal enum VkDescriptorPoolCreateFlags : uint { FreeDescriptorSet = 1, UpdateAfterBind = 2 }
    [Flags] internal enum VkShaderStageFlags : uint
    {
        Vertex = 1, TessellationControl = 2, TessellationEvaluation = 4, Geometry = 8, Fragment = 16, Compute = 32, AllGraphics = 0x1F, All = 0x7FFFFFFF,
        TaskExt = 0x40, MeshExt = 0x80, RaygenBitNvx = 0x100, ClosestHitBitNvx = 0x400, MissBitNvx = 0x200,
    }
    [Flags] internal enum VkColorComponentFlags : uint { R = 1, G = 2, B = 4, A = 8, RBit = 1, GBit = 2, BBit = 4, ABit = 8 }
    [Flags] internal enum VkCommandBufferUsageFlags : uint { OneTimeSubmit = 1, RenderPassContinue = 2, SimultaneousUse = 4 }
    [Flags] internal enum VkDependencyFlags : uint { None = 0, ByRegion = 1, DeviceGroup = 4, ViewLocal = 2 }
    [Flags] internal enum VkSampleCountFlags : uint { Count1 = 1, Count2 = 2, Count4 = 4, Count8 = 8, Count16 = 16, Count32 = 32, Count64 = 64 }
    [Flags] internal enum VkPipelineCreateFlags : uint { None = 0, DisableOptimization = 1, AllowDerivatives = 2, Derivative = 4 }
    [Flags] internal enum VkCompositeAlphaFlagsKHR : uint { OpaqueBitKhr = 1, PreMultipliedBitKhr = 2, PostMultipliedBitKhr = 4, InheritBitKhr = 8 }
    [Flags] internal enum VkDescriptorBindingFlags : uint { None = 0, UpdateAfterBind = 1, UpdateUnusedWhilePending = 2, PartiallyBound = 4, VariableDescriptorCount = 8 }
    [Flags] internal enum VkImageUsageFlagsKHR : uint { ColorAttachmentBit = 16, TransferDstBit = 2 }

    internal enum VkBlendFactor : int
    {
        Zero = 0, One = 1, SrcColor = 2, OneMinusSrcColor = 3, DstColor = 4, OneMinusDstColor = 5,
        SrcAlpha = 6, OneMinusSrcAlpha = 7, DstAlpha = 8, OneMinusDstAlpha = 9, ConstantColor = 10,
        OneMinusConstantColor = 11, ConstantAlpha = 12, OneMinusConstantAlpha = 13, SrcAlphaSaturate = 14,
        Src1Color = 15, OneMinusSrc1Color = 16, Src1Alpha = 17, OneMinusSrc1Alpha = 18,
    }

    internal enum VkBlendOp : int
    {
        Add = 0, Subtract = 1, ReverseSubtract = 2, Min = 3, Max = 4,
        ZeroExt = 1000151007, SrcExt = 1000151008, DstExt = 1000151009,
    }

    internal enum VkCompareOp : int
    {
        Never = 0, Less = 1, Equal = 2, LessOrEqual = 3, Greater = 4, NotEqual = 5, GreaterOrEqual = 6, Always = 7,
    }

    internal enum VkStencilOp : int
    {
        Keep = 0, Zero = 1, Replace = 2, IncrementAndClamp = 3, DecrementAndClamp = 4, Invert = 5, IncrementAndWrap = 6, DecrementAndWrap = 7,
    }

    internal enum VkCullModeFlags : int { None = 0, Front = 1, Back = 2, FrontAndBack = 3 }

    internal enum VkPolygonMode : int { Fill = 0, Line = 1, Point = 2 }

    internal enum VkFrontFace : int { CounterClockwise = 0, Clockwise = 1 }

    internal enum VkPrimitiveTopology : int
    {
        PointList = 0, LineList = 1, LineStrip = 2, TriangleList = 3, TriangleStrip = 4, TriangleFan = 5,
        LineListWithAdjacency = 6, LineStripWithAdjacency = 7, TriangleListWithAdjacency = 8, TriangleStripWithAdjacency = 9,
        PatchList = 10,
    }

    internal enum VkImageViewType : int { Type1D = 0, Type2D = 1, Type3D = 2, TypeCube = 3, Type1DArray = 4, Type2DArray = 5, TypeCubeArray = 6 }

    internal enum VkDescriptorType : int
    {
        Sampler = 0, CombinedImageSampler = 1, SampledImage = 2, StorageImage = 3, UniformTexelBuffer = 4, StorageTexelBuffer = 5,
        UniformBuffer = 6, StorageBuffer = 7, UniformBufferDynamic = 8, StorageBufferDynamic = 9, InputAttachment = 10,
        InlineUniformBlock = 1000138000, AccelerationStructureKhr = 1000165000,
    }

    internal enum VkFilter : int { Nearest = 0, Linear = 1, CubicExt = 1000015000 }
    internal enum VkSamplerMipmapMode : int { Nearest = 0, Linear = 1 }
    internal enum VkSamplerAddressMode : int { Repeat = 0, MirroredRepeat = 1, ClampToEdge = 2, ClampToBorder = 3, MirrorClampToEdge = 4 }
    internal enum VkBorderColor : int { FloatTransparentBlack = 0, IntTransparentBlack = 1, FloatOpaqueBlack = 2, IntOpaqueBlack = 3, FloatOpaqueWhite = 4, IntOpaqueWhite = 5 }
    internal enum VkSharingMode : int { Exclusive = 0, Concurrent = 1 }
    internal enum VkImageLayout : int
    {
        Undefined = 0, General = 1, ColorAttachmentOptimal = 2, DepthStencilAttachmentOptimal = 3,
        DepthStencilReadOnlyOptimal = 4, ShaderReadOnlyOptimal = 5, TransferSrcOptimal = 6, TransferDstOptimal = 7,
        Preinitialized = 8, PresentSrcKhr = 1000001002, DepthReadOnlyStencilAttachmentOptimal = 1000117000,
        DepthAttachmentStencilReadOnlyOptimal = 1000124000,
    }
    internal enum VkDynamicState : int
    {
        Viewport = 0, Scissor = 1, LineWidth = 2, DepthBias = 3, BlendConstants = 4, DepthBounds = 5,
        StencilCompareMask = 6, StencilWriteMask = 7, StencilReference = 8,
    }
    internal enum VkIndexType : int { Uint16 = 0, Uint32 = 1, Uint8Ext = 1000265000 }
    internal enum VkPresentModeKHR : int { ImmediateKhr = 0, MailboxKhr = 1, FifoKhr = 2, FifoRelaxedKhr = 3 }
    internal enum VkColorSpaceKHR : int { SpaceSrgbNonlinearKhr = 0 }
    internal enum VkFormatFeatureFlags : uint { None = 0 }
    internal enum VkSamplerCreateFlags : uint { None = 0 }
    internal enum VkDeviceQueueCreateFlags : uint { None = 0 }
    internal enum VkInstanceCreateFlags : uint { None = 0 }
    internal enum VkFenceCreateFlags : uint { Signaled = 1 }
    internal enum VkSemaphoreCreateFlags : uint { None = 0 }
    internal enum VkCommandPoolCreateFlags : uint { Transient = 1, ResetCommandBuffer = 2, Protected = 4 }
    internal enum VkImageTiling : int { Optimal = 0, Linear = 1 }
    internal enum VkImageType : int { Type1D = 0, Type2D = 1, Type3D = 2 }
    internal enum VkSampleCountFlagBits : int { Count1 = 1 }
    internal enum VkComponentSwizzle : int { Identity = 0, Zero = 1, One = 2, R = 3, G = 4, B = 5, A = 6 }

    #endregion

    // ════════════════════════════════════════════════════════════════
    // Structs (LayoutKind.Sequential for native interop)
    // ════════════════════════════════════════════════════════════════

    #region Structs

    [StructLayout(LayoutKind.Sequential)]
    unsafe internal struct VkApplicationInfo
    {
        public VkStructureType SType;
        public IntPtr PNext;
        public uint ApiVersion;
        public uint ApplicationVersion;
        public IntPtr PApplicationName;
        public uint EngineVersion;
        public IntPtr PEngineName;
    }

    [StructLayout(LayoutKind.Sequential)]
    unsafe internal struct VkInstanceCreateInfo
    {
        public VkStructureType SType;
        public IntPtr PNext;
        public VkInstanceCreateFlags Flags;
        public VkApplicationInfo* PApplicationInfo;
        public uint EnabledLayerCount;
        public IntPtr PpEnabledLayerNames;
        public uint EnabledExtensionCount;
        public IntPtr PpEnabledExtensionNames;
    }

    [StructLayout(LayoutKind.Sequential)]
    unsafe internal struct VkDeviceQueueCreateInfo
    {
        public VkStructureType SType;
        public IntPtr PNext;
        public VkDeviceQueueCreateFlags Flags;
        public uint QueueFamilyIndex;
        public uint QueueCount;
        public float* PQueuePriorities;
    }

    [StructLayout(LayoutKind.Sequential)]
    unsafe internal struct VkDeviceCreateInfo
    {
        public VkStructureType SType;
        public IntPtr PNext;
        public VkInstanceCreateFlags Flags;
        public uint QueueCreateInfoCount;
        public VkDeviceQueueCreateInfo* PQueueCreateInfos;
        public uint EnabledLayerCount;
        public IntPtr PpEnabledLayerNames;
        public uint EnabledExtensionCount;
        public IntPtr PpEnabledExtensionNames;
        public VkPhysicalDeviceFeatures* PEnabledFeatures;
    }

    [StructLayout(LayoutKind.Sequential)]
    unsafe internal struct VkPhysicalDeviceProperties
    {
        public uint ApiVersion;
        public uint DriverVersion;
        public uint VendorID;
        public uint DeviceID;
        public int DeviceType;
        public fixed byte DeviceName[256];
        public fixed byte PipelineCacheUUID[16];
        public VkPhysicalDeviceLimits Limits;
    }

    [StructLayout(LayoutKind.Sequential)]
    unsafe internal struct VkPhysicalDeviceLimits
    {
        public uint MaxImageDimension2D;
        public uint MaxImageDimension3D;
        public uint MaxImageDimensionCube;
        public uint MaxImageArrayLayers;
        public uint MaxTexelBufferElements;
        public uint MaxUniformBufferRange;
        public uint MaxStorageBufferRange;
        public uint MaxPushConstantsSize;
        public uint MaxMemoryAllocationCount;
        public uint MaxSamplerAllocationCount;
        public ulong BufferImageGranularity;
        public ulong SparseAddressSpaceSize;
        public uint MaxBoundDescriptorSets;
        public uint MaxPerStageDescriptorSamplers;
        public uint MaxPerStageDescriptorUniformBuffers;
        public uint MaxPerStageDescriptorStorageBuffers;
        public uint MaxPerStageDescriptorSampledImages;
        public uint MaxPerStageDescriptorStorageImages;
        public uint MaxPerStageDescriptorInputAttachments;
        public uint MaxPerStageResources;
        public uint MaxDescriptorSetSamplers;
        public uint MaxDescriptorSetUniformBuffers;
        public uint MaxDescriptorSetUniformBuffersDynamic;
        public uint MaxDescriptorSetStorageBuffers;
        public uint MaxDescriptorSetStorageBuffersDynamic;
        public uint MaxDescriptorSetSampledImages;
        public uint MaxDescriptorSetStorageImages;
        public uint MaxDescriptorSetInputAttachments;
        public uint MaxVertexInputAttributes;
        public uint MaxVertexInputBindings;
        public uint MaxVertexInputAttributeOffset;
        public uint MaxVertexInputBindingStride;
        public uint MaxVertexOutputComponents;
        public uint MaxTessellationGenerationLevel;
        public uint MaxTessellationPatchSize;
        public uint MaxTessellationControlPerVertexInputComponents;
        public uint MaxTessellationControlPerVertexOutputComponents;
        public uint MaxTessellationControlPerPatchOutputComponents;
        public uint MaxTessellationControlTotalOutputComponents;
        public uint MaxTessellationEvaluationInputComponents;
        public uint MaxTessellationEvaluationOutputComponents;
        public uint MaxGeometryShaderInvocations;
        public uint MaxGeometryInputComponents;
        public uint MaxGeometryOutputComponents;
        public uint MaxGeometryOutputVertices;
        public uint MaxGeometryTotalOutputComponents;
        public uint MaxFragmentInputComponents;
        public uint MaxFragmentOutputAttachments;
        public uint MaxFragmentDualSrcAttachments;
        public uint MaxFragmentCombinedOutputResources;
        public uint MaxComputeSharedMemorySize;
        public fixed uint MaxComputeWorkGroupCount[3];
        public uint MaxComputeWorkGroupInvocations;
        public fixed uint MaxComputeWorkGroupSize[3];
        public uint SubPixelPrecisionBits;
        public uint SubTexelPrecisionBits;
        public uint MipmapPrecisionBits;
        public uint MaxDrawIndexedIndexValue;
        public uint MaxDrawIndirectCount;
        public float MaxSamplerLodBias;
        public float MaxSamplerAnisotropy;
        public uint MaxViewports;
        public fixed uint MaxViewportDimensions[2];
        public fixed float ViewportBoundsRange[2];
        public uint ViewportSubPixelBits;
        public ulong MinMemoryMapAlignment;
        public ulong MinTexelBufferOffsetAlignment;
        public ulong MinUniformBufferOffsetAlignment;
        public ulong MinStorageBufferOffsetAlignment;
        public int MinTexelOffset;
        public uint MaxTexelOffset;
        public int MinTexelGatherOffset;
        public uint MaxTexelGatherOffset;
        public float MinInterpolationOffset;
        public float MaxInterpolationOffset;
        public uint SubPixelInterpolationOffsetBits;
        public uint MaxFramebufferWidth;
        public uint MaxFramebufferHeight;
        public uint MaxFramebufferLayers;
        public uint FramebufferColorSampleCounts;
        public uint FramebufferDepthSampleCounts;
        public uint FramebufferStencilSampleCounts;
        public uint FramebufferNoAttachmentsSampleCounts;
        public uint MaxColorAttachments;
        public uint SampledImageColorSampleCounts;
        public uint SampledImageIntegerSampleCounts;
        public uint SampledImageDepthSampleCounts;
        public uint SampledImageStencilSampleCounts;
        public uint StorageImageSampleCounts;
        public uint MaxSampleMaskWords;
        public Bool32 TimestampComputeAndGraphics;
        public float TimestampPeriod;
        public uint MaxClipDistances;
        public uint MaxCullDistances;
        public uint MaxCombinedClipAndCullDistances;
        public uint DiscreteQueuePriorities;
        public fixed float PointSizeRange[2];
        public fixed float LineWidthRange[2];
        public float PointSizeGranularity;
        public float LineWidthGranularity;
        public Bool32 StrictLines;
        public Bool32 StandardSampleLocations;
        public ulong OptimalBufferCopyOffsetAlignment;
        public ulong OptimalBufferCopyRowPitchAlignment;
        public uint NonCoherentAtomSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    unsafe internal struct VkPhysicalDeviceMemoryProperties
    {
        public uint MemoryTypeCount;
        public fixed byte MemoryTypesPadding[8 * 16]; // VkMemoryType is 8 bytes, max 32
        public uint MemoryHeapCount;
        public fixed byte MemoryHeapsPadding[16 * 16]; // VkMemoryHeap is 16 bytes, max 16
    }

    [StructLayout(LayoutKind.Sequential)]
    unsafe internal struct VkMemoryType
    {
        public VkMemoryPropertyFlags PropertyFlags;
        public uint HeapIndex;
    }

    [StructLayout(LayoutKind.Sequential)]
    unsafe internal struct VkMemoryHeap
    {
        public ulong Size;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    unsafe internal struct VkPhysicalDeviceFeatures
    {
        public Bool32 RobustBufferAccess;
        public Bool32 FullDrawIndexUint32;
        public Bool32 ImageCubeArray;
        public Bool32 IndependentBlend;
        public Bool32 GeometryShader;
        public Bool32 TessellationShader;
        public Bool32 SampleRateShading;
        public Bool32 DualSrcBlend;
        public Bool32 LogicOp;
        public Bool32 MultiDrawIndirect;
        public Bool32 DrawIndirectFirstInstance;
        public Bool32 DepthClamp;
        public Bool32 DepthBiasClamp;
        public Bool32 FillModeNonSolid;
        public Bool32 DepthBounds;
        public Bool32 WideLines;
        public Bool32 LargePoints;
        public Bool32 AlphaToOne;
        public Bool32 MultiViewport;
        public Bool32 SamplerAnisotropy;
        public Bool32 TextureCompressionETC2;
        public Bool32 TextureCompressionASTC_LDR;
        public Bool32 TextureCompressionBC;
        public Bool32 OcclusionQueryPrecise;
        public Bool32 PipelineStatisticsQuery;
        public Bool32 VertexPipelineStoresAndAtomics;
        public Bool32 FragmentStoresAndAtomics;
        public Bool32 ShaderTessellationAndGeometryPointSize;
        public Bool32 ShaderImageGatherExtended;
        public Bool32 ShaderStorageImageExtendedFormats;
        public Bool32 ShaderStorageImageMultisample;
        public Bool32 ShaderStorageImageReadWithoutFormat;
        public Bool32 ShaderStorageImageWriteWithoutFormat;
        public Bool32 ShaderUniformBufferArrayDynamicIndexing;
        public Bool32 ShaderSampledImageArrayDynamicIndexing;
        public Bool32 ShaderStorageBufferArrayDynamicIndexing;
        public Bool32 ShaderStorageImageArrayDynamicIndexing;
        public Bool32 ShaderClipDistance;
        public Bool32 ShaderCullDistance;
        public Bool32 ShaderFloat64;
        public Bool32 ShaderInt64;
        public Bool32 ShaderInt16;
        public Bool32 ShaderResourceResidency;
        public Bool32 ShaderResourceMinLod;
        public Bool32 SparseBinding;
        public Bool32 SparseResidencyBuffer;
        public Bool32 SparseResidencyImage2D;
        public Bool32 SparseResidencyImage3D;
        public Bool32 SparseResidency2Samples;
        public Bool32 SparseResidency4Samples;
        public Bool32 SparseResidency8Samples;
        public Bool32 SparseResidency16Samples;
        public Bool32 SparseResidencyAliased;
        public Bool32 VariableMultisampleRate;
        public Bool32 InheritedQueries;
    }

    [StructLayout(LayoutKind.Sequential)]
    unsafe internal struct VkMemoryRequirements
    {
        public ulong Size;
        public ulong Alignment;
        public uint MemoryTypeBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    unsafe internal struct VkMemoryAllocateInfo
    {
        public VkStructureType SType;
        public IntPtr PNext;
        public ulong AllocationSize;
        public uint MemoryTypeIndex;
    }

    [StructLayout(LayoutKind.Sequential)]
    unsafe internal struct VkMappedMemoryRange
    {
        public VkStructureType SType;
        public IntPtr PNext;
        public VkDeviceMemory Memory;
        public ulong Offset;
        public ulong Size;
    }

    [StructLayout(LayoutKind.Sequential)]
    unsafe internal struct VkBufferCreateInfo
    {
        public VkStructureType SType;
        public IntPtr PNext;
        public VkBufferUsageFlags Usage;
        public VkSharingMode SharingMode;
        public uint QueueFamilyIndexCount;
        public uint* PQueueFamilyIndices;
        public ulong Size;
        public VkInstanceCreateFlags Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    unsafe internal struct VkImageCreateInfo
    {
        public VkStructureType SType;
        public IntPtr PNext;
        public VkImageCreateFlags Flags;
        public VkImageType ImageType;
        public VkFormat Format;
        public VkExtent3D Extent;
        public uint MipLevels;
        public uint ArrayLayers;
        public VkSampleCountFlags Samples;
        public VkImageTiling Tiling;
        public VkImageUsageFlags Usage;
        public VkSharingMode SharingMode;
        public uint QueueFamilyIndexCount;
        public uint* PQueueFamilyIndices;
        public VkImageLayout InitialLayout;
    }

    [StructLayout(LayoutKind.Sequential)]
    unsafe internal struct VkExtent3D { public uint Width; public uint Height; public uint Depth; }

    [StructLayout(LayoutKind.Sequential)]
    unsafe internal struct VkExtent2D { public uint Width; public uint Height; public VkExtent2D(uint w, uint h) { Width = w; Height = h; } }

    [StructLayout(LayoutKind.Sequential)]
    unsafe internal struct VkOffset2D { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    unsafe internal struct VkOffset3D { public int X; public int Y; public int Z; }

    [StructLayout(LayoutKind.Sequential)]
    unsafe internal struct VkRect2D { public VkOffset2D Offset; public VkExtent2D Extent; }

    [StructLayout(LayoutKind.Sequential)]
    unsafe internal struct VkViewport { public float X; public float Y; public float Width; public float Height; public float MinDepth; public float MaxDepth; }

    [StructLayout(LayoutKind.Sequential)]
    unsafe internal struct VkComponentMapping
    {
        public VkComponentSwizzle R;
        public VkComponentSwizzle G;
        public VkComponentSwizzle B;
        public VkComponentSwizzle A;
    }

    [StructLayout(LayoutKind.Sequential)]
    unsafe internal struct VkImageSubresourceRange
    {
        public VkImageAspectFlags AspectMask;
        public uint BaseMipLevel;
        public uint LevelCount;
        public uint BaseArrayLayer;
        public uint LayerCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    unsafe internal struct VkImageSubresource
    {
        public VkImageAspectFlags AspectMask;
        public uint MipLevel;
        public uint ArrayLayer;
    }

    [StructLayout(LayoutKind.Sequential)]
    unsafe internal struct VkSubresourceLayout
    {
        public ulong Offset;
        public ulong Size;
        public ulong RowPitch;
        public ulong ArrayPitch;
        public ulong DepthPitch;
    }

    [StructLayout(LayoutKind.Sequential)]
    unsafe internal struct VkImageViewCreateInfo
    {
        public VkStructureType SType;
        public IntPtr PNext;
        public VkInstanceCreateFlags Flags;
        public VkImage Image;
        public VkImageViewType ViewType;
        public VkFormat Format;
        public VkComponentMapping Components;
        public VkImageSubresourceRange SubresourceRange;
    }

    [StructLayout(LayoutKind.Sequential)]
    unsafe internal struct VkSamplerCreateInfo
    {
        public VkStructureType SType;
        public IntPtr PNext;
        public VkSamplerCreateFlags Flags;
        public VkFilter MagFilter;
        public VkFilter MinFilter;
        public VkSamplerMipmapMode MipmapMode;
        public VkSamplerAddressMode AddressModeU;
        public VkSamplerAddressMode AddressModeV;
        public VkSamplerAddressMode AddressModeW;
        public float MipLodBias;
        public Bool32 AnisotropyEnable;
        public float MaxAnisotropy;
        public Bool32 CompareEnable;
        public VkCompareOp CompareOp;
        public float MinLod;
        public float MaxLod;
        public VkBorderColor BorderColor;
        public Bool32 UnnormalizedCoordinates;
    }

    [StructLayout(LayoutKind.Sequential)]
    unsafe internal struct VkShaderModuleCreateInfo
    {
        public VkStructureType SType;
        public IntPtr PNext;
        public VkInstanceCreateFlags Flags;
        public ulong CodeSize;
        public uint* PCode;
    }

    [StructLayout(LayoutKind.Sequential)]
    unsafe internal struct VkPipelineShaderStageCreateInfo
    {
        public VkStructureType SType;
        public IntPtr PNext;
        public VkPipelineCreateFlags Flags;
        public VkShaderStageFlags Stage;
        public VkShaderModule Module;
        public IntPtr PName;
        public IntPtr PSpecializationInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    unsafe internal struct VkVertexInputBindingDescription
    {
        public uint Binding;
        public uint Stride;
        public int InputRate;
    }

    [StructLayout(LayoutKind.Sequential)]
    unsafe internal struct VkVertexInputAttributeDescription
    {
        public uint Location;
        public uint Binding;
        public VkFormat Format;
        public uint Offset;
    }

    [StructLayout(LayoutKind.Sequential)]
    unsafe internal struct VkPipelineVertexInputStateCreateInfo
    {
        public VkStructureType SType;
        public IntPtr PNext;
        public VkInstanceCreateFlags Flags;
        public uint VertexBindingDescriptionCount;
        public VkVertexInputBindingDescription* PVertexBindingDescriptions;
        public uint VertexAttributeDescriptionCount;
        public VkVertexInputAttributeDescription* PVertexAttributeDescriptions;
    }

    [StructLayout(LayoutKind.Sequential)]
    unsafe internal struct VkPipelineInputAssemblyStateCreateInfo
    {
        public VkStructureType SType;
        public IntPtr PNext;
        public VkInstanceCreateFlags Flags;
        public VkPrimitiveTopology Topology;
        public Bool32 PrimitiveRestartEnable;
    }

    [StructLayout(LayoutKind.Sequential)]
    unsafe internal struct VkPipelineViewportStateCreateInfo
    {
        public VkStructureType SType;
        public IntPtr PNext;
        public VkInstanceCreateFlags Flags;
        public uint ViewportCount;
        public VkViewport* PViewports;
        public uint ScissorCount;
        public VkRect2D* PScissors;
    }

    [StructLayout(LayoutKind.Sequential)]
    unsafe internal struct VkPipelineRasterizationStateCreateInfo
    {
        public VkStructureType SType;
        public IntPtr PNext;
        public VkInstanceCreateFlags Flags;
        public Bool32 DepthClampEnable;
        public Bool32 RasterizerDiscardEnable;
        public VkPolygonMode PolygonMode;
        public VkCullModeFlags CullMode;
        public VkFrontFace FrontFace;
        public Bool32 DepthBiasEnable;
        public float DepthBiasConstantFactor;
        public float DepthBiasClamp;
        public float DepthBiasSlopeFactor;
        public float LineWidth;
    }

    [StructLayout(LayoutKind.Sequential)]
    unsafe internal struct VkPipelineMultisampleStateCreateInfo
    {
        public VkStructureType SType;
        public IntPtr PNext;
        public VkInstanceCreateFlags Flags;
        public VkSampleCountFlags RasterizationSamples;
        public Bool32 SampleShadingEnable;
        public float MinSampleShading;
        public IntPtr PSampleMask;
        public Bool32 AlphaToCoverageEnable;
        public Bool32 AlphaToOneEnable;
    }

    [StructLayout(LayoutKind.Sequential)]
    unsafe internal struct VkStencilOpState
    {
        public VkStencilOp FailOp;
        public VkStencilOp PassOp;
        public VkStencilOp DepthFailOp;
        public VkCompareOp CompareOp;
        public uint CompareMask;
        public uint WriteMask;
        public uint Reference;
    }

    [StructLayout(LayoutKind.Sequential)]
    unsafe internal struct VkPipelineDepthStencilStateCreateInfo
    {
        public VkStructureType SType;
        public IntPtr PNext;
        public VkInstanceCreateFlags Flags;
        public Bool32 DepthTestEnable;
        public Bool32 DepthWriteEnable;
        public VkCompareOp DepthCompareOp;
        public Bool32 DepthBoundsTestEnable;
        public Bool32 StencilTestEnable;
        public VkStencilOpState Front;
        public VkStencilOpState Back;
        public float MinDepthBounds;
        public float MaxDepthBounds;
    }

    [StructLayout(LayoutKind.Sequential)]
    unsafe internal struct VkPipelineColorBlendAttachmentState
    {
        public Bool32 BlendEnable;
        public VkBlendFactor SrcColorBlendFactor;
        public VkBlendFactor DstColorBlendFactor;
        public VkBlendOp ColorBlendOp;
        public VkBlendFactor SrcAlphaBlendFactor;
        public VkBlendFactor DstAlphaBlendFactor;
        public VkBlendOp AlphaBlendOp;
        public VkColorComponentFlags ColorWriteMask;
    }

    [StructLayout(LayoutKind.Sequential)]
    unsafe internal struct VkPipelineColorBlendStateCreateInfo
    {
        public VkStructureType SType;
        public IntPtr PNext;
        public VkInstanceCreateFlags Flags;
        public Bool32 LogicOpEnable;
        public int LogicOp;
        public uint AttachmentCount;
        public VkPipelineColorBlendAttachmentState* PAttachments;
        public fixed float BlendConstants[4];
    }

    [StructLayout(LayoutKind.Sequential)]
    unsafe internal struct VkPipelineDynamicStateCreateInfo
    {
        public VkStructureType SType;
        public IntPtr PNext;
        public VkInstanceCreateFlags Flags;
        public uint DynamicStateCount;
        public VkDynamicState* PDynamicStates;
    }

    [StructLayout(LayoutKind.Sequential)]
    unsafe internal struct VkPipelineLayoutCreateInfo
    {
        public VkStructureType SType;
        public IntPtr PNext;
        public VkInstanceCreateFlags Flags;
        public uint SetLayoutCount;
        public VkDescriptorSetLayout* PSetLayouts;
        public uint PushConstantRangeCount;
        public VkPushConstantRange* pPushConstantRanges;
    }

    [StructLayout(LayoutKind.Sequential)]
    unsafe internal struct VkPushConstantRange
    {
        public VkShaderStageFlags StageFlags;
        public uint Offset;
        public uint Size;
    }

    [StructLayout(LayoutKind.Sequential)]
    unsafe internal struct VkGraphicsPipelineCreateInfo
    {
        public VkStructureType SType;
        public IntPtr PNext;
        public VkPipelineCreateFlags Flags;
        public uint StageCount;
        public VkPipelineShaderStageCreateInfo* PStages;
        public IntPtr PVertexInputState;
        public IntPtr PInputAssemblyState;
        public VkPipelineViewportStateCreateInfo* PViewportState;
        public VkPipelineRasterizationStateCreateInfo* PRasterizationState;
        public VkPipelineMultisampleStateCreateInfo* PMultisampleState;
        public VkPipelineDepthStencilStateCreateInfo* PDepthStencilState;
        public VkPipelineColorBlendStateCreateInfo* PColorBlendState;
        public VkPipelineDynamicStateCreateInfo* PDynamicState;
        public VkPipelineLayout Layout;
        public VkRenderPass RenderPass;
        public uint Subpass;
        public VkPipeline BasePipelineHandle;
        public int BasePipelineIndex;
    }

    [StructLayout(LayoutKind.Sequential)]
    unsafe internal struct VkComputePipelineCreateInfo
    {
        public VkStructureType SType;
        public IntPtr PNext;
        public VkPipelineCreateFlags Flags;
        public VkPipelineShaderStageCreateInfo Stage;
        public VkPipelineLayout Layout;
        public VkPipeline BasePipelineHandle;
        public int BasePipelineIndex;
    }

    [StructLayout(LayoutKind.Sequential)]
    unsafe internal struct VkPipelineRenderingCreateInfo
    {
        public VkStructureType SType;
        public IntPtr PNext;
        public uint ViewMask;
        public uint ColorAttachmentCount;
        public VkFormat* PColorAttachmentFormats;
        public VkFormat DepthAttachmentFormat;
        public VkFormat StencilAttachmentFormat;
    }

    [StructLayout(LayoutKind.Sequential)]
    unsafe internal struct VkDescriptorSetLayoutBinding
    {
        public uint Binding;
        public VkDescriptorType DescriptorType;
        public uint DescriptorCount;
        public VkShaderStageFlags StageFlags;
        public IntPtr PImmutableSamplers;
    }

    [StructLayout(LayoutKind.Sequential)]
    unsafe internal struct VkDescriptorSetLayoutCreateInfo
    {
        public VkStructureType SType;
        public IntPtr PNext;
        public VkDescriptorSetLayoutCreateFlags Flags;
        public uint BindingCount;
        public VkDescriptorSetLayoutBinding* PBindings;
    }

    [StructLayout(LayoutKind.Sequential)]
    unsafe internal struct VkDescriptorPoolSize
    {
        public VkDescriptorType Type;
        public uint DescriptorCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    unsafe internal struct VkDescriptorPoolCreateInfo
    {
        public VkStructureType SType;
        public IntPtr PNext;
        public VkDescriptorPoolCreateFlags Flags;
        public uint MaxSets;
        public uint PoolSizeCount;
        public VkDescriptorPoolSize* pPoolSizes;
    }

    [StructLayout(LayoutKind.Sequential)]
    unsafe internal struct VkDescriptorSetAllocateInfo
    {
        public VkStructureType SType;
        public IntPtr PNext;
        public VkDescriptorPool DescriptorPool;
        public uint DescriptorSetCount;
        public VkDescriptorSetLayout* PSetLayouts;
    }

    [StructLayout(LayoutKind.Sequential)]
    unsafe internal struct VkDescriptorBufferInfo
    {
        public VkBuffer Buffer;
        public ulong Offset;
        public ulong Range;
    }

    [StructLayout(LayoutKind.Sequential)]
    unsafe internal struct VkDescriptorImageInfo
    {
        public VkSampler Sampler;
        public VkImageView ImageView;
        public VkImageLayout ImageLayout;
    }

    [StructLayout(LayoutKind.Sequential)]
    unsafe internal struct VkWriteDescriptorSet
    {
        public VkStructureType SType;
        public IntPtr PNext;
        public VkDescriptorSet DstSet;
        public uint DstBinding;
        public uint DstArrayElement;
        public uint DescriptorCount;
        public VkDescriptorType DescriptorType;
        public VkDescriptorImageInfo* PImageInfo;
        public VkDescriptorBufferInfo* pBufferInfo;
        public IntPtr PTexelBufferView;
    }

    [StructLayout(LayoutKind.Sequential)]
    unsafe internal struct VkCommandPoolCreateInfo
    {
        public VkStructureType SType;
        public IntPtr PNext;
        public VkCommandPoolCreateFlags Flags;
        public uint QueueFamilyIndex;
    }

    [StructLayout(LayoutKind.Sequential)]
    unsafe internal struct VkCommandBufferAllocateInfo
    {
        public VkStructureType SType;
        public IntPtr PNext;
        public VkCommandPool CommandPool;
        public int Level;
        public uint CommandBufferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    unsafe internal struct VkCommandBufferBeginInfo
    {
        public VkStructureType SType;
        public IntPtr PNext;
        public VkCommandBufferUsageFlags Flags;
        public IntPtr PInheritanceInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    unsafe internal struct VkSubmitInfo
    {
        public VkStructureType SType;
        public IntPtr PNext;
        public uint WaitSemaphoreCount;
        public VkSemaphore* PWaitSemaphores;
        public VkPipelineStageFlags* PWaitDstStageMask;
        public uint CommandBufferCount;
        public VkCommandBuffer* PCommandBuffers;
        public uint SignalSemaphoreCount;
        public VkSemaphore* PSignalSemaphores;
    }

    [StructLayout(LayoutKind.Sequential)]
    unsafe internal struct VkSemaphoreCreateInfo
    {
        public VkStructureType SType;
        public IntPtr PNext;
        public VkSemaphoreCreateFlags Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    unsafe internal struct VkFenceCreateInfo
    {
        public VkStructureType SType;
        public IntPtr PNext;
        public VkFenceCreateFlags Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    unsafe internal struct VkImageMemoryBarrier
    {
        public VkStructureType SType;
        public IntPtr PNext;
        public VkAccessFlags SrcAccessMask;
        public VkAccessFlags DstAccessMask;
        public VkImageLayout OldLayout;
        public VkImageLayout NewLayout;
        public uint SrcQueueFamilyIndex;
        public uint DstQueueFamilyIndex;
        public VkImage Image;
        public VkImageSubresourceRange SubresourceRange;
    }

    [StructLayout(LayoutKind.Sequential)]
    unsafe internal struct VkBufferMemoryBarrier
    {
        public VkStructureType SType;
        public IntPtr PNext;
        public VkAccessFlags SrcAccessMask;
        public VkAccessFlags DstAccessMask;
        public uint SrcQueueFamilyIndex;
        public uint DstQueueFamilyIndex;
        public VkBuffer Buffer;
        public ulong Offset;
        public ulong Size;
    }

    [StructLayout(LayoutKind.Sequential)]
    unsafe internal struct VkMemoryBarrier
    {
        public VkStructureType SType;
        public IntPtr PNext;
        public VkAccessFlags SrcAccessMask;
        public VkAccessFlags DstAccessMask;
    }

    [StructLayout(LayoutKind.Sequential)]
    unsafe internal struct VkBufferImageCopy
    {
        public ulong BufferOffset;
        public uint BufferRowLength;
        public uint BufferImageHeight;
        public VkImageSubresource ImageSubresource;
        public VkOffset3D ImageOffset;
        public VkExtent3D ImageExtent;
    }

    [StructLayout(LayoutKind.Sequential)]
    unsafe internal struct VkBufferCopy
    {
        public ulong SrcOffset;
        public ulong DstOffset;
        public ulong Size;
    }

    [StructLayout(LayoutKind.Sequential)]
    unsafe internal struct VkClearColorValue
    {
        public fixed float Float32[4];
        public fixed int Int32[4];
        public fixed uint Uint32[4];
    }

    [StructLayout(LayoutKind.Sequential)]
    unsafe internal struct VkClearDepthStencilValue
    {
        public float Depth;
        public uint Stencil;
    }

    [StructLayout(LayoutKind.Sequential)]
    unsafe internal struct VkClearValue
    {
        public VkClearColorValue Color;
        public VkClearDepthStencilValue DepthStencil;
    }

    [StructLayout(LayoutKind.Sequential)]
    unsafe internal struct VkSurfaceCapabilitiesKHR
    {
        public uint MinImageCount;
        public uint MaxImageCount;
        public VkExtent2D CurrentExtent;
        public VkExtent2D MinImageExtent;
        public VkExtent2D MaxImageExtent;
        public uint MaxImageArrayLayers;
        public uint SupportedTransforms;
        public uint CurrentTransform;
        public uint SupportedCompositeAlpha;
        public uint SupportedUsageFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    unsafe internal struct VkSurfaceFormatKHR
    {
        public VkFormat Format;
        public VkColorSpaceKHR ColorSpace;
    }

    [StructLayout(LayoutKind.Sequential)]
    unsafe internal struct VkSwapchainCreateInfoKHR
    {
        public VkStructureType SType;
        public IntPtr PNext;
        public VkInstanceCreateFlags Flags;
        public VkSurfaceKHR Surface;
        public uint MinImageCount;
        public VkFormat ImageFormat;
        public VkColorSpaceKHR ImageColorSpace;
        public VkExtent2D ImageExtent;
        public uint ImageArrayLayers;
        public VkImageUsageFlags ImageUsage;
        public VkSharingMode ImageSharingMode;
        public uint QueueFamilyIndexCount;
        public uint* PQueueFamilyIndices;
        public uint PreTransform;
        public VkCompositeAlphaFlagsKHR CompositeAlpha;
        public VkPresentModeKHR PresentMode;
        public Bool32 Clipped;
        public VkSwapchainKHR OldSwapchain;
    }

    [StructLayout(LayoutKind.Sequential)]
    unsafe internal struct VkPresentInfoKHR
    {
        public VkStructureType SType;
        public IntPtr PNext;
        public uint WaitSemaphoreCount;
        public VkSemaphore* PWaitSemaphores;
        public uint SwapchainCount;
        public VkSwapchainKHR* PSwapchains;
        public uint* PImageIndices;
        public VkResult* PResults;
    }

    [StructLayout(LayoutKind.Sequential)]
    unsafe internal struct VkQueueFamilyProperties
    {
        public VkQueueFlags QueueFlags;
        public uint QueueCount;
        public uint TimestampValidBits;
        public VkExtent3D MinImageTransferGranularity;
    }

    [StructLayout(LayoutKind.Sequential)]
    unsafe internal struct VkWin32SurfaceCreateInfoKHR
    {
        public VkStructureType SType;
        public IntPtr PNext;
        public uint Flags;
        public IntPtr Hinstance;
        public IntPtr Hwnd;
    }

    [StructLayout(LayoutKind.Sequential)]
    unsafe internal struct VkSpecializationMapEntry
    {
        public uint ConstantID;
        public uint Offset;
        public uint Size;
    }

    [StructLayout(LayoutKind.Sequential)]
    unsafe internal struct VkSpecializationInfo
    {
        public uint MapEntryCount;
        public VkSpecializationMapEntry* pMapEntries;
        public ulong DataSize;
        public IntPtr pData;
    }

    #endregion

    // ════════════════════════════════════════════════════════════════
    // P/Invoke: core instance-level functions exported by vulkan-1.dll
    // ════════════════════════════════════════════════════════════════

    internal static unsafe class VulkanLoader
    {
        private const string Lib = "vulkan-1.dll";

        [DllImport(Lib, EntryPoint = "vkGetInstanceProcAddr", CallingConvention = CallingConvention.Winapi)]
        public static extern IntPtr GetInstanceProcAddr(ulong instance, sbyte* pName);

        [DllImport(Lib, EntryPoint = "vkEnumerateInstanceVersion", CallingConvention = CallingConvention.Winapi)]
        public static extern VkResult EnumerateInstanceVersion(out uint pApiVersion);

        [DllImport(Lib, EntryPoint = "vkEnumerateInstanceExtensionProperties", CallingConvention = CallingConvention.Winapi)]
        public static extern VkResult EnumerateInstanceExtensionProperties(IntPtr pLayerName, uint* pPropertyCount, void* pProperties);

        [DllImport(Lib, EntryPoint = "vkEnumerateInstanceLayerProperties", CallingConvention = CallingConvention.Winapi)]
        public static extern VkResult EnumerateInstanceLayerProperties(uint* pPropertyCount, void* pProperties);

        [DllImport(Lib, EntryPoint = "vkCreateInstance", CallingConvention = CallingConvention.Winapi)]
        public static extern VkResult CreateInstance(VkInstanceCreateInfo* pCreateInfo, IntPtr pAllocator, out ulong pInstance);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr GetModuleHandle(string? lpModuleName);

        public static IntPtr GetUtf8Ptr(string s)
        {
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(s + "\0");
            IntPtr ptr = Marshal.AllocHGlobal(bytes.Length);
            Marshal.Copy(bytes, 0, ptr, bytes.Length);
            return ptr;
        }
    }

    // ════════════════════════════════════════════════════════════════
    // VkApi — dynamically loaded Vulkan function pointers.
    // Loads device-level commands via vkGetInstanceProcAddr / vkGetDeviceProcAddr.
    // ════════════════════════════════════════════════════════════════

    internal sealed unsafe class VkApi
    {
        private readonly ulong _instance;
        private readonly ulong _device;
        private readonly Dictionary<string, IntPtr> _cache = new();

        // ── Delegate types ──
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate VkResult DelDestroyInstance(ulong instance, IntPtr allocator);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate VkResult DelEnumeratePhysicalDevices(ulong instance, uint* pCount, ulong* pPhysicalDevices);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate void DelGetPhysicalDeviceProperties(ulong physicalDevice, VkPhysicalDeviceProperties* pProperties);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate void DelGetPhysicalDeviceMemoryProperties(ulong physicalDevice, VkPhysicalDeviceMemoryProperties* pMemoryProperties);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate void DelGetPhysicalDeviceFeatures(ulong physicalDevice, VkPhysicalDeviceFeatures* pFeatures);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate VkResult DelCreateDevice(ulong physicalDevice, VkDeviceCreateInfo* pCreateInfo, IntPtr pAllocator, out ulong pDevice);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate void DelDestroyDevice(ulong device, IntPtr pAllocator);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate void DelGetDeviceQueue(ulong device, uint queueFamilyIndex, uint queueIndex, out ulong pQueue);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate void DelDeviceWaitIdle(ulong device);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate VkResult DelCreateBuffer(ulong device, VkBufferCreateInfo* pCreateInfo, IntPtr pAllocator, out ulong pBuffer);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate void DelDestroyBuffer(ulong device, ulong buffer, IntPtr pAllocator);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate void DelGetBufferMemoryRequirements(ulong device, ulong buffer, VkMemoryRequirements* pMemoryRequirements);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate VkResult DelCreateImage(ulong device, VkImageCreateInfo* pCreateInfo, IntPtr pAllocator, out ulong pImage);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate void DelDestroyImage(ulong device, ulong image, IntPtr pAllocator);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate void DelGetImageMemoryRequirements(ulong device, ulong image, VkMemoryRequirements* pMemoryRequirements);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate void DelGetImageSubresourceLayout(ulong device, ulong image, VkImageSubresource* pSubresource, VkSubresourceLayout* pLayout);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate VkResult DelAllocateMemory(ulong device, VkMemoryAllocateInfo* pAllocInfo, IntPtr pAllocator, out ulong pMemory);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate void DelFreeMemory(ulong device, ulong memory, IntPtr pAllocator);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate VkResult DelBindBufferMemory(ulong device, ulong buffer, ulong memory, ulong memoryOffset);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate VkResult DelBindImageMemory(ulong device, ulong image, ulong memory, ulong memoryOffset);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate VkResult DelMapMemory(ulong device, ulong memory, ulong offset, ulong size, uint flags, out IntPtr ppData);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate void DelUnmapMemory(ulong device, ulong memory);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate VkResult DelFlushMappedMemoryRanges(ulong device, uint memoryRangeCount, VkMappedMemoryRange* pMemoryRanges);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate VkResult DelCreateImageView(ulong device, VkImageViewCreateInfo* pCreateInfo, IntPtr pAllocator, out ulong pView);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate void DelDestroyImageView(ulong device, ulong imageView, IntPtr pAllocator);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate VkResult DelCreateSampler(ulong device, VkSamplerCreateInfo* pCreateInfo, IntPtr pAllocator, out ulong pSampler);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate void DelDestroySampler(ulong device, ulong sampler, IntPtr pAllocator);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate VkResult DelCreateShaderModule(ulong device, VkShaderModuleCreateInfo* pCreateInfo, IntPtr pAllocator, out ulong pShaderModule);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate void DelDestroyShaderModule(ulong device, ulong shaderModule, IntPtr pAllocator);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate VkResult DelCreatePipelineLayout(ulong device, VkPipelineLayoutCreateInfo* pCreateInfo, IntPtr pAllocator, out ulong pPipelineLayout);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate void DelDestroyPipelineLayout(ulong device, ulong pipelineLayout, IntPtr pAllocator);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate VkResult DelCreateGraphicsPipelines(ulong device, ulong pipelineCache, uint createInfoCount, VkGraphicsPipelineCreateInfo* pCreateInfos, IntPtr pAllocator, ulong* pPipelines);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate VkResult DelCreateComputePipelines(ulong device, ulong pipelineCache, uint createInfoCount, VkComputePipelineCreateInfo* pCreateInfos, IntPtr pAllocator, ulong* pPipelines);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate void DelDestroyPipeline(ulong device, ulong pipeline, IntPtr pAllocator);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate VkResult DelCreateDescriptorSetLayout(ulong device, VkDescriptorSetLayoutCreateInfo* pCreateInfo, IntPtr pAllocator, out ulong pSetLayout);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate void DelDestroyDescriptorSetLayout(ulong device, ulong descriptorSetLayout, IntPtr pAllocator);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate VkResult DelCreateDescriptorPool(ulong device, VkDescriptorPoolCreateInfo* pCreateInfo, IntPtr pAllocator, out ulong pDescriptorPool);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate void DelDestroyDescriptorPool(ulong device, ulong descriptorPool, IntPtr pAllocator);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate VkResult DelAllocateDescriptorSets(ulong device, VkDescriptorSetAllocateInfo* pAllocateInfo, ulong* pDescriptorSets);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate VkResult DelFreeDescriptorSets(ulong device, ulong descriptorPool, uint descriptorSetCount, ulong* pDescriptorSets);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate void DelUpdateDescriptorSets(ulong device, uint descriptorWriteCount, VkWriteDescriptorSet* pDescriptorWrites, uint descriptorCopyCount, void* pDescriptorCopies);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate VkResult DelCreateCommandPool(ulong device, VkCommandPoolCreateInfo* pCreateInfo, IntPtr pAllocator, out ulong pCommandPool);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate void DelDestroyCommandPool(ulong device, ulong commandPool, IntPtr pAllocator);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate VkResult DelAllocateCommandBuffers(ulong device, VkCommandBufferAllocateInfo* pAllocateInfo, ulong* pCommandBuffers);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate void DelFreeCommandBuffers(ulong device, ulong commandPool, uint commandBufferCount, ulong* pCommandBuffers);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate VkResult DelBeginCommandBuffer(ulong cmdBuffer, VkCommandBufferBeginInfo* pBeginInfo);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate VkResult DelEndCommandBuffer(ulong cmdBuffer);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate VkResult DelResetCommandBuffer(ulong cmdBuffer, uint flags);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate VkResult DelQueueSubmit(ulong queue, uint submitCount, VkSubmitInfo* pSubmits, ulong fence);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate void DelQueueWaitIdle(ulong queue);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate VkResult DelCreateSemaphore(ulong device, VkSemaphoreCreateInfo* pCreateInfo, IntPtr pAllocator, out ulong pSemaphore);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate void DelDestroySemaphore(ulong device, ulong semaphore, IntPtr pAllocator);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate VkResult DelCreateFence(ulong device, VkFenceCreateInfo* pCreateInfo, IntPtr pAllocator, out ulong pFence);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate void DelDestroyFence(ulong device, ulong fence, IntPtr pAllocator);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate VkResult DelWaitForFences(ulong device, uint fenceCount, ulong* pFences, Bool32 waitAll, ulong timeout);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate VkResult DelResetFences(ulong device, uint fenceCount, ulong* pFences);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate VkResult DelGetFenceStatus(ulong device, ulong fence);
        // Command recording
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate void DelCmdPipelineBarrier(ulong cmdBuffer, VkPipelineStageFlags srcStageMask, VkPipelineStageFlags dstStageMask, VkDependencyFlags dependencyFlags, uint memoryBarrierCount, void* pMemoryBarriers, uint bufferMemoryBarrierCount, void* pBufferMemoryBarriers, uint imageMemoryBarrierCount, VkImageMemoryBarrier* pImageMemoryBarriers);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate void DelCmdCopyBuffer(ulong cmdBuffer, ulong srcBuffer, ulong dstBuffer, uint regionCount, VkBufferCopy* pRegions);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate void DelCmdCopyBufferToImage(ulong cmdBuffer, ulong srcBuffer, ulong dstImage, VkImageLayout dstImageLayout, uint regionCount, VkBufferImageCopy* pRegions);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate void DelCmdClearColorImage(ulong cmdBuffer, ulong image, VkImageLayout imageLayout, VkClearColorValue* pColor, uint rangeCount, VkImageSubresourceRange* pRanges);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate void DelCmdClearDepthStencilImage(ulong cmdBuffer, ulong image, VkImageLayout imageLayout, VkClearDepthStencilValue* pDepthStencil, uint rangeCount, VkImageSubresourceRange* pRanges);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate void DelCmdSetViewport(ulong cmdBuffer, uint firstViewport, uint viewportCount, VkViewport* pViewports);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate void DelCmdSetScissor(ulong cmdBuffer, uint firstScissor, uint scissorCount, VkRect2D* pScissors);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate void DelCmdBindPipeline(ulong cmdBuffer, int pipelineBindPoint, ulong pipeline);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate void DelCmdBindDescriptorSets(ulong cmdBuffer, int pipelineBindPoint, ulong layout, uint firstSet, uint descriptorSetCount, ulong* pDescriptorSets, uint dynamicOffsetCount, uint* pDynamicOffsets);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate void DelCmdBindVertexBuffers(ulong cmdBuffer, uint firstBinding, uint bindingCount, ulong* pBuffers, ulong* pOffsets);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate void DelCmdBindIndexBuffer(ulong cmdBuffer, ulong buffer, ulong offset, VkIndexType indexType);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate void DelCmdDraw(ulong cmdBuffer, uint vertexCount, uint instanceCount, uint firstVertex, uint firstInstance);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate void DelCmdDrawIndexed(ulong cmdBuffer, uint indexCount, uint instanceCount, uint firstIndex, int vertexOffset, uint firstInstance);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate void DelCmdDrawIndirect(ulong cmdBuffer, ulong buffer, ulong offset, uint drawCount, uint stride);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate void DelCmdDrawIndexedIndirect(ulong cmdBuffer, ulong buffer, ulong offset, uint drawCount, uint stride);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate void DelCmdDispatch(ulong cmdBuffer, uint groupCountX, uint groupCountY, uint groupCountZ);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate void DelCmdDispatchIndirect(ulong cmdBuffer, ulong buffer, ulong offset);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate void DelCmdSetStencilRef(ulong cmdBuffer, int faceMask, uint reference);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate void DelCmdSetBlendConstants(ulong cmdBuffer, float* blendConstants);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate void DelCmdUpdateBuffer(ulong cmdBuffer, ulong dstBuffer, ulong dstOffset, uint dataSize, void* pData);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate void DelCmdFillBuffer(ulong cmdBuffer, ulong dstBuffer, ulong dstOffset, ulong size, uint data);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate void DelCmdPushConstants(ulong cmdBuffer, ulong layout, VkShaderStageFlags stageFlags, uint offset, uint size, void* pValues);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate void DelGetPhysicalDeviceQueueFamilyProperties(ulong physicalDevice, uint* pQueueFamilyPropertyCount, VkQueueFamilyProperties* pQueueFamilyProperties);
        // Swapchain KHR
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate VkResult DelCreateSwapchainKHR(ulong device, VkSwapchainCreateInfoKHR* pCreateInfo, IntPtr pAllocator, out ulong pSwapchain);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate void DelDestroySwapchainKHR(ulong device, ulong swapchain, IntPtr pAllocator);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate VkResult DelGetSwapchainImagesKHR(ulong device, ulong swapchain, uint* pSwapchainImageCount, ulong* pSwapchainImages);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate VkResult DelAcquireNextImageKHR(ulong device, ulong swapchain, ulong timeout, ulong semaphore, ulong fence, uint* pImageIndex);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate VkResult DelQueuePresentKHR(ulong queue, VkPresentInfoKHR* pPresentInfo);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate VkResult DelGetPhysicalDeviceSurfaceCapabilitiesKHR(ulong physicalDevice, ulong surface, VkSurfaceCapabilitiesKHR* pSurfaceCapabilities);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate VkResult DelGetPhysicalDeviceSurfaceFormatsKHR(ulong physicalDevice, ulong surface, uint* pSurfaceFormatCount, VkSurfaceFormatKHR* pSurfaceFormats);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate VkResult DelGetPhysicalDeviceSurfacePresentModesKHR(ulong physicalDevice, ulong surface, uint* pPresentModeCount, VkPresentModeKHR* pPresentModes);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate void DelDestroySurfaceKHR(ulong instance, ulong surface, IntPtr pAllocator);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate Bool32 DelGetPhysicalDeviceWin32PresentationSupportKHR(ulong physicalDevice, uint queueFamilyIndex);

        // ── Function pointer cache ──
        private IntPtr GetProc(string name)
        {
            if (_cache.TryGetValue(name, out IntPtr ptr)) return ptr;
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(name + "\0");
            fixed (byte* pName = bytes)
            {
                ptr = VulkanLoader.GetInstanceProcAddr(_instance, (sbyte*)pName);
            }
            _cache[name] = ptr;
            return ptr;
        }

        private T Load<T>(string name) where T : Delegate
        {
            IntPtr ptr = GetProc(name);
            if (ptr == IntPtr.Zero) return null!;
            return Marshal.GetDelegateForFunctionPointer<T>(ptr);
        }

        // ── Properties (lazy-loaded delegates) ──
        public VkApi(ulong instance, ulong device) { _instance = instance; _device = device; }

        public void DestroyInstance(IntPtr allocator)
        { Load<DelDestroyInstance>("vkDestroyInstance")?.Invoke(_instance, allocator); }
        public VkResult EnumeratePhysicalDevices(uint* pCount, ulong* pPhysicalDevices)
        { return Load<DelEnumeratePhysicalDevices>("vkEnumeratePhysicalDevices").Invoke(_instance, pCount, pPhysicalDevices); }
        public void GetPhysicalDeviceProperties(ulong physicalDevice, VkPhysicalDeviceProperties* pProperties)
        { Load<DelGetPhysicalDeviceProperties>("vkGetPhysicalDeviceProperties")?.Invoke(physicalDevice, pProperties); }
        public void GetPhysicalDeviceMemoryProperties(ulong physicalDevice, VkPhysicalDeviceMemoryProperties* pMemoryProperties)
        { Load<DelGetPhysicalDeviceMemoryProperties>("vkGetPhysicalDeviceMemoryProperties")?.Invoke(physicalDevice, pMemoryProperties); }
        public void GetPhysicalDeviceFeatures(ulong physicalDevice, VkPhysicalDeviceFeatures* pFeatures)
        { Load<DelGetPhysicalDeviceFeatures>("vkGetPhysicalDeviceFeatures")?.Invoke(physicalDevice, pFeatures); }
        public void GetPhysicalDeviceQueueFamilyProperties(ulong physicalDevice, uint* pCount, VkQueueFamilyProperties* pProps)
        { Load<DelGetPhysicalDeviceQueueFamilyProperties>("vkGetPhysicalDeviceQueueFamilyProperties")?.Invoke(physicalDevice, pCount, pProps); }
        public VkResult CreateDevice(ulong physicalDevice, VkDeviceCreateInfo* pCreateInfo, IntPtr pAllocator, out ulong pDevice)
        { return Load<DelCreateDevice>("vkCreateDevice").Invoke(physicalDevice, pCreateInfo, pAllocator, out pDevice); }
        public void DestroyDevice(IntPtr pAllocator)
        { Load<DelDestroyDevice>("vkDestroyDevice")?.Invoke(_device, pAllocator); }
        public void GetDeviceQueue(uint queueFamilyIndex, uint queueIndex, out ulong pQueue)
        { Load<DelGetDeviceQueue>("vkGetDeviceQueue")?.Invoke(_device, queueFamilyIndex, queueIndex, out pQueue); }
        public void DeviceWaitIdle()
        { Load<DelDeviceWaitIdle>("vkDeviceWaitIdle")?.Invoke(_device); }
        public VkResult CreateBuffer(VkBufferCreateInfo* pCreateInfo, IntPtr pAllocator, out ulong pBuffer)
        { return Load<DelCreateBuffer>("vkCreateBuffer").Invoke(_device, pCreateInfo, pAllocator, out pBuffer); }
        public void DestroyBuffer(ulong buffer, IntPtr pAllocator)
        { Load<DelDestroyBuffer>("vkDestroyBuffer")?.Invoke(_device, buffer, pAllocator); }
        public void GetBufferMemoryRequirements(ulong buffer, VkMemoryRequirements* pMemReq)
        { Load<DelGetBufferMemoryRequirements>("vkGetBufferMemoryRequirements")?.Invoke(_device, buffer, pMemReq); }
        public VkResult CreateImage(VkImageCreateInfo* pCreateInfo, IntPtr pAllocator, out ulong pImage)
        { return Load<DelCreateImage>("vkCreateImage").Invoke(_device, pCreateInfo, pAllocator, out pImage); }
        public void DestroyImage(ulong image, IntPtr pAllocator)
        { Load<DelDestroyImage>("vkDestroyImage")?.Invoke(_device, image, pAllocator); }
        public void GetImageMemoryRequirements(ulong image, VkMemoryRequirements* pMemReq)
        { Load<DelGetImageMemoryRequirements>("vkGetImageMemoryRequirements")?.Invoke(_device, image, pMemReq); }
        public void GetImageSubresourceLayout(ulong image, VkImageSubresource* pSub, VkSubresourceLayout* pLayout)
        { Load<DelGetImageSubresourceLayout>("vkGetImageSubresourceLayout")?.Invoke(_device, image, pSub, pLayout); }
        public VkResult AllocateMemory(VkMemoryAllocateInfo* pAllocInfo, IntPtr pAllocator, out ulong pMemory)
        { return Load<DelAllocateMemory>("vkAllocateMemory").Invoke(_device, pAllocInfo, pAllocator, out pMemory); }
        public void FreeMemory(ulong memory, IntPtr pAllocator)
        { Load<DelFreeMemory>("vkFreeMemory")?.Invoke(_device, memory, pAllocator); }
        public VkResult BindBufferMemory(ulong buffer, ulong memory, ulong memoryOffset)
        { return Load<DelBindBufferMemory>("vkBindBufferMemory").Invoke(_device, buffer, memory, memoryOffset); }
        public VkResult BindImageMemory(ulong image, ulong memory, ulong memoryOffset)
        { return Load<DelBindImageMemory>("vkBindImageMemory").Invoke(_device, image, memory, memoryOffset); }
        public VkResult MapMemory(ulong memory, ulong offset, ulong size, uint flags, out IntPtr ppData)
        { return Load<DelMapMemory>("vkMapMemory").Invoke(_device, memory, offset, size, flags, out ppData); }
        public void UnmapMemory(ulong memory)
        { Load<DelUnmapMemory>("vkUnmapMemory")?.Invoke(_device, memory); }
        public VkResult FlushMappedMemoryRanges(uint count, VkMappedMemoryRange* pRanges)
        { return Load<DelFlushMappedMemoryRanges>("vkFlushMappedMemoryRanges").Invoke(_device, count, pRanges); }
        public VkResult CreateImageView(VkImageViewCreateInfo* pCreateInfo, IntPtr pAllocator, out ulong pView)
        { return Load<DelCreateImageView>("vkCreateImageView").Invoke(_device, pCreateInfo, pAllocator, out pView); }
        public void DestroyImageView(ulong imageView, IntPtr pAllocator)
        { Load<DelDestroyImageView>("vkDestroyImageView")?.Invoke(_device, imageView, pAllocator); }
        public VkResult CreateSampler(VkSamplerCreateInfo* pCreateInfo, IntPtr pAllocator, out ulong pSampler)
        { return Load<DelCreateSampler>("vkCreateSampler").Invoke(_device, pCreateInfo, pAllocator, out pSampler); }
        public void DestroySampler(ulong sampler, IntPtr pAllocator)
        { Load<DelDestroySampler>("vkDestroySampler")?.Invoke(_device, sampler, pAllocator); }
        public VkResult CreateShaderModule(VkShaderModuleCreateInfo* pCreateInfo, IntPtr pAllocator, out ulong pModule)
        { return Load<DelCreateShaderModule>("vkCreateShaderModule").Invoke(_device, pCreateInfo, pAllocator, out pModule); }
        public void DestroyShaderModule(ulong module, IntPtr pAllocator)
        { Load<DelDestroyShaderModule>("vkDestroyShaderModule")?.Invoke(_device, module, pAllocator); }
        public VkResult CreatePipelineLayout(VkPipelineLayoutCreateInfo* pCreateInfo, IntPtr pAllocator, out ulong pLayout)
        { return Load<DelCreatePipelineLayout>("vkCreatePipelineLayout").Invoke(_device, pCreateInfo, pAllocator, out pLayout); }
        public void DestroyPipelineLayout(ulong layout, IntPtr pAllocator)
        { Load<DelDestroyPipelineLayout>("vkDestroyPipelineLayout")?.Invoke(_device, layout, pAllocator); }
        public VkResult CreateGraphicsPipelines(ulong cache, uint count, VkGraphicsPipelineCreateInfo* pCreateInfos, IntPtr pAllocator, ulong* pPipelines)
        { return Load<DelCreateGraphicsPipelines>("vkCreateGraphicsPipelines").Invoke(_device, cache, count, pCreateInfos, pAllocator, pPipelines); }
        public VkResult CreateComputePipelines(ulong cache, uint count, VkComputePipelineCreateInfo* pCreateInfos, IntPtr pAllocator, ulong* pPipelines)
        { return Load<DelCreateComputePipelines>("vkCreateComputePipelines").Invoke(_device, cache, count, pCreateInfos, pAllocator, pPipelines); }
        public void DestroyPipeline(ulong pipeline, IntPtr pAllocator)
        { Load<DelDestroyPipeline>("vkDestroyPipeline")?.Invoke(_device, pipeline, pAllocator); }
        public VkResult CreateDescriptorSetLayout(VkDescriptorSetLayoutCreateInfo* pCreateInfo, IntPtr pAllocator, out ulong pSetLayout)
        { return Load<DelCreateDescriptorSetLayout>("vkCreateDescriptorSetLayout").Invoke(_device, pCreateInfo, pAllocator, out pSetLayout); }
        public void DestroyDescriptorSetLayout(ulong layout, IntPtr pAllocator)
        { Load<DelDestroyDescriptorSetLayout>("vkDestroyDescriptorSetLayout")?.Invoke(_device, layout, pAllocator); }
        public VkResult CreateDescriptorPool(VkDescriptorPoolCreateInfo* pCreateInfo, IntPtr pAllocator, out ulong pPool)
        { return Load<DelCreateDescriptorPool>("vkCreateDescriptorPool").Invoke(_device, pCreateInfo, pAllocator, out pPool); }
        public void DestroyDescriptorPool(ulong pool, IntPtr pAllocator)
        { Load<DelDestroyDescriptorPool>("vkDestroyDescriptorPool")?.Invoke(_device, pool, pAllocator); }
        public VkResult AllocateDescriptorSets(VkDescriptorSetAllocateInfo* pAllocInfo, ulong* pSets)
        { return Load<DelAllocateDescriptorSets>("vkAllocateDescriptorSets").Invoke(_device, pAllocInfo, pSets); }
        public VkResult FreeDescriptorSets(ulong pool, uint count, ulong* pSets)
        { return Load<DelFreeDescriptorSets>("vkFreeDescriptorSets").Invoke(_device, pool, count, pSets); }
        public void UpdateDescriptorSets(uint writeCount, VkWriteDescriptorSet* pWrites, uint copyCount, void* pCopies)
        { Load<DelUpdateDescriptorSets>("vkUpdateDescriptorSets")?.Invoke(_device, writeCount, pWrites, copyCount, pCopies); }
        public VkResult CreateCommandPool(VkCommandPoolCreateInfo* pCreateInfo, IntPtr pAllocator, out ulong pPool)
        { return Load<DelCreateCommandPool>("vkCreateCommandPool").Invoke(_device, pCreateInfo, pAllocator, out pPool); }
        public void DestroyCommandPool(ulong pool, IntPtr pAllocator)
        { Load<DelDestroyCommandPool>("vkDestroyCommandPool")?.Invoke(_device, pool, pAllocator); }
        public VkResult AllocateCommandBuffers(VkCommandBufferAllocateInfo* pAllocInfo, ulong* pCmdBuffers)
        { return Load<DelAllocateCommandBuffers>("vkAllocateCommandBuffers").Invoke(_device, pAllocInfo, pCmdBuffers); }
        public void FreeCommandBuffers(ulong pool, uint count, ulong* pCmdBuffers)
        { Load<DelFreeCommandBuffers>("vkFreeCommandBuffers")?.Invoke(_device, pool, count, pCmdBuffers); }
        public VkResult BeginCommandBuffer(ulong cmdBuffer, VkCommandBufferBeginInfo* pBeginInfo)
        { return Load<DelBeginCommandBuffer>("vkBeginCommandBuffer").Invoke(cmdBuffer, pBeginInfo); }
        public VkResult EndCommandBuffer(ulong cmdBuffer)
        { return Load<DelEndCommandBuffer>("vkEndCommandBuffer").Invoke(cmdBuffer); }
        public VkResult ResetCommandBuffer(ulong cmdBuffer, uint flags)
        { return Load<DelResetCommandBuffer>("vkResetCommandBuffer").Invoke(cmdBuffer, flags); }
        public VkResult QueueSubmit(ulong queue, uint count, VkSubmitInfo* pSubmits, ulong fence)
        { return Load<DelQueueSubmit>("vkQueueSubmit").Invoke(queue, count, pSubmits, fence); }
        public void QueueWaitIdle(ulong queue)
        { Load<DelQueueWaitIdle>("vkQueueWaitIdle")?.Invoke(queue); }
        public VkResult CreateSemaphore(VkSemaphoreCreateInfo* pCreateInfo, IntPtr pAllocator, out ulong pSemaphore)
        { return Load<DelCreateSemaphore>("vkCreateSemaphore").Invoke(_device, pCreateInfo, pAllocator, out pSemaphore); }
        public void DestroySemaphore(ulong semaphore, IntPtr pAllocator)
        { Load<DelDestroySemaphore>("vkDestroySemaphore")?.Invoke(_device, semaphore, pAllocator); }
        public VkResult CreateFence(VkFenceCreateInfo* pCreateInfo, IntPtr pAllocator, out ulong pFence)
        { return Load<DelCreateFence>("vkCreateFence").Invoke(_device, pCreateInfo, pAllocator, out pFence); }
        public void DestroyFence(ulong fence, IntPtr pAllocator)
        { Load<DelDestroyFence>("vkDestroyFence")?.Invoke(_device, fence, pAllocator); }
        public VkResult WaitForFences(uint count, ulong* pFences, Bool32 waitAll, ulong timeout)
        { return Load<DelWaitForFences>("vkWaitForFences").Invoke(_device, count, pFences, waitAll, timeout); }
        public VkResult ResetFences(uint count, ulong* pFences)
        { return Load<DelResetFences>("vkResetFences").Invoke(_device, count, pFences); }
        public VkResult GetFenceStatus(ulong fence)
        { return Load<DelGetFenceStatus>("vkGetFenceStatus").Invoke(_device, fence); }
        // Commands
        public void CmdPipelineBarrier(ulong cmdBuffer, VkPipelineStageFlags src, VkPipelineStageFlags dst, VkDependencyFlags dep, uint mbCount, void* pMB, uint bmbCount, void* pBMB, uint imbCount, VkImageMemoryBarrier* pIMB)
        { Load<DelCmdPipelineBarrier>("vkCmdPipelineBarrier")?.Invoke(cmdBuffer, src, dst, dep, mbCount, pMB, bmbCount, pBMB, imbCount, pIMB); }
        public void CmdCopyBuffer(ulong cmdBuffer, ulong src, ulong dst, uint count, VkBufferCopy* pRegions)
        { Load<DelCmdCopyBuffer>("vkCmdCopyBuffer")?.Invoke(cmdBuffer, src, dst, count, pRegions); }
        public void CmdCopyBufferToImage(ulong cmdBuffer, ulong srcBuffer, ulong dstImage, VkImageLayout layout, uint count, VkBufferImageCopy* pRegions)
        { Load<DelCmdCopyBufferToImage>("vkCmdCopyBufferToImage")?.Invoke(cmdBuffer, srcBuffer, dstImage, layout, count, pRegions); }
        public void CmdClearColorImage(ulong cmdBuffer, ulong image, VkImageLayout layout, VkClearColorValue* pColor, uint count, VkImageSubresourceRange* pRanges)
        { Load<DelCmdClearColorImage>("vkCmdClearColorImage")?.Invoke(cmdBuffer, image, layout, pColor, count, pRanges); }
        public void CmdClearDepthStencilImage(ulong cmdBuffer, ulong image, VkImageLayout layout, VkClearDepthStencilValue* pDS, uint count, VkImageSubresourceRange* pRanges)
        { Load<DelCmdClearDepthStencilImage>("vkCmdClearDepthStencilImage")?.Invoke(cmdBuffer, image, layout, pDS, count, pRanges); }
        public void CmdSetViewport(ulong cmdBuffer, uint first, uint count, VkViewport* pViewports)
        { Load<DelCmdSetViewport>("vkCmdSetViewport")?.Invoke(cmdBuffer, first, count, pViewports); }
        public void CmdSetScissor(ulong cmdBuffer, uint first, uint count, VkRect2D* pScissors)
        { Load<DelCmdSetScissor>("vkCmdSetScissor")?.Invoke(cmdBuffer, first, count, pScissors); }
        public void CmdBindPipeline(ulong cmdBuffer, int bindPoint, ulong pipeline)
        { Load<DelCmdBindPipeline>("vkCmdBindPipeline")?.Invoke(cmdBuffer, bindPoint, pipeline); }
        public void CmdBindDescriptorSets(ulong cmdBuffer, int bindPoint, ulong layout, uint firstSet, uint count, ulong* pSets, uint dynCount, uint* pDynOffsets)
        { Load<DelCmdBindDescriptorSets>("vkCmdBindDescriptorSets")?.Invoke(cmdBuffer, bindPoint, layout, firstSet, count, pSets, dynCount, pDynOffsets); }
        public void CmdBindVertexBuffers(ulong cmdBuffer, uint first, uint count, ulong* pBuffers, ulong* pOffsets)
        { Load<DelCmdBindVertexBuffers>("vkCmdBindVertexBuffers")?.Invoke(cmdBuffer, first, count, pBuffers, pOffsets); }
        public void CmdBindIndexBuffer(ulong cmdBuffer, ulong buffer, ulong offset, VkIndexType type)
        { Load<DelCmdBindIndexBuffer>("vkCmdBindIndexBuffer")?.Invoke(cmdBuffer, buffer, offset, type); }
        public void CmdDraw(ulong cmdBuffer, uint vertexCount, uint instanceCount, uint firstVertex, uint firstInstance)
        { Load<DelCmdDraw>("vkCmdDraw")?.Invoke(cmdBuffer, vertexCount, instanceCount, firstVertex, firstInstance); }
        public void CmdDrawIndexed(ulong cmdBuffer, uint indexCount, uint instanceCount, uint firstIndex, int vertexOffset, uint firstInstance)
        { Load<DelCmdDrawIndexed>("vkCmdDrawIndexed")?.Invoke(cmdBuffer, indexCount, instanceCount, firstIndex, vertexOffset, firstInstance); }
        public void CmdDrawIndirect(ulong cmdBuffer, ulong buffer, ulong offset, uint count, uint stride)
        { Load<DelCmdDrawIndirect>("vkCmdDrawIndirect")?.Invoke(cmdBuffer, buffer, offset, count, stride); }
        public void CmdDrawIndexedIndirect(ulong cmdBuffer, ulong buffer, ulong offset, uint count, uint stride)
        { Load<DelCmdDrawIndexedIndirect>("vkCmdDrawIndexedIndirect")?.Invoke(cmdBuffer, buffer, offset, count, stride); }
        public void CmdDispatch(ulong cmdBuffer, uint x, uint y, uint z)
        { Load<DelCmdDispatch>("vkCmdDispatch")?.Invoke(cmdBuffer, x, y, z); }
        public void CmdDispatchIndirect(ulong cmdBuffer, ulong buffer, ulong offset)
        { Load<DelCmdDispatchIndirect>("vkCmdDispatchIndirect")?.Invoke(cmdBuffer, buffer, offset); }
        public void CmdSetStencilRef(ulong cmdBuffer, int faceMask, uint reference)
        { Load<DelCmdSetStencilRef>("vkCmdSetStencilRef")?.Invoke(cmdBuffer, faceMask, reference); }
        public void CmdSetBlendConstants(ulong cmdBuffer, float* pConstants)
        { Load<DelCmdSetBlendConstants>("vkCmdSetBlendConstants")?.Invoke(cmdBuffer, pConstants); }
        public void CmdUpdateBuffer(ulong cmdBuffer, ulong dstBuffer, ulong dstOffset, uint dataSize, void* pData)
        { Load<DelCmdUpdateBuffer>("vkCmdUpdateBuffer")?.Invoke(cmdBuffer, dstBuffer, dstOffset, dataSize, pData); }
        public void CmdFillBuffer(ulong cmdBuffer, ulong dstBuffer, ulong dstOffset, ulong size, uint data)
        { Load<DelCmdFillBuffer>("vkCmdFillBuffer")?.Invoke(cmdBuffer, dstBuffer, dstOffset, size, data); }
        public void CmdPushConstants(ulong cmdBuffer, ulong layout, VkShaderStageFlags stage, uint offset, uint size, void* pValues)
        { Load<DelCmdPushConstants>("vkCmdPushConstants")?.Invoke(cmdBuffer, layout, stage, offset, size, pValues); }
        // Swapchain
        public VkResult CreateSwapchainKHR(VkSwapchainCreateInfoKHR* pCreateInfo, IntPtr pAllocator, out ulong pSwapchain)
        { return Load<DelCreateSwapchainKHR>("vkCreateSwapchainKHR").Invoke(_device, pCreateInfo, pAllocator, out pSwapchain); }
        public void DestroySwapchainKHR(ulong swapchain, IntPtr pAllocator)
        { Load<DelDestroySwapchainKHR>("vkDestroySwapchainKHR")?.Invoke(_device, swapchain, pAllocator); }
        public VkResult GetSwapchainImagesKHR(ulong swapchain, uint* pCount, ulong* pImages)
        { return Load<DelGetSwapchainImagesKHR>("vkGetSwapchainImagesKHR").Invoke(_device, swapchain, pCount, pImages); }
        public VkResult AcquireNextImageKHR(ulong swapchain, ulong timeout, ulong semaphore, ulong fence, uint* pImageIndex)
        { return Load<DelAcquireNextImageKHR>("vkAcquireNextImageKHR").Invoke(_device, swapchain, timeout, semaphore, fence, pImageIndex); }
        public VkResult QueuePresentKHR(ulong queue, VkPresentInfoKHR* pPresentInfo)
        { return Load<DelQueuePresentKHR>("vkQueuePresentKHR").Invoke(queue, pPresentInfo); }
        public VkResult GetPhysicalDeviceSurfaceCapabilitiesKHR(ulong physicalDevice, ulong surface, VkSurfaceCapabilitiesKHR* pCaps)
        { return Load<DelGetPhysicalDeviceSurfaceCapabilitiesKHR>("vkGetPhysicalDeviceSurfaceCapabilitiesKHR").Invoke(physicalDevice, surface, pCaps); }
        public VkResult GetPhysicalDeviceSurfaceFormatsKHR(ulong physicalDevice, ulong surface, uint* pCount, VkSurfaceFormatKHR* pFormats)
        { return Load<DelGetPhysicalDeviceSurfaceFormatsKHR>("vkGetPhysicalDeviceSurfaceFormatsKHR").Invoke(physicalDevice, surface, pCount, pFormats); }
        public VkResult GetPhysicalDeviceSurfacePresentModesKHR(ulong physicalDevice, ulong surface, uint* pCount, VkPresentModeKHR* pModes)
        { return Load<DelGetPhysicalDeviceSurfacePresentModesKHR>("vkGetPhysicalDeviceSurfacePresentModesKHR").Invoke(physicalDevice, surface, pCount, pModes); }
        public void DestroySurfaceKHR(ulong surface, IntPtr pAllocator)
        { Load<DelDestroySurfaceKHR>("vkDestroySurfaceKHR")?.Invoke(_instance, surface, pAllocator); }
        public Bool32 GetPhysicalDeviceWin32PresentationSupportKHR(ulong physicalDevice, uint queueFamilyIndex)
        { return Load<DelGetPhysicalDeviceWin32PresentationSupportKHR>("vkGetPhysicalDeviceWin32PresentationSupportKHR").Invoke(physicalDevice, queueFamilyIndex); }
    }

    // ════════════════════════════════════════════════════════════════
    // Helper classes
    // ════════════════════════════════════════════════════════════════


}
