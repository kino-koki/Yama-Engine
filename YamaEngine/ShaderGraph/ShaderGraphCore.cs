// =============================================================================
//  Yama Engine - ShaderGraphCore.cs
//  yama引擎 - 可视化着色器图核心系统
//
//  本文件实现完整的可视化着色器图核心 (类似 UE Material Editor / Unity Shader Graph),
//  涵盖:
//    1. ShaderGraphDataType      —— 引脚数据类型枚举 (14 种)
//    2. ShaderGraphPinDirection  —— 引脚方向
//    3. ShaderGraphTarget        —— 着色器目标平台 (Universal/Forward/Deferred/...)
//    4. ShaderGraphPrecision     —— 精度 (Half/Float/Double)
//    5. ShaderGraphBlendMode     —— 混合模式 (Opaque/Masked/Translucent/...)
//    6. ShaderGraphShadingModel  —— 着色模型 (Unlit/Lit/SSS/Hair/Cloth/...)
//    7. ShaderGraphPin           —— 引脚 (ID/方向/类型/名称/默认值/连接)
//    8. ShaderGraphConnection    —— 连接 (源节点+引脚 -> 目标节点+引脚)
//    9. ShaderGraphNode          —— 节点基类 (ID/类型/位置/标题/引脚/代码生成/预览)
//   10. NodeCodegenContext       —— 节点代码生成上下文 (输入表达式/输出表达式/声明/语句)
//   11. ShaderProperty           —— 图属性 (名称/类型/默认值/范围/分组/排序/可见性/工具提示)
//   12. ShaderSubGraph           —— 子图 (可嵌套/参数传递/复用)
//   13. ShaderGraph              —— 着色器图 (节点/连接/属性/子图/输出/目标)
//   14. 图验证 (引脚类型匹配/循环检测/必须连接引脚/输出节点存在)
//   15. 图拓扑排序 (反向 DFS, 确定节点求值顺序)
//   16. 节点预览渲染 (实时预览输出结果)
//
//  设计原则:
//    * 所有数学使用 System.Numerics
//    * 不引入新的 NuGet 依赖
//    * 命名空间: YamaEngine.ShaderGraph
//    * 节点代码生成通过 NodeCodegenContext 解耦, 支持内联表达式与多语句输出
// =============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace YamaEngine.ShaderGraph
{
    // =========================================================================
    // 1. 引脚数据类型枚举
    // =========================================================================

    /// <summary>
    /// 着色器图引脚数据类型。覆盖 HLSL 常用标量/向量/矩阵/纹理/采样器类型。
    /// 类型兼容性规则:
    ///   * Float 可隐式提升为 Float2/Float3/Float4 (标量广播)
    ///   * 高维向量可截取到低维 (.xyz / .xy)
    ///   * Bool 可连接到任意非纹理类型 (静态开关)
    ///   * 纹理/SamplerState 仅同类型兼容
    /// </summary>
    public enum ShaderGraphDataType
    {
        /// <summary>标量 float</summary>
        Float,
        /// <summary>二维向量 float2</summary>
        Float2,
        /// <summary>三维向量 float3</summary>
        Float3,
        /// <summary>四维向量 float4</summary>
        Float4,
        /// <summary>4x4 矩阵 float4x4</summary>
        Matrix,
        /// <summary>2D 纹理 Texture2D</summary>
        Texture2D,
        /// <summary>立方体贴图 TextureCube</summary>
        TextureCube,
        /// <summary>2D 纹理数组 Texture2DArray</summary>
        Texture2DArray,
        /// <summary>采样器状态 SamplerState</summary>
        SamplerState,
        /// <summary>布尔值 bool</summary>
        Bool,
        /// <summary>整数 int</summary>
        Int,
        /// <summary>无符号整数 uint</summary>
        UInt,
        /// <summary>自定义结构体</summary>
        Struct,
        /// <summary>空类型 void (执行流引脚)</summary>
        Void
    }

    /// <summary>引脚方向。</summary>
    public enum ShaderGraphPinDirection
    {
        /// <summary>输入引脚: 接收数据。</summary>
        Input,
        /// <summary>输出引脚: 提供数据。</summary>
        Output
    }

    // =========================================================================
    // 2. 目标平台 / 精度 / 混合模式 / 着色模型 枚举
    // =========================================================================

    /// <summary>
    /// 着色器图目标平台/渲染路径。决定生成的 HLSL 结构与主输出节点。
    /// </summary>
    public enum ShaderGraphTarget
    {
        /// <summary>通用 (前向/延迟自适应)</summary>
        Universal,
        /// <summary>前向渲染</summary>
        Forward,
        /// <summary>延迟渲染</summary>
        Deferred,
        /// <summary>GeometryBuffer 输出</summary>
        GeometryBuffer,
        /// <summary>后处理</summary>
        PostProcess,
        /// <summary>计算着色器</summary>
        Compute,
        /// <summary>网格着色器</summary>
        Mesh,
        /// <summary>UI 渲染</summary>
        UI,
        /// <summary>粒子渲染</summary>
        Particle,
        /// <summary>贴花渲染</summary>
        Decal,
        /// <summary>纯 HLSL 函数 (不生成完整着色器, 仅生成可调用的 HLSL 函数)</summary>
        HLSL
    }

    /// <summary>着色器精度。影响生成 HLSL 中的标量/向量类型前缀。</summary>
    public enum ShaderGraphPrecision
    {
        /// <summary>半精度 half (移动端优化)</summary>
        Half,
        /// <summary>单精度 float (默认)</summary>
        Float,
        /// <summary>双精度 double (仅计算着色器)</summary>
        Double
    }

    /// <summary>材质混合模式。</summary>
    public enum ShaderGraphBlendMode
    {
        /// <summary>不透明</summary>
        Opaque,
        /// <summary>遮罩 (Alpha 测试)</summary>
        Masked,
        /// <summary>半透明</summary>
        Translucent,
        /// <summary>叠加</summary>
        Additive,
        /// <summary>调制</summary>
        Modulate,
        /// <summary>Alpha 合成</summary>
        AlphaComposite
    }

    /// <summary>着色模型 (决定 BRDF 计算)。</summary>
    public enum ShaderGraphShadingModel
    {
        /// <summary>无光照 (自发光)</summary>
        Unlit,
        /// <summary>标准光照</summary>
        Lit,
        /// <summary>次表面散射</summary>
        SubsurfaceScattering,
        /// <summary>头发</summary>
        Hair,
        /// <summary>布料</summary>
        Cloth,
        /// <summary>眼睛</summary>
        Eye,
        /// <summary>预积分皮肤</summary>
        PreintegratedSkin,
        /// <summary>双面</summary>
        DoubleSided,
        /// <summary>清漆层</summary>
        ClearCoat
    }

    // =========================================================================
    // 3. 着色器图引脚 (ShaderGraphPin)
    // =========================================================================

    /// <summary>
    /// 着色器图引脚。定义节点的一个输入或输出端口。
    /// 输入引脚最多接受一条连接; 输出引脚可扇出到多个输入引脚。
    /// </summary>
    public sealed class ShaderGraphPin
    {
        /// <summary>引脚唯一标识 (节点内唯一)。</summary>
        public string Id { get; set; } = Guid.NewGuid().ToString("N");

        /// <summary>引脚显示名称 (如 "BaseColor", "UV", "Result")。</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>引脚方向。</summary>
        public ShaderGraphPinDirection Direction { get; set; } = ShaderGraphPinDirection.Input;

        /// <summary>引脚数据类型。</summary>
        public ShaderGraphDataType DataType { get; set; } = ShaderGraphDataType.Float;

        /// <summary>浮点/向量默认值 (统一存储为 Vector4)。</summary>
        public Vector4 DefaultValue { get; set; } = new Vector4(0, 0, 0, 1);

        /// <summary>整数默认值 (Int/UInt 类型)。</summary>
        public int IntDefaultValue { get; set; }

        /// <summary>布尔默认值 (Bool 类型)。</summary>
        public bool BoolDefaultValue { get; set; }

        /// <summary>字符串默认值 (纹理路径/采样器名称)。</summary>
        public string StringDefaultValue { get; set; } = string.Empty;

        /// <summary>是否为必需连接 (未连接时编译报错)。</summary>
        public bool Required { get; set; }

        /// <summary>滑块范围下限。</summary>
        public float SliderMin { get; set; } = 0f;

        /// <summary>滑块范围上限。</summary>
        public float SliderMax { get; set; } = 1f;

        /// <summary>当前连接到此引脚的连接 (输入引脚最多一条)。</summary>
        public ShaderGraphConnection? Connection { get; set; }

        /// <summary>引脚预览数据。</summary>
        public NodePreviewData? Preview { get; set; }

        /// <summary>获取 HLSL 类型字符串。</summary>
        public string HlslType => DataTypeToHlsl(DataType);

        /// <summary>获取默认值的 HLSL 字面量。</summary>
        public string DefaultHlslLiteral => DataType switch
        {
            ShaderGraphDataType.Float => $"{DefaultValue.X:G9}",
            ShaderGraphDataType.Float2 => $"float2({DefaultValue.X:G9}, {DefaultValue.Y:G9})",
            ShaderGraphDataType.Float3 => $"float3({DefaultValue.X:G9}, {DefaultValue.Y:G9}, {DefaultValue.Z:G9})",
            ShaderGraphDataType.Float4 => $"float4({DefaultValue.X:G9}, {DefaultValue.Y:G9}, {DefaultValue.Z:G9}, {DefaultValue.W:G9})",
            ShaderGraphDataType.Bool => BoolDefaultValue ? "true" : "false",
            ShaderGraphDataType.Int => $"{IntDefaultValue}",
            ShaderGraphDataType.UInt => $"{(uint)IntDefaultValue}u",
            ShaderGraphDataType.Matrix => "float4x4(1,0,0,0, 0,1,0,0, 0,0,1,0, 0,0,0,1)",
            ShaderGraphDataType.SamplerState => "SamplerState s_DefaultSampler",
            _ => "0"
        };

        /// <summary>数据类型 -> HLSL 类型字符串。</summary>
        public static string DataTypeToHlsl(ShaderGraphDataType type) => type switch
        {
            ShaderGraphDataType.Float => "float",
            ShaderGraphDataType.Float2 => "float2",
            ShaderGraphDataType.Float3 => "float3",
            ShaderGraphDataType.Float4 => "float4",
            ShaderGraphDataType.Matrix => "float4x4",
            ShaderGraphDataType.Texture2D => "Texture2D",
            ShaderGraphDataType.TextureCube => "TextureCube",
            ShaderGraphDataType.Texture2DArray => "Texture2DArray",
            ShaderGraphDataType.SamplerState => "SamplerState",
            ShaderGraphDataType.Bool => "bool",
            ShaderGraphDataType.Int => "int",
            ShaderGraphDataType.UInt => "uint",
            ShaderGraphDataType.Struct => "ShaderStruct",
            ShaderGraphDataType.Void => "void",
            _ => "float"
        };

        /// <summary>获取数据类型维度 (标量=1, 向量=分量数, 其他=0)。</summary>
        public static int GetDimension(ShaderGraphDataType type) => type switch
        {
            ShaderGraphDataType.Float => 1,
            ShaderGraphDataType.Float2 => 2,
            ShaderGraphDataType.Float3 => 3,
            ShaderGraphDataType.Float4 => 4,
            ShaderGraphDataType.Bool => 1,
            ShaderGraphDataType.Int => 1,
            ShaderGraphDataType.UInt => 1,
            _ => 0
        };

        /// <summary>
        /// 检查两个引脚类型是否可连接。
        /// 规则: 同类型兼容; Float 可广播到高维; 高维可截取到低维; Bool 可连接非纹理。
        /// </summary>
        public static bool AreTypesCompatible(ShaderGraphDataType source, ShaderGraphDataType target)
        {
            if (source == target) return true;

            // Bool 可连接到任意非纹理类型
            if (source == ShaderGraphDataType.Bool && !IsTextureType(target) && target != ShaderGraphDataType.SamplerState)
                return true;

            // Float 可广播到任意向量维度
            if (source == ShaderGraphDataType.Float)
            {
                return target == ShaderGraphDataType.Float2 ||
                       target == ShaderGraphDataType.Float3 ||
                       target == ShaderGraphDataType.Float4 ||
                       target == ShaderGraphDataType.Matrix;
            }

            // 高维向量截取到低维
            int sourceDim = GetDimension(source);
            int targetDim = GetDimension(target);
            if (sourceDim > targetDim && targetDim > 0) return true;

            return false;
        }

        /// <summary>是否为纹理类型。</summary>
        public static bool IsTextureType(ShaderGraphDataType type) =>
            type == ShaderGraphDataType.Texture2D ||
            type == ShaderGraphDataType.TextureCube ||
            type == ShaderGraphDataType.Texture2DArray;
    }

    // =========================================================================
    // 4. 着色器图连接 (ShaderGraphConnection)
    // =========================================================================

    /// <summary>
    /// 着色器图连接: 从源节点的输出引脚到目标节点的输入引脚。
    /// </summary>
    public sealed class ShaderGraphConnection
    {
        /// <summary>连接唯一标识。</summary>
        public string Id { get; set; } = Guid.NewGuid().ToString("N");

        /// <summary>源节点 ID。</summary>
        public string SourceNodeId { get; set; } = string.Empty;

        /// <summary>源引脚 ID。</summary>
        public string SourcePinId { get; set; } = string.Empty;

        /// <summary>目标节点 ID。</summary>
        public string TargetNodeId { get; set; } = string.Empty;

        /// <summary>目标引脚 ID。</summary>
        public string TargetPinId { get; set; } = string.Empty;

        /// <summary>连接是否启用。</summary>
        public bool Enabled { get; set; } = true;
    }

    // =========================================================================
    // 5. 节点预览数据 (NodePreviewData)
    // =========================================================================

    /// <summary>
    /// 节点预览数据。携带节点输出值的可视化缩略图, 用于编辑器实时反馈。
    /// </summary>
    public sealed class NodePreviewData
    {
        /// <summary>预览模式。</summary>
        public NodePreviewMode Mode { get; set; } = NodePreviewMode.None;

        /// <summary>标量预览值。</summary>
        public float ScalarValue { get; set; }

        /// <summary>向量/颜色预览值。</summary>
        public Vector4 VectorValue { get; set; } = new Vector4(0, 0, 0, 1);

        /// <summary>纹理缩略图路径。</summary>
        public string? TextureThumbnailPath { get; set; }

        /// <summary>迷你图表数据 (最多 64 采样点)。</summary>
        public float[]? ChartData { get; set; }

        /// <summary>预览是否过期。</summary>
        public bool Dirty { get; set; } = true;

        public static NodePreviewData Empty => new NodePreviewData();

        public static NodePreviewData FromScalar(float v) => new NodePreviewData
        {
            Mode = NodePreviewMode.Scalar,
            ScalarValue = v,
            VectorValue = new Vector4(v, v, v, 1)
        };

        public static NodePreviewData FromColor(Vector4 c) => new NodePreviewData
        {
            Mode = NodePreviewMode.Color,
            VectorValue = c
        };

        public static NodePreviewData FromTexture(string path) => new NodePreviewData
        {
            Mode = NodePreviewMode.Texture,
            TextureThumbnailPath = path
        };

        public static NodePreviewData FromChart(float[] data) => new NodePreviewData
        {
            Mode = NodePreviewMode.Chart,
            ChartData = data
        };
    }

    /// <summary>节点预览显示模式。</summary>
    public enum NodePreviewMode
    {
        None,
        Scalar,
        Color,
        Texture,
        Chart
    }

    // =========================================================================
    // 6. 节点代码生成上下文 (NodeCodegenContext)
    // =========================================================================

    /// <summary>
    /// 节点代码生成上下文。在 HLSL 代码生成阶段传递给每个节点,
    /// 提供输入引脚的已求值表达式, 并收集节点输出的表达式与附加声明/语句。
    /// </summary>
    public sealed class NodeCodegenContext
    {
        /// <summary>所属着色器图。</summary>
        public ShaderGraph Graph { get; }

        /// <summary>当前节点。</summary>
        public ShaderGraphNode Node { get; }

        /// <summary>精度。</summary>
        public ShaderGraphPrecision Precision { get; set; } = ShaderGraphPrecision.Float;

        /// <summary>启用的着色器关键字 (用于变体)。</summary>
        public HashSet<string> Keywords { get; } = new HashSet<string>();

        /// <summary>附加的全局声明 (纹理/SamplerState/cbuffer 字段)。</summary>
        public List<string> Declarations { get; } = new List<string>();

        /// <summary>当前节点需要额外发出的语句 (在输出变量赋值之前)。</summary>
        public List<string> Statements { get; } = new List<string>();

        /// <summary>输入引脚表达式映射 (引脚名称 -> HLSL 表达式)。</summary>
        private readonly Dictionary<string, string> _inputs = new Dictionary<string, string>();

        /// <summary>输出引脚表达式映射 (引脚名称 -> HLSL 表达式)。</summary>
        private readonly Dictionary<string, string> _outputs = new Dictionary<string, string>();

        public NodeCodegenContext(ShaderGraph graph, ShaderGraphNode node)
        {
            Graph = graph;
            Node = node;
        }

        /// <summary>设置输入引脚表达式。</summary>
        public void SetInput(string pinName, string expr) => _inputs[pinName] = expr;

        /// <summary>
        /// 获取输入引脚的 HLSL 表达式。若未连接则返回默认值字面量。
        /// </summary>
        public string GetInput(string pinName)
        {
            if (_inputs.TryGetValue(pinName, out var expr)) return expr;
            // 回退到引脚默认值
            var pin = Node.GetInputPin(pinName);
            return pin?.DefaultHlslLiteral ?? "0";
        }

        /// <summary>获取输入引脚 (可能为 null)。</summary>
        public ShaderGraphPin? GetInputPinObj(string pinName) => Node.GetInputPin(pinName);

        /// <summary>尝试获取输入引脚的常量浮点值 (用于常量折叠)。</summary>
        public bool TryGetInputFloat(string pinName, out float value)
        {
            value = 0f;
            var pin = Node.GetInputPin(pinName);
            if (pin == null) return false;
            // 若有上游表达式且是字面量数字, 尝试解析
            if (_inputs.TryGetValue(pinName, out var expr))
            {
                expr = expr.Trim();
                // 去除括号
                while (expr.StartsWith("(") && expr.EndsWith(")"))
                    expr = expr.Substring(1, expr.Length - 2).Trim();
                if (float.TryParse(expr, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var f))
                {
                    value = f;
                    return true;
                }
                return false;
            }
            // 未连接, 使用默认值
            value = pin.DefaultValue.X;
            return true;
        }

        /// <summary>设置输出引脚表达式。</summary>
        public void SetOutput(string pinName, string expr) => _outputs[pinName] = expr;

        /// <summary>获取输出引脚表达式。</summary>
        public string? GetOutput(string pinName) =>
            _outputs.TryGetValue(pinName, out var expr) ? expr : null;

        /// <summary>获取所有已设置的输出引脚名称。</summary>
        public IEnumerable<string> GetOutputNames() => _outputs.Keys;

        /// <summary>发出附加语句。</summary>
        public void Emit(string statement) => Statements.Add(statement);

        /// <summary>发出全局声明。</summary>
        public void EmitDeclaration(string decl)
        {
            if (!Declarations.Contains(decl))
                Declarations.Add(decl);
        }

        /// <summary>精度前缀 (half/float/double)。</summary>
        public string PrecisionPrefix => Precision switch
        {
            ShaderGraphPrecision.Half => "half",
            ShaderGraphPrecision.Float => "float",
            ShaderGraphPrecision.Double => "double",
            _ => "float"
        };

        /// <summary>获取类型的精度化 HLSL 类型字符串。</summary>
        public string TypeOf(ShaderGraphDataType type) => type switch
        {
            ShaderGraphDataType.Float => PrecisionPrefix,
            ShaderGraphDataType.Float2 => PrecisionPrefix + "2",
            ShaderGraphDataType.Float3 => PrecisionPrefix + "3",
            ShaderGraphDataType.Float4 => PrecisionPrefix + "4",
            ShaderGraphDataType.Matrix => PrecisionPrefix + "4x4",
            ShaderGraphDataType.Bool => "bool",
            ShaderGraphDataType.Int => "int",
            ShaderGraphDataType.UInt => "uint",
            ShaderGraphDataType.Texture2D => "Texture2D",
            ShaderGraphDataType.TextureCube => "TextureCube",
            ShaderGraphDataType.Texture2DArray => "Texture2DArray",
            ShaderGraphDataType.SamplerState => "SamplerState",
            ShaderGraphDataType.Struct => "ShaderStruct",
            ShaderGraphDataType.Void => "void",
            _ => PrecisionPrefix
        };
    }

    // =========================================================================
    // 7. 着色器图节点基类 (ShaderGraphNode)
    // =========================================================================

    /// <summary>
    /// 着色器图节点基类。节点是着色器图的基本运算单元。
    /// 派生类通过重写 GenerateCode 实现节点 -> HLSL 代码生成。
    /// </summary>
    public class ShaderGraphNode
    {
        /// <summary>节点唯一标识。</summary>
        public string Id { get; set; } = Guid.NewGuid().ToString("N");

        /// <summary>节点类型名称 (如 "Add", "TextureSample", "PBRMaster")。</summary>
        public string NodeType { get; set; } = string.Empty;

        /// <summary>节点标题 (编辑器显示)。</summary>
        public string Title { get; set; } = string.Empty;

        /// <summary>节点描述 (tooltip)。</summary>
        public string Description { get; set; } = string.Empty;

        /// <summary>节点在画布上的位置。</summary>
        public Vector2 Position { get; set; }

        /// <summary>节点大小。</summary>
        public Vector2 Size { get; set; } = new Vector2(200, 150);

        /// <summary>节点是否启用。</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>节点是否折叠。</summary>
        public bool Collapsed { get; set; }

        /// <summary>颜色标签。</summary>
        public string ColorTag { get; set; } = string.Empty;

        /// <summary>输入引脚列表。</summary>
        public List<ShaderGraphPin> InputPins { get; set; } = new List<ShaderGraphPin>();

        /// <summary>输出引脚列表。</summary>
        public List<ShaderGraphPin> OutputPins { get; set; } = new List<ShaderGraphPin>();

        /// <summary>节点预览。</summary>
        public NodePreviewData NodePreview { get; set; } = NodePreviewData.Empty;

        /// <summary>自定义元数据。</summary>
        public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();

        /// <summary>注释文本。</summary>
        public string Comment { get; set; } = string.Empty;

        // ── 引脚管理便捷方法 ──────────────────────────────────────

        /// <summary>添加输入引脚。</summary>
        public ShaderGraphPin AddInput(string name, ShaderGraphDataType dataType,
            Vector4 defaultValue = default, bool required = false)
        {
            var pin = new ShaderGraphPin
            {
                Id = $"{Id}_in_{name}",
                Name = name,
                Direction = ShaderGraphPinDirection.Input,
                DataType = dataType,
                DefaultValue = defaultValue == default ? new Vector4(0, 0, 0, 1) : defaultValue,
                Required = required
            };
            InputPins.Add(pin);
            return pin;
        }

        /// <summary>添加输出引脚。</summary>
        public ShaderGraphPin AddOutput(string name, ShaderGraphDataType dataType,
            Vector4 defaultValue = default)
        {
            var pin = new ShaderGraphPin
            {
                Id = $"{Id}_out_{name}",
                Name = name,
                Direction = ShaderGraphPinDirection.Output,
                DataType = dataType,
                DefaultValue = defaultValue == default ? new Vector4(0, 0, 0, 1) : defaultValue
            };
            OutputPins.Add(pin);
            return pin;
        }

        /// <summary>按名称查找输入引脚。</summary>
        public ShaderGraphPin? GetInputPin(string name) =>
            InputPins.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

        /// <summary>按名称查找输出引脚。</summary>
        public ShaderGraphPin? GetOutputPin(string name) =>
            OutputPins.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

        /// <summary>按 ID 查找引脚。</summary>
        public ShaderGraphPin? FindPinById(string pinId) =>
            InputPins.FirstOrDefault(p => p.Id == pinId) ??
            OutputPins.FirstOrDefault(p => p.Id == pinId);

        // ── 元数据便捷访问 ──────────────────────────────────────

        public float GetFloatMeta(string key, float def = 0f) =>
            Metadata.TryGetValue(key, out var v) && v is float f ? f : def;
        public void SetFloatMeta(string key, float v) => Metadata[key] = v;
        public Vector4 GetVectorMeta(string key, Vector4 def = default) =>
            Metadata.TryGetValue(key, out var v) && v is Vector4 vec ? vec : def;
        public void SetVectorMeta(string key, Vector4 v) => Metadata[key] = v;
        public string GetStringMeta(string key, string def = "") =>
            Metadata.TryGetValue(key, out var v) && v is string s ? s : def;
        public void SetStringMeta(string key, string v) => Metadata[key] = v;
        public bool GetBoolMeta(string key, bool def = false) =>
            Metadata.TryGetValue(key, out var v) && v is bool b ? b : def;
        public void SetBoolMeta(string key, bool v) => Metadata[key] = v;
        public int GetIntMeta(string key, int def = 0) =>
            Metadata.TryGetValue(key, out var v) && v is int i ? i : def;
        public void SetIntMeta(string key, int v) => Metadata[key] = v;

        // ── 代码生成 (派生类重写) ────────────────────────────────

        /// <summary>
        /// 生成 HLSL 代码。派生节点通过 ctx.GetInput 读取输入表达式,
        /// 通过 ctx.SetOutput 设置输出表达式, 通过 ctx.Emit 发出附加语句。
        /// </summary>
        public virtual void GenerateCode(NodeCodegenContext ctx)
        {
            // 默认实现: 直接传递第一个输入到第一个输出
            if (OutputPins.Count > 0)
            {
                var expr = InputPins.Count > 0 ? ctx.GetInput(InputPins[0].Name) : "0";
                ctx.SetOutput(OutputPins[0].Name, expr);
            }
        }

        /// <summary>
        /// 计算节点预览 (用于编辑器实时反馈)。默认实现返回默认颜色。
        /// 派生类可重写以提供 CPU 端预览求值。
        /// </summary>
        public virtual NodePreviewData ComputePreview()
        {
            return NodePreview ?? NodePreviewData.Empty;
        }

        /// <summary>节点是否为输出/主节点 (图的终点)。</summary>
        public virtual bool IsMasterNode => false;
    }

    // =========================================================================
    // 8. 着色器图属性 (ShaderProperty)
    // =========================================================================

    /// <summary>着色器图属性类型。</summary>
    public enum ShaderPropertyType
    {
        /// <summary>标量浮点</summary>
        Float,
        /// <summary>向量 float3/float4</summary>
        Vector,
        /// <summary>颜色 float4</summary>
        Color,
        /// <summary>2D 纹理</summary>
        Texture2D,
        /// <summary>立方体贴图</summary>
        TextureCube,
        /// <summary>2D 纹理数组</summary>
        Texture2DArray,
        /// <summary>静态开关 bool</summary>
        StaticBool,
        /// <summary>静态开关组 uint</summary>
        StaticSwitch,
        /// <summary>整数</summary>
        Int
    }

    /// <summary>
    /// 着色器图属性。定义材质图中可由外部设置的参数。
    /// 属性在编译后写入 cbuffer, 运行时通过 RHI 更新。
    /// </summary>
    public sealed class ShaderProperty
    {
        /// <summary>属性唯一标识。</summary>
        public string Id { get; set; } = Guid.NewGuid().ToString("N");

        /// <summary>属性名称 (cbuffer 字段名, 合法 HLSL 标识符)。</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>显示名称 (编辑器中)。</summary>
        public string DisplayName { get; set; } = string.Empty;

        /// <summary>工具提示。</summary>
        public string Tooltip { get; set; } = string.Empty;

        /// <summary>属性类型。</summary>
        public ShaderPropertyType PropertyType { get; set; } = ShaderPropertyType.Float;

        /// <summary>标量默认值。</summary>
        public float ScalarValue { get; set; }

        /// <summary>向量/颜色默认值。</summary>
        public Vector4 VectorValue { get; set; } = new Vector4(0, 0, 0, 1);

        /// <summary>纹理资源路径。</summary>
        public string TexturePath { get; set; } = string.Empty;

        /// <summary>布尔默认值。</summary>
        public bool BoolValue { get; set; }

        /// <summary>整数默认值。</summary>
        public int IntValue { get; set; }

        /// <summary>范围下限。</summary>
        public float MinValue { get; set; } = 0f;

        /// <summary>范围上限。</summary>
        public float MaxValue { get; set; } = 1f;

        /// <summary>是否使用范围 (滑块)。</summary>
        public bool UseRange { get; set; }

        /// <summary>所属分组。</summary>
        public string Group { get; set; } = "Default";

        /// <summary>排序索引。</summary>
        public int SortOrder { get; set; }

        /// <summary>是否可见。</summary>
        public bool Visible { get; set; } = true;

        /// <summary>对应的引脚数据类型。</summary>
        public ShaderGraphDataType ToPinType() => PropertyType switch
        {
            ShaderPropertyType.Float => ShaderGraphDataType.Float,
            ShaderPropertyType.Vector => ShaderGraphDataType.Float4,
            ShaderPropertyType.Color => ShaderGraphDataType.Float4,
            ShaderPropertyType.Texture2D => ShaderGraphDataType.Texture2D,
            ShaderPropertyType.TextureCube => ShaderGraphDataType.TextureCube,
            ShaderPropertyType.Texture2DArray => ShaderGraphDataType.Texture2DArray,
            ShaderPropertyType.StaticBool => ShaderGraphDataType.Bool,
            ShaderPropertyType.StaticSwitch => ShaderGraphDataType.UInt,
            ShaderPropertyType.Int => ShaderGraphDataType.Int,
            _ => ShaderGraphDataType.Float
        };

        /// <summary>HLSL 类型字符串。</summary>
        public string HlslType => PropertyType switch
        {
            ShaderPropertyType.Float => "float",
            ShaderPropertyType.Vector => "float4",
            ShaderPropertyType.Color => "float4",
            ShaderPropertyType.Texture2D => "Texture2D",
            ShaderPropertyType.TextureCube => "TextureCube",
            ShaderPropertyType.Texture2DArray => "Texture2DArray",
            ShaderPropertyType.StaticBool => "bool",
            ShaderPropertyType.StaticSwitch => "uint",
            ShaderPropertyType.Int => "int",
            _ => "float"
        };

        /// <summary>HLSL 默认值字面量。</summary>
        public string HlslDefaultValue => PropertyType switch
        {
            ShaderPropertyType.Float => $"{ScalarValue:G9}",
            ShaderPropertyType.Vector => $"float4({VectorValue.X:G9}, {VectorValue.Y:G9}, {VectorValue.Z:G9}, {VectorValue.W:G9})",
            ShaderPropertyType.Color => $"float4({VectorValue.X:G9}, {VectorValue.Y:G9}, {VectorValue.Z:G9}, {VectorValue.W:G9})",
            ShaderPropertyType.StaticBool => BoolValue ? "true" : "false",
            ShaderPropertyType.StaticSwitch => $"{(uint)IntValue}u",
            ShaderPropertyType.Int => $"{IntValue}",
            _ => "0"
        };

        /// <summary>是否为纹理类属性。</summary>
        public bool IsTexture => PropertyType == ShaderPropertyType.Texture2D ||
                                PropertyType == ShaderPropertyType.TextureCube ||
                                PropertyType == ShaderPropertyType.Texture2DArray;
    }

    // =========================================================================
    // 9. 子图 (ShaderSubGraph)
    // =========================================================================

    /// <summary>
    /// 着色器子图。可嵌套复用的着色器图片段, 通过参数节点接收输入,
    /// 通过输出节点返回结果。子图在编译时内联展开。
    /// </summary>
    public sealed class ShaderSubGraph
    {
        /// <summary>子图唯一标识 (GUID)。</summary>
        public string Guid { get; set; } = System.Guid.NewGuid().ToString("N");

        /// <summary>子图名称。</summary>
        public string Name { get; set; } = "SubGraph";

        /// <summary>子图描述。</summary>
        public string Description { get; set; } = string.Empty;

        /// <summary>子图文件路径 (相对/绝对)。</summary>
        public string FilePath { get; set; } = string.Empty;

        /// <summary>子图输入参数定义 (参数名 -> 类型)。</summary>
        public List<ShaderSubGraphParameter> Parameters { get; set; } = new List<ShaderSubGraphParameter>();

        /// <summary>子图输出定义。</summary>
        public List<ShaderSubGraphOutput> Outputs { get; set; } = new List<ShaderSubGraphOutput>();

        /// <summary>子图内部的着色器图 (展开后使用)。</summary>
        public ShaderGraph? Graph { get; set; }

        /// <summary>是否已加载。</summary>
        public bool IsLoaded => Graph != null;
    }

    /// <summary>子图参数定义。</summary>
    public sealed class ShaderSubGraphParameter
    {
        public string Name { get; set; } = string.Empty;
        public ShaderGraphDataType DataType { get; set; } = ShaderGraphDataType.Float;
        public Vector4 DefaultValue { get; set; } = new Vector4(0, 0, 0, 1);
        public string Description { get; set; } = string.Empty;
    }

    /// <summary>子图输出定义。</summary>
    public sealed class ShaderSubGraphOutput
    {
        public string Name { get; set; } = string.Empty;
        public ShaderGraphDataType DataType { get; set; } = ShaderGraphDataType.Float;
        public string Description { get; set; } = string.Empty;
    }

    // =========================================================================
    // 10. 验证结果 (ShaderGraphValidationResult)
    // =========================================================================

    /// <summary>验证错误严重级别。</summary>
    public enum ValidationErrorLevel
    {
        Info,
        Warning,
        Error
    }

    /// <summary>验证错误信息。</summary>
    public sealed class ValidationError
    {
        public ValidationErrorLevel Level { get; set; }
        public string NodeId { get; set; } = string.Empty;
        public string PinId { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public int Line { get; set; }

        public override string ToString() =>
            $"[{Level}] {(string.IsNullOrEmpty(NodeId) ? "" : $"Node {NodeId}: ")}{Message}";
    }

    /// <summary>图验证结果。</summary>
    public sealed class ShaderGraphValidationResult
    {
        public bool IsValid => !Errors.Any(e => e.Level == ValidationErrorLevel.Error);
        public List<ValidationError> Errors { get; set; } = new List<ValidationError>();

        public void AddError(string nodeId, string message, string pinId = "") =>
            Errors.Add(new ValidationError
            {
                Level = ValidationErrorLevel.Error,
                NodeId = nodeId,
                PinId = pinId,
                Message = message
            });

        public void AddWarning(string nodeId, string message, string pinId = "") =>
            Errors.Add(new ValidationError
            {
                Level = ValidationErrorLevel.Warning,
                NodeId = nodeId,
                PinId = pinId,
                Message = message
            });

        public void AddInfo(string nodeId, string message) =>
            Errors.Add(new ValidationError
            {
                Level = ValidationErrorLevel.Info,
                NodeId = nodeId,
                Message = message
            });
    }

    // =========================================================================
    // 11. 着色器图 (ShaderGraph)
    // =========================================================================

    /// <summary>
    /// 着色器图。包含节点、连接、属性、子图、输出节点和目标平台设置。
    /// 提供图验证、拓扑排序、预览渲染等核心功能。
    /// </summary>
    public sealed class ShaderGraph
    {
        /// <summary>图唯一标识。</summary>
        public string Guid { get; set; } = System.Guid.NewGuid().ToString("N");

        /// <summary>图名称。</summary>
        public string Name { get; set; } = "ShaderGraph";

        /// <summary>图描述。</summary>
        public string Description { get; set; } = string.Empty;

        /// <summary>目标平台。</summary>
        public ShaderGraphTarget Target { get; set; } = ShaderGraphTarget.Universal;

        /// <summary>精度。</summary>
        public ShaderGraphPrecision Precision { get; set; } = ShaderGraphPrecision.Float;

        /// <summary>混合模式。</summary>
        public ShaderGraphBlendMode BlendMode { get; set; } = ShaderGraphBlendMode.Opaque;

        /// <summary>着色模型。</summary>
        public ShaderGraphShadingModel ShadingModel { get; set; } = ShaderGraphShadingModel.Lit;

        /// <summary>是否双面渲染。</summary>
        public bool DoubleSided { get; set; }

        /// <summary>是否开启线框模式。</summary>
        public bool Wireframe { get; set; }

        /// <summary>Alpha 截断阈值 (BlendMode == Masked)。</summary>
        public float AlphaCutoff { get; set; } = 0.5f;

        /// <summary>节点列表。</summary>
        public List<ShaderGraphNode> Nodes { get; set; } = new List<ShaderGraphNode>();

        /// <summary>连接列表。</summary>
        public List<ShaderGraphConnection> Connections { get; set; } = new List<ShaderGraphConnection>();

        /// <summary>属性列表。</summary>
        public List<ShaderProperty> Properties { get; set; } = new List<ShaderProperty>();

        /// <summary>子图引用列表。</summary>
        public List<ShaderSubGraph> SubGraphs { get; set; } = new List<ShaderSubGraph>();

        /// <summary>着色器关键字 (用于变体生成)。</summary>
        public List<string> Keywords { get; set; } = new List<string>();

        /// <summary>输出节点 ID (图的终点)。</summary>
        public string? OutputNodeId { get; set; }

        // ── 节点/连接管理 ──────────────────────────────────────

        /// <summary>添加节点。</summary>
        public void AddNode(ShaderGraphNode node)
        {
            if (node == null) throw new ArgumentNullException(nameof(node));
            if (string.IsNullOrEmpty(node.NodeType))
                node.NodeType = node.GetType().Name;
            if (string.IsNullOrEmpty(node.Title))
                node.Title = node.NodeType;
            Nodes.Add(node);
        }

        /// <summary>移除节点 (同时移除相关连接)。</summary>
        public bool RemoveNode(string nodeId)
        {
            var node = Nodes.FirstOrDefault(n => n.Id == nodeId);
            if (node == null) return false;
            Nodes.Remove(node);
            // 移除相关连接
            Connections.RemoveAll(c =>
                c.SourceNodeId == nodeId || c.TargetNodeId == nodeId);
            if (OutputNodeId == nodeId) OutputNodeId = null;
            return true;
        }

        /// <summary>按 ID 查找节点。</summary>
        public ShaderGraphNode? GetNode(string nodeId) =>
            Nodes.FirstOrDefault(n => n.Id == nodeId);

        /// <summary>按类型查找节点。</summary>
        public T? GetNode<T>() where T : ShaderGraphNode =>
            Nodes.OfType<T>().FirstOrDefault();

        /// <summary>获取主输出节点。</summary>
        public ShaderGraphNode? GetOutputNode() =>
            string.IsNullOrEmpty(OutputNodeId) ? null : GetNode(OutputNodeId);

        /// <summary>添加连接 (自动校验类型兼容性)。</summary>
        public ShaderGraphConnection? Connect(
            string sourceNodeId, string sourcePinName,
            string targetNodeId, string targetPinName)
        {
            var srcNode = GetNode(sourceNodeId);
            var dstNode = GetNode(targetNodeId);
            if (srcNode == null || dstNode == null) return null;

            var srcPin = srcNode.GetOutputPin(sourcePinName);
            var dstPin = dstNode.GetInputPin(targetPinName);
            if (srcPin == null || dstPin == null) return null;

            // 类型检查
            if (!ShaderGraphPin.AreTypesCompatible(srcPin.DataType, dstPin.DataType))
                return null;

            // 输入引脚已有连接则替换
            var existing = Connections.FirstOrDefault(c =>
                c.TargetNodeId == targetNodeId && c.TargetPinId == dstPin.Id);
            if (existing != null)
            {
                Connections.Remove(existing);
                var oldSrc = GetNode(existing.SourceNodeId);
                oldSrc?.GetOutputPin(existing.SourcePinId);
            }

            var conn = new ShaderGraphConnection
            {
                SourceNodeId = sourceNodeId,
                SourcePinId = srcPin.Id,
                TargetNodeId = targetNodeId,
                TargetPinId = dstPin.Id
            };
            Connections.Add(conn);
            dstPin.Connection = conn;
            return conn;
        }

        /// <summary>断开连接。</summary>
        public bool Disconnect(string connectionId)
        {
            var conn = Connections.FirstOrDefault(c => c.Id == connectionId);
            if (conn == null) return false;
            Connections.Remove(conn);
            var dstNode = GetNode(conn.TargetNodeId);
            return true;
        }

        /// <summary>获取连接到指定输入引脚的连接。</summary>
        public ShaderGraphConnection? GetConnectionToInput(string nodeId, string pinId) =>
            Connections.FirstOrDefault(c => c.TargetNodeId == nodeId && c.TargetPinId == pinId && c.Enabled);

        /// <summary>获取从指定输出引脚出发的所有连接。</summary>
        public IEnumerable<ShaderGraphConnection> GetConnectionsFromOutput(string nodeId, string pinId) =>
            Connections.Where(c => c.SourceNodeId == nodeId && c.SourcePinId == pinId && c.Enabled);

        // ── 属性管理 ──────────────────────────────────────────

        /// <summary>添加属性。</summary>
        public void AddProperty(ShaderProperty prop)
        {
            if (prop == null) throw new ArgumentNullException(nameof(prop));
            if (string.IsNullOrEmpty(prop.DisplayName))
                prop.DisplayName = prop.Name;
            Properties.Add(prop);
        }

        /// <summary>按名称查找属性。</summary>
        public ShaderProperty? GetProperty(string name) =>
            Properties.FirstOrDefault(p => p.Name == name);

        // ── 子图管理 ──────────────────────────────────────────

        /// <summary>添加子图引用。</summary>
        public void AddSubGraph(ShaderSubGraph sub) => SubGraphs.Add(sub);

        // ── 图验证 ────────────────────────────────────────────

        /// <summary>
        /// 验证着色器图。检查:
        ///   1. 输出节点存在
        ///   2. 引脚类型匹配
        ///   3. 循环检测
        ///   4. 必需连接引脚
        /// </summary>
        public ShaderGraphValidationResult Validate()
        {
            var result = new ShaderGraphValidationResult();

            // 1. 输出节点存在
            var outputNode = GetOutputNode();
            if (outputNode == null)
            {
                // 尝试查找主节点
                var master = Nodes.FirstOrDefault(n => n.IsMasterNode);
                if (master != null)
                {
                    OutputNodeId = master.Id;
                    outputNode = master;
                }
                else
                {
                    result.AddError("", "着色器图缺少输出/主节点。");
                    return result;
                }
            }

            // 2. 检查所有连接的类型匹配与引用有效性
            foreach (var conn in Connections)
            {
                if (!conn.Enabled) continue;
                var srcNode = GetNode(conn.SourceNodeId);
                var dstNode = GetNode(conn.TargetNodeId);
                if (srcNode == null)
                {
                    result.AddError(conn.TargetNodeId, $"连接的源节点 {conn.SourceNodeId} 不存在。", conn.TargetPinId);
                    continue;
                }
                if (dstNode == null)
                {
                    result.AddError(conn.SourceNodeId, $"连接的目标节点 {conn.TargetNodeId} 不存在。", conn.SourcePinId);
                    continue;
                }
                var srcPin = srcNode.FindPinById(conn.SourcePinId);
                var dstPin = dstNode.FindPinById(conn.TargetPinId);
                if (srcPin == null || dstPin == null)
                {
                    result.AddError(conn.SourceNodeId, "连接引用了不存在的引脚。");
                    continue;
                }
                if (srcPin.Direction != ShaderGraphPinDirection.Output ||
                    dstPin.Direction != ShaderGraphPinDirection.Input)
                {
                    result.AddError(conn.SourceNodeId, "连接方向错误: 必须从输出引脚到输入引脚。");
                    continue;
                }
                if (!ShaderGraphPin.AreTypesCompatible(srcPin.DataType, dstPin.DataType))
                {
                    result.AddError(conn.TargetNodeId,
                        $"类型不匹配: 源 {srcPin.DataType} -> 目标 {dstPin.DataType}。", conn.TargetPinId);
                }
            }

            // 3. 循环检测
            if (HasCycle())
            {
                result.AddError("", "着色器图中存在循环依赖。");
            }

            // 4. 必需连接引脚检查 (仅检查从输出节点可达的节点)
            var reachable = GetReachableNodes(outputNode.Id);
            foreach (var node in reachable)
            {
                if (!node.Enabled) continue;
                foreach (var pin in node.InputPins)
                {
                    if (!pin.Required) continue;
                    var conn = GetConnectionToInput(node.Id, pin.Id);
                    if (conn == null)
                    {
                        result.AddError(node.Id, $"必需的输入引脚 '{pin.Name}' 未连接。", pin.Id);
                    }
                }
            }

            return result;
        }

        /// <summary>检测图中是否存在循环 (基于连接的图)。</summary>
        public bool HasCycle()
        {
            var visited = new HashSet<string>();
            var recursion = new HashSet<string>();

            bool Dfs(string nodeId)
            {
                visited.Add(nodeId);
                recursion.Add(nodeId);
                // 遍历此节点的输入连接的上游节点
                foreach (var conn in Connections)
                {
                    if (!conn.Enabled) continue;
                    if (conn.TargetNodeId != nodeId) continue;
                    var upstream = conn.SourceNodeId;
                    if (!visited.Contains(upstream))
                    {
                        if (Dfs(upstream)) return true;
                    }
                    else if (recursion.Contains(upstream))
                    {
                        return true;
                    }
                }
                recursion.Remove(nodeId);
                return false;
            }

            foreach (var node in Nodes)
            {
                if (!visited.Contains(node.Id))
                {
                    if (Dfs(node.Id)) return true;
                }
            }
            return false;
        }

        /// <summary>获取从指定节点出发可达的所有上游节点 (含自身)。</summary>
        public List<ShaderGraphNode> GetReachableNodes(string startNodeId)
        {
            var visited = new HashSet<string>();
            var result = new List<ShaderGraphNode>();
            var stack = new Stack<string>();
            stack.Push(startNodeId);

            while (stack.Count > 0)
            {
                var nodeId = stack.Pop();
                if (!visited.Add(nodeId)) continue;
                var node = GetNode(nodeId);
                if (node == null) continue;
                result.Add(node);
                // 向上游遍历
                foreach (var conn in Connections)
                {
                    if (!conn.Enabled) continue;
                    if (conn.TargetNodeId != nodeId) continue;
                    if (!visited.Contains(conn.SourceNodeId))
                        stack.Push(conn.SourceNodeId);
                }
            }
            return result;
        }

        /// <summary>
        /// 拓扑排序: 从输出节点反向遍历, 返回求值顺序 (上游在前, 下游在后)。
        /// 仅包含从输出节点可达的节点。
        /// </summary>
        public List<ShaderGraphNode> TopologicalSort()
        {
            var outputNode = GetOutputNode();
            if (outputNode == null)
            {
                var master = Nodes.FirstOrDefault(n => n.IsMasterNode);
                if (master != null)
                {
                    OutputNodeId = master.Id;
                    outputNode = master;
                }
                else
                {
                    return new List<ShaderGraphNode>();
                }
            }

            var visited = new HashSet<string>();
            var result = new List<ShaderGraphNode>();

            void Visit(string nodeId)
            {
                if (!visited.Add(nodeId)) return;
                var node = GetNode(nodeId);
                if (node == null || !node.Enabled) return;
                // 先访问上游
                foreach (var conn in Connections)
                {
                    if (!conn.Enabled) continue;
                    if (conn.TargetNodeId != nodeId) continue;
                    Visit(conn.SourceNodeId);
                }
                result.Add(node);
            }

            Visit(outputNode.Id);
            return result;
        }

        /// <summary>
        /// 获取从输出节点到所有叶节点的求值顺序 (别名)。
        /// </summary>
        public List<ShaderGraphNode> GetEvaluationOrder() => TopologicalSort();

        // ── 预览渲染 ──────────────────────────────────────────

        /// <summary>
        /// 更新所有节点的预览。对拓扑序中每个节点调用 ComputePreview。
        /// 实际编辑器中可由后台渲染线程调用。
        /// </summary>
        public void UpdatePreviews()
        {
            var order = TopologicalSort();
            foreach (var node in order)
            {
                try
                {
                    node.NodePreview = node.ComputePreview();
                }
                catch
                {
                    node.NodePreview = NodePreviewData.Empty;
                }
            }
        }

        /// <summary>创建图的深拷贝 (用于子图内联展开)。</summary>
        public ShaderGraph Clone()
        {
            var clone = new ShaderGraph
            {
                Name = Name + "_Copy",
                Description = Description,
                Target = Target,
                Precision = Precision,
                BlendMode = BlendMode,
                ShadingModel = ShadingModel,
                DoubleSided = DoubleSided,
                Wireframe = Wireframe,
                AlphaCutoff = AlphaCutoff
            };
            // 节点映射 (旧ID -> 新节点)
            var idMap = new Dictionary<string, ShaderGraphNode>();
            foreach (var node in Nodes)
            {
                var newNode = (ShaderGraphNode)MemberwiseCloneNode(node);
                idMap[node.Id] = newNode;
                clone.Nodes.Add(newNode);
            }
            // 连接重映射
            foreach (var conn in Connections)
            {
                if (idMap.TryGetValue(conn.SourceNodeId, out var src) &&
                    idMap.TryGetValue(conn.TargetNodeId, out var dst))
                {
                    clone.Connections.Add(new ShaderGraphConnection
                    {
                        SourceNodeId = src.Id,
                        SourcePinId = conn.SourcePinId,
                        TargetNodeId = dst.Id,
                        TargetPinId = conn.TargetPinId,
                        Enabled = conn.Enabled
                    });
                }
            }
            // 属性
            foreach (var prop in Properties)
            {
                clone.Properties.Add(new ShaderProperty
                {
                    Name = prop.Name,
                    DisplayName = prop.DisplayName,
                    PropertyType = prop.PropertyType,
                    ScalarValue = prop.ScalarValue,
                    VectorValue = prop.VectorValue,
                    TexturePath = prop.TexturePath,
                    BoolValue = prop.BoolValue,
                    IntValue = prop.IntValue,
                    MinValue = prop.MinValue,
                    MaxValue = prop.MaxValue,
                    UseRange = prop.UseRange,
                    Group = prop.Group,
                    SortOrder = prop.SortOrder,
                    Visible = prop.Visible,
                    Tooltip = prop.Tooltip
                });
            }
            if (OutputNodeId != null && idMap.TryGetValue(OutputNodeId, out var outNode))
                clone.OutputNodeId = outNode.Id;
            return clone;
        }

        /// <summary>简单的节点克隆 (浅拷贝字段, 重置 ID 与引脚)。</summary>
        private static object MemberwiseCloneNode(ShaderGraphNode node)
        {
            var type = node.GetType();
            var newNode = (ShaderGraphNode)Activator.CreateInstance(type)!;
            newNode.Id = System.Guid.NewGuid().ToString("N");
            newNode.NodeType = node.NodeType;
            newNode.Title = node.Title;
            newNode.Description = node.Description;
            newNode.Position = node.Position;
            newNode.Size = node.Size;
            newNode.Enabled = node.Enabled;
            newNode.Collapsed = node.Collapsed;
            newNode.ColorTag = node.ColorTag;
            newNode.Comment = node.Comment;
            foreach (var kv in node.Metadata)
                newNode.Metadata[kv.Key] = kv.Value;
            // 引脚: 重新创建以重置 ID 前缀
            foreach (var pin in node.InputPins)
            {
                newNode.InputPins.Add(new ShaderGraphPin
                {
                    Id = $"{newNode.Id}_in_{pin.Name}",
                    Name = pin.Name,
                    Direction = ShaderGraphPinDirection.Input,
                    DataType = pin.DataType,
                    DefaultValue = pin.DefaultValue,
                    IntDefaultValue = pin.IntDefaultValue,
                    BoolDefaultValue = pin.BoolDefaultValue,
                    StringDefaultValue = pin.StringDefaultValue,
                    Required = pin.Required,
                    SliderMin = pin.SliderMin,
                    SliderMax = pin.SliderMax
                });
            }
            foreach (var pin in node.OutputPins)
            {
                newNode.OutputPins.Add(new ShaderGraphPin
                {
                    Id = $"{newNode.Id}_out_{pin.Name}",
                    Name = pin.Name,
                    Direction = ShaderGraphPinDirection.Output,
                    DataType = pin.DataType,
                    DefaultValue = pin.DefaultValue
                });
            }
            return newNode;
        }

        /// <summary>设置输出节点。</summary>
        public void SetOutputNode(ShaderGraphNode node)
        {
            AddNode(node);
            OutputNodeId = node.Id;
        }
    }
}
