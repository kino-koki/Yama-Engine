// =============================================================================
//  Shanyu Engine - ShaderNodeLibrary.cs
//  山雨引擎 - 着色器节点库 (180+ 节点)
//
//  本文件实现可视化着色器图的所有节点类型, 按功能分类:
//    A. 输入节点      — Position/Normal/UV/Time/Color/Camera/Property/Texture...
//    B. 数学节点      — 四则运算/三角函数/取整/插值/导数/矩阵/向量运算/范围映射
//    C. 向量/矩阵节点 — 构造/拆解/Swizzle/旋转/空间变换/矩阵获取
//    D. 纹理/采样节点 — 各类采样/Load/Store/Gather/三平面映射/纹理信息
//    E. 程序化/噪声节点 — Perlin/Simplex/Voronoi/FBM/渐变/棋盘/砖块...
//    F. 颜色节点      — 灰度/HSV/混合/色彩校正/通道混合/色调映射/ACES
//    G. 光照/PBR节点  — 光源/菲涅尔/反射/PBR主节点/SSS/清漆/各向异性...
//    H. UV/几何节点   — UV变换/视差/深度淡入/世界对齐纹理
//    I. 逻辑/分支节点 — Branch/Switch/Compare/All/Any/And/Or/Ternary
//    J. 工具节点      — Preview/Comment/SubGraph/CustomFunction/Code/Loop/Array
//
//  每个节点通过重写 GenerateCode(NodeCodegenContext) 实现 HLSL 代码生成。
//  设计: 使用 BinaryMathNode/UnaryMathNode 基类减少数学节点样板代码。
//
//  命名空间: ShanyuEngine.ShaderGraph
// =============================================================================

using System;
using System.Linq;
using System.Numerics;

namespace ShanyuEngine.ShaderGraph
{
    // =========================================================================
    // 数学节点基类
    // =========================================================================

    /// <summary>
    /// 二元数学运算节点基类。提供 A/B 输入与 Result 输出,
    /// 派生类只需重写 Op(string a, string b) 返回运算表达式。
    /// </summary>
    public abstract class BinaryMathNode : ShaderGraphNode
    {
        protected ShaderGraphDataType ResultType { get; set; } = ShaderGraphDataType.Float;

        protected BinaryMathNode()
        {
            AddInput("A", ShaderGraphDataType.Float);
            AddInput("B", ShaderGraphDataType.Float);
            AddOutput("Result", ResultType);
        }

        /// <summary>返回运算表达式。</summary>
        protected abstract string Op(string a, string b);

        public override void GenerateCode(NodeCodegenContext ctx)
        {
            var a = ctx.GetInput("A");
            var b = ctx.GetInput("B");
            ctx.SetOutput("Result", Op(a, b));
        }
    }

    /// <summary>
    /// 一元数学运算节点基类。提供 X 输入与 Result 输出。
    /// </summary>
    public abstract class UnaryMathNode : ShaderGraphNode
    {
        protected ShaderGraphDataType ResultType { get; set; } = ShaderGraphDataType.Float;

        protected UnaryMathNode()
        {
            AddInput("X", ShaderGraphDataType.Float);
            AddOutput("Result", ResultType);
        }

        protected abstract string Op(string x);

        public override void GenerateCode(NodeCodegenContext ctx)
        {
            ctx.SetOutput("Result", Op(ctx.GetInput("X")));
        }
    }

    // =========================================================================
    // A. 输入节点
    // =========================================================================

    /// <summary>位置节点 (世界/模型/视图/切线空间)。</summary>
    public class PositionNode : ShaderGraphNode
    {
        public PositionNode()
        {
            NodeType = "Position";
            Title = "Position";
            AddOutput("Position", ShaderGraphDataType.Float3);
            SetStringMeta("Space", "World");
        }
        public override void GenerateCode(NodeCodegenContext ctx)
        {
            var space = GetStringMeta("Space", "World");
            var expr = space switch
            {
                "Object" => "IN.ObjectPosition",
                "World" => "IN.WorldPosition",
                "View" => "mul(g_ViewMatrix, float4(IN.WorldPosition, 1.0)).xyz",
                "Tangent" => "IN.TangentPosition",
                _ => "IN.WorldPosition"
            };
            ctx.SetOutput("Position", expr);
        }
    }

    /// <summary>法线节点 (世界/模型/视图/切线空间)。</summary>
    public class NormalNode : ShaderGraphNode
    {
        public NormalNode()
        {
            NodeType = "Normal";
            Title = "Normal";
            AddOutput("Normal", ShaderGraphDataType.Float3);
            SetStringMeta("Space", "World");
        }
        public override void GenerateCode(NodeCodegenContext ctx)
        {
            var space = GetStringMeta("Space", "World");
            var expr = space switch
            {
                "Object" => "IN.ObjectNormal",
                "World" => "IN.WorldNormal",
                "View" => "mul((float3x3)g_ViewMatrix, IN.WorldNormal)",
                "Tangent" => "float3(0, 0, 1)",
                _ => "IN.WorldNormal"
            };
            ctx.SetOutput("Normal", expr);
        }
    }

    /// <summary>切线节点。</summary>
    public class TangentNode : ShaderGraphNode
    {
        public TangentNode()
        {
            NodeType = "Tangent"; Title = "Tangent";
            AddOutput("Tangent", ShaderGraphDataType.Float3);
        }
        public override void GenerateCode(NodeCodegenContext ctx) =>
            ctx.SetOutput("Tangent", "IN.WorldTangent");
    }

    /// <summary>副切线节点。</summary>
    public class BitangentNode : ShaderGraphNode
    {
        public BitangentNode()
        {
            NodeType = "Bitangent"; Title = "Bitangent";
            AddOutput("Bitangent", ShaderGraphDataType.Float3);
        }
        public override void GenerateCode(NodeCodegenContext ctx) =>
            ctx.SetOutput("Bitangent", "IN.WorldBitangent");
    }

    /// <summary>UV 节点 (UV0/UV1/UV2/UV3)。</summary>
    public class UVNode : ShaderGraphNode
    {
        public UVNode()
        {
            NodeType = "UV"; Title = "UV";
            AddOutput("UV", ShaderGraphDataType.Float2);
            SetIntMeta("Channel", 0);
        }
        public override void GenerateCode(NodeCodegenContext ctx)
        {
            var ch = GetIntMeta("Channel", 0);
            ctx.SetOutput("UV", ch switch
            {
                0 => "IN.UV0",
                1 => "IN.UV1",
                2 => "IN.UV2",
                3 => "IN.UV3",
                _ => "IN.UV0"
            });
        }
    }

    /// <summary>常量颜色节点。</summary>
    public class ColorNode : ShaderGraphNode
    {
        public ColorNode()
        {
            NodeType = "Color"; Title = "Color";
            AddOutput("Color", ShaderGraphDataType.Float4,
                new Vector4(1, 1, 1, 1));
            SetVectorMeta("Color", new Vector4(1, 1, 1, 1));
        }
        public override void GenerateCode(NodeCodegenContext ctx)
        {
            var c = GetVectorMeta("Color", new Vector4(1, 1, 1, 1));
            ctx.SetOutput("Color",
                $"float4({c.X:G9}, {c.Y:G9}, {c.Z:G9}, {c.W:G9})");
        }
    }

    /// <summary>时间节点 (时间/正弦时间/余弦时间/帧时间)。</summary>
    public class TimeNode : ShaderGraphNode
    {
        public TimeNode()
        {
            NodeType = "Time"; Title = "Time";
            AddOutput("Time", ShaderGraphDataType.Float);
            AddOutput("SineTime", ShaderGraphDataType.Float);
            AddOutput("CosineTime", ShaderGraphDataType.Float);
            AddOutput("DeltaTime", ShaderGraphDataType.Float);
        }
        public override void GenerateCode(NodeCodegenContext ctx)
        {
            ctx.SetOutput("Time", "g_Time");
            ctx.SetOutput("SineTime", "sin(g_Time)");
            ctx.SetOutput("CosineTime", "cos(g_Time)");
            ctx.SetOutput("DeltaTime", "g_DeltaTime");
        }
    }

    /// <summary>视图方向节点。</summary>
    public class ViewDirectionNode : ShaderGraphNode
    {
        public ViewDirectionNode()
        {
            NodeType = "ViewDirection"; Title = "View Direction";
            AddOutput("ViewDir", ShaderGraphDataType.Float3);
            SetStringMeta("Space", "World");
        }
        public override void GenerateCode(NodeCodegenContext ctx)
        {
            var space = GetStringMeta("Space", "World");
            ctx.SetOutput("ViewDir", space == "World"
                ? "normalize(g_CameraWorldPos - IN.WorldPosition)"
                : "normalize(mul((float3x3)g_ViewMatrix, g_CameraWorldPos - IN.WorldPosition))");
        }
    }

    /// <summary>摄像机位置节点。</summary>
    public class CameraPositionNode : ShaderGraphNode
    {
        public CameraPositionNode()
        {
            NodeType = "CameraPosition"; Title = "Camera Position";
            AddOutput("Position", ShaderGraphDataType.Float3);
        }
        public override void GenerateCode(NodeCodegenContext ctx) =>
            ctx.SetOutput("Position", "g_CameraWorldPos");
    }

    /// <summary>屏幕位置节点。</summary>
    public class ScreenPositionNode : ShaderGraphNode
    {
        public ScreenPositionNode()
        {
            NodeType = "ScreenPosition"; Title = "Screen Position";
            AddOutput("ScreenPos", ShaderGraphDataType.Float4);
        }
        public override void GenerateCode(NodeCodegenContext ctx) =>
            ctx.SetOutput("ScreenPos", "IN.ScreenPosition");
    }

    /// <summary>屏幕 UV 节点。</summary>
    public class ScreenUVNode : ShaderGraphNode
    {
        public ScreenUVNode()
        {
            NodeType = "ScreenUV"; Title = "Screen UV";
            AddOutput("UV", ShaderGraphDataType.Float2);
        }
        public override void GenerateCode(NodeCodegenContext ctx) =>
            ctx.SetOutput("UV", "IN.ScreenPosition.xy / IN.ScreenPosition.w");
    }

    /// <summary>片元坐标节点。</summary>
    public class FragCoordNode : ShaderGraphNode
    {
        public FragCoordNode()
        {
            NodeType = "FragCoord"; Title = "Fragment Coord";
            AddOutput("FragCoord", ShaderGraphDataType.Float4);
        }
        public override void GenerateCode(NodeCodegenContext ctx) =>
            ctx.SetOutput("FragCoord", "float4(IN.PixelCoord, g_ScreenSize.zw)");
    }

    /// <summary>实例 ID 节点。</summary>
    public class InstanceIDNode : ShaderGraphNode
    {
        public InstanceIDNode()
        {
            NodeType = "InstanceID"; Title = "Instance ID";
            AddOutput("InstanceID", ShaderGraphDataType.UInt);
        }
        public override void GenerateCode(NodeCodegenContext ctx) =>
            ctx.SetOutput("InstanceID", "IN.InstanceID");
    }

    /// <summary>顶点 ID 节点。</summary>
    public class VertexIDNode : ShaderGraphNode
    {
        public VertexIDNode()
        {
            NodeType = "VertexID"; Title = "Vertex ID";
            AddOutput("VertexID", ShaderGraphDataType.UInt);
        }
        public override void GenerateCode(NodeCodegenContext ctx) =>
            ctx.SetOutput("VertexID", "IN.VertexID");
    }

    /// <summary>摄像机深度纹理节点。</summary>
    public class CameraDepthTextureNode : ShaderGraphNode
    {
        public CameraDepthTextureNode()
        {
            NodeType = "CameraDepthTexture"; Title = "Camera Depth Texture";
            AddOutput("DepthTex", ShaderGraphDataType.Texture2D);
        }
        public override void GenerateCode(NodeCodegenContext ctx)
        {
            ctx.EmitDeclaration("Texture2D g_CameraDepthTexture : register(t100);");
            ctx.SetOutput("DepthTex", "g_CameraDepthTexture");
        }
    }

    /// <summary>摄像机颜色纹理节点。</summary>
    public class CameraColorTextureNode : ShaderGraphNode
    {
        public CameraColorTextureNode()
        {
            NodeType = "CameraColorTexture"; Title = "Camera Color Texture";
            AddOutput("ColorTex", ShaderGraphDataType.Texture2D);
        }
        public override void GenerateCode(NodeCodegenContext ctx)
        {
            ctx.EmitDeclaration("Texture2D g_CameraColorTexture : register(t101);");
            ctx.SetOutput("ColorTex", "g_CameraColorTexture");
        }
    }

    /// <summary>属性节点 (引用图属性)。</summary>
    public class PropertyNode : ShaderGraphNode
    {
        public string PropertyName { get; set; } = string.Empty;

        public PropertyNode()
        {
            NodeType = "Property"; Title = "Property";
            AddOutput("Value", ShaderGraphDataType.Float);
        }
        public void BindProperty(ShaderProperty prop)
        {
            PropertyName = prop.Name;
            Title = prop.DisplayName;
            OutputPins[0].DataType = prop.ToPinType();
            OutputPins[0].Name = prop.Name;
            SetStringMeta("PropertyName", prop.Name);
        }
        public override void GenerateCode(NodeCodegenContext ctx)
        {
            var name = GetStringMeta("PropertyName", PropertyName);
            if (string.IsNullOrEmpty(name)) name = "Value";
            ctx.EmitDeclaration($"// Property: {name}");
            ctx.SetOutput(OutputPins[0].Name, $"g_Material.{name}");
        }
    }

    /// <summary>2D 纹理资产节点。</summary>
    public class Texture2DNode : ShaderGraphNode
    {
        public Texture2DNode()
        {
            NodeType = "Texture2D"; Title = "Texture 2D";
            AddOutput("Tex", ShaderGraphDataType.Texture2D);
            SetStringMeta("TexturePath", "");
            SetStringMeta("TextureName", "Texture2D_0");
        }
        public override void GenerateCode(NodeCodegenContext ctx)
        {
            var name = GetStringMeta("TextureName", "Texture2D_0");
            var slot = GetIntMeta("RegisterSlot", 0);
            ctx.EmitDeclaration($"Texture2D {name} : register(t{slot});");
            ctx.SetOutput("Tex", name);
        }
    }

    /// <summary>立方体贴图资产节点。</summary>
    public class TextureCubeNode : ShaderGraphNode
    {
        public TextureCubeNode()
        {
            NodeType = "TextureCube"; Title = "Texture Cube";
            AddOutput("Tex", ShaderGraphDataType.TextureCube);
            SetStringMeta("TextureName", "TextureCube_0");
        }
        public override void GenerateCode(NodeCodegenContext ctx)
        {
            var name = GetStringMeta("TextureName", "TextureCube_0");
            var slot = GetIntMeta("RegisterSlot", 0);
            ctx.EmitDeclaration($"TextureCube {name} : register(t{slot});");
            ctx.SetOutput("Tex", name);
        }
    }

    /// <summary>2D 纹理数组资产节点。</summary>
    public class Texture2DArrayNode : ShaderGraphNode
    {
        public Texture2DArrayNode()
        {
            NodeType = "Texture2DArray"; Title = "Texture 2D Array";
            AddOutput("Tex", ShaderGraphDataType.Texture2DArray);
            SetStringMeta("TextureName", "Texture2DArray_0");
        }
        public override void GenerateCode(NodeCodegenContext ctx)
        {
            var name = GetStringMeta("TextureName", "Texture2DArray_0");
            var slot = GetIntMeta("RegisterSlot", 0);
            ctx.EmitDeclaration($"Texture2DArray {name} : register(t{slot});");
            ctx.SetOutput("Tex", name);
        }
    }

    /// <summary>采样器状态节点。</summary>
    public class SamplerStateNode : ShaderGraphNode
    {
        public SamplerStateNode()
        {
            NodeType = "SamplerState"; Title = "Sampler State";
            AddOutput("Sampler", ShaderGraphDataType.SamplerState);
            SetStringMeta("Filter", "Linear");
            SetStringMeta("Address", "Wrap");
        }
        public override void GenerateCode(NodeCodegenContext ctx)
        {
            var name = $"sampler_{Id.Substring(0, 8)}";
            var filter = GetStringMeta("Filter", "Linear");
            var address = GetStringMeta("Address", "Wrap");
            var filterExpr = filter == "Point" ? "FILTER_MIN_MAG_MIP_POINT" : "FILTER_MIN_MAG_MIP_LINEAR";
            ctx.EmitDeclaration(
                $"SamplerState {name} : register(s{GetIntMeta("RegisterSlot", 0)}); // {filter}/{address}");
            ctx.SetOutput("Sampler", name);
        }
    }

    // =========================================================================
    // B. 数学节点
    // =========================================================================

    public class AddNode : BinaryMathNode
    {
        public AddNode() { NodeType = "Add"; Title = "Add"; }
        protected override string Op(string a, string b) => $"({a} + {b})";
    }

    public class SubtractNode : BinaryMathNode
    {
        public SubtractNode() { NodeType = "Subtract"; Title = "Subtract"; }
        protected override string Op(string a, string b) => $"({a} - {b})";
    }

    public class MultiplyNode : BinaryMathNode
    {
        public MultiplyNode() { NodeType = "Multiply"; Title = "Multiply"; }
        protected override string Op(string a, string b) => $"({a} * {b})";
    }

    public class DivideNode : BinaryMathNode
    {
        public DivideNode() { NodeType = "Divide"; Title = "Divide"; }
        protected override string Op(string a, string b) => $"({a} / {b})";
    }

    public class PowerNode : BinaryMathNode
    {
        public PowerNode() { NodeType = "Power"; Title = "Power"; }
        protected override string Op(string a, string b) => $"pow({a}, {b})";
    }

    public class SquareRootNode : UnaryMathNode
    {
        public SquareRootNode() { NodeType = "SquareRoot"; Title = "Square Root"; }
        protected override string Op(string x) => $"sqrt({x})";
    }

    public class LogNode : UnaryMathNode
    {
        public LogNode() { NodeType = "Log"; Title = "Log"; }
        protected override string Op(string x) => $"log({x})";
    }

    public class ExpNode : UnaryMathNode
    {
        public ExpNode() { NodeType = "Exp"; Title = "Exp"; }
        protected override string Op(string x) => $"exp({x})";
    }

    public class AbsNode : UnaryMathNode
    {
        public AbsNode() { NodeType = "Abs"; Title = "Abs"; }
        protected override string Op(string x) => $"abs({x})";
    }

    public class FloorNode : UnaryMathNode
    {
        public FloorNode() { NodeType = "Floor"; Title = "Floor"; }
        protected override string Op(string x) => $"floor({x})";
    }

    public class CeilingNode : UnaryMathNode
    {
        public CeilingNode() { NodeType = "Ceiling"; Title = "Ceiling"; }
        protected override string Op(string x) => $"ceil({x})";
    }

    public class RoundNode : UnaryMathNode
    {
        public RoundNode() { NodeType = "Round"; Title = "Round"; }
        protected override string Op(string x) => $"round({x})";
    }

    public class TruncateNode : UnaryMathNode
    {
        public TruncateNode() { NodeType = "Truncate"; Title = "Truncate"; }
        protected override string Op(string x) => $"trunc({x})";
    }

    public class FractionNode : UnaryMathNode
    {
        public FractionNode() { NodeType = "Fraction"; Title = "Fraction"; }
        protected override string Op(string x) => $"frac({x})";
    }

    public class SineNode : UnaryMathNode
    {
        public SineNode() { NodeType = "Sine"; Title = "Sine"; }
        protected override string Op(string x) => $"sin({x})";
    }

    public class CosineNode : UnaryMathNode
    {
        public CosineNode() { NodeType = "Cosine"; Title = "Cosine"; }
        protected override string Op(string x) => $"cos({x})";
    }

    public class TanNode : UnaryMathNode
    {
        public TanNode() { NodeType = "Tan"; Title = "Tan"; }
        protected override string Op(string x) => $"tan({x})";
    }

    public class ArcSineNode : UnaryMathNode
    {
        public ArcSineNode() { NodeType = "ArcSine"; Title = "ArcSine"; }
        protected override string Op(string x) => $"asin({x})";
    }

    public class ArcCosineNode : UnaryMathNode
    {
        public ArcCosineNode() { NodeType = "ArcCosine"; Title = "ArcCosine"; }
        protected override string Op(string x) => $"acos({x})";
    }

    public class ArcTangentNode : UnaryMathNode
    {
        public ArcTangentNode() { NodeType = "ArcTangent"; Title = "ArcTangent"; }
        protected override string Op(string x) => $"atan({x})";
    }

    public class ArcTangent2Node : BinaryMathNode
    {
        public ArcTangent2Node() { NodeType = "ArcTangent2"; Title = "ArcTangent2"; }
        protected override string Op(string a, string b) => $"atan2({a}, {b})";
    }

    public class MinNode : BinaryMathNode
    {
        public MinNode() { NodeType = "Min"; Title = "Min"; }
        protected override string Op(string a, string b) => $"min({a}, {b})";
    }

    public class MaxNode : BinaryMathNode
    {
        public MaxNode() { NodeType = "Max"; Title = "Max"; }
        protected override string Op(string a, string b) => $"max({a}, {b})";
    }

    /// <summary>钳制节点 (Clamp(Min, Max))。</summary>
    public class ClampNode : ShaderGraphNode
    {
        public ClampNode()
        {
            NodeType = "Clamp"; Title = "Clamp";
            AddInput("X", ShaderGraphDataType.Float);
            AddInput("Min", ShaderGraphDataType.Float, new Vector4(0, 0, 0, 1));
            AddInput("Max", ShaderGraphDataType.Float, new Vector4(1, 1, 1, 1));
            AddOutput("Result", ShaderGraphDataType.Float);
        }
        public override void GenerateCode(NodeCodegenContext ctx) =>
            ctx.SetOutput("Result", $"clamp({ctx.GetInput("X")}, {ctx.GetInput("Min")}, {ctx.GetInput("Max")})");
    }

    public class SaturateNode : UnaryMathNode
    {
        public SaturateNode() { NodeType = "Saturate"; Title = "Saturate"; }
        protected override string Op(string x) => $"saturate({x})";
    }

    /// <summary>线性插值节点 (Lerp(A, B, T))。</summary>
    public class LerpNode : ShaderGraphNode
    {
        public LerpNode()
        {
            NodeType = "Lerp"; Title = "Lerp";
            AddInput("A", ShaderGraphDataType.Float);
            AddInput("B", ShaderGraphDataType.Float);
            AddInput("T", ShaderGraphDataType.Float);
            AddOutput("Result", ShaderGraphDataType.Float);
        }
        public override void GenerateCode(NodeCodegenContext ctx) =>
            ctx.SetOutput("Result", $"lerp({ctx.GetInput("A")}, {ctx.GetInput("B")}, {ctx.GetInput("T")})");
    }

    /// <summary>反线性插值节点 (InverseLerp(A, B, T) = (T-A)/(B-A))。</summary>
    public class InverseLerpNode : ShaderGraphNode
    {
        public InverseLerpNode()
        {
            NodeType = "InverseLerp"; Title = "Inverse Lerp";
            AddInput("A", ShaderGraphDataType.Float);
            AddInput("B", ShaderGraphDataType.Float);
            AddInput("T", ShaderGraphDataType.Float);
            AddOutput("Result", ShaderGraphDataType.Float);
        }
        public override void GenerateCode(NodeCodegenContext ctx) =>
            ctx.SetOutput("Result", $"(({ctx.GetInput("T")} - {ctx.GetInput("A")}) / ({ctx.GetInput("B")} - {ctx.GetInput("A")}))");
    }

    /// <summary>平滑阶梯节点 (Smoothstep(Min, Max, X))。</summary>
    public class SmoothstepNode : ShaderGraphNode
    {
        public SmoothstepNode()
        {
            NodeType = "Smoothstep"; Title = "Smoothstep";
            AddInput("Min", ShaderGraphDataType.Float, new Vector4(0, 0, 0, 1));
            AddInput("Max", ShaderGraphDataType.Float, new Vector4(1, 1, 1, 1));
            AddInput("X", ShaderGraphDataType.Float);
            AddOutput("Result", ShaderGraphDataType.Float);
        }
        public override void GenerateCode(NodeCodegenContext ctx) =>
            ctx.SetOutput("Result", $"smoothstep({ctx.GetInput("Min")}, {ctx.GetInput("Max")}, {ctx.GetInput("X")})");
    }

    public class StepNode : BinaryMathNode
    {
        public StepNode() { NodeType = "Step"; Title = "Step"; }
        protected override string Op(string a, string b) => $"step({a}, {b})";
    }

    public class ModuloNode : BinaryMathNode
    {
        public ModuloNode() { NodeType = "Modulo"; Title = "Modulo"; }
        protected override string Op(string a, string b) => $"fmod({a}, {b})";
    }

    public class FmodNode : BinaryMathNode
    {
        public FmodNode() { NodeType = "Fmod"; Title = "Fmod"; }
        protected override string Op(string a, string b) => $"fmod({a}, {b})";
    }

    public class FwidthNode : UnaryMathNode
    {
        public FwidthNode() { NodeType = "Fwidth"; Title = "Fwidth"; }
        protected override string Op(string x) => $"fwidth({x})";
    }

    public class DDXNode : UnaryMathNode
    {
        public DDXNode() { NodeType = "DDX"; Title = "DDX"; }
        protected override string Op(string x) => $"ddx({x})";
    }

    public class DDYNode : UnaryMathNode
    {
        public DDYNode() { NodeType = "DDY"; Title = "DDY"; }
        protected override string Op(string x) => $"ddy({x})";
    }

    public class DeterminantNode : UnaryMathNode
    {
        public DeterminantNode()
        {
            NodeType = "Determinant"; Title = "Determinant";
            ResultType = ShaderGraphDataType.Float;
            InputPins[0].DataType = ShaderGraphDataType.Matrix;
        }
        protected override string Op(string x) => $"determinant({x})";
    }

    public class TransposeNode : UnaryMathNode
    {
        public TransposeNode()
        {
            NodeType = "Transpose"; Title = "Transpose";
            ResultType = ShaderGraphDataType.Matrix;
            InputPins[0].DataType = ShaderGraphDataType.Matrix;
        }
        protected override string Op(string x) => $"transpose({x})";
    }

    public class InverseMatrixNode : UnaryMathNode
    {
        public InverseMatrixNode()
        {
            NodeType = "InverseMatrix"; Title = "Inverse Matrix";
            ResultType = ShaderGraphDataType.Matrix;
            InputPins[0].DataType = ShaderGraphDataType.Matrix;
        }
        protected override string Op(string x) => $"InverseMatrix4x4({x})";
    }

    public class NormalizeNode : UnaryMathNode
    {
        public NormalizeNode()
        {
            NodeType = "Normalize"; Title = "Normalize";
            ResultType = ShaderGraphDataType.Float3;
            InputPins[0].DataType = ShaderGraphDataType.Float3;
        }
        protected override string Op(string x) => $"normalize({x})";
    }

    public class LengthNode : UnaryMathNode
    {
        public LengthNode()
        {
            NodeType = "Length"; Title = "Length";
            ResultType = ShaderGraphDataType.Float;
            InputPins[0].DataType = ShaderGraphDataType.Float3;
        }
        protected override string Op(string x) => $"length({x})";
    }

    public class DistanceNode : BinaryMathNode
    {
        public DistanceNode()
        {
            NodeType = "Distance"; Title = "Distance";
            ResultType = ShaderGraphDataType.Float;
            InputPins[0].DataType = ShaderGraphDataType.Float3;
            InputPins[1].DataType = ShaderGraphDataType.Float3;
        }
        protected override string Op(string a, string b) => $"distance({a}, {b})";
    }

    public class DotProductNode : BinaryMathNode
    {
        public DotProductNode()
        {
            NodeType = "DotProduct"; Title = "Dot Product";
            ResultType = ShaderGraphDataType.Float;
            InputPins[0].DataType = ShaderGraphDataType.Float3;
            InputPins[1].DataType = ShaderGraphDataType.Float3;
        }
        protected override string Op(string a, string b) => $"dot({a}, {b})";
    }

    public class CrossProductNode : BinaryMathNode
    {
        public CrossProductNode()
        {
            NodeType = "CrossProduct"; Title = "Cross Product";
            ResultType = ShaderGraphDataType.Float3;
            InputPins[0].DataType = ShaderGraphDataType.Float3;
            InputPins[1].DataType = ShaderGraphDataType.Float3;
        }
        protected override string Op(string a, string b) => $"cross({a}, {b})";
    }

    public class ReflectNode : BinaryMathNode
    {
        public ReflectNode()
        {
            NodeType = "Reflect"; Title = "Reflect";
            ResultType = ShaderGraphDataType.Float3;
            InputPins[0].DataType = ShaderGraphDataType.Float3;
            InputPins[1].DataType = ShaderGraphDataType.Float3;
        }
        protected override string Op(string a, string b) => $"reflect({a}, {b})";
    }

    public class RefractNode : ShaderGraphNode
    {
        public RefractNode()
        {
            NodeType = "Refract"; Title = "Refract";
            AddInput("I", ShaderGraphDataType.Float3);
            AddInput("N", ShaderGraphDataType.Float3);
            AddInput("Eta", ShaderGraphDataType.Float);
            AddOutput("Result", ShaderGraphDataType.Float3);
        }
        public override void GenerateCode(NodeCodegenContext ctx) =>
            ctx.SetOutput("Result", $"refract({ctx.GetInput("I")}, {ctx.GetInput("N")}, {ctx.GetInput("Eta")})");
    }

    public class ProjectNode : BinaryMathNode
    {
        public ProjectNode()
        {
            NodeType = "Project"; Title = "Project";
            ResultType = ShaderGraphDataType.Float3;
            InputPins[0].DataType = ShaderGraphDataType.Float3;
            InputPins[1].DataType = ShaderGraphDataType.Float3;
        }
        protected override string Op(string a, string b) =>
            $"({b} * dot({a}, {b}) / dot({b}, {b}))";
    }

    public class UnprojectNode : BinaryMathNode
    {
        public UnprojectNode()
        {
            NodeType = "Unproject"; Title = "Unproject";
            ResultType = ShaderGraphDataType.Float3;
            InputPins[0].DataType = ShaderGraphDataType.Float3;
            InputPins[1].DataType = ShaderGraphDataType.Float3;
        }
        protected override string Op(string a, string b) =>
            $"({a} - {b} * dot({a}, {b}) / dot({b}, {b}))";
    }

    public class FaceForwardNode : BinaryMathNode
    {
        public FaceForwardNode()
        {
            NodeType = "FaceForward"; Title = "FaceForward";
            ResultType = ShaderGraphDataType.Float3;
            InputPins[0].DataType = ShaderGraphDataType.Float3;
            InputPins[1].DataType = ShaderGraphDataType.Float3;
        }
        protected override string Op(string a, string b) => $"faceforward({a}, {b}, {a})";
    }

    public class NegateNode : UnaryMathNode
    {
        public NegateNode() { NodeType = "Negate"; Title = "Negate"; }
        protected override string Op(string x) => $"(-{x})";
    }

    public class OneMinusNode : UnaryMathNode
    {
        public OneMinusNode() { NodeType = "OneMinus"; Title = "One Minus"; }
        protected override string Op(string x) => $"(1.0 - {x})";
    }

    public class ReciprocalNode : UnaryMathNode
    {
        public ReciprocalNode() { NodeType = "Reciprocal"; Title = "Reciprocal"; }
        protected override string Op(string x) => $"(1.0 / {x})";
    }

    public class RemainderNode : BinaryMathNode
    {
        public RemainderNode() { NodeType = "Remainder"; Title = "Remainder"; }
        protected override string Op(string a, string b) => $"({a} - {b} * floor({a} / {b}))";
    }

    /// <summary>范围映射节点 (输入范围 -> 输出范围)。</summary>
    public class MapRangeNode : ShaderGraphNode
    {
        public MapRangeNode()
        {
            NodeType = "MapRange"; Title = "Map Range";
            AddInput("In", ShaderGraphDataType.Float);
            AddInput("InMin", ShaderGraphDataType.Float, new Vector4(0, 0, 0, 1));
            AddInput("InMax", ShaderGraphDataType.Float, new Vector4(1, 1, 1, 1));
            AddInput("OutMin", ShaderGraphDataType.Float, new Vector4(0, 0, 0, 1));
            AddInput("OutMax", ShaderGraphDataType.Float, new Vector4(1, 1, 1, 1));
            AddOutput("Result", ShaderGraphDataType.Float);
            SetBoolMeta("Clamp", true);
        }
        public override void GenerateCode(NodeCodegenContext ctx)
        {
            var expr = $"(({ctx.GetInput("In")} - {ctx.GetInput("InMin")}) / ({ctx.GetInput("InMax")} - {ctx.GetInput("InMin")}))";
            expr = $"({expr} * ({ctx.GetInput("OutMax")} - {ctx.GetInput("OutMin")}) + {ctx.GetInput("OutMin")})";
            if (GetBoolMeta("Clamp", true))
                expr = $"saturate({expr})";
            ctx.SetOutput("Result", expr);
        }
    }

    /// <summary>随机范围节点。</summary>
    public class RandomRangeNode : ShaderGraphNode
    {
        public RandomRangeNode()
        {
            NodeType = "RandomRange"; Title = "Random Range";
            AddInput("Seed", ShaderGraphDataType.Float2);
            AddInput("Min", ShaderGraphDataType.Float);
            AddInput("Max", ShaderGraphDataType.Float);
            AddOutput("Result", ShaderGraphDataType.Float);
        }
        public override void GenerateCode(NodeCodegenContext ctx) =>
            ctx.SetOutput("Result",
                $"lerp({ctx.GetInput("Min")}, {ctx.GetInput("Max")}, Random01({ctx.GetInput("Seed")}))");
    }

    /// <summary>通用噪声节点 (Perlin/Simplex/WhiteNoise/ValueNoise)。</summary>
    public class NoiseNode : ShaderGraphNode
    {
        public NoiseNode()
        {
            NodeType = "Noise"; Title = "Noise";
            AddInput("UV", ShaderGraphDataType.Float2);
            AddInput("Scale", ShaderGraphDataType.Float, new Vector4(1, 1, 1, 1));
            AddOutput("Result", ShaderGraphDataType.Float);
            SetStringMeta("NoiseType", "Perlin");
        }
        public override void GenerateCode(NodeCodegenContext ctx)
        {
            var type = GetStringMeta("NoiseType", "Perlin");
            var uv = ctx.GetInput("UV");
            var scale = ctx.GetInput("Scale");
            var expr = type switch
            {
                "Perlin" => $"PerlinNoise2D({uv} * {scale})",
                "Simplex" => $"SimplexNoise2D({uv} * {scale})",
                "Value" => $"ValueNoise2D({uv} * {scale})",
                "White" => $"Random01({uv})",
                _ => $"PerlinNoise2D({uv} * {scale})"
            };
            ctx.SetOutput("Result", expr);
        }
    }

    // =========================================================================
    // C. 向量/矩阵节点
    // =========================================================================

    public class MakeVector2Node : ShaderGraphNode
    {
        public MakeVector2Node()
        {
            NodeType = "MakeVector2"; Title = "Make Vector 2";
            AddInput("X", ShaderGraphDataType.Float);
            AddInput("Y", ShaderGraphDataType.Float);
            AddOutput("Result", ShaderGraphDataType.Float2);
        }
        public override void GenerateCode(NodeCodegenContext ctx) =>
            ctx.SetOutput("Result", $"float2({ctx.GetInput("X")}, {ctx.GetInput("Y")})");
    }

    public class MakeVector3Node : ShaderGraphNode
    {
        public MakeVector3Node()
        {
            NodeType = "MakeVector3"; Title = "Make Vector 3";
            AddInput("X", ShaderGraphDataType.Float);
            AddInput("Y", ShaderGraphDataType.Float);
            AddInput("Z", ShaderGraphDataType.Float);
            AddOutput("Result", ShaderGraphDataType.Float3);
        }
        public override void GenerateCode(NodeCodegenContext ctx) =>
            ctx.SetOutput("Result", $"float3({ctx.GetInput("X")}, {ctx.GetInput("Y")}, {ctx.GetInput("Z")})");
    }

    public class MakeVector4Node : ShaderGraphNode
    {
        public MakeVector4Node()
        {
            NodeType = "MakeVector4"; Title = "Make Vector 4";
            AddInput("X", ShaderGraphDataType.Float);
            AddInput("Y", ShaderGraphDataType.Float);
            AddInput("Z", ShaderGraphDataType.Float);
            AddInput("W", ShaderGraphDataType.Float);
            AddOutput("Result", ShaderGraphDataType.Float4);
        }
        public override void GenerateCode(NodeCodegenContext ctx) =>
            ctx.SetOutput("Result", $"float4({ctx.GetInput("X")}, {ctx.GetInput("Y")}, {ctx.GetInput("Z")}, {ctx.GetInput("W")})");
    }

    public class MakeMatrixNode : ShaderGraphNode
    {
        public MakeMatrixNode()
        {
            NodeType = "MakeMatrix"; Title = "Make Matrix";
            for (int i = 0; i < 4; i++)
                AddInput($"M{i}", ShaderGraphDataType.Float4);
            AddOutput("Result", ShaderGraphDataType.Matrix);
        }
        public override void GenerateCode(NodeCodegenContext ctx) =>
            ctx.SetOutput("Result",
                $"float4x4({ctx.GetInput("M0")}, {ctx.GetInput("M1")}, {ctx.GetInput("M2")}, {ctx.GetInput("M3")})");
    }

    public class BreakVector2Node : ShaderGraphNode
    {
        public BreakVector2Node()
        {
            NodeType = "BreakVector2"; Title = "Break Vector 2";
            AddInput("V", ShaderGraphDataType.Float2);
            AddOutput("X", ShaderGraphDataType.Float);
            AddOutput("Y", ShaderGraphDataType.Float);
        }
        public override void GenerateCode(NodeCodegenContext ctx)
        {
            var v = ctx.GetInput("V");
            ctx.SetOutput("X", $"({v}).x");
            ctx.SetOutput("Y", $"({v}).y");
        }
    }

    public class BreakVector3Node : ShaderGraphNode
    {
        public BreakVector3Node()
        {
            NodeType = "BreakVector3"; Title = "Break Vector 3";
            AddInput("V", ShaderGraphDataType.Float3);
            AddOutput("X", ShaderGraphDataType.Float);
            AddOutput("Y", ShaderGraphDataType.Float);
            AddOutput("Z", ShaderGraphDataType.Float);
        }
        public override void GenerateCode(NodeCodegenContext ctx)
        {
            var v = ctx.GetInput("V");
            ctx.SetOutput("X", $"({v}).x");
            ctx.SetOutput("Y", $"({v}).y");
            ctx.SetOutput("Z", $"({v}).z");
        }
    }

    public class BreakVector4Node : ShaderGraphNode
    {
        public BreakVector4Node()
        {
            NodeType = "BreakVector4"; Title = "Break Vector 4";
            AddInput("V", ShaderGraphDataType.Float4);
            AddOutput("X", ShaderGraphDataType.Float);
            AddOutput("Y", ShaderGraphDataType.Float);
            AddOutput("Z", ShaderGraphDataType.Float);
            AddOutput("W", ShaderGraphDataType.Float);
        }
        public override void GenerateCode(NodeCodegenContext ctx)
        {
            var v = ctx.GetInput("V");
            ctx.SetOutput("X", $"({v}).x");
            ctx.SetOutput("Y", $"({v}).y");
            ctx.SetOutput("Z", $"({v}).z");
            ctx.SetOutput("W", $"({v}).w");
        }
    }

    public class BreakMatrixNode : ShaderGraphNode
    {
        public BreakMatrixNode()
        {
            NodeType = "BreakMatrix"; Title = "Break Matrix";
            AddInput("M", ShaderGraphDataType.Matrix);
            for (int i = 0; i < 4; i++)
                AddOutput($"M{i}", ShaderGraphDataType.Float4);
        }
        public override void GenerateCode(NodeCodegenContext ctx)
        {
            var m = ctx.GetInput("M");
            ctx.SetOutput("M0", $"({m})[0]");
            ctx.SetOutput("M1", $"({m})[1]");
            ctx.SetOutput("M2", $"({m})[2]");
            ctx.SetOutput("M3", $"({m})[3]");
        }
    }

    /// <summary>Swizzle 节点 (分量重排)。</summary>
    public class SwizzleNode : ShaderGraphNode
    {
        public SwizzleNode()
        {
            NodeType = "Swizzle"; Title = "Swizzle";
            AddInput("In", ShaderGraphDataType.Float4);
            AddOutput("Result", ShaderGraphDataType.Float4);
            SetStringMeta("Swizzle", "xyzw");
        }
        public override void GenerateCode(NodeCodegenContext ctx)
        {
            var sw = GetStringMeta("Swizzle", "xyzw");
            // 仅允许合法分量
            var cleaned = new string(sw.Where(c => "xyzwrgba01".Contains(c)).ToArray());
            if (cleaned.Length == 0) cleaned = "xyzw";
            var input = ctx.GetInput("In");
            // 根据输出长度设置类型
            OutputPins[0].DataType = cleaned.Length switch
            {
                1 => ShaderGraphDataType.Float,
                2 => ShaderGraphDataType.Float2,
                3 => ShaderGraphDataType.Float3,
                _ => ShaderGraphDataType.Float4
            };
            ctx.SetOutput("Result", $"({input}).{cleaned}");
        }
    }

    /// <summary>合并节点 (Combine 多个分量)。</summary>
    public class CombineNode : ShaderGraphNode
    {
        public CombineNode()
        {
            NodeType = "Combine"; Title = "Combine";
            AddInput("R", ShaderGraphDataType.Float);
            AddInput("G", ShaderGraphDataType.Float);
            AddInput("B", ShaderGraphDataType.Float);
            AddInput("A", ShaderGraphDataType.Float, new Vector4(1, 1, 1, 1));
            AddOutput("Result", ShaderGraphDataType.Float4);
        }
        public override void GenerateCode(NodeCodegenContext ctx) =>
            ctx.SetOutput("Result", $"float4({ctx.GetInput("R")}, {ctx.GetInput("G")}, {ctx.GetInput("B")}, {ctx.GetInput("A")})");
    }

    /// <summary>拆分节点 (Split RGBA)。</summary>
    public class SplitNode : ShaderGraphNode
    {
        public SplitNode()
        {
            NodeType = "Split"; Title = "Split";
            AddInput("In", ShaderGraphDataType.Float4);
            AddOutput("R", ShaderGraphDataType.Float);
            AddOutput("G", ShaderGraphDataType.Float);
            AddOutput("B", ShaderGraphDataType.Float);
            AddOutput("A", ShaderGraphDataType.Float);
        }
        public override void GenerateCode(NodeCodegenContext ctx)
        {
            var v = ctx.GetInput("In");
            ctx.SetOutput("R", $"({v}).r");
            ctx.SetOutput("G", $"({v}).g");
            ctx.SetOutput("B", $"({v}).b");
            ctx.SetOutput("A", $"({v}).a");
        }
    }

    /// <summary>旋转节点 (通用)。</summary>
    public class RotateNode : ShaderGraphNode
    {
        public RotateNode()
        {
            NodeType = "Rotate"; Title = "Rotate";
            AddInput("UV", ShaderGraphDataType.Float2);
            AddInput("Center", ShaderGraphDataType.Float2, new Vector4(0.5f, 0.5f, 0, 1));
            AddInput("Angle", ShaderGraphDataType.Float);
            AddOutput("Result", ShaderGraphDataType.Float2);
        }
        public override void GenerateCode(NodeCodegenContext ctx)
        {
            var uv = ctx.GetInput("UV");
            var c = ctx.GetInput("Center");
            var a = ctx.GetInput("Angle");
            ctx.Emit($"float2 dir = {uv} - {c};");
            ctx.Emit($"float s = sin({a}); float co = cos({a});");
            ctx.SetOutput("Result", $"(float2(dir.x * co - dir.y * s, dir.x * s + dir.y * co) + {c})");
        }
    }

    /// <summary>2D 旋转节点。</summary>
    public class Rotate2DNode : ShaderGraphNode
    {
        public Rotate2DNode()
        {
            NodeType = "Rotate2D"; Title = "Rotate 2D";
            AddInput("UV", ShaderGraphDataType.Float2);
            AddInput("Angle", ShaderGraphDataType.Float);
            AddOutput("Result", ShaderGraphDataType.Float2);
        }
        public override void GenerateCode(NodeCodegenContext ctx)
        {
            var uv = ctx.GetInput("UV");
            var a = ctx.GetInput("Angle");
            ctx.SetOutput("Result",
                $"mul(float2x2(cos({a}), -sin({a}), sin({a}), cos({a})), {uv})");
        }
    }

    /// <summary>3D 旋转节点 (绕轴旋转)。</summary>
    public class Rotate3DNode : ShaderGraphNode
    {
        public Rotate3DNode()
        {
            NodeType = "Rotate3D"; Title = "Rotate 3D";
            AddInput("Vector", ShaderGraphDataType.Float3);
            AddInput("Axis", ShaderGraphDataType.Float3, new Vector4(0, 1, 0, 1));
            AddInput("Angle", ShaderGraphDataType.Float);
            AddOutput("Result", ShaderGraphDataType.Float3);
        }
        public override void GenerateCode(NodeCodegenContext ctx) =>
            ctx.SetOutput("Result",
                $"mul(RotationMatrix3D(normalize({ctx.GetInput("Axis")}), {ctx.GetInput("Angle")}), {ctx.GetInput("Vector")})");
    }

    /// <summary>空间变换节点 (世界<->模型<->视图<->切线<->屏幕)。</summary>
    public class TransformNode : ShaderGraphNode
    {
        public TransformNode()
        {
            NodeType = "Transform"; Title = "Transform";
            AddInput("Input", ShaderGraphDataType.Float3);
            AddOutput("Result", ShaderGraphDataType.Float3);
            SetStringMeta("From", "World");
            SetStringMeta("To", "Tangent");
        }
        public override void GenerateCode(NodeCodegenContext ctx)
        {
            var from = GetStringMeta("From", "World");
            var to = GetStringMeta("To", "Tangent");
            var input = ctx.GetInput("Input");
            var matrix = GetTransformMatrix(from, to);
            ctx.SetOutput("Result", $"mul({matrix}, {input})");
        }
        private static string GetTransformMatrix(string from, string to)
        {
            // 简化: 返回从 from 到 to 的矩阵表达式
            if (from == "World" && to == "View") return "(float3x3)g_ViewMatrix";
            if (from == "View" && to == "World") return "(float3x3)g_InvViewMatrix";
            if (from == "World" && to == "Tangent") return "float3x3(IN.WorldTangent, IN.WorldBitangent, IN.WorldNormal)";
            if (from == "Tangent" && to == "World") return "transpose(float3x3(IN.WorldTangent, IN.WorldBitangent, IN.WorldNormal))";
            if (from == "Object" && to == "World") return "(float3x3)g_WorldMatrix";
            if (from == "World" && to == "Object") return "(float3x3)g_InvWorldMatrix";
            return "(float3x3)g_WorldMatrix";
        }
    }

    /// <summary>矩阵乘法节点。</summary>
    public class MatrixMultiplyNode : ShaderGraphNode
    {
        public MatrixMultiplyNode()
        {
            NodeType = "MatrixMultiply"; Title = "Matrix Multiply";
            AddInput("A", ShaderGraphDataType.Matrix);
            AddInput("B", ShaderGraphDataType.Matrix);
            AddOutput("Result", ShaderGraphDataType.Matrix);
        }
        public override void GenerateCode(NodeCodegenContext ctx) =>
            ctx.SetOutput("Result", $"mul({ctx.GetInput("A")}, {ctx.GetInput("B")})");
    }

    public class ViewMatrixNode : ShaderGraphNode
    {
        public ViewMatrixNode()
        {
            NodeType = "ViewMatrix"; Title = "View Matrix";
            AddOutput("Matrix", ShaderGraphDataType.Matrix);
        }
        public override void GenerateCode(NodeCodegenContext ctx) => ctx.SetOutput("Matrix", "g_ViewMatrix");
    }

    public class ProjectionMatrixNode : ShaderGraphNode
    {
        public ProjectionMatrixNode()
        {
            NodeType = "ProjectionMatrix"; Title = "Projection Matrix";
            AddOutput("Matrix", ShaderGraphDataType.Matrix);
        }
        public override void GenerateCode(NodeCodegenContext ctx) => ctx.SetOutput("Matrix", "g_ProjMatrix");
    }

    public class ViewProjectionMatrixNode : ShaderGraphNode
    {
        public ViewProjectionMatrixNode()
        {
            NodeType = "ViewProjectionMatrix"; Title = "View Projection Matrix";
            AddOutput("Matrix", ShaderGraphDataType.Matrix);
        }
        public override void GenerateCode(NodeCodegenContext ctx) => ctx.SetOutput("Matrix", "g_ViewProjMatrix");
    }

    public class WorldMatrixNode : ShaderGraphNode
    {
        public WorldMatrixNode()
        {
            NodeType = "WorldMatrix"; Title = "World Matrix";
            AddOutput("Matrix", ShaderGraphDataType.Matrix);
        }
        public override void GenerateCode(NodeCodegenContext ctx) => ctx.SetOutput("Matrix", "g_WorldMatrix");
    }

    public class InverseViewMatrixNode : ShaderGraphNode
    {
        public InverseViewMatrixNode()
        {
            NodeType = "InverseViewMatrix"; Title = "Inverse View Matrix";
            AddOutput("Matrix", ShaderGraphDataType.Matrix);
        }
        public override void GenerateCode(NodeCodegenContext ctx) => ctx.SetOutput("Matrix", "g_InvViewMatrix");
    }

    // =========================================================================
    // D. 纹理/采样节点
    // =========================================================================

    /// <summary>纹理采样节点 (标准/三线性/各向异性)。</summary>
    public class TextureSampleNode : ShaderGraphNode
    {
        public TextureSampleNode()
        {
            NodeType = "TextureSample"; Title = "Texture Sample";
            AddInput("Tex", ShaderGraphDataType.Texture2D, required: true);
            AddInput("UV", ShaderGraphDataType.Float2);
            AddInput("Sampler", ShaderGraphDataType.SamplerState);
            AddOutput("RGBA", ShaderGraphDataType.Float4);
            AddOutput("R", ShaderGraphDataType.Float);
            AddOutput("G", ShaderGraphDataType.Float);
            AddOutput("B", ShaderGraphDataType.Float);
            AddOutput("A", ShaderGraphDataType.Float);
        }
        public override void GenerateCode(NodeCodegenContext ctx)
        {
            var tex = ctx.GetInput("Tex");
            var uv = ctx.GetInput("UV");
            var sampler = ctx.GetInput("Sampler");
            if (string.IsNullOrEmpty(sampler) || sampler == "0")
                sampler = "s_LinearWrapSampler";
            var varName = $"samp_{Id.Substring(0, 8)}";
            ctx.Emit($"{ctx.PrecisionPrefix}4 {varName} = {tex}.Sample({sampler}, {uv});");
            ctx.SetOutput("RGBA", varName);
            ctx.SetOutput("R", $"{varName}.r");
            ctx.SetOutput("G", $"{varName}.g");
            ctx.SetOutput("B", $"{varName}.b");
            ctx.SetOutput("A", $"{varName}.a");
        }
    }

    /// <summary>指定 LOD 采样节点。</summary>
    public class TextureSampleLODNode : ShaderGraphNode
    {
        public TextureSampleLODNode()
        {
            NodeType = "TextureSampleLOD"; Title = "Texture Sample LOD";
            AddInput("Tex", ShaderGraphDataType.Texture2D, required: true);
            AddInput("UV", ShaderGraphDataType.Float2);
            AddInput("LOD", ShaderGraphDataType.Float);
            AddInput("Sampler", ShaderGraphDataType.SamplerState);
            AddOutput("RGBA", ShaderGraphDataType.Float4);
        }
        public override void GenerateCode(NodeCodegenContext ctx)
        {
            var tex = ctx.GetInput("Tex");
            var uv = ctx.GetInput("UV");
            var lod = ctx.GetInput("LOD");
            var sampler = ctx.GetInput("Sampler");
            if (string.IsNullOrEmpty(sampler) || sampler == "0") sampler = "s_LinearWrapSampler";
            var varName = $"sampLod_{Id.Substring(0, 8)}";
            ctx.Emit($"{ctx.PrecisionPrefix}4 {varName} = {tex}.SampleLevel({sampler}, {uv}, {lod});");
            ctx.SetOutput("RGBA", varName);
        }
    }

    /// <summary>指定梯度采样节点。</summary>
    public class TextureSampleGradNode : ShaderGraphNode
    {
        public TextureSampleGradNode()
        {
            NodeType = "TextureSampleGrad"; Title = "Texture Sample Grad";
            AddInput("Tex", ShaderGraphDataType.Texture2D, required: true);
            AddInput("UV", ShaderGraphDataType.Float2);
            AddInput("DDX", ShaderGraphDataType.Float2);
            AddInput("DDY", ShaderGraphDataType.Float2);
            AddInput("Sampler", ShaderGraphDataType.SamplerState);
            AddOutput("RGBA", ShaderGraphDataType.Float4);
        }
        public override void GenerateCode(NodeCodegenContext ctx)
        {
            var tex = ctx.GetInput("Tex");
            var uv = ctx.GetInput("UV");
            var sampler = ctx.GetInput("Sampler");
            if (string.IsNullOrEmpty(sampler) || sampler == "0") sampler = "s_LinearWrapSampler";
            var varName = $"sampGrad_{Id.Substring(0, 8)}";
            ctx.Emit($"{ctx.PrecisionPrefix}4 {varName} = {tex}.SampleGrad({sampler}, {uv}, {ctx.GetInput("DDX")}, {ctx.GetInput("DDY")});");
            ctx.SetOutput("RGBA", varName);
        }
    }

    /// <summary>指定级别采样节点 (与 LOD 类似, 但支持 mip 级别)。</summary>
    public class TextureSampleLevelNode : ShaderGraphNode
    {
        public TextureSampleLevelNode()
        {
            NodeType = "TextureSampleLevel"; Title = "Texture Sample Level";
            AddInput("Tex", ShaderGraphDataType.Texture2D, required: true);
            AddInput("UV", ShaderGraphDataType.Float2);
            AddInput("Level", ShaderGraphDataType.Float);
            AddInput("Sampler", ShaderGraphDataType.SamplerState);
            AddOutput("RGBA", ShaderGraphDataType.Float4);
        }
        public override void GenerateCode(NodeCodegenContext ctx)
        {
            var tex = ctx.GetInput("Tex");
            var uv = ctx.GetInput("UV");
            var sampler = ctx.GetInput("Sampler");
            if (string.IsNullOrEmpty(sampler) || sampler == "0") sampler = "s_LinearWrapSampler";
            var varName = $"sampLvl_{Id.Substring(0, 8)}";
            ctx.Emit($"{ctx.PrecisionPrefix}4 {varName} = {tex}.SampleLevel({sampler}, {uv}, {ctx.GetInput("Level")});");
            ctx.SetOutput("RGBA", varName);
        }
    }

    /// <summary>直接读取纹素节点。</summary>
    public class TextureLoadNode : ShaderGraphNode
    {
        public TextureLoadNode()
        {
            NodeType = "TextureLoad"; Title = "Texture Load";
            AddInput("Tex", ShaderGraphDataType.Texture2D, required: true);
            AddInput("TexelCoord", ShaderGraphDataType.Float2);
            AddInput("Mip", ShaderGraphDataType.Float);
            AddOutput("RGBA", ShaderGraphDataType.Float4);
        }
        public override void GenerateCode(NodeCodegenContext ctx)
        {
            var tex = ctx.GetInput("Tex");
            var varName = $"load_{Id.Substring(0, 8)}";
            ctx.Emit($"{ctx.PrecisionPrefix}4 {varName} = {tex}.Load(int3((int2){ctx.GetInput("TexelCoord")}, (int){ctx.GetInput("Mip")}));");
            ctx.SetOutput("RGBA", varName);
        }
    }

    /// <summary>写入纹素节点 (仅 Compute)。</summary>
    public class TextureStoreNode : ShaderGraphNode
    {
        public TextureStoreNode()
        {
            NodeType = "TextureStore"; Title = "Texture Store";
            AddInput("Tex", ShaderGraphDataType.Texture2D, required: true);
            AddInput("TexelCoord", ShaderGraphDataType.Float2);
            AddInput("Value", ShaderGraphDataType.Float4);
            AddOutput("Success", ShaderGraphDataType.Bool);
        }
        public override void GenerateCode(NodeCodegenContext ctx)
        {
            var tex = ctx.GetInput("Tex");
            ctx.Emit($"{tex}[{(int)0}/*dispatch*/] = {ctx.GetInput("Value")}; // TextureStore via UAV");
            ctx.SetOutput("Success", "true");
        }
    }

    /// <summary>Gather 采样节点 (4 个相邻纹素)。</summary>
    public class TextureGatherNode : ShaderGraphNode
    {
        public TextureGatherNode()
        {
            NodeType = "TextureGather"; Title = "Texture Gather";
            AddInput("Tex", ShaderGraphDataType.Texture2D, required: true);
            AddInput("UV", ShaderGraphDataType.Float2);
            AddInput("Sampler", ShaderGraphDataType.SamplerState);
            AddOutput("Result", ShaderGraphDataType.Float4);
        }
        public override void GenerateCode(NodeCodegenContext ctx)
        {
            var tex = ctx.GetInput("Tex");
            var uv = ctx.GetInput("UV");
            var sampler = ctx.GetInput("Sampler");
            if (string.IsNullOrEmpty(sampler) || sampler == "0") sampler = "s_LinearWrapSampler";
            var varName = $"gather_{Id.Substring(0, 8)}";
            ctx.Emit($"{ctx.PrecisionPrefix}4 {varName} = {tex}.Gather({sampler}, {uv});");
            ctx.SetOutput("Result", varName);
        }
    }

    /// <summary>纹理尺寸节点。</summary>
    public class TextureSizeNode : ShaderGraphNode
    {
        public TextureSizeNode()
        {
            NodeType = "TextureSize"; Title = "Texture Size";
            AddInput("Tex", ShaderGraphDataType.Texture2D, required: true);
            AddInput("Mip", ShaderGraphDataType.Float);
            AddOutput("Width", ShaderGraphDataType.Float);
            AddOutput("Height", ShaderGraphDataType.Float);
        }
        public override void GenerateCode(NodeCodegenContext ctx)
        {
            var tex = ctx.GetInput("Tex");
            var varName = $"size_{Id.Substring(0, 8)}";
            ctx.Emit($"uint2 {varName}; {tex}.GetDimensions((uint){ctx.GetInput("Mip")}, {varName}.x, {varName}.y);");
            ctx.SetOutput("Width", $"(float){varName}.x");
            ctx.SetOutput("Height", $"(float){varName}.y");
        }
    }

    /// <summary>纹素尺寸节点 (1/width, 1/height)。</summary>
    public class TexelSizeNode : ShaderGraphNode
    {
        public TexelSizeNode()
        {
            NodeType = "TexelSize"; Title = "Texel Size";
            AddInput("Tex", ShaderGraphDataType.Texture2D, required: true);
            AddOutput("TexelSize", ShaderGraphDataType.Float2);
        }
        public override void GenerateCode(NodeCodegenContext ctx)
        {
            var tex = ctx.GetInput("Tex");
            var varName = $"texel_{Id.Substring(0, 8)}";
            ctx.Emit($"uint2 _dim; {tex}.GetDimensions(0, _dim.x, _dim.y);");
            ctx.Emit($"{ctx.PrecisionPrefix}2 {varName} = 1.0 / float2(_dim.x, _dim.y);");
            ctx.SetOutput("TexelSize", varName);
        }
    }

    /// <summary>MipMap 数量节点。</summary>
    public class MipMapCountNode : ShaderGraphNode
    {
        public MipMapCountNode()
        {
            NodeType = "MipMapCount"; Title = "MipMap Count";
            AddInput("Tex", ShaderGraphDataType.Texture2D, required: true);
            AddOutput("MipCount", ShaderGraphDataType.Float);
        }
        public override void GenerateCode(NodeCodegenContext ctx)
        {
            var tex = ctx.GetInput("Tex");
            var varName = $"mip_{Id.Substring(0, 8)}";
            ctx.Emit($"uint _w, _h, _levels; {tex}.GetDimensions(0, _w, _h, _levels);");
            ctx.Emit($"{ctx.PrecisionPrefix} {varName} = (float)_levels;");
            ctx.SetOutput("MipCount", varName);
        }
    }

    /// <summary>三平面映射采样节点。</summary>
    public class TriplanarMappingNode : ShaderGraphNode
    {
        public TriplanarMappingNode()
        {
            NodeType = "TriplanarMapping"; Title = "Triplanar Mapping";
            AddInput("Tex", ShaderGraphDataType.Texture2D, required: true);
            AddInput("Sampler", ShaderGraphDataType.SamplerState);
            AddInput("Scale", ShaderGraphDataType.Float, new Vector4(1, 1, 1, 1));
            AddInput("BlendPower", ShaderGraphDataType.Float, new Vector4(8, 8, 8, 1));
            AddOutput("RGBA", ShaderGraphDataType.Float4);
        }
        public override void GenerateCode(NodeCodegenContext ctx)
        {
            var tex = ctx.GetInput("Tex");
            var sampler = ctx.GetInput("Sampler");
            if (string.IsNullOrEmpty(sampler) || sampler == "0") sampler = "s_LinearWrapSampler";
            var scale = ctx.GetInput("Scale");
            var blend = ctx.GetInput("BlendPower");
            var varName = $"tri_{Id.Substring(0, 8)}";
            ctx.Emit($"{ctx.PrecisionPrefix}4 {varName} = TriplanarSample({tex}, {sampler}, IN.WorldPosition, IN.WorldNormal, {scale}, {blend});");
            ctx.SetOutput("RGBA", varName);
        }
    }

    /// <summary>纹理数组采样节点。</summary>
    public class TextureArraySampleNode : ShaderGraphNode
    {
        public TextureArraySampleNode()
        {
            NodeType = "TextureArraySample"; Title = "Texture Array Sample";
            AddInput("Tex", ShaderGraphDataType.Texture2DArray, required: true);
            AddInput("UV", ShaderGraphDataType.Float2);
            AddInput("Index", ShaderGraphDataType.Float);
            AddInput("Sampler", ShaderGraphDataType.SamplerState);
            AddOutput("RGBA", ShaderGraphDataType.Float4);
        }
        public override void GenerateCode(NodeCodegenContext ctx)
        {
            var tex = ctx.GetInput("Tex");
            var uv = ctx.GetInput("UV");
            var idx = ctx.GetInput("Index");
            var sampler = ctx.GetInput("Sampler");
            if (string.IsNullOrEmpty(sampler) || sampler == "0") sampler = "s_LinearWrapSampler";
            var varName = $"arrSamp_{Id.Substring(0, 8)}";
            ctx.Emit($"{ctx.PrecisionPrefix}4 {varName} = {tex}.Sample({sampler}, float3({uv}, {idx}));");
            ctx.SetOutput("RGBA", varName);
        }
    }

    // =========================================================================
    // E. 程序化/噪声节点
    // =========================================================================

    public class PerlinNoiseNode : ShaderGraphNode
    {
        public PerlinNoiseNode()
        {
            NodeType = "PerlinNoise"; Title = "Perlin Noise";
            AddInput("UV", ShaderGraphDataType.Float2);
            AddInput("Scale", ShaderGraphDataType.Float, new Vector4(1, 1, 1, 1));
            AddOutput("Result", ShaderGraphDataType.Float);
        }
        public override void GenerateCode(NodeCodegenContext ctx) =>
            ctx.SetOutput("Result", $"PerlinNoise2D({ctx.GetInput("UV")} * {ctx.GetInput("Scale")})");
    }

    public class SimplexNoiseNode : ShaderGraphNode
    {
        public SimplexNoiseNode()
        {
            NodeType = "SimplexNoise"; Title = "Simplex Noise";
            AddInput("UV", ShaderGraphDataType.Float2);
            AddInput("Scale", ShaderGraphDataType.Float, new Vector4(1, 1, 1, 1));
            AddOutput("Result", ShaderGraphDataType.Float);
        }
        public override void GenerateCode(NodeCodegenContext ctx) =>
            ctx.SetOutput("Result", $"SimplexNoise2D({ctx.GetInput("UV")} * {ctx.GetInput("Scale")})");
    }

    public class ValueNoiseNode : ShaderGraphNode
    {
        public ValueNoiseNode()
        {
            NodeType = "ValueNoise"; Title = "Value Noise";
            AddInput("UV", ShaderGraphDataType.Float2);
            AddInput("Scale", ShaderGraphDataType.Float, new Vector4(1, 1, 1, 1));
            AddOutput("Result", ShaderGraphDataType.Float);
        }
        public override void GenerateCode(NodeCodegenContext ctx) =>
            ctx.SetOutput("Result", $"ValueNoise2D({ctx.GetInput("UV")} * {ctx.GetInput("Scale")})");
    }

    public class WorleyNoiseNode : ShaderGraphNode
    {
        public WorleyNoiseNode()
        {
            NodeType = "WorleyNoise"; Title = "Worley Noise";
            AddInput("UV", ShaderGraphDataType.Float2);
            AddInput("Scale", ShaderGraphDataType.Float, new Vector4(5, 5, 5, 1));
            AddOutput("Result", ShaderGraphDataType.Float);
        }
        public override void GenerateCode(NodeCodegenContext ctx) =>
            ctx.SetOutput("Result", $"WorleyNoise2D({ctx.GetInput("UV")} * {ctx.GetInput("Scale")})");
    }

    public class CellularNoiseNode : ShaderGraphNode
    {
        public CellularNoiseNode()
        {
            NodeType = "CellularNoise"; Title = "Cellular Noise";
            AddInput("UV", ShaderGraphDataType.Float2);
            AddInput("Scale", ShaderGraphDataType.Float, new Vector4(5, 5, 5, 1));
            AddOutput("F1", ShaderGraphDataType.Float);
            AddOutput("F2", ShaderGraphDataType.Float);
        }
        public override void GenerateCode(NodeCodegenContext ctx)
        {
            var varName = $"cell_{Id.Substring(0, 8)}";
            ctx.Emit($"{ctx.PrecisionPrefix}2 {varName} = CellularNoise2D({ctx.GetInput("UV")} * {ctx.GetInput("Scale")});");
            ctx.SetOutput("F1", $"{varName}.x");
            ctx.SetOutput("F2", $"{varName}.y");
        }
    }

    /// <summary>Voronoi 节点。</summary>
    public class VoronoiNode : ShaderGraphNode
    {
        public VoronoiNode()
        {
            NodeType = "Voronoi"; Title = "Voronoi";
            AddInput("UV", ShaderGraphDataType.Float2);
            AddInput("Scale", ShaderGraphDataType.Float, new Vector4(5, 5, 5, 1));
            AddOutput("Distance", ShaderGraphDataType.Float);
            AddOutput("CellID", ShaderGraphDataType.Float2);
        }
        public override void GenerateCode(NodeCodegenContext ctx)
        {
            var varName = $"voronoi_{Id.Substring(0, 8)}";
            ctx.Emit($"VoronoiResult {varName} = Voronoi({ctx.GetInput("UV")} * {ctx.GetInput("Scale")});");
            ctx.SetOutput("Distance", $"{varName}.Distance");
            ctx.SetOutput("CellID", $"{varName}.CellID");
        }
    }

    /// <summary>渐变节点 (线性/径向/角度/菱形)。</summary>
    public class GradientNode : ShaderGraphNode
    {
        public GradientNode()
        {
            NodeType = "Gradient"; Title = "Gradient";
            AddInput("UV", ShaderGraphDataType.Float2);
            AddOutput("Result", ShaderGraphDataType.Float);
            SetStringMeta("GradientType", "Linear");
        }
        public override void GenerateCode(NodeCodegenContext ctx)
        {
            var type = GetStringMeta("GradientType", "Linear");
            var uv = ctx.GetInput("UV");
            var expr = type switch
            {
                "Linear" => uv + ".x",
                "Radial" => $"length({uv} - 0.5) * 2.0",
                "Angle" => $"atan2({uv}.y - 0.5, {uv}.x - 0.5) / 6.2831853 + 0.5",
                "Diamond" => $"abs({uv}.x - 0.5) + abs({uv}.y - 0.5)",
                _ => uv + ".x"
            };
            ctx.SetOutput("Result", $"saturate({expr})");
        }
    }

    /// <summary>棋盘格节点。</summary>
    public class CheckerboardNode : ShaderGraphNode
    {
        public CheckerboardNode()
        {
            NodeType = "Checkerboard"; Title = "Checkerboard";
            AddInput("UV", ShaderGraphDataType.Float2);
            AddInput("Scale", ShaderGraphDataType.Float, new Vector4(8, 8, 8, 1));
            AddInput("ColorA", ShaderGraphDataType.Float3, new Vector4(0, 0, 0, 1));
            AddInput("ColorB", ShaderGraphDataType.Float3, new Vector4(1, 1, 1, 1));
            AddOutput("Result", ShaderGraphDataType.Float3);
        }
        public override void GenerateCode(NodeCodegenContext ctx)
        {
            var uv = ctx.GetInput("UV");
            var scale = ctx.GetInput("Scale");
            ctx.Emit($"float2 _chk = floor({uv} * {scale});");
            ctx.SetOutput("Result", $"lerp({ctx.GetInput("ColorA")}, {ctx.GetInput("ColorB")}, fmod(_chk.x + _chk.y, 2.0))");
        }
    }

    /// <summary>条纹节点。</summary>
    public class StripeNode : ShaderGraphNode
    {
        public StripeNode()
        {
            NodeType = "Stripe"; Title = "Stripe";
            AddInput("UV", ShaderGraphDataType.Float2);
            AddInput("Scale", ShaderGraphDataType.Float, new Vector4(10, 10, 10, 1));
            AddInput("Width", ShaderGraphDataType.Float, new Vector4(0.5f, 0.5f, 0.5f, 1));
            AddOutput("Result", ShaderGraphDataType.Float);
        }
        public override void GenerateCode(NodeCodegenContext ctx) =>
            ctx.SetOutput("Result", $"step({ctx.GetInput("Width")}, frac({ctx.GetInput("UV")}.x * {ctx.GetInput("Scale")}))");
    }

    /// <summary>网格节点。</summary>
    public class GridNode : ShaderGraphNode
    {
        public GridNode()
        {
            NodeType = "Grid"; Title = "Grid";
            AddInput("UV", ShaderGraphDataType.Float2);
            AddInput("Scale", ShaderGraphDataType.Float, new Vector4(10, 10, 10, 1));
            AddInput("LineWidth", ShaderGraphDataType.Float, new Vector4(0.05f, 0.05f, 0.05f, 1));
            AddOutput("Result", ShaderGraphDataType.Float);
        }
        public override void GenerateCode(NodeCodegenContext ctx)
        {
            var uv = ctx.GetInput("UV");
            var scale = ctx.GetInput("Scale");
            var lw = ctx.GetInput("LineWidth");
            ctx.SetOutput("Result",
                $"(step(1.0 - {lw}, frac({uv}.x * {scale})) + step(1.0 - {lw}, frac({uv}.y * {scale})))");
        }
    }

    /// <summary>砖块节点。</summary>
    public class BrickNode : ShaderGraphNode
    {
        public BrickNode()
        {
            NodeType = "Brick"; Title = "Brick";
            AddInput("UV", ShaderGraphDataType.Float2);
            AddInput("Scale", ShaderGraphDataType.Float2, new Vector4(5, 10, 0, 1));
            AddOutput("Result", ShaderGraphDataType.Float);
        }
        public override void GenerateCode(NodeCodegenContext ctx)
        {
            var uv = ctx.GetInput("UV");
            var scale = ctx.GetInput("Scale");
            ctx.Emit($"float2 _bv = {uv} * {scale};");
            ctx.Emit($"_bv.y += (fmod(floor(_bv.x), 2.0) * 0.5);");
            ctx.SetOutput("Result", "fmod(floor(_bv.x) + floor(_bv.y), 2.0)");
        }
    }

    /// <summary>六边形节点。</summary>
    public class HexagonNode : ShaderGraphNode
    {
        public HexagonNode()
        {
            NodeType = "Hexagon"; Title = "Hexagon";
            AddInput("UV", ShaderGraphDataType.Float2);
            AddInput("Scale", ShaderGraphDataType.Float, new Vector4(5, 5, 5, 1));
            AddOutput("Result", ShaderGraphDataType.Float);
        }
        public override void GenerateCode(NodeCodegenContext ctx) =>
            ctx.SetOutput("Result", $"HexagonalNoise({ctx.GetInput("UV")} * {ctx.GetInput("Scale")})");
    }

    /// <summary>圆形节点。</summary>
    public class CircleNode : ShaderGraphNode
    {
        public CircleNode()
        {
            NodeType = "Circle"; Title = "Circle";
            AddInput("UV", ShaderGraphDataType.Float2);
            AddInput("Radius", ShaderGraphDataType.Float, new Vector4(0.5f, 0.5f, 0.5f, 1));
            AddOutput("Result", ShaderGraphDataType.Float);
        }
        public override void GenerateCode(NodeCodegenContext ctx) =>
            ctx.SetOutput("Result", $"smoothstep({ctx.GetInput("Radius")} + 0.01, {ctx.GetInput("Radius")} - 0.01, length({ctx.GetInput("UV")} - 0.5))");
    }

    /// <summary>分形布朗运动节点。</summary>
    public class FbmNode : ShaderGraphNode
    {
        public FbmNode()
        {
            NodeType = "Fbm"; Title = "FBM";
            AddInput("UV", ShaderGraphDataType.Float2);
            AddInput("Scale", ShaderGraphDataType.Float, new Vector4(1, 1, 1, 1));
            AddInput("Octaves", ShaderGraphDataType.Float, new Vector4(5, 5, 5, 1));
            AddInput("Lacunarity", ShaderGraphDataType.Float, new Vector4(2, 2, 2, 1));
            AddInput("Gain", ShaderGraphDataType.Float, new Vector4(0.5f, 0.5f, 0.5f, 1));
            AddOutput("Result", ShaderGraphDataType.Float);
        }
        public override void GenerateCode(NodeCodegenContext ctx) =>
            ctx.SetOutput("Result",
                $"fbm({ctx.GetInput("UV")} * {ctx.GetInput("Scale")}, (int){ctx.GetInput("Octaves")}, {ctx.GetInput("Lacunarity")}, {ctx.GetInput("Gain")})");
    }

    /// <summary>侵蚀效果节点。</summary>
    public class ErosionNode : ShaderGraphNode
    {
        public ErosionNode()
        {
            NodeType = "Erosion"; Title = "Erosion";
            AddInput("UV", ShaderGraphDataType.Float2);
            AddInput("Scale", ShaderGraphDataType.Float, new Vector4(1, 1, 1, 1));
            AddInput("Iterations", ShaderGraphDataType.Float, new Vector4(5, 5, 5, 1));
            AddOutput("Result", ShaderGraphDataType.Float);
        }
        public override void GenerateCode(NodeCodegenContext ctx) =>
            ctx.SetOutput("Result",
                $"Erosion({ctx.GetInput("UV")} * {ctx.GetInput("Scale")}, (int){ctx.GetInput("Iterations")})");
    }

    /// <summary>Curl 噪声节点。</summary>
    public class CurlNoiseNode : ShaderGraphNode
    {
        public CurlNoiseNode()
        {
            NodeType = "CurlNoise"; Title = "Curl Noise";
            AddInput("UV", ShaderGraphDataType.Float2);
            AddInput("Scale", ShaderGraphDataType.Float, new Vector4(1, 1, 1, 1));
            AddOutput("Result", ShaderGraphDataType.Float2);
        }
        public override void GenerateCode(NodeCodegenContext ctx) =>
            ctx.SetOutput("Result", $"CurlNoise2D({ctx.GetInput("UV")} * {ctx.GetInput("Scale")})");
    }

    // =========================================================================
    // F. 颜色节点
    // =========================================================================

    public class ColorToGrayscaleNode : UnaryMathNode
    {
        public ColorToGrayscaleNode()
        {
            NodeType = "ColorToGrayscale"; Title = "Color To Grayscale";
            ResultType = ShaderGraphDataType.Float;
            InputPins[0].DataType = ShaderGraphDataType.Float3;
        }
        protected override string Op(string x) => $"dot({x}, float3(0.299, 0.587, 0.114))";
    }

    public class GrayscaleToColorNode : UnaryMathNode
    {
        public GrayscaleToColorNode()
        {
            NodeType = "GrayscaleToColor"; Title = "Grayscale To Color";
            ResultType = ShaderGraphDataType.Float3;
        }
        protected override string Op(string x) => $"{x}.xxx";
    }

    public class HSVToRGBNode : UnaryMathNode
    {
        public HSVToRGBNode()
        {
            NodeType = "HSVToRGB"; Title = "HSV To RGB";
            ResultType = ShaderGraphDataType.Float3;
            InputPins[0].DataType = ShaderGraphDataType.Float3;
        }
        protected override string Op(string x) => $"HSVtoRGB({x})";
    }

    public class RGBToHSVNode : UnaryMathNode
    {
        public RGBToHSVNode()
        {
            NodeType = "RGBToHSV"; Title = "RGB To HSV";
            ResultType = ShaderGraphDataType.Float3;
            InputPins[0].DataType = ShaderGraphDataType.Float3;
        }
        protected override string Op(string x) => $"RGBtoHSV({x})";
    }

    /// <summary>颜色混合节点 (多种混合模式)。</summary>
    public class BlendNode : ShaderGraphNode
    {
        public BlendNode()
        {
            NodeType = "Blend"; Title = "Blend";
            AddInput("Base", ShaderGraphDataType.Float3);
            AddInput("Blend", ShaderGraphDataType.Float3);
            AddInput("Opacity", ShaderGraphDataType.Float);
            AddOutput("Result", ShaderGraphDataType.Float3);
            SetStringMeta("BlendMode", "Normal");
        }
        public override void GenerateCode(NodeCodegenContext ctx)
        {
            var mode = GetStringMeta("BlendMode", "Normal");
            var a = ctx.GetInput("Base");
            var b = ctx.GetInput("Blend");
            var op = ctx.GetInput("Opacity");
            var expr = mode switch
            {
                "Normal" => $"lerp({a}, {b}, {op})",
                "Multiply" => $"lerp({a}, {a} * {b}, {op})",
                "Screen" => $"lerp({a}, 1.0 - (1.0 - {a}) * (1.0 - {b}), {op})",
                "Overlay" => $"OverlayBlend({a}, {b}, {op})",
                "SoftLight" => $"SoftLightBlend({a}, {b}, {op})",
                "HardLight" => $"HardLightBlend({a}, {b}, {op})",
                "Dodge" => $"lerp({a}, {a} / (1.0 - {b}), {op})",
                "Burn" => $"lerp({a}, 1.0 - (1.0 - {a}) / {b}, {op})",
                "Difference" => $"lerp({a}, abs({a} - {b}), {op})",
                "Exclusion" => $"lerp({a}, {a} + {b} - 2.0 * {a} * {b}, {op})",
                "Subtract" => $"lerp({a}, {a} - {b}, {op})",
                "Divide" => $"lerp({a}, {a} / {b}, {op})",
                _ => $"lerp({a}, {b}, {op})"
            };
            ctx.SetOutput("Result", expr);
        }
    }

    /// <summary>色彩校正节点。</summary>
    public class ColorCorrectionNode : ShaderGraphNode
    {
        public ColorCorrectionNode()
        {
            NodeType = "ColorCorrection"; Title = "Color Correction";
            AddInput("Color", ShaderGraphDataType.Float3);
            AddInput("Brightness", ShaderGraphDataType.Float, new Vector4(1, 1, 1, 1));
            AddInput("Contrast", ShaderGraphDataType.Float, new Vector4(1, 1, 1, 1));
            AddInput("Saturation", ShaderGraphDataType.Float, new Vector4(1, 1, 1, 1));
            AddInput("HueShift", ShaderGraphDataType.Float);
            AddInput("Temperature", ShaderGraphDataType.Float);
            AddInput("Tint", ShaderGraphDataType.Float);
            AddOutput("Result", ShaderGraphDataType.Float3);
        }
        public override void GenerateCode(NodeCodegenContext ctx)
        {
            var c = ctx.GetInput("Color");
            var varName = $"cc_{Id.Substring(0, 8)}";
            ctx.Emit($"{ctx.PrecisionPrefix}3 {varName} = {c};");
            ctx.Emit($"{varName} *= {ctx.GetInput("Brightness")};");
            ctx.Emit($"{varName} = lerp(dot({varName}, float3(0.299, 0.587, 0.114)), {varName}, {ctx.GetInput("Saturation")});");
            ctx.Emit($"{varName} = ({varName} - 0.5) * {ctx.GetInput("Contrast")} + 0.5;");
            ctx.Emit($"{varName} = HSVtoRGB(RGBtoHSV({varName}) + float3({ctx.GetInput("HueShift")}, 0, 0));");
            ctx.Emit($"{varName}.r += {ctx.GetInput("Temperature")} * 0.1; {varName}.b -= {ctx.GetInput("Temperature")} * 0.1;");
            ctx.Emit($"{varName}.g += {ctx.GetInput("Tint")} * 0.1;");
            ctx.SetOutput("Result", varName);
        }
    }

    /// <summary>通道混合节点。</summary>
    public class ChannelMixerNode : ShaderGraphNode
    {
        public ChannelMixerNode()
        {
            NodeType = "ChannelMixer"; Title = "Channel Mixer";
            AddInput("In", ShaderGraphDataType.Float3);
            AddInput("Red", ShaderGraphDataType.Float3, new Vector4(1, 0, 0, 1));
            AddInput("Green", ShaderGraphDataType.Float3, new Vector4(0, 1, 0, 1));
            AddInput("Blue", ShaderGraphDataType.Float3, new Vector4(0, 0, 1, 1));
            AddOutput("Result", ShaderGraphDataType.Float3);
        }
        public override void GenerateCode(NodeCodegenContext ctx)
        {
            var v = ctx.GetInput("In");
            ctx.SetOutput("Result",
                $"float3(dot({v}, {ctx.GetInput("Red")}), dot({v}, {ctx.GetInput("Green")}), dot({v}, {ctx.GetInput("Blue")}))");
        }
    }

    public class InvertColorsNode : UnaryMathNode
    {
        public InvertColorsNode()
        {
            NodeType = "InvertColors"; Title = "Invert Colors";
            ResultType = ShaderGraphDataType.Float3;
            InputPins[0].DataType = ShaderGraphDataType.Float3;
        }
        protected override string Op(string x) => $"(1.0 - {x})";
    }

    /// <summary>色调分离节点。</summary>
    public class PosterizeNode : ShaderGraphNode
    {
        public PosterizeNode()
        {
            NodeType = "Posterize"; Title = "Posterize";
            AddInput("Color", ShaderGraphDataType.Float3);
            AddInput("Steps", ShaderGraphDataType.Float, new Vector4(4, 4, 4, 1));
            AddOutput("Result", ShaderGraphDataType.Float3);
        }
        public override void GenerateCode(NodeCodegenContext ctx) =>
            ctx.SetOutput("Result", $"floor({ctx.GetInput("Color")} * {ctx.GetInput("Steps")}) / {ctx.GetInput("Steps")}");
    }

    /// <summary>着色节点。</summary>
    public class TintNode : ShaderGraphNode
    {
        public TintNode()
        {
            NodeType = "Tint"; Title = "Tint";
            AddInput("Color", ShaderGraphDataType.Float3);
            AddInput("Tint", ShaderGraphDataType.Float3, new Vector4(1, 1, 1, 1));
            AddOutput("Result", ShaderGraphDataType.Float3);
        }
        public override void GenerateCode(NodeCodegenContext ctx) =>
            ctx.SetOutput("Result", $"{ctx.GetInput("Color")} * {ctx.GetInput("Tint")}");
    }

    /// <summary>替换颜色节点。</summary>
    public class ReplaceColorNode : ShaderGraphNode
    {
        public ReplaceColorNode()
        {
            NodeType = "ReplaceColor"; Title = "Replace Color";
            AddInput("In", ShaderGraphDataType.Float3);
            AddInput("From", ShaderGraphDataType.Float3);
            AddInput("To", ShaderGraphDataType.Float3);
            AddInput("Range", ShaderGraphDataType.Float, new Vector4(0.1f, 0.1f, 0.1f, 1));
            AddOutput("Result", ShaderGraphDataType.Float3);
        }
        public override void GenerateCode(NodeCodegenContext ctx)
        {
            var v = ctx.GetInput("In");
            var from = ctx.GetInput("From");
            var to = ctx.GetInput("To");
            var range = ctx.GetInput("Range");
            ctx.SetOutput("Result", $"lerp({to}, {v}, smoothstep({range}, {range} * 2.0, distance({v}, {from})))");
        }
    }

    public class SRGBToLinearNode : UnaryMathNode
    {
        public SRGBToLinearNode()
        {
            NodeType = "SRGBToLinear"; Title = "sRGB To Linear";
            ResultType = ShaderGraphDataType.Float3;
            InputPins[0].DataType = ShaderGraphDataType.Float3;
        }
        protected override string Op(string x) => $"SRGBToLinear({x})";
    }

    public class LinearToSRGBNode : UnaryMathNode
    {
        public LinearToSRGBNode()
        {
            NodeType = "LinearToSRGB"; Title = "Linear To sRGB";
            ResultType = ShaderGraphDataType.Float3;
            InputPins[0].DataType = ShaderGraphDataType.Float3;
        }
        protected override string Op(string x) => $"LinearToSRGB({x})";
    }

    /// <summary>ACES 色调映射节点。</summary>
    public class ACESNode : UnaryMathNode
    {
        public ACESNode()
        {
            NodeType = "ACES"; Title = "ACES Tonemap";
            ResultType = ShaderGraphDataType.Float3;
            InputPins[0].DataType = ShaderGraphDataType.Float3;
        }
        protected override string Op(string x) => $"ACES_Tonemap({x})";
    }

    /// <summary>色调映射节点。</summary>
    public class TonemapNode : ShaderGraphNode
    {
        public TonemapNode()
        {
            NodeType = "Tonemap"; Title = "Tonemap";
            AddInput("Color", ShaderGraphDataType.Float3);
            AddInput("Exposure", ShaderGraphDataType.Float, new Vector4(1, 1, 1, 1));
            AddOutput("Result", ShaderGraphDataType.Float3);
            SetStringMeta("Operator", "Reinhard");
        }
        public override void GenerateCode(NodeCodegenContext ctx)
        {
            var c = ctx.GetInput("Color");
            var exp = ctx.GetInput("Exposure");
            var op = GetStringMeta("Operator", "Reinhard");
            var scaled = $"({c} * {exp})";
            var expr = op switch
            {
                "Reinhard" => $"{scaled} / (1.0 + {scaled})",
                "Reinhard2" => $"{scaled} / (1.0 + dot({scaled}, float3(0.2126, 0.7152, 0.0722)))",
                "ACES" => $"ACES_Tonemap({scaled})",
                "Uncharted" => $"Uncharted2Tonemap({scaled})",
                _ => $"{scaled} / (1.0 + {scaled})"
            };
            ctx.SetOutput("Result", expr);
        }
    }

    // =========================================================================
    // G. 光照/PBR 节点
    // =========================================================================

    public class AmbientLightNode : ShaderGraphNode
    {
        public AmbientLightNode()
        {
            NodeType = "AmbientLight"; Title = "Ambient Light";
            AddOutput("Color", ShaderGraphDataType.Float3);
        }
        public override void GenerateCode(NodeCodegenContext ctx) =>
            ctx.SetOutput("Color", "g_AmbientColor");
    }

    public class DirectionalLightNode : ShaderGraphNode
    {
        public DirectionalLightNode()
        {
            NodeType = "DirectionalLight"; Title = "Directional Light";
            AddOutput("Direction", ShaderGraphDataType.Float3);
            AddOutput("Color", ShaderGraphDataType.Float3);
        }
        public override void GenerateCode(NodeCodegenContext ctx)
        {
            ctx.SetOutput("Direction", "g_DirLight.Direction");
            ctx.SetOutput("Color", "g_DirLight.Color");
        }
    }

    public class PointLightNode : ShaderGraphNode
    {
        public PointLightNode()
        {
            NodeType = "PointLight"; Title = "Point Light";
            AddInput("Index", ShaderGraphDataType.Int);
            AddOutput("Color", ShaderGraphDataType.Float3);
            AddOutput("Direction", ShaderGraphDataType.Float3);
            AddOutput("Attenuation", ShaderGraphDataType.Float);
        }
        public override void GenerateCode(NodeCodegenContext ctx)
        {
            var idx = ctx.GetInput("Index");
            ctx.Emit($"PointLight _pl = g_PointLights[(int){idx}];");
            ctx.Emit($"float3 _toLight = _pl.Position - IN.WorldPosition;");
            ctx.Emit($"float _dist = length(_toLight);");
            ctx.Emit($"float _atten = 1.0 / (_dist * _dist + 1.0);");
            ctx.SetOutput("Color", "_pl.Color");
            ctx.SetOutput("Direction", "(_toLight / _dist)");
            ctx.SetOutput("Attenuation", "_atten");
        }
    }

    public class SpotLightNode : ShaderGraphNode
    {
        public SpotLightNode()
        {
            NodeType = "SpotLight"; Title = "Spot Light";
            AddInput("Index", ShaderGraphDataType.Int);
            AddOutput("Color", ShaderGraphDataType.Float3);
            AddOutput("Attenuation", ShaderGraphDataType.Float);
        }
        public override void GenerateCode(NodeCodegenContext ctx)
        {
            var idx = ctx.GetInput("Index");
            ctx.Emit($"SpotLight _sl = g_SpotLights[(int){idx}];");
            ctx.SetOutput("Color", "_sl.Color");
            ctx.SetOutput("Attenuation", "SpotLightAttenuation(_sl, IN.WorldPosition)");
        }
    }

    /// <summary>菲涅尔节点。</summary>
    public class FresnelNode : ShaderGraphNode
    {
        public FresnelNode()
        {
            NodeType = "Fresnel"; Title = "Fresnel";
            AddInput("Normal", ShaderGraphDataType.Float3);
            AddInput("ViewDir", ShaderGraphDataType.Float3);
            AddInput("Power", ShaderGraphDataType.Float, new Vector4(5, 5, 5, 1));
            AddOutput("Result", ShaderGraphDataType.Float);
        }
        public override void GenerateCode(NodeCodegenContext ctx) =>
            ctx.SetOutput("Result", $"pow(1.0 - saturate(dot(normalize({ctx.GetInput("Normal")}), normalize({ctx.GetInput("ViewDir")}))), {ctx.GetInput("Power")})");
    }

    /// <summary>菲涅尔效果节点 (带基础反射率)。</summary>
    public class FresnelEffectNode : ShaderGraphNode
    {
        public FresnelEffectNode()
        {
            NodeType = "FresnelEffect"; Title = "Fresnel Effect";
            AddInput("Normal", ShaderGraphDataType.Float3);
            AddInput("ViewDir", ShaderGraphDataType.Float3);
            AddInput("BaseReflectance", ShaderGraphDataType.Float, new Vector4(0.04f, 0.04f, 0.04f, 1));
            AddInput("Roughness", ShaderGraphDataType.Float, new Vector4(0.5f, 0.5f, 0.5f, 1));
            AddOutput("Result", ShaderGraphDataType.Float);
        }
        public override void GenerateCode(NodeCodegenContext ctx) =>
            ctx.SetOutput("Result",
                $"FresnelSchlick(max(dot(normalize({ctx.GetInput("Normal")}), normalize({ctx.GetInput("ViewDir")})), 0.0), {ctx.GetInput("BaseReflectance")}, {ctx.GetInput("Roughness")})");
    }

    public class ReflectionProbeNode : ShaderGraphNode
    {
        public ReflectionProbeNode()
        {
            NodeType = "ReflectionProbe"; Title = "Reflection Probe";
            AddInput("ReflectionVector", ShaderGraphDataType.Float3);
            AddInput("Roughness", ShaderGraphDataType.Float);
            AddOutput("Color", ShaderGraphDataType.Float3);
        }
        public override void GenerateCode(NodeCodegenContext ctx) =>
            ctx.SetOutput("Color", $"SampleReflectionProbe({ctx.GetInput("ReflectionVector")}, {ctx.GetInput("Roughness")})");
    }

    public class PlanarReflectionNode : ShaderGraphNode
    {
        public PlanarReflectionNode()
        {
            NodeType = "PlanarReflection"; Title = "Planar Reflection";
            AddInput("UV", ShaderGraphDataType.Float2);
            AddOutput("Color", ShaderGraphDataType.Float3);
        }
        public override void GenerateCode(NodeCodegenContext ctx) =>
            ctx.SetOutput("Color", "g_PlanarReflectionTexture.Sample(s_LinearClampSampler, " + ctx.GetInput("UV") + ").rgb");
    }

    public class ReflectionVectorNode : ShaderGraphNode
    {
        public ReflectionVectorNode()
        {
            NodeType = "ReflectionVector"; Title = "Reflection Vector";
            AddInput("Normal", ShaderGraphDataType.Float3);
            AddInput("ViewDir", ShaderGraphDataType.Float3);
            AddOutput("Result", ShaderGraphDataType.Float3);
        }
        public override void GenerateCode(NodeCodegenContext ctx) =>
            ctx.SetOutput("Result", $"reflect(-normalize({ctx.GetInput("ViewDir")}), normalize({ctx.GetInput("Normal")}))");
    }

    public class RefractionVectorNode : ShaderGraphNode
    {
        public RefractionVectorNode()
        {
            NodeType = "RefractionVector"; Title = "Refraction Vector";
            AddInput("Normal", ShaderGraphDataType.Float3);
            AddInput("ViewDir", ShaderGraphDataType.Float3);
            AddInput("IOR", ShaderGraphDataType.Float, new Vector4(1.33f, 1.33f, 1.33f, 1));
            AddOutput("Result", ShaderGraphDataType.Float3);
        }
        public override void GenerateCode(NodeCodegenContext ctx) =>
            ctx.SetOutput("Result", $"refract(-normalize({ctx.GetInput("ViewDir")}), normalize({ctx.GetInput("Normal")}), 1.0 / {ctx.GetInput("IOR")})");
    }

    /// <summary>PBR 主节点 (Albedo/Metallic/Roughness/Normal/AO/Emission/Alpha)。</summary>
    public class PBRMasterNode : ShaderGraphNode
    {
        public PBRMasterNode()
        {
            NodeType = "PBRMaster"; Title = "PBR Master";
            AddInput("Albedo", ShaderGraphDataType.Float3, new Vector4(1, 1, 1, 1));
            AddInput("Metallic", ShaderGraphDataType.Float);
            AddInput("Roughness", ShaderGraphDataType.Float, new Vector4(0.5f, 0.5f, 0.5f, 1));
            AddInput("Normal", ShaderGraphDataType.Float3, new Vector4(0, 0, 1, 1));
            AddInput("AO", ShaderGraphDataType.Float, new Vector4(1, 1, 1, 1));
            AddInput("Emission", ShaderGraphDataType.Float3, new Vector4(0, 0, 0, 1));
            AddInput("Alpha", ShaderGraphDataType.Float, new Vector4(1, 1, 1, 1));
            AddInput("WorldPositionOffset", ShaderGraphDataType.Float3);
        }
        public override bool IsMasterNode => true;
        public override void GenerateCode(NodeCodegenContext ctx)
        {
            ctx.EmitDeclaration("// PBR Master Node outputs");
            ctx.Emit($"OUT.Albedo = {ctx.GetInput("Albedo")};");
            ctx.Emit($"OUT.Metallic = {ctx.GetInput("Metallic")};");
            ctx.Emit($"OUT.Roughness = {ctx.GetInput("Roughness")};");
            ctx.Emit($"OUT.Normal = normalize({ctx.GetInput("Normal")});");
            ctx.Emit($"OUT.AO = {ctx.GetInput("AO")};");
            ctx.Emit($"OUT.Emission = {ctx.GetInput("Emission")};");
            ctx.Emit($"OUT.Alpha = {ctx.GetInput("Alpha")};");
            ctx.Emit($"OUT.WorldPositionOffset = {ctx.GetInput("WorldPositionOffset")};");
        }
    }

    public class SubsurfaceScatteringNode : ShaderGraphNode
    {
        public SubsurfaceScatteringNode()
        {
            NodeType = "SubsurfaceScattering"; Title = "Subsurface Scattering";
            AddInput("Color", ShaderGraphDataType.Float3);
            AddInput("Thickness", ShaderGraphDataType.Float, new Vector4(0.5f, 0.5f, 0.5f, 1));
            AddInput("Scale", ShaderGraphDataType.Float, new Vector4(1, 1, 1, 1));
            AddOutput("Result", ShaderGraphDataType.Float3);
        }
        public override void GenerateCode(NodeCodegenContext ctx) =>
            ctx.SetOutput("Result", $"SubsurfaceScattering({ctx.GetInput("Color")}, {ctx.GetInput("Thickness")}, {ctx.GetInput("Scale")}, IN.WorldNormal, normalize(g_CameraWorldPos - IN.WorldPosition))");
    }

    public class SheenNode : ShaderGraphNode
    {
        public SheenNode()
        {
            NodeType = "Sheen"; Title = "Sheen";
            AddInput("SheenColor", ShaderGraphDataType.Float3, new Vector4(1, 1, 1, 1));
            AddInput("SheenRoughness", ShaderGraphDataType.Float, new Vector4(0.5f, 0.5f, 0.5f, 1));
            AddOutput("Result", ShaderGraphDataType.Float3);
        }
        public override void GenerateCode(NodeCodegenContext ctx) =>
            ctx.SetOutput("Result", $"SheenBRDF({ctx.GetInput("SheenColor")}, {ctx.GetInput("SheenRoughness")}, IN.WorldNormal, normalize(g_CameraWorldPos - IN.WorldPosition))");
    }

    public class ClearCoatNode : ShaderGraphNode
    {
        public ClearCoatNode()
        {
            NodeType = "ClearCoat"; Title = "Clear Coat";
            AddInput("CoatColor", ShaderGraphDataType.Float3, new Vector4(1, 1, 1, 1));
            AddInput("CoatRoughness", ShaderGraphDataType.Float, new Vector4(0.1f, 0.1f, 0.1f, 1));
            AddInput("CoatStrength", ShaderGraphDataType.Float, new Vector4(1, 1, 1, 1));
            AddOutput("Result", ShaderGraphDataType.Float3);
        }
        public override void GenerateCode(NodeCodegenContext ctx) =>
            ctx.SetOutput("Result", $"ClearCoatBRDF({ctx.GetInput("CoatColor")}, {ctx.GetInput("CoatRoughness")}, {ctx.GetInput("CoatStrength")}, IN.WorldNormal, normalize(g_CameraWorldPos - IN.WorldPosition))");
    }

    public class AnisotropyNode : ShaderGraphNode
    {
        public AnisotropyNode()
        {
            NodeType = "Anisotropy"; Title = "Anisotropy";
            AddInput("Anisotropy", ShaderGraphDataType.Float);
            AddInput("Tangent", ShaderGraphDataType.Float3);
            AddOutput("Result", ShaderGraphDataType.Float3);
        }
        public override void GenerateCode(NodeCodegenContext ctx) =>
            ctx.SetOutput("Result", $"AnisotropicBRDF({ctx.GetInput("Anisotropy")}, normalize({ctx.GetInput("Tangent")}), IN.WorldNormal, normalize(g_CameraWorldPos - IN.WorldPosition))");
    }

    public class IridescenceNode : ShaderGraphNode
    {
        public IridescenceNode()
        {
            NodeType = "Iridescence"; Title = "Iridescence";
            AddInput("Thickness", ShaderGraphDataType.Float, new Vector4(300, 300, 300, 1));
            AddInput("ViewDir", ShaderGraphDataType.Float3);
            AddInput("Normal", ShaderGraphDataType.Float3);
            AddOutput("Result", ShaderGraphDataType.Float3);
        }
        public override void GenerateCode(NodeCodegenContext ctx) =>
            ctx.SetOutput("Result", $"Iridescence({ctx.GetInput("Thickness")}, normalize({ctx.GetInput("ViewDir")}), normalize({ctx.GetInput("Normal")}))");
    }

    public class AmbientOcclusionNode : ShaderGraphNode
    {
        public AmbientOcclusionNode()
        {
            NodeType = "AmbientOcclusion"; Title = "Ambient Occlusion";
            AddInput("UV", ShaderGraphDataType.Float2);
            AddOutput("AO", ShaderGraphDataType.Float);
        }
        public override void GenerateCode(NodeCodegenContext ctx) =>
            ctx.SetOutput("AO", "g_AOTexture.Sample(s_LinearClampSampler, " + ctx.GetInput("UV") + ").r");
    }

    // =========================================================================
    // H. UV/几何 节点
    // =========================================================================

    /// <summary>UV 变换节点 (平移/旋转/缩放)。</summary>
    public class UVTransformNode : ShaderGraphNode
    {
        public UVTransformNode()
        {
            NodeType = "UVTransform"; Title = "UV Transform";
            AddInput("UV", ShaderGraphDataType.Float2);
            AddInput("Tiling", ShaderGraphDataType.Float2, new Vector4(1, 1, 0, 1));
            AddInput("Offset", ShaderGraphDataType.Float2);
            AddInput("Rotation", ShaderGraphDataType.Float);
            AddInput("Pivot", ShaderGraphDataType.Float2, new Vector4(0.5f, 0.5f, 0, 1));
            AddOutput("Result", ShaderGraphDataType.Float2);
        }
        public override void GenerateCode(NodeCodegenContext ctx)
        {
            var uv = ctx.GetInput("UV");
            var tiling = ctx.GetInput("Tiling");
            var offset = ctx.GetInput("Offset");
            var rot = ctx.GetInput("Rotation");
            var pivot = ctx.GetInput("Pivot");
            ctx.Emit($"float2 _uv = ({uv} - {pivot}) * {tiling};");
            ctx.Emit($"float s = sin({rot}); float c = cos({rot});");
            ctx.Emit($"_uv = float2(_uv.x * c - _uv.y * s, _uv.x * s + _uv.y * c) + {pivot} + {offset};");
            ctx.SetOutput("Result", "_uv");
        }
    }

    /// <summary>UV 分块节点。</summary>
    public class UVTileNode : ShaderGraphNode
    {
        public UVTileNode()
        {
            NodeType = "UVTile"; Title = "UV Tile";
            AddInput("UV", ShaderGraphDataType.Float2);
            AddInput("TilesX", ShaderGraphDataType.Float, new Vector4(4, 4, 4, 1));
            AddInput("TilesY", ShaderGraphDataType.Float, new Vector4(4, 4, 4, 1));
            AddInput("TileIndex", ShaderGraphDataType.Float);
            AddOutput("Result", ShaderGraphDataType.Float2);
        }
        public override void GenerateCode(NodeCodegenContext ctx)
        {
            var uv = ctx.GetInput("UV");
            var tx = ctx.GetInput("TilesX");
            var ty = ctx.GetInput("TilesY");
            var idx = ctx.GetInput("TileIndex");
            ctx.Emit($"uint _ti = (uint){idx};");
            ctx.Emit($"uint _cols = (uint){tx};");
            ctx.Emit($"float2 _tile = float2(_ti % _cols, _ti / _cols);");
            ctx.SetOutput("Result", $"(({uv} + _tile) / float2({tx}, {ty}))");
        }
    }

    /// <summary>视差映射节点。</summary>
    public class ParallaxMappingNode : ShaderGraphNode
    {
        public ParallaxMappingNode()
        {
            NodeType = "ParallaxMapping"; Title = "Parallax Mapping";
            AddInput("UV", ShaderGraphDataType.Float2);
            AddInput("Height", ShaderGraphDataType.Float);
            AddInput("Amplitude", ShaderGraphDataType.Float, new Vector4(0.1f, 0.1f, 0.1f, 1));
            AddOutput("Result", ShaderGraphDataType.Float2);
        }
        public override void GenerateCode(NodeCodegenContext ctx)
        {
            var uv = ctx.GetInput("UV");
            var h = ctx.GetInput("Height");
            var amp = ctx.GetInput("Amplitude");
            ctx.Emit($"float3 _viewDirTS = float3(normalize(g_CameraWorldPos - IN.WorldPosition).xy, 1.0);");
            ctx.SetOutput("Result", $"({uv} - (_viewDirTS.xy * ({h} * {amp})))");
        }
    }

    /// <summary>视差遮蔽映射节点。</summary>
    public class ParallaxOcclusionMappingNode : ShaderGraphNode
    {
        public ParallaxOcclusionMappingNode()
        {
            NodeType = "ParallaxOcclusionMapping"; Title = "Parallax Occlusion Mapping";
            AddInput("UV", ShaderGraphDataType.Float2);
            AddInput("HeightTex", ShaderGraphDataType.Texture2D);
            AddInput("Amplitude", ShaderGraphDataType.Float, new Vector4(0.1f, 0.1f, 0.1f, 1));
            AddInput("Steps", ShaderGraphDataType.Float, new Vector4(10, 10, 10, 1));
            AddOutput("Result", ShaderGraphDataType.Float2);
        }
        public override void GenerateCode(NodeCodegenContext ctx)
        {
            var uv = ctx.GetInput("UV");
            var tex = ctx.GetInput("HeightTex");
            var amp = ctx.GetInput("Amplitude");
            var steps = ctx.GetInput("Steps");
            ctx.SetOutput("Result", $"ParallaxOcclusionMapping({tex}, s_LinearWrapSampler, {uv}, {amp}, (int){steps}, IN.WorldNormal)");
        }
    }

    public class TangentToLocalNode : ShaderGraphNode
    {
        public TangentToLocalNode()
        {
            NodeType = "TangentToLocal"; Title = "Tangent To Local";
            AddInput("Vector", ShaderGraphDataType.Float3);
            AddOutput("Result", ShaderGraphDataType.Float3);
        }
        public override void GenerateCode(NodeCodegenContext ctx) =>
            ctx.SetOutput("Result", $"mul(transpose(float3x3(IN.WorldTangent, IN.WorldBitangent, IN.WorldNormal)), {ctx.GetInput("Vector")})");
    }

    public class TangentToWorldNode : ShaderGraphNode
    {
        public TangentToWorldNode()
        {
            NodeType = "TangentToWorld"; Title = "Tangent To World";
            AddInput("Vector", ShaderGraphDataType.Float3);
            AddOutput("Result", ShaderGraphDataType.Float3);
        }
        public override void GenerateCode(NodeCodegenContext ctx) =>
            ctx.SetOutput("Result", $"mul(float3x3(IN.WorldTangent, IN.WorldBitangent, IN.WorldNormal), {ctx.GetInput("Vector")})");
    }

    /// <summary>深度淡入节点。</summary>
    public class DepthFadeNode : ShaderGraphNode
    {
        public DepthFadeNode()
        {
            NodeType = "DepthFade"; Title = "Depth Fade";
            AddInput("FadeDistance", ShaderGraphDataType.Float, new Vector4(0.1f, 0.1f, 0.1f, 1));
            AddOutput("Opacity", ShaderGraphDataType.Float);
        }
        public override void GenerateCode(NodeCodegenContext ctx)
        {
            var dist = ctx.GetInput("FadeDistance");
            ctx.Emit($"float _sceneDepth = g_CameraDepthTexture.Sample(s_LinearClampSampler, IN.ScreenPosition.xy / IN.ScreenPosition.w).r;");
            ctx.Emit($"float _pixelDepth = IN.ScreenPosition.z;");
            ctx.SetOutput("Opacity", $"saturate((_sceneDepth - _pixelDepth) / {dist})");
        }
    }

    /// <summary>距离剔除节点。</summary>
    public class DistanceCullNode : ShaderGraphNode
    {
        public DistanceCullNode()
        {
            NodeType = "DistanceCull"; Title = "Distance Cull";
            AddInput("CullDistance", ShaderGraphDataType.Float, new Vector4(100, 100, 100, 1));
            AddOutput("Visible", ShaderGraphDataType.Bool);
        }
        public override void GenerateCode(NodeCodegenContext ctx) =>
            ctx.SetOutput("Visible", $"(length(g_CameraWorldPos - IN.WorldPosition) < {ctx.GetInput("CullDistance")})");
    }

    /// <summary>世界对齐纹理节点。</summary>
    public class WorldAlignedTextureNode : ShaderGraphNode
    {
        public WorldAlignedTextureNode()
        {
            NodeType = "WorldAlignedTexture"; Title = "World Aligned Texture";
            AddInput("Tex", ShaderGraphDataType.Texture2D);
            AddInput("Scale", ShaderGraphDataType.Float3, new Vector4(1, 1, 1, 1));
            AddOutput("RGBA", ShaderGraphDataType.Float4);
        }
        public override void GenerateCode(NodeCodegenContext ctx)
        {
            var tex = ctx.GetInput("Tex");
            var scale = ctx.GetInput("Scale");
            var varName = $"wa_{Id.Substring(0, 8)}";
            ctx.Emit($"{ctx.PrecisionPrefix}4 {varName} = {tex}.Sample(s_LinearWrapSampler, IN.WorldPosition.xyz * {scale} * 0.1);");
            ctx.SetOutput("RGBA", varName);
        }
    }

    /// <summary>世界对齐法线节点。</summary>
    public class WorldAlignedNormalNode : ShaderGraphNode
    {
        public WorldAlignedNormalNode()
        {
            NodeType = "WorldAlignedNormal"; Title = "World Aligned Normal";
            AddInput("Tex", ShaderGraphDataType.Texture2D);
            AddOutput("Normal", ShaderGraphDataType.Float3);
        }
        public override void GenerateCode(NodeCodegenContext ctx)
        {
            var tex = ctx.GetInput("Tex");
            var varName = $"wan_{Id.Substring(0, 8)}";
            ctx.Emit($"{ctx.PrecisionPrefix}3 {varName} = {tex}.Sample(s_LinearWrapSampler, IN.WorldPosition.xz * 0.1).xyz * 2.0 - 1.0;");
            ctx.SetOutput("Normal", varName);
        }
    }

    /// <summary>基于位置的节点。</summary>
    public class PositionBasedNode : ShaderGraphNode
    {
        public PositionBasedNode()
        {
            NodeType = "PositionBased"; Title = "Position Based";
            AddInput("Position", ShaderGraphDataType.Float3);
            AddInput("Axis", ShaderGraphDataType.Float3, new Vector4(0, 1, 0, 1));
            AddOutput("Result", ShaderGraphDataType.Float);
        }
        public override void GenerateCode(NodeCodegenContext ctx) =>
            ctx.SetOutput("Result", $"dot({ctx.GetInput("Position")}, normalize({ctx.GetInput("Axis")}))");
    }

    // =========================================================================
    // I. 逻辑/分支 节点
    // =========================================================================

    /// <summary>分支节点 (if/else)。</summary>
    public class BranchNode : ShaderGraphNode
    {
        public BranchNode()
        {
            NodeType = "Branch"; Title = "Branch";
            AddInput("Condition", ShaderGraphDataType.Bool);
            AddInput("True", ShaderGraphDataType.Float);
            AddInput("False", ShaderGraphDataType.Float);
            AddOutput("Result", ShaderGraphDataType.Float);
        }
        public override void GenerateCode(NodeCodegenContext ctx) =>
            ctx.SetOutput("Result", $"({ctx.GetInput("Condition")} ? {ctx.GetInput("True")} : {ctx.GetInput("False")})");
    }

    /// <summary>开关节点 (on Int/Bool/Enum)。</summary>
    public class SwitchNode : ShaderGraphNode
    {
        public SwitchNode()
        {
            NodeType = "Switch"; Title = "Switch";
            AddInput("Index", ShaderGraphDataType.Int);
            AddInput("Case0", ShaderGraphDataType.Float);
            AddInput("Case1", ShaderGraphDataType.Float);
            AddInput("Case2", ShaderGraphDataType.Float);
            AddInput("Case3", ShaderGraphDataType.Float);
            AddOutput("Result", ShaderGraphDataType.Float);
        }
        public override void GenerateCode(NodeCodegenContext ctx)
        {
            var idx = ctx.GetInput("Index");
            var varName = $"sw_{Id.Substring(0, 8)}";
            ctx.Emit($"{ctx.PrecisionPrefix} {varName};");
            ctx.Emit($"switch ((int){idx}) {{");
            ctx.Emit($"  case 0: {varName} = {ctx.GetInput("Case0")}; break;");
            ctx.Emit($"  case 1: {varName} = {ctx.GetInput("Case1")}; break;");
            ctx.Emit($"  case 2: {varName} = {ctx.GetInput("Case2")}; break;");
            ctx.Emit($"  case 3: {varName} = {ctx.GetInput("Case3")}; break;");
            ctx.Emit($"  default: {varName} = {ctx.GetInput("Case0")}; break;");
            ctx.Emit($"}}");
            ctx.SetOutput("Result", varName);
        }
    }

    /// <summary>比较节点。</summary>
    public class CompareNode : ShaderGraphNode
    {
        public CompareNode()
        {
            NodeType = "Compare"; Title = "Compare";
            AddInput("A", ShaderGraphDataType.Float);
            AddInput("B", ShaderGraphDataType.Float);
            AddOutput("Result", ShaderGraphDataType.Bool);
            SetStringMeta("Operator", "Equal");
        }
        public override void GenerateCode(NodeCodegenContext ctx)
        {
            var op = GetStringMeta("Operator", "Equal");
            var a = ctx.GetInput("A");
            var b = ctx.GetInput("B");
            var expr = op switch
            {
                "Equal" => $"({a} == {b})",
                "NotEqual" => $"({a} != {b})",
                "Greater" => $"({a} > {b})",
                "Less" => $"({a} < {b})",
                "GreaterEqual" => $"({a} >= {b})",
                "LessEqual" => $"({a} <= {b})",
                _ => $"({a} == {b})"
            };
            ctx.SetOutput("Result", expr);
        }
    }

    public class AllNode : UnaryMathNode
    {
        public AllNode()
        {
            NodeType = "All"; Title = "All";
            ResultType = ShaderGraphDataType.Bool;
            InputPins[0].DataType = ShaderGraphDataType.Float4;
        }
        protected override string Op(string x) => $"all({x})";
    }

    public class AnyNode : UnaryMathNode
    {
        public AnyNode()
        {
            NodeType = "Any"; Title = "Any";
            ResultType = ShaderGraphDataType.Bool;
            InputPins[0].DataType = ShaderGraphDataType.Float4;
        }
        protected override string Op(string x) => $"any({x})";
    }

    public class NotNode : UnaryMathNode
    {
        public NotNode()
        {
            NodeType = "Not"; Title = "Not";
            ResultType = ShaderGraphDataType.Bool;
            InputPins[0].DataType = ShaderGraphDataType.Bool;
        }
        protected override string Op(string x) => $"(!{x})";
    }

    public class AndNode : BinaryMathNode
    {
        public AndNode()
        {
            NodeType = "And"; Title = "And";
            ResultType = ShaderGraphDataType.Bool;
            InputPins[0].DataType = ShaderGraphDataType.Bool;
            InputPins[1].DataType = ShaderGraphDataType.Bool;
        }
        protected override string Op(string a, string b) => $"({a} && {b})";
    }

    public class OrNode : BinaryMathNode
    {
        public OrNode()
        {
            NodeType = "Or"; Title = "Or";
            ResultType = ShaderGraphDataType.Bool;
            InputPins[0].DataType = ShaderGraphDataType.Bool;
            InputPins[1].DataType = ShaderGraphDataType.Bool;
        }
        protected override string Op(string a, string b) => $"({a} || {b})";
    }

    /// <summary>三元运算节点。</summary>
    public class TernaryNode : ShaderGraphNode
    {
        public TernaryNode()
        {
            NodeType = "Ternary"; Title = "Ternary";
            AddInput("Condition", ShaderGraphDataType.Bool);
            AddInput("True", ShaderGraphDataType.Float);
            AddInput("False", ShaderGraphDataType.Float);
            AddOutput("Result", ShaderGraphDataType.Float);
        }
        public override void GenerateCode(NodeCodegenContext ctx) =>
            ctx.SetOutput("Result", $"({ctx.GetInput("Condition")} ? {ctx.GetInput("True")} : {ctx.GetInput("False")})");
    }

    // =========================================================================
    // J. 工具 节点
    // =========================================================================

    /// <summary>预览节点。</summary>
    public class PreviewNode : ShaderGraphNode
    {
        public PreviewNode()
        {
            NodeType = "Preview"; Title = "Preview";
            AddInput("In", ShaderGraphDataType.Float4);
            AddOutput("Out", ShaderGraphDataType.Float4);
        }
        public override void GenerateCode(NodeCodegenContext ctx) =>
            ctx.SetOutput("Out", ctx.GetInput("In"));
        public override NodePreviewData ComputePreview()
        {
            return NodePreviewData.FromColor(GetVectorMeta("PreviewValue", new Vector4(0, 0, 0, 1)));
        }
    }

    /// <summary>注释节点 (不参与编译)。</summary>
    public class CommentNode : ShaderGraphNode
    {
        public CommentNode()
        {
            NodeType = "Comment"; Title = "Comment";
            SetStringMeta("CommentText", "Comment");
            Size = new Vector2(300, 200);
        }
        public override void GenerateCode(NodeCodegenContext ctx)
        {
            // 注释节点不生成代码
        }
    }

    /// <summary>子图调用节点。</summary>
    public class SubGraphNode : ShaderGraphNode
    {
        public string SubGraphGuid { get; set; } = string.Empty;
        public ShaderSubGraph? SubGraph { get; set; }

        public SubGraphNode()
        {
            NodeType = "SubGraph"; Title = "Sub Graph";
            AddOutput("Result", ShaderGraphDataType.Float);
        }
        public void Configure(ShaderSubGraph sub)
        {
            SubGraph = sub;
            SubGraphGuid = sub.Guid;
            Title = sub.Name;
            // 根据子图参数创建输入引脚
            InputPins.Clear();
            foreach (var p in sub.Parameters)
            {
                AddInput(p.Name, p.DataType, p.DefaultValue);
            }
            // 根据子图输出创建输出引脚
            OutputPins.Clear();
            foreach (var o in sub.Outputs)
            {
                AddOutput(o.Name, o.DataType);
            }
        }
        public override void GenerateCode(NodeCodegenContext ctx)
        {
            // 子图内联: 生成调用, 参数为输入表达式
            if (SubGraph == null || SubGraph.Graph == null)
            {
                ctx.SetOutput("Result", "0");
                return;
            }
            var args = new System.Text.StringBuilder();
            foreach (var p in SubGraph.Parameters)
            {
                if (args.Length > 0) args.Append(", ");
                args.Append(ctx.GetInput(p.Name));
            }
            var funcName = $"SubGraph_{SubGraph.Name}";
            ctx.EmitDeclaration($"// SubGraph: {SubGraph.Name}");
            foreach (var o in SubGraph.Outputs)
            {
                var varName = $"sub_{Id.Substring(0, 8)}_{o.Name}";
                ctx.Emit($"{ShaderGraphPin.DataTypeToHlsl(o.DataType)} {varName} = {funcName}({args});");
                ctx.SetOutput(o.Name, varName);
            }
        }
    }

    /// <summary>自定义函数节点 (自定义 HLSL 代码)。</summary>
    public class CustomFunctionNode : ShaderGraphNode
    {
        public CustomFunctionNode()
        {
            NodeType = "CustomFunction"; Title = "Custom Function";
            AddInput("Input", ShaderGraphDataType.Float);
            AddOutput("Result", ShaderGraphDataType.Float);
            SetStringMeta("FunctionName", "CustomFunc");
            SetStringMeta("Code", "// 在此输入 HLSL 代码");
        }
        public override void GenerateCode(NodeCodegenContext ctx)
        {
            var funcName = GetStringMeta("FunctionName", "CustomFunc");
            var code = GetStringMeta("Code", "");
            // 发出函数声明
            ctx.EmitDeclaration($"// Custom Function: {funcName}");
            ctx.EmitDeclaration(code);
            // 生成调用
            var args = new System.Text.StringBuilder();
            foreach (var pin in InputPins)
            {
                if (args.Length > 0) args.Append(", ");
                args.Append(ctx.GetInput(pin.Name));
            }
            ctx.SetOutput(OutputPins[0].Name, $"{funcName}({args})");
        }
    }

    /// <summary>内联代码节点。</summary>
    public class CodeNode : ShaderGraphNode
    {
        public CodeNode()
        {
            NodeType = "Code"; Title = "Code";
            AddInput("In", ShaderGraphDataType.Float);
            AddOutput("Out", ShaderGraphDataType.Float);
            SetStringMeta("Code", "// 在此输入 HLSL 表达式, 使用 {In} 引用输入");
        }
        public override void GenerateCode(NodeCodegenContext ctx)
        {
            var code = GetStringMeta("Code", "0");
            // 替换 {PinName} 占位符为输入表达式
            foreach (var pin in InputPins)
            {
                code = code.Replace("{" + pin.Name + "}", ctx.GetInput(pin.Name));
            }
            ctx.SetOutput("Out", code);
        }
    }

    /// <summary>函数声明节点。</summary>
    public class FunctionNode : ShaderGraphNode
    {
        public FunctionNode()
        {
            NodeType = "Function"; Title = "Function";
            AddInput("Input", ShaderGraphDataType.Float);
            AddOutput("Result", ShaderGraphDataType.Float);
            SetStringMeta("FunctionName", "MyFunc");
            SetStringMeta("Body", "return Input;");
        }
        public override void GenerateCode(NodeCodegenContext ctx)
        {
            var funcName = GetStringMeta("FunctionName", "MyFunc");
            var body = GetStringMeta("Body", "return Input;");
            var inputType = ShaderGraphPin.DataTypeToHlsl(InputPins[0].DataType);
            var outputType = ShaderGraphPin.DataTypeToHlsl(OutputPins[0].DataType);
            var sig = $"{outputType} {funcName}({inputType} Input)";
            ctx.EmitDeclaration($"{sig} {{ {body} }}");
            ctx.SetOutput("Result", $"{funcName}({ctx.GetInput("Input")})");
        }
    }

    /// <summary>循环节点。</summary>
    public class LoopNode : ShaderGraphNode
    {
        public LoopNode()
        {
            NodeType = "Loop"; Title = "Loop";
            AddInput("Iterations", ShaderGraphDataType.Int, new Vector4(8, 8, 8, 1));
            AddInput("InitialValue", ShaderGraphDataType.Float);
            AddInput("Body", ShaderGraphDataType.Float);
            AddOutput("Result", ShaderGraphDataType.Float);
        }
        public override void GenerateCode(NodeCodegenContext ctx)
        {
            var iter = ctx.GetInput("Iterations");
            var init = ctx.GetInput("InitialValue");
            var body = ctx.GetInput("Body");
            var varName = $"loop_{Id.Substring(0, 8)}";
            ctx.Emit($"{ctx.PrecisionPrefix} {varName} = {init};");
            ctx.Emit($"for (int _i = 0; _i < (int){iter}; _i++) {{");
            ctx.Emit($"  {varName} = {varName} + {body};");
            ctx.Emit($"}}");
            ctx.SetOutput("Result", varName);
        }
    }

    /// <summary>数组节点。</summary>
    public class ArrayNode : ShaderGraphNode
    {
        public ArrayNode()
        {
            NodeType = "Array"; Title = "Array";
            AddInput("Index", ShaderGraphDataType.Int);
            AddOutput("Result", ShaderGraphDataType.Float);
            SetIntMeta("Size", 4);
        }
        public override void GenerateCode(NodeCodegenContext ctx)
        {
            var idx = ctx.GetInput("Index");
            var arrName = $"arr_{Id.Substring(0, 8)}";
            var size = GetIntMeta("Size", 4);
            ctx.EmitDeclaration($"{ctx.PrecisionPrefix} {arrName}[{size}] = {{0}};");
            ctx.SetOutput("Result", $"{arrName}[(int){idx}]");
        }
    }

    /// <summary>获取变量节点。</summary>
    public class GetVarNode : ShaderGraphNode
    {
        public GetVarNode()
        {
            NodeType = "GetVar"; Title = "Get Variable";
            AddOutput("Value", ShaderGraphDataType.Float);
            SetStringMeta("VarName", "MyVar");
        }
        public override void GenerateCode(NodeCodegenContext ctx)
        {
            var name = GetStringMeta("VarName", "MyVar");
            ctx.SetOutput("Value", name);
        }
    }

    /// <summary>设置变量节点。</summary>
    public class SetVarNode : ShaderGraphNode
    {
        public SetVarNode()
        {
            NodeType = "SetVar"; Title = "Set Variable";
            AddInput("Value", ShaderGraphDataType.Float);
            AddOutput("Out", ShaderGraphDataType.Float);
            SetStringMeta("VarName", "MyVar");
        }
        public override void GenerateCode(NodeCodegenContext ctx)
        {
            var name = GetStringMeta("VarName", "MyVar");
            var val = ctx.GetInput("Value");
            ctx.Emit($"{ctx.PrecisionPrefix} {name} = {val};");
            ctx.SetOutput("Out", name);
        }
    }

    // =========================================================================
    // 节点注册表
    // =========================================================================

    /// <summary>
    /// 着色器节点注册表。维护所有可用节点类型的工厂方法, 供编辑器创建节点。
    /// </summary>
    public static class ShaderNodeRegistry
    {
        private static readonly Dictionary<string, Func<ShaderGraphNode>> _factories =
            new Dictionary<string, Func<ShaderGraphNode>>(StringComparer.OrdinalIgnoreCase);

        private static readonly Dictionary<string, string> _categories =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        static ShaderNodeRegistry()
        {
            Register();
        }

        /// <summary>注册所有内置节点。</summary>
        private static void Register()
        {
            // 输入节点
            Reg<PositionNode>("Input");
            Reg<NormalNode>("Input");
            Reg<TangentNode>("Input");
            Reg<BitangentNode>("Input");
            Reg<UVNode>("Input");
            Reg<ColorNode>("Input");
            Reg<TimeNode>("Input");
            Reg<ViewDirectionNode>("Input");
            Reg<CameraPositionNode>("Input");
            Reg<ScreenPositionNode>("Input");
            Reg<ScreenUVNode>("Input");
            Reg<FragCoordNode>("Input");
            Reg<InstanceIDNode>("Input");
            Reg<VertexIDNode>("Input");
            Reg<CameraDepthTextureNode>("Input");
            Reg<CameraColorTextureNode>("Input");
            Reg<PropertyNode>("Input");
            Reg<Texture2DNode>("Input");
            Reg<TextureCubeNode>("Input");
            Reg<Texture2DArrayNode>("Input");
            Reg<SamplerStateNode>("Input");

            // 数学节点
            Reg<AddNode>("Math");
            Reg<SubtractNode>("Math");
            Reg<MultiplyNode>("Math");
            Reg<DivideNode>("Math");
            Reg<PowerNode>("Math");
            Reg<SquareRootNode>("Math");
            Reg<LogNode>("Math");
            Reg<ExpNode>("Math");
            Reg<AbsNode>("Math");
            Reg<FloorNode>("Math");
            Reg<CeilingNode>("Math");
            Reg<RoundNode>("Math");
            Reg<TruncateNode>("Math");
            Reg<FractionNode>("Math");
            Reg<SineNode>("Math");
            Reg<CosineNode>("Math");
            Reg<TanNode>("Math");
            Reg<ArcSineNode>("Math");
            Reg<ArcCosineNode>("Math");
            Reg<ArcTangentNode>("Math");
            Reg<ArcTangent2Node>("Math");
            Reg<MinNode>("Math");
            Reg<MaxNode>("Math");
            Reg<ClampNode>("Math");
            Reg<SaturateNode>("Math");
            Reg<LerpNode>("Math");
            Reg<InverseLerpNode>("Math");
            Reg<SmoothstepNode>("Math");
            Reg<StepNode>("Math");
            Reg<ModuloNode>("Math");
            Reg<FmodNode>("Math");
            Reg<FwidthNode>("Math");
            Reg<DDXNode>("Math");
            Reg<DDYNode>("Math");
            Reg<DeterminantNode>("Math");
            Reg<TransposeNode>("Math");
            Reg<InverseMatrixNode>("Math");
            Reg<NormalizeNode>("Math");
            Reg<LengthNode>("Math");
            Reg<DistanceNode>("Math");
            Reg<DotProductNode>("Math");
            Reg<CrossProductNode>("Math");
            Reg<ReflectNode>("Math");
            Reg<RefractNode>("Math");
            Reg<ProjectNode>("Math");
            Reg<UnprojectNode>("Math");
            Reg<FaceForwardNode>("Math");
            Reg<NegateNode>("Math");
            Reg<OneMinusNode>("Math");
            Reg<ReciprocalNode>("Math");
            Reg<RemainderNode>("Math");
            Reg<MapRangeNode>("Math");
            Reg<RandomRangeNode>("Math");
            Reg<NoiseNode>("Math");

            // 向量/矩阵节点
            Reg<MakeVector2Node>("Vector");
            Reg<MakeVector3Node>("Vector");
            Reg<MakeVector4Node>("Vector");
            Reg<MakeMatrixNode>("Vector");
            Reg<BreakVector2Node>("Vector");
            Reg<BreakVector3Node>("Vector");
            Reg<BreakVector4Node>("Vector");
            Reg<BreakMatrixNode>("Vector");
            Reg<SwizzleNode>("Vector");
            Reg<CombineNode>("Vector");
            Reg<SplitNode>("Vector");
            Reg<RotateNode>("Vector");
            Reg<Rotate2DNode>("Vector");
            Reg<Rotate3DNode>("Vector");
            Reg<TransformNode>("Vector");
            Reg<MatrixMultiplyNode>("Vector");
            Reg<ViewMatrixNode>("Vector");
            Reg<ProjectionMatrixNode>("Vector");
            Reg<ViewProjectionMatrixNode>("Vector");
            Reg<WorldMatrixNode>("Vector");
            Reg<InverseViewMatrixNode>("Vector");

            // 纹理/采样节点
            Reg<TextureSampleNode>("Texture");
            Reg<TextureSampleLODNode>("Texture");
            Reg<TextureSampleGradNode>("Texture");
            Reg<TextureSampleLevelNode>("Texture");
            Reg<TextureLoadNode>("Texture");
            Reg<TextureStoreNode>("Texture");
            Reg<TextureGatherNode>("Texture");
            Reg<TextureSizeNode>("Texture");
            Reg<TexelSizeNode>("Texture");
            Reg<MipMapCountNode>("Texture");
            Reg<TriplanarMappingNode>("Texture");
            Reg<TextureArraySampleNode>("Texture");

            // 程序化/噪声节点
            Reg<PerlinNoiseNode>("Procedural");
            Reg<SimplexNoiseNode>("Procedural");
            Reg<ValueNoiseNode>("Procedural");
            Reg<WorleyNoiseNode>("Procedural");
            Reg<CellularNoiseNode>("Procedural");
            Reg<VoronoiNode>("Procedural");
            Reg<GradientNode>("Procedural");
            Reg<CheckerboardNode>("Procedural");
            Reg<StripeNode>("Procedural");
            Reg<GridNode>("Procedural");
            Reg<BrickNode>("Procedural");
            Reg<HexagonNode>("Procedural");
            Reg<CircleNode>("Procedural");
            Reg<FbmNode>("Procedural");
            Reg<ErosionNode>("Procedural");
            Reg<CurlNoiseNode>("Procedural");

            // 颜色节点
            Reg<ColorToGrayscaleNode>("Color");
            Reg<GrayscaleToColorNode>("Color");
            Reg<HSVToRGBNode>("Color");
            Reg<RGBToHSVNode>("Color");
            Reg<BlendNode>("Color");
            Reg<ColorCorrectionNode>("Color");
            Reg<ChannelMixerNode>("Color");
            Reg<InvertColorsNode>("Color");
            Reg<PosterizeNode>("Color");
            Reg<TintNode>("Color");
            Reg<ReplaceColorNode>("Color");
            Reg<SRGBToLinearNode>("Color");
            Reg<LinearToSRGBNode>("Color");
            Reg<ACESNode>("Color");
            Reg<TonemapNode>("Color");

            // 光照/PBR节点
            Reg<AmbientLightNode>("Lighting");
            Reg<DirectionalLightNode>("Lighting");
            Reg<PointLightNode>("Lighting");
            Reg<SpotLightNode>("Lighting");
            Reg<FresnelNode>("Lighting");
            Reg<FresnelEffectNode>("Lighting");
            Reg<ReflectionProbeNode>("Lighting");
            Reg<PlanarReflectionNode>("Lighting");
            Reg<ReflectionVectorNode>("Lighting");
            Reg<RefractionVectorNode>("Lighting");
            Reg<PBRMasterNode>("Lighting");
            Reg<SubsurfaceScatteringNode>("Lighting");
            Reg<SheenNode>("Lighting");
            Reg<ClearCoatNode>("Lighting");
            Reg<AnisotropyNode>("Lighting");
            Reg<IridescenceNode>("Lighting");
            Reg<AmbientOcclusionNode>("Lighting");

            // UV/几何节点
            Reg<UVTransformNode>("UV");
            Reg<UVTileNode>("UV");
            Reg<ParallaxMappingNode>("UV");
            Reg<ParallaxOcclusionMappingNode>("UV");
            Reg<TangentToLocalNode>("UV");
            Reg<TangentToWorldNode>("UV");
            Reg<DepthFadeNode>("UV");
            Reg<DistanceCullNode>("UV");
            Reg<WorldAlignedTextureNode>("UV");
            Reg<WorldAlignedNormalNode>("UV");
            Reg<PositionBasedNode>("UV");

            // 逻辑/分支节点
            Reg<BranchNode>("Logic");
            Reg<SwitchNode>("Logic");
            Reg<CompareNode>("Logic");
            Reg<AllNode>("Logic");
            Reg<AnyNode>("Logic");
            Reg<NotNode>("Logic");
            Reg<AndNode>("Logic");
            Reg<OrNode>("Logic");
            Reg<TernaryNode>("Logic");

            // 工具节点
            Reg<PreviewNode>("Utility");
            Reg<CommentNode>("Utility");
            Reg<SubGraphNode>("Utility");
            Reg<CustomFunctionNode>("Utility");
            Reg<CodeNode>("Utility");
            Reg<FunctionNode>("Utility");
            Reg<LoopNode>("Utility");
            Reg<ArrayNode>("Utility");
            Reg<GetVarNode>("Utility");
            Reg<SetVarNode>("Utility");
        }

        private static void Reg<T>(string category) where T : ShaderGraphNode, new()
        {
            var name = typeof(T).Name;
            _factories[name] = () => new T();
            _categories[name] = category;
        }

        /// <summary>按类型名创建节点实例。</summary>
        public static ShaderGraphNode? CreateNode(string nodeType)
        {
            if (_factories.TryGetValue(nodeType, out var factory))
                return factory();
            return null;
        }

        /// <summary>获取所有已注册节点类型名。</summary>
        public static IEnumerable<string> GetRegisteredTypes() => _factories.Keys;

        /// <summary>获取节点分类。</summary>
        public static string GetCategory(string nodeType) =>
            _categories.TryGetValue(nodeType, out var cat) ? cat : "Other";

        /// <summary>按分类获取节点类型列表。</summary>
        public static IEnumerable<string> GetTypesByCategory(string category) =>
            _categories.Where(kv => kv.Value == category).Select(kv => kv.Key);

        /// <summary>已注册节点总数。</summary>
        public static int Count => _factories.Count;
    }
}
