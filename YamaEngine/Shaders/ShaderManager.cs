using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using YamaEngine.RHI;

namespace YamaEngine.Shaders
{
    // ════════════════════════════════════════════════════════════════════════
    // 着色器管理器 — 统一的着色器编译/缓存/加载系统
    //
    // 职责:
    //   1. 管理 HLSL 源码 -> 字节码的编译流程 (DXC 优先, FXC 回退)
    //   2. 内存缓存 (ConcurrentDictionary, 线程安全)
    //   3. 磁盘缓存 (.cso 文件, 增量编译)
    //   4. 着色器排列管理 (不同 define 组合)
    //   5. 统一 API: GetBytecode() 供渲染系统调用
    //
    // 使用方式:
    //   var sm = ShaderManager.Instance;
    //   sm.Initialize(@"C:\Shaders", device);
    //   var bytecode = sm.GetBytecode("ProbeField/SSGI", "SSGICS", RHIShaderStage.Compute, RHIShaderModel.SM_6_6);
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 着色器排列键: 由着色器名 + 入口点 + 目标 + define 组合唯一确定。
    /// </summary>
    public readonly struct ShaderKey : IEquatable<ShaderKey>
    {
        public readonly string ShaderName;      // e.g. "ProbeField/SSGI"
        public readonly string EntryPoint;      // e.g. "SSGICS"
        public readonly string Target;          // e.g. "cs_6_6"
        public readonly int DefinesHash;        // define 组合的哈希

        public ShaderKey(string name, string entry, string target, int definesHash)
        {
            ShaderName = name; EntryPoint = entry; Target = target; DefinesHash = definesHash;
        }

        public bool Equals(ShaderKey o) =>
            ShaderName == o.ShaderName && EntryPoint == o.EntryPoint && Target == o.Target && DefinesHash == o.DefinesHash;
        public override bool Equals(object? o) => o is ShaderKey k && Equals(k);
        public override int GetHashCode() => HashCode.Combine(ShaderName, EntryPoint, Target, DefinesHash);
        public static bool operator ==(ShaderKey a, ShaderKey b) => a.Equals(b);
        public static bool operator !=(ShaderKey a, ShaderKey b) => !a.Equals(b);
    }

    /// <summary>
    /// 着色器管理器: 统一管理着色器编译、缓存和加载。
    /// 线程安全, 支持并行编译。
    /// </summary>
    public sealed class ShaderManager
    {
        private static ShaderManager? _instance;
        /// <summary>全局实例 (需先 Initialize)。</summary>
        public static ShaderManager Instance => _instance ?? throw new InvalidOperationException("ShaderManager 未初始化。请先调用 Initialize()。");

        private DxcCompiler? _dxc;
        private string _shaderDir = "";
        private string _cacheDir = "";
        private bool _initialized;
        private bool _useDebug;
        private RHIShaderModel _defaultSM = RHIShaderModel.SM_6_6;

        // 内存缓存
        private readonly ConcurrentDictionary<ShaderKey, RHIShaderBytecode> _cache = new();
        // 源码缓存 (文件路径 -> 文件内容)
        private readonly ConcurrentDictionary<string, string> _sourceCache = new();

        // 着色器排列定义表 (着色器名 -> define 名称列表)
        private readonly ConcurrentDictionary<string, List<string>> _permutationDefs = new();

        /// <summary>是否已初始化。</summary>
        public bool IsInitialized => _initialized;
        /// <summary>DXC 是否可用。</summary>
        public bool DxcAvailable => _dxc?.IsAvailable ?? false;
        /// <summary>默认着色器模型。</summary>
        public RHIShaderModel DefaultShaderModel => _defaultSM;
        /// <summary>着色器源码根目录。</summary>
        public string ShaderDirectory => _shaderDir;

        /// <summary>初始化着色器管理器。</summary>
        public void Initialize(string shaderDir, RHIShaderModel defaultSM = RHIShaderModel.SM_6_6, bool useDebug = false)
        {
            if (_initialized) return;

            _shaderDir = shaderDir ?? throw new ArgumentNullException(nameof(shaderDir));
            _cacheDir = Path.Combine(shaderDir, "Compiled");
            _defaultSM = defaultSM;
            _useDebug = useDebug;

            Directory.CreateDirectory(_cacheDir);
            _dxc = new DxcCompiler(_shaderDir, _cacheDir, _useDebug);
            _initialized = true;

            Console.WriteLine($"[ShaderManager] 初始化完成. 着色器目录: {_shaderDir}");
            Console.WriteLine($"[ShaderManager] DXC 可用: {_dxc.IsAvailable}, COM互操作: {_dxc.UseComInterop}");
            Console.WriteLine($"[ShaderManager] 默认着色器模型: SM {_defaultSM}");
        }

        /// <summary>
        /// 获取着色器字节码 (线程安全, 自动缓存)。
        /// </summary>
        /// <param name="shaderName">着色器逻辑名称, 如 "ProbeField/SSGI" (对应 ProbeField/SSGI.hlsl)</param>
        /// <param name="entryPoint">入口点, 如 "SSGICS"</param>
        /// <param name="stage">着色器阶段</param>
        /// <param name="sm">着色器模型 (默认使用 DefaultShaderModel)</param>
        /// <param name="defines">编译宏定义 (null 则无额外定义)</param>
        public RHIShaderBytecode GetBytecode(
            string shaderName, string entryPoint, RHIShaderStage stage,
            RHIShaderModel? sm = null, Dictionary<string, string>? defines = null)
        {
            if (!_initialized) throw new InvalidOperationException("ShaderManager 未初始化。");
            var model = sm ?? _defaultSM;
            string target = DxcCompiler.GetTargetProfile(stage, model);
            int defHash = ComputeDefinesHash(defines);
            var key = new ShaderKey(shaderName, entryPoint, target, defHash);

            // 内存缓存
            if (_cache.TryGetValue(key, out var cached))
                return cached;

            // 编译
            var bytecode = CompileShader(shaderName, entryPoint, stage, model, defines);
            _cache[key] = bytecode;
            return bytecode;
        }

        /// <summary>从 HLSL 源码字符串编译 (非文件)。</summary>
        public RHIShaderBytecode CompileFromSource(
            string hlslSource, string entryPoint, RHIShaderStage stage,
            RHIShaderModel? sm = null, string? sourceName = null,
            Dictionary<string, string>? defines = null)
        {
            if (!_initialized) throw new InvalidOperationException("ShaderManager 未初始化。");
            var model = sm ?? _defaultSM;
            return _dxc!.Compile(hlslSource, entryPoint, stage, model, sourceName, defines);
        }

        /// <summary>注册着色器排列定义 (用于批量预编译)。</summary>
        public void RegisterPermutation(string shaderName, params string[] defineNames)
        {
            var list = _permutationDefs.GetOrAdd(shaderName, _ => new List<string>());
            foreach (var d in defineNames)
                if (!list.Contains(d)) list.Add(d);
        }

        /// <summary>预编译所有已注册的着色器排列。</summary>
        public void PrecompileAll()
        {
            // 遍历 Shaders/ 目录下所有 .hlsl 文件
            if (!Directory.Exists(_shaderDir)) return;
            var files = Directory.GetFiles(_shaderDir, "*.hlsl", SearchOption.AllDirectories);
            Console.WriteLine($"[ShaderManager] 预编译 {files.Length} 个着色器文件...");

            foreach (var file in files)
            {
                try
                {
                    var content = File.ReadAllText(file);
                    var entryPoints = ExtractEntryPoints(content);
                    var relPath = Path.GetRelativePath(_shaderDir, file);
                    var shaderName = Path.ChangeExtension(relPath, null).Replace('\\', '/');

                    foreach (var (entry, stage) in entryPoints)
                    {
                        try
                        {
                            GetBytecode(shaderName, entry, stage);
                            Console.WriteLine($"  [OK] {shaderName}::{entry}");
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"  [FAIL] {shaderName}::{entry}: {ex.Message.Split('\n')[0]}");
                        }
                    }
                }
                catch { }
            }
            Console.WriteLine($"[ShaderManager] 预编译完成. 内存缓存: {_cache.Count} 个着色器。");
        }

        /// <summary>从 HLSL 源码中提取入口点 (扫描 shader 签名)。</summary>
        private static List<(string entry, RHIShaderStage stage)> ExtractEntryPoints(string source)
        {
            var result = new List<(string, RHIShaderStage)>();
            // 简单扫描: [shader("compute")] void MainCS(...) / [shader("vertex")] / [shader("pixel")]
            // 或传统: void MainVS(), void MainPS(), void MainCS() 等
            var lines = source.Split('\n');
            foreach (var line in lines)
            {
                string trimmed = line.Trim();
                if (trimmed.Contains("[shader(\"compute\"") || trimmed.Contains("[numthreads"))
                {
                    var name = ExtractFunctionName(trimmed, "CS");
                    if (name != null) result.Add((name, RHIShaderStage.Compute));
                }
                if (trimmed.Contains("[shader(\"vertex\"") || trimmed.Contains("void MainVS") || trimmed.Contains("void VS("))
                {
                    var name = ExtractFunctionName(trimmed, "VS");
                    if (name != null) result.Add((name, RHIShaderStage.Vertex));
                }
                if (trimmed.Contains("[shader(\"pixel\"") || trimmed.Contains("void MainPS") || trimmed.Contains("void PS("))
                {
                    var name = ExtractFunctionName(trimmed, "PS");
                    if (name != null) result.Add((name, RHIShaderStage.Pixel));
                }
            }
            return result;
        }

        private static string? ExtractFunctionName(string line, string suffix)
        {
            // 查找 void Name( 模式
            int idx = line.IndexOf("void ");
            if (idx < 0) return null;
            int start = idx + 5;
            int paren = line.IndexOf('(', start);
            if (paren < 0) return null;
            return line.Substring(start, paren - start).Trim();
        }

        /// <summary>实际编译着色器。</summary>
        private RHIShaderBytecode CompileShader(
            string shaderName, string entryPoint, RHIShaderStage stage,
            RHIShaderModel sm, Dictionary<string, string>? defines)
        {
            // 构建文件路径
            string filePath = Path.Combine(_shaderDir, shaderName.Replace('/', Path.DirectorySeparatorChar) + ".hlsl");

            if (!File.Exists(filePath))
            {
                // 尝试其他扩展名
                filePath = Path.Combine(_shaderDir, shaderName.Replace('/', Path.DirectorySeparatorChar) + ".fx");
                if (!File.Exists(filePath))
                {
                    throw new FileNotFoundException($"着色器源文件未找到: {shaderName}.hlsl", filePath);
                }
            }

            return _dxc!.CompileFromFile(filePath, entryPoint, stage, sm, defines);
        }

        /// <summary>计算 define 字典的哈希。</summary>
        private static int ComputeDefinesHash(Dictionary<string, string>? defines)
        {
            if (defines == null || defines.Count == 0) return 0;
            int hash = 17;
            foreach (var kv in System.Linq.Enumerable.OrderBy(defines, d => d.Key))
                hash = hash * 31 + HashCode.Combine(kv.Key, kv.Value);
            return hash;
        }

        /// <summary>清除所有缓存。</summary>
        public void ClearCache()
        {
            _cache.Clear();
            _sourceCache.Clear();
        }

        /// <summary>获取缓存统计。</summary>
        public (int memCount, string cacheDir, bool dxcAvailable) GetStats()
        {
            return (_cache.Count, _cacheDir, _dxc?.IsAvailable ?? false);
        }
    }
}
