// =============================================================================
//  Yama Engine - MaterialGraph.cs
//  yama引擎 - 材质图编辑器后端
//
//  本文件实现完整的材质图系统，涵盖:
//   1. MaterialNode / MaterialNodeType —— 节点体系 (14 种内置节点)
//   2. Pin / PinDirection / PinDataType —— 引脚定义与数据类型
//   3. MaterialEdge —— 节点连接
//   4. MaterialGraph —— 材质图 (拓扑/校验/序列化)
//   5. MaterialParameter —— 材质参数 (Scalar/Vector/Texture/CubeMap)
//   6. MaterialInstance —— 材质实例 (基于父材质的参数覆盖)
//   7. ShaderCodeGen —— 从节点图生成 HLSL 代码
//   8. ShaderPreview —— 预览编译 (生成 PbrMaterial 快照)
//
//  设计原则:
//   * 所有节点类型通过 Pin 描述输入输出，支持类型安全连接校验
//   * 图拓扑支持环检测、拓扑排序、可达性分析
//   * ShaderCodeGen 逐节点生成内联 HLSL 表达式，按拓扑序拼接
//   * MaterialInstance 支持运行时参数覆盖，不修改父材质
// =============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using YamaEngine.Assets;
using YamaEngine.Scripting;

namespace YamaEngine.Materials
{
    // ════════════════════════════════════════════════════════════════════════
    // 1. 引脚定义
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>引脚方向。</summary>

    /// <summary>引脚数据类型，决定节点间连接的类型兼容性。</summary>
    public enum PinDataType
    {
        Float,      // float  (标量)
        Float2,     // float2
        Float3,     // float3
        Float4,     // float4
        Texture2D,  // Texture2D (纹理引用)
        CubeMap,    // TextureCube (立方体贴图)
        Bool        // bool (静态开关)
    }

    /// <summary>
    /// 引脚：定义节点的一个输入或输出端口。
    /// 每个引脚具有唯一名称、数据类型和方向，以及可选默认值。
    /// </summary>
    public sealed class Pin
    {
        /// <summary>引脚唯一标识。</summary>
        public string Id { get; set; } = string.Empty;

        /// <summary>引脚显示名称(如 "BaseColor", "UV", "Result")。</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>引脚方向(输入/输出)。</summary>
        public PinDirection Direction { get; set; } = PinDirection.Input;

        /// <summary>引脚数据类型。</summary>
        public PinDataType DataType { get; set; } = PinDataType.Float;

        /// <summary>默认值(未连接时的回退值)。</summary>
        public Vector4 DefaultValue { get; set; } = new Vector4(0, 0, 0, 1);

        /// <summary>默认的 HLSL 字面量表达式。</summary>
        public string DefaultLiteral
        {
            get
            {
                return DataType switch
                {
                    PinDataType.Float => $"{DefaultValue.X:G9}",
                    PinDataType.Float2 => $"float2({DefaultValue.X:G9}, {DefaultValue.Y:G9})",
                    PinDataType.Float3 => $"float3({DefaultValue.X:G9}, {DefaultValue.Y:G9}, {DefaultValue.Z:G9})",
                    PinDataType.Float4 => $"float4({DefaultValue.X:G9}, {DefaultValue.Y:G9}, {DefaultValue.Z:G9}, {DefaultValue.W:G9})",
                    PinDataType.Bool => "false",
                    _ => "0"
                };
            }
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // 2. 节点类型枚举
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 材质节点类型。覆盖常量、数学、纹理、PBR 主节点等。
    /// </summary>
    public enum MaterialNodeType
    {
        // ── 常量节点 ──
        Constant,          // 标量常量 (float)
        ConstantFloat = Constant, // 标量常量别名 (float)
        ConstantColor,      // 向量常量 (float4)
        ConstantBool,      // 布尔常量 (用于静态开关)

        // ── 数学节点 ──
        Add,               // A + B
        Subtract,          // A - B
        Multiply,          // A * B
        Divide,            // A / B
        Power,             // Base^Exp
        Lerp,              // lerp(A, B, T)
        Sine,              // sin(X)
        Cosine,            // cos(X)
        OneMinus,          // 1 - X
        Saturate,          // saturate(X)
        Abs,               // abs(X)
        Floor,             // floor(X)
        Ceil,              // ceil(X)
        Frac,              // frac(X)
        Clamp,             // clamp(X, Min, Max)
        Dot,               // dot(A, B)
        Cross,             // cross(A, B)
        Normalize,         // normalize(X)
        Length,            // length(X)
        Reflect,           // reflect(I, N)

        // ── 向量数学 ──
        VectorMath,         // 通用向量运算 (Add/Sub/Mul/Div 支持逐分量)

        // ── 纹理节点 ──
        TextureSample,     // 纹理采样 (Texture2D.Sample)
        NormalMap,         // 法线贴图采样 + 切线空间解码
        CubeMapSample,    // 立方体贴图采样

        // ── 坐标节点 ──
        TexCoord,          // UV 坐标输出 (UV0~UV7)
        PixelPosition,     // 屏幕像素坐标
        WorldPosition,     // 世界空间位置
        ViewDirection,     // 视线方向

        // ── 工具节点 ──
        Mask,              // 通道遮罩 (RGBA 选分)
        Append,            // 通道拼接
        ComponentMask,    // 分量提取

        // ── PBR 主节点 ──
        PbrMaster,         // PBR 材质主输出节点

        // ── 时间节点 ──
        Time,              // 引擎时间 (秒)
        SineWave           // 正弦波 (Frequency + Phase)
    }

    // ════════════════════════════════════════════════════════════════════════
    // 3. 材质节点
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 材质节点：材质图的基本运算单元。
    /// 每个节点具有类型、输入/输出引脚集和参数字典。
    /// </summary>
    public sealed class MaterialNode
    {
        private static int _counter = 0;

        /// <summary>节点唯一标识 (GUID 格式)。</summary>
        public string Id { get; set; } = System.Guid.NewGuid().ToString("N");

        /// <summary>节点显示名称。</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>节点类型。</summary>
        public MaterialNodeType Type { get; set; }

        /// <summary>节点在编辑器中的位置 (用于布局还原)。</summary>
        public Vector2 Position { get; set; }

        /// <summary>节点是否启用 (禁用的节点在求值和编译时被跳过)。</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>标量常量值 (Constant 节点使用)。</summary>
        public float FloatValue { get; set; }

        /// <summary>向量常量值 (ConstantColor 节点使用)。</summary>
        public Vector4 VectorValue { get; set; } = new Vector4(1, 1, 1, 1);

        /// <summary>布尔常量值 (ConstantBool 节点使用)。</summary>
        public bool BoolValue { get; set; }

        /// <summary>纹理资源路径 (TextureSample / NormalMap 节点使用)。</summary>
        public string TexturePath { get; set; } = string.Empty;

        /// <summary>立方体贴图资源路径。</summary>
        public string CubeMapPath { get; set; } = string.Empty;

        /// <summary>UV 通道索引 (TexCoord 节点使用，0~7)。</summary>
        public int UVChannel { get; set; }

        /// <summary>自定义属性字典 (用于扩展参数或关键字绑定)。</summary>
        public Dictionary<string, string> Properties { get; set; } = new();

        /// <summary>输入引脚列表。</summary>
        public List<Pin> InputPins { get; set; } = new();

        /// <summary>输出引脚列表。</summary>
        public List<Pin> OutputPins { get; set; } = new();

        /// <summary>
        /// 根据节点类型初始化默认引脚。
        /// 构造后必须调用此方法以确保引脚完整。
        /// </summary>
        public MaterialNode InitializePins()
        {
            InputPins.Clear();
            OutputPins.Clear();

            switch (Type)
            {
                case MaterialNodeType.Constant:
                    AddOutput("Value", PinDataType.Float, new Vector4(0, 0, 0, 1));
                    break;

                case MaterialNodeType.ConstantColor:
                    AddOutput("Color", PinDataType.Float4, new Vector4(1, 1, 1, 1));
                    AddOutput("RGB", PinDataType.Float3, new Vector4(1, 1, 1, 1));
                    break;

                case MaterialNodeType.ConstantBool:
                    AddOutput("Value", PinDataType.Bool, new Vector4(0, 0, 0, 1));
                    break;

                case MaterialNodeType.Add:
                    AddInput("A", PinDataType.Float, new Vector4(0, 0, 0, 0));
                    AddInput("B", PinDataType.Float, new Vector4(0, 0, 0, 0));
                    AddOutput("Result", PinDataType.Float, new Vector4(0, 0, 0, 0));
                    break;

                case MaterialNodeType.Subtract:
                    AddInput("A", PinDataType.Float, new Vector4(0, 0, 0, 0));
                    AddInput("B", PinDataType.Float, new Vector4(0, 0, 0, 0));
                    AddOutput("Result", PinDataType.Float, new Vector4(0, 0, 0, 0));
                    break;

                case MaterialNodeType.Multiply:
                    AddInput("A", PinDataType.Float, new Vector4(1, 0, 0, 0));
                    AddInput("B", PinDataType.Float, new Vector4(1, 0, 0, 0));
                    AddOutput("Result", PinDataType.Float, new Vector4(1, 0, 0, 0));
                    break;

                case MaterialNodeType.Divide:
                    AddInput("A", PinDataType.Float, new Vector4(0, 0, 0, 0));
                    AddInput("B", PinDataType.Float, new Vector4(1, 0, 0, 0));
                    AddOutput("Result", PinDataType.Float, new Vector4(0, 0, 0, 0));
                    break;

                case MaterialNodeType.Power:
                    AddInput("Base", PinDataType.Float, new Vector4(2, 0, 0, 0));
                    AddInput("Exp", PinDataType.Float, new Vector4(1, 0, 0, 0));
                    AddOutput("Result", PinDataType.Float, new Vector4(1, 0, 0, 0));
                    break;

                case MaterialNodeType.Lerp:
                    AddInput("A", PinDataType.Float, new Vector4(0, 0, 0, 0));
                    AddInput("B", PinDataType.Float, new Vector4(1, 0, 0, 0));
                    AddInput("T", PinDataType.Float, new Vector4(0.5f, 0, 0, 0));
                    AddOutput("Result", PinDataType.Float, new Vector4(0.5f, 0, 0, 0));
                    break;

                case MaterialNodeType.Sine:
                    AddInput("X", PinDataType.Float, new Vector4(0, 0, 0, 0));
                    AddOutput("Result", PinDataType.Float, new Vector4(0, 0, 0, 0));
                    break;

                case MaterialNodeType.Cosine:
                    AddInput("X", PinDataType.Float, new Vector4(0, 0, 0, 0));
                    AddOutput("Result", PinDataType.Float, new Vector4(0, 0, 0, 0));
                    break;

                case MaterialNodeType.OneMinus:
                    AddInput("X", PinDataType.Float, new Vector4(1, 0, 0, 0));
                    AddOutput("Result", PinDataType.Float, new Vector4(0, 0, 0, 0));
                    break;

                case MaterialNodeType.Saturate:
                    AddInput("X", PinDataType.Float, new Vector4(0, 0, 0, 0));
                    AddOutput("Result", PinDataType.Float, new Vector4(0, 0, 0, 0));
                    break;

                case MaterialNodeType.Abs:
                    AddInput("X", PinDataType.Float, new Vector4(0, 0, 0, 0));
                    AddOutput("Result", PinDataType.Float, new Vector4(0, 0, 0, 0));
                    break;

                case MaterialNodeType.Floor:
                    AddInput("X", PinDataType.Float, new Vector4(0, 0, 0, 0));
                    AddOutput("Result", PinDataType.Float, new Vector4(0, 0, 0, 0));
                    break;

                case MaterialNodeType.Ceil:
                    AddInput("X", PinDataType.Float, new Vector4(0, 0, 0, 0));
                    AddOutput("Result", PinDataType.Float, new Vector4(0, 0, 0, 0));
                    break;

                case MaterialNodeType.Frac:
                    AddInput("X", PinDataType.Float, new Vector4(0, 0, 0, 0));
                    AddOutput("Result", PinDataType.Float, new Vector4(0, 0, 0, 0));
                    break;

                case MaterialNodeType.Clamp:
                    AddInput("X", PinDataType.Float, new Vector4(0, 0, 0, 0));
                    AddInput("Min", PinDataType.Float, new Vector4(0, 0, 0, 0));
                    AddInput("Max", PinDataType.Float, new Vector4(1, 0, 0, 0));
                    AddOutput("Result", PinDataType.Float, new Vector4(0, 0, 0, 0));
                    break;

                case MaterialNodeType.Dot:
                    AddInput("A", PinDataType.Float3, new Vector4(Vector3.One, 0f));
                    AddInput("B", PinDataType.Float3, new Vector4(Vector3.One, 0f));
                    AddOutput("Result", PinDataType.Float, new Vector4(1, 0, 0, 0));
                    break;

                case MaterialNodeType.Cross:
                    AddInput("A", PinDataType.Float3, new Vector4(Vector3.UnitX, 0f));
                    AddInput("B", PinDataType.Float3, new Vector4(Vector3.UnitY, 0f));
                    AddOutput("Result", PinDataType.Float3, new Vector4(Vector3.UnitZ, 0f));
                    break;

                case MaterialNodeType.Normalize:
                    AddInput("X", PinDataType.Float3, new Vector4(Vector3.UnitX, 0f));
                    AddOutput("Result", PinDataType.Float3, new Vector4(Vector3.UnitX, 0f));
                    break;

                case MaterialNodeType.Length:
                    AddInput("X", PinDataType.Float3, new Vector4(Vector3.One, 0f));
                    AddOutput("Result", PinDataType.Float, new Vector4(1.732f, 0, 0, 0));
                    break;

                case MaterialNodeType.Reflect:
                    AddInput("Incident", PinDataType.Float3, new Vector4(Vector3.UnitZ, 0f));
                    AddInput("Normal", PinDataType.Float3, new Vector4(Vector3.UnitY, 0f));
                    AddOutput("Result", PinDataType.Float3, new Vector4(Vector3.UnitZ, 0f));
                    break;

                case MaterialNodeType.VectorMath:
                    AddInput("A", PinDataType.Float4, Vector4.One);
                    AddInput("B", PinDataType.Float4, Vector4.One);
                    AddOutput("Result", PinDataType.Float4, Vector4.One);
                    // Operation 通过 Properties["Op"] 指定: Add/Sub/Mul/Div
                    break;

                case MaterialNodeType.TextureSample:
                    AddInput("UV", PinDataType.Float2, new Vector4(Vector2.Zero, 0f, 0f));
                    AddOutput("RGBA", PinDataType.Float4, Vector4.One);
                    AddOutput("RGB", PinDataType.Float3, new Vector4(1, 1, 1, 0));
                    AddOutput("R", PinDataType.Float, new Vector4(1, 0, 0, 0));
                    AddOutput("G", PinDataType.Float, new Vector4(0, 1, 0, 0));
                    AddOutput("B", PinDataType.Float, new Vector4(0, 0, 1, 0));
                    AddOutput("A", PinDataType.Float, new Vector4(0, 0, 0, 1));
                    break;

                case MaterialNodeType.NormalMap:
                    AddInput("UV", PinDataType.Float2, new Vector4(Vector2.Zero, 0f, 0f));
                    AddOutput("Normal", PinDataType.Float3, new Vector4(0.5f, 0.5f, 1, 0));
                    break;

                case MaterialNodeType.CubeMapSample:
                    AddInput("Direction", PinDataType.Float3, new Vector4(Vector3.UnitZ, 0f));
                    AddOutput("Color", PinDataType.Float4, Vector4.One);
                    break;

                case MaterialNodeType.TexCoord:
                    AddOutput("UV", PinDataType.Float2, new Vector4(Vector2.Zero, 0f, 0f));
                    break;

                case MaterialNodeType.PixelPosition:
                    AddOutput("XY", PinDataType.Float2, new Vector4(Vector2.Zero, 0f, 0f));
                    break;

                case MaterialNodeType.WorldPosition:
                    AddOutput("Position", PinDataType.Float3, new Vector4(Vector3.Zero, 0f));
                    break;

                case MaterialNodeType.ViewDirection:
                    AddOutput("Direction", PinDataType.Float3, new Vector4(Vector3.UnitZ, 0f));
                    break;

                case MaterialNodeType.Mask:
                    AddInput("Input", PinDataType.Float4, Vector4.One);
                    AddOutput("Result", PinDataType.Float4, Vector4.One);
                    break;

                case MaterialNodeType.Append:
                    AddInput("X", PinDataType.Float, Vector4.Zero);
                    AddInput("Y", PinDataType.Float, Vector4.Zero);
                    AddInput("Z", PinDataType.Float, Vector4.Zero);
                    AddInput("W", PinDataType.Float, Vector4.Zero);
                    AddOutput("Result", PinDataType.Float4, Vector4.Zero);
                    break;

                case MaterialNodeType.ComponentMask:
                    AddInput("Input", PinDataType.Float4, Vector4.One);
                    AddOutput("Result", PinDataType.Float3, new Vector4(Vector3.One, 0f));
                    break;

                case MaterialNodeType.PbrMaster:
                    // PBR 主节点输入: 各材质属性
                    AddInput("BaseColor", PinDataType.Float3, new Vector4(0.8f, 0.8f, 0.8f, 1));
                    AddInput("Metallic", PinDataType.Float, new Vector4(0, 0, 0, 0));
                    AddInput("Roughness", PinDataType.Float, new Vector4(0.5f, 0, 0, 0));
                    AddInput("Normal", PinDataType.Float3, new Vector4(0.5f, 0.5f, 1, 0));
                    AddInput("Emissive", PinDataType.Float3, new Vector4(Vector3.Zero, 0f));
                    AddInput("Opacity", PinDataType.Float, new Vector4(1, 0, 0, 0));
                    AddInput("AO", PinDataType.Float, new Vector4(1, 0, 0, 0));
                    AddInput("WorldPositionOffset", PinDataType.Float3, new Vector4(Vector3.Zero, 0f));
                    // 无输出 (终端节点)
                    break;

                case MaterialNodeType.Time:
                    AddOutput("Time", PinDataType.Float, Vector4.Zero);
                    break;

                case MaterialNodeType.SineWave:
                    AddInput("Frequency", PinDataType.Float, new Vector4(1, 0, 0, 0));
                    AddInput("Phase", PinDataType.Float, Vector4.Zero);
                    AddOutput("Result", PinDataType.Float, Vector4.Zero);
                    break;
            }

            return this;
        }

        private void AddInput(string name, PinDataType dataType, Vector4 defaultValue)
        {
            InputPins.Add(new Pin
            {
                Id = $"{Id}_in_{name}",
                Name = name,
                Direction = PinDirection.Input,
                DataType = dataType,
                DefaultValue = defaultValue
            });
        }

        private void AddOutput(string name, PinDataType dataType, Vector4 defaultValue)
        {
            OutputPins.Add(new Pin
            {
                Id = $"{Id}_out_{name}",
                Name = name,
                Direction = PinDirection.Output,
                DataType = dataType,
                DefaultValue = defaultValue
            });
        }

        /// <summary>按名称获取输入引脚 (大小写不敏感)。</summary>
        public Pin? GetInput(string name)
        {
            return InputPins.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>按名称获取输出引脚 (大小写不敏感)。</summary>
        public Pin? GetOutput(string name)
        {
            return OutputPins.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // 4. 节点连接
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 节点连接(边)：从源节点的输出引脚到目标节点的输入引脚。
    /// 一个输入引脚最多接受一条连接(最后连接的生效)。
    /// </summary>
    public sealed class MaterialEdge
    {
        /// <summary>边唯一标识。</summary>
        public string Id { get; set; } = System.Guid.NewGuid().ToString("N");

        /// <summary>源节点 ID。</summary>
        public string FromNodeId { get; set; } = string.Empty;

        /// <summary>源引脚 ID。</summary>
        public string FromPinId { get; set; } = string.Empty;

        /// <summary>目标节点 ID。</summary>
        public string ToNodeId { get; set; } = string.Empty;

        /// <summary>目标引脚 ID。</summary>
        public string ToPinId { get; set; } = string.Empty;
    }

    // ════════════════════════════════════════════════════════════════════════
    // 5. 材质参数
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 材质参数类型。
    /// </summary>
    public enum MaterialParameterType
    {
        Scalar,    // float
        Vector3,    // float3
        Vector4,    // float4
        Texture2D,  // Texture2D
        CubeMap     // TextureCube
    }

    /// <summary>
    /// 材质参数：定义材质图中可由外部(实例/引擎)设置的可变参数。
    /// 参数在编译后写入 cbuffer，运行时通过 RHI 更新。
    /// </summary>

    // ════════════════════════════════════════════════════════════════════════
    // 6. 材质实例
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 材质实例：基于父材质(MaterialGraph)的参数覆盖。
    /// 不修改父材质本身，仅覆盖指定参数的值。
    /// 运行时通过此机制实现同一材质的不同外观变体。
    /// </summary>

    /// <summary>材质参数覆盖值。</summary>
    public sealed class MaterialParameterOverride
    {
        public string ParameterName { get; set; } = string.Empty;
        public MaterialParameterType OverrideType { get; set; }
        public float FloatValue { get; set; }
        public Vector4 VectorValue { get; set; }
        public string TexturePath { get; set; } = string.Empty;
    }

    // ════════════════════════════════════════════════════════════════════════
    // 7. 材质图
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 材质图：完整的材质编辑器后端数据模型。
    /// 包含节点集、边集、材质参数和编译选项。
    /// 支持拓扑校验、序列化和着色器代码生成。
    /// </summary>
    public sealed class MaterialGraph
    {
        /// <summary>序列化版本。</summary>
        public int Version { get; set; } = 2;

        /// <summary>材质图唯一标识。</summary>
        public string Guid { get; set; } = System.Guid.NewGuid().ToString("N");

        /// <summary>材质图名称。</summary>
        public string Name { get; set; } = "Material Graph";

        /// <summary>节点列表。</summary>
        public List<MaterialNode> Nodes { get; set; } = new();

        /// <summary>连接(边)列表。</summary>
        public List<MaterialEdge> Edges { get; set; } = new();

        /// <summary>材质参数列表 (编译后写入 cbuffer)。</summary>
        public List<MaterialParameter> Parameters { get; set; } = new();

        /// <summary>静态关键字列表 (用于 shader variant 生成)。</summary>
        public List<ShaderKeyword> Keywords { get; set; } = new();

        /// <summary>混合模式。</summary>
        public MaterialBlendMode BlendMode { get; set; } = MaterialBlendMode.Opaque;

        /// <summary>着色模型。</summary>
        public MaterialShadingModel ShadingModel { get; set; } = MaterialShadingModel.DefaultLit;

        /// <summary>双面渲染。</summary>
        public bool DoubleSided { get; set; }

        /// <summary>Alpha 裁剪阈值 (Masked 模式)。</summary>
        public float OpacityMaskClipValue { get; set; } = 0.5f;

        /// <summary>自定义 include 文件列表。</summary>
        public List<string> Includes { get; set; } = new();

        /// <summary>自定义 defines 列表。</summary>
        public List<string> CustomDefines { get; set; } = new();

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            IncludeFields = true,
            PropertyNameCaseInsensitive = true
        };

        // ── 工厂方法 ──────────────────────────────────────────────────

        /// <summary>创建空白材质图。</summary>
        public static MaterialGraph Create(string name)
        {
            return new MaterialGraph
            {
                Name = string.IsNullOrWhiteSpace(name) ? "Material Graph" : name.Trim()
            };
        }

        /// <summary>创建默认 PBR 材质图。</summary>
        public static MaterialGraph CreateDefaultPbr(string name)
        {
            var graph = Create(name);

            var color = graph.AddNode(MaterialNodeType.ConstantColor, "Base Color");
            color.VectorValue = new Vector4(0.8f, 0.8f, 0.8f, 1f);
            color.InitializePins();

            var metallic = graph.AddNode(MaterialNodeType.Constant, "Metallic");
            metallic.FloatValue = 0f;
            metallic.InitializePins();

            var roughness = graph.AddNode(MaterialNodeType.Constant, "Roughness");
            roughness.FloatValue = 0.5f;
            roughness.InitializePins();

            var master = graph.AddNode(MaterialNodeType.PbrMaster, "PBR Master");
            master.InitializePins();

            var colorOut = color.GetOutput("Color")!;
            var colorIn = master.GetInput("BaseColor")!;
            graph.Connect(color.Id, colorOut.Id, master.Id, colorIn.Id);

            var metOut = metallic.GetOutput("Value")!;
            var metIn = master.GetInput("Metallic")!;
            graph.Connect(metallic.Id, metOut.Id, master.Id, metIn.Id);

            var roughOut = roughness.GetOutput("Value")!;
            var roughIn = master.GetInput("Roughness")!;
            graph.Connect(roughness.Id, roughOut.Id, master.Id, roughIn.Id);

            return graph;
        }

        // ── 节点/连接管理 ──────────────────────────────────────────────

        /// <summary>添加节点并初始化引脚。</summary>
        public MaterialNode AddNode(MaterialNodeType type, string name)
        {
            var node = new MaterialNode
            {
                Id = System.Guid.NewGuid().ToString("N"),
                Type = type,
                Name = string.IsNullOrWhiteSpace(name) ? type.ToString() : name.Trim()
            };
            node.InitializePins();
            Nodes.Add(node);
            return node;
        }

        /// <summary>移除节点及其所有连接。</summary>
        public bool RemoveNode(string nodeId)
        {
            var node = Nodes.FirstOrDefault(n => n.Id == nodeId);
            if (node == null) return false;
            Edges.RemoveAll(e => e.FromNodeId == nodeId || e.ToNodeId == nodeId);
            Nodes.Remove(node);
            return true;
        }

        /// <summary>添加连接。</summary>
        public MaterialEdge Connect(string fromNodeId, string fromPinId, string toNodeId, string toPinId)
        {
            // 移除目标引脚已有的连接 (一个输入引脚只接受一条连接)
            Edges.RemoveAll(e => e.ToNodeId == toNodeId && e.ToPinId == toPinId);

            var edge = new MaterialEdge
            {
                Id = System.Guid.NewGuid().ToString("N"),
                FromNodeId = fromNodeId,
                FromPinId = fromPinId,
                ToNodeId = toNodeId,
                ToPinId = toPinId
            };
            Edges.Add(edge);
            return edge;
        }

        /// <summary>通过引脚名称添加连接(便捷方法)。</summary>
        public MaterialEdge ConnectByPinName(string fromNodeId, string fromSocket, string toNodeId, string toSocket)
        {
            var fromNode = Nodes.FirstOrDefault(n => n.Id == fromNodeId);
            var toNode = Nodes.FirstOrDefault(n => n.Id == toNodeId);
            if (fromNode == null || toNode == null)
                throw new ArgumentException("Source or target node not found.");

            var fromPin = fromNode.GetOutput(fromSocket)
                ?? throw new ArgumentException($"Output pin '{fromSocket}' not found on node '{fromNode.Name}'.");
            var toPin = toNode.GetInput(toSocket)
                ?? throw new ArgumentException($"Input pin '{toSocket}' not found on node '{toNode.Name}'.");

            return Connect(fromNodeId, fromPin.Id, toNodeId, toPin.Id);
        }

        /// <summary>移除连接。</summary>
        public bool RemoveEdge(string edgeId)
        {
            return Edges.RemoveAll(e => e.Id == edgeId) > 0;
        }

        /// <summary>获取连接到指定输入引脚的边。</summary>
        public MaterialEdge? GetIncomingConnection(string nodeId, string inputPinId)
        {
            return Edges.LastOrDefault(e => e.ToNodeId == nodeId && e.ToPinId == inputPinId);
        }

        /// <summary>获取从指定输出引脚出发的所有边。</summary>
        public List<MaterialEdge> GetOutgoingConnections(string nodeId, string outputPinId)
        {
            return Edges.Where(e => e.FromNodeId == nodeId && e.FromPinId == outputPinId).ToList();
        }

        /// <summary>获取 PBR 主节点。</summary>
        public MaterialNode? GetMasterNode()
        {
            return Nodes.FirstOrDefault(n => n.Type == MaterialNodeType.PbrMaster && n.Enabled);
        }

        /// <summary>收集图中所有参数节点的参数。</summary>
        public void CollectParameters()
        {
            Parameters.Clear();
            foreach (var node in Nodes)
            {
                if (!node.Enabled) continue;
                if (node.Properties.TryGetValue("IsParameter", out var isParam) && isParam == "true")
                {
                    var paramName = node.Properties.GetValueOrDefault("ParamName", node.Name);
                    var pType = node.Properties.GetValueOrDefault("ParamType", "Scalar");
                    Parameters.Add(new MaterialParameter
                    {
                        Name = paramName,
                        DisplayName = node.Name,
                        ParameterType = pType switch
                        {
                            "Vector3" => MaterialParameterType.Vector3,
                            "Vector4" => MaterialParameterType.Vector4,
                            "Texture2D" => MaterialParameterType.Texture2D,
                            "CubeMap" => MaterialParameterType.CubeMap,
                            _ => MaterialParameterType.Scalar
                        },
                        FloatValue = node.FloatValue,
                        VectorValue = node.VectorValue,
                        TexturePath = node.TexturePath
                    });
                }
            }
        }

        // ── 预览求值 ─────────────────────────────────────────────────

        /// <summary>求值预览：从 PBR 主节点的 Metallic / Roughness 输入回溯常量值。</summary>
        public MaterialPreviewResult EvaluatePreview()
        {
            var result = new MaterialPreviewResult();
            var master = Nodes.FirstOrDefault(n => n.Type == MaterialNodeType.PbrMaster);
            if (master == null) return result;

            float ResolveInput(string pinId)
            {
                var edge = Edges.LastOrDefault(e => e.ToNodeId == master.Id && e.ToPinId == pinId);
                if (edge == null) return 0f;
                var src = Nodes.FirstOrDefault(n => n.Id == edge.FromNodeId);
                return src?.FloatValue ?? 0f;
            }

            result.Metallic = ResolveInput("Metallic");
            result.Roughness = ResolveInput("Roughness");
            return result;
        }

        // ── 校验 ──────────────────────────────────────────────────────

        /// <summary>校验材质图完整性，返回错误列表。</summary>
        public List<string> Validate()
        {
            var errors = new List<string>();

            if (string.IsNullOrWhiteSpace(Name))
                errors.Add("Material graph name is required.");
            if (!System.Guid.TryParse(Guid, out _))
                errors.Add("Material graph GUID is invalid.");

            if (GetMasterNode() == null)
                errors.Add("Material graph requires an enabled PBR Master node.");

            // 节点 ID 唯一性
            var ids = new HashSet<string>();
            foreach (var node in Nodes)
            {
                if (string.IsNullOrWhiteSpace(node.Id))
                    errors.Add($"Node '{node.Name}' has an empty id.");
                if (!ids.Add(node.Id))
                    errors.Add($"Duplicate node id: {node.Id}");

                // 校验引脚完整性
                if (node.InputPins.Count == 0 && node.OutputPins.Count == 0 && node.Type != MaterialNodeType.PbrMaster)
                    errors.Add($"Node '{node.Name}' ({node.Type}) has no pins. Did you call InitializePins()?");
            }

            // 连接合法性
            foreach (var edge in Edges)
            {
                var fromNode = Nodes.FirstOrDefault(n => n.Id == edge.FromNodeId);
                var toNode = Nodes.FirstOrDefault(n => n.Id == edge.ToNodeId);

                if (fromNode == null)
                {
                    errors.Add($"Edge source node not found: {edge.FromNodeId}");
                    continue;
                }
                if (toNode == null)
                {
                    errors.Add($"Edge target node not found: {edge.ToNodeId}");
                    continue;
                }

                var fromPin = fromNode.OutputPins.FirstOrDefault(p => p.Id == edge.FromPinId);
                var toPin = toNode.InputPins.FirstOrDefault(p => p.Id == edge.ToPinId);

                if (fromPin == null)
                    errors.Add($"Edge source pin '{edge.FromPinId}' not found on node '{fromNode.Name}'.");
                if (toPin == null)
                    errors.Add($"Edge target pin '{edge.ToPinId}' not found on node '{toNode.Name}'.");

                // 类型兼容性检查 (允许标量 -> 向量的自动广播)
                if (fromPin != null && toPin != null && !IsTypeCompatible(fromPin.DataType, toPin.DataType))
                    errors.Add($"Type mismatch: {fromPin.DataType} -> {toPin.DataType} on edge {edge.Id}.");
            }

            // 环检测
            if (HasCycle())
                errors.Add("Material graph contains a cycle.");

            return errors;
        }

        /// <summary>
        /// 检查引脚类型是否兼容 (支持标量广播到向量)。
        /// </summary>
        private static bool IsTypeCompatible(PinDataType from, PinDataType to)
        {
            // 标量可以广播到任意维度
            if (from == PinDataType.Float)
                return to == PinDataType.Float || to == PinDataType.Float2
                    || to == PinDataType.Float3 || to == PinDataType.Float4;

            // 同类型兼容
            if (from == to) return true;

            // 向量降维 (float4 -> float3 -> float2 -> float)
            int fromDim = DimensionOf(from);
            int toDim = DimensionOf(to);
            return fromDim > toDim && toDim > 0;
        }

        /// <summary>获取数据类型的维度数。</summary>
        public static int DimensionOf(PinDataType type)
        {
            return type switch
            {
                PinDataType.Float => 1,
                PinDataType.Float2 => 2,
                PinDataType.Float3 => 3,
                PinDataType.Float4 => 4,
                PinDataType.Bool => 0,
                PinDataType.Texture2D => 0,
                PinDataType.CubeMap => 0,
                _ => 0
            };
        }

        // ── 拓扑排序 ──────────────────────────────────────────────────

        /// <summary>
        /// 从 PBR 主节点出发，反向遍历所有输入连接，进行拓扑排序。
        /// 结果保证：任一节点排在所有数据依赖之前。
        /// </summary>
        public List<MaterialNode> TopologicalSort()
        {
            var master = GetMasterNode()
                ?? throw new InvalidOperationException("No PBR Master node found.");

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
                    var conn = GetIncomingConnection(node.Id, pin.Id);
                    if (conn == null) continue;
                    var src = Nodes.FirstOrDefault(n => n.Id == conn.FromNodeId);
                    if (src != null) Visit(src);
                }

                visiting.Remove(node.Id);
                visited.Add(node.Id);
                stack.Add(node);
            }

            Visit(master);
            return stack;
        }

        // ── 环检测 ──────────────────────────────────────────────────────

        private bool HasCycle()
        {
            var graph = Edges.GroupBy(e => e.FromNodeId).ToDictionary(
                g => g.Key, g => g.Select(e => e.ToNodeId).ToList());
            var visiting = new HashSet<string>();
            var visited = new HashSet<string>();

            bool Visit(string id)
            {
                if (visiting.Contains(id)) return true;
                if (visited.Contains(id)) return false;
                visiting.Add(id);
                if (graph.TryGetValue(id, out var next))
                {
                    foreach (var child in next)
                    {
                        if (Visit(child)) return true;
                    }
                }
                visiting.Remove(id);
                visited.Add(id);
                return false;
            }

            return Nodes.Any(node => Visit(node.Id));
        }

        // ── 序列化 ──────────────────────────────────────────────────────

        /// <summary>序列化到文件。</summary>
        public void Write(string path)
        {
            var errors = Validate();
            if (errors.Count > 0)
                throw new InvalidOperationException("Cannot write invalid material graph: " + string.Join("; ", errors));

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));

            AssetMeta.FromMetadata(new AssetMetadata
            {
                Guid = System.Guid.Parse(Guid),
                Name = Name,
                AssetPath = ToProjectAssetPath(path),
                SourceFile = path,
                Type = AssetType.Shader,
                FileSize = new FileInfo(path).Length,
                ImportedAt = DateTime.Now,
                LastModified = File.GetLastWriteTime(path),
                Hash = Guid,
                Properties =
                {
                    ["shaderGraph"] = true,
                    ["nodeCount"] = Nodes.Count,
                    ["edgeCount"] = Edges.Count,
                    ["shadingModel"] = ShadingModel.ToString(),
                    ["blendMode"] = BlendMode.ToString()
                }
            }).Write(path + ".shmeta");
        }

        /// <summary>从文件反序列化。</summary>
        public static MaterialGraph Read(string path)
        {
            var graph = JsonSerializer.Deserialize<MaterialGraph>(File.ReadAllText(path), JsonOptions)
                        ?? throw new InvalidOperationException($"Invalid material graph: {path}");

            // 确保所有节点引脚已初始化
            foreach (var node in graph.Nodes)
            {
                if (node.InputPins.Count == 0 && node.OutputPins.Count == 0)
                    node.InitializePins();
            }

            var errors = graph.Validate();
            if (errors.Count > 0)
                throw new InvalidOperationException("Invalid material graph: " + string.Join("; ", errors));
            return graph;
        }

        private static string ToProjectAssetPath(string path)
        {
            var normalized = Path.GetFullPath(path).Replace('\\', '/');
            var marker = "/Assets/";
            var index = normalized.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (index >= 0) return normalized[(index + 1)..];
            return Path.GetFileName(path);
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // 8. 着色器关键字
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 着色器静态关键字。控制 shader variant 的编译时分支。
    /// </summary>
    public sealed class ShaderKeyword
    {
        /// <summary>关键字名称 (用于 #define KW_XXX)。</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>关键字显示名称。</summary>
        public string DisplayName { get; set; } = string.Empty;

        /// <summary>默认是否启用。</summary>
        public bool DefaultValue { get; set; }
    }

    // ════════════════════════════════════════════════════════════════════════
    // 9. 枚举定义 (供 MaterialGraph / ShaderGraphCompiler 共用)
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>材质混合模式。</summary>

    /// <summary>材质着色模型。</summary>

    /// <summary>材质属性名 (对应 PbrMaster 的输入引脚)。</summary>

    // ════════════════════════════════════════════════════════════════════════
    // 10. 材质值 (用于 CPU 端预览求值)
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 材质值：表示节点求值的运行时结果。
    /// 支持标量、向量和纹理路径三种语义。
    /// </summary>
    public readonly record struct MaterialValue(float Float, Vector4 Vector, string TexturePath)
    {
        public MaterialValue(float value) : this(value, new Vector4(value, value, value, value), string.Empty) { }
        public MaterialValue(Vector4 value) : this(value.X, value, string.Empty) { }
        public MaterialValue(string texturePath) : this(0f, Vector4.Zero, texturePath) { }

        public MaterialValue Add(MaterialValue other)
        {
            return new MaterialValue(Float + other.Float, Vector + other.Vector, string.Empty);
        }

        public MaterialValue Subtract(MaterialValue other)
        {
            return new MaterialValue(Float - other.Float, Vector - other.Vector, string.Empty);
        }

        public MaterialValue Multiply(MaterialValue other)
        {
            return new MaterialValue(Float * other.Float, Vector * other.Vector, string.Empty);
        }

        public static MaterialValue Lerp(MaterialValue a, MaterialValue b, float t)
        {
            return new MaterialValue(
                a.Float + (b.Float - a.Float) * t,
                Vector4.Lerp(a.Vector, b.Vector, t),
                string.Empty);
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // 11. 材质图预览求值
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 材质图预览求值器：在 CPU 端模拟节点图求值，生成 PbrMaterial 快照。
    /// 用于编辑器实时预览，不依赖 GPU 编译。
    /// </summary>
    public static class ShaderPreview
    {
        /// <summary>
        /// 求值材质图，生成 PBR 材质预览。
        /// </summary>
        public static PbrMaterial Evaluate(MaterialGraph graph)
        {
            var errors = graph.Validate();
            if (errors.Count > 0)
                throw new InvalidOperationException("Cannot evaluate invalid material graph: " + string.Join("; ", errors));

            var master = graph.GetMasterNode()
                ?? throw new InvalidOperationException("No PBR Master node found.");

            var material = PbrMaterial.Create(graph.Name + "_Preview");

            material.BaseColor = ToColor3(EvaluateInput(graph, master, "BaseColor", new MaterialValue(new Vector4(0.8f, 0.8f, 0.8f, 1f))));
            material.Metallic = Clamp01(EvaluateInput(graph, master, "Metallic", new MaterialValue(0f)).Float);
            material.Roughness = Clamp01(EvaluateInput(graph, master, "Roughness", new MaterialValue(0.5f)).Float);
            material.Alpha = Clamp01(EvaluateInput(graph, master, "Opacity", new MaterialValue(1f)).Float);
            material.EmissiveColor = ToColor3(EvaluateInput(graph, master, "Emissive", new MaterialValue(Vector4.Zero)));
            material.AmbientOcclusion = Clamp01(EvaluateInput(graph, master, "AO", new MaterialValue(1f)).Float);

            return material;
        }

        private static MaterialValue EvaluateInput(MaterialGraph graph, MaterialNode node, string pinName, MaterialValue fallback)
        {
            var pin = node.GetInput(pinName);
            if (pin == null) return fallback;

            var conn = graph.GetIncomingConnection(node.Id, pin.Id);
            return conn == null ? fallback : EvaluateNode(graph, conn.FromNodeId);
        }

        private static MaterialValue EvaluateNode(MaterialGraph graph, string nodeId)
        {
            var node = graph.Nodes.FirstOrDefault(n => n.Id == nodeId)
                ?? throw new InvalidOperationException($"Node not found: {nodeId}");

            if (!node.Enabled) return new MaterialValue(0f);

            // Vector4 → Vector3 辅助（取 xyz 分量）
            static Vector3 Vec3(MaterialValue v) => new Vector3(v.Vector.X, v.Vector.Y, v.Vector.Z);

            return node.Type switch
            {
                MaterialNodeType.Constant => new MaterialValue(node.FloatValue),
                MaterialNodeType.ConstantColor => new MaterialValue(node.VectorValue),
                MaterialNodeType.ConstantBool => new MaterialValue(node.BoolValue ? 1f : 0f),
                MaterialNodeType.TextureSample => string.IsNullOrWhiteSpace(node.TexturePath)
                    ? new MaterialValue(node.VectorValue)
                    : new MaterialValue(node.TexturePath),
                MaterialNodeType.NormalMap => EvaluateInput(graph, node, "UV", new MaterialValue(new Vector4(Vector2.Zero, 0f, 0f))),

                MaterialNodeType.Add => EvaluateInput(graph, node, "A", new MaterialValue(0f))
                    .Add(EvaluateInput(graph, node, "B", new MaterialValue(0f))),
                MaterialNodeType.Subtract => EvaluateInput(graph, node, "A", new MaterialValue(0f))
                    .Subtract(EvaluateInput(graph, node, "B", new MaterialValue(0f))),
                MaterialNodeType.Multiply => EvaluateInput(graph, node, "A", new MaterialValue(1f))
                    .Multiply(EvaluateInput(graph, node, "B", new MaterialValue(1f))),
                MaterialNodeType.Divide => EvaluateInput(graph, node, "A", new MaterialValue(0f))
                    .Multiply(new MaterialValue(1f / Math.Max(EvaluateInput(graph, node, "B", new MaterialValue(1f)).Float, 1e-6f))),
                MaterialNodeType.Power => new MaterialValue(
                    (float)Math.Pow(Math.Max(EvaluateInput(graph, node, "Base", new MaterialValue(2f)).Float, 0f),
                        EvaluateInput(graph, node, "Exp", new MaterialValue(1f)).Float)),
                MaterialNodeType.Lerp => MaterialValue.Lerp(
                    EvaluateInput(graph, node, "A", new MaterialValue(0f)),
                    EvaluateInput(graph, node, "B", new MaterialValue(1f)),
                    Clamp01(EvaluateInput(graph, node, "T", new MaterialValue(0.5f)).Float)),

                MaterialNodeType.Sine => new MaterialValue(
                    (float)Math.Sin(EvaluateInput(graph, node, "X", new MaterialValue(0f)).Float)),
                MaterialNodeType.Cosine => new MaterialValue(
                    (float)Math.Cos(EvaluateInput(graph, node, "X", new MaterialValue(0f)).Float)),
                MaterialNodeType.OneMinus => new MaterialValue(
                    1f - EvaluateInput(graph, node, "X", new MaterialValue(1f)).Float),
                MaterialNodeType.Saturate => new MaterialValue(
                    Clamp01(EvaluateInput(graph, node, "X", new MaterialValue(0f)).Float)),
                MaterialNodeType.Abs => new MaterialValue(
                    Math.Abs(EvaluateInput(graph, node, "X", new MaterialValue(0f)).Float)),
                MaterialNodeType.Floor => new MaterialValue(
                    (float)Math.Floor(EvaluateInput(graph, node, "X", new MaterialValue(0f)).Float)),
                MaterialNodeType.Ceil => new MaterialValue(
                    (float)Math.Ceiling(EvaluateInput(graph, node, "X", new MaterialValue(0f)).Float)),
                MaterialNodeType.Frac => new MaterialValue(
                    EvaluateInput(graph, node, "X", new MaterialValue(0f)).Float
                    - (float)Math.Floor(EvaluateInput(graph, node, "X", new MaterialValue(0f)).Float)),
                MaterialNodeType.Clamp => new MaterialValue(
                    Math.Clamp(EvaluateInput(graph, node, "X", new MaterialValue(0f)).Float,
                        EvaluateInput(graph, node, "Min", new MaterialValue(0f)).Float,
                        EvaluateInput(graph, node, "Max", new MaterialValue(1f)).Float)),

                MaterialNodeType.Dot => new MaterialValue(
                    Vector3.Dot(
                        Vec3(EvaluateInput(graph, node, "A", new MaterialValue(new Vector4(Vector3.One, 0f)))),
                        Vec3(EvaluateInput(graph, node, "B", new MaterialValue(new Vector4(Vector3.One, 0f)))))),
                MaterialNodeType.Normalize => new MaterialValue(
                    new Vector4(Vector3.Normalize(Vec3(EvaluateInput(graph, node, "X", new MaterialValue(new Vector4(Vector3.One, 0f))))), 0f)),
                MaterialNodeType.Length => new MaterialValue(
                    Vec3(EvaluateInput(graph, node, "X", new MaterialValue(new Vector4(Vector3.One, 0f)))).Length()),
                MaterialNodeType.Cross => new MaterialValue(
                    new Vector4(Vector3.Cross(
                        Vec3(EvaluateInput(graph, node, "A", new MaterialValue(new Vector4(Vector3.UnitX, 0f)))),
                        Vec3(EvaluateInput(graph, node, "B", new MaterialValue(new Vector4(Vector3.UnitY, 0f))))), 0f)),
                MaterialNodeType.Reflect => new MaterialValue(
                    new Vector4(Vector3.Reflect(
                        Vec3(EvaluateInput(graph, node, "Incident", new MaterialValue(new Vector4(Vector3.UnitZ, 0f)))),
                        Vec3(EvaluateInput(graph, node, "Normal", new MaterialValue(new Vector4(Vector3.UnitY, 0f))))), 0f)),

                MaterialNodeType.VectorMath => EvaluateVectorMath(graph, node),

                MaterialNodeType.PbrMaster => new MaterialValue(Vector4.Zero),

                MaterialNodeType.Time => new MaterialValue(0f), // 运行时值
                MaterialNodeType.SineWave => new MaterialValue(0f), // 运行时值
                MaterialNodeType.TexCoord => new MaterialValue(new Vector4(Vector2.Zero, 0f, 0f)),
                MaterialNodeType.PixelPosition => new MaterialValue(new Vector4(Vector2.Zero, 0f, 0f)),
                MaterialNodeType.WorldPosition => new MaterialValue(new Vector4(Vector3.Zero, 0f)),
                MaterialNodeType.ViewDirection => new MaterialValue(new Vector4(Vector3.UnitZ, 0f)),
                MaterialNodeType.Mask => EvaluateInput(graph, node, "Input", new MaterialValue(Vector4.One)),
                MaterialNodeType.Append => new MaterialValue(new Vector4(
                    EvaluateInput(graph, node, "X", new MaterialValue(0f)).Float,
                    EvaluateInput(graph, node, "Y", new MaterialValue(0f)).Float,
                    EvaluateInput(graph, node, "Z", new MaterialValue(0f)).Float,
                    EvaluateInput(graph, node, "W", new MaterialValue(0f)).Float)),
                MaterialNodeType.ComponentMask => EvaluateInput(graph, node, "Input", new MaterialValue(Vector4.One)),

                MaterialNodeType.CubeMapSample => string.IsNullOrWhiteSpace(node.CubeMapPath)
                    ? new MaterialValue(node.VectorValue)
                    : new MaterialValue(node.CubeMapPath),

                _ => new MaterialValue(0f)
            };
        }

        private static MaterialValue EvaluateVectorMath(MaterialGraph graph, MaterialNode node)
        {
            var a = EvaluateInput(graph, node, "A", new MaterialValue(Vector4.One));
            var b = EvaluateInput(graph, node, "B", new MaterialValue(Vector4.One));
            var op = node.Properties.GetValueOrDefault("Op", "Add");

            return op.ToLowerInvariant() switch
            {
                "sub" or "subtract" => a.Subtract(b),
                "mul" or "multiply" => a.Multiply(b),
                "div" or "divide" => new MaterialValue(0f,
                    new Vector4(
                        b.Vector.X != 0 ? a.Vector.X / b.Vector.X : 0,
                        b.Vector.Y != 0 ? a.Vector.Y / b.Vector.Y : 0,
                        b.Vector.Z != 0 ? a.Vector.Z / b.Vector.Z : 0,
                        b.Vector.W != 0 ? a.Vector.W / b.Vector.W : 0),
                    string.Empty),
                _ => a.Add(b) // default: Add
            };
        }

        private static Vector3 ToColor3(MaterialValue value)
        {
            return new Vector3(value.Vector.X, value.Vector.Y, value.Vector.Z);
        }

        private static float Clamp01(float value)
        {
            return Math.Clamp(value, 0f, 1f);
        }
    }

    /// <summary>材质图预览求值结果。</summary>
    public sealed class MaterialPreviewResult
    {
        public float Metallic;
        public float Roughness;
    }
}
