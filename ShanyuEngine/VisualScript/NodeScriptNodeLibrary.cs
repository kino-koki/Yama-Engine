using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace ShanyuEngine.VisualScript
{
    // ════════════════════════════════════════════════════════════════════════════
    //  山雨引擎 · 蓝图节点库 (NodeScript Node Library)
    //  ────────────────────────────────────────────────────────────────────────────
    //  提供完整的蓝图节点定义和节点注册表，涵盖：
    //    · 数学节点：加减乘除/幂/开方/三角函数/插值/取整等
    //    · 向量节点：加减/缩放/叉乘/点乘/归一化/长度/距离/反射/投影等
    //    · 旋转节点：旋转体构造/轴旋转/组合/Slerp等
    //    · 变换节点：变换构造/拆解/组合/变换坐标/获取分量等
    //    · 逻辑节点：与或非/异或/分支/多路开关/序列/门控等
    //    · 流程节点：事件/函数/循环/延迟/计时器等
    //    · 变量节点：读写变量/成员访问/数组操作等
    //    · 比较节点：等于/不等于/大于/小于/近似相等/字符串比较等
    //    · 类型转换节点：ToFloat/ToInt/ToBool/ToString/ToVector3等
    //    · Actor 节点：生成/销毁/获取属性等
    //    · 输入节点：按键/鼠标/轴值等
    //    · 游戏节点：游戏状态/控制器/伤害/打印等
    //  作者：山雨引擎组
    // ════════════════════════════════════════════════════════════════════════════

    // ─────────────────────────────────────────────────────────────────────────────
    //  蓝图节点注册表
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 蓝图节点分类。
    /// </summary>
    public enum NodeCategoryType
    {
        /// <summary>数学运算。</summary>
        Math,
        /// <summary>向量运算。</summary>
        Vector,
        /// <summary>旋转运算。</summary>
        Rotation,
        /// <summary>变换运算。</summary>
        Transform,
        /// <summary>逻辑运算。</summary>
        Logic,
        /// <summary>流程控制。</summary>
        FlowControl,
        /// <summary>变量操作。</summary>
        Variable,
        /// <summary>比较运算。</summary>
        Comparison,
        /// <summary>类型转换。</summary>
        Cast,
        /// <summary>Actor 操作。</summary>
        Actor,
        /// <summary>输入事件。</summary>
        Input,
        /// <summary>游戏系统。</summary>
        Game,
        /// <summary>事件。</summary>
        Event,
        /// <summary>函数。</summary>
        Function,
        /// <summary>常量。</summary>
        Constants
    }

    /// <summary>
    /// 蓝图节点描述信息（用于编辑器搜索和创建）。
    /// </summary>
    [Serializable]
    public class NodeScriptNodeDescriptor
    {
        /// <summary>节点类型名称（唯一标识）。</summary>
        public string NodeType { get; set; } = string.Empty;

        /// <summary>节点显示标题。</summary>
        public string Title { get; set; } = string.Empty;

        /// <summary>节点分类。</summary>
        public NodeCategoryType Category { get; set; }

        /// <summary>节点子分类路径（如 "Math/Trig"）。</summary>
        public string SubCategory { get; set; } = string.Empty;

        /// <summary>节点关键词（用于搜索）。</summary>
        string[] Keywords { get; set; } = Array.Empty<string>();

        /// <summary>节点描述。</summary>
        public string Description { get; set; } = string.Empty;

        /// <summary>节点颜色。</summary>
        public string ColorHex { get; set; } = "#808080";

        /// <summary>是否为纯节点（无副作用，可内联）。</summary>
        public bool IsPure { get; set; }

        /// <summary>是否为高级节点（默认隐藏）。</summary>
        public bool IsAdvanced { get; set; }

        /// <summary>节点工厂方法。</summary>
        public Func<GraphNode>? Factory { get; set; }
    }

    /// <summary>
    /// 蓝图节点注册表：管理所有可用的蓝图节点类型。
    /// </summary>
    public static class NodeScriptNodeRegistry
    {
        private static readonly Dictionary<string, NodeScriptNodeDescriptor> _nodes = new Dictionary<string, NodeScriptNodeDescriptor>();
        private static bool _initialized;

        /// <summary>获取所有已注册的节点描述符。</summary>
        public static IReadOnlyDictionary<string, NodeScriptNodeDescriptor> AllNodes
        {
            get
            {
                EnsureInitialized();
                return _nodes;
            }
        }

        /// <summary>
        /// 确保注册表已初始化（注册所有内置节点）。
        /// </summary>
        public static void EnsureInitialized()
        {
            if (_initialized) return;
            RegisterBuiltinNodes();
            _initialized = true;
        }

        /// <summary>
        /// 注册自定义节点类型。
        /// </summary>
        public static void RegisterNode(NodeScriptNodeDescriptor descriptor)
        {
            _nodes[descriptor.NodeType] = descriptor;
        }

        /// <summary>
        /// 通过类型名获取节点描述符。
        /// </summary>
        public static NodeScriptNodeDescriptor? GetNodeDescriptor(string nodeType)
        {
            EnsureInitialized();
            return _nodes.TryGetValue(nodeType, out var desc) ? desc : null;
        }

        /// <summary>
        /// 通过工厂方法创建节点实例。
        /// </summary>
        public static GraphNode? CreateNode(string nodeType)
        {
            EnsureInitialized();
            if (_nodes.TryGetValue(nodeType, out var desc) && desc.Factory != null)
            {
                return desc.Factory();
            }
            return null;
        }

        /// <summary>
        /// 搜索节点（按关键词模糊匹配）。
        /// </summary>
        public static List<NodeScriptNodeDescriptor> SearchNodes(string keyword, NodeCategoryType? categoryFilter = null)
        {
            EnsureInitialized();
            var lower = keyword.ToLowerInvariant();
            var results = _nodes.Values.Where(n =>
            {
                if (categoryFilter.HasValue && n.Category != categoryFilter.Value) return false;

                return n.Title.ToLowerInvariant().Contains(lower) ||
                       n.NodeType.ToLowerInvariant().Contains(lower) ||
                       n.SubCategory.ToLowerInvariant().Contains(lower) ||
                       n.Description.ToLowerInvariant().Contains(lower);
            }).ToList();

            return results;
        }

        /// <summary>
        /// 获取指定分类的所有节点。
        /// </summary>
        public static List<NodeScriptNodeDescriptor> GetNodesByCategory(NodeCategoryType category)
        {
            EnsureInitialized();
            return _nodes.Values.Where(n => n.Category == category).ToList();
        }

        /// <summary>
        /// 注册所有内置节点。
        /// </summary>
        private static void RegisterBuiltinNodes()
        {
            RegisterMathNodes();
            RegisterVectorNodes();
            RegisterRotationNodes();
            RegisterTransformNodes();
            RegisterLogicNodes();
            RegisterFlowControlNodes();
            RegisterVariableNodes();
            RegisterComparisonNodes();
            RegisterCastNodes();
            RegisterActorNodes();
            RegisterInputNodes();
            RegisterGameNodes();
        }

        // ─────────────────────────────────────────────────────────────────────────
        //  数学节点注册
        // ─────────────────────────────────────────────────────────────────────────

        private static void RegisterMathNodes()
        {
            string mathColor = "#00CC66";
            string trigSubCat = "Math/Trigonometric";
            string basicSubCat = "Math/Basic";
            string roundSubCat = "Math/Rounding";
            string interpSubCat = "Math/Interpolation";

            // 基本运算
            RegisterPureNode("Add", "Add (+)", NodeCategoryType.Math, basicSubCat, mathColor,
                "两个值相加", n => BinaryMathNode("Add", "+", n));
            RegisterPureNode("Subtract", "Subtract (-)", NodeCategoryType.Math, basicSubCat, mathColor,
                "两个值相减", n => BinaryMathNode("Subtract", "-", n));
            RegisterPureNode("Multiply", "Multiply (*)", NodeCategoryType.Math, basicSubCat, mathColor,
                "两个值相乘", n => BinaryMathNode("Multiply", "*", n));
            RegisterPureNode("Divide", "Divide (/)", NodeCategoryType.Math, basicSubCat, mathColor,
                "两个值相除", n => BinaryMathNode("Divide", "/", n));
            RegisterPureNode("Modulo", "Modulo (%)", NodeCategoryType.Math, basicSubCat, mathColor,
                "取模运算", n => BinaryMathNode("Modulo", "%", n));
            RegisterPureNode("Power", "Power (^)", NodeCategoryType.Math, basicSubCat, mathColor,
                "幂运算", n => BinaryMathNode("Power", "^", n));

            // 单值运算
            RegisterPureNode("Sqrt", "Square Root", NodeCategoryType.Math, basicSubCat, mathColor,
                "平方根", n => UnaryMathNode("Sqrt", "Sqrt", n));
            RegisterPureNode("Abs", "Absolute", NodeCategoryType.Math, basicSubCat, mathColor,
                "绝对值", n => UnaryMathNode("Abs", "Abs", n));
            RegisterPureNode("Negate", "Negate", NodeCategoryType.Math, basicSubCat, mathColor,
                "取负", n => UnaryMathNode("Negate", "-", n));
            RegisterPureNode("Sign", "Sign", NodeCategoryType.Math, basicSubCat, mathColor,
                "符号函数 (-1/0/1)", n => UnaryMathNode("Sign", "Sign", n));

            // 三角函数
            RegisterPureNode("Sin", "Sine", NodeCategoryType.Math, trigSubCat, mathColor,
                "正弦函数", n => UnaryMathNode("Sin", "Math.Sin", n));
            RegisterPureNode("Cos", "Cosine", NodeCategoryType.Math, trigSubCat, mathColor,
                "余弦函数", n => UnaryMathNode("Cos", "Math.Cos", n));
            RegisterPureNode("Tan", "Tangent", NodeCategoryType.Math, trigSubCat, mathColor,
                "正切函数", n => UnaryMathNode("Tan", "Math.Tan", n));
            RegisterPureNode("Atan2", "Atan2", NodeCategoryType.Math, trigSubCat, mathColor,
                "反正切2（根据 Y/X 计算角度）", n => BinaryMathNode("Atan2", "Atan2", n));

            // 取整
            RegisterPureNode("Round", "Round", NodeCategoryType.Math, roundSubCat, mathColor,
                "四舍五入", n => UnaryMathNode("Round", "Round", n));
            RegisterPureNode("Ceil", "Ceiling", NodeCategoryType.Math, roundSubCat, mathColor,
                "向上取整", n => UnaryMathNode("Ceil", "Ceiling", n));
            RegisterPureNode("Floor", "Floor", NodeCategoryType.Math, roundSubCat, mathColor,
                "向下取整", n => UnaryMathNode("Floor", "Floor", n));
            RegisterPureNode("Fract", "Fraction", NodeCategoryType.Math, roundSubCat, mathColor,
                "取小数部分", n => UnaryMathNode("Fract", "Fract", n));

            // 极值与范围
            RegisterPureNode("Min", "Min", NodeCategoryType.Math, basicSubCat, mathColor,
                "取最小值", n => BinaryMathNode("Min", "Min", n));
            RegisterPureNode("Max", "Max", NodeCategoryType.Math, basicSubCat, mathColor,
                "取最大值", n => BinaryMathNode("Max", "Max", n));
            RegisterPureNode("Clamp", "Clamp", NodeCategoryType.Math, basicSubCat, mathColor,
                "将值限制在范围内", n => ClampNode(n));

            // 插值
            RegisterPureNode("Lerp", "Lerp", NodeCategoryType.Math, interpSubCat, mathColor,
                "线性插值 (A + (B - A) * Alpha)", n => LerpNode(n));
            RegisterPureNode("InverseLerp", "Inverse Lerp", NodeCategoryType.Math, interpSubCat, mathColor,
                "反向线性插值 (返回 0~1 之间的值)", n => InverseLerpNode(n));
            RegisterPureNode("MapRange", "Map Range", NodeCategoryType.Math, interpSubCat, mathColor,
                "将值从一个范围映射到另一个范围", n => MapRangeNode(n));
        }

        // ─────────────────────────────────────────────────────────────────────────
        //  向量节点注册
        // ─────────────────────────────────────────────────────────────────────────

        private static void RegisterVectorNodes()
        {
            string vecColor = "#FFCC00";
            string basicVec = "Vector/Basic";
            string advVec = "Vector/Advanced";

            RegisterPureNode("Vector3Add", "Vector3 + Vector3", NodeCategoryType.Vector, basicVec, vecColor,
                "两个三维向量相加", n => BinaryVec3Node("Vector3Add", "+", n));
            RegisterPureNode("Vector3Subtract", "Vector3 - Vector3", NodeCategoryType.Vector, basicVec, vecColor,
                "两个三维向量相减", n => BinaryVec3Node("Vector3Subtract", "-", n));
            RegisterPureNode("Vector3Scale", "Vector3 * Float", NodeCategoryType.Vector, basicVec, vecColor,
                "向量乘标量", n => ScaleVec3Node(n));
            RegisterPureNode("CrossProduct", "Cross Product", NodeCategoryType.Vector, advVec, vecColor,
                "向量叉乘", n => BinaryVec3Node("CrossProduct", "x", n));
            RegisterPureNode("DotProduct", "Dot Product", NodeCategoryType.Vector, advVec, vecColor,
                "向量点乘（返回标量）", n => DotProductNode(n));
            RegisterPureNode("Normalize", "Normalize", NodeCategoryType.Vector, basicVec, vecColor,
                "向量归一化", n => UnaryVec3Node("Normalize", n));
            RegisterPureNode("Vector3Length", "Vector Length", NodeCategoryType.Vector, basicVec, vecColor,
                "向量长度", n => Vec3ToFloatNode("Vector3Length", "Length", n));
            RegisterPureNode("Vector3Distance", "Vector Distance", NodeCategoryType.Vector, basicVec, vecColor,
                "两点间距离", n => BinaryVec3ToFloatNode("Vector3Distance", "Distance", n));
            RegisterPureNode("Reflect", "Reflect Vector", NodeCategoryType.Vector, advVec, vecColor,
                "向量反射", n => BinaryVec3Node("Reflect", "Reflect", n));
            RegisterPureNode("Project", "Project Vector", NodeCategoryType.Vector, advVec, vecColor,
                "向量投影", n => BinaryVec3Node("Project", "Project", n));
            RegisterPureNode("Vector3Lerp", "Vector Lerp", NodeCategoryType.Vector, basicVec, vecColor,
                "向量线性插值", n => Vec3LerpNode(n));
            RegisterPureNode("VectorFromAxis", "Vector from Axis", NodeCategoryType.Vector, basicVec, vecColor,
                "根据轴和大小创建向量", n => VectorFromAxisNode(n));
            RegisterPureNode("GetForward", "Get Forward Vector", NodeCategoryType.Vector, basicVec, vecColor,
                "获取前方向 (0,0,1)", n => AxisVecNode("GetForward", new Vector3(0, 0, 1), n));
            RegisterPureNode("GetRight", "Get Right Vector", NodeCategoryType.Vector, basicVec, vecColor,
                "获取右方向 (1,0,0)", n => AxisVecNode("GetRight", new Vector3(1, 0, 0), n));
            RegisterPureNode("GetUp", "Get Up Vector", NodeCategoryType.Vector, basicVec, vecColor,
                "获取上方向 (0,1,0)", n => AxisVecNode("GetUp", new Vector3(0, 1, 0), n));
            RegisterPureNode("BreakVector3", "Break Vector3", NodeCategoryType.Vector, basicVec, vecColor,
                "拆解向量为 X/Y/Z 分量", n => BreakVec3Node(n));
            RegisterPureNode("MakeVector3", "Make Vector3", NodeCategoryType.Vector, basicVec, vecColor,
                "由 X/Y/Z 构造向量", n => MakeVec3Node(n));
        }

        // ─────────────────────────────────────────────────────────────────────────
        //  旋转节点注册
        // ─────────────────────────────────────────────────────────────────────────

        private static void RegisterRotationNodes()
        {
            string rotColor = "#9966FF";
            string basicRot = "Rotation/Basic";
            string advRot = "Rotation/Advanced";

            RegisterPureNode("MakeRotator", "Make Rotator", NodeCategoryType.Rotation, basicRot, rotColor,
                "由 Pitch/Yaw/Roll 构造旋转体", n => MakeRotatorNode(n));
            RegisterPureNode("RotatorFromAxis", "Rotator from Axis Angle", NodeCategoryType.Rotation, basicRot, rotColor,
                "由轴和角度构造旋转", n => RotatorFromAxisNode(n));
            RegisterPureNode("Angle", "Angle Between", NodeCategoryType.Rotation, basicRot, rotColor,
                "计算两向量间角度", n => AngleBetweenNode(n));
            RegisterPureNode("CombineRotators", "Combine Rotators", NodeCategoryType.Rotation, advRot, rotColor,
                "组合两个旋转", n => BinaryRotatorNode("CombineRotators", n));
            RegisterPureNode("RotatorFromLookAt", "Look At Rotation", NodeCategoryType.Rotation, advRot, rotColor,
                "从目标方向构造旋转", n => LookAtRotatorNode(n));
            RegisterPureNode("GetRotationX", "Get Rotation X (Roll)", NodeCategoryType.Rotation, basicRot, rotColor,
                "获取旋转体的 Roll 分量", n => RotatorComponentNode("GetRotationX", "Roll", n));
            RegisterPureNode("GetRotationY", "Get Rotation Y (Pitch)", NodeCategoryType.Rotation, basicRot, rotColor,
                "获取旋转体的 Pitch 分量", n => RotatorComponentNode("GetRotationY", "Pitch", n));
            RegisterPureNode("GetRotationZ", "Get Rotation Z (Yaw)", NodeCategoryType.Rotation, basicRot, rotColor,
                "获取旋转体的 Yaw 分量", n => RotatorComponentNode("GetRotationZ", "Yaw", n));
            RegisterPureNode("ToQuaternion", "Rotator to Quaternion", NodeCategoryType.Rotation, advRot, rotColor,
                "旋转体转四元数", n => RotatorToQuatNode(n));
            RegisterPureNode("FromQuaternion", "Quaternion to Rotator", NodeCategoryType.Rotation, advRot, rotColor,
                "四元数转旋转体", n => QuatToRotatorNode(n));
            RegisterPureNode("Slerp", "Slerp (Spherical Lerp)", NodeCategoryType.Rotation, advRot, rotColor,
                "球面线性插值", n => SlerpNode(n));
        }

        // ─────────────────────────────────────────────────────────────────────────
        //  变换节点注册
        // ─────────────────────────────────────────────────────────────────────────

        private static void RegisterTransformNodes()
        {
            string transColor = "#CC6600";
            string basicTrans = "Transform/Basic";
            string advTrans = "Transform/Advanced";

            RegisterPureNode("MakeTransform", "Make Transform", NodeCategoryType.Transform, basicTrans, transColor,
                "由位置/旋转/缩放构造变换", n => MakeTransformNode(n));
            RegisterPureNode("BreakTransform", "Break Transform", NodeCategoryType.Transform, basicTrans, transColor,
                "拆解变换为位置/旋转/缩放", n => BreakTransformNode(n));
            RegisterPureNode("TransformLocation", "Transform Location", NodeCategoryType.Transform, advTrans, transColor,
                "用变换转换位置", n => TransformLocationNode(n));
            RegisterPureNode("TransformRotation", "Transform Rotation", NodeCategoryType.Transform, advTrans, transColor,
                "用变换转换旋转", n => TransformRotationNode(n));
            RegisterPureNode("TransformDirection", "Transform Direction", NodeCategoryType.Transform, advTrans, transColor,
                "用变换转换方向", n => TransformDirectionNode(n));
            RegisterPureNode("InverseTransform", "Inverse Transform", NodeCategoryType.Transform, advTrans, transColor,
                "获取变换的逆", n => InverseTransformNode(n));
            RegisterPureNode("CombineTransforms", "Combine Transforms", NodeCategoryType.Transform, advTrans, transColor,
                "组合两个变换", n => CombineTransformsNode(n));
            RegisterPureNode("GetLocation", "Get Location", NodeCategoryType.Transform, basicTrans, transColor,
                "获取变换的位置分量", n => TransformGetComponentNode("GetLocation", "Location", GraphPinDataType.Vector3, n));
            RegisterPureNode("GetRotation", "Get Rotation", NodeCategoryType.Transform, basicTrans, transColor,
                "获取变换的旋转分量", n => TransformGetComponentNode("GetRotation", "Rotation", GraphPinDataType.Rotator, n));
            RegisterPureNode("GetScale", "Get Scale", NodeCategoryType.Transform, basicTrans, transColor,
                "获取变换的缩放分量", n => TransformGetComponentNode("GetScale", "Scale", GraphPinDataType.Vector3, n));
        }

        // ─────────────────────────────────────────────────────────────────────────
        //  逻辑节点注册
        // ─────────────────────────────────────────────────────────────────────────

        private static void RegisterLogicNodes()
        {
            string logicColor = "#00CC66";
            string basicLogic = "Logic/Basic";
            string flowLogic = "Logic/Flow";
            string switchLogic = "Logic/Switch";

            RegisterPureNode("AND", "AND (&&)", NodeCategoryType.Logic, basicLogic, logicColor,
                "逻辑与", n => BinaryBoolNode("AND", "&&", n));
            RegisterPureNode("OR", "OR (||)", NodeCategoryType.Logic, basicLogic, logicColor,
                "逻辑或", n => BinaryBoolNode("OR", "||", n));
            RegisterPureNode("NOT", "NOT (!)", NodeCategoryType.Logic, basicLogic, logicColor,
                "逻辑非", n => UnaryBoolNode("NOT", "!", n));
            RegisterPureNode("XOR", "XOR (^)", NodeCategoryType.Logic, basicLogic, logicColor,
                "逻辑异或", n => BinaryBoolNode("XOR", "^", n));
            RegisterPureNode("NAND", "NAND", NodeCategoryType.Logic, basicLogic, logicColor,
                "逻辑与非", n => BinaryBoolNode("NAND", "!(", n));
            RegisterPureNode("NOR", "NOR", NodeCategoryType.Logic, basicLogic, logicColor,
                "逻辑或非", n => BinaryBoolNode("NOR", "!(", n));

            // 流程控制逻辑
            RegisterExecNode("Branch", "Branch", NodeCategoryType.Logic, flowLogic, "#808080",
                "根据条件执行不同分支", n => BranchNode(n));
            RegisterExecNode("Switch_Int", "Switch on Int", NodeCategoryType.Logic, switchLogic, "#808080",
                "根据整数值执行不同分支", n => SwitchNode("Switch_Int", GraphPinDataType.Int, n));
            RegisterExecNode("Switch_Enum", "Switch on Enum", NodeCategoryType.Logic, switchLogic, "#808080",
                "根据枚举值执行不同分支", n => SwitchNode("Switch_Enum", GraphPinDataType.Enum, n));
            RegisterExecNode("Switch_Name", "Switch on Name", NodeCategoryType.Logic, switchLogic, "#808080",
                "根据名称执行不同分支", n => SwitchNode("Switch_Name", GraphPinDataType.Name, n));
            RegisterExecNode("Switch_String", "Switch on String", NodeCategoryType.Logic, switchLogic, "#808080",
                "根据字符串执行不同分支", n => SwitchNode("Switch_String", GraphPinDataType.String, n));
            RegisterExecNode("Sequence", "Sequence", NodeCategoryType.Logic, flowLogic, "#808080",
                "按顺序执行多个输出（0-5）", n => SequenceNode(n));
            RegisterExecNode("MultiGate", "Multi Gate", NodeCategoryType.Logic, flowLogic, "#808080",
                "循环依次触发每个输出", n => MultiGateNode(n));
            RegisterExecNode("DoOnce", "Do Once", NodeCategoryType.Logic, flowLogic, "#808080",
                "只执行一次（可重置）", n => DoOnceNode(n));
            RegisterExecNode("DoN", "Do N", NodeCategoryType.Logic, flowLogic, "#808080",
                "只执行 N 次（可重置）", n => DoNNode(n));
            RegisterExecNode("FlipFlop", "Flip Flop", NodeCategoryType.Logic, flowLogic, "#808080",
                "交替输出 A/B", n => FlipFlopNode(n));
            RegisterExecNode("Gate", "Gate", NodeCategoryType.Logic, flowLogic, "#808080",
                "门控节点（开启时允许执行流通过）", n => GateNode(n));
            RegisterExecNode("Multiplexer", "Multiplexer", NodeCategoryType.Logic, switchLogic, "#808080",
                "多路选择器（从多个输入选一个输出）", n => MultiplexerNode(n));
            RegisterExecNode("Demux", "Demux", NodeCategoryType.Logic, switchLogic, "#808080",
                "多路分配器（将输入分配到多个输出）", n => DemuxNode(n));
        }

        // ─────────────────────────────────────────────────────────────────────────
        //  流程控制节点注册
        // ─────────────────────────────────────────────────────────────────────────

        private static void RegisterFlowControlNodes()
        {
            string eventColor = "#CC0000";
            string flowColor = "#808080";
            string functionColor = "#0066CC";

            // 事件
            RegisterEventNode("EventBeginPlay", "Event BeginPlay", NodeCategoryType.FlowControl, "Events/Game", eventColor,
                "游戏开始时触发", new List<(string, GraphPinDataType)>
                {
                    ("Exec", GraphPinDataType.Exec)
                });
            RegisterEventNode("EventTick", "Event Tick", NodeCategoryType.FlowControl, "Events/Game", eventColor,
                "每帧触发", new List<(string, GraphPinDataType)>
                {
                    ("Exec", GraphPinDataType.Exec),
                    ("DeltaTime", GraphPinDataType.Float),
                    ("WorldTime", GraphPinDataType.Float)
                });
            RegisterEventNode("CustomEvent", "Custom Event", NodeCategoryType.FlowControl, "Events/Custom", "#FF6600",
                "自定义事件触发", new List<(string, GraphPinDataType)>
                {
                    ("Exec", GraphPinDataType.Exec)
                });

            // 函数
            RegisterExecNode("Function", "Function", NodeCategoryType.FlowControl, "Functions", functionColor,
                "函数定义节点", n => FunctionDefNode(n));
            RegisterExecNode("CallFunction", "Call Function", NodeCategoryType.FlowControl, "Functions", functionColor,
                "调用函数节点", n => CallFunctionNode(n));

            // 循环
            RegisterExecNode("ForEachLoop", "For Each Loop", NodeCategoryType.FlowControl, "Loops", flowColor,
                "遍历数组/集合", n => ForEachLoopNode(n));
            RegisterExecNode("ForLoop", "For Loop", NodeCategoryType.FlowControl, "Loops", flowColor,
                "计数循环", n => ForLoopNode(n));
            RegisterExecNode("WhileLoop", "While Loop", NodeCategoryType.FlowControl, "Loops", flowColor,
                "条件循环", n => WhileLoopNode(n));
            RegisterExecNode("DoWhile", "Do While", NodeCategoryType.FlowControl, "Loops", flowColor,
                "先执行后判断循环", n => DoWhileNode(n));
            RegisterExecNode("Break", "Break", NodeCategoryType.FlowControl, "Loops", flowColor,
                "跳出循环", n => LoopControlNode("Break", "跳出当前循环", n));
            RegisterExecNode("Continue", "Continue", NodeCategoryType.FlowControl, "Loops", flowColor,
                "继续下一轮循环", n => LoopControlNode("Continue", "跳到下一轮循环", n));

            // 时间
            RegisterExecNode("Delay", "Delay", NodeCategoryType.FlowControl, "Time", "#666666",
                "延迟执行", n => DelayNode(n));
            RegisterExecNode("SetTimer", "Set Timer", NodeCategoryType.FlowControl, "Time", "#666666",
                "设置定时器", n => SetTimerNode(n));
            RegisterEventNode("TimerEvent", "Timer Event", NodeCategoryType.FlowControl, "Events/Time", eventColor,
                "定时器事件", new List<(string, GraphPinDataType)>
                {
                    ("Exec", GraphPinDataType.Exec),
                    ("FunctionName", GraphPinDataType.String)
                });
        }

        // ─────────────────────────────────────────────────────────────────────────
        //  变量节点注册
        // ─────────────────────────────────────────────────────────────────────────

        private static void RegisterVariableNodes()
        {
            string varColor = "#00CCCC";
            string arrColor = "#CC00CC";

            RegisterExecNode("GetVariable", "Get Variable", NodeCategoryType.Variable, "Variables", varColor,
                "获取变量值", n => GetVariableNode(n));
            RegisterExecNode("SetVariable", "Set Variable", NodeCategoryType.Variable, "Variables", varColor,
                "设置变量值", n => SetVariableNode(n));
            RegisterExecNode("GetMember", "Get Member", NodeCategoryType.Variable, "Members", varColor,
                "获取对象成员值", n => GetMemberNode(n));
            RegisterExecNode("SetMember", "Set Member", NodeCategoryType.Variable, "Members", varColor,
                "设置对象成员值", n => SetMemberNode(n));

            // 数组操作
            RegisterPureNode("ArrayLength", "Array Length", NodeCategoryType.Variable, "Array", arrColor,
                "获取数组长度", n => ArrayLengthNode(n));
            RegisterPureNode("ArrayAdd", "Array Add", NodeCategoryType.Variable, "Array", arrColor,
                "添加元素到数组", n => ArrayAddNode(n));
            RegisterPureNode("ArrayRemove", "Array Remove", NodeCategoryType.Variable, "Array", arrColor,
                "从数组移除元素", n => ArrayRemoveNode(n));
            RegisterPureNode("ArrayFind", "Array Find", NodeCategoryType.Variable, "Array", arrColor,
                "在数组中查找元素", n => ArrayFindNode(n));
            RegisterPureNode("ArraySort", "Array Sort", NodeCategoryType.Variable, "Array", arrColor,
                "数组排序", n => ArraySortNode(n));
            RegisterPureNode("ArrayFilter", "Array Filter", NodeCategoryType.Variable, "Array", arrColor,
                "数组过滤", n => ArrayFilterNode(n));
            RegisterPureNode("ArrayMap", "Array Map", NodeCategoryType.Variable, "Array", arrColor,
                "数组映射变换", n => ArrayMapNode(n));
            RegisterExecNode("GetArrayElement", "Get Array Element", NodeCategoryType.Variable, "Array", arrColor,
                "获取数组指定索引元素", n => GetArrayElementNode(n));
            RegisterExecNode("SetArrayElement", "Set Array Element", NodeCategoryType.Variable, "Array", arrColor,
                "设置数组指定索引元素", n => SetArrayElementNode(n));
            RegisterPureNode("MakeArray", "Make Array", NodeCategoryType.Variable, "Array", arrColor,
                "构造数组", n => MakeArrayNode(n));
            RegisterPureNode("BreakArray", "Break Array", NodeCategoryType.Variable, "Array", arrColor,
                "拆解数组为各个元素", n => BreakArrayNode(n));
        }

        // ─────────────────────────────────────────────────────────────────────────
        //  比较节点注册
        // ─────────────────────────────────────────────────────────────────────────

        private static void RegisterComparisonNodes()
        {
            string cmpColor = "#00CC66";

            RegisterPureNode("Equal", "Equal (==)", NodeCategoryType.Comparison, "Comparison", cmpColor,
                "等于", n => ComparisonNode("Equal", "==", n));
            RegisterPureNode("NotEqual", "Not Equal (!=)", NodeCategoryType.Comparison, "Comparison", cmpColor,
                "不等于", n => ComparisonNode("NotEqual", "!=", n));
            RegisterPureNode("Greater", "Greater (>)", NodeCategoryType.Comparison, "Comparison", cmpColor,
                "大于", n => ComparisonNode("Greater", ">", n));
            RegisterPureNode("Less", "Less (<)", NodeCategoryType.Comparison, "Comparison", cmpColor,
                "小于", n => ComparisonNode("Less", "<", n));
            RegisterPureNode("GreaterEqual", "Greater Equal (>=)", NodeCategoryType.Comparison, "Comparison", cmpColor,
                "大于等于", n => ComparisonNode("GreaterEqual", ">=", n));
            RegisterPureNode("LessEqual", "Less Equal (<=)", NodeCategoryType.Comparison, "Comparison", cmpColor,
                "小于等于", n => ComparisonNode("LessEqual", "<=", n));
            RegisterPureNode("ApproxEqual", "Approximately Equal", NodeCategoryType.Comparison, "Comparison", cmpColor,
                "近似相等（浮点误差容差）", n => ComparisonNode("ApproxEqual", "~==", n));
            RegisterPureNode("StringEqual", "String Equal", NodeCategoryType.Comparison, "String", cmpColor,
                "字符串相等", n => ComparisonNode("StringEqual", "==", n));
            RegisterPureNode("NameEqual", "Name Equal", NodeCategoryType.Comparison, "Name", cmpColor,
                "名称标识相等", n => ComparisonNode("NameEqual", "==", n));
            RegisterPureNode("EnumEqual", "Enum Equal", NodeCategoryType.Comparison, "Enum", cmpColor,
                "枚举相等", n => ComparisonNode("EnumEqual", "==", n));
        }

        // ─────────────────────────────────────────────────────────────────────────
        //  类型转换节点注册
        // ─────────────────────────────────────────────────────────────────────────

        private static void RegisterCastNodes()
        {
            string castColor = "#FFCC00";

            RegisterPureNode("ToFloat", "To Float", NodeCategoryType.Cast, "Cast", castColor,
                "转换为浮点数", n => CastNode("ToFloat", GraphPinDataType.Float, n));
            RegisterPureNode("ToInt", "To Int", NodeCategoryType.Cast, "Cast", castColor,
                "转换为整数", n => CastNode("ToInt", GraphPinDataType.Int, n));
            RegisterPureNode("ToBool", "To Bool", NodeCategoryType.Cast, "Cast", castColor,
                "转换为布尔值", n => CastNode("ToBool", GraphPinDataType.Bool, n));
            RegisterPureNode("ToString", "To String", NodeCategoryType.Cast, "Cast", castColor,
                "转换为字符串", n => CastNode("ToString", GraphPinDataType.String, n));
            RegisterPureNode("ToVector3", "To Vector3", NodeCategoryType.Cast, "Cast", castColor,
                "转换为三维向量", n => CastNode("ToVector3", GraphPinDataType.Vector3, n));
            RegisterPureNode("ToColor", "To Color", NodeCategoryType.Cast, "Cast", castColor,
                "转换为颜色", n => CastNode("ToColor", GraphPinDataType.Color, n));
            RegisterPureNode("ToRotator", "To Rotator", NodeCategoryType.Cast, "Cast", castColor,
                "转换为旋转体", n => CastNode("ToRotator", GraphPinDataType.Rotator, n));
        }

        // ─────────────────────────────────────────────────────────────────────────
        //  Actor 节点注册
        // ─────────────────────────────────────────────────────────────────────────

        private static void RegisterActorNodes()
        {
            string actorColor = "#0000CC";

            RegisterExecNode("SpawnActor", "Spawn Actor", NodeCategoryType.Actor, "Actor/Lifecycle", actorColor,
                "生成 Actor 实例", n => SpawnActorNode(n));
            RegisterExecNode("DestroyActor", "Destroy Actor", NodeCategoryType.Actor, "Actor/Lifecycle", actorColor,
                "销毁 Actor", n => DestroyActorNode(n));
            RegisterExecNode("GetActorLocation", "Get Actor Location", NodeCategoryType.Actor, "Actor/Transform", actorColor,
                "获取 Actor 位置", n => ActorPropertyVec3Node("GetActorLocation", "Location", n));
            RegisterExecNode("SetActorLocation", "Set Actor Location", NodeCategoryType.Actor, "Actor/Transform", actorColor,
                "设置 Actor 位置", n => SetActorPropertyVec3Node("SetActorLocation", "Location", n));
            RegisterExecNode("GetActorRotation", "Get Actor Rotation", NodeCategoryType.Actor, "Actor/Transform", actorColor,
                "获取 Actor 旋转", n => ActorPropertyVec3Node("GetActorRotation", "Rotation", n));
            RegisterExecNode("SetActorRotation", "Set Actor Rotation", NodeCategoryType.Actor, "Actor/Transform", actorColor,
                "设置 Actor 旋转", n => SetActorPropertyVec3Node("SetActorRotation", "Rotation", n));
            RegisterExecNode("GetActorForwardVector", "Get Actor Forward", NodeCategoryType.Actor, "Actor/Transform", actorColor,
                "获取 Actor 前方向", n => ActorPropertyVec3Node("GetActorForwardVector", "Forward", n));
            RegisterPureNode("IsActorValid", "Is Actor Valid", NodeCategoryType.Actor, "Actor/Utility", actorColor,
                "检查 Actor 是否有效", n => IsActorValidNode(n));
            RegisterPureNode("GetAllActorsOfClass", "Get All Actors of Class", NodeCategoryType.Actor, "Actor/Query", actorColor,
                "获取指定类的所有 Actor", n => GetAllActorsOfClassNode(n));
            RegisterExecNode("ForEachActor", "For Each Actor", NodeCategoryType.Actor, "Actor/Query", actorColor,
                "遍历 Actor 列表", n => ForEachActorNode(n));
        }

        // ─────────────────────────────────────────────────────────────────────────
        //  输入节点注册
        // ─────────────────────────────────────────────────────────────────────────

        private static void RegisterInputNodes()
        {
            string inputColor = "#0066CC";

            RegisterPureNode("GetActionValue", "Get Action Value", NodeCategoryType.Input, "Input/Action", inputColor,
                "获取输入动作值（模拟轴）", n => InputValueNode("GetActionValue", GraphPinDataType.Float, n));
            RegisterPureNode("IsActionPressed", "Is Action Pressed", NodeCategoryType.Input, "Input/Action", inputColor,
                "检查动作是否被按下", n => InputValueNode("IsActionPressed", GraphPinDataType.Bool, n));
            RegisterPureNode("IsActionJustPressed", "Is Action Just Pressed", NodeCategoryType.Input, "Input/Action", inputColor,
                "检查动作是否刚被按下", n => InputValueNode("IsActionJustPressed", GraphPinDataType.Bool, n));
            RegisterPureNode("GetAxisValue", "Get Axis Value", NodeCategoryType.Input, "Input/Axis", inputColor,
                "获取输入轴值", n => InputValueNode("GetAxisValue", GraphPinDataType.Float, n));
            RegisterPureNode("GetMouseDelta", "Get Mouse Delta", NodeCategoryType.Input, "Input/Mouse", inputColor,
                "获取鼠标移动增量", n => InputVec2Node("GetMouseDelta", n));
            RegisterEventNode("GetKeyEvent", "Key Event", NodeCategoryType.Input, "Events/Input", inputColor,
                "按键事件", new List<(string, GraphPinDataType)>
                {
                    ("Exec", GraphPinDataType.Exec),
                    ("Key", GraphPinDataType.String),
                    ("EventType", GraphPinDataType.String)
                });
        }

        // ─────────────────────────────────────────────────────────────────────────
        //  游戏节点注册
        // ─────────────────────────────────────────────────────────────────────────

        private static void RegisterGameNodes()
        {
            string gameColor = "#0066CC";

            RegisterPureNode("GetGameState", "Get Game State", NodeCategoryType.Game, "Game/State", gameColor,
                "获取游戏状态", n => GameStateNode("GetGameState", n));
            RegisterPureNode("GetGameMode", "Get Game Mode", NodeCategoryType.Game, "Game/Mode", gameColor,
                "获取游戏模式", n => GameStateNode("GetGameMode", n));
            RegisterPureNode("GetPlayerController", "Get Player Controller", NodeCategoryType.Game, "Game/Player", gameColor,
                "获取玩家控制器", n => GameStateNode("GetPlayerController", n));
            RegisterPureNode("GetPawn", "Get Pawn", NodeCategoryType.Game, "Game/Player", gameColor,
                "获取 Pawn", n => GameStateNode("GetPawn", n));
            RegisterExecNode("SetGameState", "Set Game State", NodeCategoryType.Game, "Game/State", gameColor,
                "设置游戏状态", n => SetGameStateNode(n));
            RegisterExecNode("ApplyDamage", "Apply Damage", NodeCategoryType.Game, "Game/Damage", gameColor,
                "对目标施加伤害", n => ApplyDamageNode(n));
            RegisterExecNode("PrintString", "Print String", NodeCategoryType.Game, "Game/Debug", "#9900CC",
                "打印字符串到日志/屏幕", n => PrintStringNode(n));
        }

        // ─────────────────────────────────────────────────────────────────────────
        //  节点工厂方法（创建各类节点的标准实现）
        // ─────────────────────────────────────────────────────────────────────────

        // 便捷注册方法

        private static void RegisterPureNode(string nodeType, string title, NodeCategoryType cat,
            string subCat, string color, string desc, Func<string, GraphNode> factory)
        {
            _nodes[nodeType] = new NodeScriptNodeDescriptor
            {
                NodeType = nodeType,
                Title = title,
                Category = cat,
                SubCategory = subCat,
                Description = desc,
                ColorHex = color,
                IsPure = true,
                Factory = () => factory(nodeType)
            };
        }

        private static void RegisterExecNode(string nodeType, string title, NodeCategoryType cat,
            string subCat, string color, string desc, Func<string, GraphNode> factory)
        {
            _nodes[nodeType] = new NodeScriptNodeDescriptor
            {
                NodeType = nodeType,
                Title = title,
                Category = cat,
                SubCategory = subCat,
                Description = desc,
                ColorHex = color,
                IsPure = false,
                Factory = () => factory(nodeType)
            };
        }

        private static void RegisterEventNode(string nodeType, string title, NodeCategoryType cat,
            string subCat, string color, string desc, List<(string, GraphPinDataType)> outputs)
        {
            _nodes[nodeType] = new NodeScriptNodeDescriptor
            {
                NodeType = nodeType,
                Title = title,
                Category = cat,
                SubCategory = subCat,
                Description = desc,
                ColorHex = color,
                IsPure = false,
                Factory = () => EventNodeFactory(nodeType, title, outputs)
            };
        }

        // ─── 数学节点工厂 ───

        private static GraphNode BinaryMathNode(string type, string op, string name)
        {
            var node = new GraphNode
            {
                NodeType = type,
                Title = $"{name}",
                Category = "Math",
                ColorHex = "#00CC66",
                Size = new Vector2(220, 80)
            };
            node.AddInputPin("A", GraphPinDataType.Float, "0");
            node.AddInputPin("B", GraphPinDataType.Float, "0");
            node.AddOutputPin("Result", GraphPinDataType.Float);
            return node;
        }

        private static GraphNode UnaryMathNode(string type, string func, string name)
        {
            var node = new GraphNode
            {
                NodeType = type,
                Title = $"{name}",
                Category = "Math",
                ColorHex = "#00CC66",
                Size = new Vector2(200, 80)
            };
            node.AddInputPin("Input", GraphPinDataType.Float, "0");
            node.AddOutputPin("Result", GraphPinDataType.Float);
            return node;
        }

        private static GraphNode ClampNode(string name)
        {
            var node = new GraphNode
            {
                NodeType = "Clamp",
                Title = "Clamp",
                Category = "Math",
                ColorHex = "#00CC66",
                Size = new Vector2(220, 100)
            };
            node.AddInputPin("Value", GraphPinDataType.Float, "0");
            node.AddInputPin("Min", GraphPinDataType.Float, "0");
            node.AddInputPin("Max", GraphPinDataType.Float, "1");
            node.AddOutputPin("Result", GraphPinDataType.Float);
            return node;
        }

        private static GraphNode LerpNode(string name)
        {
            var node = new GraphNode
            {
                NodeType = "Lerp",
                Title = "Lerp",
                Category = "Math",
                ColorHex = "#00CC66",
                Size = new Vector2(220, 100)
            };
            node.AddInputPin("A", GraphPinDataType.Float, "0");
            node.AddInputPin("B", GraphPinDataType.Float, "1");
            node.AddInputPin("Alpha", GraphPinDataType.Float, "0");
            node.AddOutputPin("Result", GraphPinDataType.Float);
            return node;
        }

        private static GraphNode InverseLerpNode(string name)
        {
            var node = new GraphNode
            {
                NodeType = "InverseLerp",
                Title = "Inverse Lerp",
                Category = "Math",
                ColorHex = "#00CC66",
                Size = new Vector2(220, 100)
            };
            node.AddInputPin("A", GraphPinDataType.Float, "0");
            node.AddInputPin("B", GraphPinDataType.Float, "1");
            node.AddInputPin("Value", GraphPinDataType.Float, "0");
            node.AddOutputPin("Result", GraphPinDataType.Float);
            return node;
        }

        private static GraphNode MapRangeNode(string name)
        {
            var node = new GraphNode
            {
                NodeType = "MapRange",
                Title = "Map Range",
                Category = "Math",
                ColorHex = "#00CC66",
                Size = new Vector2(220, 140)
            };
            node.AddInputPin("Value", GraphPinDataType.Float, "0");
            node.AddInputPin("InMin", GraphPinDataType.Float, "0");
            node.AddInputPin("InMax", GraphPinDataType.Float, "1");
            node.AddInputPin("OutMin", GraphPinDataType.Float, "0");
            node.AddInputPin("OutMax", GraphPinDataType.Float, "1");
            node.AddOutputPin("Result", GraphPinDataType.Float);
            return node;
        }

        // ─── 向量节点工厂 ───

        private static GraphNode BinaryVec3Node(string type, string op, string name)
        {
            var node = new GraphNode
            {
                NodeType = type,
                Title = name,
                Category = "Vector",
                ColorHex = "#FFCC00",
                Size = new Vector2(220, 80)
            };
            node.AddInputPin("A", GraphPinDataType.Vector3, "(0,0,0)");
            node.AddInputPin("B", GraphPinDataType.Vector3, "(0,0,0)");
            node.AddOutputPin("Result", GraphPinDataType.Vector3);
            return node;
        }

        private static GraphNode UnaryVec3Node(string type, string name)
        {
            var node = new GraphNode
            {
                NodeType = type,
                Title = name,
                Category = "Vector",
                ColorHex = "#FFCC00",
                Size = new Vector2(200, 80)
            };
            node.AddInputPin("Input", GraphPinDataType.Vector3, "(0,0,0)");
            node.AddOutputPin("Result", GraphPinDataType.Vector3);
            return node;
        }

        private static GraphNode ScaleVec3Node(string name)
        {
            var node = new GraphNode
            {
                NodeType = "Vector3Scale",
                Title = "Vector3 * Float",
                Category = "Vector",
                ColorHex = "#FFCC00",
                Size = new Vector2(220, 80)
            };
            node.AddInputPin("Vector", GraphPinDataType.Vector3, "(0,0,0)");
            node.AddInputPin("Scale", GraphPinDataType.Float, "1");
            node.AddOutputPin("Result", GraphPinDataType.Vector3);
            return node;
        }

        private static GraphNode DotProductNode(string name)
        {
            var node = new GraphNode
            {
                NodeType = "DotProduct",
                Title = "Dot Product",
                Category = "Vector",
                ColorHex = "#FFCC00",
                Size = new Vector2(220, 80)
            };
            node.AddInputPin("A", GraphPinDataType.Vector3, "(0,0,0)");
            node.AddInputPin("B", GraphPinDataType.Vector3, "(0,0,0)");
            node.AddOutputPin("Result", GraphPinDataType.Float);
            return node;
        }

        private static GraphNode Vec3ToFloatNode(string type, string title, string name)
        {
            var node = new GraphNode
            {
                NodeType = type,
                Title = title,
                Category = "Vector",
                ColorHex = "#FFCC00",
                Size = new Vector2(200, 80)
            };
            node.AddInputPin("Vector", GraphPinDataType.Vector3, "(0,0,0)");
            node.AddOutputPin("Result", GraphPinDataType.Float);
            return node;
        }

        private static GraphNode BinaryVec3ToFloatNode(string type, string title, string name)
        {
            var node = new GraphNode
            {
                NodeType = type,
                Title = title,
                Category = "Vector",
                ColorHex = "#FFCC00",
                Size = new Vector2(220, 80)
            };
            node.AddInputPin("A", GraphPinDataType.Vector3, "(0,0,0)");
            node.AddInputPin("B", GraphPinDataType.Vector3, "(0,0,0)");
            node.AddOutputPin("Result", GraphPinDataType.Float);
            return node;
        }

        private static GraphNode Vec3LerpNode(string name)
        {
            var node = new GraphNode
            {
                NodeType = "Vector3Lerp",
                Title = "Vector Lerp",
                Category = "Vector",
                ColorHex = "#FFCC00",
                Size = new Vector2(220, 100)
            };
            node.AddInputPin("A", GraphPinDataType.Vector3, "(0,0,0)");
            node.AddInputPin("B", GraphPinDataType.Vector3, "(0,0,0)");
            node.AddInputPin("Alpha", GraphPinDataType.Float, "0");
            node.AddOutputPin("Result", GraphPinDataType.Vector3);
            return node;
        }

        private static GraphNode VectorFromAxisNode(string name)
        {
            var node = new GraphNode
            {
                NodeType = "VectorFromAxis",
                Title = "Vector from Axis",
                Category = "Vector",
                ColorHex = "#FFCC00",
                Size = new Vector2(220, 80)
            };
            node.AddInputPin("Axis", GraphPinDataType.Name, "X");
            node.AddInputPin("Length", GraphPinDataType.Float, "1");
            node.AddOutputPin("Result", GraphPinDataType.Vector3);
            return node;
        }

        private static GraphNode AxisVecNode(string type, Vector3 value, string name)
        {
            var node = new GraphNode
            {
                NodeType = type,
                Title = name,
                Category = "Vector",
                ColorHex = "#FFCC00",
                Size = new Vector2(200, 60)
            };
            node.AddOutputPin("Value", GraphPinDataType.Vector3);
            node.Metadata["DefaultValue"] = $"({value.X},{value.Y},{value.Z})";
            return node;
        }

        private static GraphNode BreakVec3Node(string name)
        {
            var node = new GraphNode
            {
                NodeType = "BreakVector3",
                Title = "Break Vector3",
                Category = "Vector",
                ColorHex = "#FFCC00",
                Size = new Vector2(200, 100)
            };
            node.AddInputPin("Vector", GraphPinDataType.Vector3, "(0,0,0)");
            node.AddOutputPin("X", GraphPinDataType.Float);
            node.AddOutputPin("Y", GraphPinDataType.Float);
            node.AddOutputPin("Z", GraphPinDataType.Float);
            return node;
        }

        private static GraphNode MakeVec3Node(string name)
        {
            var node = new GraphNode
            {
                NodeType = "MakeVector3",
                Title = "Make Vector3",
                Category = "Vector",
                ColorHex = "#FFCC00",
                Size = new Vector2(200, 100)
            };
            node.AddInputPin("X", GraphPinDataType.Float, "0");
            node.AddInputPin("Y", GraphPinDataType.Float, "0");
            node.AddInputPin("Z", GraphPinDataType.Float, "0");
            node.AddOutputPin("Result", GraphPinDataType.Vector3);
            return node;
        }

        // ─── 旋转节点工厂 ───

        private static GraphNode MakeRotatorNode(string name)
        {
            var node = new GraphNode
            {
                NodeType = "MakeRotator",
                Title = "Make Rotator",
                Category = "Rotation",
                ColorHex = "#9966FF",
                Size = new Vector2(200, 100)
            };
            node.AddInputPin("Pitch", GraphPinDataType.Float, "0");
            node.AddInputPin("Yaw", GraphPinDataType.Float, "0");
            node.AddInputPin("Roll", GraphPinDataType.Float, "0");
            node.AddOutputPin("Result", GraphPinDataType.Rotator);
            return node;
        }

        private static GraphNode RotatorFromAxisNode(string name)
        {
            var node = new GraphNode
            {
                NodeType = "RotatorFromAxis",
                Title = "Rotator from Axis Angle",
                Category = "Rotation",
                ColorHex = "#9966FF",
                Size = new Vector2(220, 80)
            };
            node.AddInputPin("Axis", GraphPinDataType.Vector3, "(0,1,0)");
            node.AddInputPin("Angle", GraphPinDataType.Float, "0");
            node.AddOutputPin("Result", GraphPinDataType.Rotator);
            return node;
        }

        private static GraphNode AngleBetweenNode(string name)
        {
            var node = new GraphNode
            {
                NodeType = "Angle",
                Title = "Angle Between",
                Category = "Rotation",
                ColorHex = "#9966FF",
                Size = new Vector2(220, 80)
            };
            node.AddInputPin("A", GraphPinDataType.Vector3, "(0,0,1)");
            node.AddInputPin("B", GraphPinDataType.Vector3, "(0,0,1)");
            node.AddOutputPin("Result", GraphPinDataType.Float);
            return node;
        }

        private static GraphNode BinaryRotatorNode(string type, string name)
        {
            var node = new GraphNode
            {
                NodeType = type,
                Title = type,
                Category = "Rotation",
                ColorHex = "#9966FF",
                Size = new Vector2(220, 80)
            };
            node.AddInputPin("A", GraphPinDataType.Rotator, "(0,0,0)");
            node.AddInputPin("B", GraphPinDataType.Rotator, "(0,0,0)");
            node.AddOutputPin("Result", GraphPinDataType.Rotator);
            return node;
        }

        private static GraphNode LookAtRotatorNode(string name)
        {
            var node = new GraphNode
            {
                NodeType = "RotatorFromLookAt",
                Title = "Look At Rotation",
                Category = "Rotation",
                ColorHex = "#9966FF",
                Size = new Vector2(220, 80)
            };
            node.AddInputPin("Start", GraphPinDataType.Vector3, "(0,0,0)");
            node.AddInputPin("Target", GraphPinDataType.Vector3, "(0,0,1)");
            node.AddOutputPin("Result", GraphPinDataType.Rotator);
            return node;
        }

        private static GraphNode RotatorComponentNode(string type, string comp, string name)
        {
            var node = new GraphNode
            {
                NodeType = type,
                Title = $"Get {comp}",
                Category = "Rotation",
                ColorHex = "#9966FF",
                Size = new Vector2(200, 80)
            };
            node.AddInputPin("Rotator", GraphPinDataType.Rotator, "(0,0,0)");
            node.AddOutputPin("Result", GraphPinDataType.Float);
            return node;
        }

        private static GraphNode RotatorToQuatNode(string name)
        {
            var node = new GraphNode
            {
                NodeType = "ToQuaternion",
                Title = "Rotator -> Quaternion",
                Category = "Rotation",
                ColorHex = "#9966FF",
                Size = new Vector2(220, 80)
            };
            node.AddInputPin("Rotator", GraphPinDataType.Rotator, "(0,0,0)");
            node.AddOutputPin("Result", GraphPinDataType.Quaternion);
            return node;
        }

        private static GraphNode QuatToRotatorNode(string name)
        {
            var node = new GraphNode
            {
                NodeType = "FromQuaternion",
                Title = "Quaternion -> Rotator",
                Category = "Rotation",
                ColorHex = "#9966FF",
                Size = new Vector2(220, 80)
            };
            node.AddInputPin("Quaternion", GraphPinDataType.Quaternion, "(0,0,0,1)");
            node.AddOutputPin("Result", GraphPinDataType.Rotator);
            return node;
        }

        private static GraphNode SlerpNode(string name)
        {
            var node = new GraphNode
            {
                NodeType = "Slerp",
                Title = "Slerp (Spherical Lerp)",
                Category = "Rotation",
                ColorHex = "#9966FF",
                Size = new Vector2(220, 100)
            };
            node.AddInputPin("A", GraphPinDataType.Quaternion, "(0,0,0,1)");
            node.AddInputPin("B", GraphPinDataType.Quaternion, "(0,0,0,1)");
            node.AddInputPin("Alpha", GraphPinDataType.Float, "0");
            node.AddOutputPin("Result", GraphPinDataType.Quaternion);
            return node;
        }

        // ─── 变换节点工厂 ───

        private static GraphNode MakeTransformNode(string name)
        {
            var node = new GraphNode
            {
                NodeType = "MakeTransform",
                Title = "Make Transform",
                Category = "Transform",
                ColorHex = "#CC6600",
                Size = new Vector2(200, 120)
            };
            node.AddInputPin("Location", GraphPinDataType.Vector3, "(0,0,0)");
            node.AddInputPin("Rotation", GraphPinDataType.Rotator, "(0,0,0)");
            node.AddInputPin("Scale", GraphPinDataType.Vector3, "(1,1,1)");
            node.AddOutputPin("Result", GraphPinDataType.Transform);
            return node;
        }

        private static GraphNode BreakTransformNode(string name)
        {
            var node = new GraphNode
            {
                NodeType = "BreakTransform",
                Title = "Break Transform",
                Category = "Transform",
                ColorHex = "#CC6600",
                Size = new Vector2(200, 120)
            };
            node.AddInputPin("Transform", GraphPinDataType.Transform);
            node.AddOutputPin("Location", GraphPinDataType.Vector3);
            node.AddOutputPin("Rotation", GraphPinDataType.Rotator);
            node.AddOutputPin("Scale", GraphPinDataType.Vector3);
            return node;
        }

        private static GraphNode TransformLocationNode(string name)
        {
            var node = new GraphNode
            {
                NodeType = "TransformLocation",
                Title = "Transform Location",
                Category = "Transform",
                ColorHex = "#CC6600",
                Size = new Vector2(220, 80)
            };
            node.AddInputPin("Transform", GraphPinDataType.Transform);
            node.AddInputPin("Location", GraphPinDataType.Vector3, "(0,0,0)");
            node.AddOutputPin("Result", GraphPinDataType.Vector3);
            return node;
        }

        private static GraphNode TransformRotationNode(string name)
        {
            var node = new GraphNode
            {
                NodeType = "TransformRotation",
                Title = "Transform Rotation",
                Category = "Transform",
                ColorHex = "#CC6600",
                Size = new Vector2(220, 80)
            };
            node.AddInputPin("Transform", GraphPinDataType.Transform);
            node.AddInputPin("Rotation", GraphPinDataType.Rotator, "(0,0,0)");
            node.AddOutputPin("Result", GraphPinDataType.Rotator);
            return node;
        }

        private static GraphNode TransformDirectionNode(string name)
        {
            var node = new GraphNode
            {
                NodeType = "TransformDirection",
                Title = "Transform Direction",
                Category = "Transform",
                ColorHex = "#CC6600",
                Size = new Vector2(220, 80)
            };
            node.AddInputPin("Transform", GraphPinDataType.Transform);
            node.AddInputPin("Direction", GraphPinDataType.Vector3, "(0,0,1)");
            node.AddOutputPin("Result", GraphPinDataType.Vector3);
            return node;
        }

        private static GraphNode InverseTransformNode(string name)
        {
            var node = new GraphNode
            {
                NodeType = "InverseTransform",
                Title = "Inverse Transform",
                Category = "Transform",
                ColorHex = "#CC6600",
                Size = new Vector2(200, 80)
            };
            node.AddInputPin("Transform", GraphPinDataType.Transform);
            node.AddOutputPin("Result", GraphPinDataType.Transform);
            return node;
        }

        private static GraphNode CombineTransformsNode(string name)
        {
            var node = new GraphNode
            {
                NodeType = "CombineTransforms",
                Title = "Combine Transforms",
                Category = "Transform",
                ColorHex = "#CC6600",
                Size = new Vector2(220, 80)
            };
            node.AddInputPin("A", GraphPinDataType.Transform);
            node.AddInputPin("B", GraphPinDataType.Transform);
            node.AddOutputPin("Result", GraphPinDataType.Transform);
            return node;
        }

        private static GraphNode TransformGetComponentNode(string type, string comp, GraphPinDataType dataType, string name)
        {
            var node = new GraphNode
            {
                NodeType = type,
                Title = $"Get {comp}",
                Category = "Transform",
                ColorHex = "#CC6600",
                Size = new Vector2(200, 80)
            };
            node.AddInputPin("Transform", GraphPinDataType.Transform);
            node.AddOutputPin(comp, dataType);
            return node;
        }

        // ─── 逻辑节点工厂 ───

        private static GraphNode BinaryBoolNode(string type, string op, string name)
        {
            var node = new GraphNode
            {
                NodeType = type,
                Title = $"{type}",
                Category = "Logic",
                ColorHex = "#00CC66",
                Size = new Vector2(220, 80)
            };
            node.AddInputPin("A", GraphPinDataType.Bool, "false");
            node.AddInputPin("B", GraphPinDataType.Bool, "false");
            node.AddOutputPin("Result", GraphPinDataType.Bool);
            return node;
        }

        private static GraphNode UnaryBoolNode(string type, string op, string name)
        {
            var node = new GraphNode
            {
                NodeType = type,
                Title = $"{type}",
                Category = "Logic",
                ColorHex = "#00CC66",
                Size = new Vector2(200, 80)
            };
            node.AddInputPin("Input", GraphPinDataType.Bool, "false");
            node.AddOutputPin("Result", GraphPinDataType.Bool);
            return node;
        }

        private static GraphNode BranchNode(string name)
        {
            var node = new GraphNode
            {
                NodeType = "Branch",
                Title = "Branch",
                Category = "Logic",
                ColorHex = "#808080",
                Size = new Vector2(200, 100)
            };
            node.AddExecInput();
            node.AddInputPin("Condition", GraphPinDataType.Bool, "false");
            node.AddExecOutput("True");
            node.AddExecOutput("False");
            return node;
        }

        private static GraphNode SwitchNode(string type, GraphPinDataType switchType, string name)
        {
            var node = new GraphNode
            {
                NodeType = type,
                Title = type,
                Category = "Logic",
                ColorHex = "#808080",
                Size = new Vector2(200, 120)
            };
            node.AddExecInput();
            node.AddInputPin("Selection", switchType);
            node.AddExecOutput("Default");
            // 可添加更多分支输出
            for (int i = 0; i < 5; i++)
            {
                node.AddExecOutput($"Case_{i}");
            }
            return node;
        }

        private static GraphNode SequenceNode(string name)
        {
            var node = new GraphNode
            {
                NodeType = "Sequence",
                Title = "Sequence",
                Category = "Logic",
                ColorHex = "#808080",
                Size = new Vector2(200, 160)
            };
            node.AddExecInput();
            for (int i = 0; i < 6; i++)
            {
                node.AddExecOutput($"Then {i}");
            }
            return node;
        }

        private static GraphNode MultiGateNode(string name)
        {
            var node = new GraphNode
            {
                NodeType = "MultiGate",
                Title = "Multi Gate",
                Category = "Logic",
                ColorHex = "#808080",
                Size = new Vector2(200, 140)
            };
            node.AddExecInput();
            node.AddInputPin("Reset", GraphPinDataType.Bool, "false");
            node.AddInputPin("Loop", GraphPinDataType.Bool, "false");
            for (int i = 0; i < 5; i++)
            {
                node.AddExecOutput($"Gate {i}");
            }
            return node;
        }

        private static GraphNode DoOnceNode(string name)
        {
            var node = new GraphNode
            {
                NodeType = "DoOnce",
                Title = "Do Once",
                Category = "Logic",
                ColorHex = "#808080",
                Size = new Vector2(200, 100)
            };
            node.AddExecInput();
            node.AddExecOutput("Exec");
            node.AddOutputPin("IsFirst", GraphPinDataType.Bool);
            node.AddInputPin("Reset", GraphPinDataType.Exec, isOptional: true);
            return node;
        }

        private static GraphNode DoNNode(string name)
        {
            var node = new GraphNode
            {
                NodeType = "DoN",
                Title = "Do N",
                Category = "Logic",
                ColorHex = "#808080",
                Size = new Vector2(220, 120)
            };
            node.AddExecInput();
            node.AddInputPin("N", GraphPinDataType.Int, "1");
            node.AddExecOutput("Exec");
            node.AddOutputPin("Counter", GraphPinDataType.Int);
            node.AddInputPin("Reset", GraphPinDataType.Exec, isOptional: true);
            return node;
        }

        private static GraphNode FlipFlopNode(string name)
        {
            var node = new GraphNode
            {
                NodeType = "FlipFlop",
                Title = "Flip Flop",
                Category = "Logic",
                ColorHex = "#808080",
                Size = new Vector2(200, 120)
            };
            node.AddExecInput();
            node.AddExecOutput("A");
            node.AddExecOutput("B");
            node.AddOutputPin("Is A", GraphPinDataType.Bool);
            node.AddInputPin("Reset", GraphPinDataType.Exec, isOptional: true);
            return node;
        }

        private static GraphNode GateNode(string name)
        {
            var node = new GraphNode
            {
                NodeType = "Gate",
                Title = "Gate",
                Category = "Logic",
                ColorHex = "#808080",
                Size = new Vector2(200, 120)
            };
            node.AddExecInput("Enter");
            node.AddExecInput("Open");
            node.AddExecInput("Close");
            node.AddExecInput("Toggle");
            node.AddExecOutput("Exit");
            return node;
        }

        private static GraphNode MultiplexerNode(string name)
        {
            var node = new GraphNode
            {
                NodeType = "Multiplexer",
                Title = "Multiplexer",
                Category = "Logic",
                ColorHex = "#808080",
                Size = new Vector2(220, 140)
            };
            node.AddInputPin("Index", GraphPinDataType.Int, "0");
            for (int i = 0; i < 8; i++)
            {
                node.AddInputPin($"Input_{i}", GraphPinDataType.Wildcard);
            }
            node.AddOutputPin("Output", GraphPinDataType.Wildcard);
            return node;
        }

        private static GraphNode DemuxNode(string name)
        {
            var node = new GraphNode
            {
                NodeType = "Demux",
                Title = "Demux",
                Category = "Logic",
                ColorHex = "#808080",
                Size = new Vector2(220, 140)
            };
            node.AddInputPin("Index", GraphPinDataType.Int, "0");
            node.AddInputPin("Input", GraphPinDataType.Wildcard);
            for (int i = 0; i < 8; i++)
            {
                node.AddOutputPin($"Output_{i}", GraphPinDataType.Wildcard);
            }
            return node;
        }

        // ─── 流程控制节点工厂 ───

        private static GraphNode EventNodeFactory(string type, string title, List<(string, GraphPinDataType)> outputs)
        {
            var node = new GraphNode
            {
                NodeType = type,
                Title = title,
                Category = "Event",
                ColorHex = "#CC0000",
                Size = new Vector2(280, 60 + outputs.Count * 30),
                IsCompilable = true
            };
            foreach (var (name, dataType) in outputs)
            {
                node.AddOutputPin(name, dataType);
            }
            return node;
        }

        private static GraphNode FunctionDefNode(string name)
        {
            var node = new GraphNode
            {
                NodeType = "Function",
                Title = "Function Definition",
                Category = "Function",
                ColorHex = "#0066CC",
                Size = new Vector2(220, 100)
            };
            node.AddExecInput();
            node.AddExecOutput("Return");
            return node;
        }

        private static GraphNode CallFunctionNode(string name)
        {
            var node = new GraphNode
            {
                NodeType = "CallFunction",
                Title = "Call Function",
                Category = "Function",
                ColorHex = "#0066CC",
                Size = new Vector2(220, 100)
            };
            node.AddExecInput();
            node.AddInputPin("FunctionName", GraphPinDataType.String);
            node.AddExecOutput("Return");
            return node;
        }

        private static GraphNode ForEachLoopNode(string name)
        {
            var node = new GraphNode
            {
                NodeType = "ForEachLoop",
                Title = "For Each Loop",
                Category = "FlowControl",
                ColorHex = "#808080",
                Size = new Vector2(220, 120)
            };
            node.AddExecInput();
            node.AddInputPin("Array", GraphPinDataType.Array);
            node.AddExecOutput("LoopBody");
            node.AddExecOutput("Completed");
            node.AddOutputPin("ArrayElement", GraphPinDataType.Wildcard);
            node.AddOutputPin("ArrayIndex", GraphPinDataType.Int);
            return node;
        }

        private static GraphNode ForLoopNode(string name)
        {
            var node = new GraphNode
            {
                NodeType = "ForLoop",
                Title = "For Loop",
                Category = "FlowControl",
                ColorHex = "#808080",
                Size = new Vector2(220, 120)
            };
            node.AddExecInput();
            node.AddInputPin("Start", GraphPinDataType.Int, "0");
            node.AddInputPin("End", GraphPinDataType.Int, "10");
            node.AddExecOutput("LoopBody");
            node.AddExecOutput("Completed");
            node.AddOutputPin("Index", GraphPinDataType.Int);
            return node;
        }

        private static GraphNode WhileLoopNode(string name)
        {
            var node = new GraphNode
            {
                NodeType = "WhileLoop",
                Title = "While Loop",
                Category = "FlowControl",
                ColorHex = "#808080",
                Size = new Vector2(200, 100)
            };
            node.AddExecInput();
            node.AddInputPin("Condition", GraphPinDataType.Bool, "true");
            node.AddExecOutput("LoopBody");
            node.AddExecOutput("Completed");
            return node;
        }

        private static GraphNode DoWhileNode(string name)
        {
            var node = new GraphNode
            {
                NodeType = "DoWhile",
                Title = "Do While",
                Category = "FlowControl",
                ColorHex = "#808080",
                Size = new Vector2(200, 100)
            };
            node.AddExecInput();
            node.AddExecOutput("LoopBody");
            node.AddExecOutput("Completed");
            node.AddInputPin("Condition", GraphPinDataType.Bool, "true");
            return node;
        }

        private static GraphNode LoopControlNode(string type, string title, string name)
        {
            var node = new GraphNode
            {
                NodeType = type,
                Title = title,
                Category = "FlowControl",
                ColorHex = "#808080",
                Size = new Vector2(180, 60)
            };
            node.AddExecInput();
            return node;
        }

        private static GraphNode DelayNode(string name)
        {
            var node = new GraphNode
            {
                NodeType = "Delay",
                Title = "Delay",
                Category = "FlowControl",
                ColorHex = "#666666",
                Size = new Vector2(200, 80)
            };
            node.AddExecInput();
            node.AddInputPin("Duration", GraphPinDataType.Float, "1.0");
            node.AddExecOutput("Completed");
            return node;
        }

        private static GraphNode SetTimerNode(string name)
        {
            var node = new GraphNode
            {
                NodeType = "SetTimer",
                Title = "Set Timer",
                Category = "FlowControl",
                ColorHex = "#666666",
                Size = new Vector2(220, 100)
            };
            node.AddExecInput();
            node.AddInputPin("FunctionName", GraphPinDataType.String);
            node.AddInputPin("Interval", GraphPinDataType.Float, "1.0");
            node.AddInputPin("bLoop", GraphPinDataType.Bool, "false");
            node.AddExecOutput("Return");
            node.AddOutputPin("TimerHandle", GraphPinDataType.Object);
            return node;
        }

        // ─── 变量节点工厂 ───

        private static GraphNode GetVariableNode(string name)
        {
            var node = new GraphNode
            {
                NodeType = "GetVariable",
                Title = "Get Variable",
                Category = "Variable",
                ColorHex = "#00CCCC",
                Size = new Vector2(200, 80)
            };
            node.AddOutputPin("Value", GraphPinDataType.Wildcard);
            return node;
        }

        private static GraphNode SetVariableNode(string name)
        {
            var node = new GraphNode
            {
                NodeType = "SetVariable",
                Title = "Set Variable",
                Category = "Variable",
                ColorHex = "#00CCCC",
                Size = new Vector2(200, 80)
            };
            node.AddExecInput();
            node.AddInputPin("Value", GraphPinDataType.Wildcard);
            node.AddExecOutput("Then");
            node.AddOutputPin("Output", GraphPinDataType.Wildcard);
            return node;
        }

        private static GraphNode GetMemberNode(string name)
        {
            var node = new GraphNode
            {
                NodeType = "GetMember",
                Title = "Get Member",
                Category = "Variable",
                ColorHex = "#00CCCC",
                Size = new Vector2(220, 80)
            };
            node.AddInputPin("Target", GraphPinDataType.Object);
            node.AddInputPin("MemberName", GraphPinDataType.String);
            node.AddOutputPin("Value", GraphPinDataType.Wildcard);
            return node;
        }

        private static GraphNode SetMemberNode(string name)
        {
            var node = new GraphNode
            {
                NodeType = "SetMember",
                Title = "Set Member",
                Category = "Variable",
                ColorHex = "#00CCCC",
                Size = new Vector2(220, 80)
            };
            node.AddExecInput();
            node.AddInputPin("Target", GraphPinDataType.Object);
            node.AddInputPin("MemberName", GraphPinDataType.String);
            node.AddInputPin("Value", GraphPinDataType.Wildcard);
            node.AddExecOutput("Then");
            return node;
        }

        // ─── 数组节点工厂 ───

        private static GraphNode ArrayLengthNode(string name)
        {
            var node = new GraphNode
            {
                NodeType = "ArrayLength",
                Title = "Array Length",
                Category = "Variable",
                ColorHex = "#CC00CC",
                Size = new Vector2(200, 80)
            };
            node.AddInputPin("Array", GraphPinDataType.Array);
            node.AddOutputPin("Length", GraphPinDataType.Int);
            return node;
        }

        private static GraphNode ArrayAddNode(string name)
        {
            var node = new GraphNode
            {
                NodeType = "ArrayAdd",
                Title = "Array Add",
                Category = "Variable",
                ColorHex = "#CC00CC",
                Size = new Vector2(200, 80)
            };
            node.AddInputPin("Target", GraphPinDataType.Array);
            node.AddInputPin("Item", GraphPinDataType.Wildcard);
            node.AddOutputPin("Result", GraphPinDataType.Array);
            return node;
        }

        private static GraphNode ArrayRemoveNode(string name)
        {
            var node = new GraphNode
            {
                NodeType = "ArrayRemove",
                Title = "Array Remove",
                Category = "Variable",
                ColorHex = "#CC00CC",
                Size = new Vector2(200, 80)
            };
            node.AddInputPin("Target", GraphPinDataType.Array);
            node.AddInputPin("Item", GraphPinDataType.Wildcard);
            node.AddOutputPin("Result", GraphPinDataType.Array);
            node.AddOutputPin("Removed", GraphPinDataType.Bool);
            return node;
        }

        private static GraphNode ArrayFindNode(string name)
        {
            var node = new GraphNode
            {
                NodeType = "ArrayFind",
                Title = "Array Find",
                Category = "Variable",
                ColorHex = "#CC00CC",
                Size = new Vector2(200, 80)
            };
            node.AddInputPin("Target", GraphPinDataType.Array);
            node.AddInputPin("Item", GraphPinDataType.Wildcard);
            node.AddOutputPin("Index", GraphPinDataType.Int);
            node.AddOutputPin("Found", GraphPinDataType.Bool);
            return node;
        }

        private static GraphNode ArraySortNode(string name)
        {
            var node = new GraphNode
            {
                NodeType = "ArraySort",
                Title = "Array Sort",
                Category = "Variable",
                ColorHex = "#CC00CC",
                Size = new Vector2(200, 80)
            };
            node.AddInputPin("Target", GraphPinDataType.Array);
            node.AddOutputPin("Result", GraphPinDataType.Array);
            return node;
        }

        private static GraphNode ArrayFilterNode(string name)
        {
            var node = new GraphNode
            {
                NodeType = "ArrayFilter",
                Title = "Array Filter",
                Category = "Variable",
                ColorHex = "#CC00CC",
                Size = new Vector2(200, 80)
            };
            node.AddInputPin("Target", GraphPinDataType.Array);
            node.AddInputPin("Predicate", GraphPinDataType.String);
            node.AddOutputPin("Result", GraphPinDataType.Array);
            return node;
        }

        private static GraphNode ArrayMapNode(string name)
        {
            var node = new GraphNode
            {
                NodeType = "ArrayMap",
                Title = "Array Map",
                Category = "Variable",
                ColorHex = "#CC00CC",
                Size = new Vector2(200, 80)
            };
            node.AddInputPin("Target", GraphPinDataType.Array);
            node.AddInputPin("Transform", GraphPinDataType.String);
            node.AddOutputPin("Result", GraphPinDataType.Array);
            return node;
        }

        private static GraphNode GetArrayElementNode(string name)
        {
            var node = new GraphNode
            {
                NodeType = "GetArrayElement",
                Title = "Get Array Element",
                Category = "Variable",
                ColorHex = "#CC00CC",
                Size = new Vector2(220, 80)
            };
            node.AddExecInput();
            node.AddInputPin("Array", GraphPinDataType.Array);
            node.AddInputPin("Index", GraphPinDataType.Int, "0");
            node.AddExecOutput("Then");
            node.AddOutputPin("Element", GraphPinDataType.Wildcard);
            return node;
        }

        private static GraphNode SetArrayElementNode(string name)
        {
            var node = new GraphNode
            {
                NodeType = "SetArrayElement",
                Title = "Set Array Element",
                Category = "Variable",
                ColorHex = "#CC00CC",
                Size = new Vector2(220, 80)
            };
            node.AddExecInput();
            node.AddInputPin("Array", GraphPinDataType.Array);
            node.AddInputPin("Index", GraphPinDataType.Int, "0");
            node.AddInputPin("Element", GraphPinDataType.Wildcard);
            node.AddExecOutput("Then");
            return node;
        }

        private static GraphNode MakeArrayNode(string name)
        {
            var node = new GraphNode
            {
                NodeType = "MakeArray",
                Title = "Make Array",
                Category = "Variable",
                ColorHex = "#CC00CC",
                Size = new Vector2(200, 120)
            };
            for (int i = 0; i < 5; i++)
            {
                node.AddInputPin($"Element_{i}", GraphPinDataType.Wildcard, isOptional: true);
            }
            node.AddOutputPin("Array", GraphPinDataType.Array);
            return node;
        }

        private static GraphNode BreakArrayNode(string name)
        {
            var node = new GraphNode
            {
                NodeType = "BreakArray",
                Title = "Break Array",
                Category = "Variable",
                ColorHex = "#CC00CC",
                Size = new Vector2(200, 120)
            };
            node.AddInputPin("Array", GraphPinDataType.Array);
            for (int i = 0; i < 5; i++)
            {
                node.AddOutputPin($"Element_{i}", GraphPinDataType.Wildcard);
            }
            return node;
        }

        // ─── 比较节点工厂 ───

        private static GraphNode ComparisonNode(string type, string op, string name)
        {
            var node = new GraphNode
            {
                NodeType = type,
                Title = $"{type} ({op})",
                Category = "Comparison",
                ColorHex = "#00CC66",
                Size = new Vector2(220, 80)
            };
            node.AddInputPin("A", GraphPinDataType.Float, "0");
            node.AddInputPin("B", GraphPinDataType.Float, "0");
            node.AddOutputPin("ReturnValue", GraphPinDataType.Bool);
            return node;
        }

        // ─── 类型转换节点工厂 ───

        private static GraphNode CastNode(string type, GraphPinDataType targetType, string name)
        {
            var node = new GraphNode
            {
                NodeType = type,
                Title = type,
                Category = "Cast",
                ColorHex = "#FFCC00",
                Size = new Vector2(200, 80)
            };
            node.AddInputPin("Input", GraphPinDataType.Wildcard);
            node.AddOutputPin("Output", targetType);
            return node;
        }

        // ─── Actor 节点工厂 ───

        private static GraphNode SpawnActorNode(string name)
        {
            var node = new GraphNode
            {
                NodeType = "SpawnActor",
                Title = "Spawn Actor",
                Category = "Actor",
                ColorHex = "#0000CC",
                Size = new Vector2(240, 100)
            };
            node.AddExecInput();
            node.AddInputPin("ActorClass", GraphPinDataType.String);
            node.AddInputPin("Location", GraphPinDataType.Vector3, "(0,0,0)");
            node.AddInputPin("Rotation", GraphPinDataType.Rotator, "(0,0,0)");
            node.AddExecOutput("Return");
            node.AddOutputPin("SpawnedActor", GraphPinDataType.Object);
            return node;
        }

        private static GraphNode DestroyActorNode(string name)
        {
            var node = new GraphNode
            {
                NodeType = "DestroyActor",
                Title = "Destroy Actor",
                Category = "Actor",
                ColorHex = "#0000CC",
                Size = new Vector2(200, 80)
            };
            node.AddExecInput();
            node.AddInputPin("Target", GraphPinDataType.Object);
            node.AddExecOutput("Then");
            return node;
        }

        private static GraphNode ActorPropertyVec3Node(string type, string prop, string name)
        {
            var node = new GraphNode
            {
                NodeType = type,
                Title = type,
                Category = "Actor",
                ColorHex = "#0000CC",
                Size = new Vector2(220, 80)
            };
            node.AddExecInput();
            node.AddInputPin("Target", GraphPinDataType.Object);
            node.AddExecOutput("Return");
            node.AddOutputPin(prop, GraphPinDataType.Vector3);
            return node;
        }

        private static GraphNode SetActorPropertyVec3Node(string type, string prop, string name)
        {
            var node = new GraphNode
            {
                NodeType = type,
                Title = type,
                Category = "Actor",
                ColorHex = "#0000CC",
                Size = new Vector2(220, 80)
            };
            node.AddExecInput();
            node.AddInputPin("Target", GraphPinDataType.Object);
            node.AddInputPin(prop, GraphPinDataType.Vector3, "(0,0,0)");
            node.AddExecOutput("Return");
            return node;
        }

        private static GraphNode IsActorValidNode(string name)
        {
            var node = new GraphNode
            {
                NodeType = "IsActorValid",
                Title = "Is Actor Valid",
                Category = "Actor",
                ColorHex = "#0000CC",
                Size = new Vector2(200, 80)
            };
            node.AddInputPin("Target", GraphPinDataType.Object);
            node.AddOutputPin("IsValid", GraphPinDataType.Bool);
            return node;
        }

        private static GraphNode GetAllActorsOfClassNode(string name)
        {
            var node = new GraphNode
            {
                NodeType = "GetAllActorsOfClass",
                Title = "Get All Actors of Class",
                Category = "Actor",
                ColorHex = "#0000CC",
                Size = new Vector2(240, 80)
            };
            node.AddInputPin("ActorClass", GraphPinDataType.String);
            node.AddOutputPin("OutActors", GraphPinDataType.Array);
            return node;
        }

        private static GraphNode ForEachActorNode(string name)
        {
            var node = new GraphNode
            {
                NodeType = "ForEachActor",
                Title = "For Each Actor",
                Category = "Actor",
                ColorHex = "#0000CC",
                Size = new Vector2(220, 120)
            };
            node.AddExecInput();
            node.AddInputPin("Actors", GraphPinDataType.Array);
            node.AddExecOutput("LoopBody");
            node.AddExecOutput("Completed");
            node.AddOutputPin("CurrentActor", GraphPinDataType.Object);
            return node;
        }

        // ─── 输入节点工厂 ───

        private static GraphNode InputValueNode(string type, GraphPinDataType outputType, string name)
        {
            var node = new GraphNode
            {
                NodeType = type,
                Title = type,
                Category = "Input",
                ColorHex = "#0066CC",
                Size = new Vector2(220, 80)
            };
            node.AddInputPin("ActionName", GraphPinDataType.String);
            node.AddOutputPin("Value", outputType);
            return node;
        }

        private static GraphNode InputVec2Node(string type, string name)
        {
            var node = new GraphNode
            {
                NodeType = type,
                Title = type,
                Category = "Input",
                ColorHex = "#0066CC",
                Size = new Vector2(200, 80)
            };
            node.AddOutputPin("Value", GraphPinDataType.Vector2);
            return node;
        }

        // ─── 游戏节点工厂 ───

        private static GraphNode GameStateNode(string type, string name)
        {
            var node = new GraphNode
            {
                NodeType = type,
                Title = type,
                Category = "Game",
                ColorHex = "#0066CC",
                Size = new Vector2(200, 80)
            };
            node.AddOutputPin("Value", GraphPinDataType.Object);
            return node;
        }

        private static GraphNode SetGameStateNode(string name)
        {
            var node = new GraphNode
            {
                NodeType = "SetGameState",
                Title = "Set Game State",
                Category = "Game",
                ColorHex = "#0066CC",
                Size = new Vector2(200, 80)
            };
            node.AddExecInput();
            node.AddInputPin("State", GraphPinDataType.String);
            node.AddExecOutput("Then");
            return node;
        }

        private static GraphNode ApplyDamageNode(string name)
        {
            var node = new GraphNode
            {
                NodeType = "ApplyDamage",
                Title = "Apply Damage",
                Category = "Game",
                ColorHex = "#0066CC",
                Size = new Vector2(240, 100)
            };
            node.AddExecInput();
            node.AddInputPin("Target", GraphPinDataType.Object);
            node.AddInputPin("BaseDamage", GraphPinDataType.Float, "10");
            node.AddInputPin("DamageType", GraphPinDataType.String);
            node.AddExecOutput("Then");
            return node;
        }

        private static GraphNode PrintStringNode(string name)
        {
            var node = new GraphNode
            {
                NodeType = "PrintString",
                Title = "Print String",
                Category = "Game",
                ColorHex = "#9900CC",
                Size = new Vector2(220, 100)
            };
            node.AddExecInput();
            node.AddInputPin("String", GraphPinDataType.String, "Hello");
            node.AddInputPin("bPrintToScreen", GraphPinDataType.Bool, "true");
            node.AddInputPin("bPrintToLog", GraphPinDataType.Bool, "true");
            node.AddInputPin("Color", GraphPinDataType.Color, "(1,1,1,1)");
            node.AddInputPin("Duration", GraphPinDataType.Float, "2.0");
            node.AddExecOutput("Then");
            return node;
        }

        // ─────────────────────────────────────────────────────────────────────────
        //  蓝图节点执行器（运行时节点求值）
        // ─────────────────────────────────────────────────────────────────────────

    }

    /// <summary>
    /// 蓝图节点执行器：运行时求值节点并返回输出值。
    /// </summary>
    public static class NodeScriptNodeExecutor
    {
        /// <summary>
        /// 执行指定节点并计算输出值。
        /// </summary>
        public static Dictionary<string, object?> Evaluate(GraphNode node, Dictionary<string, object?> inputs)
        {
            var outputs = new Dictionary<string, object?>();

            switch (node.NodeType)
            {
                // 数学节点
                case "Add":
                    outputs["Result"] = ToFloat(inputs, "A") + ToFloat(inputs, "B");
                    break;
                case "Subtract":
                    outputs["Result"] = ToFloat(inputs, "A") - ToFloat(inputs, "B");
                    break;
                case "Multiply":
                    outputs["Result"] = ToFloat(inputs, "A") * ToFloat(inputs, "B");
                    break;
                case "Divide":
                    var divisor = ToFloat(inputs, "B");
                    outputs["Result"] = divisor != 0 ? ToFloat(inputs, "A") / divisor : 0f;
                    break;
                case "Modulo":
                    outputs["Result"] = (int)ToFloat(inputs, "A") % (int)Math.Max(1, ToFloat(inputs, "B"));
                    break;
                case "Power":
                    outputs["Result"] = MathF.Pow(ToFloat(inputs, "A"), ToFloat(inputs, "B"));
                    break;
                case "Sqrt":
                    outputs["Result"] = MathF.Sqrt(Math.Max(0, ToFloat(inputs, "Input")));
                    break;
                case "Abs":
                    outputs["Result"] = MathF.Abs(ToFloat(inputs, "Input"));
                    break;
                case "Negate":
                    outputs["Result"] = -ToFloat(inputs, "Input");
                    break;
                case "Sign":
                    outputs["Result"] = MathF.Sign(ToFloat(inputs, "Input"));
                    break;
                case "Sin":
                    outputs["Result"] = MathF.Sin(ToFloat(inputs, "Input"));
                    break;
                case "Cos":
                    outputs["Result"] = MathF.Cos(ToFloat(inputs, "Input"));
                    break;
                case "Tan":
                    outputs["Result"] = MathF.Tan(ToFloat(inputs, "Input"));
                    break;
                case "Atan2":
                    outputs["Result"] = MathF.Atan2(ToFloat(inputs, "A"), ToFloat(inputs, "B"));
                    break;
                case "Round":
                    outputs["Result"] = MathF.Round(ToFloat(inputs, "Input"));
                    break;
                case "Ceil":
                    outputs["Result"] = MathF.Ceiling(ToFloat(inputs, "Input"));
                    break;
                case "Floor":
                    outputs["Result"] = MathF.Floor(ToFloat(inputs, "Input"));
                    break;
                case "Fract":
                    var val = ToFloat(inputs, "Input");
                    outputs["Result"] = val - MathF.Floor(val);
                    break;
                case "Min":
                    outputs["Result"] = MathF.Min(ToFloat(inputs, "A"), ToFloat(inputs, "B"));
                    break;
                case "Max":
                    outputs["Result"] = MathF.Max(ToFloat(inputs, "A"), ToFloat(inputs, "B"));
                    break;
                case "Clamp":
                    var cv = ToFloat(inputs, "Value");
                    outputs["Result"] = Math.Clamp(cv, ToFloat(inputs, "Min"), ToFloat(inputs, "Max"));
                    break;
                case "Lerp":
                    var la = ToFloat(inputs, "A");
                    var lb = ToFloat(inputs, "B");
                    var lalpha = ToFloat(inputs, "Alpha");
                    outputs["Result"] = la + (lb - la) * lalpha;
                    break;

                // 向量节点
                case "Vector3Add":
                    outputs["Result"] = ToVec3(inputs, "A") + ToVec3(inputs, "B");
                    break;
                case "Vector3Subtract":
                    outputs["Result"] = ToVec3(inputs, "A") - ToVec3(inputs, "B");
                    break;
                case "Vector3Scale":
                    outputs["Result"] = ToVec3(inputs, "Vector") * ToFloat(inputs, "Scale");
                    break;
                case "CrossProduct":
                    outputs["Result"] = Vector3.Cross(ToVec3(inputs, "A"), ToVec3(inputs, "B"));
                    break;
                case "DotProduct":
                    outputs["Result"] = Vector3.Dot(ToVec3(inputs, "A"), ToVec3(inputs, "B"));
                    break;
                case "Normalize":
                {
                    var v = ToVec3(inputs, "Input");
                    var len = v.Length();
                    outputs["Result"] = len > 0.0001f ? v / len : Vector3.Zero;
                    break;
                }
                case "Vector3Length":
                    outputs["Result"] = ToVec3(inputs, "Vector").Length();
                    break;
                case "Vector3Distance":
                    outputs["Result"] = (ToVec3(inputs, "A") - ToVec3(inputs, "B")).Length();
                    break;
                case "BreakVector3":
                    var bv = ToVec3(inputs, "Vector");
                    outputs["X"] = bv.X;
                    outputs["Y"] = bv.Y;
                    outputs["Z"] = bv.Z;
                    break;
                case "MakeVector3":
                    outputs["Result"] = new Vector3(ToFloat(inputs, "X"), ToFloat(inputs, "Y"), ToFloat(inputs, "Z"));
                    break;

                // 逻辑节点
                case "AND":
                    outputs["Result"] = ToBool(inputs, "A") && ToBool(inputs, "B");
                    break;
                case "OR":
                    outputs["Result"] = ToBool(inputs, "A") || ToBool(inputs, "B");
                    break;
                case "NOT":
                    outputs["Result"] = !ToBool(inputs, "Input");
                    break;
                case "XOR":
                    outputs["Result"] = ToBool(inputs, "A") ^ ToBool(inputs, "B");
                    break;

                // 比较节点
                case "Equal":
                    outputs["ReturnValue"] = ToFloat(inputs, "A") == ToFloat(inputs, "B");
                    break;
                case "NotEqual":
                    outputs["ReturnValue"] = ToFloat(inputs, "A") != ToFloat(inputs, "B");
                    break;
                case "Greater":
                    outputs["ReturnValue"] = ToFloat(inputs, "A") > ToFloat(inputs, "B");
                    break;
                case "Less":
                    outputs["ReturnValue"] = ToFloat(inputs, "A") < ToFloat(inputs, "B");
                    break;
                case "GreaterEqual":
                    outputs["ReturnValue"] = ToFloat(inputs, "A") >= ToFloat(inputs, "B");
                    break;
                case "LessEqual":
                    outputs["ReturnValue"] = ToFloat(inputs, "A") <= ToFloat(inputs, "B");
                    break;

                default:
                    // 未知节点类型，返回空输出
                    break;
            }

            return outputs;
        }

        // 类型转换辅助
        private static float ToFloat(Dictionary<string, object?> inputs, string key)
        {
            if (!inputs.TryGetValue(key, out var val) || val == null) return 0f;
            return Convert.ToSingle(val);
        }

        private static int ToInt(Dictionary<string, object?> inputs, string key)
        {
            if (!inputs.TryGetValue(key, out var val) || val == null) return 0;
            return Convert.ToInt32(val);
        }

        private static bool ToBool(Dictionary<string, object?> inputs, string key)
        {
            if (!inputs.TryGetValue(key, out var val) || val == null) return false;
            return Convert.ToBoolean(val);
        }

        private static string ToString(Dictionary<string, object?> inputs, string key)
        {
            if (!inputs.TryGetValue(key, out var val) || val == null) return string.Empty;
            return val.ToString() ?? string.Empty;
        }

        private static Vector3 ToVec3(Dictionary<string, object?> inputs, string key)
        {
            if (!inputs.TryGetValue(key, out var val) || val == null) return Vector3.Zero;
            if (val is Vector3 v3) return v3;
            return Vector3.Zero;
        }
    }
}
