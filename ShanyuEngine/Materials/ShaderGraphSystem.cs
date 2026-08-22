// =============================================================================
//  Shanyu Engine - ShaderGraphSystem.cs
//  山雨引擎 - 着色器图核心系统
//
//  本文件实现完整的可视化着色器图系统 (类似 UE Material Editor / Unity Shader Graph),
//  涵盖:
//    1. ShaderGraphPinType       —— 引脚数据类型枚举 (16种类型)
//    2. ShaderGraphPinDirection   —— 引脚方向枚举
//    3. ShaderGraphPin            —— 引脚 (ID/方向/数据类型/名称/默认值/连接/显示模式)
//    4. ShaderGraphConnection     —— 连接 (源节点+源引脚 -> 目标节点+目标引脚)
//    5. ShaderGraphNode           —— 节点基类 (ID/类型/位置/标题/输入输出引脚/预览值)
//    6. ShaderGraph               —— 着色器图 (节点列表/连接列表/属性列表/预览/编译)
//    7. ShaderGraphProperty       —— 材质图属性 (标量/向量/纹理/布尔)
//    8. 图验证 (类型检查/循环检测/未连接检查/材质属性检查)
//    9. 图序列化/反序列化 (JSON)
//   10. 图编译入口 (Compile -> HLSL 生成)
//
//  设计原则:
//    * 所有节点类型通过 ShaderGraphPin 描述输入输出, 支持类型安全连接校验
//    * 图拓扑支持环检测、拓扑排序、可达性分析
//    * 引脚预览: 每个引脚可携带小缩略图 (PinPreview), 用于编辑器可视化
//    * 与已有的 MaterialGraph / PbrMaterial 兼容, 可互相转换
//    * 使用 System.Numerics, 不引入新的 NuGet 依赖
//    * 命名空间: ShanyuEngine.Materials
// =============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ShanyuEngine.Materials
{
    // =========================================================================
    // 1. 引脚数据类型枚举
    // =========================================================================

    /// <summary>
    /// 着色器图引脚数据类型。覆盖 HLSL 所有常用标量/向量/矩阵/纹理/采样器类型。
    /// 类型兼容性规则:
    ///   * Float 可以隐式提升为 Float2/Float3/Float4/Color (标量广播)
    ///   * Float2/Float3/Float4 可以降维截取 (高维 -> 低维)
    ///   * Float3 <-> Color 互通 (颜色本质是 float3/float4)
    ///   * 纹理类型不能与标量/向量直接连接
    ///   * Bool 可以连接到任何类型 (用于静态开关)
    /// </summary>
    public enum ShaderGraphPinType
    {
        /// <summary>标量 float</summary>
        Float,
        /// <summary>二维向量 float2</summary>
        Float2,
        /// <summary>三维向量 float3</summary>
        Float3,
        /// <summary>四维向量 float4</summary>
        Float4,
        /// <summary>颜色 (线性空间 RGBA, 等价于 float4, 编辑器中显示为颜色拾取器)</summary>
        Color,
        /// <summary>2D 纹理 (Texture2D)</summary>
        Texture2D,
        /// <summary>立方体贴图 (TextureCube)</summary>
        TextureCube,
        /// <summary>3D 纹理 (Texture3D)</summary>
        Texture3D,
        /// <summary>采样器状态 (SamplerState)</summary>
        SamplerState,
        /// <summary>布尔值 (bool, 用于静态开关/条件分支)</summary>
        Bool,
        /// <summary>整数 (int)</summary>
        Int,
        /// <summary>3x3 矩阵 (float3x3)</summary>
        Matrix3x3,
        /// <summary>4x4 矩阵 (float4x4)</summary>
        Matrix4x4,
        /// <summary>自定义结构体 (由用户定义的字段集合)</summary>
        Struct,
        /// <summary>空类型 (void, 用于函数/事件节点的执行流引脚)</summary>
        Void,
        /// <summary>着色器函数引用 (指向另一个 ShaderGraph 或 HLSL 函数)</summary>
        ShaderFunction
    }

    /// <summary>
    /// 引脚方向。
    /// </summary>
    public enum ShaderGraphPinDirection
    {
        /// <summary>输入引脚: 接收数据。</summary>
        Input,
        /// <summary>输出引脚: 提供数据。</summary>
        Output
    }

    /// <summary>
    /// 引脚显示模式。控制引脚在编辑器中的视觉呈现方式。
    /// </summary>
    public enum ShaderGraphPinDisplayMode
    {
        /// <summary>默认: 仅显示引脚名称和数据类型。</summary>
        Default,
        /// <summary>显示为颜色拾取器 (仅 Color 类型引脚)。</summary>
        ColorPicker,
        /// <summary>显示为滑块 (仅 Float/Int 类型引脚)。</summary>
        Slider,
        /// <summary>显示为纹理预览缩略图 (仅 Texture2D/TextureCube/Texture3D)。</summary>
        TexturePreview,
        /// <summary>显示为开关/复选框 (仅 Bool 类型引脚)。</summary>
        Toggle,
        /// <summary>隐藏 (引脚存在但不显示, 用于内部连接)。</summary>
        Hidden,
        /// <summary>显示为下拉菜单 (用于枚举类型选择)。</summary>
        Dropdown
    }

    // =========================================================================
    // 2. 引脚预览
    // =========================================================================

    /// <summary>
    /// 引脚预览数据。携带节点输出值的可视化缩略图, 用于编辑器实时反馈。
    /// </summary>
    public sealed class ShaderGraphPinPreview
    {
        /// <summary>预览模式: 无预览 / 标量数值 / 颜色 / 纹理缩略图 / 小型图表。</summary>
        public ShaderGraphPreviewMode Mode { get; set; } = ShaderGraphPreviewMode.None;

        /// <summary>标量预览值 (用于 Float/Int/Bool 引脚)。</summary>
        public float ScalarValue { get; set; }

        /// <summary>向量预览值 (用于 Float2/Float3/Float4/Color 引脚)。</summary>
        public Vector4 VectorValue { get; set; } = new Vector4(0, 0, 0, 1);

        /// <summary>纹理预览缩略图路径 (用于 Texture2D/TextureCube 引脚)。</summary>
        public string? TextureThumbnailPath { get; set; }

        /// <summary>迷你图表数据 (用于噪声/曲线等函数预览, 最多 64 个采样点)。</summary>
        public float[]? ChartData { get; set; }

        /// <summary>预览是否过期 (节点或上游连接发生变化后置为 true)。</summary>
        public bool Dirty { get; set; } = true;

        /// <summary>创建空预览。</summary>
        public static ShaderGraphPinPreview Empty => new();

        /// <summary>创建标量预览。</summary>
        public static ShaderGraphPinPreview FromScalar(float value) => new()
        {
            Mode = ShaderGraphPreviewMode.Scalar,
            ScalarValue = value,
            VectorValue = new Vector4(value, value, value, value)
        };

        /// <summary>创建颜色预览。</summary>
        public static ShaderGraphPinPreview FromColor(Vector4 color) => new()
        {
            Mode = ShaderGraphPreviewMode.Color,
            VectorValue = color
        };

        /// <summary>创建纹理预览。</summary>
        public static ShaderGraphPinPreview FromTexture(string texturePath) => new()
        {
            Mode = ShaderGraphPreviewMode.Texture,
            TextureThumbnailPath = texturePath
        };

        /// <summary>创建迷你图表预览。</summary>
        public static ShaderGraphPinPreview FromChart(float[] data) => new()
        {
            Mode = ShaderGraphPreviewMode.Chart,
            ChartData = data
        };
    }

    /// <summary>
    /// 引脚预览显示模式。
    /// </summary>
    public enum ShaderGraphPreviewMode
    {
        /// <summary>无预览。</summary>
        None,
        /// <summary>标量数值显示。</summary>
        Scalar,
        /// <summary>颜色块显示。</summary>
        Color,
        /// <summary>纹理缩略图。</summary>
        Texture,
        /// <summary>迷你折线图。</summary>
        Chart
    }

    // =========================================================================
    // 3. 着色器图引脚 (ShaderGraphPin)
    // =========================================================================

    /// <summary>
    /// 着色器图引脚。定义节点的一个输入或输出端口。
    /// 每个引脚具有唯一 ID、方向、数据类型、显示名称、默认值和显示模式。
    /// 输入引脚最多接受一条连接; 输出引脚可以连接到多个输入引脚。
    /// </summary>
    public sealed class ShaderGraphPin
    {
        /// <summary>引脚唯一标识 (GUID 格式, 在节点内唯一)。</summary>
        public string Id { get; set; } = Guid.NewGuid().ToString("N");

        /// <summary>引脚显示名称 (如 "BaseColor", "UV", "Result")。</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>引脚方向 (输入/输出)。</summary>
        public ShaderGraphPinDirection Direction { get; set; } = ShaderGraphPinDirection.Input;

        /// <summary>引脚数据类型。</summary>
        public ShaderGraphPinType DataType { get; set; } = ShaderGraphPinType.Float;

        /// <summary>默认值 (未连接时的回退值, 存储为 Vector4 以统一标量/向量/颜色)。</summary>
        public Vector4 DefaultValue { get; set; } = new Vector4(0, 0, 0, 1);

        /// <summary>整数默认值 (仅 Int 类型使用)。</summary>
        public int IntDefaultValue { get; set; }

        /// <summary>布尔默认值 (仅 Bool 类型使用)。</summary>
        public bool BoolDefaultValue { get; set; }

        /// <summary>字符串默认值 (纹理路径/采样器名称等)。</summary>
        public string StringDefaultValue { get; set; } = string.Empty;

        /// <summary>显示模式 (控制编辑器中如何渲染此引脚)。</summary>
        public ShaderGraphPinDisplayMode DisplayMode { get; set; } = ShaderGraphPinDisplayMode.Default;

        /// <summary>滑块范围下限 (DisplayMode == Slider 时使用)。</summary>
        public float SliderMin { get; set; } = 0f;

        /// <summary>滑块范围上限 (DisplayMode == Slider 时使用)。</summary>
        public float SliderMax { get; set; } = 1f;

        /// <summary>下拉菜单选项 (DisplayMode == Dropdown 时使用)。</summary>
        public string[]? DropdownOptions { get; set; }

        /// <summary>预览数据 (编辑器中实时更新的引脚值可视化)。</summary>
        public ShaderGraphPinPreview Preview { get; set; } = ShaderGraphPinPreview.Empty;

        /// <summary>获取引脚对应的 HLSL 类型字符串。</summary>
        public string HlslTypeString => DataType switch
        {
            ShaderGraphPinType.Float => "float",
            ShaderGraphPinType.Float2 => "float2",
            ShaderGraphPinType.Float3 => "float3",
            ShaderGraphPinType.Float4 => "float4",
            ShaderGraphPinType.Color => "float4",
            ShaderGraphPinType.Texture2D => "Texture2D",
            ShaderGraphPinType.TextureCube => "TextureCube",
            ShaderGraphPinType.Texture3D => "Texture3D",
            ShaderGraphPinType.SamplerState => "SamplerState",
            ShaderGraphPinType.Bool => "bool",
            ShaderGraphPinType.Int => "int",
            ShaderGraphPinType.Matrix3x3 => "float3x3",
            ShaderGraphPinType.Matrix4x4 => "float4x4",
            ShaderGraphPinType.Void => "void",
            ShaderGraphPinType.ShaderFunction => "void",
            ShaderGraphPinType.Struct => "ShaderStruct",
            _ => "float"
        };

        /// <summary>获取默认值的 HLSL 字面量表达式。</summary>
        public string DefaultHlslLiteral
        {
            get
            {
                return DataType switch
                {
                    ShaderGraphPinType.Float => $"{DefaultValue.X:G9}",
                    ShaderGraphPinType.Float2 => $"float2({DefaultValue.X:G9}, {DefaultValue.Y:G9})",
                    ShaderGraphPinType.Float3 => $"float3({DefaultValue.X:G9}, {DefaultValue.Y:G9}, {DefaultValue.Z:G9})",
                    ShaderGraphPinType.Float4 => $"float4({DefaultValue.X:G9}, {DefaultValue.Y:G9}, {DefaultValue.Z:G9}, {DefaultValue.W:G9})",
                    ShaderGraphPinType.Color => $"float4({DefaultValue.X:G9}, {DefaultValue.Y:G9}, {DefaultValue.Z:G9}, {DefaultValue.W:G9})",
                    ShaderGraphPinType.Bool => BoolDefaultValue ? "true" : "false",
                    ShaderGraphPinType.Int => $"{IntDefaultValue}",
                    ShaderGraphPinType.Matrix3x3 => "float3x3(1,0,0, 0,1,0, 0,0,1)",
                    ShaderGraphPinType.Matrix4x4 => "float4x4(1,0,0,0, 0,1,0,0, 0,0,1,0, 0,0,0,1)",
                    _ => "0"
                };
            }
        }

        /// <summary>获取数据类型的维度 (标量 = 1, 向量 = 分量数, 其他 = 0)。</summary>
        public static int GetDimension(ShaderGraphPinType type) => type switch
        {
            ShaderGraphPinType.Float => 1,
            ShaderGraphPinType.Float2 => 2,
            ShaderGraphPinType.Float3 => 3,
            ShaderGraphPinType.Float4 => 4,
            ShaderGraphPinType.Color => 4,
            ShaderGraphPinType.Bool => 1,
            ShaderGraphPinType.Int => 1,
            _ => 0
        };

        /// <summary>
        /// 检查两个引脚类型是否可以连接。
        /// 规则: 标量可广播到高维, 同类型完全兼容, Color 与 Float4 互通。
        /// </summary>
        public static bool AreTypesCompatible(ShaderGraphPinType source, ShaderGraphPinType target)
        {
            // 完全相同
            if (source == target) return true;

            // Color <-> Float4 互通
            if ((source == ShaderGraphPinType.Color && target == ShaderGraphPinType.Float4) ||
                (source == ShaderGraphPinType.Float4 && target == ShaderGraphPinType.Color))
                return true;

            // Color <-> Float3 互通 (丢弃 alpha)
            if ((source == ShaderGraphPinType.Color && target == ShaderGraphPinType.Float3) ||
                (source == ShaderGraphPinType.Float3 && target == ShaderGraphPinType.Color))
                return true;

            // Bool 可以连接到任何非纹理类型 (用于静态开关)
            if (source == ShaderGraphPinType.Bool) return true;

            // Float 可以广播到任意维度
            if (source == ShaderGraphPinType.Float)
            {
                return target == ShaderGraphPinType.Float2 ||
                       target == ShaderGraphPinType.Float3 ||
                       target == ShaderGraphPinType.Float4 ||
                       target == ShaderGraphPinType.Color;
            }

            // 高维向量可以截取到低维
            int sourceDim = GetDimension(source);
            int targetDim = GetDimension(target);
            if (sourceDim > targetDim && targetDim > 0)
                return true;

            return false;
        }
    }

    // =========================================================================
    // 4. 着色器图连接 (ShaderGraphConnection)
    // =========================================================================

    /// <summary>
    /// 着色器图连接: 从源节点的输出引脚到目标节点的输入引脚。
    /// 一个输入引脚最多接受一条连接 (最后连接的生效, 替换旧的)。
    /// 一个输出引脚可以扇出到多个输入引脚。
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

        /// <summary>连接是否启用 (禁用后不参与编译)。</summary>
        public bool Enabled { get; set; } = true;
    }

    // =========================================================================
    // 5. 着色器图节点基类 (ShaderGraphNode)
    // =========================================================================

    /// <summary>
    /// 着色器图节点基类。节点是着色器图的基本运算单元,
    /// 每个节点携带输入引脚集、输出引脚集和可选的预览数据。
    /// 派生类 (ShaderNodeLibrary 中的具体节点) 可添加额外属性。
    /// </summary>
    public class ShaderGraphNode
    {
        /// <summary>节点唯一标识 (GUID 格式)。</summary>
        public string Id { get; set; } = Guid.NewGuid().ToString("N");

        /// <summary>节点类型名称 (如 "Add", "TextureSample", "MaterialOutput")。
        /// 由 ShaderNodeLibrary 注册时确定。</summary>
        public string NodeType { get; set; } = string.Empty;

        /// <summary>节点标题 (显示在编辑器节点窗口的标题栏, 用户可自定义)。</summary>
        public string Title { get; set; } = string.Empty;

        /// <summary>节点描述信息 (鼠标悬停时的 tooltip)。</summary>
        public string Description { get; set; } = string.Empty;

        /// <summary>节点在编辑器画布上的位置 (像素坐标)。</summary>
        public Vector2 Position { get; set; }

        /// <summary>节点大小 (编辑器中渲染的宽高, 像素)。</summary>
        public Vector2 Size { get; set; } = new Vector2(200, 150);

        /// <summary>节点是否启用 (禁用的节点在求值和编译时被跳过, 灰色显示)。</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>节点是否折叠 (编辑器中折叠后只显示标题栏)。</summary>
        public bool Collapsed { get; set; }

        /// <summary>节点颜色标签 (用于编辑器中的分类着色)。</summary>
        public string ColorTag { get; set; } = string.Empty;

        /// <summary>输入引脚列表。</summary>
        public List<ShaderGraphPin> InputPins { get; set; } = new();

        /// <summary>输出引脚列表。</summary>
        public List<ShaderGraphPin> OutputPins { get; set; } = new();

        /// <summary>节点预览缩略图 (显示在节点标题下方的迷你预览)。</summary>
        public ShaderGraphPinPreview NodePreview { get; set; } = ShaderGraphPinPreview.Empty;

        /// <summary>自定义元数据字典 (用于存储节点特有的参数, 如常量值/纹理路径等)。</summary>
        public Dictionary<string, object> Metadata { get; set; } = new();

        /// <summary>注释文本 (显示在节点下方)。</summary>
        public string Comment { get; set; } = string.Empty;

        // ── 引脚管理便捷方法 ────────────────────────────────────────

        /// <summary>添加输入引脚。</summary>
        public ShaderGraphPin AddInput(string name, ShaderGraphPinType dataType,
            Vector4 defaultValue = default, ShaderGraphPinDisplayMode displayMode = ShaderGraphPinDisplayMode.Default)
        {
            var pin = new ShaderGraphPin
            {
                Id = $"{Id}_in_{name}",
                Name = name,
                Direction = ShaderGraphPinDirection.Input,
                DataType = dataType,
                DefaultValue = defaultValue == default ? new Vector4(0, 0, 0, 1) : defaultValue,
                DisplayMode = displayMode
            };
            InputPins.Add(pin);
            return pin;
        }

        /// <summary>添加输出引脚。</summary>
        public ShaderGraphPin AddOutput(string name, ShaderGraphPinType dataType,
            Vector4 defaultValue = default, ShaderGraphPinDisplayMode displayMode = ShaderGraphPinDisplayMode.Default)
        {
            var pin = new ShaderGraphPin
            {
                Id = $"{Id}_out_{name}",
                Name = name,
                Direction = ShaderGraphPinDirection.Output,
                DataType = dataType,
                DefaultValue = defaultValue == default ? new Vector4(0, 0, 0, 1) : defaultValue,
                DisplayMode = displayMode
            };
            OutputPins.Add(pin);
            return pin;
        }

        /// <summary>按名称查找输入引脚 (大小写不敏感)。</summary>
        public ShaderGraphPin? GetInputPin(string name)
            => InputPins.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

        /// <summary>按名称查找输出引脚 (大小写不敏感)。</summary>
        public ShaderGraphPin? GetOutputPin(string name)
            => OutputPins.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

        /// <summary>按 ID 查找引脚 (输入+输出都搜索)。</summary>
        public ShaderGraphPin? FindPinById(string pinId)
            => InputPins.FirstOrDefault(p => p.Id == pinId)
               ?? OutputPins.FirstOrDefault(p => p.Id == pinId);

        // ── 元数据便捷访问 ────────────────────────────────────────

        /// <summary>获取浮点元数据 (不存在则返回默认值)。</summary>
        public float GetFloatMeta(string key, float defaultValue = 0f)
        {
            if (Metadata.TryGetValue(key, out var val) && val is float f) return f;
            return defaultValue;
        }

        /// <summary>设置浮点元数据。</summary>
        public void SetFloatMeta(string key, float value) => Metadata[key] = value;

        /// <summary>获取向量元数据。</summary>
        public Vector4 GetVectorMeta(string key, Vector4 defaultValue = default)
        {
            if (Metadata.TryGetValue(key, out var val) && val is Vector4 v) return v;
            return defaultValue == default ? Vector4.Zero : defaultValue;
        }

        /// <summary>设置向量元数据。</summary>
        public void SetVectorMeta(string key, Vector4 value) => Metadata[key] = value;

        /// <summary>获取字符串元数据。</summary>
        public string GetStringMeta(string key, string defaultValue = "")
        {
            if (Metadata.TryGetValue(key, out var val) && val is string s) return s;
            return defaultValue;
        }

        /// <summary>设置字符串元数据。</summary>
        public void SetStringMeta(string key, string value) => Metadata[key] = value;

        /// <summary>获取布尔元数据。</summary>
        public bool GetBoolMeta(string key, bool defaultValue = false)
        {
            if (Metadata.TryGetValue(key, out var val) && val is bool b) return b;
            return defaultValue;
        }

        /// <summary>设置布尔元数据。</summary>
        public void SetBoolMeta(string key, bool value) => Metadata[key] = value;

        /// <summary>获取整数元数据。</summary>
        public int GetIntMeta(string key, int defaultValue = 0)
        {
            if (Metadata.TryGetValue(key, out var val) && val is int i) return i;
            return defaultValue;
        }

        /// <summary>设置整数元数据。</summary>
        public void SetIntMeta(string key, int value) => Metadata[key] = value;
    }

    // =========================================================================
    // 6. 着色器图属性 (ShaderGraphProperty)
    // =========================================================================

    /// <summary>
    /// 着色器图属性类型。决定材质参数在 GPU 中的存储方式。
    /// </summary>
    public enum ShaderGraphPropertyType
    {
        /// <summary>标量浮点 (float)</summary>
        Scalar,
        /// <summary>向量 (float3 / float4)</summary>
        Vector,
        /// <summary>颜色 (float4, 编辑器中显示为颜色拾取器)</summary>
        Color,
        /// <summary>2D 纹理 (Texture2D)</summary>
        Texture2D,
        /// <summary>立方体贴图 (TextureCube)</summary>
        CubeMap,
        /// <summary>3D 纹理 (Texture3D)</summary>
        Texture3D,
        /// <summary>静态开关 (bool, 用于 shader variant)</summary>
        StaticBool,
        /// <summary>静态开关组 (uint, 多选一, 用于 shader variant)</summary>
        StaticSwitch,
        /// <summary>通道遮罩 (float4, 用于材质层遮罩)</summary>
        ChannelMask
    }

    /// <summary>
    /// 着色器图属性。定义材质图中可由外部 (材质实例/引擎/脚本) 设置的参数。
    /// 属性在编译后写入 cbuffer, 运行时通过 RHI 更新。
    /// 每个属性对应一个 Property 节点实例。
    /// </summary>
    public sealed class ShaderGraphProperty
    {
        /// <summary>属性唯一标识。</summary>
        public string Id { get; set; } = Guid.NewGuid().ToString("N");

        /// <summary>属性名称 (cbuffer 中的字段名, 必须是合法 HLSL 标识符)。</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>属性显示名称 (编辑器中显示的用户友好名称)。</summary>
        public string DisplayName { get; set; } = string.Empty;

        /// <summary>属性类型。</summary>
        public ShaderGraphPropertyType PropertyType { get; set; } = ShaderGraphPropertyType.Scalar;

        /// <summary>标量默认值。</summary>
        public float ScalarValue { get; set; }

        /// <summary>向量/颜色默认值。</summary>
        public Vector4 VectorValue { get; set; } = new Vector4(0, 0, 0, 1);

        /// <summary>纹理资源路径。</summary>
        public string TexturePath { get; set; } = string.Empty;

        /// <summary>布尔默认值。</summary>
        public bool BoolValue { get; set; }

        /// <summary>整数默认值 (用于 StaticSwitch)。</summary>
        public int IntValue { get; set; }

        /// <summary>所属的属性组名称 (用于编辑器分组显示, 如 "PBR Parameters")。</summary>
        public string Group { get; set; } = "Default";

        /// <summary>是否在编辑器中显示 (隐藏的属性仍参与编译, 但不出现在属性面板)。</summary>
        public bool Visible { get; set; } = true;

        /// <summary>标量范围下限 (用于编辑器滑块)。</summary>
        public float MinValue { get; set; } = 0f;

        /// <summary>标量范围上限 (用于编辑器滑块)。</summary>
        public float MaxValue { get; set; } = 1f;

        /// <summary>获取 HLSL 类型字符串。</summary>
        public string HlslType => PropertyType switch
        {
            ShaderGraphPropertyType.Scalar => "float",
            ShaderGraphPropertyType.Vector => "float4",
            ShaderGraphPropertyType.Color => "float4",
            ShaderGraphPropertyType.Texture2D => "Texture2D",
            ShaderGraphPropertyType.CubeMap => "TextureCube",
            ShaderGraphPropertyType.Texture3D => "Texture3D",
            ShaderGraphPropertyType.StaticBool => "bool",
            ShaderGraphPropertyType.StaticSwitch => "uint",
            ShaderGraphPropertyType.ChannelMask => "float4",
            _ => "float"
        };

        /// <summary>获取 HLSL 默认值字面量。</summary>
        public string HlslDefaultValue => PropertyType switch
        {
            ShaderGraphPropertyType.Scalar => $"{ScalarValue:G9}",
            ShaderGraphPropertyType.Vector => $"float4({VectorValue.X:G9}, {VectorValue.Y:G9}, {VectorValue.Z:G9}, {VectorValue.W:G9})",
            ShaderGraphPropertyType.Color => $"float4({VectorValue.X:G9}, {VectorValue.Y:G9}, {VectorValue.Z:G9}, {VectorValue.W:G9})",
            ShaderGraphPropertyType.StaticBool => BoolValue ? "true" : "false",
            ShaderGraphPropertyType.StaticSwitch => $"{IntValue}u",
            ShaderGraphPropertyType.ChannelMask => $"float4({VectorValue.X:G9}, {VectorValue.Y:G9}, {VectorValue.Z:G9}, {VectorValue.W:G9})",
            _ => "0"
        };
    }

    // =========================================================================
    // 7. 着色器图验证结果
    // =========================================================================

    /// <summary>
    /// 验证错误严重级别。
    /// </summary>
    public enum ShaderGraphValidationErrorLevel
    {
        /// <summary>信息 (不阻止编译)。</summary>
        Info,
        /// <summary>警告 (可能影响渲染结果)。</summary>
        Warning,
        /// <summary>错误 (阻止编译)。</summary>
        Error
    }

    /// <summary>
    /// 验证错误条目。
    /// </summary>
    public sealed class ShaderGraphValidationError
    {
        /// <summary>错误级别。</summary>
        public ShaderGraphValidationErrorLevel Level { get; set; } = ShaderGraphValidationErrorLevel.Error;

        /// <summary>错误消息。</summary>
        public string Message { get; set; } = string.Empty;

        /// <summary>关联的节点 ID (可为空)。</summary>
        public string? NodeId { get; set; }

        /// <summary>关联的引脚 ID (可为空)。</summary>
        public string? PinId { get; set; }

        /// <summary>关联的连接 ID (可为空)。</summary>
        public string? ConnectionId { get; set; }
    }

    // =========================================================================
    // 8. 着色器图 (ShaderGraph) —— 核心图类
    // =========================================================================

    /// <summary>
    /// 着色器图: 完整的可视化着色器编辑器后端数据模型。
    /// 包含节点集、连接集、属性集, 支持拓扑校验、序列化和 HLSL 编译。
    /// 
    /// 典型工作流:
    ///   1. 创建空图: ShaderGraph.Create("MyMaterial")
    ///   2. 添加节点: graph.AddNode("Add", "Add Node")
    ///   3. 连接引脚: graph.Connect(...)
    ///   4. 验证图: var errors = graph.Validate()
    ///   5. 编译图: var result = graph.Compile()
    ///   6. 序列化: graph.Write("path.sgraph")
    /// </summary>
    public sealed class ShaderGraph
    {
        /// <summary>序列化版本号。</summary>
        public int Version { get; set; } = 1;

        /// <summary>着色器图唯一标识。</summary>
        public string Guid { get; set; } = System.Guid.NewGuid().ToString("N");

        /// <summary>着色器图名称。</summary>
        public string Name { get; set; } = "Shader Graph";

        /// <summary>着色器图描述。</summary>
        public string Description { get; set; } = string.Empty;

        /// <summary>节点列表。</summary>
        public List<ShaderGraphNode> Nodes { get; set; } = new();

        /// <summary>连接列表。</summary>
        public List<ShaderGraphConnection> Connections { get; set; } = new();

        /// <summary>材质属性列表 (编译后写入 cbuffer)。</summary>
        public List<ShaderGraphProperty> Properties { get; set; } = new();

        /// <summary>着色器目标模型 (SM5.0 / SM6.0 / SM6.5)。</summary>
        public string TargetShaderModel { get; set; } = "sm_5_0";

        /// <summary>材质域 (Surface / Deferred / PostProcess 等)。</summary>
        public string MaterialDomain { get; set; } = "Surface";

        /// <summary>混合模式 (Opaque / Translucent / Masked 等)。</summary>
        public string BlendMode { get; set; } = "Opaque";

        /// <summary>着色模型 (DefaultLit / Unlit / Subsurface 等)。</summary>
        public string ShadingModel { get; set; } = "DefaultLit";

        /// <summary>是否双面渲染。</summary>
        public bool DoubleSided { get; set; }

        /// <summary>自定义 includes 列表 (HLSL #include 文件路径)。</summary>
        public List<string> CustomIncludes { get; set; } = new();

        /// <summary>自定义 defines 列表 (HLSL #define 宏)。</summary>
        public List<string> CustomDefines { get; set; } = new();

        /// <summary>自定义预处理器代码 (插入到生成 HLSL 的头部)。</summary>
        public string CustomPreprocessorCode { get; set; } = string.Empty;

        private static readonly JsonSerializerOptions s_jsonOptions = new()
        {
            WriteIndented = true,
            IncludeFields = true,
            PropertyNameCaseInsensitive = true,
            Converters = { new MetadataJsonConverter() }
        };

        // ── 工厂方法 ────────────────────────────────────────────────

        /// <summary>创建空着色器图。</summary>
        public static ShaderGraph Create(string name)
        {
            return new ShaderGraph
            {
                Name = string.IsNullOrWhiteSpace(name) ? "Shader Graph" : name.Trim()
            };
        }

        // ── 节点管理 ───────────────────────────────────────────────

        /// <summary>添加节点到图中。</summary>
        public ShaderGraphNode AddNode(ShaderGraphNode node)
        {
            if (node == null) throw new ArgumentNullException(nameof(node));
            Nodes.Add(node);
            return node;
        }

        /// <summary>创建并添加新节点。</summary>
        public ShaderGraphNode CreateNode(string nodeType, string title, Vector2 position)
        {
            var node = new ShaderGraphNode
            {
                NodeType = nodeType,
                Title = string.IsNullOrWhiteSpace(title) ? nodeType : title.Trim(),
                Position = position
            };
            Nodes.Add(node);
            return node;
        }

        /// <summary>移除节点及其所有关联连接。</summary>
        public bool RemoveNode(string nodeId)
        {
            var node = Nodes.FirstOrDefault(n => n.Id == nodeId);
            if (node == null) return false;
            Connections.RemoveAll(c =>
                c.SourceNodeId == nodeId || c.TargetNodeId == nodeId);
            Nodes.Remove(node);
            return true;
        }

        /// <summary>按 ID 查找节点。</summary>
        public ShaderGraphNode? FindNode(string nodeId)
            => Nodes.FirstOrDefault(n => n.Id == nodeId);

        /// <summary>按类型查找所有节点。</summary>
        public List<ShaderGraphNode> FindNodesByType(string nodeType)
            => Nodes.Where(n => n.NodeType == nodeType).ToList();

        /// <summary>获取主输出节点 (MaterialOutput 节点)。</summary>
        public ShaderGraphNode? GetOutputNode()
            => Nodes.FirstOrDefault(n => n.NodeType == "MaterialOutput" && n.Enabled);

        // ── 连接管理 ───────────────────────────────────────────────

        /// <summary>
        /// 创建连接: 从源节点的输出引脚到目标节点的输入引脚。
        /// 自动执行类型兼容性检查; 目标引脚已有连接时替换旧连接。
        /// </summary>
        public ShaderGraphConnection? Connect(
            string sourceNodeId, string sourcePinId,
            string targetNodeId, string targetPinId)
        {
            var sourceNode = FindNode(sourceNodeId);
            var targetNode = FindNode(targetNodeId);
            if (sourceNode == null || targetNode == null) return null;

            var sourcePin = sourceNode.FindPinById(sourcePinId);
            var targetPin = targetNode.FindPinById(targetPinId);
            if (sourcePin == null || targetPin == null) return null;

            // 类型兼容性检查
            if (!ShaderGraphPin.AreTypesCompatible(sourcePin.DataType, targetPin.DataType))
                return null;

            // 防止自连接
            if (sourceNodeId == targetNodeId) return null;

            // 移除目标引脚的旧连接 (一个输入只接受一条连接)
            Connections.RemoveAll(c =>
                c.TargetNodeId == targetNodeId && c.TargetPinId == targetPinId);

            var connection = new ShaderGraphConnection
            {
                SourceNodeId = sourceNodeId,
                SourcePinId = sourcePinId,
                TargetNodeId = targetNodeId,
                TargetPinId = targetPinId
            };
            Connections.Add(connection);
            return connection;
        }

        /// <summary>
        /// 通过引脚名称创建连接 (便捷方法)。
        /// </summary>
        public ShaderGraphConnection? ConnectByPinName(
            string sourceNodeId, string sourcePinName,
            string targetNodeId, string targetPinName)
        {
            var sourceNode = FindNode(sourceNodeId);
            var targetNode = FindNode(targetNodeId);
            if (sourceNode == null || targetNode == null) return null;

            var sourcePin = sourceNode.GetOutputPin(sourcePinName);
            var targetPin = targetNode.GetInputPin(targetPinName);
            if (sourcePin == null || targetPin == null) return null;

            return Connect(sourceNodeId, sourcePin.Id, targetNodeId, targetPin.Id);
        }

        /// <summary>断开连接。</summary>
        public bool Disconnect(string connectionId)
            => Connections.RemoveAll(c => c.Id == connectionId) > 0;

        /// <summary>断开指定引脚上的所有连接。</summary>
        public void DisconnectPin(string nodeId, string pinId)
            => Connections.RemoveAll(c =>
                (c.SourceNodeId == nodeId && c.SourcePinId == pinId) ||
                (c.TargetNodeId == nodeId && c.TargetPinId == pinId));

        /// <summary>获取连接到指定输入引脚的连接。</summary>
        public ShaderGraphConnection? GetIncomingConnection(string nodeId, string inputPinId)
            => Connections.LastOrDefault(c =>
                c.Enabled && c.TargetNodeId == nodeId && c.TargetPinId == inputPinId);

        /// <summary>获取从指定输出引脚出发的所有连接。</summary>
        public List<ShaderGraphConnection> GetOutgoingConnections(string nodeId, string outputPinId)
            => Connections.Where(c =>
                c.Enabled && c.SourceNodeId == nodeId && c.SourcePinId == outputPinId).ToList();

        // ── 属性管理 ───────────────────────────────────────────────

        /// <summary>添加材质属性。</summary>
        public ShaderGraphProperty AddProperty(ShaderGraphProperty property)
        {
            Properties.Add(property);
            return property;
        }

        /// <summary>创建标量属性。</summary>
        public ShaderGraphProperty CreateScalarProperty(string name, float defaultValue = 0f,
            string group = "Default", float min = 0f, float max = 1f)
        {
            var prop = new ShaderGraphProperty
            {
                Name = name,
                DisplayName = name,
                PropertyType = ShaderGraphPropertyType.Scalar,
                ScalarValue = defaultValue,
                MinValue = min,
                MaxValue = max,
                Group = group
            };
            Properties.Add(prop);
            return prop;
        }

        /// <summary>创建颜色属性。</summary>
        public ShaderGraphProperty CreateColorProperty(string name, Vector4 defaultValue,
            string group = "Default")
        {
            var prop = new ShaderGraphProperty
            {
                Name = name,
                DisplayName = name,
                PropertyType = ShaderGraphPropertyType.Color,
                VectorValue = defaultValue,
                Group = group
            };
            Properties.Add(prop);
            return prop;
        }

        /// <summary>创建纹理属性。</summary>
        public ShaderGraphProperty CreateTextureProperty(string name, string texturePath = "",
            string group = "Default")
        {
            var prop = new ShaderGraphProperty
            {
                Name = name,
                DisplayName = name,
                PropertyType = ShaderGraphPropertyType.Texture2D,
                TexturePath = texturePath,
                Group = group
            };
            Properties.Add(prop);
            return prop;
        }

        // ── 拓扑排序 ───────────────────────────────────────────────

        /// <summary>
        /// 从主输出节点出发, 反向遍历所有输入连接, 进行拓扑排序。
        /// 结果保证: 任一节点排在所有数据依赖之前。
        /// </summary>
        public List<ShaderGraphNode> TopologicalSort()
        {
            var outputNode = GetOutputNode()
                ?? throw new InvalidOperationException("No MaterialOutput node found in shader graph.");

            var visited = new HashSet<string>();
            var visiting = new HashSet<string>();
            var stack = new List<ShaderGraphNode>();

            void Visit(ShaderGraphNode node)
            {
                if (visited.Contains(node.Id)) return;
                if (visiting.Contains(node.Id))
                    throw new InvalidOperationException(
                        $"Cycle detected at node: {node.Title} ({node.Id})");
                if (!node.Enabled) return;

                visiting.Add(node.Id);

                foreach (var pin in node.InputPins)
                {
                    var conn = GetIncomingConnection(node.Id, pin.Id);
                    if (conn == null) continue;
                    var src = FindNode(conn.SourceNodeId);
                    if (src != null) Visit(src);
                }

                visiting.Remove(node.Id);
                visited.Add(node.Id);
                stack.Add(node);
            }

            Visit(outputNode);
            return stack;
        }

        // ── 环检测 ────────────────────────────────────────────────

        /// <summary>检测图中是否存在循环依赖。</summary>
        public bool HasCycle()
        {
            var adjacency = Connections
                .Where(c => c.Enabled)
                .GroupBy(c => c.SourceNodeId)
                .ToDictionary(g => g.Key, g => g.Select(c => c.TargetNodeId).ToList());

            var visiting = new HashSet<string>();
            var visited = new HashSet<string>();

            bool Visit(string id)
            {
                if (visiting.Contains(id)) return true;
                if (visited.Contains(id)) return false;
                visiting.Add(id);

                if (adjacency.TryGetValue(id, out var next))
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

        // ── 图验证 ────────────────────────────────────────────────

        /// <summary>
        /// 验证着色器图的完整性, 返回错误列表。
        /// 检查项: 名称/GUID/输出节点/节点ID唯一性/引脚完整性/连接合法性/类型兼容/环检测。
        /// </summary>
        public List<ShaderGraphValidationError> Validate()
        {
            var errors = new List<ShaderGraphValidationError>();

            // 基础检查
            if (string.IsNullOrWhiteSpace(Name))
                errors.Add(new ShaderGraphValidationError
                {
                    Level = ShaderGraphValidationErrorLevel.Error,
                    Message = "Shader graph name is required."
                });

            if (!System.Guid.TryParse(Guid, out _))
                errors.Add(new ShaderGraphValidationError
                {
                    Level = ShaderGraphValidationErrorLevel.Error,
                    Message = "Shader graph GUID is invalid."
                });

            // 检查主输出节点
            if (GetOutputNode() == null)
                errors.Add(new ShaderGraphValidationError
                {
                    Level = ShaderGraphValidationErrorLevel.Error,
                    Message = "Shader graph requires an enabled MaterialOutput node."
                });

            // 节点 ID 唯一性
            var nodeIds = new HashSet<string>();
            foreach (var node in Nodes)
            {
                if (string.IsNullOrWhiteSpace(node.Id))
                    errors.Add(new ShaderGraphValidationError
                    {
                        Level = ShaderGraphValidationErrorLevel.Error,
                        Message = $"Node '{node.Title}' has an empty id.",
                        NodeId = node.Id
                    });

                if (!nodeIds.Add(node.Id))
                    errors.Add(new ShaderGraphValidationError
                    {
                        Level = ShaderGraphValidationErrorLevel.Error,
                        Message = $"Duplicate node id: {node.Id}",
                        NodeId = node.Id
                    });

                // 引脚完整性检查
                if (node.InputPins.Count == 0 && node.OutputPins.Count == 0
                    && node.NodeType != "MaterialOutput")
                    errors.Add(new ShaderGraphValidationError
                    {
                        Level = ShaderGraphValidationErrorLevel.Warning,
                        Message = $"Node '{node.Title}' ({node.NodeType}) has no pins.",
                        NodeId = node.Id
                    });
            }

            // 连接合法性检查
            foreach (var conn in Connections)
            {
                if (!conn.Enabled) continue;

                var sourceNode = FindNode(conn.SourceNodeId);
                var targetNode = FindNode(conn.TargetNodeId);

                if (sourceNode == null)
                {
                    errors.Add(new ShaderGraphValidationError
                    {
                        Level = ShaderGraphValidationErrorLevel.Error,
                        Message = $"Connection source node not found: {conn.SourceNodeId}",
                        ConnectionId = conn.Id
                    });
                    continue;
                }
                if (targetNode == null)
                {
                    errors.Add(new ShaderGraphValidationError
                    {
                        Level = ShaderGraphValidationErrorLevel.Error,
                        Message = $"Connection target node not found: {conn.TargetNodeId}",
                        ConnectionId = conn.Id
                    });
                    continue;
                }

                var sourcePin = sourceNode.FindPinById(conn.SourcePinId);
                var targetPin = targetNode.FindPinById(conn.TargetPinId);

                if (sourcePin == null)
                    errors.Add(new ShaderGraphValidationError
                    {
                        Level = ShaderGraphValidationErrorLevel.Error,
                        Message = $"Source pin '{conn.SourcePinId}' not found on node '{sourceNode.Title}'.",
                        ConnectionId = conn.Id
                    });
                if (targetPin == null)
                    errors.Add(new ShaderGraphValidationError
                    {
                        Level = ShaderGraphValidationErrorLevel.Error,
                        Message = $"Target pin '{conn.TargetPinId}' not found on node '{targetNode.Title}'.",
                        ConnectionId = conn.Id
                    });

                // 类型兼容性
                if (sourcePin != null && targetPin != null &&
                    !ShaderGraphPin.AreTypesCompatible(sourcePin.DataType, targetPin.DataType))
                    errors.Add(new ShaderGraphValidationError
                    {
                        Level = ShaderGraphValidationErrorLevel.Warning,
                        Message = $"Type mismatch: {sourcePin.DataType} -> {targetPin.DataType} on connection {conn.Id}.",
                        ConnectionId = conn.Id
                    });
            }

            // 环检测
            if (HasCycle())
                errors.Add(new ShaderGraphValidationError
                {
                    Level = ShaderGraphValidationErrorLevel.Error,
                    Message = "Shader graph contains a cycle dependency."
                });

            // 材质属性检查
            var propNames = new HashSet<string>();
            foreach (var prop in Properties)
            {
                if (string.IsNullOrWhiteSpace(prop.Name))
                    errors.Add(new ShaderGraphValidationError
                    {
                        Level = ShaderGraphValidationErrorLevel.Error,
                        Message = "Property has empty name."
                    });

                if (!propNames.Add(prop.Name))
                    errors.Add(new ShaderGraphValidationError
                    {
                        Level = ShaderGraphValidationErrorLevel.Error,
                        Message = $"Duplicate property name: {prop.Name}"
                    });
            }

            return errors;
        }

        // ── 可达性分析 ────────────────────────────────────────────

        /// <summary>
        /// 获取从输出节点可达的所有节点 (即实际参与编译的节点)。
        /// 不可达的节点是孤立的, 不会生成 HLSL 代码。
        /// </summary>
        public HashSet<string> GetReachableNodeIds()
        {
            var outputNode = GetOutputNode();
            if (outputNode == null) return new HashSet<string>();

            var reachable = new HashSet<string>();
            var queue = new Queue<string>();
            queue.Enqueue(outputNode.Id);

            while (queue.Count > 0)
            {
                var currentId = queue.Dequeue();
                if (!reachable.Add(currentId)) continue;

                var currentNode = FindNode(currentId);
                if (currentNode == null) continue;

                foreach (var pin in currentNode.InputPins)
                {
                    var conn = GetIncomingConnection(currentId, pin.Id);
                    if (conn != null && !reachable.Contains(conn.SourceNodeId))
                        queue.Enqueue(conn.SourceNodeId);
                }
            }

            return reachable;
        }

        // ── 预览更新 ─────────────────────────────────────────────

        /// <summary>
        /// 标记所有节点的预览为过期 (节点或连接发生变化后调用)。
        /// </summary>
        public void InvalidateAllPreviews()
        {
            foreach (var node in Nodes)
            {
                node.NodePreview.Dirty = true;
                foreach (var pin in node.InputPins) pin.Preview.Dirty = true;
                foreach (var pin in node.OutputPins) pin.Preview.Dirty = true;
            }
        }

        // ── 编译入口 ─────────────────────────────────────────────

        /// <summary>
        /// 编译着色器图。执行验证 -> 拓扑排序 -> HLSL 代码生成。
        /// 实际的代码生成委托给 ShaderGraphCompiler (见 ShaderGraphCompiler.cs)。
        /// </summary>
        public ShaderGraphCompileResult Compile(ShaderGraphCompilerOptions? options = null)
        {
            var errors = Validate();
            if (errors.Any(e => e.Level == ShaderGraphValidationErrorLevel.Error))
            {
                return new ShaderGraphCompileResult
                {
                    Success = false,
                    Errors = errors,
                    ErrorMessage = string.Join("; ", errors.Select(e => e.Message))
                };
            }

            var compiler = new ShaderGraphHlslCompiler();
            return compiler.Compile(this, options ?? new ShaderGraphCompilerOptions());
        }

        // ── 序列化 ────────────────────────────────────────────────

        /// <summary>序列化到 JSON 文件。</summary>
        public void Write(string path)
        {
            var errors = Validate();
            if (errors.Any(e => e.Level == ShaderGraphValidationErrorLevel.Error))
                throw new InvalidOperationException(
                    "Cannot write invalid shader graph: " +
                    string.Join("; ", errors.Select(e => e.Message)));

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(this, s_jsonOptions));
        }

        /// <summary>从 JSON 文件反序列化。</summary>
        public static ShaderGraph Read(string path)
        {
            var json = File.ReadAllText(path);
            var graph = JsonSerializer.Deserialize<ShaderGraph>(json, s_jsonOptions)
                        ?? throw new InvalidOperationException($"Invalid shader graph: {path}");

            // 确保所有节点的引脚 ID 格式正确
            foreach (var node in graph.Nodes)
            {
                foreach (var pin in node.InputPins)
                {
                    if (string.IsNullOrEmpty(pin.Id))
                        pin.Id = $"{node.Id}_in_{pin.Name}";
                }
                foreach (var pin in node.OutputPins)
                {
                    if (string.IsNullOrEmpty(pin.Id))
                        pin.Id = $"{node.Id}_out_{pin.Name}";
                }
            }

            var errors = graph.Validate();
            if (errors.Any(e => e.Level == ShaderGraphValidationErrorLevel.Error))
                throw new InvalidOperationException(
                    "Invalid shader graph: " +
                    string.Join("; ", errors.Select(e => e.Message)));

            return graph;
        }

        /// <summary>序列化为 JSON 字符串。</summary>
        public string ToJson() => JsonSerializer.Serialize(this, s_jsonOptions);

        /// <summary>从 JSON 字符串反序列化。</summary>
        public static ShaderGraph FromJson(string json)
        {
            var graph = JsonSerializer.Deserialize<ShaderGraph>(json, s_jsonOptions)
                        ?? throw new InvalidOperationException("Invalid shader graph JSON.");
            return graph;
        }
    }

    // =========================================================================
    // 9. 编译结果 (编译器使用)
    // =========================================================================

    /// <summary>
    /// 着色器图编译结果。包含生成的 HLSL 源码和编译状态。
    /// </summary>
    public sealed class ShaderGraphCompileResult
    {
        /// <summary>编译是否成功。</summary>
        public bool Success { get; set; }

        /// <summary>错误消息。</summary>
        public string ErrorMessage { get; set; } = string.Empty;

        /// <summary>生成的顶点着色器 HLSL 源码。</summary>
        public string? VertexShaderSource { get; set; }

        /// <summary>生成的像素着色器 HLSL 源码。</summary>
        public string? PixelShaderSource { get; set; }

        /// <summary>验证错误列表。</summary>
        public List<ShaderGraphValidationError> Errors { get; set; } = new();

        /// <summary>编译耗时 (毫秒)。</summary>
        public long ElapsedMs { get; set; }

        /// <summary>材质统计信息 (指令数/纹理采样数等)。</summary>
        public ShaderGraphStatistics? Statistics { get; set; }
    }

    /// <summary>
    /// 着色器图统计信息。估算着色器的复杂度指标。
    /// </summary>
    public sealed class ShaderGraphStatistics
    {
        /// <summary>节点总数。</summary>
        public int TotalNodeCount { get; set; }

        /// <summary>参与编译的节点数 (可达节点)。</summary>
        public int ActiveNodeCount { get; set; }

        /// <summary>连接总数。</summary>
        public int ConnectionCount { get; set; }

        /// <summary>估算顶点着色器指令数。</summary>
        public int EstimatedVSInstructions { get; set; }

        /// <summary>估算像素着色器指令数。</summary>
        public int EstimatedPSInstructions { get; set; }

        /// <summary>纹理采样次数。</summary>
        public int TextureSampleCount { get; set; }

        /// <summary>常量缓冲区大小 (字节, 16字节对齐)。</summary>
        public int ConstantBufferSize { get; set; }

        /// <summary>使用的纹理槽位数。</summary>
        public int TextureSlotCount { get; set; }

        /// <summary>使用的采样器槽位数。</summary>
        public int SamplerSlotCount { get; set; }

        /// <summary>着色器变体数量 (基于静态开关排列)。</summary>
        public int VariantCount { get; set; }
    }

    /// <summary>
    /// 着色器图编译器选项。
    /// </summary>
    public sealed class ShaderGraphCompilerOptions
    {
        /// <summary>目标着色器模型 (默认 sm_5_0)。</summary>
        public string TargetShaderModel { get; set; } = "sm_5_0";

        /// <summary>是否生成顶点着色器。</summary>
        public bool GenerateVertexShader { get; set; } = true;

        /// <summary>是否生成像素着色器。</summary>
        public bool GeneratePixelShader { get; set; } = true;

        /// <summary>是否优化无用节点。</summary>
        public bool OptimizeUnusedNodes { get; set; } = true;

        /// <summary>是否折叠常量表达式。</summary>
        public bool ConstantFolding { get; set; } = true;

        /// <summary>是否内联简单节点 (单输入节点)。</summary>
        public bool InlineSimpleNodes { get; set; } = true;

        /// <summary>是否生成调试注释 (行号/节点ID映射)。</summary>
        public bool EmitDebugComments { get; set; } = true;

        /// <summary>包含的公共头文件路径。</summary>
        public string? CommonHeaderPath { get; set; }
    }

    // =========================================================================
    // 10. 编译器占位 (实际实现在 ShaderGraphCompiler.cs)
    // =========================================================================

    /// <summary>
    /// HLSL 编译器占位类。实际的编译逻辑在 ShaderGraphCompiler.cs 中实现。
    /// 此处提供最小实现以确保 ShaderGraph.Compile() 可编译。
    /// </summary>
    public sealed class ShaderGraphHlslCompiler
    {
        /// <summary>编译着色器图为 HLSL。</summary>
        public ShaderGraphCompileResult Compile(ShaderGraph graph, ShaderGraphCompilerOptions options)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var result = new ShaderGraphCompileResult { Success = true };

            try
            {
                var sortedNodes = graph.TopologicalSort();

                // 基础统计
                var stats = new ShaderGraphStatistics
                {
                    TotalNodeCount = graph.Nodes.Count,
                    ActiveNodeCount = sortedNodes.Count,
                    ConnectionCount = graph.Connections.Count(c => c.Enabled),
                    TextureSampleCount = sortedNodes.Count(n =>
                        n.NodeType.Contains("Texture", StringComparison.OrdinalIgnoreCase) ||
                        n.NodeType.Contains("Sample", StringComparison.OrdinalIgnoreCase)),
                    EstimatedVSInstructions = sortedNodes.Count * 3,
                    EstimatedPSInstructions = sortedNodes.Count * 5
                };

                result.Statistics = stats;
                result.Success = true;
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
    }

    // =========================================================================
    // 11. JSON 转换器 (处理 Metadata 字典的 object 值)
    // =========================================================================

    /// <summary>
    /// 元数据字典 JSON 转换器。处理 Dictionary&lt;string, object&gt; 的序列化,
    /// 支持基本类型 (float/int/bool/string/Vector4) 的自动识别。
    /// </summary>
    internal sealed class MetadataJsonConverter : JsonConverter<Dictionary<string, object>>
    {
        public override Dictionary<string, object>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var result = new Dictionary<string, object>();
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                if (reader.TokenType != JsonTokenType.PropertyName) continue;
                var key = reader.GetString() ?? string.Empty;
                reader.Read();

                switch (reader.TokenType)
                {
                    case JsonTokenType.Number:
                        if (reader.TryGetInt32(out int intVal))
                            result[key] = intVal;
                        else if (reader.TryGetSingle(out float floatVal))
                            result[key] = floatVal;
                        else
                            result[key] = reader.GetDouble();
                        break;
                    case JsonTokenType.String:
                        result[key] = reader.GetString() ?? string.Empty;
                        break;
                    case JsonTokenType.True:
                    case JsonTokenType.False:
                        result[key] = reader.GetBoolean();
                        break;
                    default:
                        result[key] = string.Empty;
                        break;
                }
            }
            return result;
        }

        public override void Write(Utf8JsonWriter writer, Dictionary<string, object> value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            foreach (var kv in value)
            {
                writer.WritePropertyName(kv.Key);
                switch (kv.Value)
                {
                    case float f:
                        writer.WriteNumberValue(f);
                        break;
                    case int i:
                        writer.WriteNumberValue(i);
                        break;
                    case double d:
                        writer.WriteNumberValue(d);
                        break;
                    case bool b:
                        writer.WriteBooleanValue(b);
                        break;
                    case string s:
                        writer.WriteStringValue(s);
                        break;
                    case Vector4 v:
                        writer.WriteStartArray();
                        writer.WriteNumberValue(v.X);
                        writer.WriteNumberValue(v.Y);
                        writer.WriteNumberValue(v.Z);
                        writer.WriteNumberValue(v.W);
                        writer.WriteEndArray();
                        break;
                    default:
                        writer.WriteStringValue(kv.Value?.ToString() ?? "");
                        break;
                }
            }
            writer.WriteEndObject();
        }
    }
}
