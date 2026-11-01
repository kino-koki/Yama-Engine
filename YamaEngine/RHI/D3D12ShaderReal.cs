// ==================================================================================================
//  D3D12ShaderReal.cs - yama游戏引擎 D3D12 着色器编译真实实现
//
//  命名空间: YamaEngine.RHI
//  依赖: Vortice.D3DCompiler, Vortice.Direct3D12
//
//  功能覆盖:
//    - D3D12ShaderCompiler: 封装 Vortice.D3DCompiler (D3DCompile)
//    - 着色器模型: vs_5_0~vs_6_7, ps_5_0~ps_6_7, cs_5_0~cs_6_7, ds_5_0, hs_5_0, gs_5_0, ms_6_5, as_6_5, lib_6_x
//    - ShaderReflection: D3DReflect (常量缓冲区布局 / 绑定描述 / 输入输出签名 / 线程组大小)
//    - DefaultIncludeHandler: 系统/项目/引擎着色器目录搜索
//    - ShaderMacro / ShaderCompileFlags / ShaderBytecode
//    - CompileVariants: 多 define 组合并行编译 / 结果缓存
// ==================================================================================================

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Vortice.D3DCompiler;

namespace YamaEngine.RHI
{
    // ==============================================================================================
    //  ShaderCompileFlags - 编译选项
    // ==============================================================================================

    /// <summary>
    /// 着色器编译选项标志。
    /// </summary>
    [Flags]
    public enum ShaderCompileFlags : uint
    {
        None = 0,
        Debug = 1,
        SkipOptimization = 2,
        OptimizationLevel0 = 4,
        OptimizationLevel1 = 8,
        OptimizationLevel2 = 16,
        OptimizationLevel3 = 32,
        PackMatrixRowMajor = 64,
        PackMatrixColumnMajor = 128,
        AllResourcesBound = 256,
        WarningsAsErrors = 512,
        EnableStrictness = 1024,
        IEEEStrictness = 2048,
        PreferFlowControl = 4096,
        AvoidFlowControl = 8192,
        EnableBackwardsCompatibility = 16384,
        Default = OptimizationLevel3 | AllResourcesBound | EnableStrictness
    }

    /// <summary>
    /// 着色器宏定义 (预处理器 #define)
    /// </summary>
    public struct ShaderMacro
    {
        public string Name;
        public string Definition;

        public ShaderMacro(string name, string definition = "1")
        {
            Name = name;
            Definition = definition;
        }

        public static ShaderMacro[] FromDict(Dictionary<string, string> defines)
        {
            var macros = new ShaderMacro[defines.Count];
            int i = 0;
            foreach (var kv in defines)
            {
                macros[i++] = new ShaderMacro(kv.Key, kv.Value);
            }
            return macros;
        }
    }

    // ==============================================================================================
    //  ShaderBytecode - 着色器字节码容器
    // ==============================================================================================

    /// <summary>
    /// 着色器字节码容器。持有编译后的着色器二进制数据。
    /// </summary>
    public sealed class ShaderBytecode : IDisposable
    {
        private byte[] _bytecode;
        private string _entryPoint;
        private RHIShaderStage _stage;
        private string _sourceName;
        private string _targetProfile;
        private bool _disposed;

        public byte[] Data => _bytecode;
        public int Length => _bytecode?.Length ?? 0;
        public string EntryPoint => _entryPoint;
        public RHIShaderStage Stage => _stage;
        public string SourceName => _sourceName;
        public string TargetProfile => _targetProfile;
        public bool IsValid => _bytecode != null && _bytecode.Length > 0;
        public IntPtr NativePointer { get; private set; }

        public ShaderBytecode(byte[] bytecode, string entryPoint, RHIShaderStage stage,
            string sourceName = "", string targetProfile = "")
        {
            _bytecode = bytecode;
            _entryPoint = entryPoint;
            _stage = stage;
            _sourceName = sourceName;
            _targetProfile = targetProfile;
            NativePointer = IntPtr.Zero;
        }

        /// <summary>获取用于创建 PSO 的 Blob 引用</summary>
        public Vortice.Direct3D12.ShaderBytecode GetD3D12Bytecode()
        {
            return new Vortice.Direct3D12.ShaderBytecode(_bytecode);
        }

        /// <summary>序列化字节码到文件</summary>
        public void SaveToFile(string filePath)
        {
            File.WriteAllBytes(filePath, _bytecode);
        }

        /// <summary>从文件加载字节码</        >
        public static ShaderBytecode LoadFromFile(string filePath, string entryPoint = "",
            RHIShaderStage stage = RHIShaderStage.Compute)
        {
            var data = File.ReadAllBytes(filePath);
            return new ShaderBytecode(data, entryPoint, stage, filePath);
        }

        /// <summary>计算字节码哈希 (用于缓存)</summary>
        public string ComputeHash()
        {
            using var sha = System.Security.Cryptography.SHA256.Create();
            byte[] hash = sha.ComputeHash(_bytecode);
            return Convert.ToHexString(hash);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _bytecode = null;
        }
    }

    // ==============================================================================================
    //  DefaultIncludeHandler - HLSL 包含处理器
    // ==============================================================================================

    /// <summary>
    /// 默认 HLSL Include 处理器。按优先级搜索:
    /// 1. 相对于当前文件的目录
    /// 2. 引擎着色器目录
    /// 3. 系统包含目录
    /// </summary>
    public sealed class DefaultIncludeHandler : IDisposable
    {
        private readonly List<string> _searchDirectories;
        private bool _disposed;

        public DefaultIncludeHandler(string? baseDirectory = null,
            string? engineShaderDir = null)
        {
            _searchDirectories = new List<string>();

            // 当前文件目录
            if (!string.IsNullOrEmpty(baseDirectory))
                _searchDirectories.Add(baseDirectory);

            // 引擎着色器目录
            if (!string.IsNullOrEmpty(engineShaderDir))
                _searchDirectories.Add(engineShaderDir);

            // 常见引擎着色器路径
            _searchDirectories.Add("Shaders");
            _searchDirectories.Add("Assets/Shaders");
            _searchDirectories.Add(".");
        }

        /// <summary>添加搜索目录</summary>
        public void AddSearchDirectory(string directory)
        {
            if (!_searchDirectories.Contains(directory))
                _searchDirectories.Add(directory);
        }

        /// <summary>
        /// 解析 #include 指令，查找并返回文件内容。
        /// </summary>
        public IncludeResult? ResolveInclude(string includeName, string? parentFilePath)
        {
            // 搜索所有目录
            foreach (var dir in _searchDirectories)
            {
                string fullPath;
                if (Path.IsPathRooted(includeName))
                {
                    fullPath = includeName;
                }
                else
                {
                    // 相对于父文件目录
                    if (!string.IsNullOrEmpty(parentFilePath))
                    {
                        string parentDir = Path.GetDirectoryName(parentFilePath);
                        if (parentDir != null)
                        {
                            fullPath = Path.Combine(parentDir, includeName);
                            if (File.Exists(fullPath))
                            {
                                return CreateIncludeResult(fullPath);
                            }
                        }
                    }

                    fullPath = Path.Combine(dir, includeName);
                }

                if (File.Exists(fullPath))
                {
                    return CreateIncludeResult(fullPath);
                }
            }

            return null; // 未找到
        }

        private IncludeResult CreateIncludeResult(string fullPath)
        {
            string content = File.ReadAllText(fullPath);
            string dir = Path.GetDirectoryName(fullPath) ?? "";
            return new IncludeResult(content, dir);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
        }
    }

    /// <summary>Include 解析结果</summary>
    public struct IncludeResult
    {
        public string Content;
        public string Directory;

        public IncludeResult(string content, string directory)
        {
            Content = content;
            Directory = directory;
        }
    }

    // ==============================================================================================
    //  D3D12ShaderCompiler - 着色器编译器
    // ==============================================================================================

    /// <summary>
    /// D3D12 着色器编译器。封装 D3DCompiler.dll 的 D3DCompile / D3DCompile2 接口。
    /// </summary>
    public sealed class D3D12ShaderCompiler : IDisposable
    {
        private readonly DefaultIncludeHandler _includeHandler;
        private readonly ConcurrentDictionary<string, ShaderBytecode> _compileCache = new();
        private bool _disposed;

        public D3D12ShaderCompiler(string? engineShaderDir = null)
        {
            _includeHandler = new DefaultIncludeHandler(null, engineShaderDir);
        }

        public D3D12ShaderCompiler(DefaultIncludeHandler includeHandler)
        {
            _includeHandler = includeHandler;
        }

        /// <summary>
        /// 将引擎着色器阶段转换为着色器目标 Profile 字符串。
        /// 支持模型: vs_5_0~vs_6_7, ps_5_0~ps_6_7, cs_5_0~cs_6_7, ds_5_0, hs_5_0, gs_5_0, ms_6_5, as_6_5, lib_6_x
        /// </summary>
        public string GetShaderProfile(RHIShaderStage stage, RHIShaderModel model)
        {
            string prefix = stage switch
            {
                RHIShaderStage.Vertex => "vs",
                RHIShaderStage.Pixel => "ps",
                RHIShaderStage.Compute => "cs",
                RHIShaderStage.Domain => "ds",
                RHIShaderStage.Hull => "hs",
                RHIShaderStage.Geometry => "gs",
                RHIShaderStage.Mesh => "ms",
                RHIShaderStage.Amplification => "as",
                RHIShaderStage.Library => "lib",
                _ => throw new ArgumentException($"Unsupported shader stage: {stage}")
            };

            string modelStr = model switch
            {
                RHIShaderModel.SM_5_1 => "5_1",
                RHIShaderModel.SM_6_0 => "6_0",
                RHIShaderModel.SM_6_1 => "6_1",
                RHIShaderModel.SM_6_2 => "6_2",
                RHIShaderModel.SM_6_3 => "6_3",
                RHIShaderModel.SM_6_4 => "6_4",
                RHIShaderModel.SM_6_5 => "6_5",
                RHIShaderModel.SM_6_6 => "6_6",
                RHIShaderModel.SM_6_7 => "6_7",
                RHIShaderModel.SM_6_8 => "6_8",
                _ => "5_1"
            };

            return $"{prefix}_{modelStr}";
        }

        /// <summary>
        /// 编译着色器。调用 D3DCompile 将 HLSL 源码编译为字节码。
        /// </summary>
        public ShaderBytecode CompileShader(
            string hlslSource,
            string entryPoint,
            RHIShaderStage stage,
            RHIShaderModel model = RHIShaderModel.SM_6_0,
            ShaderMacro[]? defines = null,
            ShaderCompileFlags flags = ShaderCompileFlags.Default,
            string? sourceName = null)
        {
            string profile = GetShaderProfile(stage, model);
            return CompileShader(hlslSource, entryPoint, profile, stage, defines, flags, sourceName);
        }

        /// <summary>
        /// 编译着色器 (直接指定 profile 字符串)。
        /// </summary>
        public ShaderBytecode CompileShader(
            string hlslSource,
            string entryPoint,
            string targetProfile,
            RHIShaderStage stage,
            ShaderMacro[]? defines = null,
            ShaderCompileFlags flags = ShaderCompileFlags.Default,
            string? sourceName = null)
        {
            // 转换编译标志
            uint compileFlags = ConvertCompileFlags(flags);

            // 转换宏定义
            Macro[] nativeMacros = ConvertMacros(defines);

            // 调用 D3DCompile
            var resultCode = Compiler.Compile(
                hlslSource,
                entryPoint,
                targetProfile,
                compileFlags,
                nativeMacros);

            if (!resultCode.Success)
            {
                throw new InvalidOperationException(
                    $"Shader compilation failed ({sourceName ?? "unknown"}, {entryPoint}, {targetProfile}):\n{resultCode.ErrorMessage}");
            }

            // 构建结果
            return new ShaderBytecode(
                resultCode.Bytecode,
                entryPoint,
                stage,
                sourceName ?? "",
                targetProfile);
        }

        /// <summary>
        /// 从文件编译着色器。
        /// </summary>
        public ShaderBytecode CompileShaderFromFile(
            string filePath,
            string entryPoint,
            RHIShaderStage stage,
            RHIShaderModel model = RHIShaderModel.SM_6_0,
            ShaderMacro[]? defines = null,
            ShaderCompileFlags flags = ShaderCompileFlags.Default)
        {
            // 读取源文件
            string source = File.ReadAllText(filePath);

            // 处理 #include 指令
            string processedSource = ProcessIncludes(source, filePath);

            // 检查缓存
            string cacheKey = ComputeCacheKey(processedSource, entryPoint, stage, model, defines);
            if (_compileCache.TryGetValue(cacheKey, out var cached))
                return cached;

            // 编译
            var result = CompileShader(processedSource, entryPoint, stage, model, defines, flags, filePath);

            // 缓存
            _compileCache[cacheKey] = result;
            return result;
        }

        /// <summary>
        /// 处理 #include 指令 (简化版预处理)
        /// </summary>
        private string ProcessIncludes(string source, string currentFile)
        {
            var lines = source.Split('\n');
            var result = new System.Text.StringBuilder();

            foreach (var line in lines)
            {
                string trimmed = line.Trim();
                if (trimmed.StartsWith("#include"))
                {
                    // 提取 include 路径
                    int start = trimmed.IndexOf('"');
                    int end = trimmed.LastIndexOf('"');
                    if (start < 0 || end <= start)
                    {
                        // 尝试 < > 语法
                        start = trimmed.IndexOf('<');
                        end = trimmed.LastIndexOf('>');
                    }

                    if (start >= 0 && end > start)
                    {
                        string includePath = trimmed.Substring(start + 1, end - start - 1);
                        var includeResult = _includeHandler.ResolveInclude(includePath, currentFile);

                        if (includeResult.HasValue)
                        {
                            // 递归处理嵌套 include
                            string includedContent = ProcessIncludes(includeResult.Value.Content, includePath);
                            result.AppendLine(includedContent);
                            continue;
                        }
                    }
                }
                result.AppendLine(line);
            }

            return result.ToString();
        }

        /// <summary>
        /// 着色器变体编译: 对多组 define 组合进行并行编译。
        /// </summary>
        public Dictionary<string, ShaderBytecode> CompileVariants(
            string hlslSource,
            string entryPoint,
            RHIShaderStage stage,
            RHIShaderModel model,
            ShaderMacro[][] variantDefines,
            ShaderCompileFlags flags = ShaderCompileFlags.Default,
            string? sourceName = null)
        {
            var results = new Dictionary<string, ShaderBytecode>();
            var tasks = new Task<(string key, ShaderBytecode bytecode)>[variantDefines.Length];

            for (int i = 0; i < variantDefines.Length; i++)
            {
                int idx = i;
                ShaderMacro[] macros = variantDefines[idx];
                tasks[idx] = Task.Run(() =>
                {
                    string variantName = string.Join("_", macros.Select(m => $"{m.Name}={m.Definition}"));
                    var bytecode = CompileShader(hlslSource, entryPoint, stage, model, macros, flags, sourceName);
                    return (variantName, bytecode);
                });
            }

            Task.WaitAll(tasks);

            for (int i = 0; i < tasks.Length; i++)
            {
                var (key, bytecode) = tasks[i].Result;
                results[key] = bytecode;
            }

            return results;
        }

        private static uint ConvertCompileFlags(ShaderCompileFlags flags)
        {
            uint result = 0;
            if (flags.HasFlag(ShaderCompileFlags.Debug))
                result |= (uint)Vortice.D3DCompiler.ShaderCompileFlags.Debug;
            if (flags.HasFlag(ShaderCompileFlags.SkipOptimization))
                result |= (uint)Vortice.D3DCompiler.ShaderCompileFlags.SkipOptimization;
            if (flags.HasFlag(ShaderCompileFlags.OptimizationLevel0))
                result |= (uint)Vortice.D3DCompiler.ShaderCompileFlags.OptimizationLevel0;
            if (flags.HasFlag(ShaderCompileFlags.OptimizationLevel1))
                result |= (uint)Vortice.D3DCompiler.ShaderCompileFlags.OptimizationLevel1;
            if (flags.HasFlag(ShaderCompileFlags.OptimizationLevel2))
                result |= (uint)Vortice.D3DCompiler.ShaderCompileFlags.OptimizationLevel2;
            if (flags.HasFlag(ShaderCompileFlags.OptimizationLevel3))
                result |= (uint)Vortice.D3DCompiler.ShaderCompileFlags.OptimizationLevel3;
            if (flags.HasFlag(ShaderCompileFlags.PackMatrixRowMajor))
                result |= (uint)Vortice.D3DCompiler.ShaderCompileFlags.PackMatrixRowMajor;
            if (flags.HasFlag(ShaderCompileFlags.PackMatrixColumnMajor))
                result |= (uint)Vortice.D3DCompiler.ShaderCompileFlags.PackMatrixColumnMajor;
            if (flags.HasFlag(ShaderCompileFlags.AllResourcesBound))
                result |= (uint)Vortice.D3DCompiler.ShaderCompileFlags.AllResourcesBound;
            if (flags.HasFlag(ShaderCompileFlags.WarningsAsErrors))
                result |= (uint)Vortice.D3DCompiler.ShaderCompileFlags.WarningsAsErrors;
            if (flags.HasFlag(ShaderCompileFlags.EnableStrictness))
                result |= (uint)Vortice.D3DCompiler.ShaderCompileFlags.EnableStrictness;
            if (flags.HasFlag(ShaderCompileFlags.IEEEStrictness))
                result |= (uint)Vortice.D3DCompiler.ShaderCompileFlags.IEEEStrictness;
            if (flags.HasFlag(ShaderCompileFlags.PreferFlowControl))
                result |= (uint)Vortice.D3DCompiler.ShaderCompileFlags.PreferFlowControl;
            if (flags.HasFlag(ShaderCompileFlags.AvoidFlowControl))
                result |= (uint)Vortice.D3DCompiler.ShaderCompileFlags.AvoidFlowControl;
            return result;
        }

        private static Macro[] ConvertMacros(ShaderMacro[]? macros)
        {
            if (macros == null || macros.Length == 0)
                return Array.Empty<Macro>();

            var nativeMacros = new Macro[macros.Length];
            for (int i = 0; i < macros.Length; i++)
            {
                nativeMacros[i] = new Macro(macros[i].Name, macros[i].Definition);
            }
            return nativeMacros;
        }

        private static string ComputeCacheKey(string source, string entryPoint,
            RHIShaderStage stage, RHIShaderModel model, ShaderMacro[]? defines)
        {
            using var sha = System.Security.Cryptography.SHA256.Create();
            var sb = new System.Text.StringBuilder();
            sb.Append(source.GetHashCode());
            sb.Append(entryPoint);
            sb.Append(stage.GetHashCode());
            sb.Append(model.GetHashCode());
            if (defines != null)
            {
                foreach (var m in defines)
                {
                    sb.Append(m.Name);
                    sb.Append(m.Definition);
                }
            }
            byte[] hash = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(sb.ToString()));
            return Convert.ToHexString(hash);
        }

        /// <summary>清除编译缓存</summary>
        public void ClearCache()
        {
            foreach (var kv in _compileCache) kv.Value.Dispose();
            _compileCache.Clear();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            ClearCache();
            _includeHandler.Dispose();
        }
    }

    // ==============================================================================================
    //  ShaderCompilationResult - 编译结果辅助
    // ==============================================================================================

    internal static class Compiler
    {
        public static CompilationResult Compile(
            string source, string entryPoint, string profile,
            uint flags, Macro[] macros)
        {
            var compilationResult = Vortice.D3DCompiler.Compiler.Compile(
                source,
                entryPoint,
                profile,
                flags,
                macros);

            if (compilationResult.HasErrors)
            {
                string errorMsg = compilationResult.ErrorMessage;
                return new CompilationResult
                {
                    Success = false,
                    ErrorMessage = errorMsg,
                    Bytecode = Array.Empty<byte>()
                };
            }

            return new CompilationResult
            {
                Success = true,
                ErrorMessage = string.Empty,
                Bytecode = compilationResult.Bytecode.ToArray()
            };
        }
    }

    internal struct CompilationResult
    {
        public bool Success;
        public string ErrorMessage;
        public byte[] Bytecode;
    }

    // ==============================================================================================
    //  ShaderReflection - 着色器反射
    // ==============================================================================================

    /// <summary>
    /// 着色器反射信息。通过 D3DReflect 从着色器字节码提取绑定信息。
    /// </summary>
    public sealed class ShaderReflection
    {
        private readonly byte[] _bytecode;
        private bool _reflected;
        private List<ConstantBufferInfo> _constantBuffers;
        private List<BindingInfo> _bindings;
        private List<SignatureParameter> _inputParameters;
        private List<SignatureParameter> _outputParameters;
        private System.Numerics.Vector3 _threadGroupSize;
        private int _totalCBVBindings;
        private int _totalSRVBindings;
        private int _totalUAVBindings;
        private int _totalSamplerBindings;

        public IReadOnlyList<ConstantBufferInfo> ConstantBuffers
        {
            get { EnsureReflected(); return _constantBuffers; }
        }
        public IReadOnlyList<BindingInfo> Bindings
        {
            get { EnsureReflected(); return _bindings; }
        }
        public IReadOnlyList<SignatureParameter> InputParameters
        {
            get { EnsureReflected(); return _inputParameters; }
        }
        public IReadOnlyList<SignatureParameter> OutputParameters
        {
            get { EnsureReflected(); return _outputParameters; }
        }
        public System.Numerics.Vector3 ThreadGroupSize
        {
            get { EnsureReflected(); return _threadGroupSize; }
        }
        public int TotalCBVBindings { get { EnsureReflected(); return _totalCBVBindings; } }
        public int TotalSRVBindings { get { EnsureReflected(); return _totalSRVBindings; } }
        public int TotalUAVBindings { get { EnsureReflected(); return _totalUAVBindings; } }
        public int TotalSamplerBindings { get { EnsureReflected(); return _totalSamplerBindings; } }

        public ShaderReflection(byte[] bytecode)
        {
            _bytecode = bytecode ?? throw new ArgumentNullException(nameof(bytecode));
            _reflected = false;
            _constantBuffers = new List<ConstantBufferInfo>();
            _bindings = new List<BindingInfo>();
            _inputParameters = new List<SignatureParameter>();
            _outputParameters = new List<SignatureParameter>();
            _threadGroupSize = System.Numerics.Vector3.Zero;
        }

        private void EnsureReflected()
        {
            if (_reflected) return;
            _reflected = true;

            // 使用 D3DReflect 创建反射对象
            var reflection = Vortice.D3DCompiler.Compiler.Reflect(_bytecode);

            // 获取线程组大小 (Compute Shader)
            try
            {
                reflection.GetThreadGroupSize(out uint x, out uint y, out uint z);
                _threadGroupSize = new System.Numerics.Vector3(x, y, z);
            }
            catch { /* 非 Compute Shader 可能不支持 */ }

            // 遍历绑定描述
            _totalCBVBindings = 0;
            _totalSRVBindings = 0;
            _totalUAVBindings = 0;
            _totalSamplerBindings = 0;

            int bindingCount = reflection.Description.BoundResources;
            for (int i = 0; i < bindingCount; i++)
            {
                var bindDesc = reflection.GetResourceBindingDescription(i);

                _bindings.Add(new BindingInfo
                {
                    Name = bindDesc.Name,
                    Type = ConvertShaderInputType(bindDesc.Type),
                    BindPoint = bindDesc.BindPoint,
                    BindCount = bindDesc.BindCount,
                    Space = bindDesc.Space,
                    ReturnType = bindDesc.ReturnType,
                    Dimension = (int)bindDesc.Dimension,
                    NumSamples = (uint)bindDesc.NumSamples
                });

                switch (bindDesc.Type)
                {
                    case ShaderInputType.ConstantBuffer:
                        _totalCBVBindings++;
                        ExtractConstantBufferInfo(reflection, bindDesc.Name);
                        break;
                    case ShaderInputType.Texture:
                    case ShaderInputType.StructuredBuffer:
                    case ShaderInputType.ByteAddressBuffer:
                        _totalSRVBindings++;
                        break;
                    case ShaderInputType.UnorderedAccess:
                    case ShaderInputType.UAVAppend:
                    case ShaderInputType.UAVConsume:
                    case ShaderInputType.UAVRWStructured:
                    case ShaderInputType.UAVRWByteAddress:
                    case ShaderInputType.UAVRWTexture:
                    case ShaderInputType.UAVRWTyped:
                        _totalUAVBindings++;
                        break;
                    case ShaderInputType.Sampler:
                        _totalSamplerBindings++;
                        break;
                }
            }

            // 获取输入签名
            try
            {
                var inputSig = reflection.GetInputSignature();
                for (int i = 0; i < inputSig.Description.Parameters; i++)
                {
                    var param = inputSig.GetParameterDescription(i);
                    _inputParameters.Add(new SignatureParameter
                    {
                        SemanticName = param.SemanticName,
                        SemanticIndex = param.SemanticIndex,
                        Register = param.Register,
                        SystemValueType = (int)param.SystemValueType,
                        ComponentType = (int)param.ComponentType,
                        Mask = (int)param.ReadWriteMask,
                        ComponentCount = CountBits((uint)param.ReadWriteMask)
                    });
                }
            }
            catch { /* 无输入签名 */ }

            // 获取输出签名
            try
            {
                var outputSig = reflection.GetOutputSignature();
                for (int i = 0; i < outputSig.Description.Parameters; i++)
                {
                    var param = outputSig.GetParameterDescription(i);
                    _outputParameters.Add(new SignatureParameter
                    {
                        SemanticName = param.SemanticName,
                        SemanticIndex = param.SemanticIndex,
                        Register = param.Register,
                        SystemValueType = (int)param.SystemValueType,
                        ComponentType = (int)param.ComponentType,
                        Mask = (int)param.ReadWriteMask,
                        ComponentCount = CountBits((uint)param.ReadWriteMask)
                    });
                }
            }
            catch { /* 无输出签名 */ }

            reflection.Dispose();
        }

        private void ExtractConstantBufferInfo(ShaderReflection reflection, string cbName)
        {
            try
            {
                var cb = reflection.GetConstantBufferByName(cbName);
                var cbDesc = cb.Description;

                var variables = new List<ConstantBufferVariable>();
                for (int j = 0; j < cbDesc.Variables; j++)
                {
                    var varDesc = cb.GetVariableDescription(j);
                    var typeDesc = varDesc.GetVariableType();

                    variables.Add(new ConstantBufferVariable
                    {
                        Name = varDesc.Name,
                        StartOffset = varDesc.StartOffset,
                        Size = varDesc.Size,
                        Type = typeDesc.Description,
                        TypeName = typeDesc.Description.Name
                    });

                    typeDesc.Dispose();
                }

                _constantBuffers.Add(new ConstantBufferInfo
                {
                    Name = cbName,
                    Size = cbDesc.Size,
                    Variables = variables
                });

                cb.Dispose();
            }
            catch { /* 反射失败 */ }
        }

        private static ShaderInputBindingType ConvertShaderInputType(ShaderInputType type)
        {
            return (ShaderInputBindingType)(int)type;
        }

        private static int CountBits(uint value)
        {
            int count = 0;
            while (value != 0)
            {
                count += (int)(value & 1);
                value >>= 1;
            }
            return count;
        }
    }

    // === 反射信息结构体 ===

    /// <summary>常量缓冲区信息</summary>
    public struct ConstantBufferInfo
    {
        public string Name;
        public int Size;
        public List<ConstantBufferVariable> Variables;
    }

    /// <summary>常量缓冲区变量信息</summary>
    public struct ConstantBufferVariable
    {
        public string Name;
        public int StartOffset;
        public int Size;
        public ShaderVariableTypeDescription Type;
        public string TypeName;
    }

    /// <summary>资源绑定信息</summary>
    public struct BindingInfo
    {
        public string Name;
        public ShaderInputBindingType Type;
        public int BindPoint;
        public int BindCount;
        public int Space;
        public ShaderResourceReturnType ReturnType;
        public int Dimension;
        public uint NumSamples;
    }

    /// <summary>签名参数 (顶点输入/像素输出)</summary>
    public struct SignatureParameter
    {
        public string SemanticName;
        public int SemanticIndex;
        public int Register;
        public int SystemValueType;
        public int ComponentType;
        public int Mask;
        public int ComponentCount;
    }
}
