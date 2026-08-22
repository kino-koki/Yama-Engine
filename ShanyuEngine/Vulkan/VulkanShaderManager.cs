// =============================================================================
// VulkanShaderManager.cs - 山雨引擎 Vulkan Shader 管理器 (shaderc 编译+缓存)
// =============================================================================
// Shader 管理做几件事：
//   1. GLSL/HLSL 源码编译成 SPIR-V (通过 shaderc)
//   2. SPIR-V 字节码缓存 (磁盘 + 内存)
//   3. ShaderModule 创建/复用
//   4. 反射出 descriptor set layout / push constant / vertex input
//   5. shader 变更监视 (hot reload)
//
// shaderc 的 API 比较啰嗦，而且通过 P/Invoke 调，每个调用都要小心内存。
// 这套实现把编译细节封装起来，上层只管传源码拿 SPIR-V。
//
// FIXME: HLSL 编译还没接 (需要 DXC)
// TODO: SPIR-V 反射用 SPIRV-Reflect 替代手撸
//
// 作者: 山雨引擎团队
// =============================================================================

using Vulkan = Silk.NET.Vulkan;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Silk.NET.Vulkan;
using Semaphore = Silk.NET.Vulkan.Semaphore;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace ShanyuEngine.Vulkan
{
    // ════════════════════════════════════════════════════════════════════════════
    // 区域 1: 公共类型
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>Shader 阶段。</summary>
    public enum ShaderStageExt
    {
        Vertex,
        Fragment,
        Compute,
        Geometry,
        TessellationControl,
        TessellationEvaluation,
        Mesh,
        Task,
        Raygen,
        AnyHit,
        ClosestHit,
        Miss,
        Intersection,
        Callable,
    }

    /// <summary>Shader 源码语言。</summary>
    public enum ShaderSourceLanguage
    {
        Glsl,
        Hlsl,
        Spirv,           // 直接给 SPIR-V 字节码
        SpirvAssembly,
    }

    /// <summary>shaderc 优化级别。</summary>
    public enum ShaderOptimizationLevel
    {
        None = 0,
        Size = 1,
        Performance = 2,
    }

    /// <summary>编译结果。</summary>
    public struct ShaderCompileResult
    {
        public bool Success;
        public byte[] Spirv;
        public string ErrorMsg;
        public string WarningMsg;
        public long CompileTimeMs;
        public ShaderStageExt Stage;
        public string SourcePath;

        public override string ToString()
            => Success ? $"OK ({Spirv?.Length ?? 0} bytes, {CompileTimeMs}ms)"
                       : $"FAIL: {ErrorMsg}";
    }

    /// <summary>Shader 反射信息。</summary>
    public class ShaderReflectionInfo
    {
        public ShaderStageExt Stage;
        public List<DescriptorSetInfo> DescriptorSets = new();
        public List<PushConstantRange> PushConstants = new();
        public List<VertexInputAttribute> VertexInputs = new();
        public List<ShaderResource> Resources = new();
        public Dictionary<uint, uint> SpecConstants = new();
        public string EntryPoint;
        public uint LocalSizeX, LocalSizeY, LocalSizeZ;  // compute
    }

    /// <summary>Descriptor set 反射信息。</summary>
    public class DescriptorSetInfo
    {
        public uint Set;
        public List<DescriptorBindingInfo> Bindings = new();
    }

    /// <summary>Descriptor binding 反射信息。</summary>
    public class DescriptorBindingInfo
    {
        public uint Binding;
        public DescriptorType Type;
        public uint Count;
        public ShaderStageFlags StageFlags;
        public string Name;
        public bool IsBindless;
    }

    /// <summary>顶点输入属性。</summary>
    public class VertexInputAttribute
    {
        public uint Location;
        public Format Format;
        public string Name;
    }

    /// <summary>Shader 资源 (uniform/block)。</summary>
    public class ShaderResource
    {
        public string Name;
        public uint Set;
        public uint Binding;
        public DescriptorType Type;
        public ulong Size;
        public ShaderStageFlags Stage;
    }

    /// <summary>Shader 管理器统计。</summary>
    public struct ShaderManagerStats
    {
        public int TotalCompiled;
        public int CacheHits;
        public int CacheMisses;
        public int ModulesCreated;
        public int ModulesActive;
        public long TotalCompileTimeMs;

        public float HitRate => (CacheHits + CacheMisses) == 0 ? 0 : (float)CacheHits / (CacheHits + CacheMisses);

        public override string ToString()
            => $"Shaders: compiled={TotalCompiled}, hits={CacheHits}, misses={CacheMisses}, " +
               $"hit_rate={HitRate * 100:F1}%, modules={ModulesActive}/{ModulesCreated}, " +
               $"compile={TotalCompileTimeMs}ms";
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 2: shaderc P/Invoke
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>shaderc 的 P/Invoke 包装。</summary>
    internal static unsafe class ShadercNative
    {
        private const string Lib = "shaderc_shared.dll";

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr shaderc_compiler_initialize();

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern void shaderc_compiler_release(IntPtr compiler);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr shaderc_compile_options_initialize();

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern void shaderc_compile_options_release(IntPtr options);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern void shaderc_compile_options_set_optimization_level(
            IntPtr options, int level);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern void shaderc_compile_options_set_target_env(
            IntPtr options, int target, uint version);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern void shaderc_compile_options_set_auto_bind_uniforms(
            IntPtr options, [MarshalAs(UnmanagedType.Bool)] bool auto_bind);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern void shaderc_compile_options_set_forced_version_profile(
            IntPtr options, int version, int profile);

        // shader_kind 枚举
        public const int KindVertex = 0;
        public const int KindFragment = 1;
        public const int KindCompute = 2;
        public const int KindGeometry = 3;
        public const int KindTessControl = 4;
        public const int KindTessEvaluation = 5;

        // optimization
        public const int OptNone = 0;
        public const int OptSize = 1;
        public const int OptPerformance = 2;

        // target env
        public const int TargetVulkan = 1;
        public const int TargetOpengl = 0;
        public const int TargetVulkanVersion11 = 1;

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr shaderc_compile_into_spv(
            IntPtr compiler, byte* source, UIntPtr source_len,
            int shader_kind, byte* file_name, byte* entry_point,
            IntPtr options);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern void shaderc_result_release(IntPtr result);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern byte* shaderc_result_get_bytes(IntPtr result);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern UIntPtr shaderc_result_get_length(IntPtr result);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int shaderc_result_get_compilation_status(IntPtr result);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern byte* shaderc_result_get_error_message(IntPtr result);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int shaderc_get_spv_version();
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 3: shaderc 编译器封装
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// shaderc 编译器封装。一个进程一个实例，复用 compiler handle。
    /// </summary>
    internal sealed unsafe class ShadercCompiler : IDisposable
    {
        private IntPtr _compiler;
        private IntPtr _options;
        private bool _disposed;

        public ShadercCompiler(ShaderOptimizationLevel optLevel = ShaderOptimizationLevel.Performance)
        {
            _compiler = ShadercNative.shaderc_compiler_initialize();
            if (_compiler == IntPtr.Zero)
                throw new InvalidOperationException("shaderc_compiler_initialize 失败，检查 shaderc_shared.dll");

            _options = ShadercNative.shaderc_compile_options_initialize();
            ShadercNative.shaderc_compile_options_set_optimization_level(_options, (int)optLevel);
            ShadercNative.shaderc_compile_options_set_target_env(_options, ShadercNative.TargetVulkan, 1);
            ShadercNative.shaderc_compile_options_set_auto_bind_uniforms(_options, true);
        }

        /// <summary>编译 GLSL 到 SPIR-V。</summary>
        public ShaderCompileResult Compile(string source, ShaderStageExt stage,
                                           string fileName = "shader.glsl",
                                           string entryPoint = "main")
        {
            var sw = Stopwatch.StartNew();
            int kind = StageToKind(stage);

            byte[] srcBytes = Encoding.UTF8.GetBytes(source);
            byte[] nameBytes = Encoding.UTF8.GetBytes(fileName + "\0");
            byte[] entryBytes = Encoding.UTF8.GetBytes(entryPoint + "\0");

            fixed (byte* pSrc = srcBytes)
            fixed (byte* pName = nameBytes)
            fixed (byte* pEntry = entryBytes)
            {
                var result = ShadercNative.shaderc_compile_into_spv(
                    _compiler, pSrc, (UIntPtr)srcBytes.Length,
                    kind, pName, pEntry, _options);

                if (result == IntPtr.Zero)
                {
                    return new ShaderCompileResult
                    {
                        Success = false,
                        ErrorMsg = "shaderc_compile_into_spv 返回 null",
                        Stage = stage,
                        SourcePath = fileName,
                    };
                }

                int status = ShadercNative.shaderc_result_get_compilation_status(result);
                if (status != 0)  // 0 = success
                {
                    byte* err = ShadercNative.shaderc_result_get_error_message(result);
                    string errMsg = err != null ? Marshal.PtrToStringAnsi((IntPtr)err) : "unknown error";
                    ShadercNative.shaderc_result_release(result);
                    sw.Stop();
                    return new ShaderCompileResult
                    {
                        Success = false,
                        ErrorMsg = errMsg,
                        Stage = stage,
                        SourcePath = fileName,
                        CompileTimeMs = sw.ElapsedMilliseconds,
                    };
                }

                UIntPtr len = ShadercNative.shaderc_result_get_length(result);
                byte* bytes = ShadercNative.shaderc_result_get_bytes(result);
                byte[] spv = new byte[(int)len];
                Marshal.Copy((IntPtr)bytes, spv, 0, (int)len);

                ShadercNative.shaderc_result_release(result);
                sw.Stop();
                return new ShaderCompileResult
                {
                    Success = true,
                    Spirv = spv,
                    Stage = stage,
                    SourcePath = fileName,
                    CompileTimeMs = sw.ElapsedMilliseconds,
                };
            }
        }

        private static int StageToKind(ShaderStageExt stage) => stage switch
        {
            ShaderStageExt.Vertex => ShadercNative.KindVertex,
            ShaderStageExt.Fragment => ShadercNative.KindFragment,
            ShaderStageExt.Compute => ShadercNative.KindCompute,
            ShaderStageExt.Geometry => ShadercNative.KindGeometry,
            ShaderStageExt.TessellationControl => ShadercNative.KindTessControl,
            ShaderStageExt.TessellationEvaluation => ShadercNative.KindTessEvaluation,
            _ => ShadercNative.KindVertex,
        };

        public void Dispose()
        {
            if (_disposed) return;
            if (_options != IntPtr.Zero)
                ShadercNative.shaderc_compile_options_release(_options);
            if (_compiler != IntPtr.Zero)
                ShadercNative.shaderc_compiler_release(_compiler);
            _disposed = true;
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 4: SPIR-V 磁盘缓存
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// SPIR-V 磁盘缓存。源码 hash 相同就直接读缓存，避免重复编译。
    /// </summary>
    internal sealed class SpirvDiskCache
    {
        private readonly string _cacheDir;
        private readonly object _lock = new();

        public SpirvDiskCache(string cacheDir)
        {
            _cacheDir = cacheDir;
            if (!Directory.Exists(cacheDir))
                Directory.CreateDirectory(cacheDir);
        }

        /// <summary>尝试从缓存读。</summary>
        public bool TryLoad(string sourceHash, ShaderStageExt stage, out byte[] spv)
        {
            spv = null;
            string path = GetPath(sourceHash, stage);
            if (!File.Exists(path)) return false;
            try
            {
                spv = File.ReadAllBytes(path);
                return true;
            }
            catch { return false; }
        }

        /// <summary>写入缓存。</summary>
        public void Save(string sourceHash, ShaderStageExt stage, byte[] spv)
        {
            string path = GetPath(sourceHash, stage);
            try
            {
                File.WriteAllBytes(path, spv);
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"[ShaderCache] 保存失败: {e.Message}");
            }
        }

        /// <summary>清空缓存。</summary>
        public void Clear()
        {
            lock (_lock)
            {
                try
                {
                    Directory.Delete(_cacheDir, true);
                    Directory.CreateDirectory(_cacheDir);
                }
                catch { }
            }
        }

        /// <summary>缓存大小。</summary>
        public long GetCacheSize()
        {
            try
            {
                var dir = new DirectoryInfo(_cacheDir);
                long size = 0;
                foreach (var f in dir.GetFiles()) size += f.Length;
                return size;
            }
            catch { return 0; }
        }

        private string GetPath(string hash, ShaderStageExt stage)
            => Path.Combine(_cacheDir, $"{stage}_{hash}.spv");
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 5: ShaderModule 缓存
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>ShaderModule 缓存。相同 SPIR-V 只创建一次 module。</summary>
    public sealed unsafe class ShaderModuleManager : IDisposable
    {
        private readonly Vk _vk;
        private readonly Device _device;
        private readonly Dictionary<ulong, ShaderModule> _cache = new();
        private readonly Dictionary<ulong, string> _names = new();
        private readonly object _lock = new();

        public ShaderModuleManager(Vk vk, Device device)
        {
            _vk = vk;
            _device = device;
        }

        public ShaderModule GetOrCreate(byte[] spirv, string debugName = null)
        {
            ulong hash = HashSpirv(spirv);
            lock (_lock)
            {
                if (_cache.TryGetValue(hash, out var m)) return m;
            }

            fixed (byte* pCode = spirv)
            {
                var info = new ShaderModuleCreateInfo
                {
                    SType = StructureType.ShaderModuleCreateInfo,
                    CodeSize = (nuint)spirv.Length,
                    PCode = (uint*)pCode,
                };
                ShaderModule mod;
                var r = _vk.CreateShaderModule(_device, info, null, &mod);
                if (r != Result.Success)
                    throw new VulkanException(r, "CreateShaderModule 失败");
                lock (_lock)
                {
                    _cache[hash] = mod;
                    if (debugName != null) _names[hash] = debugName;
                }
                return mod;
            }
        }

        public void DestroyByHash(ulong hash)
        {
            lock (_lock)
            {
                if (_cache.TryGetValue(hash, out var m))
                {
                    _vk.DestroyShaderModule(_device, m, null);
                    _cache.Remove(hash);
                    _names.Remove(hash);
                }
            }
        }

        public int Count
        {
            get { lock (_lock) return _cache.Count; }
        }

        private static ulong HashSpirv(byte[] data)
        {
            // xxHash64 简化版
            ulong hash = 14695981039346656037UL;
            unchecked
            {
                foreach (var b in data)
                {
                    hash ^= b;
                    hash *= 1099511628211UL;
                }
            }
            return hash;
        }

        public void Dispose()
        {
            lock (_lock)
            {
                foreach (var m in _cache.Values)
                    _vk.DestroyShaderModule(_device, m, null);
                _cache.Clear();
                _names.Clear();
            }
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 6: Shader 反射 (简化版)
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 简化版 SPIR-V 反射。真实工程应该用 SPIRV-Reflect 库。
    /// 这里只解析 SPIR-V header + OpEntryPoint 找出 stage，descriptor 信息得用专门的反射库。
    /// </summary>
    public static class SpirvReflector
    {
        /// <summary>从 SPIR-V 字节码解析基本信息。</summary>
        public static ShaderReflectionInfo Reflect(byte[] spirv, ShaderStageExt stage)
        {
            var info = new ShaderReflectionInfo
            {
                Stage = stage,
                EntryPoint = "main",
            };

            // 验证 magic number
            if (spirv.Length < 4) return info;
            uint magic = BitConverter.ToUInt32(spirv, 0);
            if (magic != 0x07230203)
            {
                Console.Error.WriteLine("[SpirvReflect] 不是有效的 SPIR-V");
                return info;
            }

            // 解析 version
            if (spirv.Length >= 8)
            {
                uint version = BitConverter.ToUInt32(spirv, 4);
                // version = major << 16 | minor << 8
            }

            // 实际的 descriptor set / binding 反射需要完整解析 SPIR-V 指令流
            // 这里只给骨架，真实实现用 SPIRV-Reflect
            // TODO: 接入 SPIRV-Reflect

            return info;
        }

        /// <summary>从 GLSL 源码粗略提取 binding (调试用)。</summary>
        public static List<DescriptorBindingInfo> ParseBindingsFromGlsl(string source, ShaderStageExt stage)
        {
            var bindings = new List<DescriptorBindingInfo>();
            var lines = source.Split('\n');
            var stageFlags = StageToFlags(stage);

            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("layout"))
                {
                    // 简单解析 layout(set=N, binding=M) ... uniform ...
                    // 这个解析很粗糙，只能处理常见格式
                    int setIdx = trimmed.IndexOf("set=");
                    int bindingIdx = trimmed.IndexOf("binding=");
                    if (setIdx >= 0 && bindingIdx >= 0)
                    {
                        uint set = ParseNumberAfter(trimmed, setIdx + 4);
                        uint binding = ParseNumberAfter(trimmed, bindingIdx + 8);
                        DescriptorType type = GuessDescriptorType(trimmed);
                        bindings.Add(new DescriptorBindingInfo
                        {
                            Set = set,
                            Binding = binding,
                            Type = type,
                            Count = 1,
                            StageFlags = stageFlags,
                        });
                    }
                }
            }
            return bindings;
        }

        private static ShaderStageFlags StageToFlags(ShaderStageExt stage) => stage switch
        {
            ShaderStageExt.Vertex => ShaderStageFlags.VertexBit,
            ShaderStageExt.Fragment => ShaderStageFlags.FragmentBit,
            ShaderStageExt.Compute => ShaderStageFlags.ComputeBit,
            ShaderStageExt.Geometry => ShaderStageFlags.GeometryBit,
            _ => ShaderStageFlags.All,
        };

        private static uint ParseNumberAfter(string s, int start)
        {
            int i = start;
            while (i < s.Length && !char.IsDigit(s[i])) i++;
            int j = i;
            while (j < s.Length && char.IsDigit(s[j])) j++;
            if (i >= s.Length) return 0;
            return uint.Parse(s.Substring(i, j - i));
        }

        private static DescriptorType GuessDescriptorType(string line)
        {
            var l = line.ToLowerInvariant();
            if (l.Contains("samplertexture") || l.Contains("sampler2d") || l.Contains("sampler"))
                return DescriptorType.CombinedImageSampler;
            if (l.Contains("uniformbuffer") || l.Contains("uniform struct") || l.Contains("cbuffer"))
                return DescriptorType.UniformBuffer;
            if (l.Contains("rwstructuredbuffer") || l.Contains("buffer"))
                return DescriptorType.StorageBuffer;
            if (l.Contains("rwtexture") || l.Contains("image2d"))
                return DescriptorType.StorageImage;
            return DescriptorType.UniformBuffer;
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 7: Shader 文件监视 (hot reload)
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Shader 文件监视。源码改了之后触发重新编译。
    /// </summary>
    public sealed class ShaderFileWatcher : IDisposable
    {
        private readonly Dictionary<string, FileSystemWatcher> _watchers = new();
        private readonly Dictionary<string, DateTime> _lastWrite = new();
        private readonly object _lock = new();
        private bool _disposed;

        public event Action<string> OnShaderChanged;

        public void Watch(string path)
        {
            if (!File.Exists(path)) return;
            string dir = Path.GetDirectoryName(path);
            string name = Path.GetFileName(path);
            if (string.IsNullOrEmpty(dir)) return;

            lock (_lock)
            {
                if (!_watchers.TryGetValue(dir, out var w))
                {
                    w = new FileSystemWatcher(dir)
                    {
                        NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName,
                        EnableRaisingEvents = true,
                    };
                    w.Changed += (s, e) => OnFileChanged(e.FullPath);
                    w.Created += (s, e) => OnFileChanged(e.FullPath);
                    _watchers[dir] = w;
                }
                _lastWrite[path] = File.GetLastWriteTime(path);
            }
        }

        private void OnFileChanged(string path)
        {
            if (_disposed) return;
            lock (_lock)
            {
                if (!_lastWrite.TryGetValue(path, out var last)) return;
                try
                {
                    var current = File.GetLastWriteTime(path);
                    if (current <= last) return;
                    _lastWrite[path] = current;
                }
                catch { return; }
            }

            // 防抖：等一下再通知 (编辑器保存可能多次写)
            ThreadPool.QueueUserWorkItem(_ =>
            {
                Thread.Sleep(100);
                OnShaderChanged?.Invoke(path);
            });
        }

        public void Dispose()
        {
            _disposed = true;
            lock (_lock)
            {
                foreach (var w in _watchers.Values) w.Dispose();
                _watchers.Clear();
            }
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 8: Shader 管理器主类
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Shader 管理器主入口。
    /// </summary>
    public sealed unsafe class VulkanShaderManager : IDisposable
    {
        private readonly Vk _vk;
        private readonly Device _device;
        private readonly ShadercCompiler _compiler;
        private readonly SpirvDiskCache _diskCache;
        private readonly ShaderModuleManager _moduleMgr;
        private readonly ShaderFileWatcher _watcher;
        private readonly Dictionary<string, byte[]> _sourceCache = new();
        private readonly Dictionary<string, ShaderModule> _pathToModule = new();
        private readonly object _lock = new();
        private readonly ShaderManagerStats _stats;

        public ShaderModuleManager ModuleManager => _moduleMgr;
        public ShaderFileWatcher FileWatcher => _watcher;
        public ref ShaderManagerStats Stats => ref _stats;

        public VulkanShaderManager(Vk vk, Device device, string cacheDir,
                                   ShaderOptimizationLevel optLevel = ShaderOptimizationLevel.Performance)
        {
            _vk = vk;
            _device = device;
            _compiler = new ShadercCompiler(optLevel);
            _diskCache = new SpirvDiskCache(Path.Combine(cacheDir, "spirv"));
            _moduleMgr = new ShaderModuleManager(vk, device);
            _watcher = new ShaderFileWatcher();
            _stats = new ShaderManagerStats();

            _watcher.OnShaderChanged += OnShaderFileChanged;
        }

        /// <summary>编译并创建 ShaderModule。</summary>
        public ShaderModule CompileFromFile(string path, ShaderStageExt stage,
                                            string entryPoint = "main")
        {
            if (!File.Exists(path))
                throw new FileNotFoundException($"Shader 文件不存在: {path}");

            string source = File.ReadAllText(path);
            return CompileFromSource(source, path, stage, entryPoint);
        }

        /// <summary>从源码编译并创建 ShaderModule。</summary>
        public ShaderModule CompileFromSource(string source, string virtualPath,
                                              ShaderStageExt stage, string entryPoint = "main")
        {
            string hash = ComputeSourceHash(source);

            // 1. 内存缓存
            lock (_lock)
            {
                if (_pathToModule.TryGetValue(virtualPath, out var mod))
                {
                    _stats.CacheHits++;
                    return mod;
                }
            }

            // 2. 磁盘缓存
            byte[] spirv;
            if (_diskCache.TryLoad(hash, stage, out spirv))
            {
                _stats.CacheHits++;
            }
            else
            {
                // 3. 编译
                _stats.CacheMisses++;
                var result = _compiler.Compile(source, stage, virtualPath, entryPoint);
                if (!result.Success)
                {
                    throw new VulkanException(Result.ErrorUnknown,
                        $"Shader 编译失败: {virtualPath}\n{result.ErrorMsg}");
                }
                spirv = result.Spirv;
                _diskCache.Save(hash, stage, spirv);
                _stats.TotalCompiled++;
                _stats.TotalCompileTimeMs += result.CompileTimeMs;
            }

            // 4. 创建 module
            var module = _moduleMgr.GetOrCreate(spirv, virtualPath);
            _stats.ModulesCreated++;

            lock (_lock)
            {
                _pathToModule[virtualPath] = module;
                _sourceCache[virtualPath] = spirv;
            }

            // 5. 监视文件
            if (File.Exists(virtualPath))
                _watcher.Watch(virtualPath);

            return module;
        }

        /// <summary>直接从 SPIR-V 创建 module。</summary>
        public ShaderModule CreateFromSpirv(byte[] spirv, string debugName = null)
        {
            _stats.TotalCompiled++;
            return _moduleMgr.GetOrCreate(spirv, debugName);
        }

        /// <summary>获取某个 path 的 module (已编译过的)。</summary>
        public ShaderModule GetModule(string path)
        {
            lock (_lock)
            {
                return _pathToModule.TryGetValue(path, out var m) ? m : default;
            }
        }

        /// <summary>反射 shader 信息。</summary>
        public ShaderReflectionInfo Reflect(string path)
        {
            lock (_lock)
            {
                if (!_sourceCache.TryGetValue(path, out var spv)) return null;
                return SpirvReflector.Reflect(spv, GuessStage(path));
            }
        }

        /// <summary>失效某个 shader (强制重编译)。</summary>
        public void Invalidate(string path)
        {
            lock (_lock)
            {
                _pathToModule.Remove(path);
                _sourceCache.Remove(path);
            }
        }

        /// <summary>失效所有 shader。</summary>
        public void InvalidateAll()
        {
            lock (_lock)
            {
                _pathToModule.Clear();
                _sourceCache.Clear();
            }
        }

        /// <summary>获取统计。</summary>
        public ShaderManagerStats GetStats()
        {
            _stats.ModulesActive = _moduleMgr.Count;
            return _stats;
        }

        /// <summary>文件变更回调。</summary>
        private void OnShaderFileChanged(string path)
        {
            Console.WriteLine($"[ShaderMgr] 检测到 shader 变更: {path}");
            Invalidate(path);
            // 实际工程里这里会触发重新编译，并通知管线缓存失效
        }

        private static string ComputeSourceHash(string source)
        {
            using var sha = SHA256.Create();
            var bytes = Encoding.UTF8.GetBytes(source);
            var hash = sha.ComputeHash(bytes);
            return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant().Substring(0, 16);
        }

        private static ShaderStageExt GuessStage(string path)
        {
            var l = path.ToLowerInvariant();
            if (l.Contains(".vert") || l.Contains("vertex")) return ShaderStageExt.Vertex;
            if (l.Contains(".frag") || l.Contains("pixel")) return ShaderStageExt.Fragment;
            if (l.Contains(".comp") || l.Contains("compute")) return ShaderStageExt.Compute;
            if (l.Contains(".geom")) return ShaderStageExt.Geometry;
            if (l.Contains(".tesc")) return ShaderStageExt.TessellationControl;
            if (l.Contains(".tese")) return ShaderStageExt.TessellationEvaluation;
            return ShaderStageExt.Vertex;
        }

        public void Dispose()
        {
            _watcher.Dispose();
            _moduleMgr.Dispose();
            _compiler.Dispose();
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 9: Shader program (多个 stage 组合)
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>Shader program。把多个 stage 的 module 组合起来。</summary>
    public sealed class ShaderProgram : IDisposable
    {
        private readonly List<ShaderModule> _modules = new();
        private readonly List<PipelineShaderStageCreateInfo> _stages = new();
        private readonly VulkanShaderManager _mgr;
        private readonly string _name;

        public string Name => _name;
        public IReadOnlyList<PipelineShaderStageCreateInfo> Stages => _stages;
        public IReadOnlyList<ShaderModule> Modules => _modules;

        public ShaderProgram(VulkanShaderManager mgr, string name)
        {
            _mgr = mgr;
            _name = name;
        }

        public ShaderProgram AddStage(string path, ShaderStageExt stage, string entry = "main")
        {
            var module = _mgr.CompileFromFile(path, stage, entry);
            _modules.Add(module);
            _stages.Add(new PipelineShaderStageCreateInfo
            {
                SType = StructureType.PipelineShaderStageCreateInfo,
                Stage = StageToFlags(stage),
                Module = module,
                PName = (byte*)Marshal.StringToHGlobalAnsi(entry),
            });
            return this;
        }

        public ShaderProgram AddStageFromSource(string source, string virtualPath,
                                               ShaderStageExt stage, string entry = "main")
        {
            var module = _mgr.CompileFromSource(source, virtualPath, stage, entry);
            _modules.Add(module);
            _stages.Add(new PipelineShaderStageCreateInfo
            {
                SType = StructureType.PipelineShaderStageCreateInfo,
                Stage = StageToFlags(stage),
                Module = module,
                PName = (byte*)Marshal.StringToHGlobalAnsi(entry),
            });
            return this;
        }

        private static ShaderStageFlags StageToFlags(ShaderStageExt stage) => stage switch
        {
            ShaderStageExt.Vertex => ShaderStageFlags.VertexBit,
            ShaderStageExt.Fragment => ShaderStageFlags.FragmentBit,
            ShaderStageExt.Compute => ShaderStageFlags.ComputeBit,
            ShaderStageExt.Geometry => ShaderStageFlags.GeometryBit,
            ShaderStageExt.TessellationControl => ShaderStageFlags.TessellationControlBit,
            ShaderStageExt.TessellationEvaluation => ShaderStageFlags.TessellationEvaluationBit,
            _ => ShaderStageFlags.VertexBit,
        };

        public void Dispose()
        {
            // module 由 manager 管理，不在这里销毁
            _modules.Clear();
            _stages.Clear();
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 10: Shader include 解析
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// GLSL include 解析。shaderc 默认不处理 #include，需要 callback。
    /// 这里做预处理：递归把 include 展开成内联。
    /// </summary>
    public sealed class GlslIncludeProcessor
    {
        private readonly string _includeDir;
        private readonly HashSet<string> _included = new();

        public GlslIncludeProcessor(string includeDir)
        {
            _includeDir = includeDir;
        }

        /// <summary>预处理 GLSL 源码，展开 #include。</summary>
        public string Process(string source, string basePath = null)
        {
            _included.Clear();
            return ProcessInternal(source, basePath ?? _includeDir);
        }

        private string ProcessInternal(string source, string basePath)
        {
            var lines = source.Split('\n');
            var sb = new StringBuilder();
            foreach (var line in lines)
            {
                var trimmed = line.TrimStart();
                if (trimmed.StartsWith("#include"))
                {
                    // 提取 include 路径
                    int start = trimmed.IndexOf('"');
                    int end = trimmed.LastIndexOf('"');
                    if (start < 0 || end <= start)
                    {
                        sb.AppendLine(line);
                        continue;
                    }
                    string incPath = trimmed.Substring(start + 1, end - start - 1);
                    string fullPath = Path.IsPathRooted(incPath)
                        ? incPath
                        : Path.Combine(basePath, incPath);
                    fullPath = Path.GetFullPath(fullPath);

                    if (_included.Contains(fullPath))
                    {
                        sb.AppendLine($"// [already included] {incPath}");
                        continue;
                    }
                    _included.Add(fullPath);

                    if (File.Exists(fullPath))
                    {
                        string incSource = File.ReadAllText(fullPath);
                        string incBase = Path.GetDirectoryName(fullPath);
                        sb.AppendLine($"// === begin include: {incPath} ===");
                        sb.AppendLine(ProcessInternal(incSource, incBase));
                        sb.AppendLine($"// === end include: {incPath} ===");
                    }
                    else
                    {
                        sb.AppendLine($"// [ERROR] include not found: {incPath}");
                    }
                }
                else
                {
                    sb.AppendLine(line);
                }
            }
            return sb.ToString();
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 11: Shader 宏定义
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>Shader 宏定义。编译时注入 #define。</summary>
    public sealed class ShaderMacros
    {
        private readonly Dictionary<string, string> _macros = new();

        public ShaderMacros Define(string name, string value = "1")
        {
            _macros[name] = value;
            return this;
        }

        public ShaderMacros DefineInt(string name, int value)
        {
            _macros[name] = value.ToString();
            return this;
        }

        public ShaderMacros DefineFloat(string name, float value)
        {
            _macros[name] = value.ToString("F6", System.Globalization.CultureInfo.InvariantCulture);
            return this;
        }

        /// <summary>生成 #define 注入到 shader 顶部。</summary>
        public string GenerateDefines()
        {
            var sb = new StringBuilder();
            foreach (var kv in _macros)
            {
                sb.AppendLine($"#define {kv.Key} {kv.Value}");
            }
            return sb.ToString();
        }

        public void Clear() => _macros.Clear();
        public int Count => _macros.Count;
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 12: 版本信息
    // ════════════════════════════════════════════════════════════════════════════

    public static class ShaderManagerVersion
    {
        public const string Version = "1.0.0";
        public const string Description = "ShanyuEngine Vulkan Shader Manager (shaderc + SPIR-V cache + reflection)";
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 13: 使用示例
    // ════════════════════════════════════════════════════════════════════════════

    /*
    // === 使用示例 ===
    //
    // var shaderMgr = new VulkanShaderManager(vk, device, "Cache/");
    //
    // var program = new ShaderProgram(shaderMgr, "MeshShader")
    //     .AddStage("shaders/mesh.vert", ShaderStageExt.Vertex)
    //     .AddStage("shaders/mesh.frag", ShaderStageExt.Fragment);
    //
    // // 创建 pipeline 时用 program.Stages
    // var ci = new GraphicsPipelineCreateInfo { ... };
    // ci.PStages = program.Stages.ToArray();
    //
    // // hot reload
    // shaderMgr.Invalidate("shaders/mesh.vert");
    */

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 14: 单元测试桩
    // ════════════════════════════════════════════════════════════════════════════

    public static class ShaderManagerTestStub
    {
        public static bool TestSpirvMagic(byte[] spirv)
        {
            if (spirv.Length < 4) return false;
            return BitConverter.ToUInt32(spirv, 0) == 0x07230203;
        }

        public static string TestHash(string s)
        {
            using var sha = SHA256.Create();
            return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(s)))
                .Replace("-", "").Substring(0, 16);
        }

        public static ShaderStageExt TestGuessStage(string path)
        {
            var l = path.ToLowerInvariant();
            if (l.Contains(".vert")) return ShaderStageExt.Vertex;
            if (l.Contains(".frag")) return ShaderStageExt.Fragment;
            if (l.Contains(".comp")) return ShaderStageExt.Compute;
            return ShaderStageExt.Vertex;
        }
    }
}

// 文件结束 - VulkanShaderManager.cs
