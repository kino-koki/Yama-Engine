// =============================================================================
//  Yama Engine - HLSLCodeGenerator.cs
//  yama引擎 - HLSL 代码生成器
//
//  本文件负责将 ShaderGraph (着色器图) 转换为可编译的 HLSL 源码。
//
//  核心功能:
//    * HLSLGenerator 类 —— 图 -> HLSL 代码生成
//    * 生成器架构:
//        - 属性声明 (cbuffer / 常量缓冲区)
//        - 结构体声明 (VSInput / PSInput / PSOutput)
//        - 顶点着色器代码
//        - 像素着色器代码
//        - 函数调用链 (按拓扑排序生成)
//        - 变量命名 (自动唯一命名 / 引脚 -> 变量映射)
//        - 代码缩进 / 格式化
//    * 生成模式:
//        - GenerateVertexShader()  —— 顶点着色器
//        - GeneratePixelShader()   —— 像素着色器
//        - GenerateComputeShader() —— 计算着色器
//        - GenerateHullShader() / GenerateDomainShader() —— 曲面细分
//        - GenerateGeometryShader() —— 几何着色器
//    * 预处理 (宏定义 / 条件编译 / 平台特定代码)
//    * 代码优化 (常量折叠 / 死代码消除 / 内联)
//    * 代码收集 (属性 / 纹理 / SamplerState / 常量缓冲区 / 结构体 / 函数 / Main)
//    * ShaderModel 版本选择 (5.0/5.1/6.0~6.7)
//
//  命名空间: YamaEngine.ShaderGraph
// =============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Text;
using YamaEngine.RHI;

namespace YamaEngine.ShaderGraph
{
    // =========================================================================
    // 生成选项与结果
    // =========================================================================

    /// <summary>HLSL 代码生成选项。</summary>
    public sealed class HLSLGenerateOptions
    {
        /// <summary>目标 Shader Model。</summary>
        public RHIShaderModel ShaderModel { get; set; } = RHIShaderModel.SM_6_6;

        /// <summary>是否生成顶点着色器。</summary>
        public bool GenerateVertexShader { get; set; } = true;

        /// <summary>是否生成像素着色器。</summary>
        public bool GeneratePixelShader { get; set; } = true;

        /// <summary>是否生成计算着色器。</summary>
        public bool GenerateComputeShader { get; set; } = false;

        /// <summary>是否生成外壳/域着色器 (曲面细分)。</summary>
        public bool GenerateTessellation { get; set; } = false;

        /// <summary>是否生成几何着色器。</summary>
        public bool GenerateGeometryShader { get; set; } = false;

        /// <summary>是否启用常量折叠优化。</summary>
        public bool ConstantFolding { get; set; } = true;

        /// <summary>是否启用死代码消除。</summary>
        public bool DeadCodeElimination { get; set; } = true;

        /// <summary>是否在生成的代码中包含调试注释。</summary>
        public bool EmitDebugComments { get; set; } = true;

        /// <summary>启用的着色器关键字 (变体)。</summary>
        public HashSet<string> Keywords { get; set; } = new HashSet<string>();

        /// <summary>额外的宏定义 (名称 -> 值)。</summary>
        public Dictionary<string, string> Defines { get; set; } = new Dictionary<string, string>();

        /// <summary>额外的 include 头文件路径。</summary>
        public List<string> Includes { get; set; } = new List<string>();

        /// <summary>计算着色器线程组大小。</summary>
        public Vector3 ComputeThreadGroupSize { get; set; } = new Vector3(8, 8, 1);

        /// <summary>缩进字符串。</summary>
        public string Indent { get; set; } = "    ";
    }

    /// <summary>HLSL 代码生成结果。</summary>
    public sealed class HLSLGenerateResult
    {
        /// <summary>顶点着色器源码 (含完整结构体与函数)。</summary>
        public string? VertexShaderSource { get; set; }

        /// <summary>像素着色器源码。</summary>
        public string? PixelShaderSource { get; set; }

        /// <summary>计算着色器源码。</summary>
        public string? ComputeShaderSource { get; set; }

        /// <summary>外壳着色器源码。</summary>
        public string? HullShaderSource { get; set; }

        /// <summary>域着色器源码。</summary>
        public string? DomainShaderSource { get; set; }

        /// <summary>几何着色器源码。</summary>
        public string? GeometryShaderSource { get; set; }

        /// <summary>合并的 HLSL 源码 (VS + PS 在同一文件)。</summary>
        public string? CombinedSource { get; set; }

        /// <summary>收集的全局声明 (纹理/SamplerState/cbuffer)。</summary>
        public List<string> Declarations { get; set; } = new List<string>();

        /// <summary>生成的节点求值函数列表。</summary>
        public List<string> NodeFunctions { get; set; } = new List<string>();

        /// <summary>是否成功。</summary>
        public bool Success { get; set; } = true;

        /// <summary>错误信息。</summary>
        public string ErrorMessage { get; set; } = string.Empty;

        /// <summary>生成耗时 (毫秒)。</summary>
        public long ElapsedMs { get; set; }

        /// <summary>指令数估算 (生成的语句数)。</summary>
        public int EstimatedInstructions { get; set; }

        /// <summary>纹理采样次数。</summary>
        public int TextureSampleCount { get; set; }
    }

    // =========================================================================
    // HLSL 代码生成器
    // =========================================================================

    /// <summary>
    /// HLSL 代码生成器。将 ShaderGraph 转换为完整的 HLSL 源码。
    /// 生成流程:
    ///   1. 拓扑排序节点 (确定求值顺序)
    ///   2. 收集属性 -> cbuffer 声明
    ///   3. 收集纹理/SamplerState 声明
    ///   4. 生成结构体 (VSInput / PSInput / PSOutput)
    ///   5. 按拓扑序逐节点调用 GenerateCode, 收集语句与输出变量
    ///   6. 生成顶点/像素/计算着色器入口函数
    ///   7. 常量折叠与死代码消除优化
    /// </summary>
    public sealed class HLSLGenerator
    {
        private readonly ShaderGraph _graph;
        private readonly HLSLGenerateOptions _options;
        private readonly StringBuilder _declBuilder = new StringBuilder();
        private readonly StringBuilder _structBuilder = new StringBuilder();
        private readonly StringBuilder _funcBuilder = new StringBuilder();
        private readonly StringBuilder _mainBuilder = new StringBuilder();

        // 引脚 -> 变量名 映射 (输出引脚的 HLSL 变量名)
        private readonly Dictionary<string, string> _pinVarMap = new Dictionary<string, string>();
        // 已收集的全局声明 (去重)
        private readonly HashSet<string> _declSet = new HashSet<string>();
        // 变量名计数器 (保证唯一)
        private int _varCounter;
        // 纹理采样次数
        private int _textureSampleCount;

        /// <summary>构造生成器。</summary>
        public HLSLGenerator(ShaderGraph graph, HLSLGenerateOptions? options = null)
        {
            _graph = graph ?? throw new ArgumentNullException(nameof(graph));
            _options = options ?? new HLSLGenerateOptions();
        }

        // ── 入口: 生成全部着色器 ────────────────────────────────

        /// <summary>生成所有着色器源码。</summary>
        public HLSLGenerateResult Generate()
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var result = new HLSLGenerateResult();

            try
            {
                // 1. 收集声明
                CollectDeclarations();

                // 2. 生成结构体
                GenerateStructures();

                // 3. 生成节点求值代码
                var evalCode = GenerateNodeEvaluation();

                // 4. 生成各着色器入口
                if (_options.GenerateVertexShader)
                    result.VertexShaderSource = GenerateVertexShaderSource(evalCode);
                if (_options.GeneratePixelShader)
                    result.PixelShaderSource = GeneratePixelShaderSource(evalCode);
                if (_options.GenerateComputeShader)
                    result.ComputeShaderSource = GenerateComputeShaderSource(evalCode);
                if (_options.GenerateTessellation)
                {
                    result.HullShaderSource = GenerateHullShaderSource(evalCode);
                    result.DomainShaderSource = GenerateDomainShaderSource(evalCode);
                }
                if (_options.GenerateGeometryShader)
                    result.GeometryShaderSource = GenerateGeometryShaderSource(evalCode);

                // 5. 合并源码
                result.CombinedSource = BuildCombinedSource(evalCode);
                result.Declarations = _declSet.ToList();
                result.EstimatedInstructions = _mainBuilder.Length / 20; // 粗略估算
                result.TextureSampleCount = _textureSampleCount;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = ex.Message;
            }

            sw.Stop();
            result.ElapsedMs = sw.ElapsedMilliseconds;
            return result;
        }

        // ── 收集声明 ──────────────────────────────────────────

        /// <summary>收集属性 (cbuffer)、纹理、SamplerState 声明。</summary>
        private void CollectDeclarations()
        {
            // 全局常量缓冲区 (引擎级)
            EmitDecl("cbuffer EngineConstants : register(b0)");
            EmitDecl("{");
            EmitDecl("    float4x4 g_WorldMatrix;");
            EmitDecl("    float4x4 g_ViewMatrix;");
            EmitDecl("    float4x4 g_ProjMatrix;");
            EmitDecl("    float4x4 g_ViewProjMatrix;");
            EmitDecl("    float4x4 g_InvViewMatrix;");
            EmitDecl("    float4x4 g_InvWorldMatrix;");
            EmitDecl("    float3   g_CameraWorldPos;");
            EmitDecl("    float    g_Time;");
            EmitDecl("    float    g_DeltaTime;");
            EmitDecl("    float4   g_ScreenSize; // xy=分辨率, zw=1/分辨率");
            EmitDecl("    float3   g_AmbientColor;");
            EmitDecl("    float    g_AlphaCutoff;");
            EmitDecl("};");

            // 方向光
            EmitDecl("cbuffer DirectionalLightCB : register(b1)");
            EmitDecl("{");
            EmitDecl("    float3 g_DirLightDirection;");
            EmitDecl("    float  _pad0;");
            EmitDecl("    float3 g_DirLightColor;");
            EmitDecl("    float  g_DirLightIntensity;");
            EmitDecl("};");

            // 材质属性 cbuffer
            var matProps = _graph.Properties.Where(p => !p.IsTexture).ToList();
            if (matProps.Count > 0)
            {
                EmitDecl("cbuffer MaterialConstants : register(b2)");
                EmitDecl("{");
                foreach (var prop in matProps)
                {
                    EmitDecl($"    {prop.HlslType} {prop.Name}; // {prop.DisplayName}");
                }
                EmitDecl("};");
            }

            // 静态采样器
            EmitDecl("SamplerState s_LinearWrapSampler : register(s0);");
            EmitDecl("SamplerState s_LinearClampSampler : register(s1);");
            EmitDecl("SamplerState s_PointWrapSampler : register(s2);");
            EmitDecl("SamplerState s_PointClampSampler : register(s3);");
            EmitDecl("SamplerComparisonState s_ComparisonSampler : register(s4);");

            // 纹理属性
            int texSlot = 10;
            foreach (var prop in _graph.Properties.Where(p => p.IsTexture))
            {
                var hlslType = prop.HlslType;
                EmitDecl($"{hlslType} {prop.Name} : register(t{texSlot}); // {prop.DisplayName}");
                texSlot++;
            }
        }

        // ── 生成结构体 ────────────────────────────────────────

        /// <summary>生成 VSInput / PSInput / PSOutput 结构体。</summary>
        private void GenerateStructures()
        {
            // 顶点着色器输入
            EmitStruct("struct VSInput");
            EmitStruct("{");
            EmitStruct("    float3 ObjectPosition : POSITION;");
            EmitStruct("    float3 ObjectNormal   : NORMAL;");
            EmitStruct("    float4 Tangent        : TANGENT;");
            EmitStruct("    float2 UV0            : TEXCOORD0;");
            EmitStruct("    float2 UV1            : TEXCOORD1;");
            EmitStruct("    float2 UV2            : TEXCOORD2;");
            EmitStruct("    float2 UV3            : TEXCOORD3;");
            EmitStruct("    float4 Color          : COLOR;");
            EmitStruct("    uint   InstanceID     : SV_InstanceID;");
            EmitStruct("    uint   VertexID       : SV_VertexID;");
            EmitStruct("};");

            // 像素着色器输入 (VS 输出)
            EmitStruct("struct PSInput");
            EmitStruct("{");
            EmitStruct("    float4 ScreenPosition : SV_POSITION;");
            EmitStruct("    float3 WorldPosition  : TEXCOORD0;");
            EmitStruct("    float3 ObjectPosition : TEXCOORD1;");
            EmitStruct("    float3 WorldNormal     : TEXCOORD2;");
            EmitStruct("    float3 ObjectNormal    : TEXCOORD3;");
            EmitStruct("    float3 WorldTangent    : TEXCOORD4;");
            EmitStruct("    float3 WorldBitangent  : TEXCOORD5;");
            EmitStruct("    float2 UV0             : TEXCOORD6;");
            EmitStruct("    float2 UV1             : TEXCOORD7;");
            EmitStruct("    float2 UV2             : TEXCOORD8;");
            EmitStruct("    float2 UV3             : TEXCOORD9;");
            EmitStruct("    float4 Color           : COLOR0;");
            EmitStruct("    float3 TangentPosition : TEXCOORD10;");
            EmitStruct("    uint   InstanceID      : SV_InstanceID;");
            EmitStruct("    float2 PixelCoord      : VPOS;");
            EmitStruct("};");

            // 像素着色器输出
            EmitStruct("struct PSOutput");
            EmitStruct("{");
            EmitStruct("    float4 Color : SV_Target0;");
            EmitStruct("};");

            // 材质输出 (中间结构)
            EmitStruct("struct MaterialOutput");
            EmitStruct("{");
            EmitStruct("    float3 Albedo;");
            EmitStruct("    float  Metallic;");
            EmitStruct("    float  Roughness;");
            EmitStruct("    float3 Normal;");
            EmitStruct("    float  AO;");
            EmitStruct("    float3 Emission;");
            EmitStruct("    float  Alpha;");
            EmitStruct("    float3 WorldPositionOffset;");
            EmitStruct("};");
        }

        // ── 生成节点求值代码 ──────────────────────────────────

        /// <summary>
        /// 按拓扑序遍历节点, 调用每个节点的 GenerateCode,
        /// 收集语句与输出变量, 返回求值代码字符串。
        /// </summary>
        private string GenerateNodeEvaluation()
        {
            var order = _graph.TopologicalSort();
            if (_options.DeadCodeElimination)
            {
                // 死代码消除: 仅保留从输出节点可达的节点 (TopologicalSort 已保证)
            }

            var sb = new StringBuilder();
            _pinVarMap.Clear();
            _varCounter = 0;

            foreach (var node in order)
            {
                if (!node.Enabled) continue;
                if (node is CommentNode) continue;

                if (_options.EmitDebugComments)
                    sb.AppendLine($"    // Node: {node.Title} ({node.NodeType}) [{node.Id}]");

                // 构建上下文
                var ctx = new NodeCodegenContext(_graph, node)
                {
                    Precision = _graph.Precision
                };
                foreach (var kw in _options.Keywords)
                    ctx.Keywords.Add(kw);

                // 为每个输入引脚设置上游输出变量表达式
                foreach (var pin in node.InputPins)
                {
                    var conn = _graph.GetConnectionToInput(node.Id, pin.Id);
                    if (conn != null)
                    {
                        var upstreamVar = GetPinVariable(conn.SourceNodeId, conn.SourcePinId);
                        ctx.SetInput(pin.Name, upstreamVar);
                    }
                    else
                    {
                        ctx.SetInput(pin.Name, pin.DefaultHlslLiteral);
                    }
                }

                // 常量折叠: 如果所有输入都是字面量且节点是数学节点, 可预计算
                if (_options.ConstantFolding && TryConstantFold(node, ctx, out var foldedValue))
                {
                    // 直接使用折叠后的值
                    var outPin = node.OutputPins.FirstOrDefault();
                    if (outPin != null)
                    {
                        var varName = AllocVariable(node, outPin);
                        var type = ctx.TypeOf(outPin.DataType);
                        sb.AppendLine($"    {type} {varName} = {foldedValue};");
                        _pinVarMap[PinKey(node.Id, outPin.Id)] = varName;
                    }
                    continue;
                }

                // 调用节点代码生成
                node.GenerateCode(ctx);

                // 收集声明
                foreach (var decl in ctx.Declarations)
                    EmitDecl(decl);

                // 发出节点附加语句
                foreach (var stmt in ctx.Statements)
                    sb.AppendLine($"    {stmt}");

                // 为每个输出引脚创建变量
                foreach (var outPin in node.OutputPins)
                {
                    var expr = ctx.GetOutput(outPin.Name);
                    if (string.IsNullOrEmpty(expr)) continue;

                    // 检测纹理采样 (用于统计)
                    if (expr.Contains(".Sample") || expr.Contains(".SampleLevel") ||
                        expr.Contains(".SampleGrad") || expr.Contains(".Gather"))
                        _textureSampleCount++;

                    var varName = AllocVariable(node, outPin);
                    var type = ctx.TypeOf(outPin.DataType);
                    // 若表达式是多语句变量引用, 直接赋值
                    sb.AppendLine($"    {type} {varName} = {expr};");
                    _pinVarMap[PinKey(node.Id, outPin.Id)] = varName;
                }
            }

            return sb.ToString();
        }

        /// <summary>尝试常量折叠。如果节点所有输入都是编译时已知的常量, 预计算结果。</summary>
        private bool TryConstantFold(ShaderGraphNode node, NodeCodegenContext ctx, out string foldedValue)
        {
            foldedValue = string.Empty;

            // 仅对简单一元/二元数学节点尝试折叠
            if (node is UnaryMathNode)
            {
                if (ctx.TryGetInputFloat("X", out var x))
                {
                    if (node is AbsNode) foldedValue = $"{Math.Abs(x):G9}";
                    else if (node is NegateNode) foldedValue = $"{(-x):G9}";
                    else if (node is OneMinusNode) foldedValue = $"{(1.0f - x):G9}";
                    else if (node is ReciprocalNode) foldedValue = x != 0 ? $"{(1.0f / x):G9}" : "1e9";
                    else if (node is SaturateNode) foldedValue = $"{Math.Clamp(x, 0f, 1f):G9}";
                    else if (node is FloorNode) foldedValue = $"{MathF.Floor(x):G9}";
                    else if (node is CeilingNode) foldedValue = $"{MathF.Ceiling(x):G9}";
                    else if (node is SineNode) foldedValue = $"{MathF.Sin(x):G9}";
                    else if (node is CosineNode) foldedValue = $"{MathF.Cos(x):G9}";
                    else return false;
                    return true;
                }
            }
            else if (node is BinaryMathNode bn)
            {
                if (ctx.TryGetInputFloat("A", out var a) && ctx.TryGetInputFloat("B", out var b))
                {
                    if (bn is AddNode) foldedValue = $"{(a + b):G9}";
                    else if (bn is SubtractNode) foldedValue = $"{(a - b):G9}";
                    else if (bn is MultiplyNode) foldedValue = $"{(a * b):G9}";
                    else if (bn is DivideNode) foldedValue = b != 0 ? $"{(a / b):G9}" : "0";
                    else if (bn is MinNode) foldedValue = $"{Math.Min(a, b):G9}";
                    else if (bn is MaxNode) foldedValue = $"{Math.Max(a, b):G9}";
                    else if (bn is PowerNode) foldedValue = $"{MathF.Pow(a, b):G9}";
                    else if (bn is ModuloNode) foldedValue = b != 0 ? $"{(a % b):G9}" : "0";
                    else return false;
                    return true;
                }
            }

            return false;
        }

        // ── 生成顶点着色器 ────────────────────────────────────

        /// <summary>生成顶点着色器源码。</summary>
        public string GenerateVertexShader(string evalCode = "")
        {
            if (string.IsNullOrEmpty(evalCode))
                evalCode = GenerateNodeEvaluation();
            return GenerateVertexShaderSource(evalCode);
        }

        private string GenerateVertexShaderSource(string evalCode)
        {
            var sb = new StringBuilder();
            AppendHeader(sb);
            AppendDeclarations(sb);
            AppendStructures(sb);

            sb.AppendLine();
            sb.AppendLine("// =========================================================================");
            sb.AppendLine("// 顶点着色器");
            sb.AppendLine("// =========================================================================");
            sb.AppendLine("PSInput VSMain(VSInput IN)");
            sb.AppendLine("{");
            sb.AppendLine("    PSInput OUT = (PSInput)0;");
            sb.AppendLine();
            sb.AppendLine("    // 空间变换");
            sb.AppendLine("    OUT.ObjectPosition = IN.ObjectPosition;");
            sb.AppendLine("    OUT.ObjectNormal = IN.ObjectNormal;");
            sb.AppendLine("    float4 worldPos = mul(g_WorldMatrix, float4(IN.ObjectPosition, 1.0));");
            sb.AppendLine("    OUT.WorldPosition = worldPos.xyz;");
            sb.AppendLine("    OUT.WorldNormal = normalize(mul((float3x3)g_WorldMatrix, IN.ObjectNormal));");
            sb.AppendLine("    OUT.WorldTangent = normalize(mul((float3x3)g_WorldMatrix, IN.Tangent.xyz));");
            sb.AppendLine("    OUT.WorldBitangent = cross(OUT.WorldNormal, OUT.WorldTangent) * IN.Tangent.w;");
            sb.AppendLine("    OUT.UV0 = IN.UV0; OUT.UV1 = IN.UV1; OUT.UV2 = IN.UV2; OUT.UV3 = IN.UV3;");
            sb.AppendLine("    OUT.Color = IN.Color;");
            sb.AppendLine("    OUT.InstanceID = IN.InstanceID;");
            sb.AppendLine("    OUT.ScreenPosition = mul(g_ViewProjMatrix, worldPos);");
            sb.AppendLine("    OUT.PixelCoord = OUT.ScreenPosition.xy;");
            sb.AppendLine();
            sb.AppendLine("    // 节点求值 (世界位置偏移)");
            sb.AppendLine("    MaterialOutput mat = (MaterialOutput)0;");
            sb.Append(evalCode);
            sb.AppendLine("    worldPos.xyz += mat.WorldPositionOffset;");
            sb.AppendLine("    OUT.ScreenPosition = mul(g_ViewProjMatrix, worldPos);");
            sb.AppendLine("    return OUT;");
            sb.AppendLine("}");

            return sb.ToString();
        }

        // ── 生成像素着色器 ────────────────────────────────────

        /// <summary>生成像素着色器源码。</summary>
        public string GeneratePixelShader(string evalCode = "")
        {
            if (string.IsNullOrEmpty(evalCode))
                evalCode = GenerateNodeEvaluation();
            return GeneratePixelShaderSource(evalCode);
        }

        private string GeneratePixelShaderSource(string evalCode)
        {
            var sb = new StringBuilder();
            AppendHeader(sb);
            AppendDeclarations(sb);
            AppendStructures(sb);

            // 辅助函数库
            AppendHelperFunctions(sb);

            sb.AppendLine();
            sb.AppendLine("// =========================================================================");
            sb.AppendLine("// 像素着色器");
            sb.AppendLine("// =========================================================================");
            sb.AppendLine("PSOutput PSMain(PSInput IN)");
            sb.AppendLine("{");
            sb.AppendLine("    PSOutput OUT = (PSOutput)0;");
            sb.AppendLine("    MaterialOutput mat = (MaterialOutput)0;");
            sb.AppendLine("    mat.Albedo = float3(1, 1, 1);");
            sb.AppendLine("    mat.Roughness = 0.5;");
            sb.AppendLine("    mat.Alpha = 1.0;");
            sb.AppendLine("    mat.Normal = IN.WorldNormal;");
            sb.AppendLine("    mat.AO = 1.0;");
            sb.AppendLine();
            sb.AppendLine("    // ---- 节点求值 ----");
            sb.Append(evalCode);
            sb.AppendLine();

            // 根据着色模型生成最终颜色
            AppendShadingModel(sb);

            // Alpha 处理
            if (_graph.BlendMode == ShaderGraphBlendMode.Masked)
            {
                sb.AppendLine($"    // Alpha 截断 (Masked)");
                sb.AppendLine($"    clip(mat.Alpha - g_AlphaCutoff);");
            }

            sb.AppendLine("    OUT.Color = float4(finalColor, mat.Alpha);");
            sb.AppendLine("    return OUT;");
            sb.AppendLine("}");

            return sb.ToString();
        }

        /// <summary>根据着色模型追加最终颜色计算。</summary>
        private void AppendShadingModel(StringBuilder sb)
        {
            sb.AppendLine("    // ---- 着色模型 ----");
            switch (_graph.ShadingModel)
            {
                case ShaderGraphShadingModel.Unlit:
                    sb.AppendLine("    float3 finalColor = mat.Albedo + mat.Emission;");
                    break;
                case ShaderGraphShadingModel.Lit:
                    sb.AppendLine("    float3 N = normalize(mat.Normal);");
                    sb.AppendLine("    float3 V = normalize(g_CameraWorldPos - IN.WorldPosition);");
                    sb.AppendLine("    float3 L = normalize(-g_DirLightDirection);");
                    sb.AppendLine("    float3 H = normalize(L + V);");
                    sb.AppendLine("    float NdotL = max(dot(N, L), 0.0);");
                    sb.AppendLine("    float NdotH = max(dot(N, H), 0.0);");
                    sb.AppendLine("    float NdotV = max(dot(N, V), 0.0);");
                    sb.AppendLine("    // Cook-Torrance BRDF");
                    sb.AppendLine("    float3 F0 = lerp(float3(0.04, 0.04, 0.04), mat.Albedo, mat.Metallic);");
                    sb.AppendLine("    float3 radiance = g_DirLightColor * NdotL;");
                    sb.AppendLine("    float alpha = mat.Roughness * mat.Roughness;");
                    sb.AppendLine("    float a2 = alpha * alpha;");
                    sb.AppendLine("    float denom = NdotH * NdotH * (a2 - 1.0) + 1.0;");
                    sb.AppendLine("    float D = a2 / (3.14159265 * denom * denom);");
                    sb.AppendLine("    float3 F = F0 + (1.0 - F0) * pow(1.0 - max(dot(H, V), 0.0), 5.0);");
                    sb.AppendLine("    float k = (mat.Roughness + 1.0) * (mat.Roughness + 1.0) / 8.0;");
                    sb.AppendLine("    float G = NdotV / (NdotV * (1.0 - k) + k);");
                    sb.AppendLine("    float3 specular = D * F * G / (4.0 * NdotV * NdotL + 0.001);");
                    sb.AppendLine("    float3 kD = (1.0 - F) * (1.0 - mat.Metallic);");
                    sb.AppendLine("    float3 diffuse = kD * mat.Albedo / 3.14159265;");
                    sb.AppendLine("    float3 finalColor = (diffuse + specular) * radiance * mat.AO + mat.Emission + g_AmbientColor * mat.Albedo * mat.AO;");
                    break;
                case ShaderGraphShadingModel.ClearCoat:
                    sb.AppendLine("    float3 finalColor = mat.Albedo * 0.8 + mat.Emission; // ClearCoat 简化");
                    break;
                case ShaderGraphShadingModel.SubsurfaceScattering:
                    sb.AppendLine("    float3 finalColor = mat.Albedo + mat.Emission; // SSS 简化");
                    break;
                default:
                    sb.AppendLine("    float3 finalColor = mat.Albedo + mat.Emission;");
                    break;
            }
        }

        // ── 生成计算着色器 ────────────────────────────────────

        /// <summary>生成计算着色器源码。</summary>
        public string GenerateComputeShader(string evalCode = "")
        {
            if (string.IsNullOrEmpty(evalCode))
                evalCode = GenerateNodeEvaluation();
            return GenerateComputeShaderSource(evalCode);
        }

        private string GenerateComputeShaderSource(string evalCode)
        {
            var sb = new StringBuilder();
            AppendHeader(sb);
            AppendDeclarations(sb);

            var tg = _options.ComputeThreadGroupSize;
            sb.AppendLine();
            sb.AppendLine("// =========================================================================");
            sb.AppendLine("// 计算着色器");
            sb.AppendLine("// =========================================================================");
            sb.AppendLine($"[numthreads({(int)tg.X}, {(int)tg.Y}, {(int)tg.Z})]");
            sb.AppendLine("void CSMain(uint3 DTid : SV_DispatchThreadID, uint3 Gid : SV_GroupID, uint3 GTid : SV_GroupThreadID, uint GI : SV_GroupIndex)");
            sb.AppendLine("{");
            sb.AppendLine("    // 计算着色器节点求值");
            sb.Append(evalCode);
            sb.AppendLine("}");

            return sb.ToString();
        }

        // ── 生成曲面细分着色器 ────────────────────────────────

        /// <summary>生成外壳着色器源码。</summary>
        public string GenerateHullShader(string evalCode = "") =>
            GenerateHullShaderSource(string.IsNullOrEmpty(evalCode) ? GenerateNodeEvaluation() : evalCode);

        private string GenerateHullShaderSource(string evalCode)
        {
            var sb = new StringBuilder();
            AppendHeader(sb);
            AppendDeclarations(sb);
            AppendStructures(sb);
            sb.AppendLine();
            sb.AppendLine("// =========================================================================");
            sb.AppendLine("// 外壳着色器 (Hull Shader)");
            sb.AppendLine("// =========================================================================");
            sb.AppendLine("struct HSConst");
            sb.AppendLine("{");
            sb.AppendLine("    float Edges[3] : SV_TessFactor;");
            sb.AppendLine("    float Inside   : SV_InsideTessFactor;");
            sb.AppendLine("};");
            sb.AppendLine();
            sb.AppendLine("HSConst HSConstFunc(InputPatch<PSInput, 3> patch)");
            sb.AppendLine("{");
            sb.AppendLine("    HSConst OUT;");
            sb.AppendLine("    OUT.Edges[0] = OUT.Edges[1] = OUT.Edges[2] = 3.0;");
            sb.AppendLine("    OUT.Inside = 3.0;");
            sb.AppendLine("    return OUT;");
            sb.AppendLine("}");
            sb.AppendLine();
            sb.AppendLine("[domain(\"tri\")]");
            sb.AppendLine("[partitioning(\"fractional_odd\")]");
            sb.AppendLine("[outputtopology(\"triangle_cw\")]");
            sb.AppendLine("[outputcontrolpoints(3)]");
            sb.AppendLine("[patchconstantfunc(\"HSConstFunc\")]");
            sb.AppendLine("PSInput HSMain(InputPatch<PSInput, 3> patch, uint id : SV_OutputControlPointID)");
            sb.AppendLine("{");
            sb.AppendLine("    return patch[id];");
            sb.AppendLine("}");
            return sb.ToString();
        }

        /// <summary>生成域着色器源码。</summary>
        public string GenerateDomainShader(string evalCode = "") =>
            GenerateDomainShaderSource(string.IsNullOrEmpty(evalCode) ? GenerateNodeEvaluation() : evalCode);

        private string GenerateDomainShaderSource(string evalCode)
        {
            var sb = new StringBuilder();
            AppendHeader(sb);
            AppendDeclarations(sb);
            AppendStructures(sb);
            sb.AppendLine();
            sb.AppendLine("// =========================================================================");
            sb.AppendLine("// 域着色器 (Domain Shader)");
            sb.AppendLine("// =========================================================================");
            sb.AppendLine("[domain(\"tri\")]");
            sb.AppendLine("PSInput DSMain(HSConst input, float3 uv : SV_DomainLocation, const OutputPatch<PSInput, 3> patch)");
            sb.AppendLine("{");
            sb.AppendLine("    PSInput OUT = (PSInput)0;");
            sb.AppendLine("    float3 bary = uv;");
            sb.AppendLine("    OUT.WorldPosition = patch[0].WorldPosition * bary.x + patch[1].WorldPosition * bary.y + patch[2].WorldPosition * bary.z;");
            sb.AppendLine("    OUT.WorldNormal = normalize(patch[0].WorldNormal * bary.x + patch[1].WorldNormal * bary.y + patch[2].WorldNormal * bary.z);");
            sb.AppendLine("    OUT.UV0 = patch[0].UV0 * bary.x + patch[1].UV0 * bary.y + patch[2].UV0 * bary.z;");
            sb.AppendLine("    OUT.ScreenPosition = mul(g_ViewProjMatrix, float4(OUT.WorldPosition, 1.0));");
            sb.Append(evalCode);
            sb.AppendLine("    return OUT;");
            sb.AppendLine("}");
            return sb.ToString();
        }

        // ── 生成几何着色器 ────────────────────────────────────

        /// <summary>生成几何着色器源码。</summary>
        public string GenerateGeometryShader(string evalCode = "") =>
            GenerateGeometryShaderSource(string.IsNullOrEmpty(evalCode) ? GenerateNodeEvaluation() : evalCode);

        private string GenerateGeometryShaderSource(string evalCode)
        {
            var sb = new StringBuilder();
            AppendHeader(sb);
            AppendDeclarations(sb);
            AppendStructures(sb);
            sb.AppendLine();
            sb.AppendLine("// =========================================================================");
            sb.AppendLine("// 几何着色器 (Geometry Shader)");
            sb.AppendLine("// =========================================================================");
            sb.AppendLine("[maxvertexcount(6)]");
            sb.AppendLine("void GSMain(triangle PSInput input[3], inout TriangleStream<PSInput> stream)");
            sb.AppendLine("{");
            sb.AppendLine("    for (int i = 0; i < 3; i++)");
            sb.AppendLine("        stream.Append(input[i]);");
            sb.AppendLine("    stream.RestartStrip();");
            sb.Append(evalCode);
            sb.AppendLine("}");
            return sb.ToString();
        }

        // ── 合并源码 ──────────────────────────────────────────

        /// <summary>构建合并的 HLSL 源码 (VS + PS 在同一文件)。</summary>
        private string BuildCombinedSource(string evalCode)
        {
            var sb = new StringBuilder();
            AppendHeader(sb);
            AppendDeclarations(sb);
            AppendStructures(sb);
            AppendHelperFunctions(sb);

            // VS
            sb.AppendLine();
            sb.AppendLine("PSInput VSMain(VSInput IN)");
            sb.AppendLine("{");
            sb.AppendLine("    PSInput OUT = (PSInput)0;");
            sb.AppendLine("    float4 worldPos = mul(g_WorldMatrix, float4(IN.ObjectPosition, 1.0));");
            sb.AppendLine("    OUT.WorldPosition = worldPos.xyz;");
            sb.AppendLine("    OUT.WorldNormal = normalize(mul((float3x3)g_WorldMatrix, IN.ObjectNormal));");
            sb.AppendLine("    OUT.WorldTangent = normalize(mul((float3x3)g_WorldMatrix, IN.Tangent.xyz));");
            sb.AppendLine("    OUT.WorldBitangent = cross(OUT.WorldNormal, OUT.WorldTangent) * IN.Tangent.w;");
            sb.AppendLine("    OUT.UV0 = IN.UV0; OUT.UV1 = IN.UV1; OUT.UV2 = IN.UV2; OUT.UV3 = IN.UV3;");
            sb.AppendLine("    OUT.Color = IN.Color;");
            sb.AppendLine("    OUT.ScreenPosition = mul(g_ViewProjMatrix, worldPos);");
            sb.AppendLine("    OUT.InstanceID = IN.InstanceID;");
            sb.AppendLine("    return OUT;");
            sb.AppendLine("}");

            // PS
            sb.AppendLine();
            sb.AppendLine("PSOutput PSMain(PSInput IN)");
            sb.AppendLine("{");
            sb.AppendLine("    PSOutput OUT = (PSOutput)0;");
            sb.AppendLine("    MaterialOutput mat = (MaterialOutput)0;");
            sb.AppendLine("    mat.Albedo = float3(1,1,1); mat.Roughness = 0.5; mat.Alpha = 1.0;");
            sb.AppendLine("    mat.Normal = IN.WorldNormal; mat.AO = 1.0;");
            sb.Append(evalCode);
            AppendShadingModel(sb);
            if (_graph.BlendMode == ShaderGraphBlendMode.Masked)
                sb.AppendLine("    clip(mat.Alpha - g_AlphaCutoff);");
            sb.AppendLine("    OUT.Color = float4(finalColor, mat.Alpha);");
            sb.AppendLine("    return OUT;");
            sb.AppendLine("}");

            return sb.ToString();
        }

        // ── 辅助函数库 ────────────────────────────────────────

        /// <summary>追加辅助函数 (噪声/数学/颜色空间转换等)。</summary>
        private void AppendHelperFunctions(StringBuilder sb)
        {
            sb.AppendLine();
            sb.AppendLine("// =========================================================================");
            sb.AppendLine("// 辅助函数库");
            sb.AppendLine("// =========================================================================");

            // 哈希
            sb.AppendLine("float2 Hash22(float2 p)");
            sb.AppendLine("{");
            sb.AppendLine("    p = float2(dot(p, float2(127.1, 311.7)), dot(p, float2(269.5, 183.3)));");
            sb.AppendLine("    return frac(sin(p) * 43758.5453);");
            sb.AppendLine("}");

            // Random01
            sb.AppendLine("float Random01(float2 uv)");
            sb.AppendLine("{");
            sb.AppendLine("    return frac(sin(dot(uv, float2(12.9898, 78.233))) * 43758.5453);");
            sb.AppendLine("}");

            // Perlin 噪声
            sb.AppendLine("float PerlinNoise2D(float2 uv)");
            sb.AppendLine("{");
            sb.AppendLine("    float2 i = floor(uv); float2 f = frac(uv);");
            sb.AppendLine("    float a = Random01(i);");
            sb.AppendLine("    float b = Random01(i + float2(1, 0));");
            sb.AppendLine("    float c = Random01(i + float2(0, 1));");
            sb.AppendLine("    float d = Random01(i + float2(1, 1));");
            sb.AppendLine("    float2 u = f * f * (3.0 - 2.0 * f);");
            sb.AppendLine("    return lerp(lerp(a, b, u.x), lerp(c, d, u.x), u.y);");
            sb.AppendLine("}");

            // Simplex 噪声 (简化)
            sb.AppendLine("float SimplexNoise2D(float2 uv) { return PerlinNoise2D(uv); }");
            sb.AppendLine("float ValueNoise2D(float2 uv) { return PerlinNoise2D(uv); }");

            // Worley/Cellular
            sb.AppendLine("float WorleyNoise2D(float2 uv)");
            sb.AppendLine("{");
            sb.AppendLine("    float2 i = floor(uv); float2 f = frac(uv);");
            sb.AppendLine("    float minDist = 1.0;");
            sb.AppendLine("    for (int y = -1; y <= 1; y++)");
            sb.AppendLine("        for (int x = -1; x <= 1; x++)");
            sb.AppendLine("        {");
            sb.AppendLine("            float2 neighbor = float2(x, y);");
            sb.AppendLine("            float2 point = Hash22(i + neighbor);");
            sb.AppendLine("            float2 diff = neighbor + point - f;");
            sb.AppendLine("            minDist = min(minDist, length(diff));");
            sb.AppendLine("        }");
            sb.AppendLine("    return minDist;");
            sb.AppendLine("}");
            sb.AppendLine("float2 CellularNoise2D(float2 uv)");
            sb.AppendLine("{");
            sb.AppendLine("    float2 i = floor(uv); float2 f = frac(uv);");
            sb.AppendLine("    float f1 = 1.0, f2 = 1.0;");
            sb.AppendLine("    for (int y = -1; y <= 1; y++)");
            sb.AppendLine("        for (int x = -1; x <= 1; x++)");
            sb.AppendLine("        {");
            sb.AppendLine("            float2 neighbor = float2(x, y);");
            sb.AppendLine("            float2 point = Hash22(i + neighbor);");
            sb.AppendLine("            float d = length(neighbor + point - f);");
            sb.AppendLine("            if (d < f1) { f2 = f1; f1 = d; } else if (d < f2) f2 = d;");
            sb.AppendLine("        }");
            sb.AppendLine("    return float2(f1, f2);");
            sb.AppendLine("}");

            // Voronoi
            sb.AppendLine("struct VoronoiResult { float Distance; float2 CellID; };");
            sb.AppendLine("VoronoiResult Voronoi(float2 uv)");
            sb.AppendLine("{");
            sb.AppendLine("    float2 i = floor(uv); float2 f = frac(uv);");
            sb.AppendLine("    float minDist = 1.0; float2 minPoint;");
            sb.AppendLine("    for (int y = -1; y <= 1; y++)");
            sb.AppendLine("        for (int x = -1; x <= 1; x++)");
            sb.AppendLine("        {");
            sb.AppendLine("            float2 neighbor = float2(x, y);");
            sb.AppendLine("            float2 point = Hash22(i + neighbor);");
            sb.AppendLine("            float d = length(neighbor + point - f);");
            sb.AppendLine("            if (d < minDist) { minDist = d; minPoint = i + neighbor; }");
            sb.AppendLine("        }");
            sb.AppendLine("    VoronoiResult r; r.Distance = minDist; r.CellID = minPoint;");
            sb.AppendLine("    return r;");
            sb.AppendLine("}");

            // FBM
            sb.AppendLine("float fbm(float2 uv, int octaves, float lacunarity, float gain)");
            sb.AppendLine("{");
            sb.AppendLine("    float sum = 0.0; float amp = 0.5; float freq = 1.0;");
            sb.AppendLine("    for (int i = 0; i < 8; i++)");
            sb.AppendLine("    {");
            sb.AppendLine("        if (i >= octaves) break;");
            sb.AppendLine("        sum += amp * PerlinNoise2D(uv * freq);");
            sb.AppendLine("        freq *= lacunarity; amp *= gain;");
            sb.AppendLine("    }");
            sb.AppendLine("    return sum;");
            sb.AppendLine("}");

            // Erosion / Curl
            sb.AppendLine("float Erosion(float2 uv, int iter) { return fbm(uv, iter, 2.0, 0.5); }");
            sb.AppendLine("float2 CurlNoise2D(float2 uv)");
            sb.AppendLine("{");
            sb.AppendLine("    float e = 0.01;");
            sb.AppendLine("    float n1 = PerlinNoise2D(uv + float2(e, 0));");
            sb.AppendLine("    float n2 = PerlinNoise2D(uv - float2(e, 0));");
            sb.AppendLine("    float n3 = PerlinNoise2D(uv + float2(0, e));");
            sb.AppendLine("    float n4 = PerlinNoise2D(uv - float2(0, e));");
            sb.AppendLine("    return float2((n3 - n4) / (2*e), -(n1 - n2) / (2*e));");
            sb.AppendLine("}");
            sb.AppendLine("float HexagonalNoise(float2 uv)");
            sb.AppendLine("{");
            sb.AppendLine("    uv.x *= 0.866;");
            sb.AppendLine("    return frac(floor(uv.x) + floor(uv.y));");
            sb.AppendLine("}");

            // 颜色空间
            sb.AppendLine("float3 RGBtoHSV(float3 c)");
            sb.AppendLine("{");
            sb.AppendLine("    float4 K = float4(0.0, -1.0/3.0, 2.0/3.0, -1.0);");
            sb.AppendLine("    float4 p = lerp(float4(c.bg, K.wz), float4(c.gb, K.xy), step(c.b, c.g));");
            sb.AppendLine("    float4 q = lerp(float4(p.xyw, c.r), float4(c.r, p.yzx), step(p.x, c.r));");
            sb.AppendLine("    float d = q.x - min(q.w, q.y);");
            sb.AppendLine("    float e = 1.0e-10;");
            sb.AppendLine("    return float3(abs(q.z + (q.w - q.y) / (6.0 * d + e)), d / (q.x + e), q.x);");
            sb.AppendLine("}");
            sb.AppendLine("float3 HSVtoRGB(float3 c)");
            sb.AppendLine("{");
            sb.AppendLine("    float4 K = float4(1.0, 2.0/3.0, 1.0/3.0, 3.0);");
            sb.AppendLine("    float3 p = abs(frac(c.xxx + K.xyz) * 6.0 - K.www);");
            sb.AppendLine("    return c.z * lerp(K.xxx, clamp(p - K.xxx, 0.0, 1.0), c.y);");
            sb.AppendLine("}");
            sb.AppendLine("float3 SRGBToLinear(float3 c) { return pow(c, 2.2); }");
            sb.AppendLine("float3 LinearToSRGB(float3 c) { return pow(c, 1.0/2.2); }");

            // 色调映射
            sb.AppendLine("float3 ACES_Tonemap(float3 x)");
            sb.AppendLine("{");
            sb.AppendLine("    float a = 2.51; float b = 0.03; float c = 2.43; float d = 0.59; float e = 0.14;");
            sb.AppendLine("    return clamp((x*(a*x+b))/(x*(c*x+d)+e), 0.0, 1.0);");
            sb.AppendLine("}");
            sb.AppendLine("float3 Uncharted2Tonemap(float3 x)");
            sb.AppendLine("{");
            sb.AppendLine("    float A=0.15,B=0.50,C=0.10,D=0.20,E=0.02,F=0.30;");
            sb.AppendLine("    return ((x*(A*x+C*B)+D*E)/(x*(A*x+B)+D*F))-E/F;");
            sb.AppendLine("}");

            // 混合模式辅助
            sb.AppendLine("float3 OverlayBlend(float3 a, float3 b, float o)");
            sb.AppendLine("{");
            sb.AppendLine("    float3 r = (a < 0.5) ? 2.0*a*b : 1.0 - 2.0*(1.0-a)*(1.0-b);");
            sb.AppendLine("    return lerp(a, r, o);");
            sb.AppendLine("}");
            sb.AppendLine("float3 SoftLightBlend(float3 a, float3 b, float o)");
            sb.AppendLine("{");
            sb.AppendLine("    float3 r = (b < 0.5) ? a - (1.0-2.0*b)*a*(1.0-a) : a + (2.0*b-1.0)*(sqrt(a)-a);");
            sb.AppendLine("    return lerp(a, r, o);");
            sb.AppendLine("}");
            sb.AppendLine("float3 HardLightBlend(float3 a, float3 b, float o)");
            sb.AppendLine("{");
            sb.AppendLine("    float3 r = (b < 0.5) ? 2.0*a*b : 1.0 - 2.0*(1.0-a)*(1.0-b);");
            sb.AppendLine("    return lerp(a, r, o);");
            sb.AppendLine("}");

            // BRDF 辅助
            sb.AppendLine("float FresnelSchlick(float cosTheta, float F0, float roughness)");
            sb.AppendLine("{");
            sb.AppendLine("    return F0 + (1.0 - F0) * pow(1.0 - cosTheta, 5.0 - roughness * 4.0);");
            sb.AppendLine("}");
            sb.AppendLine("float3 SubsurfaceScattering(float3 color, float thickness, float scale, float3 N, float3 V)");
            sb.AppendLine("{");
            sb.AppendLine("    float3 L = normalize(-g_DirLightDirection);");
            sb.AppendLine("    return color * scale * pow(saturate(dot(-L, V) + thickness), 4.0);");
            sb.AppendLine("}");
            sb.AppendLine("float3 SheenBRDF(float3 sheenColor, float roughness, float3 N, float3 V)");
            sb.AppendLine("{");
            sb.AppendLine("    float NdotV = max(dot(N, V), 0.0);");
            sb.AppendLine("    return sheenColor * pow(NdotV, 1.0 / (roughness + 0.1));");
            sb.AppendLine("}");
            sb.AppendLine("float3 ClearCoatBRDF(float3 coatColor, float coatRough, float strength, float3 N, float3 V)");
            sb.AppendLine("{");
            sb.AppendLine("    float NdotV = max(dot(N, V), 0.0);");
            sb.AppendLine("    return coatColor * strength * FresnelSchlick(NdotV, 0.04, coatRough);");
            sb.AppendLine("}");
            sb.AppendLine("float3 AnisotropicBRDF(float aniso, float3 T, float3 N, float3 V)");
            sb.AppendLine("{");
            sb.AppendLine("    return N * (1.0 - abs(aniso));");
            sb.AppendLine("}");
            sb.AppendLine("float3 Iridescence(float thickness, float3 V, float3 N)");
            sb.AppendLine("{");
            sb.AppendLine("    float f = cos(thickness * 0.01 + dot(N, V) * 6.28);");
            sb.AppendLine("    return float3(0.5 + 0.5*cos(f), 0.5 + 0.5*cos(f+2.094), 0.5 + 0.5*cos(f+4.188));");
            sb.AppendLine("}");

            // 矩阵辅助
            sb.AppendLine("float4x4 InverseMatrix4x4(float4x4 m)");
            sb.AppendLine("{");
            sb.AppendLine("    // 简化: 使用行主序求逆 (实际引擎中应使用完整实现)");
            sb.AppendLine("    return mul(m, g_InvViewMatrix); // 占位");
            sb.AppendLine("}");
            sb.AppendLine("float3x3 RotationMatrix3D(float3 axis, float angle)");
            sb.AppendLine("{");
            sb.AppendLine("    float c = cos(angle); float s = sin(angle); float t = 1.0 - c;");
            sb.AppendLine("    return float3x3(");
            sb.AppendLine("        t*axis.x*axis.x + c, t*axis.x*axis.y - s*axis.z, t*axis.x*axis.z + s*axis.y,");
            sb.AppendLine("        t*axis.x*axis.y + s*axis.z, t*axis.y*axis.y + c, t*axis.y*axis.z - s*axis.x,");
            sb.AppendLine("        t*axis.x*axis.z - s*axis.y, t*axis.y*axis.z + s*axis.x, t*axis.z*axis.z + c);");
            sb.AppendLine("}");

            // 三平面映射
            sb.AppendLine("float4 TriplanarSample(Texture2D tex, SamplerState samp, float3 pos, float3 normal, float scale, float blend)");
            sb.AppendLine("{");
            sb.AppendLine("    float3 p = pos * scale * 0.1;");
            sb.AppendLine("    float3 w = pow(abs(normal), blend);");
            sb.AppendLine("    w /= dot(w, 1.0);");
            sb.AppendLine("    float4 x = tex.Sample(samp, p.yz);");
            sb.AppendLine("    float4 y = tex.Sample(samp, p.xz);");
            sb.AppendLine("    float4 z = tex.Sample(samp, p.xy);");
            sb.AppendLine("    return x * w.x + y * w.y + z * w.z;");
            sb.AppendLine("}");

            // 聚光灯衰减
            sb.AppendLine("struct SpotLight { float3 Position; float3 Direction; float3 Color; float CosInner; float CosOuter; };");
            sb.AppendLine("float SpotLightAttenuation(SpotLight sl, float3 worldPos)");
            sb.AppendLine("{");
            sb.AppendLine("    float3 toLight = sl.Position - worldPos;");
            sb.AppendLine("    float dist = length(toLight);");
            sb.AppendLine("    float3 dir = toLight / dist;");
            sb.AppendLine("    float cosAngle = dot(dir, -sl.Direction);");
            sb.AppendLine("    float spot = smoothstep(sl.CosOuter, sl.CosInner, cosAngle);");
            sb.AppendLine("    return spot / (dist * dist + 1.0);");
            sb.AppendLine("}");

            // 反射探针
            sb.AppendLine("float3 SampleReflectionProbe(float3 reflVec, float roughness)");
            sb.AppendLine("{");
            sb.AppendLine("    return g_AmbientColor; // 占位: 实际从 IBL 环境贴图采样");
            sb.AppendLine("}");

            // POM
            sb.AppendLine("float2 ParallaxOcclusionMapping(Texture2D heightTex, SamplerState samp, float2 uv, float amplitude, int steps, float3 normal)");
            sb.AppendLine("{");
            sb.AppendLine("    float2 result = uv;");
            sb.AppendLine("    float stepSize = 1.0 / steps;");
            sb.AppendLine("    for (int i = 0; i < 16; i++)");
            sb.AppendLine("    {");
            sb.AppendLine("        if (i >= steps) break;");
            sb.AppendLine("        float h = heightTex.SampleLevel(samp, result, 0).r * amplitude;");
            sb.AppendLine("        result += normal.xy * stepSize * h;");
            sb.AppendLine("    }");
            sb.AppendLine("    return result;");
            sb.AppendLine("}");
        }

        // ── 文件头 ────────────────────────────────────────────

        /// <summary>追加文件头 (注释/ShaderModel/宏定义/include)。</summary>
        private void AppendHeader(StringBuilder sb)
        {
            sb.AppendLine("// =========================================================================");
            sb.AppendLine($"// yama引擎自动生成的 HLSL 着色器");
            sb.AppendLine($"// 图名称: {_graph.Name}");
            sb.AppendLine($"// 目标: {_graph.Target}");
            sb.AppendLine($"// 精度: {_graph.Precision}");
            sb.AppendLine($"// 混合模式: {_graph.BlendMode}");
            sb.AppendLine($"// 着色模型: {_graph.ShadingModel}");
            sb.AppendLine($"// ShaderModel: {ShaderModelString(_options.ShaderModel)}");
            sb.AppendLine($"// 生成时间: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
            sb.AppendLine("// =========================================================================");
            sb.AppendLine();

            // 宏定义
            foreach (var def in _options.Defines)
                sb.AppendLine($"#define {def.Key} {def.Value}");

            // 关键字
            foreach (var kw in _options.Keywords)
                sb.AppendLine($"#define {kw} 1");

            if (_options.Defines.Count > 0 || _options.Keywords.Count > 0)
                sb.AppendLine();

            // includes
            foreach (var inc in _options.Includes)
                sb.AppendLine($"#include \"{inc}\"");

            if (_options.Includes.Count > 0)
                sb.AppendLine();
        }

        // ── 工具方法 ──────────────────────────────────────────

        private void AppendDeclarations(StringBuilder sb)
        {
            if (_declBuilder.Length > 0)
            {
                sb.AppendLine("// ---- 声明 ----");
                sb.Append(_declBuilder.ToString());
                sb.AppendLine();
            }
        }

        private void AppendStructures(StringBuilder sb)
        {
            if (_structBuilder.Length > 0)
            {
                sb.AppendLine("// ---- 结构体 ----");
                sb.Append(_structBuilder.ToString());
            }
        }

        private void EmitDecl(string decl)
        {
            if (_declSet.Add(decl))
                _declBuilder.AppendLine(decl);
        }

        private void EmitStruct(string line)
        {
            _structBuilder.AppendLine(line);
        }

        /// <summary>分配输出引脚变量名。</summary>
        private string AllocVariable(ShaderGraphNode node, ShaderGraphPin pin)
        {
            return $"v_{node.Id.Substring(0, 6)}_{pin.Name}_{_varCounter++}";
        }

        /// <summary>获取输出引脚的变量名。</summary>
        private string GetPinVariable(string nodeId, string pinId)
        {
            var key = PinKey(nodeId, pinId);
            return _pinVarMap.TryGetValue(key, out var v) ? v : "0";
        }

        private static string PinKey(string nodeId, string pinId) => $"{nodeId}_{pinId}";

        /// <summary>ShaderModel 枚举 -> HLSL 目标字符串。</summary>
        public static string ShaderModelString(RHIShaderModel sm) => sm switch
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
            _ => "6_6"
        };

        /// <summary>获取着色器阶段目标字符串 (如 "vs_6_6")。</summary>
        public static string GetTargetProfile(RHIShaderStage stage, RHIShaderModel sm)
        {
            var smStr = ShaderModelString(sm);
            return stage switch
            {
                RHIShaderStage.Vertex => $"vs_{smStr}",
                RHIShaderStage.Pixel => $"ps_{smStr}",
                RHIShaderStage.Compute => $"cs_{smStr}",
                RHIShaderStage.Hull => $"hs_{smStr}",
                RHIShaderStage.Domain => $"ds_{smStr}",
                RHIShaderStage.Geometry => $"gs_{smStr}",
                RHIShaderStage.Mesh => $"ms_{smStr}",
                RHIShaderStage.Amplification => $"as_{smStr}",
                _ => $"ps_{smStr}"
            };
        }

        /// <summary>格式化代码 (统一缩进)。简单实现: 保持原样。</summary>
        public static string FormatHlsl(string source, string indent = "    ")
        {
            // 简单格式化: 移除多余空行
            var lines = source.Split('\n');
            var sb = new StringBuilder();
            int depth = 0;
            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("}")) depth = Math.Max(0, depth - 1);
                if (trimmed.Length > 0)
                {
                    for (int i = 0; i < depth; i++) sb.Append(indent);
                    sb.AppendLine(trimmed);
                }
                else
                {
                    sb.AppendLine();
                }
                if (trimmed.EndsWith("{")) depth++;
            }
            return sb.ToString();
        }
    }
}
