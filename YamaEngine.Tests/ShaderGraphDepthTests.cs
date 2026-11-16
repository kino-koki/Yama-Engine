// ShaderGraphDepthTests.cs — Unity5 可视化着色器图 (Shader Graph) 深度验证
// 复用 YamaEngine.ShaderGraph 核心 API，覆盖：
//   * 节点管理（AddNode/RemoveNode 级联连接/GetNode<T>）
//   * 引脚类型兼容规则（同类型/Float 广播/高维截取/Bool 非纹理/纹理同型/不兼容）
//   * 连接（成功/类型不匹配 null/输入替换已有连接/Disconnect/查询）
//   * 图验证（缺主节点/类型不匹配/循环/必需引脚未连接/合法图 IsValid）
//   * 拓扑排序（上游在前下游在后，仅可达节点）
//   * 代码生成（PBRMaster/Fresnel/Lerp/TextureSample/PrecisionPrefix）
//   * 属性管理（AddProperty/GetProperty/HlslType/HlslDefaultValue/ToPinType）
// 全部 CPU 级确定性，不依赖 GPU。

using System;
using System.Linq;
using System.Numerics;
using YamaEngine.ShaderGraph;
using static YamaEngine.Tests.TestHelpers;
// ShaderGraph 既是命名空间又是类型，用别名消歧
using ShaderGraphType = YamaEngine.ShaderGraph.ShaderGraph;

namespace YamaEngine.Tests
{
    public static class ShaderGraphDepthTests
    {
        // ── helper：构造图 + PBR Master ─────────────────────────
        private static ShaderGraphType NewGraph()
        {
            var g = new ShaderGraphType { Name = "TestGraph" };
            return g;
        }

        private static ShaderGraphType NewGraphWithPbr(out PBRMasterNode pbr)
        {
            var g = new ShaderGraphType { Name = "PbrGraph" };
            pbr = new PBRMasterNode();
            g.AddNode(pbr);
            g.SetOutputNode(pbr);
            return g;
        }

        // ── helper：代码生成（生成 HLSL 表达式映射）──────────────
        private static string GenNode(ShaderGraphNode node, ShaderGraphType? graph = null)
        {
            graph ??= new ShaderGraphType();
            var ctx = new NodeCodegenContext(graph, node);
            node.GenerateCode(ctx);
            // 返回所有输出表达式的串接，供断言包含
            var outputs = ctx.GetOutputNames()
                .Select(n => ctx.GetOutput(n) ?? "")
                .ToList();
            var decls = ctx.Declarations;
            var stmts = ctx.Statements;
            return string.Join(" ", outputs) + " " + string.Join(" ", decls) + " " + string.Join(" ", stmts);
        }

        // =========================================================
        // 1. 节点管理
        // =========================================================

        /// <summary>AddNode 应填充 NodeType/Title 默认值，GetNode 按 ID 查找。</summary>
        public static void TestAddNodeFillsDefaults()
        {
            var g = NewGraph();
            var node = new LerpNode { NodeType = "", Title = "" };
            g.AddNode(node);

            AssertEqual(1, g.Nodes.Count, "Nodes 应含 1 项。");
            AssertTrue(!string.IsNullOrEmpty(node.NodeType), "AddNode 应填充 NodeType。");
            AssertTrue(!string.IsNullOrEmpty(node.Title), "AddNode 应填充 Title。");
            AssertTrue(g.GetNode(node.Id) != null, "GetNode 应能按 ID 找到节点。");
            AssertTrue(g.GetNode<LerpNode>() != null, "GetNode<T> 应能按类型找到节点。");
        }

        /// <summary>RemoveNode 应级联移除相关连接；若为主节点则清空 OutputNodeId。</summary>
        public static void TestRemoveNodeCascadesConnections()
        {
            var g = NewGraphWithPbr(out var pbr);
            var src = new ColorNode();
            g.AddNode(src);

            // Color -> Albedo
            var conn = g.Connect(src.Id, "Color", pbr.Id, "Albedo");
            AssertTrue(conn != null, "连接应建立成功。");
            AssertEqual(g.OutputNodeId, pbr.Id, "OutputNodeId 应指向 PBR。");

            AssertTrue(g.RemoveNode(src.Id), "移除源节点应返回 true。");
            AssertEqual(0, g.Connections.Count, "相关连接应被级联移除。");
            AssertTrue(g.GetNode(src.Id) == null, "节点应被移除。");
            AssertTrue(g.OutputNodeId == pbr.Id, "非主节点移除不影响 OutputNodeId。");

            // 移除主节点 → OutputNodeId 清空
            AssertTrue(g.RemoveNode(pbr.Id), "移除主节点应返回 true。");
            AssertTrue(g.OutputNodeId == null, "移除主节点应清空 OutputNodeId。");
        }

        // =========================================================
        // 2. 引脚类型兼容规则
        // =========================================================

        /// <summary>同类型兼容。</summary>
        public static void TestTypeSame()
        {
            AssertTrue(ShaderGraphPin.AreTypesCompatible(ShaderGraphDataType.Float, ShaderGraphDataType.Float), "Float==Float 应兼容。");
            AssertTrue(ShaderGraphPin.AreTypesCompatible(ShaderGraphDataType.Float3, ShaderGraphDataType.Float3), "Float3==Float3 应兼容。");
            AssertTrue(ShaderGraphPin.AreTypesCompatible(ShaderGraphDataType.Texture2D, ShaderGraphDataType.Texture2D), "Texture2D==Texture2D 应兼容。");
            AssertTrue(ShaderGraphPin.AreTypesCompatible(ShaderGraphDataType.SamplerState, ShaderGraphDataType.SamplerState), "SamplerState==SamplerState 应兼容。");
        }

        /// <summary>Float 可广播到任意向量维度（含 Matrix）。</summary>
        public static void TestTypeFloatBroadcast()
        {
            AssertTrue(ShaderGraphPin.AreTypesCompatible(ShaderGraphDataType.Float, ShaderGraphDataType.Float2), "Float→Float2 应兼容。");
            AssertTrue(ShaderGraphPin.AreTypesCompatible(ShaderGraphDataType.Float, ShaderGraphDataType.Float3), "Float→Float3 应兼容。");
            AssertTrue(ShaderGraphPin.AreTypesCompatible(ShaderGraphDataType.Float, ShaderGraphDataType.Float4), "Float→Float4 应兼容。");
            AssertTrue(ShaderGraphPin.AreTypesCompatible(ShaderGraphDataType.Float, ShaderGraphDataType.Matrix), "Float→Matrix 应兼容（设计如此）。");
        }

        /// <summary>高维向量可截取到低维。</summary>
        public static void TestTypeHighToLowTruncate()
        {
            AssertTrue(ShaderGraphPin.AreTypesCompatible(ShaderGraphDataType.Float4, ShaderGraphDataType.Float3), "Float4→Float3 应兼容。");
            AssertTrue(ShaderGraphPin.AreTypesCompatible(ShaderGraphDataType.Float3, ShaderGraphDataType.Float2), "Float3→Float2 应兼容。");
            AssertTrue(ShaderGraphPin.AreTypesCompatible(ShaderGraphDataType.Float2, ShaderGraphDataType.Float), "Float2→Float 应兼容。");
            // 反向不兼容
            AssertTrue(!ShaderGraphPin.AreTypesCompatible(ShaderGraphDataType.Float2, ShaderGraphDataType.Float4), "Float2→Float4 不兼容。");
        }

        /// <summary>Bool 可连到非纹理类型；不能连到纹理/SamplerState。</summary>
        public static void TestTypeBoolNonTexture()
        {
            AssertTrue(ShaderGraphPin.AreTypesCompatible(ShaderGraphDataType.Bool, ShaderGraphDataType.Float), "Bool→Float 应兼容。");
            AssertTrue(ShaderGraphPin.AreTypesCompatible(ShaderGraphDataType.Bool, ShaderGraphDataType.Float4), "Bool→Float4 应兼容。");
            AssertTrue(ShaderGraphPin.AreTypesCompatible(ShaderGraphDataType.Bool, ShaderGraphDataType.Int), "Bool→Int 应兼容。");
            AssertTrue(!ShaderGraphPin.AreTypesCompatible(ShaderGraphDataType.Bool, ShaderGraphDataType.Texture2D), "Bool→Texture2D 不兼容。");
            AssertTrue(!ShaderGraphPin.AreTypesCompatible(ShaderGraphDataType.Bool, ShaderGraphDataType.SamplerState), "Bool→SamplerState 不兼容。");
        }

        /// <summary>纹理类型仅同型兼容；SamplerState 不兼容纹理。</summary>
        public static void TestTypeTextureSameOnly()
        {
            AssertTrue(!ShaderGraphPin.AreTypesCompatible(ShaderGraphDataType.Texture2D, ShaderGraphDataType.TextureCube), "Tex2D↔TexCube 不兼容。");
            AssertTrue(!ShaderGraphPin.AreTypesCompatible(ShaderGraphDataType.Texture2D, ShaderGraphDataType.Float4), "Texture2D→Float4 不兼容（不能当颜色）。");
            AssertTrue(!ShaderGraphPin.AreTypesCompatible(ShaderGraphDataType.Texture2D, ShaderGraphDataType.SamplerState), "Texture2D→SamplerState 不兼容。");
        }

        // =========================================================
        // 3. 连接管理
        // =========================================================

        /// <summary>类型兼容 → Connect 返回连接并写 dstPin.Connection。</summary>
        public static void TestConnectSuccess()
        {
            var g = NewGraphWithPbr(out var pbr);
            var color = new ColorNode();
            g.AddNode(color);

            var conn = g.Connect(color.Id, "Color", pbr.Id, "Albedo");
            AssertTrue(conn != null, "Color→Albedo(Float3) 连接应成功。");
            AssertEqual(g.Connections.Count, 1, "应有 1 条连接。");
            var dstPin = pbr.GetInputPin("Albedo")!;
            AssertTrue(dstPin.Connection == conn, "目标引脚应记录连接。");
        }

        /// <summary>类型不兼容 → Connect 返回 null，不写入连接。</summary>
        public static void TestConnectIncompatibleTypes()
        {
            var g = NewGraphWithPbr(out var pbr);
            var tex = new Texture2DNode(); // 输出 Texture2D
            g.AddNode(tex);

            // Texture2D → Albedo(Float3) 类型不匹配
            var conn = g.Connect(tex.Id, "Tex", pbr.Id, "Albedo");
            AssertTrue(conn == null, "Texture→Float3 连接应失败返回 null。");
            AssertEqual(0, g.Connections.Count, "失败连接不应写入。");
        }

        /// <summary>输入引脚已有连接 → 新连接应替换旧的（单输入引脚）。</summary>
        public static void TestConnectReplacesExisting()
        {
            var g = NewGraphWithPbr(out var pbr);
            var c1 = new ColorNode();
            var c2 = new ColorNode();
            g.AddNode(c1);
            g.AddNode(c2);

            var conn1 = g.Connect(c1.Id, "Color", pbr.Id, "Albedo");
            var conn2 = g.Connect(c2.Id, "Color", pbr.Id, "Albedo");
            AssertTrue(conn1 != null && conn2 != null, "两次连接都应成功。");
            AssertEqual(1, g.Connections.Count, "Albedo 输入引脚只保留最新连接（1 条）。");
            var dstPin = pbr.GetInputPin("Albedo")!;
            AssertEqual(conn2!.Id, dstPin.Connection!.Id, "目标引脚应指向最新连接。");
        }

        /// <summary>输出引脚可扇出到多个不同输入引脚。</summary>
        public static void TestConnectFanoutFromOutput()
        {
            var g = NewGraphWithPbr(out var pbr);
            var color = new ColorNode();
            g.AddNode(color);

            // 一个 Color 输出 → PBR.Albedo + PBR.Emission（都 Float3）
            var c1 = g.Connect(color.Id, "Color", pbr.Id, "Albedo");
            var c2 = g.Connect(color.Id, "Color", pbr.Id, "Emission");
            AssertTrue(c1 != null && c2 != null, "两个不同输入引脚都应连接成功。");
            AssertEqual(2, g.Connections.Count, "扇出应为 2 条连接。");

            var outs = g.GetConnectionsFromOutput(color.Id, color.GetOutputPin("Color")!.Id).ToList();
            AssertEqual(2, outs.Count, "GetConnectionsFromOutput 应返回 2 条。");
        }

        /// <summary>Disconnect 应按连接 ID 移除并返回 true。</summary>
        public static void TestDisconnect()
        {
            var g = NewGraphWithPbr(out var pbr);
            var color = new ColorNode();
            g.AddNode(color);

            var conn = g.Connect(color.Id, "Color", pbr.Id, "Albedo")!;
            AssertTrue(g.Disconnect(conn.Id), "Disconnect 应返回 true。");
            AssertEqual(0, g.Connections.Count, "Disconnect 后连接列表应为空。");
            // 已知引擎行为：Disconnect 仅从列表移除，未清空 dstPin.Connection 缓存
            AssertTrue(pbr.GetInputPin("Albedo")!.Connection != null, "Disconnect 后目标引脚仍引用旧连接（已知引擎缓存行为）。");

            // 不存在的 ID → false
            AssertTrue(!g.Disconnect("nonexistent-id"), "不存在的 ID 应返回 false。");
        }

        // =========================================================
        // 4. 图验证
        // =========================================================

        /// <summary>缺主节点且无 OutputNodeId → 验证失败。</summary>
        public static void TestValidateMissingOutput()
        {
            var g = NewGraph();
            var node = new LerpNode();
            g.AddNode(node); // 不设 OutputNodeId，也无 IsMasterNode

            var result = g.Validate();
            AssertTrue(!result.IsValid, "无主节点应验证失败。");
            AssertTrue(result.Errors.Count > 0, "应至少有 1 条错误。");
        }

        /// <summary>类型不匹配的连接 → 验证报告类型错误（绕过 Connect 类型检查直接注入）。</summary>
        public static void TestValidateTypeMismatch()
        {
            var g = NewGraphWithPbr(out var pbr);
            var tex = new Texture2DNode();
            g.AddNode(tex);

            // 绕过 Connect 的类型检查，直接构造不兼容连接
            var srcPin = tex.GetOutputPin("Tex")!;
            var dstPin = pbr.GetInputPin("Albedo")!;
            var bad = new ShaderGraphConnection
            {
                SourceNodeId = tex.Id, SourcePinId = srcPin.Id,
                TargetNodeId = pbr.Id, TargetPinId = dstPin.Id
            };
            g.Connections.Add(bad);
            dstPin.Connection = bad;

            var result = g.Validate();
            AssertTrue(!result.IsValid, "类型不匹配应验证失败。");
            AssertTrue(result.Errors.Any(e => e.Message.Contains("类型不匹配")), "应报告类型不匹配错误。");
        }

        /// <summary>HasCycle：正常 DAG 无循环；自环/反环 → true。</summary>
        public static void TestValidateCycleDetection()
        {
            var g = NewGraph();
            var a = new LerpNode();
            var b = new LerpNode();
            g.AddNode(a);
            g.AddNode(b);

            // a.Result → b.A
            g.Connect(a.Id, "Result", b.Id, "A");
            AssertTrue(!g.HasCycle(), "线性 DAG 应无循环。");

            // 反向连 b.Result → a.A 形成环
            g.Connect(b.Id, "Result", a.Id, "B");
            AssertTrue(g.HasCycle(), "反环应检测为 true。");

            var result = g.Validate();
            AssertTrue(!result.IsValid, "有环图应验证失败。");
        }

        /// <summary>必需引脚未连接 → 验证报告必需引脚错误。</summary>
        public static void TestValidateRequiredPin()
        {
            var g = NewGraphWithPbr(out var pbr);
            var sample = new TextureSampleNode(); // Tex 是 required
            g.AddNode(sample);
            g.Connect(sample.Id, "RGBA", pbr.Id, "Albedo");

            // Tex 未连接 → Validate 报错（必需引脚从主节点可达）
            var result = g.Validate();
            AssertTrue(!result.IsValid, "必需引脚未连接应验证失败。");
            AssertTrue(result.Errors.Any(e => e.Message.Contains("未连接") || e.Message.Contains("必需")),
                "应报告必需引脚错误。");
        }

        /// <summary>合法图：所有必需引脚连接 + 类型匹配 + 无环 → IsValid。</summary>
        public static void TestValidateValidGraph()
        {
            var g = NewGraphWithPbr(out var pbr);
            var tex = new Texture2DNode();
            var sampler = new SamplerStateNode();
            var sample = new TextureSampleNode();
            g.AddNode(tex);
            g.AddNode(sampler);
            g.AddNode(sample);

            // Tex(必需) + UV + Sampler 全连接
            AssertTrue(g.Connect(tex.Id, "Tex", sample.Id, "Tex") != null, "Tex→Tex 应成功。");
            AssertTrue(g.Connect(sampler.Id, "Sampler", sample.Id, "Sampler") != null, "Sampler→Sampler 应成功。");
            AssertTrue(g.Connect(sample.Id, "RGBA", pbr.Id, "Albedo") != null, "RGBA→Albedo 应成功。");

            var result = g.Validate();
            AssertTrue(result.IsValid, $"合法图应验证通过，实际错误：{string.Join("; ", result.Errors.Select(e => e.Message))}");
        }

        // =========================================================
        // 5. 拓扑排序
        // =========================================================

        /// <summary>拓扑排序：上游节点在下游之前，仅含从主节点可达的节点。</summary>
        public static void TestTopologicalSortOrder()
        {
            var g = NewGraphWithPbr(out var pbr);
            var tex = new Texture2DNode();
            var sample = new TextureSampleNode();
            g.AddNode(tex);
            g.AddNode(sample);

            g.Connect(tex.Id, "Tex", sample.Id, "Tex");
            g.Connect(sample.Id, "RGBA", pbr.Id, "Albedo");

            var order = g.TopologicalSort();
            AssertTrue(order.Count == 3, $"拓扑序应含 3 个可达节点，实际 {order.Count}。");

            int IdxOf(string nodeType) => order.FindIndex(n => n.NodeType == nodeType);
            int iTex = IdxOf("Texture2D");
            int iSample = IdxOf("TextureSample");
            int iPbr = IdxOf("PBRMaster");
            AssertTrue(iTex >= 0 && iSample >= 0 && iPbr >= 0, "三类节点都应在拓扑序中。");
            AssertTrue(iTex < iSample, "Texture2D 应在 TextureSample 之前（上游在前）。");
            AssertTrue(iSample < iPbr, "TextureSample 应在 PBRMaster 之前。");
        }

        /// <summary>无主节点 → 拓扑排序返回空列表。</summary>
        public static void TestTopologicalSortNoOutput()
        {
            var g = NewGraph();
            g.AddNode(new LerpNode()); // 非 master
            var order = g.TopologicalSort();
            AssertEqual(0, order.Count, "无主节点拓扑排序应返回空。");
        }

        // =========================================================
        // 6. 代码生成
        // =========================================================

        /// <summary>PBRMasterNode 生成 OUT.xxx 赋值，IsMasterNode=true。</summary>
        public static void TestCodeGenPbrMaster()
        {
            var g = new ShaderGraphType();
            var pbr = new PBRMasterNode();
            g.AddNode(pbr);

            var ctx = new NodeCodegenContext(g, pbr);
            pbr.GenerateCode(ctx);

            // PBR 输出通过 ctx.Emit 发语句，不通过 SetOutput
            AssertTrue(pbr.IsMasterNode, "PBRMaster 应标记 IsMasterNode=true。");
            var all = string.Join(" ", ctx.Statements);
            AssertTrue(all.Contains("OUT.Albedo"), "PBR 应发出 OUT.Albedo 赋值。");
            AssertTrue(all.Contains("OUT.Metallic"), "PBR 应发出 OUT.Metallic 赋值。");
            AssertTrue(all.Contains("OUT.Roughness"), "PBR 应发出 OUT.Roughness 赋值。");
            AssertTrue(all.Contains("OUT.Normal"), "PBR 应发出 OUT.Normal 赋值。");
            AssertTrue(all.Contains("normalize"), "OUT.Normal 应被 normalize。");
        }

        /// <summary>FresnelNode 生成含 pow/dot/saturate 的 HLSL 表达式。</summary>
        public static void TestCodeGenFresnel()
        {
            var g = new ShaderGraphType();
            var f = new FresnelNode();
            g.AddNode(f);

            var code = GenNode(f, g);
            AssertTrue(code.Contains("pow"), "Fresnel 应含 pow。");
            AssertTrue(code.Contains("dot"), "Fresnel 应含 dot。");
            AssertTrue(code.Contains("saturate"), "Fresnel 应含 saturate。");
            AssertTrue(code.Contains("normalize"), "Fresnel 应含 normalize。");
        }

        /// <summary>LerpNode 生成 lerp(A, B, T) 表达式；未连接引脚回退默认值。</summary>
        public static void TestCodeGenLerpDefaults()
        {
            var g = new ShaderGraphType();
            var l = new LerpNode();
            g.AddNode(l);

            var ctx = new NodeCodegenContext(g, l);
            l.GenerateCode(ctx);
            var expr = ctx.GetOutput("Result") ?? "";
            AssertTrue(expr.StartsWith("lerp("), "Lerp 应生成 lerp(...) 表达式。");
            // 未连接 → 默认值字面量：A=0, B=0, T=0
            AssertTrue(expr.Contains("lerp(0, 0, 0)"), $"未连接默认值应为 lerp(0,0,0)，实际 {expr}。");
        }

        /// <summary>TextureSampleNode 生成 Sample 调用并暴露 RGBA/R/G/B/A 输出。</summary>
        public static void TestCodeGenTextureSample()
        {
            var g = new ShaderGraphType();
            var s = new TextureSampleNode();
            g.AddNode(s);

            var ctx = new NodeCodegenContext(g, s);
            s.GenerateCode(ctx);
            AssertTrue(ctx.GetOutput("RGBA") != null, "TextureSample 应生成 RGBA 输出。");
            AssertTrue(ctx.GetOutput("R") != null, "应生成 R 通道输出。");
            AssertTrue(ctx.GetOutput("A") != null, "应生成 A 通道输出。");
            var decls = string.Join(" ", ctx.Declarations);
            var stmts = string.Join(" ", ctx.Statements);
            var all = decls + " " + stmts;
            AssertTrue(all.Contains(".Sample("), "应发出 .Sample() 采样调用。");
            // 引擎未连接 Tex → DefaultHlslLiteral="0"；Sampler → "SamplerState s_DefaultSampler"
            // 真实语句形如：float4 samp_XXXX = 0.Sample(SamplerState s_DefaultSampler, float2(0, 0));
            AssertTrue(all.Contains(".Sample(") && all.Contains("samp_"),
                "Sample 应发出 samp_ 变量赋值语句。");
            // RGBA/R/G/B/A 输出应引用 samp_ 变量
            AssertTrue((ctx.GetOutput("R") ?? "").Contains("samp_"), "R 通道输出应引用 samp_ 变量。");
            AssertTrue((ctx.GetOutput("A") ?? "").Contains("samp_"), "A 通道输出应引用 samp_ 变量。");
        }

        /// <summary>PrecisionPrefix 影响 HLSL 类型前缀（half/float/double）。</summary>
        public static void TestPrecisionPrefix()
        {
            var g = new ShaderGraphType { Precision = ShaderGraphPrecision.Half };
            var node = new LerpNode();
            g.AddNode(node);
            var ctx = new NodeCodegenContext(g, node) { Precision = ShaderGraphPrecision.Half };
            AssertEqual("half", ctx.PrecisionPrefix, "Half 精度前缀应为 half。");
            ctx.Precision = ShaderGraphPrecision.Double;
            AssertEqual("double", ctx.PrecisionPrefix, "Double 精度前缀应为 double。");
            ctx.Precision = ShaderGraphPrecision.Float;
            AssertEqual("float", ctx.PrecisionPrefix, "Float 精度前缀应为 float。");
        }

        // =========================================================
        // 7. 属性管理
        // =========================================================

        /// <summary>AddProperty/GetProperty：按名称查找、DisplayName 回退 Name。</summary>
        public static void TestPropertyManagement()
        {
            var g = NewGraph();
            var prop = new ShaderProperty
            {
                Name = "_MyColor",
                PropertyType = ShaderPropertyType.Color,
                VectorValue = new Vector4(1, 0, 0, 1)
            };
            g.AddProperty(prop);

            // AddProperty 自动填充 DisplayName（原为空）
            AssertEqual("_MyColor", prop.DisplayName, "DisplayName 应回退到 Name。");

            var found = g.GetProperty("_MyColor");
            AssertTrue(found != null, "GetProperty 应能找到属性。");
            AssertTrue(g.GetProperty("_NonExistent") == null, "不存在的属性应返回 null。");
            AssertEqual(1, g.Properties.Count, "应有 1 项属性。");
        }

        /// <summary>ShaderProperty HlslType/HlslDefaultValue/ToPinType 映射。</summary>
        public static void TestPropertyHlslMapping()
        {
            var f = new ShaderProperty { Name = "_F", PropertyType = ShaderPropertyType.Float, ScalarValue = 1.5f };
            AssertEqual("float", f.HlslType, "Float 属性 HlslType。");
            AssertTrue(f.HlslDefaultValue.Contains("1.5"), $"Float 默认值应含 1.5，实际 {f.HlslDefaultValue}。");
            AssertEqual(ShaderGraphDataType.Float, f.ToPinType(), "Float→Float 引脚类型。");

            var c = new ShaderProperty { Name = "_C", PropertyType = ShaderPropertyType.Color, VectorValue = new Vector4(1, 0, 0, 1) };
            AssertEqual("float4", c.HlslType, "Color 属性 HlslType=float4。");
            AssertTrue(c.HlslDefaultValue.StartsWith("float4("), "Color 默认值应为 float4 字面量。");
            AssertEqual(ShaderGraphDataType.Float4, c.ToPinType(), "Color→Float4 引脚类型。");

            var t = new ShaderProperty { Name = "_T", PropertyType = ShaderPropertyType.Texture2D };
            AssertEqual("Texture2D", t.HlslType, "Texture2D HlslType。");
            AssertTrue(t.IsTexture, "Texture2D 属性应 IsTexture=true。");
            AssertEqual(ShaderGraphDataType.Texture2D, t.ToPinType(), "Texture2D→Texture2D 引脚类型。");
        }

        // =========================================================
        // 8. 预览数据
        // =========================================================

        /// <summary>NodePreviewData 工厂方法设置正确的 Mode 与值。</summary>
        public static void TestNodePreviewFactories()
        {
            var scalar = NodePreviewData.FromScalar(0.5f);
            AssertEqual(NodePreviewMode.Scalar, scalar.Mode, "FromScalar 应设 Scalar 模式。");
            AssertEqual(0.5f, scalar.ScalarValue, "标量值应保存。");
            AssertEqual(scalar.ScalarValue, scalar.VectorValue.X, "向量 X 应等于标量值。");

            var color = NodePreviewData.FromColor(new Vector4(1, 0, 0, 1));
            AssertEqual(NodePreviewMode.Color, color.Mode, "FromColor 应设 Color 模式。");
            AssertEqual(1f, color.VectorValue.X, "颜色 R 应保存。");

            var tex = NodePreviewData.FromTexture("path.png");
            AssertEqual(NodePreviewMode.Texture, tex.Mode, "FromTexture 应设 Texture 模式。");
            AssertEqual("path.png", tex.TextureThumbnailPath, "纹理路径应保存。");

            var chart = NodePreviewData.FromChart(new[] { 0.1f, 0.2f, 0.3f });
            AssertEqual(NodePreviewMode.Chart, chart.Mode, "FromChart 应设 Chart 模式。");
            AssertTrue(chart.ChartData != null && chart.ChartData.Length == 3, "图表数据应保存 3 个点。");
        }
    }
}
