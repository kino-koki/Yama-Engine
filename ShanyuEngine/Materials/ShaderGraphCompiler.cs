// =============================================================================
//  Shanyu Engine - ShaderGraphCompiler.cs
//  山雨引擎 - 着色器图编译器
//
//  本文件负责将材质编辑器中的节点图 (MaterialGraph) 编译为可执行的
//  HLSL 源码，并通过 RHI 设备 (RHIDevice.CompileShader) 生成着色器字节码。
//
//  核心功能:
//    * 拓扑排序节点求值 -- 从主输出节点反向 DFS，确定求值顺序
//    * 节点 -> HLSL 代码生成 -- 逐节点生成内联表达式或临时变量
//    * Vertex Shader 生成 -- 顶点变换 + 世界位置偏移 (WPO) 注入
//    * Pixel Shader 生成 -- 材质属性求值 + 着色模型 (BRDF) 调度
//    * Compute Shader 生成 -- 材质计算着色器 (离线烘焙 / 后处理)
//    * Mesh Shader 生成 -- 基于 Meshlet 的 VS+PS 替代路径
//    * UV 集合管理 -- UV0 ~ UV7 多通道支持
//    * 着色器变体 (Shader Variant) 与关键字 (Keyword) 系统 -- #define 驱动的分支
//    * Permutation 优化 -- 剔除不可达/冗余的变体排列组合
//    * Includes / Defines 管理 -- 自定义头文件和宏定义注入
//    * 着色器缓存与增量编译 -- 基于内容哈希的磁盘缓存
//    * 与 RHIDevice.CompileShader 对接 -- 最终产出 RHIShaderBytecode
//
//  代码风格: 与 Common.hlsli / BasicPBR.hlsl 的约定保持一致
//    (行主序矩阵、mul(vector, matrix) 约定、b0 寄存器存放逐绘制常量)。
// =============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using ShanyuEngine.RHI;

namespace ShanyuEngine.Materials
{
    // ----------------------------------------------------------------
    // 编译选项与结果
    // ----------------------------------------------------------------

    /// <summary>着色器目标语言。</summary>
    public enum ShaderTargetLanguage
    {
        HLSL,
        GLSL
    }

    /// <summary>着色器编译选项。控制编译目标、启用的关键字与生成的着色器阶段。</summary>
    public sealed class ShaderCompileOptions
    {
        public RHIShaderModel TargetShaderModel { get; set; } = RHIShaderModel.SM_6_6;
        public ShaderTargetLanguage Language { get; set; } = ShaderTargetLanguage.HLSL;
        public HashSet<string> Keywords { get; set; } = new();
        public bool GenerateVertexShader { get; set; } = true;
        public bool GeneratePixelShader { get; set; } = true;
        public bool GenerateComputeShader { get; set; } = false;
        public bool GenerateMeshShader { get; set; } = false;
        public bool UseCache { get; set; } = true;
        public bool EmitDebugComments { get; set; } = true;

        /// <summary>计算着色器线程组大小 (X, Y, Z)。</summary>
        public Vector3 ComputeThreadGroupSize { get; set; } = new Vector3(8, 8, 1);
    }

    /// <summary>着色器编译结果。包含生成的源码与编译状态。</summary>
    public sealed class ShaderCompileResult
    {
        public bool Success { get; set; }
        public string ErrorMessage { get; set; } = string.Empty;
        public string? VertexShaderSource { get; set; }
        public string? PixelShaderSource { get; set; }
        public string? ComputeShaderSource { get; set; }
        public string? MeshShaderSource { get; set; }
        public string ContentHash { get; set; } = string.Empty;
        public long ElapsedMs { get; set; }
        public bool CacheHit { get; set; }
    }

    // ----------------------------------------------------------------
    // 着色器变体与 Permutation
    // ----------------------------------------------------------------

    /// <summary>着色器变体描述。由启用的关键字集合唯一定义一个变体。</summary>
    public readonly struct ShaderVariantKey : IEquatable<ShaderVariantKey>
    {
        public string KeyString { get; }
        public ulong Hash { get; }

        public ShaderVariantKey(IEnumerable<string> keywords)
        {
            var sorted = keywords.OrderBy(k => k, StringComparer.Ordinal).ToList();
            KeyString = string.Join(",", sorted);
            // FNV-1a 64-bit
            ulong h = 14695981039346656037UL;
            foreach (var c in KeyString)
            {
                h ^= (byte)c;
                h *= 1099511628211UL;
            }
            Hash = h;
        }

        public bool Equals(ShaderVariantKey other) => Hash == other.Hash;
        public override bool Equals(object? obj) => obj is ShaderVariantKey k && Equals(k);
        public override int GetHashCode() => Hash.GetHashCode();
        public static bool operator ==(ShaderVariantKey a, ShaderVariantKey b) => a.Hash == b.Hash;
        public static bool operator !=(ShaderVariantKey a, ShaderVariantKey b) => a.Hash != b.Hash;
    }

    /// <summary>
    /// Permutation 生成器。根据材质图中定义的所有关键字，枚举可能的变体排列组合，
    /// 并通过可达性分析剔除实际不会用到的冗余变体。
    /// </summary>
    public static class ShaderPermutationBuilder
    {
        /// <summary>枚举材质图的所有有效变体。</summary>
        public static List<ShaderVariantKey> EnumerateVariants(MaterialGraph graph)
        {
            var keywords = graph.Keywords;
            if (keywords == null || keywords.Count == 0)
                return new List<ShaderVariantKey> { new ShaderVariantKey(Array.Empty<string>()) };

            var results = new List<ShaderVariantKey>();
            var current = new List<string>();

            void Recurse(int index)
            {
                if (index == keywords.Count)
                {
                    results.Add(new ShaderVariantKey(current));
                    return;
                }
                Recurse(index + 1);
                current.Add(keywords[index].Name);
                Recurse(index + 1);
                current.RemoveAt(current.Count - 1);
            }
            Recurse(0);

            // 剔除未被任何节点引用的关键字
            var referenced = CollectReferencedKeywords(graph);
            if (referenced.Count < keywords.Count)
            {
                results = results
                    .Where(v => v.KeyString.Split(',', StringSplitOptions.RemoveEmptyEntries)
                                 .All(k => referenced.Contains(k)))
                    .ToList();
                if (!results.Any(v => string.IsNullOrEmpty(v.KeyString)))
                    results.Insert(0, new ShaderVariantKey(Array.Empty<string>()));
            }

            return results;
        }

        private static HashSet<string> CollectReferencedKeywords(MaterialGraph graph)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            foreach (var n in graph.Nodes)
            {
                if (n.Properties.TryGetValue("Keyword", out var kw) && !string.IsNullOrEmpty(kw))
                    set.Add(kw);
            }
            return set;
        }
    }

    // ----------------------------------------------------------------
    // 着色器缓存
    // ----------------------------------------------------------------

    /// <summary>着色器缓存条目。</summary>
    public sealed class ShaderCacheEntry
    {
        public string Hash { get; set; } = string.Empty;
        public string MaterialGuid { get; set; } = string.Empty;
        public ulong VariantHash { get; set; }
        public string VertexSource { get; set; } = string.Empty;
        public string PixelSource { get; set; } = string.Empty;
        public string ComputeSource { get; set; } = string.Empty;
        public string MeshSource { get; set; } = string.Empty;
        public DateTime CompiledAt { get; set; }
        public DateTime SourceModified { get; set; }
    }

    /// <summary>
    /// 着色器磁盘缓存。基于 SHA-256 内容哈希实现增量编译。
    /// </summary>
    public sealed class ShaderCache
    {
        private readonly string _cacheDir;
        private readonly Dictionary<string, ShaderCacheEntry> _memoryCache = new();

        public string CacheDirectory => _cacheDir;

        public ShaderCache(string? cacheDir = null)
        {
            _cacheDir = cacheDir ?? Path.Combine(Path.GetTempPath(), "ShanyuEngine_ShaderCache");
            try { Directory.CreateDirectory(_cacheDir); } catch { }
        }

        /// <summary>计算材质图 + 选项的内容哈希 (SHA-256 截断为 16 字节十六进制)。</summary>
        public string ComputeHash(MaterialGraph graph, ShaderCompileOptions options)
        {
            var sb = new StringBuilder();
            sb.Append(graph.Guid).Append('|');
            sb.Append(graph.Version).Append('|');
            sb.Append(graph.BlendMode).Append('|');
            sb.Append(graph.ShadingModel).Append('|');
            sb.Append(graph.DoubleSided).Append('|');
            sb.Append(graph.OpacityMaskClipValue.ToString("G9")).Append('|');
            sb.Append(graph.Nodes.Count).Append('|');

            foreach (var n in graph.Nodes.OrderBy(n => n.Id))
            {
                sb.Append(n.Id).Append(':').Append(n.Type).Append(':').Append(n.Name).Append(';');
                sb.Append(n.FloatValue.ToString("G9")).Append(',');
                sb.Append(n.VectorValue.X.ToString("G9")).Append(',');
                sb.Append(n.VectorValue.Y.ToString("G9")).Append(',');
                sb.Append(n.VectorValue.Z.ToString("G9")).Append(',');
                sb.Append(n.VectorValue.W.ToString("G9")).Append(',');
                sb.Append(n.Properties.Count).Append(',');
                foreach (var p in n.Properties.OrderBy(p => p.Key))
                    sb.Append(p.Key).Append('=').Append(p.Value).Append(',');
                sb.Append('|');
            }

            foreach (var c in graph.Edges.OrderBy(c => c.Id))
                sb.Append(c.FromNodeId).Append('.').Append(c.FromPinId)
                  .Append("->").Append(c.ToNodeId).Append('.').Append(c.ToPinId).Append(';');

            foreach (var inc in graph.Includes.OrderBy(i => i))
                sb.Append("inc:").Append(inc).Append(';');

            foreach (var def in graph.CustomDefines.OrderBy(d => d))
                sb.Append("def:").Append(def).Append(';');

            sb.Append(options.TargetShaderModel).Append('|');
            sb.Append(options.Language).Append('|');
            var sortedKw = options.Keywords.OrderBy(k => k, StringComparer.Ordinal);
            sb.Append(string.Join(",", sortedKw));

            return ComputeSHA256(sb.ToString());
        }

        public bool TryGet(string hash, ulong variantHash, out ShaderCacheEntry? entry)
        {
            var key = MakeKey(hash, variantHash);
            if (_memoryCache.TryGetValue(key, out entry)) return true;

            var path = Path.Combine(_cacheDir, key + ".cache.json");
            if (File.Exists(path))
            {
                try
                {
                    var opts = new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                    entry = System.Text.Json.JsonSerializer.Deserialize<ShaderCacheEntry>(File.ReadAllText(path), opts);
                    if (entry != null)
                    {
                        _memoryCache[key] = entry;
                        return true;
                    }
                }
                catch { }
            }
            entry = null;
            return false;
        }

        public void Put(ShaderCacheEntry entry)
        {
            var key = MakeKey(entry.Hash, entry.VariantHash);
            _memoryCache[key] = entry;
            var path = Path.Combine(_cacheDir, key + ".cache.json");
            try
            {
                var opts = new System.Text.Json.JsonSerializerOptions { WriteIndented = true };
                File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(entry, opts));
            }
            catch { }
        }

        public void Clear()
        {
            _memoryCache.Clear();
            try { foreach (var f in Directory.GetFiles(_cacheDir, "*.cache.json")) File.Delete(f); }
            catch { }
        }

        private static string MakeKey(string hash, ulong variantHash) => $"{hash}_{variantHash:X16}";

        private static string ComputeSHA256(string input)
        {
            using var sha = SHA256.Create();
            var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(input));
            var sb = new StringBuilder(32);
            for (int i = 0; i < 16; i++) sb.Append(bytes[i].ToString("x2"));
            return sb.ToString();
        }
    }

    // ----------------------------------------------------------------
    // UV 集合管理
    // ----------------------------------------------------------------

    /// <summary>
    /// UV 集合管理器。跟踪材质图中实际使用了哪些 UV 通道 (UV0 ~ UV7)，
    /// 并在着色器中声明对应的 TEXCOORD 语义。
    /// </summary>
    internal sealed class UvSetManager
    {
        private readonly HashSet<int> _usedChannels = new();

        /// <summary>标记使用某个 UV 通道。</summary>
        public void MarkUsed(int channel)
        {
            if (channel >= 0 && channel <= 7)
                _usedChannels.Add(channel);
        }

        /// <summary>自动扫描材质图，标记所有 TexCoord 节点使用的通道。</summary>
        public void ScanGraph(MaterialGraph graph)
        {
            _usedChannels.Clear();
            foreach (var node in graph.Nodes)
            {
                if (!node.Enabled) continue;
                if (node.Type == MaterialNodeType.TexCoord)
                    MarkUsed(node.UVChannel);
            }
        }

        /// <summary>获取排序后的已用 UV 通道列表。</summary>
        public List<int> GetUsedChannels() => _usedChannels.OrderBy(c => c).ToList();

        /// <summary>获取使用的 UV 通道数量。</summary>
        public int Count => _usedChannels.Count;

        /// <summary>
        /// 生成顶点输入结构中的 UV 字段声明。
        /// </summary>
        public string GenerateVertexInputDeclarations()
        {
            if (_usedChannels.Count == 0) return string.Empty;

            var sb = new StringBuilder();
            foreach (var ch in _usedChannels.OrderBy(c => c))
            {
                sb.AppendLine($"    float2 TexCoord{ch} : TEXCOORD{ch};");
            }
            return sb.ToString();
        }

        /// <summary>
        /// 生成像素输入结构中的 UV 字段声明。
        /// </summary>
        public string GeneratePixelInputDeclarations(int extraOffset)
        {
            if (_usedChannels.Count == 0) return string.Empty;

            var sb = new StringBuilder();
            foreach (var ch in _usedChannels.OrderBy(c => c))
            {
                sb.AppendLine($"    float2 TexCoord{ch} : TEXCOORD{extraOffset + ch};");
            }
            return sb.ToString();
        }

        /// <summary>
        /// 生成 VS 中将输入 UV 传递到输出的赋值代码。
        /// </summary>
        public string GenerateVSUvPassthrough(int extraOffset)
        {
            if (_usedChannels.Count == 0) return string.Empty;

            var sb = new StringBuilder();
            foreach (var ch in _usedChannels.OrderBy(c => c))
            {
                sb.AppendLine($"    output.TexCoord{ch} = input.TexCoord{ch};");
            }
            return sb.ToString();
        }
    }

    // ----------------------------------------------------------------
    // Includes / Defines 管理
    // ----------------------------------------------------------------

    /// <summary>
    /// Includes 和 Defines 管理器。
    /// 负责收集材质图的 include 路径和自定义宏定义，
    /// 并在生成的着色器源码中按正确位置注入。
    /// </summary>
    internal sealed class ShaderIncludeDefineManager
    {
        private readonly List<string> _includes = new();
        private readonly Dictionary<string, string> _defines = new(StringComparer.Ordinal);
        private readonly List<string> _graphIncludes;
        private readonly List<string> _graphDefines;
        private readonly List<ShaderKeyword> _keywords;
        private readonly HashSet<string> _enabledKeywords;

        public ShaderIncludeDefineManager(MaterialGraph graph, ShaderCompileOptions options)
        {
            _graphIncludes = graph.Includes;
            _graphDefines = graph.CustomDefines;
            _keywords = graph.Keywords;
            _enabledKeywords = options.Keywords;
        }

        /// <summary>
        /// 收集所有 include 和 define。
        /// </summary>
        public void Collect()
        {
            _includes.Clear();
            _defines.Clear();

            // 材质图自定义 includes
            foreach (var inc in _graphIncludes)
                _includes.Add(inc);

            // 材质图自定义 defines
            foreach (var def in _graphDefines)
            {
                var eqIdx = def.IndexOf('=');
                if (eqIdx > 0)
                    _defines[def.Substring(0, eqIdx).Trim()] = def.Substring(eqIdx + 1).Trim();
                else
                    _defines[def.Trim()] = "1";
            }

            // 关键字 defines: KW_XXX 1 或 0
            foreach (var kw in _keywords)
            {
                var key = $"KW_{kw.Name.ToUpperInvariant()}";
                var enabled = _enabledKeywords.Contains(kw.Name);
                _defines[key] = enabled ? "1" : "0";
            }
        }

        /// <summary>生成 #include 指令块。</summary>
        public string GenerateIncludeBlock()
        {
            if (_includes.Count == 0) return string.Empty;

            var sb = new StringBuilder();
            sb.AppendLine("// === Includes ===");
            foreach (var inc in _includes)
                sb.AppendLine($"#include \"{inc}\"");
            sb.AppendLine();
            return sb.ToString();
        }

        /// <summary>生成 #define 指令块。</summary>
        public string GenerateDefineBlock()
        {
            if (_defines.Count == 0) return string.Empty;

            var sb = new StringBuilder();
            sb.AppendLine("// === Defines ===");
            foreach (var kv in _defines.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                sb.AppendLine($"#define {kv.Key} {kv.Value}");
            sb.AppendLine();
            return sb.ToString();
        }

        /// <summary>获取所有定义的 key。</summary>
        public IReadOnlyDictionary<string, string> Defines => _defines;
    }

    // ----------------------------------------------------------------
    // 材质函数 (供 FunctionCall 节点内联展开)
    // ----------------------------------------------------------------

    /// <summary>材质函数：可复用的子节点图。</summary>

    // ----------------------------------------------------------------
    // 着色器图编译器主体
    // ----------------------------------------------------------------

    /// <summary>
    /// 着色器图编译器。将 MaterialGraph 编译为 HLSL 源码。
    ///
    /// 编译流程:
    ///   1. 校验材质图合法性
    ///   2. 计算内容哈希，查询缓存(命中则直接返回)
    ///   3. UV 集合扫描
    ///   4. Includes / Defines 收集
    ///   5. 从主输出节点反向 DFS，拓扑排序所有可达节点
    ///   6. 逐节点生成 HLSL 表达式(内联或临时变量)
    ///   7. 组装公共头(cbuffer/结构体/PBR 函数)
    ///   8. 生成 VS / PS / CS / MS 源码
    ///   9. 写入缓存并返回结果
    /// </summary>
    public sealed class ShaderGraphCompiler
    {
        private readonly ShaderCache _cache;
        private readonly Dictionary<string, MaterialFunction> _functions = new();

        public ShaderCache Cache => _cache;

        public ShaderGraphCompiler(ShaderCache? cache = null)
        {
            _cache = cache ?? new ShaderCache();
        }

        /// <summary>注册材质函数(供 FunctionCall 节点内联展开)。</summary>
        public void RegisterFunction(MaterialFunction function) => _functions[function.Name] = function;

        // ════════════════════════════════════════════════════════════
        // 主编译入口
        // ════════════════════════════════════════════════════════════

        /// <summary>编译材质图为着色器源码。</summary>
        public ShaderCompileResult Compile(MaterialGraph graph, ShaderCompileOptions options)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var result = new ShaderCompileResult();

            // 步骤 1: 校验
            var errors = graph.Validate();
            if (errors.Count > 0)
            {
                result.Success = false;
                result.ErrorMessage = "Material graph validation failed: " + string.Join("; ", errors);
                result.ElapsedMs = sw.ElapsedMilliseconds;
                return result;
            }

            // 步骤 2: 缓存查询
            var contentHash = _cache.ComputeHash(graph, options);
            var variantKey = new ShaderVariantKey(options.Keywords);
            result.ContentHash = contentHash;

            if (options.UseCache && _cache.TryGet(contentHash, variantKey.Hash, out var cached) && cached != null)
            {
                result.Success = true;
                result.VertexShaderSource = cached.VertexSource;
                result.PixelShaderSource = cached.PixelSource;
                result.ComputeShaderSource = string.IsNullOrEmpty(cached.ComputeSource) ? null : cached.ComputeSource;
                result.MeshShaderSource = string.IsNullOrEmpty(cached.MeshSource) ? null : cached.MeshSource;
                result.CacheHit = true;
                result.ElapsedMs = sw.ElapsedMilliseconds;
                return result;
            }

            // 步骤 3: UV 集合扫描
            var uvManager = new UvSetManager();
            uvManager.ScanGraph(graph);

            // 步骤 4: Includes / Defines 收集
            var includeDefineMgr = new ShaderIncludeDefineManager(graph, options);
            includeDefineMgr.Collect();

            // 步骤 5: 拓扑排序
            var masterNode = graph.GetMasterNode()!;
            List<MaterialNode> sortedNodes;
            try
            {
                sortedNodes = TopologicalSort(graph, masterNode);
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = "Topological sort failed: " + ex.Message;
                result.ElapsedMs = sw.ElapsedMilliseconds;
                return result;
            }

            // 步骤 6: 生成节点求值代码
            var nodeCode = new StringBuilder();
            var context = new NodeCodeGenContext(graph, options, _functions);
            foreach (var node in sortedNodes)
            {
                if (!node.Enabled) continue;
                GenerateNodeCode(node, context, nodeCode);
            }

            // 步骤 7 & 8: 组装完整着色器源码
            var commonHeader = GenerateCommonHeader(graph, options, uvManager, includeDefineMgr);

            string? vsSource = null, psSource = null, csSource = null, msSource = null;
            if (options.GenerateVertexShader)
                vsSource = GenerateVertexShader(graph, options, commonHeader, context, uvManager);
            if (options.GeneratePixelShader)
                psSource = GeneratePixelShader(graph, options, commonHeader, context, nodeCode, uvManager);
            if (options.GenerateComputeShader)
                csSource = GenerateComputeShader(graph, options, commonHeader, context, uvManager);
            if (options.GenerateMeshShader)
                msSource = GenerateMeshShader(graph, options, commonHeader, context);

            // 步骤 9: 缓存写入
            result.Success = true;
            result.VertexShaderSource = vsSource;
            result.PixelShaderSource = psSource;
            result.ComputeShaderSource = csSource;
            result.MeshShaderSource = msSource;
            result.ElapsedMs = sw.ElapsedMilliseconds;

            if (options.UseCache)
            {
                _cache.Put(new ShaderCacheEntry
                {
                    Hash = contentHash,
                    MaterialGuid = graph.Guid,
                    VariantHash = variantKey.Hash,
                    VertexSource = vsSource ?? string.Empty,
                    PixelSource = psSource ?? string.Empty,
                    ComputeSource = csSource ?? string.Empty,
                    MeshSource = msSource ?? string.Empty,
                    CompiledAt = DateTime.Now,
                    SourceModified = DateTime.Now
                });
            }

            return result;
        }

        // ════════════════════════════════════════════════════════════
        // 拓扑排序
        // ════════════════════════════════════════════════════════════

        private List<MaterialNode> TopologicalSort(MaterialGraph graph, MaterialNode masterNode)
        {
            var visited = new HashSet<string>();
            var visiting = new HashSet<string>();
            var stack = new List<MaterialNode>();

            void Visit(MaterialNode node)
            {
                if (visited.Contains(node.Id)) return;
                if (visiting.Contains(node.Id))
                    throw new InvalidOperationException($"Cycle detected at node: {node.Name} ({node.Id})");
                visiting.Add(node.Id);

                foreach (var pin in node.InputPins)
                {
                    var conn = graph.GetIncomingConnection(node.Id, pin.Id);
                    if (conn == null) continue;
                    var srcNode = graph.Nodes.FirstOrDefault(n => n.Id == conn.FromNodeId);
                    if (srcNode != null) Visit(srcNode);
                }

                visiting.Remove(node.Id);
                visited.Add(node.Id);
                stack.Add(node);
            }

            Visit(masterNode);
            return stack;
        }

        // ════════════════════════════════════════════════════════════
        // 节点代码生成上下文
        // ════════════════════════════════════════════════════════════

        /// <summary>
        /// 节点代码生成上下文。维护引脚 -> HLSL 变量名映射、纹理寄存器分配、参数声明。
        /// </summary>
        private sealed class NodeCodeGenContext
        {
            public MaterialGraph Graph { get; }
            public ShaderCompileOptions Options { get; }
            public Dictionary<string, MaterialFunction> Functions { get; }

            /// <summary>引脚 Id -> 生成的 HLSL 变量名。</summary>
            public Dictionary<string, string> PinVarNames { get; } = new();

            /// <summary>下一个纹理寄存器索引。</summary>
            public int NextTextureRegister { get; set; } = 0;

            /// <summary>下一个 Sampler 寄存器索引。</summary>
            public int NextSamplerRegister { get; set; } = 1; // s0 已被 LinearWrapSampler 占用

            /// <summary>收集到的纹理绑定。</summary>
            public List<TextureBinding> TextureBindings { get; } = new();

            /// <summary>收集到的材质参数。</summary>
            public List<MaterialParameter> Parameters { get; } = new();

            public NodeCodeGenContext(MaterialGraph graph, ShaderCompileOptions options,
                                      Dictionary<string, MaterialFunction> functions)
            {
                Graph = graph;
                Options = options;
                Functions = functions;
            }

            /// <summary>为节点输出引脚分配唯一变量名。</summary>
            public string AllocVarName(MaterialNode node, Pin pin)
            {
                var key = pin.Id;
                if (PinVarNames.TryGetValue(key, out var name)) return name;
                var idx = node.OutputPins.IndexOf(pin);
                name = $"n{SanitizeId(node.Id)}_out{idx}";
                PinVarNames[key] = name;
                return name;
            }

            /// <summary>获取输入引脚的表达式。未连接则返回默认值。</summary>
            public string GetInputExpr(MaterialNode node, string pinName)
            {
                var pin = node.GetInput(pinName);
                if (pin == null) return GetDefaultLiteralForType(PinDataType.Float);
                var conn = Graph.GetIncomingConnection(node.Id, pin.Id);
                if (conn != null)
                {
                    var srcNode = Graph.Nodes.FirstOrDefault(n => n.Id == conn.FromNodeId);
                    if (srcNode != null)
                    {
                        var srcPin = srcNode.OutputPins.FirstOrDefault(p => p.Id == conn.FromPinId);
                        if (srcPin != null)
                        {
                            var varName = AllocVarName(srcNode, srcPin);
                            return AdaptType(varName, srcPin.DataType, pin.DataType);
                        }
                    }
                }
                return pin.DefaultLiteral;
            }

            /// <summary>类型适配。</summary>
            private static string AdaptType(string expr, PinDataType from, PinDataType to)
            {
                if (from == to) return expr;
                if (to == PinDataType.Texture2D || to == PinDataType.CubeMap) return expr;
                int fromDim = MaterialGraph.DimensionOf(from);
                int toDim = MaterialGraph.DimensionOf(to);
                if (fromDim == toDim) return expr;

                if (fromDim == 1 && toDim > 1)
                {
                    return toDim switch
                    {
                        2 => $"float2(({expr}), ({expr}))",
                        3 => $"float3(({expr}), ({expr}), ({expr}))",
                        4 => $"float4(({expr}), ({expr}), ({expr}), ({expr}))",
                        _ => expr
                    };
                }
                if (fromDim > toDim && toDim > 0)
                {
                    var swizzle = toDim switch
                    {
                        1 => ".x",
                        2 => ".xy",
                        3 => ".xyz",
                        _ => ""
                    };
                    return $"({expr}){swizzle}";
                }
                return toDim switch
                {
                    2 => $"float2(({expr}), 0)",
                    3 => $"float3(({expr}), 0, 0)",
                    4 => $"float4(({expr}), 0, 0, 0)",
                    _ => expr
                };
            }

            private static string GetDefaultLiteralForType(PinDataType type)
            {
                return type switch
                {
                    PinDataType.Float => "0",
                    PinDataType.Float2 => "float2(0, 0)",
                    PinDataType.Float3 => "float3(0, 0, 0)",
                    PinDataType.Float4 => "float4(0, 0, 0, 1)",
                    PinDataType.Bool => "false",
                    _ => "0"
                };
            }

            private static string SanitizeId(string id)
            {
                return id.Length >= 8 ? id.Substring(0, 8) : id;
            }
        }

        /// <summary>纹理绑定描述。</summary>
        private sealed class TextureBinding
        {
            public string Register { get; set; } = string.Empty;
            public string SamplerRegister { get; set; } = string.Empty;
            public string ParameterName { get; set; } = string.Empty;
            public string TexturePath { get; set; } = string.Empty;
            public PinDataType SampleType { get; set; }
            public bool IsCubeMap { get; set; }
        }

        // ════════════════════════════════════════════════════════════
        // 逐节点代码生成
        // ════════════════════════════════════════════════════════════

        /// <summary>为单个节点生成 HLSL 代码。</summary>
        private void GenerateNodeCode(MaterialNode node, NodeCodeGenContext ctx, StringBuilder sb)
        {
            if (ctx.Options.EmitDebugComments)
                sb.AppendLine($"    // --- Node: {node.Name} ({node.Type}) ---");

            switch (node.Type)
            {
                // ── 常量节点 ──
                case MaterialNodeType.Constant:
                    {
                        var v = node.GetOutput("Value")!;
                        var varName = ctx.AllocVarName(node, v);
                        sb.AppendLine($"    float {varName} = {node.FloatValue:G9};");
                        break;
                    }

                case MaterialNodeType.ConstantColor:
                    {
                        var v = node.GetOutput("Color")!;
                        var varName = ctx.AllocVarName(node, v);
                        sb.AppendLine($"    float4 {varName} = float4({node.VectorValue.X:G9}, {node.VectorValue.Y:G9}, {node.VectorValue.Z:G9}, {node.VectorValue.W:G9});");
                        var rgb = node.GetOutput("RGB");
                        if (rgb != null)
                        {
                            var rgbName = ctx.AllocVarName(node, rgb);
                            sb.AppendLine($"    float3 {rgbName} = {varName}.rgb;");
                        }
                        break;
                    }

                case MaterialNodeType.ConstantBool:
                    {
                        var v = node.GetOutput("Value")!;
                        var varName = ctx.AllocVarName(node, v);
                        sb.AppendLine($"    bool {varName} = {(node.BoolValue ? "true" : "false")};");
                        break;
                    }

                // ── 数学节点 ──
                case MaterialNodeType.Add:
                    {
                        var a = ctx.GetInputExpr(node, "A");
                        var b = ctx.GetInputExpr(node, "B");
                        var v = node.GetOutput("Result")!;
                        var varName = ctx.AllocVarName(node, v);
                        sb.AppendLine($"    float {varName} = ({a}) + ({b});");
                        break;
                    }

                case MaterialNodeType.Subtract:
                    {
                        var a = ctx.GetInputExpr(node, "A");
                        var b = ctx.GetInputExpr(node, "B");
                        var v = node.GetOutput("Result")!;
                        var varName = ctx.AllocVarName(node, v);
                        sb.AppendLine($"    float {varName} = ({a}) - ({b});");
                        break;
                    }

                case MaterialNodeType.Multiply:
                    {
                        var a = ctx.GetInputExpr(node, "A");
                        var b = ctx.GetInputExpr(node, "B");
                        var v = node.GetOutput("Result")!;
                        var varName = ctx.AllocVarName(node, v);
                        sb.AppendLine($"    float {varName} = ({a}) * ({b});");
                        break;
                    }

                case MaterialNodeType.Divide:
                    {
                        var a = ctx.GetInputExpr(node, "A");
                        var b = ctx.GetInputExpr(node, "B");
                        var v = node.GetOutput("Result")!;
                        var varName = ctx.AllocVarName(node, v);
                        sb.AppendLine($"    float {varName} = ({a}) / max(({b}), 1e-6);");
                        break;
                    }

                case MaterialNodeType.Power:
                    {
                        var baseExpr = ctx.GetInputExpr(node, "Base");
                        var expExpr = ctx.GetInputExpr(node, "Exp");
                        var v = node.GetOutput("Result")!;
                        var varName = ctx.AllocVarName(node, v);
                        sb.AppendLine($"    float {varName} = pow(max(({baseExpr}), 0.0), ({expExpr}));");
                        break;
                    }

                case MaterialNodeType.Lerp:
                    {
                        var a = ctx.GetInputExpr(node, "A");
                        var b = ctx.GetInputExpr(node, "B");
                        var t = ctx.GetInputExpr(node, "T");
                        var v = node.GetOutput("Result")!;
                        var varName = ctx.AllocVarName(node, v);
                        sb.AppendLine($"    float {varName} = lerp(({a}), ({b}), saturate(({t})));");
                        break;
                    }

                case MaterialNodeType.Sine:
                    {
                        var x = ctx.GetInputExpr(node, "X");
                        var v = node.GetOutput("Result")!;
                        var varName = ctx.AllocVarName(node, v);
                        sb.AppendLine($"    float {varName} = sin(({x}));");
                        break;
                    }

                case MaterialNodeType.Cosine:
                    {
                        var x = ctx.GetInputExpr(node, "X");
                        var v = node.GetOutput("Result")!;
                        var varName = ctx.AllocVarName(node, v);
                        sb.AppendLine($"    float {varName} = cos(({x}));");
                        break;
                    }

                case MaterialNodeType.OneMinus:
                    {
                        var x = ctx.GetInputExpr(node, "X");
                        var v = node.GetOutput("Result")!;
                        var varName = ctx.AllocVarName(node, v);
                        sb.AppendLine($"    float {varName} = 1.0 - ({x});");
                        break;
                    }

                case MaterialNodeType.Saturate:
                    {
                        var x = ctx.GetInputExpr(node, "X");
                        var v = node.GetOutput("Result")!;
                        var varName = ctx.AllocVarName(node, v);
                        sb.AppendLine($"    float {varName} = saturate(({x}));");
                        break;
                    }

                case MaterialNodeType.Abs:
                    {
                        var x = ctx.GetInputExpr(node, "X");
                        var v = node.GetOutput("Result")!;
                        var varName = ctx.AllocVarName(node, v);
                        sb.AppendLine($"    float {varName} = abs(({x}));");
                        break;
                    }

                case MaterialNodeType.Floor:
                    {
                        var x = ctx.GetInputExpr(node, "X");
                        var v = node.GetOutput("Result")!;
                        var varName = ctx.AllocVarName(node, v);
                        sb.AppendLine($"    float {varName} = floor(({x}));");
                        break;
                    }

                case MaterialNodeType.Ceil:
                    {
                        var x = ctx.GetInputExpr(node, "X");
                        var v = node.GetOutput("Result")!;
                        var varName = ctx.AllocVarName(node, v);
                        sb.AppendLine($"    float {varName} = ceil(({x}));");
                        break;
                    }

                case MaterialNodeType.Frac:
                    {
                        var x = ctx.GetInputExpr(node, "X");
                        var v = node.GetOutput("Result")!;
                        var varName = ctx.AllocVarName(node, v);
                        sb.AppendLine($"    float {varName} = frac(({x}));");
                        break;
                    }

                case MaterialNodeType.Clamp:
                    {
                        var x = ctx.GetInputExpr(node, "X");
                        var minE = ctx.GetInputExpr(node, "Min");
                        var maxE = ctx.GetInputExpr(node, "Max");
                        var v = node.GetOutput("Result")!;
                        var varName = ctx.AllocVarName(node, v);
                        sb.AppendLine($"    float {varName} = clamp(({x}), ({minE}), ({maxE}));");
                        break;
                    }

                case MaterialNodeType.Dot:
                    {
                        var a = ctx.GetInputExpr(node, "A");
                        var b = ctx.GetInputExpr(node, "B");
                        var v = node.GetOutput("Result")!;
                        var varName = ctx.AllocVarName(node, v);
                        sb.AppendLine($"    float {varName} = dot(({a}), ({b}));");
                        break;
                    }

                case MaterialNodeType.Cross:
                    {
                        var a = ctx.GetInputExpr(node, "A");
                        var b = ctx.GetInputExpr(node, "B");
                        var v = node.GetOutput("Result")!;
                        var varName = ctx.AllocVarName(node, v);
                        sb.AppendLine($"    float3 {varName} = cross(({a}), ({b}));");
                        break;
                    }

                case MaterialNodeType.Normalize:
                    {
                        var x = ctx.GetInputExpr(node, "X");
                        var v = node.GetOutput("Result")!;
                        var varName = ctx.AllocVarName(node, v);
                        sb.AppendLine($"    float3 {varName} = normalize(({x}));");
                        break;
                    }

                case MaterialNodeType.Length:
                    {
                        var x = ctx.GetInputExpr(node, "X");
                        var v = node.GetOutput("Result")!;
                        var varName = ctx.AllocVarName(node, v);
                        sb.AppendLine($"    float {varName} = length(({x}));");
                        break;
                    }

                case MaterialNodeType.Reflect:
                    {
                        var inc = ctx.GetInputExpr(node, "Incident");
                        var n = ctx.GetInputExpr(node, "Normal");
                        var v = node.GetOutput("Result")!;
                        var varName = ctx.AllocVarName(node, v);
                        sb.AppendLine($"    float3 {varName} = reflect(({inc}), ({n}));");
                        break;
                    }

                case MaterialNodeType.VectorMath:
                    GenerateVectorMathCode(node, ctx, sb);
                    break;

                // ── 纹理节点 ──
                case MaterialNodeType.TextureSample:
                    GenerateTextureSampleCode(node, ctx, sb, false);
                    break;

                case MaterialNodeType.NormalMap:
                    GenerateTextureSampleCode(node, ctx, sb, true);
                    break;

                case MaterialNodeType.CubeMapSample:
                    GenerateCubeMapSampleCode(node, ctx, sb);
                    break;

                // ── 坐标节点 ──
                case MaterialNodeType.TexCoord:
                    {
                        var channel = node.UVChannel;
                        var v = node.GetOutput("UV")!;
                        var varName = ctx.AllocVarName(node, v);
                        sb.AppendLine($"    float2 {varName} = input.TexCoord{channel}; // UV channel {channel}");
                        break;
                    }

                case MaterialNodeType.PixelPosition:
                    {
                        var v = node.GetOutput("XY")!;
                        var varName = ctx.AllocVarName(node, v);
                        sb.AppendLine($"    float2 {varName} = input.Position.xy;");
                        break;
                    }

                case MaterialNodeType.WorldPosition:
                    {
                        var v = node.GetOutput("Position")!;
                        var varName = ctx.AllocVarName(node, v);
                        sb.AppendLine($"    float3 {varName} = input.WorldPos;");
                        break;
                    }

                case MaterialNodeType.ViewDirection:
                    {
                        var v = node.GetOutput("Direction")!;
                        var varName = ctx.AllocVarName(node, v);
                        sb.AppendLine($"    float3 {varName} = normalize(CameraPosition - input.WorldPos);");
                        break;
                    }

                // ── 工具节点 ──
                case MaterialNodeType.Mask:
                    {
                        var inputExpr = ctx.GetInputExpr(node, "Input");
                        var maskStr = node.Properties.GetValueOrDefault("Mask", "RGBA");
                        var v = node.GetOutput("Result")!;
                        var varName = ctx.AllocVarName(node, v);
                        var swizzle = maskStr.ToLowerInvariant() switch
                        {
                            "r" => ".r", "g" => ".g", "b" => ".b", "a" => ".a",
                            "rg" => ".rg", "rgb" => ".rgb", "rgba" => ".rgba",
                            _ => ".rgba"
                        };
                        sb.AppendLine($"    float4 {varName} = ({inputExpr}){swizzle};");
                        break;
                    }

                case MaterialNodeType.Append:
                    {
                        var x = ctx.GetInputExpr(node, "X");
                        var y = ctx.GetInputExpr(node, "Y");
                        var z = ctx.GetInputExpr(node, "Z");
                        var w = ctx.GetInputExpr(node, "W");
                        var v = node.GetOutput("Result")!;
                        var varName = ctx.AllocVarName(node, v);
                        sb.AppendLine($"    float4 {varName} = float4(({x}), ({y}), ({z}), ({w}));");
                        break;
                    }

                case MaterialNodeType.ComponentMask:
                    {
                        var inputExpr = ctx.GetInputExpr(node, "Input");
                        var maskStr = node.Properties.GetValueOrDefault("Mask", "RGB");
                        var v = node.GetOutput("Result")!;
                        var varName = ctx.AllocVarName(node, v);
                        var (typeStr, swizzle) = maskStr.ToLowerInvariant() switch
                        {
                            "r" or "g" or "b" or "a" => ("float", maskStr.ToLowerInvariant()),
                            "rg" => ("float2", ".rg"),
                            "rgb" => ("float3", ".rgb"),
                            "rgba" => ("float4", ".rgba"),
                            _ => ("float3", ".rgb")
                        };
                        sb.AppendLine($"    {typeStr} {varName} = ({inputExpr}){swizzle};");
                        break;
                    }

                // ── PBR 主节点 (终端，不生成代码) ──
                case MaterialNodeType.PbrMaster:
                    break;

                // ── 时间节点 ──
                case MaterialNodeType.Time:
                    {
                        var v = node.GetOutput("Time")!;
                        var varName = ctx.AllocVarName(node, v);
                        sb.AppendLine($"    float {varName} = EngineTime;");
                        break;
                    }

                case MaterialNodeType.SineWave:
                    {
                        var freq = ctx.GetInputExpr(node, "Frequency");
                        var phase = ctx.GetInputExpr(node, "Phase");
                        var v = node.GetOutput("Result")!;
                        var varName = ctx.AllocVarName(node, v);
                        sb.AppendLine($"    float {varName} = sin(EngineTime * ({freq}) + ({phase}));");
                        break;
                    }

                default:
                    if (ctx.Options.EmitDebugComments)
                        sb.AppendLine($"    // Unimplemented node type: {node.Type}");
                    break;
            }
        }

        // ── 向量数学 ──

        private void GenerateVectorMathCode(MaterialNode node, NodeCodeGenContext ctx, StringBuilder sb)
        {
            var a = ctx.GetInputExpr(node, "A");
            var b = ctx.GetInputExpr(node, "B");
            var v = node.GetOutput("Result")!;
            var varName = ctx.AllocVarName(node, v);
            var op = node.Properties.GetValueOrDefault("Op", "Add");

            var hlslOp = op.ToLowerInvariant() switch
            {
                "sub" or "subtract" => "-",
                "mul" or "multiply" => "*",
                "div" or "divide" => "/ max(({b}), float4(1e-6, 1e-6, 1e-6, 1e-6))",
                _ => "+"
            };

            if (op.ToLowerInvariant() == "div" || op.ToLowerInvariant() == "divide")
                sb.AppendLine($"    float4 {varName} = ({a}) / max(({b}), float4(1e-6, 1e-6, 1e-6, 1e-6));");
            else
                sb.AppendLine($"    float4 {varName} = ({a}) {hlslOp} ({b});");
        }

        // ── 纹理采样 ──

        private void GenerateTextureSampleCode(MaterialNode node, NodeCodeGenContext ctx, StringBuilder sb, bool isNormalMap)
        {
            var reg = $"t{ctx.NextTextureRegister++}";
            var texPath = node.Properties.GetValueOrDefault("TexturePath", node.TexturePath);
            var paramName = node.Properties.GetValueOrDefault("ParamName", SanitizeName(node.Name));
            ctx.TextureBindings.Add(new TextureBinding
            {
                Register = reg,
                ParameterName = paramName,
                TexturePath = texPath,
                SampleType = PinDataType.Texture2D,
                IsCubeMap = false
            });

            var uvExpr = ctx.GetInputExpr(node, "UV");
            if (string.IsNullOrWhiteSpace(uvExpr) || uvExpr == "0" || uvExpr == "float2(0, 0)")
                uvExpr = "input.TexCoord0";

            var sampleVar = $"tex_{ctx.TextureBindings.Count - 1}";
            var samplerName = $"MaterialSampler{ctx.NextSamplerRegister}";
            sb.AppendLine($"    float4 {sampleVar} = Tex_{paramName}.Sample({samplerName}, ({uvExpr}));");

            if (isNormalMap)
            {
                var normalPin = node.GetOutput("Normal")!;
                var varName = ctx.AllocVarName(node, normalPin);
                // NormalMap 节点输出已解码的切线空间法线
                var strength = node.Properties.GetValueOrDefault("Strength", "1.0");
                sb.AppendLine($"    float3 {varName} = {sampleVar}.rgb * 2.0 - 1.0;");
                sb.AppendLine($"    {varName} = normalize(float3({varName}.xy * {strength}, {varName}.z));");
                sb.AppendLine($"    {varName} = {varName} * 0.5 + 0.5; // encode back to [0,1]");
            }
            else
            {
                // TextureSample: 分通道输出
                var rgbaPin = node.GetOutput("RGBA");
                var rgbPin = node.GetOutput("RGB");
                var rPin = node.GetOutput("R");
                var gPin = node.GetOutput("G");
                var bPin = node.GetOutput("B");
                var aPin = node.GetOutput("A");

                if (rgbaPin != null)
                {
                    var name = ctx.AllocVarName(node, rgbaPin);
                    sb.AppendLine($"    float4 {name} = {sampleVar};");
                }
                if (rgbPin != null)
                {
                    var name = ctx.AllocVarName(node, rgbPin);
                    sb.AppendLine($"    float3 {name} = {sampleVar}.rgb;");
                }
                if (rPin != null)
                {
                    var name = ctx.AllocVarName(node, rPin);
                    sb.AppendLine($"    float {name} = {sampleVar}.r;");
                }
                if (gPin != null)
                {
                    var name = ctx.AllocVarName(node, gPin);
                    sb.AppendLine($"    float {name} = {sampleVar}.g;");
                }
                if (bPin != null)
                {
                    var name = ctx.AllocVarName(node, bPin);
                    sb.AppendLine($"    float {name} = {sampleVar}.b;");
                }
                if (aPin != null)
                {
                    var name = ctx.AllocVarName(node, aPin);
                    sb.AppendLine($"    float {name} = {sampleVar}.a;");
                }
            }
        }

        private void GenerateCubeMapSampleCode(MaterialNode node, NodeCodeGenContext ctx, StringBuilder sb)
        {
            var reg = $"t{ctx.NextTextureRegister++}";
            var cubePath = node.Properties.GetValueOrDefault("CubeMapPath", node.CubeMapPath);
            var paramName = node.Properties.GetValueOrDefault("ParamName", SanitizeName(node.Name));
            ctx.TextureBindings.Add(new TextureBinding
            {
                Register = reg,
                ParameterName = paramName,
                TexturePath = cubePath,
                SampleType = PinDataType.CubeMap,
                IsCubeMap = true
            });

            var dirExpr = ctx.GetInputExpr(node, "Direction");
            if (string.IsNullOrWhiteSpace(dirExpr) || dirExpr == "0" || dirExpr == "float3(0, 0, 0)")
                dirExpr = "float3(0, 1, 0)";

            var sampleVar = $"cube_{ctx.TextureBindings.Count - 1}";
            sb.AppendLine($"    float4 {sampleVar} = CubeTex_{paramName}.Sample(LinearClampSampler, ({dirExpr}));");

            var colorPin = node.GetOutput("Color")!;
            var varName = ctx.AllocVarName(node, colorPin);
            sb.AppendLine($"    float4 {varName} = {sampleVar};");
        }

        private static string SanitizeName(string name)
        {
            var sb = new StringBuilder();
            foreach (var c in name)
            {
                if (char.IsLetterOrDigit(c) || c == '_')
                    sb.Append(c);
                else
                    sb.Append('_');
            }
            return sb.ToString();
        }

        // ════════════════════════════════════════════════════════════
        // 公共头生成
        // ════════════════════════════════════════════════════════════

        private string GenerateCommonHeader(MaterialGraph graph, ShaderCompileOptions options,
                                            UvSetManager uvMgr, ShaderIncludeDefineManager includeDefineMgr)
        {
            var sb = new StringBuilder();

            // 文件头
            sb.AppendLine("// ============================================================");
            sb.AppendLine("//  Shanyu Engine - Auto-Generated Shader");
            sb.AppendLine($"//  Material: {graph.Name}  GUID: {graph.Guid}");
            sb.AppendLine($"//  BlendMode: {graph.BlendMode}  ShadingModel: {graph.ShadingModel}");
            sb.AppendLine($"//  Keywords: {string.Join(", ", options.Keywords.OrderBy(k => k))}");
            sb.AppendLine("// ============================================================");
            sb.AppendLine();

            // Includes
            var includeBlock = includeDefineMgr.GenerateIncludeBlock();
            sb.Append(includeBlock);

            // Defines
            var defineBlock = includeDefineMgr.GenerateDefineBlock();
            sb.Append(defineBlock);

            // 数学常量
            sb.AppendLine("#define PI        3.14159265358979323846");
            sb.AppendLine("#define INV_PI    0.31830988618379067154");
            sb.AppendLine("#define HALF_PI  1.57079632679489661923");
            sb.AppendLine("#define EPSILON   1e-5");
            sb.AppendLine();

            // 逐绘制常量缓冲 (b0)
            sb.AppendLine("cbuffer PBRConstants : register(b0)");
            sb.AppendLine("{");
            sb.AppendLine("    matrix  WorldViewProj;");
            sb.AppendLine("    matrix  World;");
            sb.AppendLine("    float3  LightDirection;");
            sb.AppendLine("    float   LightPad0;");
            sb.AppendLine("    float3  LightColor;");
            sb.AppendLine("    float   LightPad1;");
            sb.AppendLine("    float3  CameraPosition;");
            sb.AppendLine("    float   CameraPad;");
            sb.AppendLine("    float3  BaseColor;");
            sb.AppendLine("    float   Metallic;");
            sb.AppendLine("    float   Roughness;");
            sb.AppendLine("    float   Alpha;");
            sb.AppendLine("    float   EngineTime;");
            sb.AppendLine("    float   DeltaTime;");
            sb.AppendLine("    float   FrameCount;");
            sb.AppendLine("    float   OpacityMaskClipValue;");
            sb.AppendLine("};");
            sb.AppendLine();

            // 材质参数常量缓冲 (b1)
            graph.CollectParameters();
            if (graph.Parameters.Count > 0)
            {
                sb.AppendLine("cbuffer MaterialParams : register(b1)");
                sb.AppendLine("{");
                foreach (var p in graph.Parameters)
                {
                    sb.AppendLine($"    {p.HlslType} {p.Name};");
                }
                sb.AppendLine("};");
                sb.AppendLine();
            }

            // 采样器
            sb.AppendLine("SamplerState LinearWrapSampler : register(s0);");
            sb.AppendLine("SamplerState LinearClampSampler : register(s1);");
            sb.AppendLine();

            // 顶点输入结构
            sb.AppendLine("struct VertexInput");
            sb.AppendLine("{");
            sb.AppendLine("    float3 Position : POSITION;");
            sb.AppendLine("    float3 Normal   : NORMAL;");
            sb.AppendLine("    float3 Tangent  : TANGENT;");
            // UV 声明
            var uvDecls = uvMgr.GenerateVertexInputDeclarations();
            if (string.IsNullOrEmpty(uvDecls))
                sb.AppendLine("    float2 TexCoord : TEXCOORD0;");
            else
                sb.Append(uvDecls);
            sb.AppendLine("};");
            sb.AppendLine();

            // 像素输入结构
            int texCoordOffset = 1;
            sb.AppendLine("struct PixelInput");
            sb.AppendLine("{");
            sb.AppendLine("    float4 Position : SV_POSITION;");
            sb.AppendLine("    float3 Normal   : TEXCOORD0;");
            sb.AppendLine("    float3 WorldPos : TEXCOORD1;");
            // UV 声明 (从 TEXCOORD2 开始)
            var pixelUvDecls = uvMgr.GeneratePixelInputDeclarations(2);
            if (string.IsNullOrEmpty(pixelUvDecls))
                sb.AppendLine("    float2 TexCoord : TEXCOORD2;");
            else
                sb.Append(pixelUvDecls);
            sb.AppendLine("};");
            sb.AppendLine();

            // PBR 辅助函数
            sb.AppendLine("// --- PBR Helper Functions ---");
            sb.AppendLine("float DistributionGGX(float3 N, float3 H, float roughness)");
            sb.AppendLine("{");
            sb.AppendLine("    float a  = roughness * roughness;");
            sb.AppendLine("    float a2 = a * a;");
            sb.AppendLine("    float NdotH = max(dot(N, H), 0.0);");
            sb.AppendLine("    float denom = NdotH * NdotH * (a2 - 1.0) + 1.0;");
            sb.AppendLine("    return a2 / max(PI * denom * denom, EPSILON);");
            sb.AppendLine("}");
            sb.AppendLine();
            sb.AppendLine("float GeometrySchlickGGX(float NdotV, float roughness)");
            sb.AppendLine("{");
            sb.AppendLine("    float r = roughness + 1.0;");
            sb.AppendLine("    float k = (r * r) / 8.0;");
            sb.AppendLine("    return NdotV / max(NdotV * (1.0 - k) + k, EPSILON);");
            sb.AppendLine("}");
            sb.AppendLine();
            sb.AppendLine("float GeometrySmith(float3 N, float3 V, float3 L, float roughness)");
            sb.AppendLine("{");
            sb.AppendLine("    float NdotV = max(dot(N, V), 0.0);");
            sb.AppendLine("    float NdotL = max(dot(N, L), 0.0);");
            sb.AppendLine("    return GeometrySchlickGGX(NdotV, roughness) * GeometrySchlickGGX(NdotL, roughness);");
            sb.AppendLine("}");
            sb.AppendLine();
            sb.AppendLine("float3 FresnelSchlick(float cosTheta, float3 F0)");
            sb.AppendLine("{");
            sb.AppendLine("    float f0 = 1.0 - clamp(cosTheta, 0.0, 1.0);");
            sb.AppendLine("    return F0 + (1.0 - F0) * (f0 * f0 * f0 * f0 * f0);");
            sb.AppendLine("}");
            sb.AppendLine();

            return sb.ToString();
        }

        // ════════════════════════════════════════════════════════════
        // 顶点着色器生成
        // ════════════════════════════════════════════════════════════

        private string GenerateVertexShader(MaterialGraph graph, ShaderCompileOptions options,
                                            string commonHeader, NodeCodeGenContext ctx, UvSetManager uvMgr)
        {
            var sb = new StringBuilder();
            sb.Append(commonHeader);

            sb.AppendLine("// --- Vertex Shader ---");
            sb.AppendLine("PixelInput VSMain(VertexInput input)");
            sb.AppendLine("{");
            sb.AppendLine("    PixelInput output;");
            sb.AppendLine();

            // 世界位置偏移 (WPO)
            sb.AppendLine("    // World Position Offset (WPO) injection point");
            sb.AppendLine("    float3 worldPosOffset = float3(0, 0, 0);");

            var master = graph.GetMasterNode()!;
            var wpoPin = master.GetInput("WorldPositionOffset");
            if (wpoPin != null)
            {
                var conn = graph.GetIncomingConnection(master.Id, wpoPin.Id);
                if (conn != null)
                {
                    var wpoNodes = CollectDependentNodes(graph, master, wpoPin.Id);
                    foreach (var n in wpoNodes)
                    {
                        if (n.Enabled) GenerateNodeCode(n, ctx, sb);
                    }
                    var wpoExpr = ctx.GetInputExpr(master, "WorldPositionOffset");
                    sb.AppendLine($"    worldPosOffset = ({wpoExpr});");
                }
            }

            sb.AppendLine();
            sb.AppendLine("    // Apply world position offset");
            sb.AppendLine("    float3 worldPos = mul(float4(input.Position, 1.0), World).xyz + worldPosOffset;");
            sb.AppendLine("    output.Position = mul(float4(worldPos, 1.0), WorldViewProj);");
            sb.AppendLine("    output.WorldPos = worldPos;");
            sb.AppendLine("    output.Normal   = normalize(mul(input.Normal, (float3x3)World));");

            // UV 传递
            if (uvMgr.Count == 0)
                sb.AppendLine("    output.TexCoord = input.TexCoord;");
            else
            {
                var passthrough = uvMgr.GenerateVSUvPassthrough(2);
                sb.Append(passthrough);
            }

            sb.AppendLine("    return output;");
            sb.AppendLine("}");
            sb.AppendLine();

            return InjectTextureDeclarations(sb.ToString(), ctx);
        }

        // ════════════════════════════════════════════════════════════
        // 像素着色器生成
        // ════════════════════════════════════════════════════════════

        private string GeneratePixelShader(MaterialGraph graph, ShaderCompileOptions options,
                                           string commonHeader, NodeCodeGenContext ctx,
                                           StringBuilder nodeCode, UvSetManager uvMgr)
        {
            var sb = new StringBuilder();
            sb.Append(commonHeader);

            // 像素输出结构
            sb.AppendLine("struct PixelOutput");
            sb.AppendLine("{");
            sb.AppendLine("    float4 Color : SV_TARGET;");
            sb.AppendLine("};");
            sb.AppendLine();

            sb.AppendLine("// --- Pixel Shader ---");
            sb.AppendLine("PixelOutput PSMain(PixelInput input)");
            sb.AppendLine("{");
            sb.AppendLine("    PixelOutput output;");
            sb.AppendLine();

            // 节点求值代码
            sb.AppendLine("    // === Material Node Evaluation ===");
            sb.Append(nodeCode.ToString());
            sb.AppendLine();

            // 读取主输出节点的各属性
            var master = graph.GetMasterNode()!;
            sb.AppendLine("    // === Material Property Read ===");
            var baseColorExpr = ctx.GetInputExpr(master, "BaseColor");
            var normalExpr = ctx.GetInputExpr(master, "Normal");
            var roughnessExpr = ctx.GetInputExpr(master, "Roughness");
            var metallicExpr = ctx.GetInputExpr(master, "Metallic");
            var emissiveExpr = ctx.GetInputExpr(master, "Emissive");
            var opacityExpr = ctx.GetInputExpr(master, "Opacity");
            var aoExpr = ctx.GetInputExpr(master, "AO");

            sb.AppendLine($"    float3 matBaseColor = ({baseColorExpr});");
            sb.AppendLine($"    float3 matNormal    = ({normalExpr});");
            sb.AppendLine($"    float  matRoughness  = saturate(({roughnessExpr}));");
            sb.AppendLine($"    float  matMetallic   = saturate(({metallicExpr}));");
            sb.AppendLine($"    float3 matEmissive   = ({emissiveExpr});");
            sb.AppendLine($"    float  matOpacity    = ({opacityExpr});");
            sb.AppendLine($"    float  matAO         = ({aoExpr});");
            sb.AppendLine();

            // 混合模式处理
            if (graph.BlendMode == MaterialBlendMode.Masked)
            {
                sb.AppendLine($"    // Masked: Alpha clip (threshold={graph.OpacityMaskClipValue:G9})");
                sb.AppendLine($"    clip(matOpacity - OpacityMaskClipValue);");
                sb.AppendLine();
            }

            // 法线解码 (切线空间 -> 世界空间)
            sb.AppendLine("    // Normal decode: tangent space -> world space");
            sb.AppendLine("    float3 N = normalize(input.Normal);");
            sb.AppendLine("    float3 tangentNormal = matNormal * 2.0 - 1.0;");
            sb.AppendLine("    float3 dp1 = ddx(input.WorldPos);");
            sb.AppendLine("    float3 dp2 = ddy(input.WorldPos);");
            sb.AppendLine("    float2 duv1 = ddx(input.TexCoord0);");
            sb.AppendLine("    float2 duv2 = ddy(input.TexCoord0);");
            sb.AppendLine("    float3 dp2perp = cross(N, dp2);");
            sb.AppendLine("    float3 dp1perp = cross(dp1, N);");
            sb.AppendLine("    float3 T = dp2perp * duv1.x + dp1perp * duv2.x;");
            sb.AppendLine("    float3 B = dp2perp * duv1.y + dp1perp * duv2.y;");
            sb.AppendLine("    float invmax = rsqrt(max(dot(T, T), dot(B, B)));");
            sb.AppendLine("    T *= invmax; B *= invmax;");
            sb.AppendLine("    float3x3 TBN = float3x3(T, B, N);");
            sb.AppendLine("    N = normalize(mul(tangentNormal, TBN));");
            sb.AppendLine();

            // 着色模型调度
            sb.AppendLine("    // === Shading Model Dispatch ===");
            sb.AppendLine("    float3 finalColor = float3(0, 0, 0);");

            switch (graph.ShadingModel)
            {
                case MaterialShadingModel.Unlit:
                    sb.AppendLine("    finalColor = matEmissive;");
                    break;
                case MaterialShadingModel.DefaultLit:
                    GenerateDefaultLitShading(sb);
                    break;
                case MaterialShadingModel.Subsurface:
                    GenerateSubsurfaceShading(sb, ctx, master);
                    break;
                case MaterialShadingModel.Hair:
                    GenerateHairShading(sb);
                    break;
                case MaterialShadingModel.Cloth:
                    GenerateClothShading(sb);
                    break;
                case MaterialShadingModel.Eye:
                    GenerateEyeShading(sb);
                    break;
            }

            sb.AppendLine();
            sb.AppendLine("    // Tone mapping + Gamma correction");
            sb.AppendLine("    finalColor = finalColor / (finalColor + 1.0);");
            sb.AppendLine("    finalColor = pow(max(finalColor, 0.0), 1.0 / 2.2);");
            sb.AppendLine();

            var alphaExpr = graph.BlendMode switch
            {
                MaterialBlendMode.Opaque => "1.0",
                MaterialBlendMode.Masked => "1.0",
                MaterialBlendMode.Translucent => "matOpacity",
                MaterialBlendMode.Additive => "matOpacity",
                _ => "1.0"
            };
            sb.AppendLine($"    output.Color = float4(finalColor, {alphaExpr});");
            sb.AppendLine("    return output;");
            sb.AppendLine("}");
            sb.AppendLine();

            return InjectTextureDeclarations(sb.ToString(), ctx);
        }

        // ── 着色模型实现 ──

        private void GenerateDefaultLitShading(StringBuilder sb)
        {
            sb.AppendLine("    // Default Lit: Cook-Torrance PBR");
            sb.AppendLine("    float3 V = normalize(CameraPosition - input.WorldPos);");
            sb.AppendLine("    float3 L = normalize(-LightDirection);");
            sb.AppendLine("    float3 H = normalize(V + L);");
            sb.AppendLine("    float3 albedo = matBaseColor;");
            sb.AppendLine("    float3 F0 = lerp(float3(0.04, 0.04, 0.04), albedo, matMetallic);");
            sb.AppendLine("    float NdotL = max(dot(N, L), 0.0);");
            sb.AppendLine("    float NdotV = max(dot(N, V), 0.0);");
            sb.AppendLine("    float  D = DistributionGGX(N, H, matRoughness);");
            sb.AppendLine("    float  G = GeometrySmith(N, V, L, matRoughness);");
            sb.AppendLine("    float3 F = FresnelSchlick(max(dot(H, V), 0.0), F0);");
            sb.AppendLine("    float3 specular = (D * G * F) / max(4.0 * NdotV * NdotL + EPSILON, EPSILON);");
            sb.AppendLine("    float3 kD = (1.0 - F) * (1.0 - matMetallic);");
            sb.AppendLine("    float3 diffuse = kD * albedo * INV_PI;");
            sb.AppendLine("    float3 radiance = LightColor * NdotL;");
            sb.AppendLine("    float3 Lo = (diffuse + specular) * radiance;");
            sb.AppendLine("    float3 ambient = 0.03 * albedo * matAO;");
            sb.AppendLine("    finalColor = ambient + Lo + matEmissive;");
        }

        private void GenerateSubsurfaceShading(StringBuilder sb, NodeCodeGenContext ctx, MaterialNode master)
        {
            var sssExpr = ctx.GetInputExpr(master, "SubsurfaceColor");
            sb.AppendLine("    // Subsurface: approx SSS");
            sb.AppendLine("    float3 V = normalize(CameraPosition - input.WorldPos);");
            sb.AppendLine("    float3 L = normalize(-LightDirection);");
            sb.AppendLine("    float3 H = normalize(V + L);");
            sb.AppendLine("    float3 albedo = matBaseColor;");
            sb.AppendLine("    float3 sssColor = (" + sssExpr + ");");
            sb.AppendLine("    float3 F0 = lerp(float3(0.04, 0.04, 0.04), albedo, matMetallic);");
            sb.AppendLine("    float NdotL = max(dot(N, L), 0.0);");
            sb.AppendLine("    float NdotV = max(dot(N, V), 0.0);");
            sb.AppendLine("    float D = DistributionGGX(N, H, matRoughness);");
            sb.AppendLine("    float G = GeometrySmith(N, V, L, matRoughness);");
            sb.AppendLine("    float3 F = FresnelSchlick(max(dot(H, V), 0.0), F0);");
            sb.AppendLine("    float3 specular = (D * G * F) / max(4.0 * NdotV * NdotL + EPSILON, EPSILON);");
            sb.AppendLine("    float3 kD = (1.0 - F) * (1.0 - matMetallic);");
            sb.AppendLine("    float3 diffuse = kD * albedo * INV_PI;");
            sb.AppendLine("    float3 backLight = pow(saturate(dot(-L, V)), 3.0) * sssColor * 0.5;");
            sb.AppendLine("    float3 radiance = LightColor * NdotL;");
            sb.AppendLine("    float3 Lo = (diffuse + specular) * radiance + backLight * radiance;");
            sb.AppendLine("    float3 ambient = 0.03 * albedo * matAO;");
            sb.AppendLine("    finalColor = ambient + Lo + matEmissive;");
        }

        private void GenerateHairShading(StringBuilder sb)
        {
            sb.AppendLine("    // Hair: Marschner dual specular approximation");
            sb.AppendLine("    float3 V = normalize(CameraPosition - input.WorldPos);");
            sb.AppendLine("    float3 L = normalize(-LightDirection);");
            sb.AppendLine("    float3 albedo = matBaseColor;");
            sb.AppendLine("    float NdotL = max(dot(N, L), 0.0);");
            sb.AppendLine("    float NdotV = max(dot(N, V), 0.0);");
            sb.AppendLine("    float3 spec1 = pow(max(NdotL, 0.0), 8.0) * LightColor * 0.3;");
            sb.AppendLine("    float3 spec2 = pow(max(dot(N, normalize(L + float3(0.1, 0, 0))), 0.0), 16.0) * LightColor * 0.2;");
            sb.AppendLine("    float3 diffuse = albedo * INV_PI * NdotL * 0.5;");
            sb.AppendLine("    finalColor = (diffuse + spec1 + spec2) * LightColor + 0.03 * albedo * matAO + matEmissive;");
        }

        private void GenerateClothShading(StringBuilder sb)
        {
            sb.AppendLine("    // Cloth: Charlie sheen + subsurface approximation");
            sb.AppendLine("    float3 V = normalize(CameraPosition - input.WorldPos);");
            sb.AppendLine("    float3 L = normalize(-LightDirection);");
            sb.AppendLine("    float3 albedo = matBaseColor;");
            sb.AppendLine("    float NdotL = max(dot(N, L), 0.0);");
            sb.AppendLine("    float NdotV = max(dot(N, V), 0.0);");
            sb.AppendLine("    float sheen = pow(max(1.0 - NdotV, 0.0), 4.0) * (1.0 - matRoughness);");
            sb.AppendLine("    float3 sheenColor = sheen * albedo * 0.5;");
            sb.AppendLine("    float3 diffuse = albedo * INV_PI * NdotL * 0.8;");
            sb.AppendLine("    finalColor = (diffuse + sheenColor) * LightColor + 0.03 * albedo * matAO + matEmissive;");
        }

        private void GenerateEyeShading(StringBuilder sb)
        {
            sb.AppendLine("    // Eye: cornea/iris layered refraction approximation");
            sb.AppendLine("    float3 V = normalize(CameraPosition - input.WorldPos);");
            sb.AppendLine("    float3 L = normalize(-LightDirection);");
            sb.AppendLine("    float3 albedo = matBaseColor;");
            sb.AppendLine("    float NdotL = max(dot(N, L), 0.0);");
            sb.AppendLine("    float NdotV = max(dot(N, V), 0.0);");
            sb.AppendLine("    float3 H = normalize(V + L);");
            sb.AppendLine("    float3 F0 = float3(0.04, 0.04, 0.04);");
            sb.AppendLine("    float3 F = FresnelSchlick(max(dot(H, V), 0.0), F0);");
            sb.AppendLine("    float spec = pow(max(dot(N, H), 0.0), 128.0);");
            sb.AppendLine("    float3 specular = spec * F * LightColor;");
            sb.AppendLine("    float3 diffuse = albedo * INV_PI * NdotL;");
            sb.AppendLine("    finalColor = (diffuse + specular) + 0.03 * albedo * matAO + matEmissive;");
        }

        // ════════════════════════════════════════════════════════════
        // 计算着色器生成
        // ════════════════════════════════════════════════════════════

        /// <summary>
        /// 生成计算着色器。用于材质属性的离线烘焙或后处理计算。
        /// 包含材质节点求值代码，按线程组大小配置 dispatch。
        /// </summary>
        private string GenerateComputeShader(MaterialGraph graph, ShaderCompileOptions options,
                                            string commonHeader, NodeCodeGenContext ctx, UvSetManager uvMgr)
        {
            var sb = new StringBuilder();
            sb.Append(commonHeader);

            // 计算着色器输入/输出缓冲区
            sb.AppendLine("// === Compute Shader ===");
            sb.AppendLine("RWTexture2D<float4> OutputTexture : register(u0);");
            sb.AppendLine("Texture2D InputTexture : register(t255);"); // reserved for input
            sb.AppendLine();

            // 线程组常量
            var tg = options.ComputeThreadGroupSize;
            sb.AppendLine($"#define CS_THREAD_GROUP_X {tg.X}");
            sb.AppendLine($"#define CS_THREAD_GROUP_Y {tg.Y}");
            sb.AppendLine($"#define CS_THREAD_GROUP_Z {tg.Z}");
            sb.AppendLine();

            sb.AppendLine($"[numthreads({tg.X}, {tg.Y}, {tg.Z})]");
            sb.AppendLine("void CSMain(");
            sb.AppendLine("    uint3 dispatchThreadId : SV_DispatchThreadID,");
            sb.AppendLine("    uint3 groupThreadId    : SV_GroupThreadID,");
            sb.AppendLine("    uint3 groupId          : SV_GroupID)");
            sb.AppendLine("{");
            sb.AppendLine($"    // Pixel coordinates");
            sb.AppendLine($"    float2 uv = float2(dispatchThreadId.x, dispatchThreadId.y);");
            sb.AppendLine($"    float2 screenSize = float2(CS_THREAD_GROUP_X * groupId.x + groupThreadId.x + 1,");
            sb.AppendLine($"                            CS_THREAD_GROUP_Y * groupId.y + groupThreadId.y + 1);");
            sb.AppendLine($"    uv = uv / screenSize;");
            sb.AppendLine();

            // 节点求值 (复用 PS 的求值代码)
            var master = graph.GetMasterNode()!;
            sb.AppendLine("    // === Material Node Evaluation ===");
            // 重新生成节点代码 (因为上下文中的 nodeCode 已经绑定到 PS 上下文)
            var csNodeCode = new StringBuilder();
            var csSorted = TopologicalSort(graph, master);
            foreach (var node in csSorted)
            {
                if (!node.Enabled) continue;
                GenerateNodeCode(node, ctx, csNodeCode);
            }
            sb.Append(csNodeCode.ToString());
            sb.AppendLine();

            // 输出材质属性
            sb.AppendLine("    // Output material properties as RGBA");
            var baseColorExpr = ctx.GetInputExpr(master, "BaseColor");
            var roughnessExpr = ctx.GetInputExpr(master, "Roughness");
            var metallicExpr = ctx.GetInputExpr(master, "Metallic");
            var aoExpr = ctx.GetInputExpr(master, "AO");

            sb.AppendLine($"    float3 baseColor = ({baseColorExpr});");
            sb.AppendLine($"    float  roughness = saturate(({roughnessExpr}));");
            sb.AppendLine($"    float  metallic  = saturate(({metallicExpr}));");
            sb.AppendLine($"    float  ao        = ({aoExpr});");
            sb.AppendLine();
            sb.AppendLine("    // Pack: R=baseColor.r, G=baseColor.g, B=baseColor.b, A=roughness");
            sb.AppendLine("    float4 packed = float4(baseColor, roughness);");
            sb.AppendLine("    OutputTexture[dispatchThreadId.xy] = packed;");
            sb.AppendLine("}");
            sb.AppendLine();

            return InjectTextureDeclarations(sb.ToString(), ctx);
        }

        // ════════════════════════════════════════════════════════════
        // Mesh Shader 生成
        // ════════════════════════════════════════════════════════════

        private string GenerateMeshShader(MaterialGraph graph, ShaderCompileOptions options,
                                          string commonHeader, NodeCodeGenContext ctx)
        {
            var sb = new StringBuilder();
            sb.Append(commonHeader);

            sb.AppendLine("// === Mesh Shader ===");
            sb.AppendLine("struct Meshlet");
            sb.AppendLine("{");
            sb.AppendLine("    uint VertCount;");
            sb.AppendLine("    uint VertOffset;");
            sb.AppendLine("    uint PrimCount;");
            sb.AppendLine("    uint PrimOffset;");
            sb.AppendLine("};");
            sb.AppendLine();
            sb.AppendLine("StructuredBuffer<Meshlet>       Meshlets         : register(t0);");
            sb.AppendLine("StructuredBuffer<uint>         VertexIndices    : register(t1);");
            sb.AppendLine("StructuredBuffer<uint>         PrimitiveIndices : register(t2);");
            sb.AppendLine("StructuredBuffer<VertexInput>  MeshVertices     : register(t3);");
            sb.AppendLine();
            sb.AppendLine("[outputtopology(\"triangle\")]");
            sb.AppendLine("struct VertexOut");
            sb.AppendLine("{");
            sb.AppendLine("    float4 Position : SV_POSITION;");
            sb.AppendLine("    float3 Normal   : NORMAL;");
            sb.AppendLine("    float2 TexCoord : TEXCOORD0;");
            sb.AppendLine("    float3 WorldPos : TEXCOORD1;");
            sb.AppendLine("};");
            sb.AppendLine();
            sb.AppendLine("[numthreads(128, 1, 1)]");
            sb.AppendLine("[outputtopology(\"triangle\")]");
            sb.AppendLine("void MSMain(");
            sb.AppendLine("    uint gtid : SV_GroupThreadID,");
            sb.AppendLine("    uint gid  : SV_GroupID,");
            sb.AppendLine("    out indices uint3 tris[256],");
            sb.AppendLine("    out vertices VertexOut verts[256])");
            sb.AppendLine("{");
            sb.AppendLine("    Meshlet m = Meshlets[gid];");
            sb.AppendLine("    SetMeshOutputCounts(m.VertCount, m.PrimCount);");
            sb.AppendLine();
            sb.AppendLine("    if (gtid < m.VertCount)");
            sb.AppendLine("    {");
            sb.AppendLine("        uint vIdx = VertexIndices[m.VertOffset + gtid];");
            sb.AppendLine("        VertexInput vin = MeshVertices[vIdx];");
            sb.AppendLine("        verts[gtid].Position = mul(float4(vin.Position, 1.0), WorldViewProj);");
            sb.AppendLine("        verts[gtid].WorldPos = mul(float4(vin.Position, 1.0), World).xyz;");
            sb.AppendLine("        verts[gtid].Normal   = mul(vin.Normal, (float3x3)World);");
            sb.AppendLine("        verts[gtid].TexCoord = vin.TexCoord;");
            sb.AppendLine("    }");
            sb.AppendLine();
            sb.AppendLine("    if (gtid < m.PrimCount)");
            sb.AppendLine("    {");
            sb.AppendLine("        uint pIdx = PrimitiveIndices[m.PrimOffset + gtid];");
            sb.AppendLine("        tris[gtid] = uint3(pIdx, pIdx + 1, pIdx + 2);");
            sb.AppendLine("    }");
            sb.AppendLine("}");
            sb.AppendLine();

            return InjectTextureDeclarations(sb.ToString(), ctx);
        }

        // ════════════════════════════════════════════════════════════
        // 纹理声明注入
        // ════════════════════════════════════════════════════════════

        private string InjectTextureDeclarations(string source, NodeCodeGenContext ctx)
        {
            if (ctx.TextureBindings.Count == 0) return source;

            var decls = new StringBuilder();
            decls.AppendLine("// === Auto-injected Texture Declarations ===");
            foreach (var tb in ctx.TextureBindings)
            {
                if (tb.IsCubeMap)
                    decls.AppendLine($"TextureCube CubeTex_{tb.ParameterName} : register({tb.Register});");
                else
                    decls.AppendLine($"Texture2D Tex_{tb.ParameterName} : register({tb.Register});");
            }
            decls.AppendLine();

            var insertMarker = "cbuffer PBRConstants";
            var idx = source.IndexOf(insertMarker, StringComparison.Ordinal);
            if (idx < 0) return decls.ToString() + source;
            return source.Substring(0, idx) + decls.ToString() + source.Substring(idx);
        }

        // ════════════════════════════════════════════════════════════
        // 辅助: 收集依赖节点
        // ════════════════════════════════════════════════════════════

        private List<MaterialNode> CollectDependentNodes(MaterialGraph graph,
                                                           MaterialNode startNode, string inputPinId)
        {
            var result = new List<MaterialNode>();
            var visited = new HashSet<string>();

            void Visit(MaterialNode node)
            {
                if (visited.Contains(node.Id)) return;
                visited.Add(node.Id);
                foreach (var pin in node.InputPins)
                {
                    var conn = graph.GetIncomingConnection(node.Id, pin.Id);
                    if (conn == null) continue;
                    var src = graph.Nodes.FirstOrDefault(n => n.Id == conn.FromNodeId);
                    if (src != null) Visit(src);
                }
                result.Add(node);
            }

            var conn0 = graph.GetIncomingConnection(startNode.Id, inputPinId);
            if (conn0 != null)
            {
                var src = graph.Nodes.FirstOrDefault(n => n.Id == conn0.FromNodeId);
                if (src != null) Visit(src);
            }
            return result;
        }

        // ════════════════════════════════════════════════════════════
        // 与 RHI 设备对接的便捷方法
        // ════════════════════════════════════════════════════════════

        /// <summary>编译材质图并通过 RHI 设备生成着色器字节码。</summary>
        public (RHIShaderBytecode VS, RHIShaderBytecode PS)? CompileToBytecode(
            MaterialGraph graph, RHIDevice device, ShaderCompileOptions options)
        {
            var result = Compile(graph, options);
            if (!result.Success || result.VertexShaderSource == null || result.PixelShaderSource == null)
                return null;

            var vs = device.CompileShader(result.VertexShaderSource, "VSMain",
                                          RHIShaderStage.Vertex, options.TargetShaderModel);
            var ps = device.CompileShader(result.PixelShaderSource, "PSMain",
                                          RHIShaderStage.Pixel, options.TargetShaderModel);

            if (!vs.IsValid || !ps.IsValid) return null;
            return (vs, ps);
        }

        /// <summary>批量编译所有有效变体 (Permutation)。</summary>
        public Dictionary<ShaderVariantKey, (RHIShaderBytecode VS, RHIShaderBytecode PS)> CompileAllVariants(
            MaterialGraph graph, RHIDevice device,
            RHIShaderModel shaderModel = RHIShaderModel.SM_6_6)
        {
            var variants = ShaderPermutationBuilder.EnumerateVariants(graph);
            var results = new Dictionary<ShaderVariantKey, (RHIShaderBytecode VS, RHIShaderBytecode PS)>();

            foreach (var variant in variants)
            {
                var keywords = string.IsNullOrEmpty(variant.KeyString)
                    ? new HashSet<string>()
                    : new HashSet<string>(variant.KeyString.Split(',', StringSplitOptions.RemoveEmptyEntries));

                var options = new ShaderCompileOptions
                {
                    TargetShaderModel = shaderModel,
                    Keywords = keywords,
                    GenerateVertexShader = true,
                    GeneratePixelShader = true,
                    GenerateMeshShader = false,
                    GenerateComputeShader = false,
                    UseCache = true
                };

                var bytecode = CompileToBytecode(graph, device, options);
                if (bytecode != null)
                    results[variant] = bytecode.Value;
            }

            return results;
        }

        /// <summary>增量编译 (缓存优先)。</summary>
        public ShaderCompileResult CompileIncremental(MaterialGraph graph, ShaderCompileOptions options)
        {
            options.UseCache = true;
            return Compile(graph, options);
        }
    }
}
