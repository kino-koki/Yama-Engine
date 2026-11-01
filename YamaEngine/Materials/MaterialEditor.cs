// =============================================================================
//  Yama Engine - MaterialEditor.cs
//  yama引擎 - 材质编辑器系统
//
//  本文件实现了完整的节点式材质图编辑器，包含：
//    * 引脚(Pin)连接系统 —— 强类型的输入/输出引脚与引脚间连线
//    * 丰富的节点类型 —— 纹理采样、常量、数学运算、三角函数、时间、UV、
//      菲涅尔、高度转法线、去饱和、遮罩、合并、分量遮罩等
//    * 材质属性输出节点 —— BaseColor / Normal / Roughness / Metallic /
//      Emissive / Opacity / AO 七大通道
//    * 混合模式 —— Opaque / Masked / Translucent / Additive
//    * 着色模型 —— Unlit / Default Lit / Subsurface / Hair / Cloth / Eye
//    * 材质函数(Material Function) —— 可复用的节点子图
//    * 材质参数集合(Material Parameter Collection) —— 跨材质全局参数
//    * 材质实例(Material Instance) —— 动态参数覆盖，无需重编译
//    * 材质预览球渲染 —— 借助 RHI 设备实时渲染预览
//    * 与 RHI 着色器编译对接 —— 通过 ShaderGraphCompiler 生成着色器字节码
//
//  与已有的 MaterialGraph.cs / PbrMaterial.cs 配合使用：
//    可将编辑器图(MaterialEditorGraph)序列化为旧的 MaterialGraph 以保持兼容，
//    也可直接编译为 RHI 着色器字节码并创建管线状态对象(PSO)。
// =============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.Json;
using YamaEngine.Assets;
using YamaEngine.RHI;
using YamaEngine.Scripting;
using YamaEngine.Rendering;

namespace YamaEngine.Materials
{
    // ──────────────────────────────────────────────────────────────────────────
    // 枚举定义区
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 材质混合模式。决定像素如何与帧缓冲混合，影响 PSO 的 BlendState 设置。
    /// </summary>
    public enum MaterialBlendMode
    {
        /// <summary>不透明：关闭混合，深度写入开启。</summary>
        Opaque = 0,
        /// <summary>遮罩：通过 Alpha 阈值(OpacityMaskClipValue)裁剪像素，深度写入开启。</summary>
        Masked = 1,
        /// <summary>半透明：Alpha 混合，深度写入关闭，深度测试开启。</summary>
        Translucent = 2,
        /// <summary>加法：源 Alpha * 1 的加法混合，常用于粒子/光晕。</summary>
        Additive = 3
    }

    /// <summary>
    /// 着色模型。决定像素着色阶段使用的 BRDF/BSDF 计算路径。
    /// </summary>
    public enum MaterialShadingModel
    {
        /// <summary>无光照：直接输出 Emissive/自发光颜色，不做光照计算。</summary>
        Unlit = 0,
        /// <summary>默认光照：标准 Cook-Torrance PBR 金属度-粗糙度工作流。</summary>
        DefaultLit = 1,
        /// <summary>次表面散射：皮肤/蜡等半透明材质的近似 SSS。</summary>
        Subsurface = 2,
        /// <summary>头发：基于 Marschner/双高光的头发着色模型。</summary>
        Hair = 3,
        /// <summary>布料：Charlie sheen 分布 + 次表面近似，用于织物。</summary>
        Cloth = 4,
        /// <summary>眼睛：角膜/虹膜/瞳孔的分层折射近似。</summary>
        Eye = 5
    }

    /// <summary>
    /// 材质属性输出通道。对应主输出节点上的各个输入引脚。
    /// </summary>
    public enum MaterialProperty
    {
        BaseColor,   // 基础色 (float3)
        Normal,      // 切线空间法线 (float3)
        Roughness,   // 粗糙度 (float)
        Metallic,    // 金属度 (float)
        Emissive,    // 自发光 (float3)
        Opacity,     // 不透明度 (float)
        AO,          // 环境光遮蔽 (float)
        Specular,    // 高光强度倍数 (float)
        SubsurfaceColor, // 次表面颜色 (float3，仅 Subsurface 模型有效)
        Tangent,     // 切线 (float3，可选)
        WorldPositionOffset // 世界位置偏移 (float3，在 VS 中应用)
    }

    // PinDataType 定义已统一至 MaterialGraph.cs，此处不再重复定义以避免 CS0101 编译冲突
    // MaterialGraph.cs 中的 PinDataType 包含完整类型：Float, Float2, Float3, Float4, Texture2D, CubeMap, Bool

    /// <summary>引脚方向：输入引脚接收数据，输出引脚提供数据。</summary>

    /// <summary>节点分类，用于编辑器调色板分组显示。</summary>
    public enum NodeCategory
    {
        Texture,    // 纹理相关
        Constant,   // 常量
        Math,       // 数学运算
        Trig,       // 三角/超越函数
        Time,       // 时间
        UV,         // 纹理坐标
        Utility,    // 实用工具(Fresnel/Desaturation 等)
        Normal,     // 法线工具
        Color,      // 颜色工具
        Master,     // 主输出节点
        Function,   // 材质函数调用
        Parameter   // 参数引用
    }

    /// <summary>
    /// 编辑器节点类型。比旧版 MaterialNodeType 更丰富，覆盖编辑器所需全部节点。
    /// </summary>
    public enum EditorNodeType
    {
        // 纹理
        TextureSample,      // 纹理采样
        TextureObject,      // 纹理对象传递(不采样)

        // 常量
        Constant,           // 浮点常量(可标量/向量)
        ConstantColor,      // 颜色拾取器常量
        Constant2Vector,    // float2 常量
        Constant3Vector,    // float3 常量
        Constant4Vector,    // float4 常量

        // 数学运算
        Add,                // A + B
        Subtract,           // A - B
        Multiply,           // A * B
        Divide,             // A / B
        Power,              // pow(Base, Exp)
        Lerp,               // 线性插值
        Sine,               // sin
        Cosine,             // cos
        Abs,                // 绝对值
        Floor,              // 向下取整
        Ceil,               // 向上取整
        Frac,               // 小数部分
        Clamp,              // 钳制到 [Min, Max]
        Saturate,           // 钳制到 [0, 1]
        OneMinus,           // 1 - X

        // 时间
        Time,               // 引擎时间(秒)
        SineWave,           // 正弦波(带频率/相位)

        // UV
        TexCoord,           // 纹理坐标通道(0..7)

        // 实用工具
        Fresnel,            // 菲涅尔效应
        Desaturation,       // 去饱和度
        Mask,               // 通道遮罩(R/G/B/A)
        Append,             // 合并向量
        ComponentMask,      // 分量提取(可重组 swizzle)

        // 法线工具
        NormalFromHeight,   // 高度图转法线
        NormalBlend,        // 法线混合

        // 主输出与参数
        MasterOutput,       // 材质属性主输出节点
        FunctionCall,       // 材质函数调用
        ScalarParameter,    // 标量参数
        VectorParameter,    // 向量参数
        TextureParameter,   // 纹理参数
        ParameterReference // 参数集合引用
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 引脚与节点
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 材质引脚(Pin)。每个节点拥有若干输入/输出引脚，引脚之间通过 <see cref="MaterialConnection"/>
    /// 连接。引脚携带数据类型，连接时会执行类型兼容性检查与自动提升(标量->向量)。
    /// </summary>
    public sealed class MaterialPin
    {
        /// <summary>引脚唯一标识。</summary>
        public string Id { get; set; } = Guid.NewGuid().ToString("N");

        /// <summary>所属节点 Id。</summary>
        public string OwnerNodeId { get; set; } = string.Empty;

        /// <summary>引脚显示名称(如 "A"、"BaseColor"、"UV")。</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>引脚方向。</summary>
        public PinDirection Direction { get; set; }

        /// <summary>引脚数据类型。</summary>
        public PinDataType DataType { get; set; }

        /// <summary>默认值(未连接时使用)。以 float4 存储，使用时按类型截取。</summary>
        public Vector4 DefaultValue { get; set; } = Vector4.Zero;

        /// <summary>编辑器中的相对位置偏移(用于绘制连线锚点)。</summary>
        public Vector2 EditorOffset { get; set; }

        /// <summary>是否允许多个输入连入(仅对输入引脚有意义)。默认 false：单一连接。</summary>
        public bool AllowMultiple { get; set; }

        /// <summary>返回该引脚在 HLSL 中对应的类型字符串。</summary>
        public string HlslType => DataType switch
        {
            PinDataType.Float => "float",
            PinDataType.Float2 => "float2",
            PinDataType.Float3 => "float3",
            PinDataType.Float4 => "float4",
            PinDataType.Texture2D => "Texture2D",
            PinDataType.Bool => "bool",
            _ => "float4"
        };

        /// <summary>将默认值转换为对应类型的字符串字面量(用于 HLSL 代码生成)。</summary>
        public string DefaultLiteral()
        {
            return DataType switch
            {
                PinDataType.Float => DefaultValue.X.ToString("G9"),
                PinDataType.Float2 => $"float2({DefaultValue.X:G9}, {DefaultValue.Y:G9})",
                PinDataType.Float3 => $"float3({DefaultValue.X:G9}, {DefaultValue.Y:G9}, {DefaultValue.Z:G9})",
                PinDataType.Float4 => $"float4({DefaultValue.X:G9}, {DefaultValue.Y:G9}, {DefaultValue.Z:G9}, {DefaultValue.W:G9})",
                _ => "0"
            };
        }
    }

    /// <summary>
    /// 材质编辑器节点。封装节点元数据、引脚列表与节点参数。
    /// 与旧版 <see cref="MaterialNode"/> 不同，本节点使用强类型引脚系统。
    /// </summary>
    public sealed class MaterialEditorNode
    {
        /// <summary>节点唯一标识。</summary>
        public string Id { get; set; } = Guid.NewGuid().ToString("N");

        /// <summary>节点类型。</summary>
        public EditorNodeType NodeType { get; set; }

        /// <summary>节点显示名称。</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>节点分类。</summary>
        public NodeCategory Category { get; set; }

        /// <summary>编辑器画布坐标。</summary>
        public Vector2 Position { get; set; }

        /// <summary>节点注释/备注。</summary>
        public string Comment { get; set; } = string.Empty;

        /// <summary>是否启用(禁用的节点在编译时跳过)。</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>输入引脚列表。</summary>
        public List<MaterialPin> InputPins { get; set; } = new();

        /// <summary>输出引脚列表。</summary>
        public List<MaterialPin> OutputPins { get; set; } = new();

        /// <summary>节点参数字典(如纹理路径、UV 通道、遮罩掩码、常量值等)。</summary>
        public Dictionary<string, string> Parameters { get; set; } = new();

        /// <summary>便捷访问：获取指定名称的输入引脚。</summary>
        public MaterialPin? GetInput(string name) =>
            InputPins.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

        /// <summary>便捷访问：获取指定名称的输出引脚。</summary>
        public MaterialPin? GetOutput(string name) =>
            OutputPins.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

        /// <summary>添加一个输入引脚并返回该引脚。</summary>
        public MaterialPin AddInput(string name, PinDataType type, Vector4? defaultValue = null)
        {
            var pin = new MaterialPin
            {
                OwnerNodeId = Id,
                Name = name,
                Direction = PinDirection.Input,
                DataType = type,
                DefaultValue = defaultValue ?? Vector4.Zero
            };
            InputPins.Add(pin);
            return pin;
        }

        /// <summary>添加一个输出引脚并返回该引脚。</summary>
        public MaterialPin AddOutput(string name, PinDataType type)
        {
            var pin = new MaterialPin
            {
                OwnerNodeId = Id,
                Name = name,
                Direction = PinDirection.Output,
                DataType = type
            };
            OutputPins.Add(pin);
            return pin;
        }
    }

    /// <summary>
    /// 引脚间连接。一条连接从某个输出引脚指向某个输入引脚。
    /// 连接具有方向性：From = 输出端，To = 输入端。
    /// </summary>
    public sealed class MaterialConnection
    {
        /// <summary>连接唯一标识。</summary>
        public string Id { get; set; } = Guid.NewGuid().ToString("N");

        /// <summary>源节点 Id(数据产生方)。</summary>
        public string FromNodeId { get; set; } = string.Empty;

        /// <summary>源引脚 Id(输出引脚)。</summary>
        public string FromPinId { get; set; } = string.Empty;

        /// <summary>目标节点 Id(数据消费方)。</summary>
        public string ToNodeId { get; set; } = string.Empty;

        /// <summary>目标引脚 Id(输入引脚)。</summary>
        public string ToPinId { get; set; } = string.Empty;
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 材质函数与参数
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 材质函数(Material Function)。一段可复用的节点子图，拥有输入/输出引脚签名，
    /// 可在任意材质中被 <see cref="EditorNodeType.FunctionCall"/> 节点调用。
    /// 函数内部维护自己的节点与连接集合，编译时内联展开。
    /// </summary>
    public sealed class MaterialFunction
    {
        /// <summary>函数唯一标识。</summary>
        public string Guid { get; set; } = System.Guid.NewGuid().ToString("N");

        /// <summary>函数名称(作为 HLSL 函数名，需合法标识符)。</summary>
        public string Name { get; set; } = "MaterialFunction";

        /// <summary>函数描述。</summary>
        public string Description { get; set; } = string.Empty;

        /// <summary>函数输入参数签名(名称 + 类型)。</summary>
        public List<FunctionParameter> InputParameters { get; set; } = new();

        /// <summary>函数输出参数签名(名称 + 类型)。</summary>
        public List<FunctionParameter> OutputParameters { get; set; } = new();

        /// <summary>函数内部节点。</summary>
        public List<MaterialEditorNode> Nodes { get; set; } = new();

        /// <summary>函数内部连接。</summary>
        public List<MaterialConnection> Connections { get; set; } = new();

        /// <summary>验证函数签名完整性。</summary>
        public List<string> Validate()
        {
            var errors = new List<string>();
            if (string.IsNullOrWhiteSpace(Name))
                errors.Add("材质函数名称不能为空。");

            // 检查节点 Id 唯一性
            var ids = new HashSet<string>();
            foreach (var n in Nodes)
            {
                if (!ids.Add(n.Id))
                    errors.Add($"函数 [{Name}] 内存在重复节点 Id: {n.Id}");
            }
            return errors;
        }
    }

    /// <summary>材质函数/参数的参数签名描述。</summary>
    public sealed class FunctionParameter
    {
        public string Name { get; set; } = string.Empty;
        public PinDataType DataType { get; set; }
        public Vector4 DefaultValue { get; set; }
    }

    /// <summary>
    /// 材质参数。命名的可动画/可覆盖值，运行时可通过常量缓冲更新。
    /// </summary>
    public sealed class MaterialParameter
    {
        /// <summary>参数名(在材质内唯一，用作 HLSL uniform 名)。</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>参数类型。</summary>
        public PinDataType DataType { get; set; }

        /// <summary>默认值。</summary>
        public Vector4 DefaultValue { get; set; }

        /// <summary>参数分组(编辑器中折叠分组显示)。</summary>
        public string Group { get; set; } = "Default";

        /// <summary>排序索引。</summary>
        public int SortOrder { get; set; }

        /// <summary>是否在材质实例中可覆盖。</summary>
        public bool InstanceEditable { get; set; } = true;

        /// <summary>是否参与材质实例动画系统。</summary>
        public bool Animatable { get; set; } = true;

        /// <summary>显示名称(图表参数收集时设置)。</summary>
        public string? DisplayName { get; set; }

        /// <summary>参数类型(标量/向量/纹理等)。</summary>
        public MaterialParameterType ParameterType { get; set; } = MaterialParameterType.Scalar;

        /// <summary>标量默认值。</summary>
        public float FloatValue { get; set; }

        /// <summary>向量默认值。</summary>
        public Vector4 VectorValue { get; set; }

        /// <summary>纹理路径(纹理参数)。</summary>
        public string? TexturePath { get; set; }

        /// <summary>HLSL 类型名(float/float3/float4/sampler2D 等)，用于常量缓冲声明。</summary>
        public string? HlslType { get; set; }

        /// <summary>取值范围(可选)。</summary>
        public Vector2 Range { get; set; } = new(float.MinValue, float.MaxValue);

        /// <summary>生成 HLSL uniform 声明语句。</summary>
        public string ToHlslDeclaration()
        {
            var type = DataType switch
            {
                PinDataType.Float => "float",
                PinDataType.Float2 => "float2",
                PinDataType.Float3 => "float3",
                PinDataType.Float4 => "float4",
                _ => "float4"
            };
            return $"{type} {Name}; // group={Group}";
        }
    }

    /// <summary>
    /// 材质实例：基于父材质图的参数覆盖。
    /// 记录实例名称与其父材质路径，供运行时动态覆盖参数，无需重编译。
    /// </summary>
    public sealed class MaterialInstance
    {
        /// <summary>实例名称。</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>父材质路径(资源路径或图名)。</summary>
        public string ParentMaterialPath { get; set; } = string.Empty;

        /// <summary>参数覆盖表(参数名 -> 覆盖值)。</summary>
        public Dictionary<string, Vector4> Overrides { get; } = new();

        /// <summary>设置参数覆盖值。</summary>
        public void SetOverride(string parameterName, Vector4 value)
        {
            Overrides[parameterName] = value;
        }

        /// <summary>获取参数覆盖值(未覆盖则返回 null)。</summary>
        public Vector4? GetOverride(string parameterName)
            => Overrides.TryGetValue(parameterName, out var v) ? v : null;
    }

    /// <summary>
    /// 材质参数集合(Material Parameter Collection)。
    /// 全局共享的参数集合，以独立常量缓冲存在，可被多个材质引用，
    /// 适合全局风效、时间、季节等跨材质参数。
    /// </summary>

    // ──────────────────────────────────────────────────────────────────────────
    // 材质实例
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 材质实例(Material Instance)。基于一个父材质(MaterialEditorGraph)创建，
    /// 可覆盖其中的参数默认值而无需重新编译着色器。仅覆盖常量缓冲数据。
    /// </summary>

    // ──────────────────────────────────────────────────────────────────────────
    // 材质编辑器图
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 材质编辑器图。包含节点、连接、混合模式、着色模型、参数列表与引用的参数集合。
    /// 这是编辑器与编译器共同操作的核心数据结构。
    /// </summary>
    public sealed class MaterialEditorGraph
    {
        /// <summary>图版本号(序列化兼容性)。</summary>
        public int Version { get; set; } = 1;

        /// <summary>图唯一标识。</summary>
        public string Guid { get; set; } = System.Guid.NewGuid().ToString("N");

        /// <summary>材质名称。</summary>
        public string Name { get; set; } = "Material";

        /// <summary>混合模式。</summary>
        public MaterialBlendMode BlendMode { get; set; } = MaterialBlendMode.Opaque;

        /// <summary>着色模型。</summary>
        public MaterialShadingModel ShadingModel { get; set; } = MaterialShadingModel.DefaultLit;

        /// <summary>是否双面渲染。</summary>
        public bool DoubleSided { get; set; }

        /// <summary>Masked 模式下的 Alpha 裁剪阈值。</summary>
        public float OpacityMaskClipValue { get; set; } = 0.5f;

        /// <summary>是否在像素阶段使用置换/世界位置偏移。</summary>
        public bool UsesWorldPositionOffset { get; set; }

        /// <summary>节点列表。</summary>
        public List<MaterialEditorNode> Nodes { get; set; } = new();

        /// <summary>连接列表。</summary>
        public List<MaterialConnection> Connections { get; set; } = new();

        /// <summary>材质参数列表(从参数节点自动收集)。</summary>
        public List<MaterialParameter> Parameters { get; set; } = new();

        /// <summary>引用的材质参数集合路径列表。</summary>
        public List<string> ParameterCollectionRefs { get; set; } = new();

        /// <summary>静态关键字定义(用于着色器变体)。</summary>
        public List<MaterialKeyword> Keywords { get; set; } = new();

        /// <summary>
        /// 将编辑器图(MaterialEditorGraph)转换为编译期图(MaterialGraph)。
        /// 用于兼容旧的 <see cref="ShaderGraphCompiler"/> 编译管线。
        /// </summary>
        public MaterialGraph ToMaterialGraph()
        {
            var g = new MaterialGraph
            {
                Version = Math.Max(Version, 2),
                Guid = Guid,
                Name = Name
            };
            g.Nodes.Clear();
            g.Edges.Clear();

            foreach (var node in Nodes)
            {
                var mn = new MaterialNode
                {
                    Id = node.Id,
                    Name = node.Name,
                    Type = MapEditorNodeType(node.NodeType),
                    Position = node.Position,
                    Enabled = node.Enabled
                };

                if (node.Parameters.TryGetValue("Value", out var sv) && float.TryParse(sv, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var fv))
                    mn.FloatValue = fv;

                var outPin = node.OutputPins.FirstOrDefault();
                if (outPin != null) mn.VectorValue = outPin.DefaultValue;

                if (node.Parameters.TryGetValue("TexturePath", out var tp))
                    mn.TexturePath = tp;
                if (node.Parameters.TryGetValue("CubeMapPath", out var cp))
                    mn.CubeMapPath = cp;

                foreach (var kv in node.Parameters)
                    mn.Properties[kv.Key] = kv.Value;

                mn.InitializePins();
                g.Nodes.Add(mn);
            }

            foreach (var c in Connections)
            {
                g.Edges.Add(new MaterialEdge
                {
                    FromNodeId = c.FromNodeId,
                    FromPinId = c.FromPinId,
                    ToNodeId = c.ToNodeId,
                    ToPinId = c.ToPinId
                });
            }

            return g;
        }

        /// <summary>将编辑器节点类型映射为编译期节点类型；无直接对应的节点回退到最接近的类型。</summary>
        private static MaterialNodeType MapEditorNodeType(EditorNodeType t) => t switch
        {
            EditorNodeType.Constant => MaterialNodeType.Constant,
            EditorNodeType.ConstantColor => MaterialNodeType.ConstantColor,
            EditorNodeType.Constant2Vector or EditorNodeType.Constant3Vector
              or EditorNodeType.Constant4Vector => MaterialNodeType.ConstantColor,
            EditorNodeType.Add => MaterialNodeType.Add,
            EditorNodeType.Subtract => MaterialNodeType.Subtract,
            EditorNodeType.Multiply => MaterialNodeType.Multiply,
            EditorNodeType.Divide => MaterialNodeType.Divide,
            EditorNodeType.Power => MaterialNodeType.Power,
            EditorNodeType.Lerp => MaterialNodeType.Lerp,
            EditorNodeType.Sine => MaterialNodeType.Sine,
            EditorNodeType.Cosine => MaterialNodeType.Cosine,
            EditorNodeType.Abs => MaterialNodeType.Abs,
            EditorNodeType.Floor => MaterialNodeType.Floor,
            EditorNodeType.Ceil => MaterialNodeType.Ceil,
            EditorNodeType.Frac => MaterialNodeType.Frac,
            EditorNodeType.Clamp => MaterialNodeType.Clamp,
            EditorNodeType.Saturate => MaterialNodeType.Saturate,
            EditorNodeType.OneMinus => MaterialNodeType.OneMinus,
            EditorNodeType.TextureSample => MaterialNodeType.TextureSample,
            EditorNodeType.TexCoord => MaterialNodeType.TexCoord,
            EditorNodeType.Time => MaterialNodeType.Time,
            EditorNodeType.SineWave => MaterialNodeType.SineWave,
            EditorNodeType.Mask => MaterialNodeType.Mask,
            EditorNodeType.Append => MaterialNodeType.Append,
            EditorNodeType.ComponentMask => MaterialNodeType.ComponentMask,
            EditorNodeType.MasterOutput => MaterialNodeType.PbrMaster,
            _ => MaterialNodeType.Constant
        };

        /// <summary>编辑器中选中的节点 Id 集合。</summary>
        public HashSet<string> SelectedNodeIds { get; set; } = new();

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            IncludeFields = true,
            PropertyNameCaseInsensitive = true
        };

        // ── 节点与连接管理 ──────────────────────────────────────────────

        /// <summary>添加节点到图中。</summary>
        public MaterialEditorNode AddNode(MaterialEditorNode node)
        {
            if (string.IsNullOrEmpty(node.Id))
                node.Id = System.Guid.NewGuid().ToString("N");
            Nodes.Add(node);
            return node;
        }

        /// <summary>按类型创建并添加节点。</summary>
        public MaterialEditorNode CreateNode(EditorNodeType type, string name, Vector2 position)
        {
            var node = new MaterialEditorNode
            {
                NodeType = type,
                Name = name,
                Position = position,
                Category = InferCategory(type)
            };
            PopulateDefaultPins(node);
            return AddNode(node);
        }

        /// <summary>删除节点及其相关连接。</summary>
        public bool RemoveNode(string nodeId)
        {
            var node = Nodes.FirstOrDefault(n => n.Id == nodeId);
            if (node == null) return false;
            Nodes.Remove(node);
            // 移除所有涉及该节点的连接
            Connections.RemoveAll(c =>
                c.FromNodeId == nodeId || c.ToNodeId == nodeId);
            return true;
        }

        /// <summary>在两个引脚之间建立连接，返回连接对象；若类型不兼容则返回 null。</summary>
        public MaterialConnection? Connect(string fromNodeId, string fromPinId,
                                           string toNodeId, string toPinId)
        {
            var fromNode = Nodes.FirstOrDefault(n => n.Id == fromNodeId);
            var toNode = Nodes.FirstOrDefault(n => n.Id == toNodeId);
            if (fromNode == null || toNode == null) return null;

            var outPin = fromNode.OutputPins.FirstOrDefault(p => p.Id == fromPinId);
            var inPin = toNode.InputPins.FirstOrDefault(p => p.Id == toPinId);
            if (outPin == null || inPin == null) return null;

            // 类型兼容性检查：标量可自动提升为任意向量；向量间需维度兼容或截断
            if (!IsTypeCompatible(outPin.DataType, inPin.DataType))
                return null;

            // 若输入引脚不允许多重连接，则先移除已有连接
            if (!inPin.AllowMultiple)
                Connections.RemoveAll(c => c.ToNodeId == toNodeId && c.ToPinId == toPinId);

            var conn = new MaterialConnection
            {
                FromNodeId = fromNodeId,
                FromPinId = fromPinId,
                ToNodeId = toNodeId,
                ToPinId = toPinId
            };
            Connections.Add(conn);
            return conn;
        }

        /// <summary>断开指定连接。</summary>
        public bool Disconnect(string connectionId)
        {
            return Connections.RemoveAll(c => c.Id == connectionId) > 0;
        }

        /// <summary>获取连入指定输入引脚的连接(取最后一条)。</summary>
        public MaterialConnection? GetIncomingConnection(string nodeId, string pinId)
        {
            return Connections.LastOrDefault(c => c.ToNodeId == nodeId && c.ToPinId == pinId);
        }

        /// <summary>获取从指定输出引脚连出的所有连接。</summary>
        public List<MaterialConnection> GetOutgoingConnections(string nodeId, string pinId)
        {
            return Connections.Where(c => c.FromNodeId == nodeId && c.FromPinId == pinId).ToList();
        }

        // ── 类型兼容性 ──────────────────────────────────────────────────

        /// <summary>
        /// 判断输出类型是否可连接到输入类型。
        /// 规则：Float 可提升为任意向量；同维度兼容；高维向量可截断到低维(编译器处理)；
        /// Texture 仅与 Texture 兼容。
        /// </summary>
        public static bool IsTypeCompatible(PinDataType output, PinDataType input)
        {
            if (output == input) return true;
            // 标量可提升
            if (output == PinDataType.Float &&
                (input == PinDataType.Float2 || input == PinDataType.Float3 || input == PinDataType.Float4))
                return true;
            // 高维向量可截断(运行时由编译器生成 swizzle)
            int outDim = DimensionOf(output);
            int inDim = DimensionOf(input);
            if (outDim >= inDim && outDim <= 4 && inDim >= 1) return true;
            // 纹理必须匹配
            if (output == PinDataType.Texture2D || input == PinDataType.Texture2D)
                return output == input;
            return false;
        }

        /// <summary>返回数据类型的分量维度(Texture/Bool 视为 0)。</summary>
        public static int DimensionOf(PinDataType t) => t switch
        {
            PinDataType.Float => 1,
            PinDataType.Float2 => 2,
            PinDataType.Float3 => 3,
            PinDataType.Float4 => 4,
            _ => 0
        };

        // ── 节点引脚模板 ────────────────────────────────────────────────

        /// <summary>根据节点类型填充默认引脚。</summary>
        private static void PopulateDefaultPins(MaterialEditorNode node)
        {
            switch (node.NodeType)
            {
                case EditorNodeType.TextureSample:
                    node.AddInput("UV", PinDataType.Float2, new Vector4(0, 0, 0, 0));
                    node.AddOutput("RGB", PinDataType.Float3);
                    node.AddOutput("R", PinDataType.Float);
                    node.AddOutput("G", PinDataType.Float);
                    node.AddOutput("B", PinDataType.Float);
                    node.AddOutput("A", PinDataType.Float);
                    break;

                case EditorNodeType.Constant:
                    node.AddOutput("Value", PinDataType.Float);
                    break;

                case EditorNodeType.ConstantColor:
                    node.AddOutput("Color", PinDataType.Float4);
                    node.AddOutput("RGB", PinDataType.Float3);
                    break;

                case EditorNodeType.Constant2Vector:
                    node.AddOutput("Value", PinDataType.Float2);
                    break;
                case EditorNodeType.Constant3Vector:
                    node.AddOutput("Value", PinDataType.Float3);
                    break;
                case EditorNodeType.Constant4Vector:
                    node.AddOutput("Value", PinDataType.Float4);
                    break;

                case EditorNodeType.Add:
                case EditorNodeType.Subtract:
                case EditorNodeType.Multiply:
                case EditorNodeType.Divide:
                    node.AddInput("A", PinDataType.Float, new Vector4(0, 0, 0, 0));
                    node.AddInput("B", PinDataType.Float, new Vector4(1, 1, 1, 1));
                    node.AddOutput("Result", PinDataType.Float);
                    break;

                case EditorNodeType.Power:
                    node.AddInput("Base", PinDataType.Float, new Vector4(1, 1, 1, 1));
                    node.AddInput("Exp", PinDataType.Float, new Vector4(2, 2, 2, 2));
                    node.AddOutput("Result", PinDataType.Float);
                    break;

                case EditorNodeType.Lerp:
                    node.AddInput("A", PinDataType.Float, new Vector4(0, 0, 0, 0));
                    node.AddInput("B", PinDataType.Float, new Vector4(1, 1, 1, 1));
                    node.AddInput("T", PinDataType.Float, new Vector4(0.5f, 0.5f, 0.5f, 0.5f));
                    node.AddOutput("Result", PinDataType.Float);
                    break;

                case EditorNodeType.Sine:
                case EditorNodeType.Cosine:
                case EditorNodeType.Abs:
                case EditorNodeType.Floor:
                case EditorNodeType.Ceil:
                case EditorNodeType.Frac:
                case EditorNodeType.Saturate:
                case EditorNodeType.OneMinus:
                    node.AddInput("X", PinDataType.Float);
                    node.AddOutput("Result", PinDataType.Float);
                    break;

                case EditorNodeType.Clamp:
                    node.AddInput("X", PinDataType.Float);
                    node.AddInput("Min", PinDataType.Float, new Vector4(0, 0, 0, 0));
                    node.AddInput("Max", PinDataType.Float, new Vector4(1, 1, 1, 1));
                    node.AddOutput("Result", PinDataType.Float);
                    break;

                case EditorNodeType.Time:
                    node.AddOutput("Time", PinDataType.Float);
                    break;

                case EditorNodeType.SineWave:
                    node.AddInput("Frequency", PinDataType.Float, new Vector4(1, 1, 1, 1));
                    node.AddInput("Phase", PinDataType.Float, new Vector4(0, 0, 0, 0));
                    node.AddOutput("Result", PinDataType.Float);
                    break;

                case EditorNodeType.TexCoord:
                    node.Parameters["Channel"] = "0";
                    node.AddOutput("UV", PinDataType.Float2);
                    break;

                case EditorNodeType.Fresnel:
                    node.AddInput("Exponent", PinDataType.Float, new Vector4(5, 5, 5, 5));
                    node.AddInput("BaseReflectance", PinDataType.Float, new Vector4(0.04f, 0.04f, 0.04f, 0.04f));
                    node.AddOutput("Fresnel", PinDataType.Float);
                    break;

                case EditorNodeType.NormalFromHeight:
                    node.AddInput("Height", PinDataType.Float);
                    node.AddInput("Strength", PinDataType.Float, new Vector4(1, 1, 1, 1));
                    node.AddOutput("Normal", PinDataType.Float3);
                    break;

                case EditorNodeType.NormalBlend:
                    node.AddInput("BaseNormal", PinDataType.Float3, new Vector4(0.5f, 0.5f, 1, 1));
                    node.AddInput("DetailNormal", PinDataType.Float3, new Vector4(0.5f, 0.5f, 1, 1));
                    node.AddOutput("Normal", PinDataType.Float3);
                    break;

                case EditorNodeType.Desaturation:
                    node.AddInput("Color", PinDataType.Float3);
                    node.AddInput("Amount", PinDataType.Float, new Vector4(0.5f, 0.5f, 0.5f, 0.5f));
                    node.AddOutput("Result", PinDataType.Float3);
                    break;

                case EditorNodeType.Mask:
                    node.AddInput("Input", PinDataType.Float4);
                    node.Parameters["Mask"] = "RGBA";
                    node.AddOutput("Result", PinDataType.Float4);
                    break;

                case EditorNodeType.Append:
                    node.AddInput("X", PinDataType.Float);
                    node.AddInput("Y", PinDataType.Float);
                    node.AddInput("Z", PinDataType.Float, new Vector4(0, 0, 0, 0));
                    node.AddInput("W", PinDataType.Float, new Vector4(0, 0, 0, 0));
                    node.AddOutput("Result", PinDataType.Float4);
                    break;

                case EditorNodeType.ComponentMask:
                    node.AddInput("Input", PinDataType.Float4);
                    node.Parameters["Mask"] = "RGB";
                    node.AddOutput("Result", PinDataType.Float3);
                    break;

                case EditorNodeType.MasterOutput:
                    node.AddInput(MaterialProperty.BaseColor.ToString(), PinDataType.Float3, new Vector4(0.8f, 0.8f, 0.8f, 1));
                    node.AddInput(MaterialProperty.Normal.ToString(), PinDataType.Float3, new Vector4(0.5f, 0.5f, 1, 1));
                    node.AddInput(MaterialProperty.Roughness.ToString(), PinDataType.Float, new Vector4(0.5f, 0.5f, 0.5f, 0.5f));
                    node.AddInput(MaterialProperty.Metallic.ToString(), PinDataType.Float, new Vector4(0, 0, 0, 0));
                    node.AddInput(MaterialProperty.Emissive.ToString(), PinDataType.Float3, Vector4.Zero);
                    node.AddInput(MaterialProperty.Opacity.ToString(), PinDataType.Float, new Vector4(1, 1, 1, 1));
                    node.AddInput(MaterialProperty.AO.ToString(), PinDataType.Float, new Vector4(1, 1, 1, 1));
                    node.AddInput(MaterialProperty.Specular.ToString(), PinDataType.Float, new Vector4(0.5f, 0.5f, 0.5f, 0.5f));
                    node.AddInput(MaterialProperty.SubsurfaceColor.ToString(), PinDataType.Float3, new Vector4(1, 1, 1, 1));
                    node.AddInput(MaterialProperty.WorldPositionOffset.ToString(), PinDataType.Float3, Vector4.Zero);
                    break;

                case EditorNodeType.ScalarParameter:
                    node.AddOutput("Value", PinDataType.Float);
                    break;
                case EditorNodeType.VectorParameter:
                    node.AddOutput("Value", PinDataType.Float4);
                    break;
                case EditorNodeType.TextureParameter:
                    node.AddInput("UV", PinDataType.Float2);
                    node.AddOutput("RGB", PinDataType.Float3);
                    node.AddOutput("A", PinDataType.Float);
                    break;
                case EditorNodeType.FunctionCall:
                    // 引脚由绑定函数签名动态添加
                    break;
            }
        }

        /// <summary>根据节点类型推断分类。</summary>
        private static NodeCategory InferCategory(EditorNodeType type) => type switch
        {
            EditorNodeType.TextureSample or EditorNodeType.TextureObject => NodeCategory.Texture,
            EditorNodeType.Constant or EditorNodeType.ConstantColor or EditorNodeType.Constant2Vector
                or EditorNodeType.Constant3Vector or EditorNodeType.Constant4Vector => NodeCategory.Constant,
            EditorNodeType.Add or EditorNodeType.Subtract or EditorNodeType.Multiply or EditorNodeType.Divide
                or EditorNodeType.Power or EditorNodeType.Lerp or EditorNodeType.Clamp
                or EditorNodeType.Saturate or EditorNodeType.OneMinus or EditorNodeType.Abs
                or EditorNodeType.Floor or EditorNodeType.Ceil or EditorNodeType.Frac => NodeCategory.Math,
            EditorNodeType.Sine or EditorNodeType.Cosine => NodeCategory.Trig,
            EditorNodeType.Time or EditorNodeType.SineWave => NodeCategory.Time,
            EditorNodeType.TexCoord => NodeCategory.UV,
            EditorNodeType.Fresnel or EditorNodeType.Desaturation or EditorNodeType.Mask
                or EditorNodeType.Append or EditorNodeType.ComponentMask => NodeCategory.Utility,
            EditorNodeType.NormalFromHeight or EditorNodeType.NormalBlend => NodeCategory.Normal,
            EditorNodeType.MasterOutput => NodeCategory.Master,
            EditorNodeType.FunctionCall => NodeCategory.Function,
            EditorNodeType.ScalarParameter or EditorNodeType.VectorParameter
                or EditorNodeType.TextureParameter or EditorNodeType.ParameterReference => NodeCategory.Parameter,
            _ => NodeCategory.Utility
        };

        // ── 主输出节点访问 ─────────────────────────────────────────────

        /// <summary>获取图中的主输出节点；不存在则返回 null。</summary>
        public MaterialEditorNode? GetMasterNode()
            => Nodes.FirstOrDefault(n => n.NodeType == EditorNodeType.MasterOutput);

        // ── 校验 ────────────────────────────────────────────────────────

        /// <summary>校验图的完整性，返回错误信息列表(空列表表示通过)。</summary>
        public List<string> Validate()
        {
            var errors = new List<string>();
            if (string.IsNullOrWhiteSpace(Name))
                errors.Add("材质名称不能为空。");
            if (!System.Guid.TryParse(Guid, out _))
                errors.Add("材质 GUID 无效。");
            if (GetMasterNode() == null)
                errors.Add("材质图必须包含一个主输出节点(MasterOutput)。");

            // 节点 Id 唯一性
            var ids = new HashSet<string>();
            foreach (var n in Nodes)
            {
                if (string.IsNullOrEmpty(n.Id)) errors.Add($"节点 [{n.Name}] 缺少 Id。");
                else if (!ids.Add(n.Id)) errors.Add($"重复节点 Id: {n.Id}");
            }

            // 连接有效性
            foreach (var c in Connections)
            {
                if (!Nodes.Any(n => n.Id == c.FromNodeId))
                    errors.Add($"连接源节点不存在: {c.FromNodeId}");
                if (!Nodes.Any(n => n.Id == c.ToNodeId))
                    errors.Add($"连接目标节点不存在: {c.ToNodeId}");
            }

            // 检测环路
            if (HasCycle())
                errors.Add("材质图存在环路，无法求值。");

            // 混合模式与着色模型约束
            if (BlendMode == MaterialBlendMode.Masked && OpacityMaskClipValue is < 0 or > 1)
                errors.Add("Masked 模式下 OpacityMaskClipValue 必须在 [0,1] 范围内。");

            return errors;
        }

        /// <summary>检测图中是否存在环路(DFS)。</summary>
        public bool HasCycle()
        {
            var adj = new Dictionary<string, List<string>>();
            foreach (var n in Nodes) adj[n.Id] = new List<string>();
            foreach (var c in Connections)
            {
                if (adj.ContainsKey(c.FromNodeId))
                    adj[c.FromNodeId].Add(c.ToNodeId);
            }
            var visiting = new HashSet<string>();
            var visited = new HashSet<string>();
            bool Dfs(string id)
            {
                if (visiting.Contains(id)) return true;
                if (visited.Contains(id)) return false;
                visiting.Add(id);
                if (adj.TryGetValue(id, out var next))
                    foreach (var t in next)
                        if (Dfs(t)) return true;
                visiting.Remove(id);
                visited.Add(id);
                return false;
            }
            return Nodes.Any(n => Dfs(n.Id));
        }

        /// <summary>从图中收集所有参数节点并更新 <see cref="Parameters"/> 列表。</summary>
        public void CollectParameters()
        {
            Parameters.Clear();
            foreach (var n in Nodes)
            {
                switch (n.NodeType)
                {
                    case EditorNodeType.ScalarParameter:
                        Parameters.Add(new MaterialParameter
                        {
                            Name = n.Parameters.GetValueOrDefault("ParamName", n.Name),
                            DataType = PinDataType.Float,
                            DefaultValue = n.GetOutput("Value")?.DefaultValue ?? Vector4.Zero,
                            Group = n.Parameters.GetValueOrDefault("Group", "Default")
                        });
                        break;
                    case EditorNodeType.VectorParameter:
                        Parameters.Add(new MaterialParameter
                        {
                            Name = n.Parameters.GetValueOrDefault("ParamName", n.Name),
                            DataType = PinDataType.Float4,
                            DefaultValue = n.GetOutput("Value")?.DefaultValue ?? Vector4.One,
                            Group = n.Parameters.GetValueOrDefault("Group", "Default")
                        });
                        break;
                    case EditorNodeType.TextureParameter:
                        Parameters.Add(new MaterialParameter
                        {
                            Name = n.Parameters.GetValueOrDefault("ParamName", n.Name),
                            DataType = PinDataType.Texture2D,
                            DefaultValue = Vector4.Zero,
                            Group = n.Parameters.GetValueOrDefault("Group", "Default")
                        });
                        break;
                }
            }
        }

        // ── 序列化 ──────────────────────────────────────────────────────

        /// <summary>序列化为 .shmat JSON 文件。</summary>
        public void Write(string path)
        {
            var errors = Validate();
            if (errors.Count > 0)
                throw new InvalidOperationException("无法写入无效的材质图: " + string.Join("; ", errors));
            CollectParameters();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));

            // 写入资源元数据
            AssetMeta.FromMetadata(new AssetMetadata
            {
                Guid = System.Guid.Parse(Guid),
                Name = Name,
                AssetPath = ToProjectAssetPath(path),
                SourceFile = path,
                Type = AssetType.Material,
                FileSize = new FileInfo(path).Length,
                ImportedAt = DateTime.Now,
                LastModified = File.GetLastWriteTime(path),
                Hash = Guid,
                Properties =
                {
                    ["blendMode"] = BlendMode.ToString(),
                    ["shadingModel"] = ShadingModel.ToString(),
                    ["doubleSided"] = DoubleSided,
                    ["nodeCount"] = Nodes.Count,
                    ["paramCount"] = Parameters.Count
                }
            }).Write(path + ".shmeta");
        }

        /// <summary>从 .shmat 文件反序列化。</summary>
        public static MaterialEditorGraph Read(string path)
        {
            var graph = JsonSerializer.Deserialize<MaterialEditorGraph>(File.ReadAllText(path), JsonOptions)
                        ?? throw new InvalidOperationException($"无效的材质图: {path}");
            var errors = graph.Validate();
            if (errors.Count > 0)
                throw new InvalidOperationException("无效的材质图: " + string.Join("; ", errors));
            return graph;
        }

        private static string ToProjectAssetPath(string path)
        {
            var normalized = Path.GetFullPath(path).Replace('\\', '/');
            var marker = "/Assets/";
            var index = normalized.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            return index >= 0 ? normalized[(index + 1)..] : Path.GetFileName(path);
        }
    }

    /// <summary>材质静态关键字(用于着色器变体)。</summary>
    public sealed class MaterialKeyword
    {
        /// <summary>关键字名称(用于 #define / #if)。</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>默认值。</summary>
        public bool DefaultValue { get; set; }

        /// <summary>描述。</summary>
        public string Description { get; set; } = string.Empty;
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 材质预览渲染器
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 材质预览渲染器。使用一个预览球网格，将编译后的材质着色器绑定到 PSO，
    /// 在离屏渲染目标上绘制预览图像，供编辑器缩略图使用。
    /// </summary>
    public sealed class MaterialPreviewRenderer : IDisposable
    {
        private readonly RHIDevice _device;
        private readonly RHITexture _renderTarget;
        private readonly RHIDescriptorHandle _rtvHandle;
        private readonly int _width;
        private readonly int _height;

        /// <summary>预览渲染分辨率宽。</summary>
        public int Width => _width;

        /// <summary>预览渲染分辨率高。</summary>
        public int Height => _height;

        /// <summary>
        /// 构造预览渲染器。
        /// </summary>
        /// <param name="device">RHI 设备。</param>
        /// <param name="width">预览宽度(像素)。</param>
        /// <param name="height">预览高度(像素)。</param>
        public MaterialPreviewRenderer(RHIDevice device, int width = 256, int height = 256)
        {
            _device = device ?? throw new ArgumentNullException(nameof(device));
            _width = width;
            _height = height;

            // 创建离屏渲染目标
            var rtDesc = new RHITextureDesc
            {
                Dimension = RHIResourceDimension.Texture2D,
                Format = RHIFormat.R8G8B8A8_UNorm,
                Width = width,
                Height = height,
                DepthOrArraySize = 1,
                MipLevels = 1,
                SampleCount = 1,
                Flags = RHITextureFlags.RenderTarget | RHITextureFlags.ShaderResource
            };
            _renderTarget = _device.CreateTexture(rtDesc, "MaterialPreviewRT");
            _rtvHandle = _device.CreateRTV(_renderTarget);
        }

        /// <summary>
        /// 渲染材质预览。将给定 PSO 应用于预览球并绘制到离屏目标。
        /// </summary>
        /// <param name="cmdList">已打开的命令列表。</param>
        /// <param name="pso">材质对应的管线状态对象。</param>
        /// <param name="rootSignature">根签名。</param>
        /// <param name="clearColor">背景清屏色。</param>
        public void Render(RHICommandList cmdList, RHIPipelineState pso,
                           RHIRootSignature rootSignature, Vector4 clearColor)
        {
            if (cmdList == null || pso == null) return;

            // 将渲染目标转换到 RT 状态
            cmdList.TransitionTo(_renderTarget, RHIResourceState.RenderTarget);

            // 设置渲染目标与视口
            cmdList.OMSetRenderTargets(new[] { _rtvHandle }, null);
            cmdList.SetViewport(new RHIViewport(_width, _height));
            cmdList.SetScissorRect(new RHIScissorRect(0, 0, _width, _height));

            // 清屏
            cmdList.ClearRenderTargetView(_rtvHandle, clearColor);

            // 绑定 PSO 与根签名
            cmdList.SetPipelineState(pso);
            cmdList.SetGraphicsRootSignature(rootSignature);

            // 绘制预览球(此处由上层绑定顶点/索引缓冲后调用 DrawIndexedInstanced)
            // 实际预览球网格由编辑器层维护并绑定，本方法负责状态设置与清屏。
            // 上层调用者应在调用本方法后执行 cmdList.DrawIndexedInstanced(...)
        }

        /// <summary>获取渲染目标的 SRV 句柄(用于读取预览结果)。</summary>
        public RHIDescriptorHandle GetRenderTargetSRV()
        {
            return _device.CreateSRV(_renderTarget);
        }

        /// <summary>获取渲染目标纹理(供截图/回读)。</summary>
        public RHITexture GetRenderTarget() => _renderTarget;

        /// <summary>释放资源。</summary>
        public void Dispose()
        {
            _renderTarget?.Dispose();
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 材质编辑器主系统
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 材质编辑器主系统。作为编辑器与运行时的统一入口，管理当前编辑的材质图、
    /// 材质函数库、参数集合、材质实例，并负责驱动着色器编译与 PSO 创建。
    /// </summary>
    public sealed class MaterialEditorSystem : IDisposable
    {
        private readonly RHIDevice _device;
        private readonly Dictionary<string, MaterialFunction> _functionLibrary = new();
        private readonly Dictionary<string, MaterialParameterCollection> _parameterCollections = new();
        private readonly Dictionary<string, MaterialInstance> _instances = new();
        private MaterialPreviewRenderer? _previewRenderer;

        /// <summary>当前正在编辑的材质图。</summary>
        public MaterialEditorGraph? CurrentGraph { get; private set; }

        /// <summary>材质函数库(函数名 -> 函数对象)。</summary>
        public IReadOnlyDictionary<string, MaterialFunction> FunctionLibrary => _functionLibrary;

        /// <summary>材质参数集合(集合名 -> 集合对象)。</summary>
        public IReadOnlyDictionary<string, MaterialParameterCollection> ParameterCollections => _parameterCollections;

        /// <summary>材质实例表(实例名 -> 实例对象)。</summary>
        public IReadOnlyDictionary<string, MaterialInstance> Instances => _instances;

        /// <summary>预览渲染器。</summary>
        public MaterialPreviewRenderer PreviewRenderer => _previewRenderer!;

        /// <summary>
        /// 构造材质编辑器系统。
        /// </summary>
        /// <param name="device">RHI 设备，用于编译着色器与创建 PSO。</param>
        /// <param name="previewSize">预览球渲染分辨率。</param>
        public MaterialEditorSystem(RHIDevice device, int previewSize = 256)
        {
            _device = device ?? throw new ArgumentNullException(nameof(device));
            _previewRenderer = new MaterialPreviewRenderer(_device, previewSize, previewSize);
        }

        // ── 材质图管理 ──────────────────────────────────────────────────

        /// <summary>创建并打开一个新的材质图进行编辑。</summary>
        public MaterialEditorGraph CreateNewMaterial(string name, MaterialBlendMode blend = MaterialBlendMode.Opaque,
                                                     MaterialShadingModel shading = MaterialShadingModel.DefaultLit)
        {
            var graph = new MaterialEditorGraph
            {
                Name = name,
                BlendMode = blend,
                ShadingModel = shading
            };
            // 自动添加主输出节点
            graph.CreateNode(EditorNodeType.MasterOutput, "Material Output", new Vector2(600, 100));
            CurrentGraph = graph;
            return graph;
        }

        /// <summary>打开已有材质图文件。</summary>
        public MaterialEditorGraph OpenMaterial(string path)
        {
            CurrentGraph = MaterialEditorGraph.Read(path);
            return CurrentGraph;
        }

        /// <summary>保存当前材质图。</summary>
        public void SaveCurrent(string path)
        {
            if (CurrentGraph == null)
                throw new InvalidOperationException("当前没有打开的材质图。");
            CurrentGraph.Write(path);
        }

        // ── 默认材质创建 ────────────────────────────────────────────────

        /// <summary>创建一个默认 PBR 材质图(常量色 + 金属度/粗糙度)。</summary>
        public MaterialEditorGraph CreateDefaultPbr(string name)
        {
            var graph = CreateNewMaterial(name);
            var color = graph.CreateNode(EditorNodeType.ConstantColor, "Base Color", new Vector2(0, 0));
            color.GetOutput("RGB")!.DefaultValue = new Vector4(0.8f, 0.8f, 0.8f, 1);
            var metallic = graph.CreateNode(EditorNodeType.Constant, "Metallic", new Vector2(0, 100));
            metallic.GetOutput("Value")!.DefaultValue = new Vector4(0, 0, 0, 0);
            var roughness = graph.CreateNode(EditorNodeType.Constant, "Roughness", new Vector2(0, 200));
            roughness.GetOutput("Value")!.DefaultValue = new Vector4(0.5f, 0.5f, 0.5f, 0.5f);

            var master = graph.GetMasterNode()!;
            graph.Connect(color.Id, color.GetOutput("RGB")!.Id, master.Id, master.GetInput(MaterialProperty.BaseColor.ToString())!.Id);
            graph.Connect(metallic.Id, metallic.GetOutput("Value")!.Id, master.Id, master.GetInput(MaterialProperty.Metallic.ToString())!.Id);
            graph.Connect(roughness.Id, roughness.GetOutput("Value")!.Id, master.Id, master.GetInput(MaterialProperty.Roughness.ToString())!.Id);
            return graph;
        }

        // ── 材质函数库 ──────────────────────────────────────────────────

        /// <summary>注册材质函数到函数库。</summary>
        public void RegisterFunction(MaterialFunction function)
        {
            var errors = function.Validate();
            if (errors.Count > 0)
                throw new InvalidOperationException("材质函数无效: " + string.Join("; ", errors));
            _functionLibrary[function.Name] = function;
        }

        /// <summary>从函数库中移除材质函数。</summary>
        public bool UnregisterFunction(string name) => _functionLibrary.Remove(name);

        /// <summary>获取材质函数。</summary>
        public MaterialFunction? GetFunction(string name) =>
            _functionLibrary.TryGetValue(name, out var f) ? f : null;

        // ── 参数集合 ────────────────────────────────────────────────────

        /// <summary>注册材质参数集合。</summary>
        public void RegisterParameterCollection(MaterialParameterCollection collection)
        {
            _parameterCollections[collection.Name] = collection;
        }

        /// <summary>获取参数集合。</summary>
        public MaterialParameterCollection? GetParameterCollection(string name) =>
            _parameterCollections.TryGetValue(name, out var c) ? c : null;

        /// <summary>更新参数集合中某参数的运行时值(供游戏逻辑调用)。</summary>
        public void UpdateParameter(string collectionName, string paramName, Vector4 value)
        {
            if (_parameterCollections.TryGetValue(collectionName, out var col))
                col.SetValue(paramName, value);
        }

        // ── 材质实例 ────────────────────────────────────────────────────

        /// <summary>基于当前材质图创建材质实例。</summary>
        public MaterialInstance CreateInstance(string instanceName, string parentMaterialPath)
        {
            var inst = new MaterialInstance
            {
                Name = instanceName,
                ParentMaterialPath = parentMaterialPath
            };
            _instances[instanceName] = inst;
            return inst;
        }

        /// <summary>获取材质实例。</summary>
        public MaterialInstance? GetInstance(string name) =>
            _instances.TryGetValue(name, out var i) ? i : null;

        // ── 与 RHI 着色器编译对接 ───────────────────────────────────────

        /// <summary>
        /// 编译当前材质图为着色器字节码，并通过 RHI 设备创建图形管线状态对象(PSO)。
        /// 实际的节点图 -> HLSL 转译由 <see cref="ShaderGraphCompiler"/> 完成。
        /// 此处负责协调编译器与 RHI 设备，并应用混合模式/着色模型对应的渲染状态。
        /// </summary>
        /// <param name="compiler">着色器图编译器实例。</param>
        /// <param name="variantKeywords">本次编译启用的关键字(变体选择)。</param>
        /// <param name="shaderModel">目标着色器模型。</param>
        /// <returns>已创建的 PSO；若编译失败则返回 null。</returns>
        public RHIPipelineState? CompileCurrentMaterial(ShaderGraphCompiler compiler,
                                                        HashSet<string>? variantKeywords = null,
                                                        RHIShaderModel shaderModel = RHIShaderModel.SM_6_6)
        {
            if (CurrentGraph == null)
                throw new InvalidOperationException("当前没有可编译的材质图。");

            var errors = CurrentGraph.Validate();
            if (errors.Count > 0)
                throw new InvalidOperationException("材质图校验失败: " + string.Join("; ", errors));

            // 步骤 1：将节点图编译为 HLSL 源码
            var compileResult = compiler.Compile(CurrentGraph, new ShaderCompileOptions
            {
                TargetShaderModel = shaderModel,
                Keywords = variantKeywords ?? new HashSet<string>(),
                GenerateVertexShader = true,
                GeneratePixelShader = true,
                GenerateMeshShader = _device.SupportsMeshShaders
            });

            if (!compileResult.Success || compileResult.VertexShaderSource == null || compileResult.PixelShaderSource == null)
            {
                System.Diagnostics.Debug.WriteLine("材质编译失败: " + compileResult.ErrorMessage);
                return null;
            }

            // 步骤 2：通过 RHI 设备将 HLSL 源码编译为字节码
            var vsBytecode = _device.CompileShader(compileResult.VertexShaderSource, "VSMain",
                                                    RHIShaderStage.Vertex, shaderModel);
            var psBytecode = _device.CompileShader(compileResult.PixelShaderSource, "PSMain",
                                                    RHIShaderStage.Pixel, shaderModel);

            if (!vsBytecode.IsValid || !psBytecode.IsValid)
            {
                System.Diagnostics.Debug.WriteLine("RHI 着色器编译失败。");
                return null;
            }

            // 步骤 3：根据混合模式与着色模型配置 PSO 渲染状态
            var pipelineDesc = new RHIGraphicsPipelineDesc
            {
                Name = CurrentGraph.Name,
                VS = vsBytecode,
                PS = psBytecode,
                PrimitiveTopology = RHIPrimitiveTopology.TriangleList,
                InputLayout = BuildDefaultInputLayout(),
                RTVFormats = new[] { RHIFormat.R8G8B8A8_UNorm },
                DSVFormat = RHIFormat.D32_Float
            };

            // 根据混合模式设置 BlendState 与 DepthStencilState
            ApplyBlendMode(pipelineDesc, CurrentGraph.BlendMode, CurrentGraph.DoubleSided);

            // 若支持 Mesh Shader 且图需要，可改用 Mesh PSO 路径
            if (compileResult.GenerateMeshShader && _device.SupportsMeshShaders &&
                compileResult.MeshShaderSource != null)
            {
                var msBytecode = _device.CompileShader(compileResult.MeshShaderSource, "MSMain",
                                                        RHIShaderStage.Mesh, shaderModel);
                if (msBytecode.IsValid)
                {
                    var meshDesc = new RHIMeshPipelineDesc
                    {
                        Name = CurrentGraph.Name + "_Mesh",
                        MS = msBytecode,
                        PS = psBytecode,
                        RTVFormats = pipelineDesc.RTVFormats,
                        DSVFormat = pipelineDesc.DSVFormat
                    };
                    ApplyBlendModeMesh(meshDesc, CurrentGraph.BlendMode, CurrentGraph.DoubleSided);
                    return _device.CreateMeshPipelineState(meshDesc);
                }
            }

            return _device.CreateGraphicsPipelineState(pipelineDesc);
        }

        /// <summary>根据混合模式应用图形 PSO 的混合/深度/光栅化状态。</summary>
        private static void ApplyBlendMode(RHIGraphicsPipelineDesc desc, MaterialBlendMode mode, bool doubleSided)
        {
            desc.RasterizerState = doubleSided ? RHIRasterizerDesc.NoCull : RHIRasterizerDesc.Default;

            switch (mode)
            {
                case MaterialBlendMode.Opaque:
                    desc.BlendState = RHIBlendDesc.Default;
                    desc.DepthStencilState = RHIDepthDesc.DepthWrite;
                    break;

                case MaterialBlendMode.Masked:
                    // Masked 与 Opaque 类似，但 PS 内部执行 clip(alpha - clipValue)
                    desc.BlendState = RHIBlendDesc.Default;
                    desc.DepthStencilState = RHIDepthDesc.DepthWrite;
                    break;

                case MaterialBlendMode.Translucent:
                    desc.BlendState = RHIBlendDesc.AlphaBlend;
                    // 半透明不写深度，但仍做深度测试以正确排序
                    desc.DepthStencilState = RHIDepthDesc.DepthRead;
                    break;

                case MaterialBlendMode.Additive:
                    desc.BlendState = RHIBlendDesc.AdditiveBlend;
                    desc.DepthStencilState = RHIDepthDesc.DepthRead;
                    break;
            }
        }

        /// <summary>Mesh 管线的混合模式应用(与图形管线逻辑一致)。</summary>
        private static void ApplyBlendModeMesh(RHIMeshPipelineDesc desc, MaterialBlendMode mode, bool doubleSided)
        {
            desc.RasterizerState = doubleSided ? RHIRasterizerDesc.NoCull : RHIRasterizerDesc.Default;
            switch (mode)
            {
                case MaterialBlendMode.Opaque:
                case MaterialBlendMode.Masked:
                    desc.BlendState = RHIBlendDesc.Default;
                    desc.DepthStencilState = RHIDepthDesc.DepthWrite;
                    break;
                case MaterialBlendMode.Translucent:
                    desc.BlendState = RHIBlendDesc.AlphaBlend;
                    desc.DepthStencilState = RHIDepthDesc.DepthRead;
                    break;
                case MaterialBlendMode.Additive:
                    desc.BlendState = RHIBlendDesc.AdditiveBlend;
                    desc.DepthStencilState = RHIDepthDesc.DepthRead;
                    break;
            }
        }

        /// <summary>构建默认顶点输入布局(Position/Normal/TexCoord)。</summary>
        private static RHIInputElement[] BuildDefaultInputLayout()
        {
            return new[]
            {
                new RHIInputElement("POSITION", RHIFormat.R32G32B32_Float, 0, 0),
                new RHIInputElement("NORMAL", RHIFormat.R32G32B32_Float, 0, 12),
                new RHIInputElement("TEXCOORD", RHIFormat.R32G32_Float, 0, 24)
            };
        }

        // ── 预览渲染 ────────────────────────────────────────────────────

        /// <summary>
        /// 渲染当前材质的预览球。会先确保材质已编译为 PSO。
        /// </summary>
        public void RenderPreview(RHICommandList cmdList, RHIPipelineState pso,
                                  RHIRootSignature rootSig, Vector4 clearColor)
        {
            _previewRenderer?.Render(cmdList, pso, rootSig, clearColor);
        }

        // ── 释放 ────────────────────────────────────────────────────────

        /// <summary>释放编辑器系统持有的资源。</summary>
        public void Dispose()
        {
            _previewRenderer?.Dispose();
        }
    }
}
