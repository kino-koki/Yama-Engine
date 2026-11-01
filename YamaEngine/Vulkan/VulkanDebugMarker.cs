// =============================================================================
// VulkanDebugMarker.cs - yama引擎 Vulkan 调试标记系统
// =============================================================================
// 这套东西给 RenderDoc/Pix/NSight 用的，让它们能显示资源名字、scope 颜色等。
// 两个扩展：
//   - VK_EXT_debug_utils: 推荐用，功能全 (能指定颜色、queue 标记、object tag)
//   - VK_EXT_debug_marker: 老的，device 端标记
//
// 没 validation layer 的话这些标记不会做任何事，但开销很小，
// 所以生产环境也可以留着。
//
// FIXME: utils 回调 (message severity callback) 还没接
// TODO: 加 GPU 端的 debug label 颜色规范
//
// 作者: yama引擎团队
// =============================================================================

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.EXT;

using Buffer = Silk.NET.Vulkan.Buffer;
using Semaphore = Silk.NET.Vulkan.Semaphore;
namespace YamaEngine.Vulkan
{
    // ════════════════════════════════════════════════════════════════════════════
    // 区域 1: 公共类型
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>调试标记颜色 (RGBA, 0~1)。</summary>
    public struct DebugColor
    {
        public float R, G, B, A;

        public DebugColor(float r, float g, float b, float a = 1f)
        { R = r; G = g; B = b; A = a; }

        public static DebugColor Red => new(1, 0, 0);
        public static DebugColor Green => new(0, 1, 0);
        public static DebugColor Blue => new(0, 0, 1);
        public static DebugColor Yellow => new(1, 1, 0);
        public static DebugColor Cyan => new(0, 1, 1);
        public static DebugColor Magenta => new(1, 0, 1);
        public static DebugColor White => new(1, 1, 1);
        public static DebugColor Gray => new(0.5f, 0.5f, 0.5f);
        public static DebugColor Orange => new(1, 0.5f, 0);
        public static DebugColor Purple => new(0.5f, 0, 0.5f);

        /// <summary>按 pass 类型给个默认颜色。</summary>
        public static DebugColor ForPassType(string passType) => passType?.ToLowerInvariant() switch
        {
            "shadow" => new DebugColor(0.2f, 0.2f, 0.2f),
            "geometrybuffer" => new DebugColor(0.3f, 0.6f, 0.3f),
            "lighting" => new DebugColor(0.8f, 0.6f, 0.2f),
            "transparent" => new DebugColor(0.5f, 0.8f, 1.0f),
            "postprocess" => new DebugColor(0.8f, 0.4f, 1.0f),
            "ui" => new DebugColor(1.0f, 1.0f, 0.4f),
            "compute" => new DebugColor(0.4f, 1.0f, 0.8f),
            "transfer" => new DebugColor(0.6f, 0.6f, 0.6f),
            _ => Gray,
        };
    }

    /// <summary>调试消息严重性。</summary>
    [Flags]
    public enum DebugMessageSeverity : uint
    {
        Verbose = 0x1,
        Info = 0x10,
        Warning = 0x100,
        Error = 0x1000,
        All = Verbose | Info | Warning | Error,
    }

    /// <summary>调试消息类型。</summary>
    [Flags]
    public enum DebugMessageType : uint
    {
        General = 0x1,
        Validation = 0x2,
        Performance = 0x4,
        All = General | Validation | Performance,
    }

    /// <summary>调试对象类型。</summary>
    public enum DebugObjectType
    {
        Unknown,
        Instance,
        PhysicalDevice,
        Device,
        Queue,
        Semaphore,
        CommandBuffer,
        Fence,
        DeviceMemory,
        Buffer,
        Image,
        Event,
        QueryPool,
        BufferView,
        ImageView,
        ShaderModule,
        PipelineCache,
        PipelineLayout,
        RenderPass,
        Pipeline,
        DescriptorSetLayout,
        Sampler,
        DescriptorPool,
        DescriptorSet,
        Framebuffer,
        CommandPool,
        Swapchain,
    }

    /// <summary>调试统计。</summary>
    public struct DebugMarkerStats
    {
        public int ObjectNamesSet;
        public int BeginLabels;
        public int EndLabels;
        public int InsertLabels;
        public int MessagesReceived;
        public int ValidationErrors;
        public int ValidationWarnings;
        public int PerformanceWarnings;

        public override string ToString()
            => $"DebugMarker: names={ObjectNamesSet}, " +
               $"begin/end/insert={BeginLabels}/{EndLabels}/{InsertLabels}, " +
               $"msgs={MessagesReceived} (err={ValidationErrors}, warn={ValidationWarnings}, perf={PerformanceWarnings})";
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 2: Debug Utils 回调
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>调试消息回调委托。</summary>
    public delegate void DebugMessageCallback(DebugMessageSeverity severity, DebugMessageType type,
                                              string message);

    /// <summary>
    /// debug messenger 回调。validation layer 的消息都走这里。
    /// </summary>
    public sealed unsafe class DebugMessenger : IDisposable
    {
        private readonly Vk _vk;
        private readonly Instance _instance;
        private readonly ExtDebugUtils _ext;
        private DebugUtilsMessengerEXT _messenger;
        private readonly DebugMessageCallback _callback;
        private readonly DebugMessageSeverity _severities;
        private readonly DebugMessageType _types;
        private bool _disposed;

        public DebugMessenger(Vk vk, Instance instance, ExtDebugUtils ext,
                              DebugMessageCallback callback,
                              DebugMessageSeverity severities = DebugMessageSeverity.Warning | DebugMessageSeverity.Error,
                              DebugMessageType types = DebugMessageType.All)
        {
            _vk = vk;
            _instance = instance;
            _ext = ext;
            _callback = callback;
            _severities = severities;
            _types = types;
        }

        public void Create()
        {
            var info = new DebugUtilsMessengerCreateInfoEXT
            {
                SType = StructureType.DebugUtilsMessengerCreateInfoExt,
                MessageSeverity = (DebugUtilsMessageSeverityFlagsEXT)_severities,
                MessageType = (DebugUtilsMessageTypeFlagsEXT)_types,
                PfnUserCallback = new PfnDebugUtilsMessengerCallbackEXT(MessengerCallback),
            };

            fixed (DebugUtilsMessengerEXT* pMsg = &_messenger)
            {
                var r = _ext.CreateDebugUtilsMessenger(_instance, info, null, pMsg);
                if (r != Result.Success)
                    Console.Error.WriteLine($"[DebugMessenger] 创建失败: {r}");
            }
        }

        private uint MessengerCallback(DebugUtilsMessageSeverityFlagsEXT severity,
                                       DebugUtilsMessageTypeFlagsEXT type,
                                       DebugUtilsMessengerCallbackDataEXT* pData,
                                       void* pUserData)
        {
            string msg = pData->PMessage != null
                ? Marshal.PtrToStringAnsi((IntPtr)pData->PMessage)
                : "(null)";

            var s = (DebugMessageSeverity)severity;
            var t = (DebugMessageType)type;

            try
            {
                _callback?.Invoke(s, t, msg);
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"[DebugMessenger] 回调异常: {e}");
            }

            // 返回非 0 会中断调用，一般返回 0
            return 0;
        }

        public void Dispose()
        {
            if (_disposed) return;
            if (_messenger.Handle != 0)
                _ext.DestroyDebugUtilsMessenger(_instance, _messenger, null);
            _disposed = true;
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 3: 调试标记管理器
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 调试标记管理器主类。
    /// 用 VK_EXT_debug_utils 给 Vulkan 对象起名字、给 command buffer 加 scope label。
    /// </summary>
    public sealed unsafe class VulkanDebugMarker : IDisposable
    {
        private readonly Vk _vk;
        private readonly Device _device;
        private readonly Instance _instance;
        private readonly ExtDebugUtils _ext;
        private readonly bool _enabled;
        private DebugMarkerStats _stats;
        private readonly object _lock = new();

        // 存所有命名过的对象，方便 hot reload 时清理
        private readonly Dictionary<ulong, string> _namedObjects = new();

        public bool IsEnabled => _enabled;
        public ref DebugMarkerStats Stats => ref _stats;

        public VulkanDebugMarker(Vk vk, Instance instance, Device device)
        {
            _vk = vk;
            _instance = instance;
            _device = device;
            _stats = new DebugMarkerStats();

            _enabled = vk.TryGetInstanceExtension(instance, out _ext);
            if (!_enabled)
            {
                Console.WriteLine("[DebugMarker] VK_EXT_debug_utils 不可用，调试标记禁用");
            }
        }

        /// <summary>给对象起名字。</summary>
        public void SetObjectName(ulong handle, DebugObjectType type, string name)
        {
            if (!_enabled || string.IsNullOrEmpty(name)) return;

            var info = new DebugUtilsObjectNameInfoEXT
            {
                SType = StructureType.DebugUtilsObjectNameInfoExt,
                ObjectType = MapObjectType(type),
                ObjectHandle = handle,
                PObjectName = (byte*)Marshal.StringToHGlobalAnsi(name),
            };

            var r = _ext.SetDebugUtilsObjectName(_device, info);
            Marshal.FreeHGlobal((IntPtr)info.PObjectName);
            if (r != Result.Success)
            {
                Console.Error.WriteLine($"[DebugMarker] SetObjectName 失败: {r}");
                return;
            }

            lock (_lock)
            {
                _namedObjects[handle] = name;
                _stats.ObjectNamesSet++;
            }
        }

        /// <summary>给对象打 tag (二进制数据)。</summary>
        public void SetObjectTag(ulong handle, DebugObjectType type, ulong tagName,
                                 byte[] data)
        {
            if (!_enabled || data == null || data.Length == 0) return;

            fixed (byte* pData = data)
            {
                var info = new DebugUtilsObjectTagInfoEXT
                {
                    SType = StructureType.DebugUtilsObjectTagInfoExt,
                    ObjectType = MapObjectType(type),
                    ObjectHandle = handle,
                    TagName = tagName,
                    TagSize = (nuint)data.Length,
                    PTag = pData,
                };
                _ext.SetDebugUtilsObjectTag(_device, info);
            }
        }

        /// <summary>开始一个 debug label (command buffer 内)。</summary>
        public void BeginLabel(CommandBuffer cmd, string name, DebugColor? color = null)
        {
            if (!_enabled || string.IsNullOrEmpty(name)) return;
            var c = color ?? DebugColor.Gray;
            var label = new DebugUtilsLabelEXT
            {
                SType = StructureType.DebugUtilsLabelExt,
                PLabelName = (byte*)Marshal.StringToHGlobalAnsi(name),
            };
            label.Color[0] = c.R; label.Color[1] = c.G; label.Color[2] = c.B; label.Color[3] = c.A;
            _ext.CmdBeginDebugUtilsLabel(cmd, label);
            Marshal.FreeHGlobal((IntPtr)label.PLabelName);
            _stats.BeginLabels++;
        }

        /// <summary>结束一个 debug label。</summary>
        public void EndLabel(CommandBuffer cmd)
        {
            if (!_enabled) return;
            _ext.CmdEndDebugUtilsLabel(cmd);
            _stats.EndLabels++;
        }

        /// <summary>插入一个 marker (不配对 begin/end)。</summary>
        public void InsertLabel(CommandBuffer cmd, string name, DebugColor? color = null)
        {
            if (!_enabled || string.IsNullOrEmpty(name)) return;
            var c = color ?? DebugColor.Gray;
            var label = new DebugUtilsLabelEXT
            {
                SType = StructureType.DebugUtilsLabelExt,
                PLabelName = (byte*)Marshal.StringToHGlobalAnsi(name),
            };
            label.Color[0] = c.R; label.Color[1] = c.G; label.Color[2] = c.B; label.Color[3] = c.A;
            _ext.CmdInsertDebugUtilsLabel(cmd, label);
            Marshal.FreeHGlobal((IntPtr)label.PLabelName);
            _stats.InsertLabels++;
        }

        /// <summary>开始一个 queue label (跨多个 command buffer)。</summary>
        public void BeginQueueLabel(Queue queue, string name, DebugColor? color = null)
        {
            if (!_enabled || string.IsNullOrEmpty(name)) return;
            var c = color ?? DebugColor.Gray;
            var label = new DebugUtilsLabelEXT
            {
                SType = StructureType.DebugUtilsLabelExt,
                PLabelName = (byte*)Marshal.StringToHGlobalAnsi(name),
            };
            label.Color[0] = c.R; label.Color[1] = c.G; label.Color[2] = c.B; label.Color[3] = c.A;
            _ext.QueueBeginDebugUtilsLabel(queue, label);
            Marshal.FreeHGlobal((IntPtr)label.PLabelName);
        }

        /// <summary>结束 queue label。</summary>
        public void EndQueueLabel(Queue queue)
        {
            if (!_enabled) return;
            _ext.QueueEndDebugUtilsLabel(queue);
        }

        /// <summary>提交消息 (主动发，不靠 validation)。</summary>
        public void SubmitMessage(DebugMessageSeverity severity, DebugMessageType type,
                                  string name, string message)
        {
            if (!_enabled) return;
            var info = new DebugUtilsMessengerCallbackDataEXT
            {
                SType = StructureType.DebugUtilsMessengerCallbackDataExt,
                PMessageIdName = (byte*)Marshal.StringToHGlobalAnsi(name),
                PMessage = (byte*)Marshal.StringToHGlobalAnsi(message),
            };
            _ext.SubmitDebugUtilsMessage(_instance,
                (DebugUtilsMessageSeverityFlagsEXT)severity,
                (DebugUtilsMessageTypeFlagsEXT)type,
                info);
            Marshal.FreeHGlobal((IntPtr)info.PMessageIdName);
            Marshal.FreeHGlobal((IntPtr)info.PMessage);
        }

        // ── 对象命名便捷方法 ──

        public void NameBuffer(Buffer buf, string name)
            => SetObjectName(buf.Handle, DebugObjectType.Buffer, name);
        public void NameImage(Image img, string name)
            => SetObjectName(img.Handle, DebugObjectType.Image, name);
        public void NameImageView(ImageView view, string name)
            => SetObjectName(view.Handle, DebugObjectType.ImageView, name);
        public void NameSampler(Sampler s, string name)
            => SetObjectName(s.Handle, DebugObjectType.Sampler, name);
        public void NamePipeline(Pipeline p, string name)
            => SetObjectName(p.Handle, DebugObjectType.Pipeline, name);
        public void NamePipelineLayout(PipelineLayout p, string name)
            => SetObjectName(p.Handle, DebugObjectType.PipelineLayout, name);
        public void NameDescriptorSet(DescriptorSet s, string name)
            => SetObjectName(s.Handle, DebugObjectType.DescriptorSet, name);
        public void NameDescriptorSetLayout(DescriptorSetLayout s, string name)
            => SetObjectName(s.Handle, DebugObjectType.DescriptorSetLayout, name);
        public void NameRenderPass(RenderPass rp, string name)
            => SetObjectName(rp.Handle, DebugObjectType.RenderPass, name);
        public void NameFramebuffer(Framebuffer fb, string name)
            => SetObjectName(fb.Handle, DebugObjectType.Framebuffer, name);
        public void NameCommandBuffer(CommandBuffer cb, string name)
            => SetObjectName((ulong)cb.Handle, DebugObjectType.CommandBuffer, name);
        public void NameSemaphore(Semaphore s, string name)
            => SetObjectName(s.Handle, DebugObjectType.Semaphore, name);
        public void NameFence(Fence f, string name)
            => SetObjectName(f.Handle, DebugObjectType.Fence, name);
        public void NameDeviceMemory(ulong handle, string name)
            => SetObjectName(handle, DebugObjectType.DeviceMemory, name);
        public void NameShaderModule(ShaderModule m, string name)
            => SetObjectName(m.Handle, DebugObjectType.ShaderModule, name);

        /// <summary>获取已命名对象数量。</summary>
        public int NamedObjectCount
        {
            get { lock (_lock) return _namedObjects.Count; }
        }

        /// <summary>获取统计。</summary>
        public DebugMarkerStats GetStats() => _stats;

        private static ObjectType MapObjectType(DebugObjectType t) => t switch
        {
            DebugObjectType.Unknown => 0,
            DebugObjectType.Instance => ObjectType.Instance,
            DebugObjectType.PhysicalDevice => ObjectType.PhysicalDevice,
            DebugObjectType.Device => ObjectType.Device,
            DebugObjectType.Queue => ObjectType.Queue,
            DebugObjectType.Semaphore => ObjectType.Semaphore,
            DebugObjectType.CommandBuffer => ObjectType.CommandBuffer,
            DebugObjectType.Fence => ObjectType.Fence,
            DebugObjectType.DeviceMemory => ObjectType.DeviceMemory,
            DebugObjectType.Buffer => ObjectType.Buffer,
            DebugObjectType.Image => ObjectType.Image,
            DebugObjectType.Event => ObjectType.Event,
            DebugObjectType.QueryPool => ObjectType.QueryPool,
            DebugObjectType.BufferView => ObjectType.BufferView,
            DebugObjectType.ImageView => ObjectType.ImageView,
            DebugObjectType.ShaderModule => ObjectType.ShaderModule,
            DebugObjectType.PipelineCache => ObjectType.PipelineCache,
            DebugObjectType.PipelineLayout => ObjectType.PipelineLayout,
            DebugObjectType.RenderPass => ObjectType.RenderPass,
            DebugObjectType.Pipeline => ObjectType.Pipeline,
            DebugObjectType.DescriptorSetLayout => ObjectType.DescriptorSetLayout,
            DebugObjectType.Sampler => ObjectType.Sampler,
            DebugObjectType.DescriptorPool => ObjectType.DescriptorPool,
            DebugObjectType.DescriptorSet => ObjectType.DescriptorSet,
            DebugObjectType.Framebuffer => ObjectType.Framebuffer,
            DebugObjectType.CommandPool => ObjectType.CommandPool,
            DebugObjectType.Swapchain => ObjectType.SurfaceKhr,
            _ => 0,
        };

        public void Dispose()
        {
            // ExtDebugUtils 由 Vk 实例管理
            lock (_lock) _namedObjects.Clear();
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 4: debug scope (using 模式)
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Debug scope，用 using 块自动 begin/end label。
    /// </summary>
    public readonly struct DebugScope : IDisposable
    {
        private readonly VulkanDebugMarker _marker;
        private readonly CommandBuffer _cmd;
        private readonly bool _enabled;

        public DebugScope(VulkanDebugMarker marker, CommandBuffer cmd, string name, DebugColor? color = null)
        {
            _marker = marker;
            _cmd = cmd;
            _enabled = marker != null && marker.IsEnabled;
            if (_enabled)
                _marker.BeginLabel(cmd, name, color);
        }

        public void Dispose()
        {
            if (_enabled)
                _marker.EndLabel(_cmd);
        }
    }

    /// <summary>Queue scope。</summary>
    public readonly struct QueueDebugScope : IDisposable
    {
        private readonly VulkanDebugMarker _marker;
        private readonly Queue _queue;
        private readonly bool _enabled;

        public QueueDebugScope(VulkanDebugMarker marker, Queue queue, string name, DebugColor? color = null)
        {
            _marker = marker;
            _queue = queue;
            _enabled = marker != null && marker.IsEnabled;
            if (_enabled)
                _marker.BeginQueueLabel(queue, name, color);
        }

        public void Dispose()
        {
            if (_enabled)
                _marker.EndQueueLabel(_queue);
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 5: 默认消息处理器
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>默认的 validation 消息处理器。打到 console。</summary>
    public sealed class DefaultDebugMessageHandler
    {
        private DebugMarkerStats _stats;
        private readonly bool _breakOnError;
        private readonly bool _verboseToConsole;

        public DefaultDebugMessageHandler(DebugMarkerStats stats,
                                          bool breakOnError = false,
                                          bool verboseToConsole = false)
        {
            _stats = stats;
            _breakOnError = breakOnError;
            _verboseToConsole = verboseToConsole;
        }

        public void Handle(DebugMessageSeverity severity, DebugMessageType type, string message)
        {
            _stats.MessagesReceived++;

            switch (severity)
            {
                case DebugMessageSeverity.Error:
                    _stats.ValidationErrors++;
                    Console.Error.WriteLine($"[VK-ERROR] {message}");
                    if (_breakOnError && System.Diagnostics.Debugger.IsAttached)
                        System.Diagnostics.Debugger.Break();
                    break;
                case DebugMessageSeverity.Warning:
                    if (type == DebugMessageType.Performance)
                    {
                        _stats.PerformanceWarnings++;
                        Console.ForegroundColor = ConsoleColor.Yellow;
                        Console.WriteLine($"[VK-PERF] {message}");
                    }
                    else
                    {
                        _stats.ValidationWarnings++;
                        Console.ForegroundColor = ConsoleColor.DarkYellow;
                        Console.WriteLine($"[VK-WARN] {message}");
                    }
                    Console.ResetColor();
                    break;
                case DebugMessageSeverity.Info:
                    if (_verboseToConsole)
                        Console.WriteLine($"[VK-INFO] {message}");
                    break;
                case DebugMessageSeverity.Verbose:
                    if (_verboseToConsole)
                        Console.WriteLine($"[VK-VERBOSE] {message}");
                    break;
            }
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 6: 对象命名辅助 (批量)
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>批量命名辅助。</summary>
    public static class DebugNamingHelper
    {
        /// <summary>给 swapchain images 批量命名。</summary>
        public static void NameSwapchainImages(VulkanDebugMarker marker, SwapchainImage[] images, string prefix = "Swapchain")
        {
            if (marker == null) return;
            for (int i = 0; i < images.Length; i++)
            {
                marker.NameImage(images[i].Image, $"{prefix}_Image_{i}");
                marker.NameImageView(images[i].View, $"{prefix}_View_{i}");
            }
        }

        /// <summary>给一组 buffer 命名。</summary>
        public static void NameBuffers(VulkanDebugMarker marker, Buffer[] buffers, string prefix)
        {
            if (marker == null) return;
            for (int i = 0; i < buffers.Length; i++)
                marker.NameBuffer(buffers[i], $"{prefix}_{i}");
        }

        /// <summary>给一组 image 命名。</summary>
        public static void NameImages(VulkanDebugMarker marker, Image[] images, string prefix)
        {
            if (marker == null) return;
            for (int i = 0; i < images.Length; i++)
                marker.NameImage(images[i], $"{prefix}_{i}");
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 7: 调试日志记录
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>调试日志记录器。把 validation 消息存到文件。</summary>
    public sealed class DebugLogger
    {
        private readonly string _logPath;
        private readonly object _lock = new();
        private readonly System.Text.StringBuilder _sb = new();
        private readonly int _maxEntries;
        private int _entryCount;

        public DebugLogger(string logPath, int maxEntries = 10000)
        {
            _logPath = logPath;
            _maxEntries = maxEntries;
        }

        public void Log(DebugMessageSeverity severity, DebugMessageType type, string message)
        {
            lock (_lock)
            {
                if (_entryCount >= _maxEntries) return;
                _sb.AppendLine($"[{DateTime.Now:HH:mm:ss.fff}] [{severity}] [{type}] {message}");
                _entryCount++;
            }
        }

        public void Flush()
        {
            lock (_lock)
            {
                try
                {
                    File.AppendAllText(_logPath, _sb.ToString());
                }
                catch (Exception e)
                {
                    Console.Error.WriteLine($"[DebugLogger] Flush 失败: {e.Message}");
                }
                _sb.Clear();
                _entryCount = 0;
            }
        }

        public int EntryCount
        {
            get { lock (_lock) return _entryCount; }
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 8: 版本信息
    // ════════════════════════════════════════════════════════════════════════════

    public static class DebugMarkerVersion
    {
        public const string Version = "1.0.0";
        public const string Description = "YamaEngine Vulkan Debug Marker (VK_EXT_debug_utils)";
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 9: 使用示例
    // ════════════════════════════════════════════════════════════════════════════

    /*
    // === 使用示例 ===
    //
    // var marker = new VulkanDebugMarker(vk, instance, device);
    // marker.NameBuffer(vertexBuf, "VertexBuf");
    // marker.NameImage(colorTarget, "ColorTarget");
    //
    // // 命令缓冲录制时加 scope
    // using (marker.BeginScope(cmd, "ShadowPass", DebugColor.ForPassType("shadow")))
    // {
    //     vkCmdDraw(...);
    // }
    //
    // // 插入 marker
    // marker.InsertLabel(cmd, "DrawMesh", DebugColor.Green);
    */

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 10: DebugScope 扩展
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>VulkanDebugMarker 扩展方法。</summary>
    public static class DebugMarkerExtensions
    {
        /// <summary>创建一个 using scope。</summary>
        public static DebugScope BeginScope(this VulkanDebugMarker marker, CommandBuffer cmd,
                                            string name, DebugColor? color = null)
        {
            return new DebugScope(marker, cmd, name, color);
        }

        /// <summary>创建一个 queue using scope。</summary>
        public static QueueDebugScope BeginQueueScope(this VulkanDebugMarker marker, Queue queue,
                                                       string name, DebugColor? color = null)
        {
            return new QueueDebugScope(marker, queue, name, color);
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 11: 单元测试桩
    // ════════════════════════════════════════════════════════════════════════════

    public static class DebugMarkerTestStub
    {
        public static bool TestColorEquals(DebugColor a, DebugColor b)
            => Math.Abs(a.R - b.R) < 0.001f &&
               Math.Abs(a.G - b.G) < 0.001f &&
               Math.Abs(a.B - b.B) < 0.001f;

        public static DebugColor TestForPassType(string pass)
            => DebugColor.ForPassType(pass);
    }
}

// 文件结束 - VulkanDebugMarker.cs
