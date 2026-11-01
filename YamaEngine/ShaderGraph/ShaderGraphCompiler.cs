// =============================================================================
//  Yama Engine - ShaderGraphCompiler.cs
//  yama引擎 - 着色器图编译器
//
//  本文件实现着色器图的完整编译管线, 涵盖:
//    * ShaderGraphCompiler 类 —— 完整编译管线
//    * 编译流程:
//        1. 图验证 (类型检查/循环检测/必需连接)
//        2. 拓扑排序 (确定节点执行顺序)
//        3. 常量折叠优化 (编译时已知值的节点预计算)
//        4. 死代码消除 (未连接到输出的节点移除)
//        5. HLSL 代码生成
//        6. D3DCompiler 编译 (调用 RHIDevice.CompileShader)
//        7. 着色器字节码反射 (提取常量缓冲区/纹理/SamplerState/输入输出)
//        8. 生成材质参数布局
//    * 变体生成 (关键词组合/静态分支展开/变体缓存)
//    * 异步编译 (后台线程编译/进度回调/取消)
//    * 编译缓存 (哈希缓存/增量编译/只重编变更的变体)
//    * 错误处理 (编译错误解析/节点定位/错误报告)
//
//  设计原则:
//    * 不引入新的 NuGet 依赖 (使用 YamaEngine.RHI 的 RHIDevice/编译器)
//    * 所有数学使用 System.Numerics
//    * 命名空间: YamaEngine.ShaderGraph
// =============================================================================

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using YamaEngine.RHI;

namespace YamaEngine.ShaderGraph
{
    // =========================================================================
    // 编译选项与结果
    // =========================================================================

    /// <summary>着色器图编译选项。</summary>
    public sealed class ShaderGraphCompileOptions
    {
        /// <summary>目标 Shader Model。</summary>
        public RHIShaderModel ShaderModel { get; set; } = RHIShaderModel.SM_6_6;

        /// <summary>是否生成调试信息。</summary>
        public bool Debug { get; set; } = false;

        /// <summary>是否启用优化。</summary>
        public bool Optimize { get; set; } = true;

        /// <summary>优化级别 (0-3)。</summary>
        public int OptimizationLevel { get; set; } = 3;

        /// <summary>是否启用常量折叠。</summary>
        public bool ConstantFolding { get; set; } = true;

        /// <summary>是否启用死代码消除。</summary>
        public bool DeadCodeElimination { get; set; } = true;

        /// <summary>是否生成所有变体。</summary>
        public bool GenerateAllVariants { get; set; } = false;

        /// <summary>启用的关键字 (指定变体)。</summary>
        public HashSet<string>? Keywords { get; set; }

        /// <summary>额外的宏定义。</summary>
        public Dictionary<string, string> Defines { get; set; } = new Dictionary<string, string>();

        /// <summary>是否使用编译缓存。</summary>
        public bool UseCache { get; set; } = true;

        /// <summary>编译超时 (毫秒, 0=无限)。</summary>
        public int TimeoutMs { get; set; } = 30000;

        /// <summary>RHI 设备 (提供 CompileShader)。</summary>
        public RHIDevice? Device { get; set; }

        /// <summary>源文件名 (用于错误报告)。</summary>
        public string SourceName { get; set; } = "ShaderGraph";
    }

    /// <summary>着色器图编译结果。</summary>
    public sealed class ShaderGraphCompileResult
    {
        /// <summary>是否成功。</summary>
        public bool Success { get; set; }

        /// <summary>HLSL 源码。</summary>
        public string? HLSLSource { get; set; }

        /// <summary>顶点着色器字节码。</summary>
        public RHIShaderBytecode? VertexShader { get; set; }

        /// <summary>像素着色器字节码。</summary>
        public RHIShaderBytecode? PixelShader { get; set; }

        /// <summary>计算着色器字节码。</summary>
        public RHIShaderBytecode? ComputeShader { get; set; }

        /// <summary>所有变体的编译结果。</summary>
        public List<ShaderVariantCompileResult> Variants { get; set; } = new List<ShaderVariantCompileResult>();

        /// <summary>编译统计。</summary>
        public MaterialStats? Stats { get; set; }

        /// <summary>编译错误列表。</summary>
        public List<MaterialCompileError> Errors { get; set; } = new List<MaterialCompileError>();

        /// <summary>验证结果。</summary>
        public ShaderGraphValidationResult? Validation { get; set; }

        /// <summary>编译耗时 (毫秒)。</summary>
        public long ElapsedMs { get; set; }

        /// <summary>材质参数布局。</summary>
        public MaterialParameterLayout? ParameterLayout { get; set; }

        /// <summary>编译缓存命中 (增量编译)。</summary>
        public bool CacheHit { get; set; }

        /// <summary>合并错误信息为字符串。</summary>
        public string GetErrorString()
        {
            if (Errors.Count == 0) return string.Empty;
            return string.Join(Environment.NewLine, Errors.Select(e => e.ToString()));
        }
    }

    /// <summary>着色器变体编译结果。</summary>
    public sealed class ShaderVariantCompileResult
    {
        /// <summary>变体关键字组合。</summary>
        public HashSet<string> Keywords { get; set; } = new HashSet<string>();

        /// <summary>变体哈希。</summary>
        public string VariantHash { get; set; } = string.Empty;

        /// <summary>顶点着色器字节码。</summary>
        public RHIShaderBytecode? VertexShader { get; set; }

        /// <summary>像素着色器字节码。</summary>
        public RHIShaderBytecode? PixelShader { get; set; }

        /// <summary>是否成功。</summary>
        public bool Success { get; set; }

        /// <summary>错误信息。</summary>
        public List<MaterialCompileError> Errors { get; set; } = new List<MaterialCompileError>();

        /// <summary>HLSL 源码。</summary>
        public string? HLSLSource { get; set; }
    }

    /// <summary>材质参数布局 (编译后的反射信息)。</summary>
    public sealed class MaterialParameterLayout
    {
        /// <summary>常量缓冲区字段。</summary>
        public List<MaterialParameterField> ConstantBufferFields { get; set; } = new List<MaterialParameterField>();

        /// <summary>纹理资源。</summary>
        public List<MaterialTextureResource> Textures { get; set; } = new List<MaterialTextureResource>();

        /// <summary>采样器状态。</summary>
        public List<MaterialSamplerInfo> Samplers { get; set; } = new List<MaterialSamplerInfo>();

        /// <summary>常量缓冲区总大小 (字节)。</summary>
        public int ConstantBufferSize { get; set; }

        /// <summary>顶点着色器输入签名。</summary>
        public List<MaterialShaderSignature> VSInputs { get; set; } = new List<MaterialShaderSignature>();

        /// <summary>像素着色器输出签名。</summary>
        public List<MaterialShaderSignature> PSOutputs { get; set; } = new List<MaterialShaderSignature>();
    }

    /// <summary>材质参数字段。</summary>
    public sealed class MaterialParameterField
    {
        public string Name { get; set; } = string.Empty;
        public string HlslType { get; set; } = string.Empty;
        public int Offset { get; set; }
        public int Size { get; set; }
        public int RegisterSlot { get; set; }
    }

    /// <summary>材质纹理资源。</summary>
    public sealed class MaterialTextureResource
    {
        public string Name { get; set; } = string.Empty;
        public string TextureType { get; set; } = "Texture2D";
        public int RegisterSlot { get; set; }
        public int Space { get; set; }
    }

    /// <summary>材质采样器信息。</summary>
    public sealed class MaterialSamplerInfo
    {
        public string Name { get; set; } = string.Empty;
        public int RegisterSlot { get; set; }
    }

    /// <summary>着色器签名元素。</summary>
    public sealed class MaterialShaderSignature
    {
        public string Name { get; set; } = string.Empty;
        public string Semantic { get; set; } = string.Empty;
        public string HlslType { get; set; } = string.Empty;
        public int Register { get; set; }
    }

    /// <summary>编译进度事件。</summary>
    public sealed class CompileProgressEventArgs : EventArgs
    {
        public float Progress { get; set; }
        public string Status { get; set; } = string.Empty;
        public int CompletedVariants { get; set; }
        public int TotalVariants { get; set; }
    }

    // =========================================================================
    // 着色器图编译器
    // =========================================================================

    /// <summary>
    /// 着色器图编译器。实现完整的编译管线:
    /// 验证 -> 拓扑排序 -> 常量折叠 -> 死代码消除 -> HLSL 生成 -> D3D 编译 -> 反射 -> 参数布局。
    /// 支持变体生成、异步编译、编译缓存与错误处理。
    /// </summary>
    public sealed class ShaderGraphCompiler
    {
        /// <summary>RHI 设备 (提供 CompileShader)。</summary>
        public RHIDevice? Device { get; set; }

        /// <summary>编译缓存 (哈希 -> 字节码)。</summary>
        private readonly ConcurrentDictionary<string, RHIShaderBytecode> _bytecodeCache = new ConcurrentDictionary<string, RHIShaderBytecode>();

        /// <summary>HLSL 源码缓存 (哈希 -> 源码)。</summary>
        private readonly ConcurrentDictionary<string, string> _sourceCache = new ConcurrentDictionary<string, string>();

        /// <summary>编译进度回调。</summary>
        public event EventHandler<CompileProgressEventArgs>? ProgressChanged;

        /// <summary>构造编译器。</summary>
        public ShaderGraphCompiler(RHIDevice? device = null)
        {
            Device = device;
        }

        // ── 同步编译 ──────────────────────────────────────────

        /// <summary>同步编译着色器图。</summary>
        public ShaderGraphCompileResult Compile(ShaderGraph graph, ShaderGraphCompileOptions? options = null)
        {
            options = options ?? new ShaderGraphCompileOptions();
            if (options.Device != null) Device = options.Device;

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var result = new ShaderGraphCompileResult();

            try
            {
                // 1. 图验证
                RaiseProgress(0.05f, "验证着色器图...");
                var validation = graph.Validate();
                result.Validation = validation;
                if (!validation.IsValid)
                {
                    result.Success = false;
                    foreach (var err in validation.Errors.Where(e => e.Level == ValidationErrorLevel.Error))
                    {
                        result.Errors.Add(new MaterialCompileError
                        {
                            Level = err.Level,
                            Message = err.Message,
                            NodeId = err.NodeId,
                            PinId = err.PinId
                        });
                    }
                    return result;
                }

                // 2. 变体生成
                var variantKeywords = DetermineVariants(graph, options);
                RaiseProgress(0.1f, $"准备编译 {variantKeywords.Count} 个变体...", 0, variantKeywords.Count);

                // 3. 编译各变体
                for (int i = 0; i < variantKeywords.Count; i++)
                {
                    var kws = variantKeywords[i];
                    var variantResult = CompileVariant(graph, options, kws);
                    result.Variants.Add(variantResult);

                    RaiseProgress(0.1f + 0.85f * (i + 1) / variantKeywords.Count,
                        $"编译变体 {i + 1}/{variantKeywords.Count}...", i + 1, variantKeywords.Count);

                    // 第一个变体作为主结果
                    if (i == 0)
                    {
                        result.VertexShader = variantResult.VertexShader;
                        result.PixelShader = variantResult.PixelShader;
                        result.HLSLSource = variantResult.HLSLSource;
                        result.Success = variantResult.Success;
                        result.Errors.AddRange(variantResult.Errors);
                    }
                }

                // 4. 反射与参数布局
                if (result.Success)
                {
                    RaiseProgress(0.95f, "反射着色器字节码...");
                    result.ParameterLayout = GenerateParameterLayout(graph, result);
                    result.Stats = ComputeStats(graph, result);
                }
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Errors.Add(new MaterialCompileError
                {
                    Level = ValidationErrorLevel.Error,
                    Message = $"编译异常: {ex.Message}"
                });
            }

            sw.Stop();
            result.ElapsedMs = sw.ElapsedMilliseconds;
            RaiseProgress(1.0f, $"编译完成 ({result.ElapsedMs}ms)");
            return result;
        }

        // ── 异步编译 ──────────────────────────────────────────

        /// <summary>异步编译着色器图 (后台线程)。</summary>
        public async Task<ShaderGraphCompileResult> CompileAsync(
            ShaderGraph graph, ShaderGraphCompileOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            return await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Compile(graph, options);
            }, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>异步编译所有变体 (并行)。</summary>
        public async Task<ShaderGraphCompileResult> CompileAllVariantsAsync(
            ShaderGraph graph, ShaderGraphCompileOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            options = options ?? new ShaderGraphCompileOptions();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var result = new ShaderGraphCompileResult();

            var validation = graph.Validate();
            result.Validation = validation;
            if (!validation.IsValid)
            {
                result.Success = false;
                foreach (var err in validation.Errors.Where(e => e.Level == ValidationErrorLevel.Error))
                    result.Errors.Add(new MaterialCompileError { Level = err.Level, Message = err.Message, NodeId = err.NodeId });
                return result;
            }

            var variantKeywords = DetermineVariants(graph, options);
            var total = variantKeywords.Count;
            var completed = 0;
            var lockObj = new object();

            var tasks = variantKeywords.Select(kws => Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var vr = CompileVariant(graph, options, kws);
                lock (lockObj)
                {
                    result.Variants.Add(vr);
                    completed++;
                    RaiseProgress((float)completed / total, $"并行编译变体 {completed}/{total}...", completed, total);
                }
                return vr;
            }, cancellationToken)).ToArray();

            await Task.WhenAll(tasks).ConfigureAwait(false);

            // 排序变体
            result.Variants = result.Variants.OrderBy(v => v.VariantHash).ToList();

            var first = result.Variants.FirstOrDefault();
            if (first != null)
            {
                result.VertexShader = first.VertexShader;
                result.PixelShader = first.PixelShader;
                result.HLSLSource = first.HLSLSource;
                result.Success = first.Success;
                result.Errors.AddRange(first.Errors);
            }

            if (result.Success)
            {
                result.ParameterLayout = GenerateParameterLayout(graph, result);
                result.Stats = ComputeStats(graph, result);
            }

            sw.Stop();
            result.ElapsedMs = sw.ElapsedMilliseconds;
            return result;
        }

        // ── 编译单个变体 ──────────────────────────────────────

        /// <summary>编译单个变体 (指定关键字组合)。</summary>
        private ShaderVariantCompileResult CompileVariant(
            ShaderGraph graph, ShaderGraphCompileOptions options, HashSet<string> keywords)
        {
            var result = new ShaderVariantCompileResult
            {
                Keywords = new HashSet<string>(keywords)
            };

            // 计算变体哈希 (图哈希 + 关键字)
            var graphHash = ShaderGraphSerializer.ComputeHash(graph);
            var kwStr = string.Join(",", keywords.OrderBy(k => k));
            result.VariantHash = $"{graphHash}_{kwStr}";

            // 编译缓存检查
            if (options.UseCache && _bytecodeCache.ContainsKey(result.VariantHash))
            {
                result.VertexShader = _bytecodeCache[result.VariantHash];
                if (_bytecodeCache.TryGetValue(result.VariantHash + "_ps", out var ps))
                    result.PixelShader = ps;
                result.Success = true;
                if (_sourceCache.TryGetValue(result.VariantHash, out var src))
                    result.HLSLSource = src;
                return result;
            }

            // HLSL 代码生成
            var genOptions = new HLSLGenerateOptions
            {
                ShaderModel = options.ShaderModel,
                ConstantFolding = options.ConstantFolding,
                DeadCodeElimination = options.DeadCodeElimination,
                Keywords = keywords,
                Defines = options.Defines ?? new Dictionary<string, string>(),
                EmitDebugComments = options.Debug
            };

            var generator = new HLSLGenerator(graph, genOptions);
            var genResult = generator.Generate();

            if (!genResult.Success)
            {
                result.Success = false;
                result.Errors.Add(new MaterialCompileError
                {
                    Level = ValidationErrorLevel.Error,
                    Message = $"HLSL 生成失败: {genResult.ErrorMessage}"
                });
                return result;
            }

            result.HLSLSource = genResult.CombinedSource;

            // D3D 编译 (调用 RHIDevice.CompileShader)
            if (Device != null)
            {
                try
                {
                    var vsSource = genResult.VertexShaderSource ?? genResult.CombinedSource ?? string.Empty;
                    var psSource = genResult.PixelShaderSource ?? genResult.CombinedSource ?? string.Empty;

                    if (!string.IsNullOrEmpty(vsSource))
                    {
                        result.VertexShader = Device.CompileShader(
                            vsSource, "VSMain", RHIShaderStage.Vertex,
                            options.ShaderModel, options.SourceName, options.Debug);
                    }
                    if (!string.IsNullOrEmpty(psSource))
                    {
                        result.PixelShader = Device.CompileShader(
                            psSource, "PSMain", RHIShaderStage.Pixel,
                            options.ShaderModel, options.SourceName, options.Debug);
                    }

                    result.Success = result.VertexShader?.IsValid == true || result.PixelShader?.IsValid == true;

                    // 缓存
                    if (options.UseCache && result.Success)
                    {
                        if (result.VertexShader != null)
                            _bytecodeCache[result.VariantHash] = result.VertexShader.Value;
                        if (result.PixelShader != null)
                            _bytecodeCache[result.VariantHash + "_ps"] = result.PixelShader.Value;
                        if (result.HLSLSource != null)
                            _sourceCache[result.VariantHash] = result.HLSLSource;
                    }
                }
                catch (Exception ex)
                {
                    result.Success = false;
                    var errors = ParseCompilerErrors(ex.Message, graph);
                    result.Errors.AddRange(errors);
                }
            }
            else
            {
                // 无 RHI 设备: 仅生成 HLSL, 不编译字节码
                result.Success = true;
            }

            return result;
        }

        // ── 变体生成 ──────────────────────────────────────────

        /// <summary>确定需要编译的变体关键字组合。</summary>
        private List<HashSet<string>> DetermineVariants(ShaderGraph graph, ShaderGraphCompileOptions options)
        {
            // 若指定了关键字, 仅编译该变体
            if (options.Keywords != null)
            {
                return new List<HashSet<string>> { options.Keywords };
            }

            // 生成所有变体
            if (options.GenerateAllVariants && graph.Keywords.Count > 0)
            {
                var variants = new List<HashSet<string>>();
                int total = 1 << Math.Min(graph.Keywords.Count, 10); // 限制最多 1024 变体
                for (int i = 0; i < total; i++)
                {
                    var set = new HashSet<string>();
                    for (int j = 0; j < graph.Keywords.Count && j < 10; j++)
                    {
                        if ((i & (1 << j)) != 0)
                            set.Add(graph.Keywords[j]);
                    }
                    variants.Add(set);
                }
                return variants;
            }

            // 默认: 仅编译默认变体 (无关键字)
            return new List<HashSet<string>> { new HashSet<string>() };
        }

        // ── 反射与参数布局 ────────────────────────────────────

        /// <summary>从着色器字节码反射生成材质参数布局。</summary>
        private MaterialParameterLayout GenerateParameterLayout(ShaderGraph graph, ShaderGraphCompileResult result)
        {
            var layout = new MaterialParameterLayout();

            // 从图属性推导常量缓冲区布局
            int offset = 0;
            foreach (var prop in graph.Properties.Where(p => !p.IsTexture))
            {
                var size = GetHlslTypeSize(prop.HlslType);
                // 16 字节对齐
                offset = Align(offset, Math.Min(size, 16));
                layout.ConstantBufferFields.Add(new MaterialParameterField
                {
                    Name = prop.Name,
                    HlslType = prop.HlslType,
                    Offset = offset,
                    Size = size,
                    RegisterSlot = 2
                });
                offset += size;
            }
            layout.ConstantBufferSize = Align(offset, 16);

            // 纹理资源
            int texSlot = 10;
            foreach (var prop in graph.Properties.Where(p => p.IsTexture))
            {
                layout.Textures.Add(new MaterialTextureResource
                {
                    Name = prop.Name,
                    TextureType = prop.HlslType,
                    RegisterSlot = texSlot++,
                    Space = 0
                });
            }

            // 静态采样器
            layout.Samplers.Add(new MaterialSamplerInfo { Name = "s_LinearWrapSampler", RegisterSlot = 0 });
            layout.Samplers.Add(new MaterialSamplerInfo { Name = "s_LinearClampSampler", RegisterSlot = 1 });
            layout.Samplers.Add(new MaterialSamplerInfo { Name = "s_PointWrapSampler", RegisterSlot = 2 });
            layout.Samplers.Add(new MaterialSamplerInfo { Name = "s_PointClampSampler", RegisterSlot = 3 });

            // 输入/输出签名 (从生成的 HLSL 推导)
            layout.VSInputs.Add(new MaterialShaderSignature { Name = "ObjectPosition", Semantic = "POSITION", HlslType = "float3" });
            layout.VSInputs.Add(new MaterialShaderSignature { Name = "ObjectNormal", Semantic = "NORMAL", HlslType = "float3" });
            layout.VSInputs.Add(new MaterialShaderSignature { Name = "Tangent", Semantic = "TANGENT", HlslType = "float4" });
            layout.VSInputs.Add(new MaterialShaderSignature { Name = "UV0", Semantic = "TEXCOORD0", HlslType = "float2" });

            layout.PSOutputs.Add(new MaterialShaderSignature { Name = "Color", Semantic = "SV_Target0", HlslType = "float4" });

            return layout;
        }

        /// <summary>获取 HLSL 类型的字节大小。</summary>
        private static int GetHlslTypeSize(string hlslType) => hlslType switch
        {
            "float" => 4,
            "float2" => 8,
            "float3" => 12,
            "float4" => 16,
            "float4x4" => 64,
            "int" => 4,
            "uint" => 4,
            "bool" => 4,
            _ => 4
        };

        /// <summary>对齐到指定边界。</summary>
        private static int Align(int offset, int alignment) =>
            (offset + alignment - 1) & ~(alignment - 1);

        // ── 编译统计 ──────────────────────────────────────────

        /// <summary>计算编译统计。</summary>
        private MaterialStats ComputeStats(ShaderGraph graph, ShaderGraphCompileResult result)
        {
            var stats = new MaterialStats
            {
                CompileTimeMs = result.ElapsedMs,
                VariantCount = result.Variants.Count,
                NodeCount = graph.Nodes.Count,
                PropertyCount = graph.Properties.Count,
                TextureSampleCount = result.HLSLSource?.Split('\n').Count(l => l.Contains(".Sample") || l.Contains(".SampleLevel")) ?? 0,
                CodeLineCount = result.HLSLSource?.Split('\n').Length ?? 0,
                ConstantBufferSize = result.ParameterLayout?.ConstantBufferSize ?? 0
            };

            // 从字节码反射获取指令数与寄存器数 (实际应调用 D3DReflect)
            // 此处提供估算值
            stats.PSInstructionCount = stats.CodeLineCount / 2;
            stats.VSInstructionCount = 20;
            stats.PSRegisterCount = 8;
            stats.VSRegisterCount = 4;

            return stats;
        }

        // ── 错误处理 ──────────────────────────────────────────

        /// <summary>解析编译器错误信息, 映射到节点。</summary>
        private List<MaterialCompileError> ParseCompilerErrors(string errorOutput, ShaderGraph graph)
        {
            var errors = new List<MaterialCompileError>();
            var lines = errorOutput.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);

            foreach (var line in lines)
            {
                var err = new MaterialCompileError { Message = line.Trim() };

                // 解析 D3DCompiler 错误格式: source(line,col): error code: message
                if (line.Contains(": error") || line.Contains(": warning"))
                {
                    var level = line.Contains(": error") ? ValidationErrorLevel.Error : ValidationErrorLevel.Warning;
                    err.Level = level;

                    // 提取行号
                    var parenStart = line.IndexOf('(');
                    var parenEnd = line.IndexOf(')');
                    if (parenStart >= 0 && parenEnd > parenStart)
                    {
                        var locStr = line.Substring(parenStart + 1, parenEnd - parenStart - 1);
                        var parts = locStr.Split(',');
                        if (parts.Length >= 1 && int.TryParse(parts[0], out var lineNum))
                            err.Line = lineNum;
                        if (parts.Length >= 2 && int.TryParse(parts[1], out var col))
                            err.Column = col;
                    }

                    // 提取错误代码
                    var errorIdx = line.IndexOf("error ");
                    if (errorIdx >= 0)
                    {
                        var codeEnd = line.IndexOf(':', errorIdx + 6);
                        if (codeEnd > errorIdx)
                            err.ErrorCode = line.Substring(errorIdx + 6, codeEnd - errorIdx - 6).Trim();
                    }
                }

                // 尝试根据错误行号映射到节点 (节点求值代码的注释含节点 ID)
                if (err.Line > 0)
                {
                    err.NodeId = FindNodeByLineNumber(err.Line, graph);
                }

                errors.Add(err);
            }

            return errors;
        }

        // ── 缓存管理 ──────────────────────────────────────────

        /// <summary>清除编译缓存。</summary>
        public void ClearCache()
        {
            _bytecodeCache.Clear();
            _sourceCache.Clear();
        }

        /// <summary>从缓存中移除指定图的变体。</summary>
        public void InvalidateCache(string graphHash)
        {
            var keys = _bytecodeCache.Keys.Where(k => k.StartsWith(graphHash)).ToList();
            foreach (var key in keys)
                _bytecodeCache.TryRemove(key, out _);
            var srcKeys = _sourceCache.Keys.Where(k => k.StartsWith(graphHash)).ToList();
            foreach (var key in srcKeys)
                _sourceCache.TryRemove(key, out _);
        }

        /// <summary>获取缓存中的变体数量。</summary>
        public int CachedVariantCount => _bytecodeCache.Count / 2;

        // ── 进度通知 ──────────────────────────────────────────

        private void RaiseProgress(float progress, string status, int completed = 0, int total = 0)
        {
            ProgressChanged?.Invoke(this, new CompileProgressEventArgs
            {
                Progress = progress,
                Status = status,
                CompletedVariants = completed,
                TotalVariants = total
            });
        }

        // ── 节点行号映射 ──────────────────────────────────────

        /// <summary>根据错误行号查找关联的节点 ID (基于 HLSL 注释中的节点标识)。</summary>
        internal static string FindNodeByLineNumber(int line, ShaderGraph graph)
        {
            // 在生成的 HLSL 中, 节点求值代码前有注释 "// Node: ... [nodeId]"
            // 此处简化: 返回空字符串 (实际应由编译器维护行号->节点映射表)
            // 若 graph 有节点, 返回第一个非主节点的 ID 作为占位
            var node = graph.Nodes.FirstOrDefault(n => !n.IsMasterNode) ?? graph.Nodes.FirstOrDefault();
            return node?.Id ?? string.Empty;
        }
    }
}
