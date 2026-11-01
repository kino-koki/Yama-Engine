using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.Json;

namespace YamaEngine.VisualScript
{
    // ════════════════════════════════════════════════════════════════════════════
    //  yama引擎 · 动画蓝图系统 (Animation NodeScript System)
    //  ────────────────────────────────────────────────────────────────────────────
    //  实现完整的动画蓝图可视化系统：
    //    · AnimPinDataType       — 动画数据引脚类型
    //    · AnimNodeScriptNode     — 动画蓝图节点基类
    //    · AnimBP_EventNodes     — 事件节点（初始化/更新/后评估）
    //    · AnimBP_BoneNodes      — 骨骼操作节点（获取/设置骨骼变换/位置/旋转）
    //    · AnimBP_CurveNodes     — 动画曲线节点（获取/设置曲线值）
    //    · AnimBP_SocketNodes    — 插槽节点（获取插槽变换/位置/旋转）
    //    · AnimBP_MathNodes      — 动画参数计算节点（乘法/钳制/插值）
    //    · AnimBP_TransformNodes — 直接骨骼控制节点（TransformBone/ForwardSolve）
    //    · AnimBP_IKNodes        — IK 求解器节点（TwoBoneIK/LookAtIK/SpringIK）
    //    · AnimBP_BlendNodes     — 混合节点（ApplyAdditive/BlendPoses/LayeredBoneBlend）
    //    · AnimNodeScript         — 动画蓝图（事件图/更新图/AnimGraph/变量/函数）
    //    · AnimGraphInstance     — 运行时动画蓝图实例
    //    · AnimBPDebugger        — 动画蓝图调试器
    //  作者：yama引擎组
    // ════════════════════════════════════════════════════════════════════════════

    // ─────────────────────────────────────────────────────────────────────────────
    //  AnimPinDataType - 动画数据引脚类型
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 动画蓝图专用引脚数据类型。
    /// </summary>
    public enum AnimPinDataType
    {
        /// <summary>执行流引脚。</summary>
        Exec,
        /// <summary>位姿（Pose，动画核心数据类型）。</summary>
        Pose,
        /// <summary>组件空间位姿。</summary>
        ComponentSpacePose,
        /// <summary>局部空间位姿。</summary>
        LocalSpacePose,
        /// <summary>浮点动画曲线。</summary>
        FloatCurve,
        /// <summary>骨骼变换。</summary>
        TransformBone,
        /// <summary>角度 (Theta)。</summary>
        Theta,
        /// <summary>布尔值。</summary>
        Bool,
        /// <summary>浮点数。</summary>
        Float,
        /// <summary>整数。</summary>
        Int,
        /// <summary>字符串。</summary>
        String,
        /// <summary>向量3。</summary>
        Vector3,
        /// <summary>旋转体。</summary>
        Rotator,
        /// <summary>四元数。</summary>
        Quaternion,
        /// <summary>变换。</summary>
        Transform,
        /// <summary>数组。</summary>
        Array,
        /// <summary>权重数组（蒙皮权重）。</summary>
        WeightArray,
        /// <summary>骨骼索引。</summary>
        BoneIndex,
        /// <summary>动画资产引用。</summary>
        AnimAsset,
        /// <summary>任意类型。</summary>
        Wildcard
    }

    /// <summary>
    /// 动画蓝图节点分类。
    /// </summary>
    public enum AnimNodeCategory
    {
        /// <summary>事件。</summary>
        Event,
        /// <summary>骨骼操作。</summary>
        Bone,
        /// <summary>曲线。</summary>
        Curve,
        /// <summary>插槽。</summary>
        Socket,
        /// <summary>数学。</summary>
        Math,
        /// <summary>变换控制。</summary>
        Transform,
        /// <summary>IK 求解器。</summary>
        IK,
        /// <summary>混合。</summary>
        Blend,
        /// <summary>状态机。</summary>
        StateMachine,
        /// <summary>动画播放。</summary>
        Playback,
        /// <summary>蒙皮网格。</summary>
        Mesh,
        /// <summary>数学常量。</summary>
        Constants
    }

    // ─────────────────────────────────────────────────────────────────────────────
    //  AnimNodeScriptNode - 动画蓝图节点基类
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 动画蓝图节点引脚。
    /// </summary>
    [Serializable]
    public class AnimNodeScriptPin
    {
        /// <summary>引脚 ID。</summary>
        public Guid Id { get; set; } = Guid.NewGuid();

        /// <summary>所属节点 ID。</summary>
        public Guid OwnerNodeId { get; set; }

        /// <summary>引脚方向。</summary>
        public GraphPinDirection Direction { get; set; }

        /// <summary>引脚数据类型。</summary>
        public AnimPinDataType DataType { get; set; }

        /// <summary>引脚名称。</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>引脚默认值。</summary>
        public string? DefaultValue { get; set; }

        /// <summary>连接列表。</summary>
        public List<Guid> ConnectionIds { get; set; } = new List<Guid>();

        /// <summary>引脚显示顺序。</summary>
        public int OrderIndex { get; set; }

        /// <summary>是否可选。</summary>
        public bool IsOptional { get; set; }

        /// <summary>是否被连接。</summary>
        [JsonIgnore]
        public bool IsConnected => ConnectionIds.Count > 0;

        /// <summary>创建输入引脚。</summary>
        public static AnimNodeScriptPin CreateInput(string name, AnimPinDataType type, string? defaultValue = null, int order = 0)
        {
            return new AnimNodeScriptPin { Name = name, Direction = GraphPinDirection.Input, DataType = type, DefaultValue = defaultValue, OrderIndex = order };
        }

        /// <summary>创建输出引脚。</summary>
        public static AnimNodeScriptPin CreateOutput(string name, AnimPinDataType type, int order = 0)
        {
            return new AnimNodeScriptPin { Name = name, Direction = GraphPinDirection.Output, DataType = type, OrderIndex = order };
        }
    }

    /// <summary>
    /// 动画蓝图连接。
    /// </summary>
    [Serializable]
    public class AnimNodeScriptConnection
    {
        /// <summary>连接 ID。</summary>
        public Guid Id { get; set; } = Guid.NewGuid();

        /// <summary>源节点 ID。</summary>
        public Guid SourceNodeId { get; set; }

        /// <summary>源引脚 ID。</summary>
        public Guid SourcePinId { get; set; }

        /// <summary>目标节点 ID。</summary>
        public Guid TargetNodeId { get; set; }

        /// <summary>目标引脚 ID。</summary>
        public Guid TargetPinId { get; set; }

        /// <summary>连接颜色。</summary>
        public string ColorHex { get; set; } = "#FFFFFF";

        /// <summary>数据类型。</summary>
        public AnimPinDataType DataType { get; set; }
    }

    /// <summary>
    /// 动画蓝图节点基类。
    /// </summary>
    [Serializable]
    public class AnimNodeScriptNode
    {
        /// <summary>节点 ID。</summary>
        public Guid Id { get; set; } = Guid.NewGuid();

        /// <summary>节点类型名称。</summary>
        public string NodeType { get; set; } = "AnimNode";

        /// <summary>节点标题。</summary>
        public string Title { get; set; } = "Anim Node";

        /// <summary>节点分类。</summary>
        public AnimNodeCategory Category { get; set; }

        /// <summary>节点描述。</summary>
        public string? Description { get; set; }

        /// <summary>节点位置。</summary>
        public Vector2 Position { get; set; }

        /// <summary>节点大小。</summary>
        public Vector2 Size { get; set; } = new Vector2(220, 100);

        /// <summary>节点颜色。</summary>
        public string ColorHex { get; set; } = "#4488CC";

        /// <summary>是否启用。</summary>
        public bool IsEnabled { get; set; } = true;

        /// <summary>是否折叠。</summary>
        public bool IsCollapsed { get; set; }

        /// <summary>输入引脚。</summary>
        public List<AnimNodeScriptPin> InputPins { get; set; } = new List<AnimNodeScriptPin>();

        /// <summary>输出引脚。</summary>
        public List<AnimNodeScriptPin> OutputPins { get; set; } = new List<AnimNodeScriptPin>();

        /// <summary>自定义元数据。</summary>
        public Dictionary<string, string> Metadata { get; set; } = new Dictionary<string, string>();

        /// <summary>获取引脚。</summary>
        public AnimNodeScriptPin? GetPin(Guid pinId)
        {
            return InputPins.FirstOrDefault(p => p.Id == pinId) ?? OutputPins.FirstOrDefault(p => p.Id == pinId);
        }

        /// <summary>添加输入引脚。</summary>
        public AnimNodeScriptPin AddInput(string name, AnimPinDataType type, string? defaultValue = null, bool isOptional = false)
        {
            var pin = AnimNodeScriptPin.CreateInput(name, type, defaultValue, InputPins.Count);
            pin.OwnerNodeId = Id;
            pin.IsOptional = isOptional;
            InputPins.Add(pin);
            return pin;
        }

        /// <summary>添加输出引脚。</summary>
        public AnimNodeScriptPin AddOutput(string name, AnimPinDataType type)
        {
            var pin = AnimNodeScriptPin.CreateOutput(name, type, OutputPins.Count);
            pin.OwnerNodeId = Id;
            OutputPins.Add(pin);
            return pin;
        }

        /// <summary>执行节点（子类重写）。</summary>
        public virtual void Execute(Dictionary<string, object?> context) { }

        /// <summary>获取节点输出值。</summary>
        public virtual object? GetOutputValue(string pinName, Dictionary<string, object?> context)
        {
            return null;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    //  动画蓝图事件节点
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>动画蓝图初始化事件节点。</summary>
    [Serializable]
    public class AnimBP_EventInitialize : AnimNodeScriptNode
    {
        public AnimBP_EventInitialize()
        {
            NodeType = "AnimBP_EventInitialize";
            Title = "Event Initialize";
            Category = AnimNodeCategory.Event;
            ColorHex = "#CC0000";
            Size = new Vector2(280, 80);
            AddOutput("Exec", AnimPinDataType.Exec);
            Description = "动画蓝图初始化时触发";
        }

        public override void Execute(Dictionary<string, object?> context)
        {
            context["EventType"] = "Initialize";
        }
    }

    /// <summary>动画蓝图更新事件节点。</summary>
    [Serializable]
    public class AnimBP_EventNodeScriptUpdate : AnimNodeScriptNode
    {
        public AnimBP_EventNodeScriptUpdate()
        {
            NodeType = "AnimBP_EventNodeScriptUpdate";
            Title = "Event NodeScript Update";
            Category = AnimNodeCategory.Event;
            ColorHex = "#CC0000";
            Size = new Vector2(280, 100);
            AddOutput("Exec", AnimPinDataType.Exec);
            AddOutput("DeltaTime", AnimPinDataType.Float);
            AddOutput("WorldTime", AnimPinDataType.Float);
            Description = "动画蓝图每帧更新时触发";
        }

        public override void Execute(Dictionary<string, object?> context)
        {
            context["EventType"] = "NodeScriptUpdate";
        }
    }

    /// <summary>动画蓝图后评估事件节点。</summary>
    [Serializable]
    public class AnimBP_EventPostEvaluate : AnimNodeScriptNode
    {
        public AnimBP_EventPostEvaluate()
        {
            NodeType = "AnimBP_EventPostEvaluate";
            Title = "Event Post Evaluate";
            Category = AnimNodeCategory.Event;
            ColorHex = "#CC0000";
            Size = new Vector2(280, 80);
            AddOutput("Exec", AnimPinDataType.Exec);
            Description = "动画蓝图评估完成后触发";
        }

        public override void Execute(Dictionary<string, object?> context)
        {
            context["EventType"] = "PostEvaluate";
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    //  骨骼操作节点
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>获取骨骼变换。</summary>
    [Serializable]
    public class AnimBP_GetBoneTransform : AnimNodeScriptNode
    {
        public AnimBP_GetBoneTransform()
        {
            NodeType = "GetBoneTransform";
            Title = "Get Bone Transform";
            Category = AnimNodeCategory.Bone;
            ColorHex = "#9966FF";
            Size = new Vector2(240, 100);
            AddInput("ComponentSpacePose", AnimPinDataType.ComponentSpacePose);
            AddInput("BoneName", AnimPinDataType.String);
            AddOutput("Transform", AnimPinDataType.Transform);
            Description = "获取指定骨骼在组件空间中的变换";
        }

        public override object? GetOutputValue(string pinName, Dictionary<string, object?> context)
        {
            if (pinName == "Transform")
            {
                var boneName = context.GetValueOrDefault("BoneName_Input", string.Empty) as string;
                return new Matrix4x4(); // 占位，实际从骨骼系统获取
            }
            return null;
        }
    }

    /// <summary>设置骨骼变换。</summary>
    [Serializable]
    public class AnimBP_SetBoneTransform : AnimNodeScriptNode
    {
        public AnimBP_SetBoneTransform()
        {
            NodeType = "SetBoneTransform";
            Title = "Set Bone Transform";
            Category = AnimNodeCategory.Bone;
            ColorHex = "#9966FF";
            Size = new Vector2(260, 120);
            AddInput("Exec", AnimPinDataType.Exec);
            AddInput("ComponentSpacePose", AnimPinDataType.ComponentSpacePose);
            AddInput("BoneName", AnimPinDataType.String);
            AddInput("Transform", AnimPinDataType.Transform);
            AddOutput("Exec", AnimPinDataType.Exec);
            AddOutput("ComponentSpacePose", AnimPinDataType.ComponentSpacePose);
            Description = "设置指定骨骼在组件空间中的变换";
        }
    }

    /// <summary>获取骨骼位置。</summary>
    [Serializable]
    public class AnimBP_GetBoneLocation : AnimNodeScriptNode
    {
        public AnimBP_GetBoneLocation()
        {
            NodeType = "GetBoneLocation";
            Title = "Get Bone Location";
            Category = AnimNodeCategory.Bone;
            ColorHex = "#9966FF";
            Size = new Vector2(240, 100);
            AddInput("ComponentSpacePose", AnimPinDataType.ComponentSpacePose);
            AddInput("BoneName", AnimPinDataType.String);
            AddInput("BoneSpace", AnimPinDataType.Bool, "false");
            AddOutput("Location", AnimPinDataType.Vector3);
        }
    }

    /// <summary>获取骨骼旋转。</summary>
    [Serializable]
    public class AnimBP_GetBoneRotation : AnimNodeScriptNode
    {
        public AnimBP_GetBoneRotation()
        {
            NodeType = "GetBoneRotation";
            Title = "Get Bone Rotation";
            Category = AnimNodeCategory.Bone;
            ColorHex = "#9966FF";
            Size = new Vector2(240, 100);
            AddInput("ComponentSpacePose", AnimPinDataType.ComponentSpacePose);
            AddInput("BoneName", AnimPinDataType.String);
            AddInput("BoneSpace", AnimPinDataType.Bool, "false");
            AddOutput("Rotation", AnimPinDataType.Rotator);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    //  动画曲线节点
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>获取动画曲线值。</summary>
    [Serializable]
    public class AnimBP_GetCurveValue : AnimNodeScriptNode
    {
        public AnimBP_GetCurveValue()
        {
            NodeType = "GetCurveValue";
            Title = "Get Curve Value";
            Category = AnimNodeCategory.Curve;
            ColorHex = "#FFCC00";
            Size = new Vector2(240, 100);
            AddInput("CurveName", AnimPinDataType.String);
            AddInput("Time", AnimPinDataType.Float, "0");
            AddOutput("Value", AnimPinDataType.Float);
            Description = "获取指定动画曲线在给定时间的值";
        }
    }

    /// <summary>设置动画曲线值。</summary>
    [Serializable]
    public class AnimBP_SetCurveValue : AnimNodeScriptNode
    {
        public AnimBP_SetCurveValue()
        {
            NodeType = "SetCurveValue";
            Title = "Set Curve Value";
            Category = AnimNodeCategory.Curve;
            ColorHex = "#FFCC00";
            Size = new Vector2(240, 100);
            AddInput("CurveName", AnimPinDataType.String);
            AddInput("Value", AnimPinDataType.Float, "0");
            Description = "设置动画曲线的当前值";
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    //  插槽节点
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>获取插槽变换。</summary>
    [Serializable]
    public class AnimBP_GetSocketTransform : AnimNodeScriptNode
    {
        public AnimBP_GetSocketTransform()
        {
            NodeType = "GetSocketTransform";
            Title = "Get Socket Transform";
            Category = AnimNodeCategory.Socket;
            ColorHex = "#CC6600";
            Size = new Vector2(240, 100);
            AddInput("InPose", AnimPinDataType.ComponentSpacePose);
            AddInput("SocketName", AnimPinDataType.String);
            AddOutput("Transform", AnimPinDataType.Transform);
        }
    }

    /// <summary>获取插槽位置。</summary>
    [Serializable]
    public class AnimBP_GetSocketLocation : AnimNodeScriptNode
    {
        public AnimBP_GetSocketLocation()
        {
            NodeType = "GetSocketLocation";
            Title = "Get Socket Location";
            Category = AnimNodeCategory.Socket;
            ColorHex = "#CC6600";
            Size = new Vector2(240, 100);
            AddInput("InPose", AnimPinDataType.ComponentSpacePose);
            AddInput("SocketName", AnimPinDataType.String);
            AddOutput("Location", AnimPinDataType.Vector3);
        }
    }

    /// <summary>获取插槽旋转。</summary>
    [Serializable]
    public class AnimBP_GetSocketRotation : AnimNodeScriptNode
    {
        public AnimBP_GetSocketRotation()
        {
            NodeType = "GetSocketRotation";
            Title = "Get Socket Rotation";
            Category = AnimNodeCategory.Socket;
            ColorHex = "#CC6600";
            Size = new Vector2(240, 100);
            AddInput("InPose", AnimPinDataType.ComponentSpacePose);
            AddInput("SocketName", AnimPinDataType.String);
            AddOutput("Rotation", AnimPinDataType.Rotator);
        }
    }

    /// <summary>获取蒙皮网格缩放。</summary>
    [Serializable]
    public class AnimBP_GetMeshScale : AnimNodeScriptNode
    {
        public AnimBP_GetMeshScale()
        {
            NodeType = "GetMeshScale";
            Title = "Get Mesh Scale";
            Category = AnimNodeCategory.Mesh;
            ColorHex = "#CC6600";
            Size = new Vector2(200, 80);
            AddOutput("Scale", AnimPinDataType.Vector3);
        }
    }

    /// <summary>获取所有者旋转。</summary>
    [Serializable]
    public class AnimBP_GetOwnerRotation : AnimNodeScriptNode
    {
        public AnimBP_GetOwnerRotation()
        {
            NodeType = "GetOwnerRotation";
            Title = "Get Owner Rotation";
            Category = AnimNodeCategory.Mesh;
            ColorHex = "#CC6600";
            Size = new Vector2(200, 80);
            AddOutput("Rotation", AnimPinDataType.Rotator);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    //  动画参数计算节点
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>浮点乘法。</summary>
    [Serializable]
    public class AnimBP_MultiplyFloat : AnimNodeScriptNode
    {
        public AnimBP_MultiplyFloat()
        {
            NodeType = "MultiplyFloat";
            Title = "Multiply Float";
            Category = AnimNodeCategory.Math;
            ColorHex = "#00CC66";
            Size = new Vector2(220, 80);
            AddInput("A", AnimPinDataType.Float, "1");
            AddInput("B", AnimPinDataType.Float, "1");
            AddOutput("Result", AnimPinDataType.Float);
        }
    }

    /// <summary>浮点钳制。</summary>
    [Serializable]
    public class AnimBP_ClampFloat : AnimNodeScriptNode
    {
        public AnimBP_ClampFloat()
        {
            NodeType = "ClampFloat";
            Title = "Clamp Float";
            Category = AnimNodeCategory.Math;
            ColorHex = "#00CC66";
            Size = new Vector2(220, 100);
            AddInput("Value", AnimPinDataType.Float, "0");
            AddInput("Min", AnimPinDataType.Float, "0");
            AddInput("Max", AnimPinDataType.Float, "1");
            AddOutput("Result", AnimPinDataType.Float);
        }
    }

    /// <summary>浮点插值。</summary>
    [Serializable]
    public class AnimBP_LerpFloat : AnimNodeScriptNode
    {
        public AnimBP_LerpFloat()
        {
            NodeType = "LerpFloat";
            Title = "Lerp Float";
            Category = AnimNodeCategory.Math;
            ColorHex = "#00CC66";
            Size = new Vector2(220, 100);
            AddInput("A", AnimPinDataType.Float, "0");
            AddInput("B", AnimPinDataType.Float, "1");
            AddInput("Alpha", AnimPinDataType.Float, "0");
            AddOutput("Result", AnimPinDataType.Float);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    //  直接骨骼控制节点
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>变换骨骼。</summary>
    [Serializable]
    public class AnimBP_TransformBone : AnimNodeScriptNode
    {
        public AnimBP_TransformBone()
        {
            NodeType = "TransformBone";
            Title = "Transform Bone";
            Category = AnimNodeCategory.Transform;
            ColorHex = "#9966FF";
            Size = new Vector2(280, 140);
            AddInput("ComponentSpacePose", AnimPinDataType.ComponentSpacePose);
            AddInput("BoneName", AnimPinDataType.String);
            AddInput("Translation", AnimPinDataType.Vector3, "(0,0,0)");
            AddInput("Rotation", AnimPinDataType.Rotator, "(0,0,0)");
            AddInput("Scale", AnimPinDataType.Vector3, "(1,1,1)");
            AddInput("TranslationMode", AnimPinDataType.Int, "0");
            AddInput("RotationMode", AnimPinDataType.Int, "0");
            AddInput("ScaleMode", AnimPinDataType.Int, "0");
            AddOutput("ComponentSpacePose", AnimPinDataType.ComponentSpacePose);
            Description = "直接变换指定骨骼的位置、旋转和缩放";
        }
    }

    /// <summary>正向求解（Forward Solve）。</summary>
    [Serializable]
    public class AnimBP_ForwardSolve : AnimNodeScriptNode
    {
        public AnimBP_ForwardSolve()
        {
            NodeType = "ForwardSolve";
            Title = "Forward Solve";
            Category = AnimNodeCategory.Transform;
            ColorHex = "#9966FF";
            Size = new Vector2(240, 80);
            AddInput("ComponentSpacePose", AnimPinDataType.ComponentSpacePose);
            AddOutput("ComponentSpacePose", AnimPinDataType.ComponentSpacePose);
            Description = "将局部空间位姿正向求解到组件空间";
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    //  IK 求解器节点
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>两骨骼 IK 求解器。</summary>
    [Serializable]
    public class AnimBP_TwoBoneIK : AnimNodeScriptNode
    {
        public AnimBP_TwoBoneIK()
        {
            NodeType = "TwoBoneIK";
            Title = "Two Bone IK";
            Category = AnimNodeCategory.IK;
            ColorHex = "#FF6600";
            Size = new Vector2(300, 180);
            AddInput("ComponentSpacePose", AnimPinDataType.ComponentSpacePose);
            AddInput("IKBoneName", AnimPinDataType.String);
            AddInput("EffectorLocation", AnimPinDataType.Vector3);
            AddInput("JointTargetLocation", AnimPinDataType.Vector3);
            AddInput("bMaintainEffectorRelRot", AnimPinDataType.Bool, "true");
            AddInput("bTakeRotationFromEffector", AnimPinDataType.Bool, "false");
            AddInput("bPinJointLocation", AnimPinDataType.Bool, "false");
            AddInput("bClampToMaxIKAngle", AnimPinDataType.Bool, "true");
            AddInput("MaxIKAngle", AnimPinDataType.Float, "90");
            AddOutput("ComponentSpacePose", AnimPinDataType.ComponentSpacePose);
            AddOutput("OutEffectorLocation", AnimPinDataType.Vector3);
            AddOutput("bIKValid", AnimPinDataType.Bool);
            Description = "两骨骼 IK 求解器：求解链式骨骼到目标位置";
        }
    }

    /// <summary>注视 IK 求解器。</summary>
    [Serializable]
    public class AnimBP_LookAtIK : AnimNodeScriptNode
    {
        public AnimBP_LookAtIK()
        {
            NodeType = "LookAtIK";
            Title = "Look At IK";
            Category = AnimNodeCategory.IK;
            ColorHex = "#FF6600";
            Size = new Vector2(280, 140);
            AddInput("ComponentSpacePose", AnimPinDataType.ComponentSpacePose);
            AddInput("BoneName", AnimPinDataType.String);
            AddInput("TargetLocation", AnimPinDataType.Vector3);
            AddInput("LookAtAxis", AnimPinDataType.Vector3, "(0,0,1)");
            AddInput("bUseLookUpBone", AnimPinDataType.Bool, "false");
            AddInput("LookUpBoneName", AnimPinDataType.String);
            AddInput("LookUpAxis", AnimPinDataType.Vector3, "(0,1,0)");
            AddOutput("ComponentSpacePose", AnimPinDataType.ComponentSpacePose);
            Description = "注视 IK：使骨骼朝向目标位置";
        }
    }

    /// <summary>弹簧 IK 求解器。</summary>
    [Serializable]
    public class AnimBP_SpringIK : AnimNodeScriptNode
    {
        public AnimBP_SpringIK()
        {
            NodeType = "SpringIK";
            Title = "Spring IK";
            Category = AnimNodeCategory.IK;
            ColorHex = "#FF6600";
            Size = new Vector2(280, 140);
            AddInput("ComponentSpacePose", AnimPinDataType.ComponentSpacePose);
            AddInput("BoneName", AnimPinDataType.String);
            AddInput("TargetLocation", AnimPinDataType.Vector3);
            AddInput("bEnableSpring", AnimPinDataType.Bool, "true");
            AddInput("SpringStiffness", AnimPinDataType.Float, "50");
            AddInput("SpringDamping", AnimPinDataType.Float, "2");
            AddInput("bApplyAndTranslate", AnimPinDataType.Bool, "true");
            AddOutput("ComponentSpacePose", AnimPinDataType.ComponentSpacePose);
            Description = "弹簧 IK：使用弹簧物理求解骨骼位置";
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    //  混合节点
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>应用叠加动画。</summary>
    [Serializable]
    public class AnimBP_ApplyAdditive : AnimNodeScriptNode
    {
        public AnimBP_ApplyAdditive()
        {
            NodeType = "ApplyAdditive";
            Title = "Apply Additive";
            Category = AnimNodeCategory.Blend;
            ColorHex = "#00CCFF";
            Size = new Vector2(280, 100);
            AddInput("BasePose", AnimPinDataType.ComponentSpacePose);
            AddInput("AdditivePose", AnimPinDataType.ComponentSpacePose);
            AddInput("Alpha", AnimPinDataType.Float, "1");
            AddOutput("ComponentSpacePose", AnimPinDataType.ComponentSpacePose);
            Description = "将叠加动画位姿以指定权重应用到基础位姿上";
        }
    }

    /// <summary>混合位姿。</summary>
    [Serializable]
    public class AnimBP_BlendPoses : AnimNodeScriptNode
    {
        public AnimBP_BlendPoses()
        {
            NodeType = "BlendPoses";
            Title = "Blend Poses";
            Category = AnimNodeCategory.Blend;
            ColorHex = "#00CCFF";
            Size = new Vector2(260, 100);
            AddInput("PoseA", AnimPinDataType.ComponentSpacePose);
            AddInput("PoseB", AnimPinDataType.ComponentSpacePose);
            AddInput("BlendWeight", AnimPinDataType.Float, "0.5");
            AddOutput("ComponentSpacePose", AnimPinDataType.ComponentSpacePose);
            Description = "按权重混合两个动画位姿";
        }
    }

    /// <summary>分层骨骼混合。</summary>
    [Serializable]
    public class AnimBP_LayeredBoneBlend : AnimNodeScriptNode
    {
        public AnimBP_LayeredBoneBlend()
        {
            NodeType = "LayeredBoneBlend";
            Title = "Layered Bone Blend";
            Category = AnimNodeCategory.Blend;
            ColorHex = "#00CCFF";
            Size = new Vector2(300, 140);
            AddInput("BasePose", AnimPinDataType.ComponentSpacePose);
            AddInput("Layer1Pose", AnimPinDataType.ComponentSpacePose);
            AddInput("Layer1Weight", AnimPinDataType.Float, "0");
            AddInput("Layer2Pose", AnimPinDataType.ComponentSpacePose);
            AddInput("Layer2Weight", AnimPinDataType.Float, "0");
            AddInput("Layer3Pose", AnimPinDataType.ComponentSpacePose);
            AddInput("Layer3Weight", AnimPinDataType.Float, "0");
            AddInput("bMeshSpaceRotationBlend", AnimPinDataType.Bool, "false");
            AddOutput("ComponentSpacePose", AnimPinDataType.ComponentSpacePose);
            Description = "分层骨骼混合：按权重将多层动画应用到指定骨骼分支";
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    //  AnimNodeScript - 动画蓝图
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 动画蓝图：完整的动画蓝图定义。
    /// <para>包含事件图、更新图、AnimGraph、变量和函数。</para>
    /// </summary>
    [Serializable]
    public class AnimNodeScript
    {
        /// <summary>蓝图唯一标识符。</summary>
        public Guid Id { get; set; } = Guid.NewGuid();

        /// <summary>蓝图名称。</summary>
        public string Name { get; set; } = "NewAnimNodeScript";

        /// <summary>蓝图显示标题。</summary>
        public string DisplayName { get; set; } = "New Anim NodeScript";

        /// <summary>蓝图描述。</summary>
        public string? Description { get; set; }

        /// <summary>目标骨骼资产名称。</summary>
        public string? TargetSkeleton { get; set; }

        /// <summary>事件图（节点和连接）。</summary>
        public List<AnimNodeScriptNode> EventGraphNodes { get; set; } = new List<AnimNodeScriptNode>();

        /// <summary>事件图连接。</summary>
        public List<AnimNodeScriptConnection> EventGraphConnections { get; set; } = new List<AnimNodeScriptConnection>();

        /// <summary>更新图节点。</summary>
        public List<AnimNodeScriptNode> UpdateGraphNodes { get; set; } = new List<AnimNodeScriptNode>();

        /// <summary>更新图连接。</summary>
        public List<AnimNodeScriptConnection> UpdateGraphConnections { get; set; } = new List<AnimNodeScriptConnection>();

        /// <summary>AnimGraph 节点。</summary>
        public List<AnimNodeScriptNode> AnimGraphNodes { get; set; } = new List<AnimNodeScriptNode>();

        /// <summary>AnimGraph 连接。</summary>
        public List<AnimNodeScriptConnection> AnimGraphConnections { get; set; } = new List<AnimNodeScriptConnection>();

        /// <summary>蓝图变量。</summary>
        public List<ScriptVariable> Variables { get; set; } = new List<ScriptVariable>();

        /// <summary>蓝图函数。</summary>
        public List<GraphFunction> Functions { get; set; } = new List<GraphFunction>();

        /// <summary>蓝图版本。</summary>
        public int Version { get; set; }

        /// <summary>自定义元数据。</summary>
        public Dictionary<string, string> Metadata { get; set; } = new Dictionary<string, string>();

        /// <summary>
        /// 添加节点到事件图。
        /// </summary>
        public AnimNodeScriptNode AddEventGraphNode(AnimNodeScriptNode node)
        {
            EventGraphNodes.Add(node);
            return node;
        }

        /// <summary>
        /// 添加节点到更新图。
        /// </summary>
        public AnimNodeScriptNode AddUpdateGraphNode(AnimNodeScriptNode node)
        {
            UpdateGraphNodes.Add(node);
            return node;
        }

        /// <summary>
        /// 添加节点到 AnimGraph。
        /// </summary>
        public AnimNodeScriptNode AddAnimGraphNode(AnimNodeScriptNode node)
        {
            AnimGraphNodes.Add(node);
            return node;
        }

        /// <summary>
        /// 创建连接。
        /// </summary>
        public AnimNodeScriptConnection? Connect(
            List<AnimNodeScriptNode> nodes,
            List<AnimNodeScriptConnection> connections,
            Guid sourceNode, Guid sourcePin,
            Guid targetNode, Guid targetPin)
        {
            var srcNode = nodes.FirstOrDefault(n => n.Id == sourceNode);
            var tgtNode = nodes.FirstOrDefault(n => n.Id == targetNode);
            if (srcNode == null || tgtNode == null) return null;

            var srcPin = srcNode.GetPin(sourcePin);
            var tgtPin = tgtNode.GetPin(targetPin);
            if (srcPin == null || tgtPin == null) return null;
            if (srcPin.Direction != GraphPinDirection.Output || tgtPin.Direction != GraphPinDirection.Input) return null;

            var conn = new AnimNodeScriptConnection
            {
                SourceNodeId = sourceNode,
                SourcePinId = sourcePin,
                TargetNodeId = targetNode,
                TargetPinId = targetPin,
                DataType = srcPin.DataType,
                ColorHex = "#FFFFFF"
            };

            connections.Add(conn);
            srcPin.ConnectionIds.Add(conn.Id);
            tgtPin.ConnectionIds.Add(conn.Id);
            return conn;
        }

        /// <summary>
        /// 序列化动画蓝图。
        /// </summary>
        public string SerializeToJson()
        {
            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
            };
            return JsonSerializer.Serialize(this, options);
        }

        /// <summary>
        /// 从 JSON 反序列化动画蓝图。
        /// </summary>
        public static AnimNodeScript? DeserializeFromJson(string json)
        {
            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
            };
            return JsonSerializer.Deserialize<AnimNodeScript>(json, options);
        }

        /// <summary>保存到文件。</summary>
        public void SaveToFile(string filePath)
        {
            System.IO.File.WriteAllText(filePath, SerializeToJson());
        }

        /// <summary>从文件加载。</summary>
        public static AnimNodeScript? LoadFromFile(string filePath)
        {
            return DeserializeFromJson(System.IO.File.ReadAllText(filePath));
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    //  AnimGraphInstance - 运行时动画蓝图实例
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 动画蓝图运行时实例：将 AnimNodeScript 实例化为可执行的运行时对象。
    /// </summary>
    public class AnimGraphInstance
    {
        /// <summary>关联的动画蓝图。</summary>
        public AnimNodeScript NodeScript { get; }

        /// <summary>运行时变量值表。</summary>
        public Dictionary<string, object?> Variables { get; } = new Dictionary<string, object?>();

        /// <summary>是否已初始化。</summary>
        public bool IsInitialized { get; private set; }

        /// <summary>总运行时间。</summary>
        public float TotalTime { get; private set; }

        /// <summary>当前帧增量时间。</summary>
        public float DeltaTime { get; private set; }

        /// <summary>执行上下文。</summary>
        private readonly Dictionary<string, object?> _context = new Dictionary<string, object?>();

        /// <summary>初始化事件。</summary>
        public event Action? OnInitialized;
        public event Action<float>? OnUpdated;
        public event Action? OnPostEvaluated;

        /// <summary>
        /// 创建运行时实例。
        /// </summary>
        public AnimGraphInstance(AnimNodeScript nodescript)
        {
            NodeScript = nodescript ?? throw new ArgumentNullException(nameof(nodescript));

            // 初始化变量
            foreach (var variable in nodescript.Variables)
            {
                Variables[variable.Name] = null;
            }
        }

        /// <summary>
        /// 初始化动画蓝图实例。
        /// </summary>
        public void Initialize()
        {
            // 初始化变量默认值
            foreach (var variable in NodeScript.Variables)
            {
                variable.InitializeToDefault();
                Variables[variable.Name] = variable.Value;
            }

            // 执行事件图中的初始化事件
            ExecuteEventGraph("Initialize");

            IsInitialized = true;
            OnInitialized?.Invoke();
        }

        /// <summary>
        /// 每帧更新。
        /// </summary>
        public void Update(float deltaTime)
        {
            if (!IsInitialized) return;

            DeltaTime = deltaTime;
            TotalTime += deltaTime;

            _context["DeltaTime"] = deltaTime;
            _context["WorldTime"] = TotalTime;

            // 执行更新图
            ExecuteUpdateGraph(deltaTime);

            OnUpdated?.Invoke(deltaTime);
        }

        /// <summary>
        /// 评估 AnimGraph（生成最终位姿）。
        /// </summary>
        public void Evaluate(float deltaTime)
        {
            // 执行 AnimGraph 节点链
            _context["DeltaTime"] = deltaTime;

            foreach (var node in NodeScript.AnimGraphNodes)
            {
                if (node.IsEnabled)
                {
                    node.Execute(_context);
                }
            }

            // 后评估事件
            ExecuteEventGraph("PostEvaluate");
            OnPostEvaluated?.Invoke();
        }

        /// <summary>
        /// 获取运行时变量值。
        /// </summary>
        public T? GetVariableValue<T>(string name)
        {
            if (Variables.TryGetValue(name, out var value))
            {
                try { return (T)Convert.ChangeType(value, typeof(T)); }
                catch { return default; }
            }
            return default;
        }

        /// <summary>
        /// 设置运行时变量值。
        /// </summary>
        public void SetVariableValue(string name, object? value)
        {
            Variables[name] = value;
        }

        private void ExecuteEventGraph(string eventType)
        {
            foreach (var node in NodeScript.EventGraphNodes)
            {
                if (!node.IsEnabled) continue;

                if (eventType == "Initialize" && node is AnimBP_EventInitialize init)
                {
                    init.Execute(_context);
                }
                else if (eventType == "PostEvaluate" && node is AnimBP_EventPostEvaluate post)
                {
                    post.Execute(_context);
                }
            }
        }

        private void ExecuteUpdateGraph(float deltaTime)
        {
            foreach (var node in NodeScript.UpdateGraphNodes)
            {
                if (!node.IsEnabled) continue;

                if (node is AnimBP_EventNodeScriptUpdate update)
                {
                    update.Execute(_context);
                }
            }
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    //  AnimBPDebugger - 动画蓝图调试器
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 动画蓝图调试器：提供实时预览、变量监视、节点状态和曲线值调试功能。
    /// </summary>
    public class AnimBPDebugger
    {
        /// <summary>关联的运行时实例。</summary>
        public AnimGraphInstance Instance { get; }

        /// <summary>是否启用调试。</summary>
        public bool IsEnabled { get; set; } = true;

        /// <summary>当前正在执行的节点 ID。</summary>
        public Guid? ActiveNodeId { get; private set; }

        /// <summary>节点执行计时器（节点 ID → 执行时间毫秒）。</summary>
        public Dictionary<Guid, double> NodeExecutionTimes { get; } = new Dictionary<Guid, double>();

        /// <summary>节点执行计数。</summary>
        public Dictionary<Guid, int> NodeExecutionCounts { get; } = new Dictionary<Guid, int>();

        /// <summary>曲线值监视（曲线名 → 当前值）。</summary>
        public Dictionary<string, float> CurveValues { get; } = new Dictionary<string, float>();

        /// <summary>骨骼变换监视（骨骼名 → 变换字符串）。</summary>
        public Dictionary<string, string> BoneTransforms { get; } = new Dictionary<string, string>();

        /// <summary>调试日志。</summary>
        public List<string> DebugLog { get; } = new List<string>();

        /// <summary>最大日志条数。</summary>
        public int MaxLogEntries { get; set; } = 500;

        /// <summary>是否启用实时预览。</summary>
        public bool EnableLivePreview { get; set; } = true;

        /// <summary>是否启用节点执行追踪。</summary>
        public bool EnableNodeTracing { get; set; }

        /// <summary>预览帧率限制。</summary>
        public int PreviewFrameRate { get; set; } = 30;

        /// <summary>
        /// 创建调试器。
        /// </summary>
        public AnimBPDebugger(AnimGraphInstance instance)
        {
            Instance = instance ?? throw new ArgumentNullException(nameof(instance));
        }

        /// <summary>
        /// 更新调试信息（每帧调用）。
        /// </summary>
        public void UpdateDebugInfo()
        {
            if (!IsEnabled) return;

            // 更新变量值监视
            foreach (var kvp in Instance.Variables)
            {
                // 变量值已在 Instance.Variables 中实时更新
            }
        }

        /// <summary>
        /// 记录节点执行。
        /// </summary>
        public void RecordNodeExecution(Guid nodeId, double executionTimeMs)
        {
            if (!EnableNodeTracing) return;

            ActiveNodeId = nodeId;

            if (!NodeExecutionTimes.ContainsKey(nodeId))
                NodeExecutionTimes[nodeId] = 0;
            NodeExecutionTimes[nodeId] = executionTimeMs;

            if (!NodeExecutionCounts.ContainsKey(nodeId))
                NodeExecutionCounts[nodeId] = 0;
            NodeExecutionCounts[nodeId]++;

            if (NodeExecutionTimes.Count > 1000)
            {
                // 清理旧数据
                var oldest = NodeExecutionTimes.Keys.First();
                NodeExecutionTimes.Remove(oldest);
                NodeExecutionCounts.Remove(oldest);
            }
        }

        /// <summary>
        /// 记录曲线值。
        /// </summary>
        public void RecordCurveValue(string curveName, float value)
        {
            CurveValues[curveName] = value;
        }

        /// <summary>
        /// 记录骨骼变换。
        /// </summary>
        public void RecordBoneTransform(string boneName, string transformStr)
        {
            BoneTransforms[boneName] = transformStr;
        }

        /// <summary>
        /// 添加调试日志。
        /// </summary>
        public void Log(string message)
        {
            DebugLog.Add($"[{DateTime.UtcNow:HH:mm:ss.fff}] {message}");
            while (DebugLog.Count > MaxLogEntries)
            {
                DebugLog.RemoveAt(0);
            }
        }

        /// <summary>
        /// 获取调试面板数据。
        /// </summary>
        public Dictionary<string, object> GetDebugPanelData()
        {
            return new Dictionary<string, object>
            {
                ["IsEnabled"] = IsEnabled,
                ["IsInitialized"] = Instance.IsInitialized,
                ["TotalTime"] = Instance.TotalTime,
                ["DeltaTime"] = Instance.DeltaTime,
                ["ActiveNodeId"] = ActiveNodeId,
                ["VariableCount"] = Instance.Variables.Count,
                ["CurveCount"] = CurveValues.Count,
                ["BoneCount"] = BoneTransforms.Count,
                ["LogCount"] = DebugLog.Count
            };
        }

        /// <summary>
        /// 获取性能统计（按节点类型汇总）。
        /// </summary>
        public string GetPerformanceStats()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("=== AnimBP 性能统计 ===");

            var totalTime = NodeExecutionTimes.Values.Sum();
            sb.AppendLine($"  总执行时间: {totalTime:F3}ms");
            sb.AppendLine($"  节点执行总数: {NodeExecutionCounts.Values.Sum()}");
            sb.AppendLine($"  活跃节点数: {NodeExecutionCounts.Count}");
            sb.AppendLine($"  曲线数: {CurveValues.Count}");
            sb.AppendLine($"  骨骼监视数: {BoneTransforms.Count}");

            return sb.ToString();
        }

        /// <summary>
        /// 清除所有调试数据。
        /// </summary>
        public void Clear()
        {
            NodeExecutionTimes.Clear();
            NodeExecutionCounts.Clear();
            CurveValues.Clear();
            BoneTransforms.Clear();
            DebugLog.Clear();
            ActiveNodeId = null;
        }
    }
}
