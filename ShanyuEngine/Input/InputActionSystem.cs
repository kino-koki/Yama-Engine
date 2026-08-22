// =====================================================================
// InputActionSystem.cs — 山雨引擎 动作映射引擎 (完整重写)
// 命名空间: ShanyuEngine.Input.Enhanced
// =====================================================================
// 功能概述:
//   1. ActionMap            : 动作分组容器，支持启用/禁用、优先级
//   2. 动作绑定解析器       : 从 JSON/二进制加载映射配置
//   3. 运行时动作评估器     : 累积修饰器 + 检测触发器
//   4. 动作事件委托         : Started/Triggered/Completed/Canceled
//   5. EnhancedInputAction  : 完整的动作定义 (绑定/修改器/触发器/状态机)
//   6. InputActionMap       : 映射表 (管理动作集合)
//   7. InputActionAsset     : 资产 (映射表集合 + 控制方案 + 绑定覆盖)
//   8. InputActionManager   : 全局管理器 (注册/更新/查询)
//   9. InputBinding         : 绑定 (路径/修改器/处理器/交互)
//   10. InputBindingComposite: 组合绑定 (D-Pad/轴/修饰键/向量)
//   11. BindingOverride     : 绑定覆盖记录
// =====================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ShanyuEngine.Input.Enhanced
{
    #region 枚举定义

    /// <summary>
    /// 输入动作类型 —— 定义动作产生的值的数据类型
    /// </summary>
    public enum InputActionType
    {
        /// <summary>布尔按钮型 (0/1)</summary>
        Button,
        /// <summary>一维浮点轴 [-1,1] 或 [0,1]</summary>
        Axis,
        /// <summary>二维向量 (如摇杆)</summary>
        Vector2,
        /// <summary>三维向量 (如陀螺仪/空间设备)</summary>
        Vector3,
        /// <summary>位姿 (位置 + 旋转，用于 VR/AR)</summary>
        Pose
    }

    /// <summary>
    /// 动作状态机 —— 精确描述动作在某一帧的完整状态
    /// </summary>
    public enum InputActionState
    {
        /// <summary>等待中：没有绑定处于激活状态</summary>
        Waiting,
        /// <summary>已开始：刚从 Waiting 变为有绑定激活（按下瞬间）</summary>
        Started,
        /// <summary>已执行：触发条件被满足（如按住超过阈值）</summary>
        Performed,
        /// <summary>进行中：值持续更新但尚未触发</summary>
        Ongoing,
        /// <summary>已取消：绑定不再激活（释放瞬间）</summary>
        Canceled
    }

    /// <summary>
    /// 动作相位 —— 简化的三阶段生命周期 (Started -> Triggered -> Canceled)
    /// </summary>
    public enum InputActionPhase
    {
        /// <summary>未激活</summary>
        Waiting,
        /// <summary>刚激活（按下帧）</summary>
        Started,
        /// <summary>触发中（满足触发条件，每帧触发）</summary>
        Triggered,
        /// <summary>持续进行中（值在变化但未触发）</summary>
        Ongoing,
        /// <summary>已取消（释放帧）</summary>
        Canceled
    }

    /// <summary>
    /// 绑定类型
    /// </summary>
    public enum InputBindingType
    {
        /// <summary>直接绑定到单个控制</summary>
        Direct,
        /// <summary>组合绑定（多个控制组合为一个值）</summary>
        Composite
    }

    #endregion

    #region InputBinding —— 单个输入绑定

    /// <summary>
    /// 输入绑定 —— 将一个具体的物理控制映射到动作
    /// 绑定路径示例: "Keyboard/W", "Gamepad/ButtonSouth", "Mouse/Button0"
    /// </summary>
    public class InputBinding
    {
        /// <summary>绑定的唯一标识 (GUID)</summary>
        public string Id { get; } = Guid.NewGuid().ToString("N").Substring(0, 8);

        /// <summary>绑定路径: "Keyboard/{Key}", "Gamepad/{Control}", "Mouse/{Control}", "Touch/{N}/{Axis}"</summary>
        public string Path { get; set; }

        /// <summary>绑定类型</summary>
        public InputBindingType BindingType { get; set; } = InputBindingType.Direct;

        /// <summary>所属组合绑定的名称 (仅当 BindingType == Composite 时有效)</summary>
        public string CompositeName { get; set; }

        /// <summary>在组合绑定中的角色名 (如 "positive", "negative", "up", "down")</summary>
        public string CompositePart { get; set; }

        /// <summary>叠加组名：同组的绑定值会被合并</summary>
        public string Groups { get; set; }

        /// <summary>修改器列表：在值传递给动作前进行变换</summary>
        public List<InputModifier> Modifiers { get; set; } = new List<InputModifier>();

        /// <summary>交互行为</summary>
        public InputInteraction Interactions { get; set; }

        /// <summary>处理器列表</summary>
        public List<InputProcessor> Processors { get; set; } = new List<InputProcessor>();

        /// <summary>是否启用此绑定</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>此绑定的权重 (用于多绑定时合并值)</summary>
        public float Weight { get; set; } = 1f;

        /// <summary>运行时：当前绑定的原始值</summary>
        [JsonIgnore]
        public float RawValue { get; internal set; }

        /// <summary>运行时：应用修改器后的值</summary>
        [JsonIgnore]
        public float ProcessedValue { get; internal set; }

        /// <summary>运行时：此绑定是否有有效输入</summary>
        [JsonIgnore]
        public bool HasValue { get; internal set; }

        public InputBinding() { }

        public InputBinding(string path)
        {
            Path = path;
        }

        public InputBinding(string path, string groups)
        {
            Path = path;
            Groups = groups;
        }

        /// <summary>添加修改器（链式调用）</summary>
        public InputBinding WithModifier(InputModifier modifier)
        {
            Modifiers.Add(modifier);
            return this;
        }

        /// <summary>添加处理器（链式调用）</summary>
        public InputBinding WithProcessor(InputProcessor processor)
        {
            Processors.Add(processor);
            return this;
        }

        /// <summary>添加交互（链式调用）</summary>
        public InputBinding WithInteraction(InputInteraction interaction)
        {
            Interactions = interaction;
            return this;
        }

        public override string ToString() => $"Binding[{Path}]";
    }

    #endregion

    #region InputBindingComposite —— 组合绑定

    /// <summary>
    /// 组合绑定基类 —— 将多个输入控制组合为一个逻辑值
    /// </summary>
    public abstract class InputBindingComposite
    {
        /// <summary>组合绑定名称</summary>
        public string Name { get; set; }

        /// <summary>此组合产生的动作类型</summary>
        public InputActionType ValueType { get; set; }

        /// <summary>组合内各部分绑定的原始值</summary>
        protected Dictionary<string, float> PartValues { get; } = new Dictionary<string, float>();

        public InputBindingComposite(string name, InputActionType valueType)
        {
            Name = name;
            ValueType = valueType;
        }

        /// <summary>设置某个部分的原始值</summary>
        public void SetPartValue(string part, float value)
        {
            PartValues[part] = value;
        }

        /// <summary>计算组合后的最终值</summary>
        public abstract float Evaluate();
        public abstract Vector2 EvaluateVector2();
        public abstract Vector3 EvaluateVector3();

        /// <summary>获取此组合需要的部分名称列表</summary>
        public abstract IEnumerable<string> GetPartNames();
    }

    /// <summary>
    /// 一维轴组合：正键 + 负键 -> [-1, 1]
    /// </summary>
    public class AxisComposite : InputBindingComposite
    {
        public const string PartPositive = "positive";
        public const string PartNegative = "negative";

        public AxisComposite(string name = "Axis") : base(name, InputActionType.Axis) { }

        public override float Evaluate()
        {
            PartValues.TryGetValue(PartPositive, out float pos);
            PartValues.TryGetValue(PartNegative, out float neg);
            return Math.Clamp(pos - neg, -1f, 1f);
        }

        public override Vector2 EvaluateVector2() => new Vector2(Evaluate(), 0f);
        public override Vector3 EvaluateVector3() => new Vector3(Evaluate(), 0f, 0f);

        public override IEnumerable<string> GetPartNames()
        {
            yield return PartPositive;
            yield return PartNegative;
        }
    }

    /// <summary>
    /// D-Pad 组合：上/下/左/右四键 -> Vector2
    /// </summary>
    public class DpadComposite : InputBindingComposite
    {
        public const string PartUp = "up";
        public const string PartDown = "down";
        public const string PartLeft = "left";
        public const string PartRight = "right";

        public DpadComposite(string name = "Dpad") : base(name, InputActionType.Vector2) { }

        public override float Evaluate()
        {
            var v = EvaluateVector2();
            return MathF.Max(MathF.Abs(v.X), MathF.Abs(v.Y));
        }

        public override Vector2 EvaluateVector2()
        {
            PartValues.TryGetValue(PartUp, out float up);
            PartValues.TryGetValue(PartDown, out float down);
            PartValues.TryGetValue(PartLeft, out float left);
            PartValues.TryGetValue(PartRight, out float right);

            var result = new Vector2(right - left, up - down);
            float len = result.Length();
            return len > 1f ? Vector2.Normalize(result) : result;
        }

        public override Vector3 EvaluateVector3() => new Vector3(EvaluateVector2(), 0f);

        public override IEnumerable<string> GetPartNames()
        {
            yield return PartUp;
            yield return PartDown;
            yield return PartLeft;
            yield return PartRight;
        }
    }

    /// <summary>
    /// 任意四键 -> Vector2（自定义映射）
    /// </summary>
    public class Vector2Composite : InputBindingComposite
    {
        public const string PartUp = "up";
        public const string PartDown = "down";
        public const string PartLeft = "left";
        public const string PartRight = "right";

        public Vector2Composite(string name = "2DVector") : base(name, InputActionType.Vector2) { }

        public override float Evaluate()
        {
            var v = EvaluateVector2();
            return MathF.Max(MathF.Abs(v.X), MathF.Abs(v.Y));
        }

        public override Vector2 EvaluateVector2()
        {
            PartValues.TryGetValue(PartUp, out float up);
            PartValues.TryGetValue(PartDown, out float down);
            PartValues.TryGetValue(PartLeft, out float left);
            PartValues.TryGetValue(PartRight, out float right);

            var result = new Vector2(right - left, up - down);
            float len = result.Length();
            return len > 1f ? Vector2.Normalize(result) : result;
        }

        public override Vector3 EvaluateVector3() => new Vector3(EvaluateVector2(), 0f);

        public override IEnumerable<string> GetPartNames()
        {
            yield return PartUp;
            yield return PartDown;
            yield return PartLeft;
            yield return PartRight;
        }
    }

    /// <summary>
    /// 一修饰键 + 动作键 组合：修饰键按下时，动作键才有效
    /// </summary>
    public class OneModifierComposite : InputBindingComposite
    {
        public const string PartModifier = "modifier";
        public const string PartAction = "action";

        public OneModifierComposite(string name = "OneModifier") : base(name, InputActionType.Button) { }

        public override float Evaluate()
        {
            PartValues.TryGetValue(PartModifier, out float mod);
            PartValues.TryGetValue(PartAction, out float act);
            return (mod > 0.5f) ? act : 0f;
        }

        public override Vector2 EvaluateVector2() => Vector2.Zero;
        public override Vector3 EvaluateVector3() => Vector3.Zero;

        public override IEnumerable<string> GetPartNames()
        {
            yield return PartModifier;
            yield return PartAction;
        }
    }

    /// <summary>
    /// 双修饰键 + 动作键 组合：两个修饰键都按下时，动作键才有效
    /// </summary>
    public class TwoModifiersComposite : InputBindingComposite
    {
        public const string PartModifier1 = "modifier1";
        public const string PartModifier2 = "modifier2";
        public const string PartAction = "action";

        public TwoModifiersComposite(string name = "TwoModifiers") : base(name, InputActionType.Button) { }

        public override float Evaluate()
        {
            PartValues.TryGetValue(PartModifier1, out float mod1);
            PartValues.TryGetValue(PartModifier2, out float mod2);
            PartValues.TryGetValue(PartAction, out float act);
            return (mod1 > 0.5f && mod2 > 0.5f) ? act : 0f;
        }

        public override Vector2 EvaluateVector2() => Vector2.Zero;
        public override Vector3 EvaluateVector3() => Vector3.Zero;

        public override IEnumerable<string> GetPartNames()
        {
            yield return PartModifier1;
            yield return PartModifier2;
            yield return PartAction;
        }
    }

    /// <summary>
    /// 三维组合绑定：六个方向键 -> Vector3 (常用于调试/编辑器自由移动)
    /// </summary>
    public class Vector3Composite : InputBindingComposite
    {
        public const string PartForward = "forward";
        public const string PartBackward = "backward";
        public const string PartLeft = "left";
        public const string PartRight = "right";
        public const string PartUp = "up";
        public const string PartDown = "down";

        public Vector3Composite(string name = "3DVector") : base(name, InputActionType.Vector3) { }

        public override float Evaluate()
        {
            var v = EvaluateVector3();
            return MathF.Max(MathF.Max(MathF.Abs(v.X), MathF.Abs(v.Y)), MathF.Abs(v.Z));
        }

        public override Vector2 EvaluateVector2() => new Vector2(EvaluateVector3().X, EvaluateVector3().Y);

        public override Vector3 EvaluateVector3()
        {
            PartValues.TryGetValue(PartForward, out float fwd);
            PartValues.TryGetValue(PartBackward, out float bwd);
            PartValues.TryGetValue(PartLeft, out float left);
            PartValues.TryGetValue(PartRight, out float right);
            PartValues.TryGetValue(PartUp, out float up);
            PartValues.TryGetValue(PartDown, out float down);

            var result = new Vector3(right - left, up - down, fwd - bwd);
            float len = result.Length();
            return len > 1f ? Vector3.Normalize(result) : result;
        }

        public override IEnumerable<string> GetPartNames()
        {
            yield return PartForward;
            yield return PartBackward;
            yield return PartLeft;
            yield return PartRight;
            yield return PartUp;
            yield return PartDown;
        }
    }

    #endregion

    #region EnhancedInputAction —— 增强版输入动作

    /// <summary>
    /// 增强输入动作 —— 支持多绑定、修改器、触发器、交互的完整动作定义
    /// 包含完整状态机、多设备支持、组合绑定
    /// 事件委托: OnStarted / OnTriggered / OnPerformed / OnCompleted / OnCanceled
    /// </summary>
    public class EnhancedInputAction
    {
        /// <summary>动作名称</summary>
        public string Name { get; set; }

        /// <summary>动作类型</summary>
        public InputActionType ExpectedControlType { get; set; } = InputActionType.Button;

        /// <summary>描述</summary>
        public string Description { get; set; } = "";

        /// <summary>绑定列表</summary>
        public List<InputBinding> Bindings { get; set; } = new List<InputBinding>();

        /// <summary>组合绑定列表</summary>
        public Dictionary<string, InputBindingComposite> Composites { get; set; } = new Dictionary<string, InputBindingComposite>();

        /// <summary>当前动作状态</summary>
        public InputActionState State { get; private set; } = InputActionState.Waiting;

        /// <summary>当前动作相位</summary>
        public InputActionPhase Phase { get; private set; } = InputActionPhase.Waiting;

        /// <summary>当前值 (float)</summary>
        public float Value { get; private set; }

        /// <summary>当前值 (Vector2)</summary>
        public Vector2 Vector2Value { get; private set; }

        /// <summary>当前值 (Vector3)</summary>
        public Vector3 Vector3Value { get; private set; }

        /// <summary>原始值 (未经修改器处理)</summary>
        public float RawValue { get; private set; }

        /// <summary>持续时间 (按下后经过的时间)</summary>
        public float HoldTime { get; private set; }

        /// <summary>是否已启用</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>触发器 (决定何时触发 Performed)</summary>
        public InputTrigger Trigger { get; set; } = new PressTrigger();

        // ========== 动作事件委托 ==========
        /// <summary>Started: 动作从 Waiting -> Started (按下瞬间)</summary>
        public event Action<EnhancedInputAction> OnStarted;
        /// <summary>Performed/Triggered: 满足触发条件 (如 Press 瞬间, Hold 到达阈值)</summary>
        public event Action<EnhancedInputAction> OnPerformed;
        /// <summary>Completed: 动作值从有效变为无效 (释放瞬间, 平滑结束)</summary>
        public event Action<EnhancedInputAction> OnCompleted;
        /// <summary>Canceled: 动作被意外取消 (如上下文切换, 设备断开)</summary>
        public event Action<EnhancedInputAction> OnCanceled;
        /// <summary>Ongoing: 值持续变化中 (持续按住/摇杆移动)</summary>
        public event Action<EnhancedInputAction> OnOngoing;
        /// <summary>值变化时触发</summary>
        public event Action<EnhancedInputAction> OnValueChanged;

        /// <summary>状态开始时的回调 (C# event + callback)</summary>
        public Action<EnhancedInputAction> StartedCallback;
        /// <summary>执行时的回调</summary>
        public Action<EnhancedInputAction> PerformedCallback;
        /// <summary>完成时的回调</summary>
        public Action<EnhancedInputAction> CompletedCallback;
        /// <summary>取消时的回调</summary>
        public Action<EnhancedInputAction> CanceledCallback;

        // ========== 运行时内部状态 ==========
        internal float _controlValue;
        internal bool _controlHasValue;
        internal float _interactionHoldTime;
        internal float _triggerHeldTime;
        internal float _lastTriggerTime;
        internal int _tapCount;
        internal float _lastTapTime;

        public EnhancedInputAction(string name, InputActionType type = InputActionType.Button, string description = "")
        {
            Name = name;
            ExpectedControlType = type;
            Description = description;
        }

        /// <summary>添加绑定</summary>
        public EnhancedInputAction AddBinding(InputBinding binding)
        {
            Bindings.Add(binding);
            return this;
        }

        /// <summary>添加绑定（路径形式，便捷方法）</summary>
        public EnhancedInputAction AddBinding(string path, string groups = null)
        {
            var binding = new InputBinding(path) { Groups = groups };
            Bindings.Add(binding);
            return this;
        }

        /// <summary>添加组合绑定</summary>
        public EnhancedInputAction AddComposite(string name, InputBindingComposite composite, params (string partName, string path)[] parts)
        {
            Composites[name] = composite;
            foreach (var (partName, path) in parts)
            {
                Bindings.Add(new InputBinding(path)
                {
                    BindingType = InputBindingType.Composite,
                    CompositeName = name,
                    CompositePart = partName
                });
            }
            return this;
        }

        /// <summary>便捷方法：绑定 WASD -> 移动 Vector2</summary>
        public EnhancedInputAction BindWASD(string groups = null)
        {
            return AddComposite("WASD", new DpadComposite("WASD"),
                ("up", "Keyboard/W"), ("down", "Keyboard/S"),
                ("left", "Keyboard/A"), ("right", "Keyboard/D"));
        }

        /// <summary>便捷方法：绑定方向键 -> Vector2</summary>
        public EnhancedInputAction BindArrowKeys(string groups = null)
        {
            return AddComposite("Arrows", new DpadComposite("Arrows"),
                ("up", "Keyboard/UpArrow"), ("down", "Keyboard/DownArrow"),
                ("left", "Keyboard/LeftArrow"), ("right", "Keyboard/RightArrow"));
        }

        /// <summary>便捷方法：绑定游戏摇杆 -> Vector2</summary>
        public EnhancedInputAction BindGamepadLeftStick(string groups = null)
        {
            return AddBinding("Gamepad/LeftStick", groups);
        }

        /// <summary>便捷方法：绑定修饰键组合</summary>
        public EnhancedInputAction BindWithModifier(string actionPath, string modifierPath, string groups = null)
        {
            return AddComposite("Modifier", new OneModifierComposite("Modifier"),
                ("modifier", modifierPath), ("action", actionPath));
        }

        /// <summary>设置触发器</summary>
        public EnhancedInputAction WithTrigger(InputTrigger trigger)
        {
            Trigger = trigger;
            return this;
        }

        /// <summary>重置运行时状态</summary>
        public void Reset()
        {
            SetState(InputActionState.Waiting);
            Value = 0f;
            Vector2Value = Vector2.Zero;
            Vector3Value = Vector3.Zero;
            RawValue = 0f;
            HoldTime = 0f;
            _controlValue = 0f;
            _controlHasValue = false;
            _interactionHoldTime = 0f;
            _triggerHeldTime = 0f;
            _tapCount = 0;
        }

        /// <summary>更新动作状态（由 InputActionManager 每帧调用）-- 运行时动作评估器核心</summary>
        internal void Update(float deltaTime)
        {
            if (!Enabled) return;

            // 1. 收集所有绑定的值 (运行时动作评估器: 值收集阶段)
            float maxValue = 0f;
            bool anyHasValue = false;
            Vector2 maxVec2 = Vector2.Zero;
            Vector3 maxVec3 = Vector3.Zero;

            // 处理组合绑定
            foreach (var composite in Composites.Values)
            {
                switch (ExpectedControlType)
                {
                    case InputActionType.Button:
                    case InputActionType.Axis:
                        float cVal = composite.Evaluate();
                        if (Math.Abs(cVal) > Math.Abs(maxValue)) maxValue = cVal;
                        anyHasValue = anyHasValue || Math.Abs(cVal) > 0.001f;
                        break;
                    case InputActionType.Vector2:
                        var cv2 = composite.EvaluateVector2();
                        if (cv2.LengthSquared() > maxVec2.LengthSquared()) maxVec2 = cv2;
                        anyHasValue = anyHasValue || cv2.LengthSquared() > 0.0001f;
                        break;
                    case InputActionType.Vector3:
                        var cv3 = composite.EvaluateVector3();
                        if (cv3.LengthSquared() > maxVec3.LengthSquared()) maxVec3 = cv3;
                        anyHasValue = anyHasValue || cv3.LengthSquared() > 0.0001f;
                        break;
                }
            }

            // 处理直接绑定 (运行时动作评估器: 修饰器累积阶段)
            foreach (var binding in Bindings)
            {
                if (!binding.Enabled) continue;
                if (binding.BindingType == InputBindingType.Composite) continue;

                float rawVal = binding.RawValue;
                float processedVal = rawVal;

                // 累积应用修改器管线
                foreach (var mod in binding.Modifiers)
                {
                    processedVal = mod.ProcessValue(processedVal);
                }

                // 累积应用处理器管线
                foreach (var proc in binding.Processors)
                {
                    processedVal = proc.Process(processedVal);
                }

                binding.ProcessedValue = processedVal;
                binding.HasValue = Math.Abs(processedVal) > 0.001f;

                if (binding.HasValue)
                {
                    float weighted = processedVal * binding.Weight;
                    if (Math.Abs(weighted) > Math.Abs(maxValue)) maxValue = weighted;
                    anyHasValue = true;
                }
            }

            // 2. 更新原始值和控制值
            RawValue = maxValue;
            _controlValue = maxValue;
            _controlHasValue = anyHasValue;

            // 3. 更新时间和交互
            if (anyHasValue)
            {
                HoldTime += deltaTime;
                _interactionHoldTime += deltaTime;
                _triggerHeldTime += deltaTime;
            }
            else
            {
                HoldTime = 0f;
                _interactionHoldTime = 0f;
                _triggerHeldTime = 0f;
            }

            // 4. 状态机更新 (运行时动作评估器: 触发器检测阶段)
            UpdateStateMachine(deltaTime, anyHasValue);

            // 5. 设置值
            Value = maxValue;
            Vector2Value = maxVec2;
            Vector3Value = maxVec3;

            // 6. 值变化回调
            OnValueChanged?.Invoke(this);
        }

        /// <summary>状态机更新 -- 触发器检测核心</summary>
        private void UpdateStateMachine(float deltaTime, bool hasInput)
        {
            switch (State)
            {
                case InputActionState.Waiting:
                    if (hasInput)
                    {
                        SetState(InputActionState.Started);
                    }
                    break;

                case InputActionState.Started:
                    if (hasInput)
                    {
                        // 评估触发器
                        var triggerResult = Trigger?.Evaluate(this, Value, _controlValue) ?? TriggerState.None;
                        if (triggerResult == TriggerState.Triggered)
                        {
                            SetState(InputActionState.Performed);
                        }
                        else if (triggerResult == TriggerState.Potential)
                        {
                            State = InputActionState.Ongoing;
                        }
                    }
                    else
                    {
                        // 输入消失了, 触发 Completed (正常释放)
                        SetState(InputActionState.Waiting);
                        OnCompleted?.Invoke(this);
                        CompletedCallback?.Invoke(this);
                    }
                    break;

                case InputActionState.Ongoing:
                    if (hasInput)
                    {
                        var triggerResult = Trigger?.Evaluate(this, Value, _controlValue) ?? TriggerState.None;
                        if (triggerResult == TriggerState.Triggered)
                        {
                            SetState(InputActionState.Performed);
                        }
                        OnOngoing?.Invoke(this);
                    }
                    else
                    {
                        SetState(InputActionState.Waiting);
                        OnCompleted?.Invoke(this);
                        CompletedCallback?.Invoke(this);
                    }
                    break;

                case InputActionState.Performed:
                    if (hasInput)
                    {
                        // 某些触发器可以持续触发
                        var triggerResult = Trigger?.Evaluate(this, Value, _controlValue) ?? TriggerState.None;
                        if (triggerResult == TriggerState.Triggered)
                        {
                            OnPerformed?.Invoke(this);
                            PerformedCallback?.Invoke(this);
                        }
                        else
                        {
                            State = InputActionState.Ongoing;
                        }
                    }
                    else
                    {
                        // 输入结束, 先完成再取消
                        OnCompleted?.Invoke(this);
                        CompletedCallback?.Invoke(this);
                        SetState(InputActionState.Waiting);
                    }
                    break;

                case InputActionState.Canceled:
                    // 取消后回到等待状态
                    SetState(InputActionState.Waiting);
                    if (hasInput)
                    {
                        SetState(InputActionState.Started);
                    }
                    break;
            }
        }

        /// <summary>设置新状态并触发对应事件</summary>
        private void SetState(InputActionState newState)
        {
            if (State == newState) return;

            var oldState = State;
            State = newState;

            // 同步相位
            Phase = newState switch
            {
                InputActionState.Waiting => InputActionPhase.Waiting,
                InputActionState.Started => InputActionPhase.Started,
                InputActionState.Performed => InputActionPhase.Triggered,
                InputActionState.Ongoing => InputActionPhase.Ongoing,
                InputActionState.Canceled => InputActionPhase.Canceled,
                _ => InputActionPhase.Waiting
            };

            // 触发事件委托
            switch (newState)
            {
                case InputActionState.Started:
                    OnStarted?.Invoke(this);
                    StartedCallback?.Invoke(this);
                    break;
                case InputActionState.Performed:
                    OnPerformed?.Invoke(this);
                    PerformedCallback?.Invoke(this);
                    break;
                case InputActionState.Canceled:
                    OnCanceled?.Invoke(this);
                    CanceledCallback?.Invoke(this);
                    break;
            }
        }

        /// <summary>是否在当前帧被触发 (Performed 状态)</summary>
        public bool WasPerformedThisFrame => State == InputActionState.Performed;

        /// <summary>是否在当前帧开始 (Started 状态)</summary>
        public bool WasStartedThisFrame => State == InputActionState.Started;

        /// <summary>是否在当前帧取消 (Canceled 状态)</summary>
        public bool WasCanceledThisFrame => State == InputActionState.Canceled;

        /// <summary>读取当前值（带类型的便捷方法）</summary>
        public T ReadValue<T>() where T : struct
        {
            if (typeof(T) == typeof(float)) return (T)(object)Value;
            if (typeof(T) == typeof(Vector2)) return (T)(object)Vector2Value;
            if (typeof(T) == typeof(Vector3)) return (T)(object)Vector3Value;
            return default;
        }

        public override string ToString() => $"Action[{Name}] State={State} Value={Value:F2}";
    }

    #endregion

    #region InputActionMap —— 动作映射表 (ActionMap 分组)

    /// <summary>
    /// 输入动作映射表 —— ActionMap: 将相关动作组织在一起，可以整体启用/禁用
    /// 类似 UE 的 Input Mapping Context 或 Unity 的 Action Map
    /// 支持按优先级叠加: 多个 ActionMap 可以同时启用，优先级高的覆盖低的
    /// </summary>
    public class InputActionMap
    {
        /// <summary>映射表名称</summary>
        public string Name { get; set; }

        /// <summary>显示名称</summary>
        public string DisplayName { get; set; }

        /// <summary>映射表描述</summary>
        public string Description { get; set; } = "";

        /// <summary>优先级 (数值越小优先级越高，默认 0)</summary>
        public int Priority { get; set; } = 0;

        /// <summary>是否启用</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>此映射表中的动作列表</summary>
        public Dictionary<string, EnhancedInputAction> Actions { get; } = new Dictionary<string, EnhancedInputAction>();

        /// <summary>适用的设备方案 (null = 所有方案)</summary>
        public string ControlScheme { get; set; }

        /// <summary>映射表启用/禁用事件</summary>
        public event Action<InputActionMap, bool> OnEnabledChanged;
        /// <summary>动作触发事件</summary>
        public event Action<EnhancedInputAction> OnActionTriggered;
        /// <summary>动作启动事件</summary>
        public event Action<EnhancedInputAction> OnActionStarted;
        /// <summary>动作完成事件</summary>
        public event Action<EnhancedInputAction> OnActionCompleted;
        /// <summary>动作取消事件</summary>
        public event Action<EnhancedInputAction> OnActionCanceled;

        public InputActionMap(string name, string displayName = null, string description = "")
        {
            Name = name;
            DisplayName = displayName ?? name;
            Description = description;
        }

        /// <summary>添加动作</summary>
        public EnhancedInputAction AddAction(string name, InputActionType type = InputActionType.Button, string description = "")
        {
            if (Actions.ContainsKey(name))
                throw new ArgumentException($"Action '{name}' already exists in map '{Name}'");

            var action = new EnhancedInputAction(name, type, description);
            Actions[name] = action;

            // 绑定映射表级事件
            action.OnPerformed += a => OnActionTriggered?.Invoke(a);
            action.OnStarted += a => OnActionStarted?.Invoke(a);
            action.OnCompleted += a => OnActionCompleted?.Invoke(a);
            action.OnCanceled += a => OnActionCanceled?.Invoke(a);
            return action;
        }

        /// <summary>获取动作</summary>
        public EnhancedInputAction GetAction(string name)
        {
            return Actions.TryGetValue(name, out var a) ? a : null;
        }

        /// <summary>尝试获取动作</summary>
        public bool TryGetAction(string name, out EnhancedInputAction action)
        {
            return Actions.TryGetValue(name, out action);
        }

        /// <summary>移除动作</summary>
        public bool RemoveAction(string name)
        {
            return Actions.Remove(name);
        }

        /// <summary>启用此映射表</summary>
        public void Enable()
        {
            Enabled = true;
            OnEnabledChanged?.Invoke(this, true);
        }

        /// <summary>禁用此映射表</summary>
        public void Disable()
        {
            Enabled = false;
            OnEnabledChanged?.Invoke(this, false);
            // 取消所有进行中的动作
            foreach (var action in Actions.Values)
            {
                action.Reset();
            }
        }

        /// <summary>更新所有动作</summary>
        internal void Update(float deltaTime)
        {
            if (!Enabled) return;
            foreach (var action in Actions.Values)
            {
                action.Update(deltaTime);
            }
        }

        /// <summary>获取所有动作名称</summary>
        public IEnumerable<string> GetActionNames() => Actions.Keys;

        /// <summary>获取启用状态下有效的动作数量</summary>
        public int ActiveActionCount => Enabled ? Actions.Count : 0;
    }

    #endregion

    #region InputActionAsset —— 输入动作资产

    /// <summary>
    /// 输入动作资产 —— 一组动作映射表的集合，可保存/加载
    /// 类似 UE 的 Input Action Asset 或 Unity 的 Input Action Asset (.inputactions)
    /// </summary>
    public class InputActionAsset
    {
        /// <summary>资产名称</summary>
        public string Name { get; set; }

        /// <summary>资产版本</summary>
        public int Version { get; set; } = 1;

        /// <summary>映射表集合</summary>
        public Dictionary<string, InputActionMap> ActionMaps { get; } = new Dictionary<string, InputActionMap>();

        /// <summary>控制方案列表 (由 InputRebindingSystem 填充)</summary>
        public List<ControlScheme> ControlSchemes { get; set; } = new List<ControlScheme>();

        /// <summary>绑定覆盖列表 (用户自定义重绑定)</summary>
        public List<BindingOverride> BindingOverrides { get; set; } = new List<BindingOverride>();

        public InputActionAsset(string name = "Default")
        {
            Name = name;
        }

        /// <summary>添加映射表</summary>
        public InputActionMap AddActionMap(string name, string displayName = null, string description = "")
        {
            if (ActionMaps.ContainsKey(name))
                throw new ArgumentException($"Action map '{name}' already exists in asset '{Name}'");

            var map = new InputActionMap(name, displayName, description);
            ActionMaps[name] = map;
            return map;
        }

        /// <summary>获取映射表</summary>
        public InputActionMap GetActionMap(string name)
        {
            return ActionMaps.TryGetValue(name, out var m) ? m : null;
        }

        /// <summary>尝试获取映射表</summary>
        public bool TryGetActionMap(string name, out InputActionMap map)
        {
            return ActionMaps.TryGetValue(name, out map);
        }

        /// <summary>移除映射表</summary>
        public bool RemoveActionMap(string name)
        {
            return ActionMaps.Remove(name);
        }

        /// <summary>启用所有映射表</summary>
        public void Enable()
        {
            foreach (var map in ActionMaps.Values) map.Enable();
        }

        /// <summary>禁用所有映射表</summary>
        public void Disable()
        {
            foreach (var map in ActionMaps.Values) map.Disable();
        }

        /// <summary>按名称启用特定映射表</summary>
        public void EnableMap(string mapName)
        {
            if (ActionMaps.TryGetValue(mapName, out var map)) map.Enable();
        }

        /// <summary>按名称禁用特定映射表</summary>
        public void DisableMap(string mapName)
        {
            if (ActionMaps.TryGetValue(mapName, out var map)) map.Disable();
        }

        /// <summary>获取所有映射表</summary>
        public IEnumerable<InputActionMap> GetAllMaps() => ActionMaps.Values;

        /// <summary>查找资产中所有满足条件的动作</summary>
        public List<EnhancedInputAction> FindActions(Predicate<EnhancedInputAction> predicate)
        {
            var result = new List<EnhancedInputAction>();
            foreach (var map in ActionMaps.Values)
            {
                foreach (var action in map.Actions.Values)
                {
                    if (predicate(action)) result.Add(action);
                }
            }
            return result;
        }

        /// <summary>按名称查找动作 (跨所有映射表)</summary>
        public EnhancedInputAction FindAction(string actionName)
        {
            foreach (var map in ActionMaps.Values)
            {
                if (map.TryGetAction(actionName, out var action)) return action;
            }
            return null;
        }
    }

    #endregion

    #region ActionBindingResolver —— 动作绑定解析器 (JSON/二进制加载)

    /// <summary>
    /// 动作绑定解析器 —— 从 JSON 或二进制配置加载映射配置
    /// 支持:
    ///   - JSON 格式: 可读性强，编辑器友好
    ///   - 二进制格式: 加载速度快，运行时热重载
    ///   - 版本迁移: 自动处理不同版本间的格式差异
    /// </summary>
    public static class ActionBindingResolver
    {
        /// <summary>当前配置版本</summary>
        public const int CurrentVersion = 3;

        /// <summary>JSON 序列化选项</summary>
        private static readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
        };

        // ========== JSON 加载 ==========

        /// <summary>
        /// 从 JSON 字符串加载 InputActionAsset
        /// </summary>
        public static InputActionAsset LoadFromJson(string json)
        {
            if (string.IsNullOrEmpty(json))
                throw new ArgumentException("JSON 字符串为空");

            var dto = JsonSerializer.Deserialize<ActionAssetDTO>(json, _jsonOptions);
            if (dto == null)
                throw new InvalidOperationException("JSON 反序列化失败");

            dto = MigrateVersion(dto);
            return BuildAsset(dto);
        }

        /// <summary>
        /// 从 JSON 文件加载 InputActionAsset
        /// </summary>
        public static InputActionAsset LoadFromJsonFile(string filePath)
        {
            string json = File.ReadAllText(filePath);
            return LoadFromJson(json);
        }

        // ========== JSON 保存 ==========

        /// <summary>
        /// 将 InputActionAsset 保存为 JSON 字符串
        /// </summary>
        public static string SaveToJson(InputActionAsset asset)
        {
            if (asset == null) throw new ArgumentNullException(nameof(asset));
            var dto = AssetToDTO(asset);
            return JsonSerializer.Serialize(dto, _jsonOptions);
        }

        /// <summary>
        /// 将 InputActionAsset 保存为 JSON 文件
        /// </summary>
        public static void SaveToJsonFile(InputActionAsset asset, string filePath)
        {
            string json = SaveToJson(asset);
            File.WriteAllText(filePath, json);
        }

        // ========== 二进制加载/保存 ==========

        /// <summary>
        /// 从二进制数据加载 InputActionAsset (JSON + Base64 编码)
        /// </summary>
        public static InputActionAsset LoadFromBinary(byte[] data)
        {
            string json = System.Text.Encoding.UTF8.GetString(data);
            return LoadFromJson(json);
        }

        /// <summary>
        /// 从二进制文件加载 InputActionAsset
        /// </summary>
        public static InputActionAsset LoadFromBinaryFile(string filePath)
        {
            byte[] data = File.ReadAllBytes(filePath);
            return LoadFromBinary(data);
        }

        /// <summary>
        /// 将 InputActionAsset 保存为二进制数据
        /// </summary>
        public static byte[] SaveToBinary(InputActionAsset asset)
        {
            string json = SaveToJson(asset);
            return System.Text.Encoding.UTF8.GetBytes(json);
        }

        /// <summary>
        /// 将 InputActionAsset 保存为二进制文件
        /// </summary>
        public static void SaveToBinaryFile(InputActionAsset asset, string filePath)
        {
            byte[] data = SaveToBinary(asset);
            File.WriteAllBytes(filePath, data);
        }

        // ========== 版本迁移 ==========

        /// <summary>
        /// 版本迁移 —— 将旧版本 DTO 升级到当前版本
        /// </summary>
        private static ActionAssetDTO MigrateVersion(ActionAssetDTO dto)
        {
            while (dto.Version < CurrentVersion)
            {
                dto.Version++;
                // 版本 2 -> 3: 添加默认绑定覆盖支持
                if (dto.Version == 3 && dto.BindingOverrides == null)
                {
                    dto.BindingOverrides = new List<BindingOverrideDTΟ>();
                }
            }
            return dto;
        }

        // ========== DTO -> Asset 转换 ==========

        private static InputActionAsset BuildAsset(ActionAssetDTO dto)
        {
            var asset = new InputActionAsset(dto.Name ?? "Unnamed") { Version = dto.Version };

            if (dto.Maps != null)
            {
                foreach (var mapDto in dto.Maps)
                {
                    var map = asset.AddActionMap(mapDto.Name, mapDto.DisplayName, mapDto.Description);
                    map.Priority = mapDto.Priority;
                    map.ControlScheme = mapDto.ControlScheme;

                    if (mapDto.Actions != null)
                    {
                        foreach (var actionDto in mapDto.Actions)
                        {
                            var action = map.AddAction(actionDto.Name,
                                actionDto.Type, actionDto.Description ?? "");

                            action.Enabled = actionDto.Enabled;

                            if (actionDto.Bindings != null)
                            {
                                foreach (var bindingDto in actionDto.Bindings)
                                {
                                    var binding = new InputBinding(bindingDto.Path ?? "")
                                    {
                                        Groups = bindingDto.Groups,
                                        Weight = bindingDto.Weight,
                                        Enabled = bindingDto.Enabled
                                    };

                                    if (bindingDto.CompositeName != null)
                                    {
                                        binding.BindingType = InputBindingType.Composite;
                                        binding.CompositeName = bindingDto.CompositeName;
                                        binding.CompositePart = bindingDto.CompositePart;
                                    }

                                    action.Bindings.Add(binding);
                                }
                            }

                            if (actionDto.TriggerType != null)
                            {
                                action.Trigger = CreateTriggerFromType(actionDto.TriggerType,
                                    actionDto.TriggerParams);
                            }
                        }
                    }
                }
            }

            if (dto.BindingOverrides != null)
            {
                foreach (var bo in dto.BindingOverrides)
                {
                    asset.BindingOverrides.Add(new BindingOverride(
                        bo.ActionMap, bo.Action, bo.OriginalBinding,
                        bo.OverrideBinding, bo.ControlScheme));
                }
            }

            return asset;
        }

        // ========== Asset -> DTO 转换 ==========

        private static ActionAssetDTO AssetToDTO(InputActionAsset asset)
        {
            var dto = new ActionAssetDTO
            {
                Name = asset.Name,
                Version = asset.Version,
                Maps = new List<ActionMapDTO>()
            };

            foreach (var mapKvp in asset.ActionMaps)
            {
                var map = mapKvp.Value;
                var mapDto = new ActionMapDTO
                {
                    Name = map.Name,
                    DisplayName = map.DisplayName,
                    Description = map.Description,
                    Priority = map.Priority,
                    ControlScheme = map.ControlScheme,
                    Actions = new List<ActionDTO>()
                };

                foreach (var actionKvp in map.Actions)
                {
                    var action = actionKvp.Value;
                    var actionDto = new ActionDTO
                    {
                        Name = action.Name,
                        Type = action.ExpectedControlType,
                        Description = action.Description,
                        Enabled = action.Enabled,
                        Bindings = new List<BindingDTO>()
                    };

                    foreach (var binding in action.Bindings)
                    {
                        actionDto.Bindings.Add(new BindingDTO
                        {
                            Path = binding.Path,
                            Groups = binding.Groups,
                            Weight = binding.Weight,
                            Enabled = binding.Enabled,
                            CompositeName = binding.CompositeName,
                            CompositePart = binding.CompositePart
                        });
                    }

                    actionDto.TriggerType = action.Trigger?.Name ?? "Press";

                    mapDto.Actions.Add(actionDto);
                }

                dto.Maps.Add(mapDto);
            }

            if (asset.BindingOverrides != null)
            {
                dto.BindingOverrides = new List<BindingOverrideDTΟ>();
                foreach (var bo in asset.BindingOverrides)
                {
                    dto.BindingOverrides.Add(new BindingOverrideDTΟ
                    {
                        ActionMap = bo.ActionMap,
                        Action = bo.Action,
                        OriginalBinding = bo.OriginalBinding,
                        OverrideBinding = bo.OverrideBinding,
                        ControlScheme = bo.ControlScheme
                    });
                }
            }

            return dto;
        }

        // ========== 触发器工厂 ==========

        /// <summary>根据类型名称创建触发器实例</summary>
        public static InputTrigger CreateTriggerFromType(string typeName, Dictionary<string, float> parameters = null)
        {
            parameters ??= new Dictionary<string, float>();

            return typeName switch
            {
                "Press" => new PressTrigger(),
                "Release" => new ReleaseTrigger(),
                "Hold" => new HoldTrigger(
                    parameters.TryGetValue("holdTime", out float ht) ? ht : 0.2f),
                "Tap" => new TapTrigger(
                    parameters.TryGetValue("maxHoldTime", out float mht) ? mht : 0.3f),
                "SlowTap" => new SlowTapTrigger(
                    parameters.TryGetValue("minHoldTime", out float minHt) ? minHt : 0.5f),
                "MultiTap" => new MultiTapTrigger(
                    (int)(parameters.TryGetValue("tapCount", out float tc) ? tc : 2),
                    parameters.TryGetValue("tapInterval", out float ti) ? ti : 0.4f),
                "Threshold" => new ThresholdTrigger(
                    parameters.TryGetValue("threshold", out float th) ? th : 0.5f),
                "Pulse" => new PulseTrigger(
                    parameters.TryGetValue("interval", out float interval) ? interval : 0.1f,
                    parameters.TryGetValue("initialDelay", out float initDelay) ? initDelay : 0.5f),
                _ => new PressTrigger()
            };
        }

        // ========== DTO 定义 ==========

        private class ActionAssetDTO
        {
            public string Name { get; set; }
            public int Version { get; set; } = CurrentVersion;
            public List<ActionMapDTO> Maps { get; set; }
            public List<BindingOverrideDTΟ> BindingOverrides { get; set; }
        }

        private class ActionMapDTO
        {
            public string Name { get; set; }
            public string DisplayName { get; set; }
            public string Description { get; set; }
            public int Priority { get; set; }
            public string ControlScheme { get; set; }
            public List<ActionDTO> Actions { get; set; }
        }

        private class ActionDTO
        {
            public string Name { get; set; }
            public InputActionType Type { get; set; } = InputActionType.Button;
            public string Description { get; set; }
            public bool Enabled { get; set; } = true;
            public List<BindingDTO> Bindings { get; set; }
            public string TriggerType { get; set; }
            public Dictionary<string, float> TriggerParams { get; set; }
        }

        private class BindingDTO
        {
            public string Path { get; set; }
            public string Groups { get; set; }
            public float Weight { get; set; } = 1f;
            public bool Enabled { get; set; } = true;
            public string CompositeName { get; set; }
            public string CompositePart { get; set; }
        }

        private class BindingOverrideDTΟ
        {
            public string ActionMap { get; set; }
            public string Action { get; set; }
            public string OriginalBinding { get; set; }
            public string OverrideBinding { get; set; }
            public string ControlScheme { get; set; }
        }
    }

    #endregion

    #region BindingOverride —— 绑定覆盖

    /// <summary>
    /// 绑定覆盖记录 —— 记录用户对绑定的自定义修改
    /// </summary>
    public class BindingOverride
    {
        /// <summary>动作所在映射表</summary>
        public string ActionMap { get; set; }
        /// <summary>动作名称</summary>
        public string Action { get; set; }
        /// <summary>原绑定路径</summary>
        public string OriginalBinding { get; set; }
        /// <summary>新绑定路径</summary>
        public string OverrideBinding { get; set; }
        /// <summary>适用的控制方案 (null = 全局)</summary>
        public string ControlScheme { get; set; }

        public BindingOverride() { }

        public BindingOverride(string actionMap, string action, string originalBinding, string overrideBinding, string controlScheme = null)
        {
            ActionMap = actionMap;
            Action = action;
            OriginalBinding = originalBinding;
            OverrideBinding = overrideBinding;
            ControlScheme = controlScheme;
        }
    }

    #endregion

    #region InputActionManager —— 全局动作管理器

    /// <summary>
    /// 输入动作管理器 —— 管理所有 InputActionAsset 的注册、更新、查询
    /// 是增强输入系统的入口点，负责:
    ///   - 资产注册/注销
    ///   - 按优先级排序的动作更新
    ///   - 全局处理器管线
    ///   - 跨资产动作查找
    ///   - 动作事件分发 (Started/Triggered/Completed/Canceled)
    /// </summary>
    public class InputActionManager
    {
        private static InputActionManager _instance;
        /// <summary>全局单例</summary>
        public static InputActionManager Instance
        {
            get
            {
                if (_instance == null) _instance = new InputActionManager();
                return _instance;
            }
        }

        /// <summary>已注册的资产列表</summary>
        private readonly List<InputActionAsset> _assets = new List<InputActionAsset>();

        /// <summary>全局处理器管线</summary>
        public List<InputProcessor> GlobalProcessors { get; } = new List<InputProcessor>();

        /// <summary>设备系统引用</summary>
        public InputDeviceManager DeviceManager { get; set; }

        // ========== 动作事件委托 ==========
        /// <summary>动作开始 (按下瞬间)</summary>
        public event Action<EnhancedInputAction> OnActionStarted;
        /// <summary>动作执行/触发 (满足触发器条件)</summary>
        public event Action<EnhancedInputAction> OnActionPerformed;
        /// <summary>动作完成 (正常释放)</summary>
        public event Action<EnhancedInputAction> OnActionCompleted;
        /// <summary>动作取消 (意外取消)</summary>
        public event Action<EnhancedInputAction> OnActionCanceled;

        public InputActionManager() { }

        /// <summary>注册资产</summary>
        public void RegisterAsset(InputActionAsset asset)
        {
            if (!_assets.Contains(asset))
            {
                _assets.Add(asset);

                // 监听资产中所有动作的事件
                foreach (var map in asset.ActionMaps.Values)
                {
                    HookActionMapEvents(map);
                }
            }
        }

        /// <summary>取消注册资产</summary>
        public void UnregisterAsset(InputActionAsset asset)
        {
            if (_assets.Remove(asset))
            {
                foreach (var map in asset.ActionMaps.Values)
                {
                    UnhookActionMapEvents(map);
                }
            }
        }

        /// <summary>从 JSON 文件加载并注册资产</summary>
        public InputActionAsset LoadAndRegisterAsset(string jsonOrFilePath, bool isFilePath = false)
        {
            InputActionAsset asset = isFilePath
                ? ActionBindingResolver.LoadFromJsonFile(jsonOrFilePath)
                : ActionBindingResolver.LoadFromJson(jsonOrFilePath);
            RegisterAsset(asset);
            return asset;
        }

        /// <summary>从二进制文件加载并注册资产</summary>
        public InputActionAsset LoadAndRegisterBinary(string filePath)
        {
            InputActionAsset asset = ActionBindingResolver.LoadFromBinaryFile(filePath);
            RegisterAsset(asset);
            return asset;
        }

        /// <summary>保存资产到文件</summary>
        public void SaveAsset(InputActionAsset asset, string filePath, bool binary = false)
        {
            if (binary)
                ActionBindingResolver.SaveToBinaryFile(asset, filePath);
            else
                ActionBindingResolver.SaveToJsonFile(asset, filePath);
        }

        private void HookActionMapEvents(InputActionMap map)
        {
            foreach (var action in map.Actions.Values)
            {
                action.OnStarted += HandleActionStarted;
                action.OnPerformed += HandleActionPerformed;
                action.OnCanceled += HandleActionCanceled;
            }

            map.OnActionCompleted += HandleActionCompleted;
        }

        private void UnhookActionMapEvents(InputActionMap map)
        {
            foreach (var action in map.Actions.Values)
            {
                action.OnStarted -= HandleActionStarted;
                action.OnPerformed -= HandleActionPerformed;
                action.OnCanceled -= HandleActionCanceled;
            }

            map.OnActionCompleted -= HandleActionCompleted;
        }

        private void HandleActionStarted(EnhancedInputAction action) => OnActionStarted?.Invoke(action);
        private void HandleActionPerformed(EnhancedInputAction action) => OnActionPerformed?.Invoke(action);
        private void HandleActionCanceled(EnhancedInputAction action) => OnActionCanceled?.Invoke(action);
        private void HandleActionCompleted(EnhancedInputAction action) => OnActionCompleted?.Invoke(action);

        /// <summary>添加全局处理器</summary>
        public void AddGlobalProcessor(InputProcessor processor)
        {
            GlobalProcessors.Add(processor);
            GlobalProcessors.Sort((a, b) => a.Priority.CompareTo(b.Priority));
        }

        /// <summary>移除全局处理器</summary>
        public bool RemoveGlobalProcessor(InputProcessor processor) => GlobalProcessors.Remove(processor);

        /// <summary>每帧更新所有已注册的资产</summary>
        public void Update(float deltaTime)
        {
            // 收集所有启用的映射表，按优先级排序
            var allMaps = new List<(InputActionMap map, int priority)>();
            foreach (var asset in _assets)
            {
                foreach (var map in asset.ActionMaps.Values)
                {
                    if (map.Enabled)
                        allMaps.Add((map, map.Priority));
                }
            }
            allMaps.Sort((a, b) => a.priority.CompareTo(b.priority));

            // 依次更新每个映射表
            foreach (var (map, _) in allMaps)
            {
                map.Update(deltaTime);
            }
        }

        /// <summary>启用所有资产</summary>
        public void EnableAll()
        {
            foreach (var asset in _assets) asset.Enable();
        }

        /// <summary>禁用所有资产</summary>
        public void DisableAll()
        {
            foreach (var asset in _assets) asset.Disable();
        }

        /// <summary>按名称启用映射表</summary>
        public void EnableMap(string mapName)
        {
            foreach (var asset in _assets) asset.EnableMap(mapName);
        }

        /// <summary>按名称禁用映射表</summary>
        public void DisableMap(string mapName)
        {
            foreach (var asset in _assets) asset.DisableMap(mapName);
        }

        /// <summary>查找动作 (跨所有资产)</summary>
        public EnhancedInputAction FindAction(string actionName)
        {
            foreach (var asset in _assets)
            {
                var action = asset.FindAction(actionName);
                if (action != null) return action;
            }
            return null;
        }

        /// <summary>查找指定映射表中的动作</summary>
        public EnhancedInputAction FindAction(string mapName, string actionName)
        {
            foreach (var asset in _assets)
            {
                if (asset.TryGetActionMap(mapName, out var map))
                {
                    if (map.TryGetAction(actionName, out var action)) return action;
                }
            }
            return null;
        }

        /// <summary>获取所有已注册资产</summary>
        public IReadOnlyList<InputActionAsset> GetAssets() => _assets.AsReadOnly();

        /// <summary>重置所有动作状态</summary>
        public void ResetAllActions()
        {
            foreach (var asset in _assets)
            {
                foreach (var map in asset.ActionMaps.Values)
                {
                    foreach (var action in map.Actions.Values)
                    {
                        action.Reset();
                    }
                }
            }
        }

        /// <summary>重置所有动作的触发器内部状态（如 tapCount）</summary>
        public void ResetAllTriggers()
        {
            foreach (var asset in _assets)
            {
                foreach (var map in asset.ActionMaps.Values)
                {
                    foreach (var action in map.Actions.Values)
                    {
                        action._tapCount = 0;
                        action._lastTapTime = 0f;
                        action._triggerHeldTime = 0f;
                        action._interactionHoldTime = 0f;
                    }
                }
            }
        }
    }

    #endregion
}
