// =====================================================================
// EnhancedInputSystem.cs — 山雨引擎 UE6 级增强输入系统
// 命名空间: ShanyuEngine.Input
// =====================================================================
// 功能概述:
//   1. InputMappingContext : 映射上下文，按优先级叠加，支持条件绑定
//   2. KeyBinding          : Action → 物理键映射 (键盘/鼠标/手柄轴/触摸手势)
//   3. InputModifier        : 修改器 (DeadZone/Swizzle/Invert/Scalar/Smoothing)
//   4. InputTrigger         : 触发器 (Press/Release/Hold/Pulse/MultiTap/DualAxis)
//   5. PlayerInputComponent : 每个玩家独立的输入组件
//   6. InputDevice 管理     : 键盘/鼠标/手柄/触摸，自动检测热插拔
//   7. 手柄支持             : Gamepad 映射、模拟摇杆、扳机阈值、震动反馈
//   8. 触摸输入             : 多指触摸、手势识别 (滑动/捏合/旋转/长按)
//   9. 输入预处理队列       : 延迟注入虚拟输入
// =====================================================================

using System;
using System.Collections.Generic;
using System.Numerics;

using InputDeviceType = ShanyuEngine.Input.Enhanced.InputDeviceType;
namespace ShanyuEngine.Input
{
    // =================================================================
    // #region 全局命名动作常量 — 预定义的标准动作名称
    // =================================================================

    /// <summary>
    /// 标准输入动作名称 — 引擎内置的常用动作 ID
    /// </summary>
    public static class InputActionNames
    {
        // 移动
        public const string Move       = "Move";
        public const string Look       = "Look";
        public const string Jump       = "Jump";
        public const string Crouch     = "Crouch";
        public const string Sprint     = "Sprint";

        // 交互
        public const string Interact   = "Interact";
        public const string Use        = "Use";
        public const string Fire       = "Fire";
        public const string AltFire    = "AltFire";
        public const string Reload     = "Reload";
        public const string Throw      = "Throw";
        public const string Melee      = "Melee";

        // 导航
        public const string Navigate   = "Navigate";
        public const string Confirm    = "Confirm";
        public const string Cancel     = "Cancel";
        public const string Menu       = "Menu";
        public const string Pause      = "Pause";

        // 武器切换
        public const string WeaponNext = "WeaponNext";
        public const string WeaponPrev = "WeaponPrev";

        // 编辑器
        public const string Select     = "Select";
        public const string Delete     = "Delete";
        public const string Undo       = "Undo";
        public const string Redo       = "Redo";
        public const string Duplicate  = "Duplicate";
    }

    // =================================================================
    // #region KeyBinding — 动作到物理键的完整映射
    // =================================================================

    /// <summary>
    /// 键绑定源类型 — 绑定可以来自哪些输入设备
    /// </summary>
    public enum BindingSourceType
    {
        Keyboard,
        Mouse,
        GamepadAxis,
        GamepadButton,
        Touch,
        Gesture,
        Custom
    }

    /// <summary>
    /// 键绑定 — 将一个输入动作绑定到具体的物理控制
    /// 支持键盘按键、鼠标按键/轴、手柄轴/按钮、触摸手势
    /// </summary>
    public class KeyBinding
    {
        /// <summary>绑定源类型</summary>
        public BindingSourceType SourceType { get; set; }

        /// <summary>绑定的物理键 (键盘/鼠标/手柄)</summary>
        public InputKey Key { get; set; } = InputKey.None;

        /// <summary>绑定的轴 (手柄摇杆/扳机/鼠标轴)</summary>
        public InputAxis Axis { get; set; } = InputAxis.None;

        /// <summary>轴方向</summary>
        public InputAxisDirection AxisDirection { get; set; } = InputAxisDirection.Positive;

        /// <summary>轴激活阈值 [0, 1]</summary>
        public float AxisThreshold { get; set; } = 0.15f;

        /// <summary>需要按住的修饰键列表</summary>
        public List<InputKey> ModifierKeys { get; set; } = new List<InputKey>();

        /// <summary>绑定优先级 (同级绑定时选择优先级最高的)</summary>
        public int Priority { get; set; }

        /// <summary>绑定权重 (多绑定时用于值合并)</summary>
        public float Weight { get; set; } = 1.0f;

        /// <summary>适用的控制方案 (null = 所有方案)</summary>
        public string ControlScheme { get; set; }

        /// <summary>绑定路径 (序列化用, 如 "Keyboard/W", "Gamepad/LeftStick")</summary>
        public string Path { get; set; }

        /// <summary>是否启用</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>条件评估委托 (返回 true 时此绑定才生效)</summary>
        public Func<bool> Condition { get; set; }

        /// <summary>运行时原始值</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public float RawValue { get; internal set; }

        /// <summary>运行时处理后的值</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public float ProcessedValue { get; internal set; }

        /// <summary>运行时是否有有效输入</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public bool HasValue { get; internal set; }

        // -- 便捷构造函数 --

        /// <summary>创建键盘按键绑定</summary>
        public static KeyBinding Keyboard(InputKey key, params InputKey[] modifiers)
        {
            return new KeyBinding
            {
                SourceType = BindingSourceType.Keyboard,
                Key = key,
                ModifierKeys = new List<InputKey>(modifiers),
                Path = $"Keyboard/{key}"
            };
        }

        /// <summary>创建鼠标按键绑定</summary>
        public static KeyBinding Mouse(InputKey button)
        {
            return new KeyBinding
            {
                SourceType = BindingSourceType.Mouse,
                Key = button,
                Path = $"Mouse/{button}"
            };
        }

        /// <summary>创建手柄按钮绑定</summary>
        public static KeyBinding GamepadButton(InputKey button)
        {
            return new KeyBinding
            {
                SourceType = BindingSourceType.GamepadButton,
                Key = button,
                Path = $"Gamepad/{button}"
            };
        }

        /// <summary>创建手柄轴绑定</summary>
        public static KeyBinding GamepadAxis(InputAxis axis, InputAxisDirection dir = InputAxisDirection.Positive, float threshold = 0.15f)
        {
            return new KeyBinding
            {
                SourceType = BindingSourceType.GamepadAxis,
                Axis = axis,
                AxisDirection = dir,
                AxisThreshold = threshold,
                Path = $"Gamepad/{axis}"
            };
        }

        /// <summary>创建触摸手势绑定</summary>
        public static KeyBinding Gesture(TouchGestureType gestureType)
        {
            return new KeyBinding
            {
                SourceType = BindingSourceType.Gesture,
                Path = $"Gesture/{gestureType}"
            };
        }

        /// <summary>设置绑定优先级 (链式)</summary>
        public KeyBinding WithPriority(int priority)
        {
            Priority = priority;
            return this;
        }

        /// <summary>设置绑定权重 (链式)</summary>
        public KeyBinding WithWeight(float weight)
        {
            Weight = weight;
            return this;
        }

        /// <summary>设置适用控制方案 (链式)</summary>
        public KeyBinding WithScheme(string scheme)
        {
            ControlScheme = scheme;
            return this;
        }

        /// <summary>设置条件 (链式)</summary>
        public KeyBinding WithCondition(Func<bool> condition)
        {
            Condition = condition;
            return this;
        }

        public override string ToString() => $"KeyBinding[{SourceType}] {Path ?? Key.ToString()}";
    }

    // =================================================================
    // #region InputMappingContext — 映射上下文
    // =================================================================

    /// <summary>
    /// 输入映射上下文 — UE6 概念的映射上下文
    /// 类似 UE 的 Input Mapping Context: 一组绑定按优先级叠加到玩家输入上
    /// 支持条件绑定：当条件不满足时，整个上下文或单个绑定不生效
    /// </summary>
    public class InputMappingContext
    {
        /// <summary>上下文名称</summary>
        public string Name { get; set; }

        /// <summary>显示名称</summary>
        public string DisplayName { get; set; }

        /// <summary>优先级 (数值越小越优先，同动作冲突时优先级高的覆盖低的)</summary>
        public int Priority { get; set; } = 0;

        /// <summary>是否启用</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>上下文全局条件 (返回 false 时整个上下文不生效)</summary>
        public Func<bool> GlobalCondition { get; set; }

        /// <summary>此上下文中的所有动作绑定</summary>
        public Dictionary<string, List<KeyBinding>> ActionBindings { get; }
            = new Dictionary<string, List<KeyBinding>>();

        /// <summary>适用的控制方案 (null = 所有方案)</summary>
        public string ControlScheme { get; set; }

        // -- 事件 --
        /// <summary>上下文启用</summary>
        public event Action<InputMappingContext> OnEnabled;
        /// <summary>上下文禁用</summary>
        public event Action<InputMappingContext> OnDisabled;

        public InputMappingContext(string name, int priority = 0, string displayName = null)
        {
            Name = name;
            Priority = priority;
            DisplayName = displayName ?? name;
        }

        /// <summary>添加动作绑定</summary>
        public InputMappingContext AddBinding(string actionName, KeyBinding binding)
        {
            if (!ActionBindings.ContainsKey(actionName))
                ActionBindings[actionName] = new List<KeyBinding>();
            ActionBindings[actionName].Add(binding);
            return this;
        }

        /// <summary>添加键盘绑定</summary>
        public InputMappingContext BindKeyboard(string actionName, InputKey key, params InputKey[] modifiers)
        {
            return AddBinding(actionName, KeyBinding.Keyboard(key, modifiers));
        }

        /// <summary>添加手柄按钮绑定</summary>
        public InputMappingContext BindGamepadButton(string actionName, InputKey button)
        {
            return AddBinding(actionName, KeyBinding.GamepadButton(button));
        }

        /// <summary>添加手柄轴绑定</summary>
        public InputMappingContext BindGamepadAxis(string actionName, InputAxis axis,
            InputAxisDirection dir = InputAxisDirection.Positive, float threshold = 0.15f)
        {
            return AddBinding(actionName, KeyBinding.GamepadAxis(axis, dir, threshold));
        }

        /// <summary>添加鼠标按钮绑定</summary>
        public InputMappingContext BindMouse(string actionName, InputKey button)
        {
            return AddBinding(actionName, KeyBinding.Mouse(button));
        }

        /// <summary>添加触摸手势绑定</summary>
        public InputMappingContext BindGesture(string actionName, TouchGestureType gesture)
        {
            return AddBinding(actionName, KeyBinding.Gesture(gesture));
        }

        /// <summary>获取指定动作的所有绑定</summary>
        public List<KeyBinding> GetBindingsForAction(string actionName)
        {
            return ActionBindings.TryGetValue(actionName, out var bindings) ? bindings : null;
        }

        /// <summary>移除指定动作的所有绑定</summary>
        public bool RemoveAction(string actionName) => ActionBindings.Remove(actionName);

        /// <summary>清除所有绑定</summary>
        public void ClearBindings() => ActionBindings.Clear();

        /// <summary>获取所有动作名称</summary>
        public IEnumerable<string> GetActionNames() => ActionBindings.Keys;

        /// <summary>启用上下文</summary>
        public void Enable()
        {
            if (!Enabled)
            {
                Enabled = true;
                OnEnabled?.Invoke(this);
            }
        }

        /// <summary>禁用上下文</summary>
        public void Disable()
        {
            if (Enabled)
            {
                Enabled = false;
                OnDisabled?.Invoke(this);
            }
        }

        /// <summary>评估上下文全局条件是否满足</summary>
        public bool EvaluateCondition() => GlobalCondition?.Invoke() ?? true;

        public override string ToString() => $"MappingContext[{Name}] Priority={Priority} Enabled={Enabled}";
    }

    // =================================================================
    // #region InputModifier — 修改器 (轻量级，用于 KeyBinding)
    // =================================================================

    /// <summary>
    /// 修改器类型 — 绑定级输入值修改器
    /// </summary>
    public enum InputModifierType
    {
        DeadZone,
        Swizzle,
        Invert,
        Scalar,
        Smoothing,
        Clamp,
        Curve,
        Normalize,
        Custom
    }

    /// <summary>
    /// 输入修改器 (轻量版) — 对 KeyBinding 的原始值进行变换
    /// 与 Enhanced 命名空间中的 InputModifier 互补
    /// 此版本更轻量，直接用于 KeyBinding 管线
    /// </summary>
    public class InputModifier
    {
        /// <summary>修改器类型</summary>
        public InputModifierType Type { get; set; }

        /// <summary>参数字典 (各修改器类型使用不同参数)</summary>
        public Dictionary<string, float> Parameters { get; set; } = new Dictionary<string, float>();

        /// <summary>是否启用</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>Swizzle 顺序字符串 (如 "XY", "YX")</summary>
        public string SwizzleOrder { get; set; } = "XY";

        /// <summary>自定义处理委托</summary>
        public Func<float, float> CustomProcess { get; set; }

        // -- 静态工厂方法 --

        public static InputModifier DeadZone(float min = 0.1f, float max = 1.0f)
            => new InputModifier { Type = InputModifierType.DeadZone, Parameters = { ["min"] = min, ["max"] = max } };

        public static InputModifier Invert(bool invertX = true, bool invertY = false)
            => new InputModifier { Type = InputModifierType.Invert, Parameters = { ["invertX"] = invertX ? 1f : 0f, ["invertY"] = invertY ? 1f : 0f } };

        public static InputModifier Scalar(float factor)
            => new InputModifier { Type = InputModifierType.Scalar, Parameters = { ["factor"] = factor } };

        public static InputModifier Swizzle(string order)
            => new InputModifier { Type = InputModifierType.Swizzle, SwizzleOrder = order };

        public static InputModifier Smoothing(float factor = 0.15f)
            => new InputModifier { Type = InputModifierType.Smoothing, Parameters = { ["factor"] = factor } };

        public static InputModifier Clamp(float min = -1f, float max = 1f)
            => new InputModifier { Type = InputModifierType.Clamp, Parameters = { ["min"] = min, ["max"] = max } };

        public static InputModifier Curve(float exponent)
            => new InputModifier { Type = InputModifierType.Curve, Parameters = { ["exponent"] = exponent } };

        public static InputModifier Normalize()
            => new InputModifier { Type = InputModifierType.Normalize };

        public static InputModifier Custom(Func<float, float> process)
            => new InputModifier { Type = InputModifierType.Custom, CustomProcess = process };

        // -- 运行时状态 (平滑滤波) --
        [System.Text.Json.Serialization.JsonIgnore]
        private float _smoothedValue;

        /// <summary>处理 float 值</summary>
        public float Process(float value)
        {
            if (!Enabled) return value;

            switch (Type)
            {
                case InputModifierType.DeadZone:
                    Parameters.TryGetValue("min", out float dzMin);
                    Parameters.TryGetValue("max", out float dzMax);
                    float absVal = Math.Abs(value);
                    if (absVal <= dzMin) return 0f;
                    if (absVal >= dzMax) return Math.Sign(value) * dzMax;
                    return Math.Sign(value) * ((absVal - dzMin) / (dzMax - dzMin) * dzMax);

                case InputModifierType.Invert:
                    Parameters.TryGetValue("invertX", out float invX);
                    return invX > 0.5f ? -value : value;

                case InputModifierType.Scalar:
                    Parameters.TryGetValue("factor", out float factor);
                    return value * factor;

                case InputModifierType.Smoothing:
                    Parameters.TryGetValue("factor", out float smoothFactor);
                    _smoothedValue = _smoothedValue + (value - _smoothedValue) * smoothFactor;
                    return _smoothedValue;

                case InputModifierType.Clamp:
                    Parameters.TryGetValue("min", out float cMin);
                    Parameters.TryGetValue("max", out float cMax);
                    return Math.Clamp(value, cMin, cMax);

                case InputModifierType.Curve:
                    Parameters.TryGetValue("exponent", out float exp);
                    return MathF.Pow(Math.Abs(value), Math.Max(0.01f, exp)) * MathF.Sign(value);

                case InputModifierType.Normalize:
                {
                    float len = Math.Abs(value);
                    return len > 0.001f ? Math.Sign(value) : 0f;
                }

                case InputModifierType.Custom:
                    return CustomProcess?.Invoke(value) ?? value;

                case InputModifierType.Swizzle:
                default:
                    return value;
            }
        }

        /// <summary>重置运行时状态</summary>
        public void Reset()
        {
            _smoothedValue = 0f;
        }
    }

    // =================================================================
    // #region InputTrigger — 触发器 (轻量级，用于 KeyBinding)
    // =================================================================

    /// <summary>
    /// 触发器类型
    /// </summary>
    public enum InputTriggerType
    {
        Press,
        Release,
        Hold,
        Pulse,
        MultiTap,
        DualAxis,
        Tap,
        SlowTap,
        Threshold,
        Custom
    }

    /// <summary>
    /// 输入触发器 (轻量版) — 决定动作值何时产生有效输出
    /// </summary>
    public class InputTrigger
    {
        /// <summary>触发器类型</summary>
        public InputTriggerType Type { get; set; }

        /// <summary>参数字典</summary>
        public Dictionary<string, float> Parameters { get; set; } = new Dictionary<string, float>();

        /// <summary>自定义评估委托</summary>
        public Func<float, float, bool, bool> CustomEvaluate { get; set; }

        // -- 静态工厂 --

        public static InputTrigger Press()
            => new InputTrigger { Type = InputTriggerType.Press };

        public static InputTrigger Release()
            => new InputTrigger { Type = InputTriggerType.Release };

        public static InputTrigger Hold(float holdTime = 0.2f)
            => new InputTrigger { Type = InputTriggerType.Hold, Parameters = { ["holdTime"] = holdTime } };

        public static InputTrigger Pulse(float interval = 0.1f, float initialDelay = 0.5f)
            => new InputTrigger { Type = InputTriggerType.Pulse, Parameters = { ["interval"] = interval, ["initialDelay"] = initialDelay } };

        public static InputTrigger MultiTap(int tapCount = 2, float tapInterval = 0.4f)
            => new InputTrigger { Type = InputTriggerType.MultiTap, Parameters = { ["tapCount"] = tapCount, ["tapInterval"] = tapInterval } };

        public static InputTrigger DualAxis(float threshold = 0.15f)
            => new InputTrigger { Type = InputTriggerType.DualAxis, Parameters = { ["threshold"] = threshold } };

        public static InputTrigger Tap(float maxHoldTime = 0.3f)
            => new InputTrigger { Type = InputTriggerType.Tap, Parameters = { ["maxHoldTime"] = maxHoldTime } };

        public static InputTrigger SlowTap(float minHoldTime = 0.5f)
            => new InputTrigger { Type = InputTriggerType.SlowTap, Parameters = { ["minHoldTime"] = minHoldTime } };

        public static InputTrigger Threshold(float threshold = 0.5f)
            => new InputTrigger { Type = InputTriggerType.Threshold, Parameters = { ["threshold"] = threshold } };

        public static InputTrigger Custom(Func<float, float, bool, bool> evaluate)
            => new InputTrigger { Type = InputTriggerType.Custom, CustomEvaluate = evaluate };

        // -- 运行时状态 --
        [System.Text.Json.Serialization.JsonIgnore]
        public float HoldTimer { get; set; }
        [System.Text.Json.Serialization.JsonIgnore]
        public float PulseTimer { get; set; }
        [System.Text.Json.Serialization.JsonIgnore]
        public int TapCount { get; set; }
        [System.Text.Json.Serialization.JsonIgnore]
        public float LastTapTime { get; set; }
        [System.Text.Json.Serialization.JsonIgnore]
        public bool WasPressed { get; set; }
        [System.Text.Json.Serialization.JsonIgnore]
        public float LastTriggerTime { get; set; }
        [System.Text.Json.Serialization.JsonIgnore]
        private bool _initialPulseDone;

        /// <summary>
        /// 评估触发条件
        /// </summary>
        /// <param name="value">当前输入值</param>
        /// <param name="deltaTime">帧间隔</param>
        /// <param name="wasJustPressed">本帧是否刚按下</param>
        /// <returns>true = 触发</returns>
        public bool Evaluate(float value, float deltaTime, bool wasJustPressed)
        {
            bool isActive = value > 0.1f;

            switch (Type)
            {
                case InputTriggerType.Press:
                    return wasJustPressed;

                case InputTriggerType.Release:
                    if (WasPressed && !isActive && HoldTimer > 0f)
                    {
                        HoldTimer = 0f;
                        return true;
                    }
                    break;

                case InputTriggerType.Hold:
                    Parameters.TryGetValue("holdTime", out float holdTime);
                    if (isActive)
                    {
                        HoldTimer += deltaTime;
                        if (HoldTimer >= holdTime)
                        {
                            HoldTimer = 0f;
                            return true;
                        }
                    }
                    else
                    {
                        HoldTimer = 0f;
                    }
                    break;

                case InputTriggerType.Pulse:
                    Parameters.TryGetValue("interval", out float interval);
                    Parameters.TryGetValue("initialDelay", out float initialDelay);
                    if (isActive)
                    {
                        if (!_initialPulseDone)
                        {
                            PulseTimer += deltaTime;
                            if (PulseTimer >= initialDelay)
                            {
                                _initialPulseDone = true;
                                PulseTimer = 0f;
                                return true;
                            }
                        }
                        else
                        {
                            PulseTimer += deltaTime;
                            if (PulseTimer >= interval)
                            {
                                PulseTimer -= interval;
                                return true;
                            }
                        }
                    }
                    else
                    {
                        PulseTimer = 0f;
                        _initialPulseDone = false;
                    }
                    break;

                case InputTriggerType.MultiTap:
                    Parameters.TryGetValue("tapCount", out float tapCount);
                    Parameters.TryGetValue("tapInterval", out float tapInterval);
                    if (wasJustPressed)
                    {
                        TapCount++;
                        if (TapCount >= (int)tapCount)
                        {
                            TapCount = 0;
                            return true;
                        }
                        LastTapTime = 0f;
                    }
                    if (TapCount > 0)
                    {
                        LastTapTime += deltaTime;
                        if (LastTapTime > tapInterval)
                            TapCount = 0;
                    }
                    break;

                case InputTriggerType.DualAxis:
                    Parameters.TryGetValue("threshold", out float daThreshold);
                    return Math.Abs(value) > daThreshold;

                case InputTriggerType.Tap:
                    Parameters.TryGetValue("maxHoldTime", out float maxHold);
                    if (WasPressed && !isActive && HoldTimer > 0f && HoldTimer <= maxHold)
                    {
                        HoldTimer = 0f;
                        return true;
                    }
                    break;

                case InputTriggerType.SlowTap:
                    Parameters.TryGetValue("minHoldTime", out float minHold);
                    if (WasPressed && !isActive && HoldTimer >= minHold)
                    {
                        HoldTimer = 0f;
                        return true;
                    }
                    break;

                case InputTriggerType.Threshold:
                    Parameters.TryGetValue("threshold", out float thThreshold);
                    return Math.Abs(value) > thThreshold && wasJustPressed;

                case InputTriggerType.Custom:
                    return CustomEvaluate?.Invoke(value, deltaTime, wasJustPressed) ?? false;
            }

            if (isActive) HoldTimer += deltaTime;
            else HoldTimer = 0f;
            WasPressed = isActive;

            return false;
        }

        /// <summary>重置触发器运行时状态</summary>
        public void Reset()
        {
            HoldTimer = 0f;
            PulseTimer = 0f;
            TapCount = 0;
            LastTapTime = 0f;
            WasPressed = false;
            LastTriggerTime = 0f;
            _initialPulseDone = false;
        }
    }

    // =================================================================
    // #region 触摸手势识别
    // =================================================================

    /// <summary>
    /// 触摸手势类型
    /// </summary>
    public enum TouchGestureType
    {
        /// <summary>无</summary>
        None,
        /// <summary>点击/触摸</summary>
        Tap,
        /// <summary>长按</summary>
        LongPress,
        /// <summary>滑动 (单指)</summary>
        Swipe,
        /// <summary>双指捏合缩放</summary>
        Pinch,
        /// <summary>双指旋转</summary>
        Rotate,
        /// <summary>双击</summary>
        DoubleTap,
        /// <summary>拖拽</summary>
        Drag,
        /// <summary>向上滑动</summary>
        SwipeUp,
        /// <summary>向下滑动</summary>
        SwipeDown,
        /// <summary>向左滑动</summary>
        SwipeLeft,
        /// <summary>向右滑动</summary>
        SwipeRight,
        /// <summary>双指平移</summary>
        TwoFingerPan
    }

    /// <summary>
    /// 触摸手势识别器 — 基于多点触摸数据识别常见手势
    /// 支持滑动、捏合、旋转、长按、双击等
    /// </summary>
    public class TouchGestureRecognizer
    {
        /// <summary>滑动的最小距离 (像素)</summary>
        public float SwipeMinDistance { get; set; } = 30f;

        /// <summary>滑动的最大持续时间 (秒)</summary>
        public float SwipeMaxDuration { get; set; } = 0.5f;

        /// <summary>长按的最短持续时间 (秒)</summary>
        public float LongPressMinDuration { get; set; } = 0.5f;

        /// <summary>长按的最大移动距离 (像素)</summary>
        public float LongPressMaxMovement { get; set; } = 10f;

        /// <summary>双击的最大间隔 (秒)</summary>
        public float DoubleTapMaxInterval { get; set; } = 0.3f;

        /// <summary>捏合的最小缩放变化量</summary>
        public float PinchMinScaleDelta { get; set; } = 0.05f;

        /// <summary>旋转的最小角度变化 (弧度)</summary>
        public float RotateMinAngleDelta { get; set; } = 0.1f;

        // -- 手势事件 --
        /// <summary>滑动事件 (方向, 速度)</summary>
        public event Action<TouchGestureType, Vector2, float> OnSwipe;
        /// <summary>点击事件 (位置)</summary>
        public event Action<Vector2> OnTap;
        /// <summary>双击事件 (位置)</summary>
        public event Action<Vector2> OnDoubleTap;
        /// <summary>长按事件 (位置, 持续时间)</summary>
        public event Action<Vector2, float> OnLongPress;
        /// <summary>长按结束事件</summary>
        public event Action OnLongPressEnded;
        /// <summary>捏合事件 (缩放因子, 中心点)</summary>
        public event Action<float, Vector2> OnPinch;
        /// <summary>旋转事件 (角度变化, 中心点)</summary>
        public event Action<float, Vector2> OnRotate;

        // -- 运行时状态 --
        [System.Text.Json.Serialization.JsonIgnore]
        private Vector2 _touchStartPos;
        [System.Text.Json.Serialization.JsonIgnore]
        private float _touchStartTime;
        [System.Text.Json.Serialization.JsonIgnore]
        private bool _isTrackingLongPress;
        [System.Text.Json.Serialization.JsonIgnore]
        private float _lastTapTime;
        [System.Text.Json.Serialization.JsonIgnore]
        private Vector2 _lastTapPos;
        [System.Text.Json.Serialization.JsonIgnore]
        private float _lastPinchDistance;
        [System.Text.Json.Serialization.JsonIgnore]
        private float _lastRotateAngle;

        /// <summary>处理触摸开始</summary>
        public void HandleTouchBegan(int id, Vector2 position, float currentTime)
        {
            if (id == 0) // 使用主触摸点
            {
                _touchStartPos = position;
                _touchStartTime = currentTime;
                _isTrackingLongPress = true;
            }
        }

        /// <summary>处理触摸移动</summary>
        public void HandleTouchMoved(int id, Vector2 position, float currentTime)
        {
            if (!_isTrackingLongPress) return;

            float distance = Vector2.Distance(position, _touchStartPos);
            if (distance > LongPressMaxMovement)
                _isTrackingLongPress = false;
        }

        /// <summary>处理触摸结束 — 检测点击/滑动/双击</summary>
        public void HandleTouchEnded(int id, Vector2 position, float currentTime)
        {
            if (id != 0) return;

            float duration = currentTime - _touchStartTime;
            Vector2 delta = position - _touchStartPos;
            float distance = delta.Length();

            _isTrackingLongPress = false;

            // 检测滑动
            if (distance > SwipeMinDistance && duration <= SwipeMaxDuration)
            {
                float speed = distance / Math.Max(duration, 0.001f);
                TouchGestureType direction = ClassifySwipeDirection(delta);

                OnSwipe?.Invoke(direction, delta, speed);
                return;
            }

            // 检测点击
            if (distance <= SwipeMinDistance && duration < DoubleTapMaxInterval * 2)
            {
                if (currentTime - _lastTapTime <= DoubleTapMaxInterval &&
                    Vector2.Distance(position, _lastTapPos) < SwipeMinDistance)
                {
                    // 双击
                    OnDoubleTap?.Invoke(position);
                    _lastTapTime = 0f;
                    return;
                }

                OnTap?.Invoke(position);
                _lastTapTime = currentTime;
                _lastTapPos = position;
            }
        }

        /// <summary>处理双指触摸 (捏合/旋转)</summary>
        public void HandleTwoFingerTouch(Vector2 pos0, Vector2 pos1, float currentTime)
        {
            float distance = Vector2.Distance(pos0, pos1);
            float angle = MathF.Atan2(pos1.Y - pos0.Y, pos1.X - pos0.X);

            if (_lastPinchDistance > 0f)
            {
                float scaleFactor = distance / Math.Max(_lastPinchDistance, 0.001f);
                if (Math.Abs(scaleFactor - 1f) > PinchMinScaleDelta)
                {
                    Vector2 center = (pos0 + pos1) * 0.5f;
                    OnPinch?.Invoke(scaleFactor, center);
                }
            }

            if (_lastRotateAngle != 0f)
            {
                float angleDelta = angle - _lastRotateAngle;
                if (Math.Abs(angleDelta) > RotateMinAngleDelta)
                {
                    Vector2 center = (pos0 + pos1) * 0.5f;
                    OnRotate?.Invoke(angleDelta, center);
                }
            }

            _lastPinchDistance = distance;
            _lastRotateAngle = angle;
        }

        /// <summary>双指触摸结束</summary>
        public void HandleTwoFingerEnd()
        {
            _lastPinchDistance = 0f;
            _lastRotateAngle = 0f;
        }

        /// <summary>每帧更新 (检测长按)</summary>
        public void Update(float deltaTime, float currentTime)
        {
            if (_isTrackingLongPress && currentTime - _touchStartTime >= LongPressMinDuration)
            {
                _isTrackingLongPress = false;
                OnLongPress?.Invoke(_touchStartPos, currentTime - _touchStartTime);
            }
        }

        /// <summary>通知长按结束</summary>
        public void EndLongPress()
        {
            OnLongPressEnded?.Invoke();
        }

        private TouchGestureType ClassifySwipeDirection(Vector2 delta)
        {
            float absX = Math.Abs(delta.X);
            float absY = Math.Abs(delta.Y);

            if (absX > absY)
                return delta.X > 0 ? TouchGestureType.SwipeRight : TouchGestureType.SwipeLeft;
            else
                return delta.Y > 0 ? TouchGestureType.SwipeDown : TouchGestureType.SwipeUp;
        }

        /// <summary>重置识别器状态</summary>
        public void Reset()
        {
            _isTrackingLongPress = false;
            _lastTapTime = 0f;
            _lastPinchDistance = 0f;
            _lastRotateAngle = 0f;
        }
    }

    // =================================================================
    // #region PlayerInputComponent — 玩家输入组件
    // =================================================================

    /// <summary>
    /// 玩家输入组件 — 每个玩家/控制器独立拥有的输入状态
    /// 管理映射上下文的叠加、动作值的评估和事件分发
    /// </summary>
    public class PlayerInputComponent
    {
        /// <summary>玩家索引</summary>
        public int PlayerIndex { get; set; }

        /// <summary>玩家名称</summary>
        public string PlayerName { get; set; } = "Player";

        /// <summary>是否启用</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>当前激活的映射上下文列表 (按优先级排序)</summary>
        private readonly List<InputMappingContext> _activeContexts = new List<InputMappingContext>();

        /// <summary>已注册的映射上下文池</summary>
        private readonly Dictionary<string, InputMappingContext> _contextPool = new Dictionary<string, InputMappingContext>();

        /// <summary>当前帧的已评估动作值 (动作名 → 值)</summary>
        private readonly Dictionary<string, float> _actionValues = new Dictionary<string, float>();

        /// <summary>当前帧的 Vector2 动作值 (动作名 → 向量)</summary>
        private readonly Dictionary<string, Vector2> _actionVector2Values = new Dictionary<string, Vector2>();

        /// <summary>上一帧的动作值 (用于检测按下/释放)</summary>
        private readonly Dictionary<string, float> _previousActionValues = new Dictionary<string, float>();

        /// <summary>动作修饰器列表 (动作名 → 修改器管线)</summary>
        private readonly Dictionary<string, List<InputModifier>> _actionModifiers = new Dictionary<string, List<InputModifier>>();

        /// <summary>动作触发器列表 (动作名 → 触发器)</summary>
        private readonly Dictionary<string, InputTrigger> _actionTriggers = new Dictionary<string, InputTrigger>();

        /// <summary>控制方案名称 (null = 自动检测)</summary>
        public string ActiveControlScheme { get; set; }

        /// <summary>输入预处理队列</summary>
        private readonly Queue<Action> _inputPreprocessQueue = new Queue<Action>();

        /// <summary>虚拟输入注入队列 (延迟执行)</summary>
        private readonly List<(float executeTime, Action action)> _delayedInputs = new List<(float, Action)>();

        /// <summary>当前游戏时间 (外部设置)</summary>
        public float CurrentTime { get; set; }

        // -- 动作事件 --
        /// <summary>动作触发 (满足触发器条件)</summary>
        public event Action<string, float> OnActionTriggered;
        /// <summary>动作开始 (按下瞬间)</summary>
        public event Action<string> OnActionStarted;
        /// <summary>动作完成 (释放瞬间)</summary>
        public event Action<string> OnActionCompleted;
        /// <summary>动作取消 (条件不满足)</summary>
        public event Action<string> OnActionCanceled;
        /// <summary>动作值变化</summary>
        public event Action<string, float> OnActionValueChanged;
        /// <summary>Vector2 动作值变化</summary>
        public event Action<string, Vector2> OnActionVector2Changed;

        public PlayerInputComponent(int playerIndex = 0, string playerName = null)
        {
            PlayerIndex = playerIndex;
            PlayerName = playerName ?? $"Player{playerIndex}";
        }

        // ========== 映射上下文管理 ==========

        /// <summary>注册映射上下文到池中 (不激活)</summary>
        public void RegisterContext(InputMappingContext context)
        {
            _contextPool[context.Name] = context;
        }

        /// <summary>取消注册映射上下文</summary>
        public bool UnregisterContext(string contextName) => _contextPool.Remove(contextName);

        /// <summary>激活映射上下文</summary>
        public void PushContext(InputMappingContext context)
        {
            if (!_activeContexts.Contains(context))
            {
                _activeContexts.Add(context);
                _activeContexts.Sort((a, b) => a.Priority.CompareTo(b.Priority));
            }
        }

        /// <summary>通过名称激活映射上下文</summary>
        public void PushContext(string contextName)
        {
            if (_contextPool.TryGetValue(contextName, out var ctx))
                PushContext(ctx);
        }

        /// <summary>停用映射上下文</summary>
        public void PopContext(InputMappingContext context) => _activeContexts.Remove(context);

        /// <summary>通过名称停用映射上下文</summary>
        public void PopContext(string contextName)
        {
            if (_contextPool.TryGetValue(contextName, out var ctx))
                PopContext(ctx);
        }

        /// <summary>获取所有激活的上下文</summary>
        public IReadOnlyList<InputMappingContext> GetActiveContexts() => _activeContexts.AsReadOnly();

        /// <summary>获取上下文池中所有上下文</summary>
        public IEnumerable<InputMappingContext> GetAllRegisteredContexts() => _contextPool.Values;

        // ========== 修饰器/触发器设置 ==========

        /// <summary>为动作添加修改器</summary>
        public void AddModifier(string actionName, InputModifier modifier)
        {
            if (!_actionModifiers.ContainsKey(actionName))
                _actionModifiers[actionName] = new List<InputModifier>();
            _actionModifiers[actionName].Add(modifier);
        }

        /// <summary>设置动作触发器</summary>
        public void SetTrigger(string actionName, InputTrigger trigger)
        {
            _actionTriggers[actionName] = trigger;
        }

        /// <summary>移除动作的所有修改器</summary>
        public void ClearModifiers(string actionName) => _actionModifiers.Remove(actionName);

        // ========== 输入预处理/延迟注入 ==========

        /// <summary>向预处理队列添加操作 (在下一帧 Update 之前执行)</summary>
        public void EnqueuePreprocess(Action action)
        {
            _inputPreprocessQueue.Enqueue(action);
        }

        /// <summary>注入延迟虚拟输入 (在指定时间后执行)</summary>
        public void InjectDelayedInput(float delaySeconds, Action action)
        {
            _delayedInputs.Add((CurrentTime + delaySeconds, action));
        }

        /// <summary>立即注入虚拟输入</summary>
        public void InjectInput(Action action) => _inputPreprocessQueue.Enqueue(action);

        // ========== 每帧更新 ==========

        /// <summary>
        /// 每帧更新 — 执行所有激活上下文中的绑定评估
        /// </summary>
        /// <param name="rawInputFunc">原始输入查询函数: (KeyBinding) => float</param>
        /// <param name="deltaTime">帧间隔</param>
        public void Update(Func<KeyBinding, float> rawInputFunc, float deltaTime)
        {
            if (!Enabled) return;

            CurrentTime += deltaTime;

            // 1. 执行输入预处理队列
            ProcessPreprocessQueue();

            // 2. 执行延迟注入
            ProcessDelayedInputs();

            // 3. 保存上一帧值
            _previousActionValues.Clear();
            foreach (var kvp in _actionValues)
                _previousActionValues[kvp.Key] = kvp.Value;

            // 4. 清空当前帧值
            _actionValues.Clear();
            _actionVector2Values.Clear();

            // 5. 按优先级遍历激活上下文
            foreach (var context in _activeContexts)
            {
                if (!context.Enabled) continue;
                if (context.ControlScheme != null && ActiveControlScheme != null &&
                    context.ControlScheme != ActiveControlScheme) continue;
                if (!context.EvaluateCondition()) continue;

                // 遍历上下文中每个动作的绑定
                foreach (var kvp in context.ActionBindings)
                {
                    string actionName = kvp.Key;
                    var bindings = kvp.Value;

                    float maxValue = 0f;
                    bool anyBound = false;

                    // 如果此动作已被更高优先级的上下文设置了值，跳过
                    if (_actionValues.ContainsKey(actionName) &&
                        Math.Abs(_actionValues[actionName]) > 0.001f)
                        continue;

                    foreach (var binding in bindings)
                    {
                        if (!binding.Enabled) continue;

                        // 检查控制方案
                        if (binding.ControlScheme != null && ActiveControlScheme != null &&
                            binding.ControlScheme != ActiveControlScheme)
                            continue;

                        // 检查绑定条件
                        if (binding.Condition != null && !binding.Condition())
                            continue;

                        // 获取原始值
                        float rawVal = rawInputFunc(binding);
                        binding.RawValue = rawVal;

                        // 应用修饰器
                        float processedVal = rawVal;
                        if (_actionModifiers.TryGetValue(actionName, out var mods))
                        {
                            foreach (var mod in mods)
                                processedVal = mod.Process(processedVal);
                        }

                        binding.ProcessedValue = processedVal;
                        binding.HasValue = Math.Abs(processedVal) > 0.001f;

                        if (binding.HasValue)
                        {
                            float weighted = processedVal * binding.Weight;
                            if (Math.Abs(weighted) > Math.Abs(maxValue))
                                maxValue = weighted;
                            anyBound = true;
                        }
                    }

                    if (anyBound)
                        _actionValues[actionName] = maxValue;
                }
            }

            // 6. 评估触发器并发送事件
            foreach (var kvp in _actionValues)
            {
                string actionName = kvp.Key;
                float value = kvp.Value;

                _previousActionValues.TryGetValue(actionName, out float prevValue);

                // 事件: Started (刚按下)
                if (value > 0.1f && prevValue <= 0.1f)
                    OnActionStarted?.Invoke(actionName);

                // 事件: Completed (刚释放)
                if (value <= 0.1f && prevValue > 0.1f)
                    OnActionCompleted?.Invoke(actionName);

                // 事件: Triggered (满足触发器条件)
                if (_actionTriggers.TryGetValue(actionName, out var trigger))
                {
                    bool wasJustPressed = value > 0.1f && prevValue <= 0.1f;
                    bool triggered = trigger.Evaluate(value, deltaTime, wasJustPressed);
                    if (triggered)
                        OnActionTriggered?.Invoke(actionName, value);
                }

                // 事件: ValueChanged
                if (Math.Abs(value - prevValue) > 0.0001f)
                    OnActionValueChanged?.Invoke(actionName, value);
            }
        }

        private void ProcessPreprocessQueue()
        {
            while (_inputPreprocessQueue.Count > 0)
            {
                var action = _inputPreprocessQueue.Dequeue();
                try { action(); }
                catch (Exception) { /* 忽略预处理错误 */ }
            }
        }

        private void ProcessDelayedInputs()
        {
            var remaining = new List<(float, Action)>();
            foreach (var (execTime, action) in _delayedInputs)
            {
                if (CurrentTime >= execTime)
                {
                    try { action(); }
                    catch (Exception) { /* 忽略延迟输入错误 */ }
                }
                else
                {
                    remaining.Add((execTime, action));
                }
            }
            _delayedInputs.Clear();
            _delayedInputs.AddRange(remaining);
        }

        // ========== 动作值查询 ==========

        /// <summary>获取动作当前 float 值</summary>
        public float GetActionValue(string actionName)
        {
            return _actionValues.TryGetValue(actionName, out float v) ? v : 0f;
        }

        /// <summary>获取动作当前 Vector2 值</summary>
        public Vector2 GetActionVector2(string actionName)
        {
            return _actionVector2Values.TryGetValue(actionName, out Vector2 v) ? v : Vector2.Zero;
        }

        /// <summary>动作是否正在激活 (值 > 0.1)</summary>
        public bool IsActionActive(string actionName)
        {
            return _actionValues.TryGetValue(actionName, out float v) && v > 0.1f;
        }

        /// <summary>动作是否在本帧刚按下</summary>
        public bool WasActionJustPressed(string actionName)
        {
            return IsActionActive(actionName) &&
                   (!_previousActionValues.TryGetValue(actionName, out float prev) || prev <= 0.1f);
        }

        /// <summary>动作是否在本帧刚释放</summary>
        public bool WasActionJustReleased(string actionName)
        {
            return !IsActionActive(actionName) &&
                   _previousActionValues.TryGetValue(actionName, out float prev) && prev > 0.1f;
        }

        /// <summary>获取所有已评估的动作名称</summary>
        public IEnumerable<string> GetEvaluatedActionNames() => _actionValues.Keys;

        // ========== 重置 ==========

        /// <summary>重置所有动作值和触发器状态</summary>
        public void Reset()
        {
            _actionValues.Clear();
            _actionVector2Values.Clear();
            _previousActionValues.Clear();

            foreach (var trigger in _actionTriggers.Values)
                trigger.Reset();

            foreach (var mods in _actionModifiers.Values)
                foreach (var mod in mods)
                    mod.Reset();

            _inputPreprocessQueue.Clear();
            _delayedInputs.Clear();
        }
    }

    // =================================================================
    // #region InputDeviceHotplugDetector — 设备热插拔检测
    // =================================================================

    /// <summary>
    /// 设备热插拔检测器 — 自动检测输入设备的连接/断开
    /// </summary>
    public class InputDeviceHotplugDetector
    {
        /// <summary>设备连接事件 (设备类型, 设备ID, 设备名称)</summary>
        public event Action<InputDeviceType, string, string> OnDeviceConnected;
        /// <summary>设备断开事件</summary>
        public event Action<InputDeviceType, string, string> OnDeviceDisconnected;

        /// <summary>已知设备列表 (设备ID → 类型)</summary>
        private readonly Dictionary<string, (InputDeviceType type, string name, float lastSeen)> _knownDevices
            = new Dictionary<string, (InputDeviceType, string, float)>();

        /// <summary>轮询间隔 (秒)</summary>
        public float PollInterval { get; set; } = 1.0f;

        /// <summary>设备超时时间 (秒) — 超过此时间未检测到视为断开</summary>
        public float DeviceTimeout { get; set; } = 3.0f;

        /// <summary>轮询计时器</summary>
        private float _pollTimer;
        private float _currentTime;

        /// <summary>注册外部设备查询回调</summary>
        /// <summary>回调应返回当前连接的设备列表</summary>
        public Func<List<(InputDeviceType type, string deviceId, string name)>> QueryConnectedDevices { get; set; }

        /// <summary>每帧更新</summary>
        public void Update(float deltaTime)
        {
            _currentTime += deltaTime;
            _pollTimer += deltaTime;

            if (_pollTimer >= PollInterval)
            {
                _pollTimer -= PollInterval;
                Poll();
            }

            // 检查超时
            var timedOut = new List<string>();
            foreach (var kvp in _knownDevices)
            {
                if (_currentTime - kvp.Value.lastSeen > DeviceTimeout)
                    timedOut.Add(kvp.Key);
            }

            foreach (var id in timedOut)
            {
                var info = _knownDevices[id];
                _knownDevices.Remove(id);
                OnDeviceDisconnected?.Invoke(info.type, id, info.name);
            }
        }

        private void Poll()
        {
            if (QueryConnectedDevices == null) return;

            var connected = QueryConnectedDevices();
            var seenIds = new HashSet<string>();

            foreach (var (type, deviceId, name) in connected)
            {
                seenIds.Add(deviceId);

                if (!_knownDevices.ContainsKey(deviceId))
                {
                    // 新设备
                    _knownDevices[deviceId] = (type, name, _currentTime);
                    OnDeviceConnected?.Invoke(type, deviceId, name);
                }
                else
                {
                    // 更新最后检测时间
                    var existing = _knownDevices[deviceId];
                    _knownDevices[deviceId] = (type, name, _currentTime);
                }
            }
        }

        /// <summary>获取所有已知设备</summary>
        public IReadOnlyDictionary<string, (InputDeviceType type, string name, float lastSeen)> GetKnownDevices()
            => _knownDevices;

        /// <summary>检查指定设备类型是否有设备连接</summary>
        public bool IsDeviceTypeConnected(InputDeviceType type)
        {
            foreach (var info in _knownDevices.Values)
            {
                if (info.type == type && _currentTime - info.lastSeen <= DeviceTimeout)
                    return true;
            }
            return false;
        }
    }

    // =================================================================
    // #region GamepadRumbleHelper — 手柄震动反馈辅助
    // =================================================================

    /// <summary>
    /// 手柄震动预设
    /// </summary>
    public static class GamepadRumblePresets
    {
        /// <summary>轻触反馈</summary>
        public static readonly (float low, float high, float duration) Light = (0.1f, 0.1f, 0.1f);

        /// <summary>中等反馈</summary>
        public static readonly (float low, float high, float duration) Medium = (0.3f, 0.3f, 0.2f);

        /// <summary>强力反馈</summary>
        public static readonly (float low, float high, float duration) Heavy = (0.6f, 0.8f, 0.3f);

        /// <summary>碰撞反馈</summary>
        public static readonly (float low, float high, float duration) Collision = (0.8f, 1.0f, 0.15f);

        /// <summary>射击反馈</summary>
        public static readonly (float low, float high, float duration) Gunshot = (0.2f, 0.5f, 0.1f);

        /// <summary>爆炸反馈</summary>
        public static readonly (float low, float high, float duration) Explosion = (1.0f, 1.0f, 0.5f);

        /// <summary> footsteps 反馈</summary>
        public static readonly (float low, float high, float duration) Footstep = (0.05f, 0.02f, 0.05f);
    }

    /// <summary>
    /// 手柄震动控制器 — 管理手柄马达的震动强度和持续时间
    /// </summary>
    public class GamepadRumbleController
    {
        /// <summary>当前左马达强度 [0, 1]</summary>
        public float LeftMotor { get; private set; }

        /// <summary>当前右马达强度 [0, 1]</summary>
        public float RightMotor { get; private set; }

        /// <summary>剩余震动时间 (秒)</summary>
        public float RemainingTime { get; private set; }

        /// <summary>低频马达目标值</summary>
        private float _targetLeft;
        /// <summary>高频马达目标值</summary>
        private float _targetRight;
        /// <summary>震动总持续时间</summary>
        private float _totalDuration;

        /// <summary>震动曲线类型</summary>
        public enum RumbleCurve
        {
            Linear,
            EaseOut,
            EaseIn,
            Sine
        }

        /// <summary>震动衰减曲线</summary>
        public RumbleCurve FadeCurve { get; set; } = RumbleCurve.Linear;

        /// <summary>是否正在震动</summary>
        public bool IsRumbling => RemainingTime > 0f;

        /// <summary>触发震动</summary>
        public void SetRumble(float left, float right, float duration)
        {
            _targetLeft = Math.Clamp(left, 0f, 1f);
            _targetRight = Math.Clamp(right, 0f, 1f);
            _totalDuration = Math.Max(0f, duration);
            RemainingTime = _totalDuration;
        }

        /// <summary>使用预设触发震动</summary>
        public void SetRumble((float low, float high, float duration) preset)
        {
            SetRumble(preset.low, preset.high, preset.duration);
        }

        /// <summary>停止震动</summary>
        public void StopRumble()
        {
            LeftMotor = 0f;
            RightMotor = 0f;
            RemainingTime = 0f;
            _targetLeft = 0f;
            _targetRight = 0f;
        }

        /// <summary>每帧更新震动强度</summary>
        public void Update(float deltaTime)
        {
            if (RemainingTime <= 0f)
            {
                LeftMotor = 0f;
                RightMotor = 0f;
                return;
            }

            RemainingTime -= deltaTime;

            float progress = 1f - (RemainingTime / Math.Max(_totalDuration, 0.001f));
            float fade = CalculateFade(progress);

            LeftMotor = _targetLeft * fade;
            RightMotor = _targetRight * fade;

            if (RemainingTime <= 0f)
            {
                LeftMotor = 0f;
                RightMotor = 0f;
                RemainingTime = 0f;
            }
        }

        private float CalculateFade(float progress)
        {
            return FadeCurve switch
            {
                RumbleCurve.Linear => 1f - progress,
                RumbleCurve.EaseOut => 1f - progress * progress,
                RumbleCurve.EaseIn => (1f - progress) * (1f - progress),
                RumbleCurve.Sine => MathF.Cos(progress * MathF.PI * 0.5f),
                _ => 1f - progress
            };
        }
    }

    // =================================================================
    // #region GamepadAxisProcessor — 摇杆/扳机处理
    // =================================================================

    /// <summary>
    /// 手柄轴处理器 — 对摇杆和扳机值进行死区、灵敏度、曲线处理
    /// </summary>
    public class GamepadAxisProcessor
    {
        /// <summary>摇杆死区半径 (低于此值归零)</summary>
        public float StickDeadzone { get; set; } = 0.15f;

        /// <summary>摇杆灵敏度倍率</summary>
        public float StickSensitivity { get; set; } = 1.0f;

        /// <summary>扳机激活阈值 [0, 1]</summary>
        public float TriggerThreshold { get; set; } = 0.1f;

        /// <summary>扳机灵敏度倍率</summary>
        public float TriggerSensitivity { get; set; } = 1.0f;

        /// <summary>摇杆响应曲线指数 (1=线性, >1=加速)</summary>
        public float StickCurveExponent { get; set; } = 1.0f;

        /// <summary>是否对扳机应用曲线</summary>
        public bool ApplyTriggerCurve { get; set; } = false;

        /// <summary>处理摇杆值 (带死区和灵敏度)</summary>
        public Vector2 ProcessStick(Vector2 rawValue)
        {
            float magnitude = rawValue.Length();

            // 径向死区
            if (magnitude <= StickDeadzone) return Vector2.Zero;

            // 重新映射死区边缘到零
            float normalizedMag = (magnitude - StickDeadzone) / (1f - StickDeadzone);
            normalizedMag = Math.Clamp(normalizedMag, 0f, 1f);

            // 应用曲线
            if (StickCurveExponent != 1f)
                normalizedMag = MathF.Pow(normalizedMag, StickCurveExponent);

            // 归一化方向并缩放
            Vector2 direction = magnitude > 0.001f ? rawValue / magnitude : Vector2.Zero;
            return direction * normalizedMag * StickSensitivity;
        }

        /// <summary>处理扳机值</summary>
        public float ProcessTrigger(float rawValue)
        {
            float value = rawValue * TriggerSensitivity;

            if (value <= TriggerThreshold) return 0f;

            // 重新映射 [threshold, 1] → [0, 1]
            float normalized = (value - TriggerThreshold) / (1f - TriggerThreshold);
            return Math.Clamp(normalized, 0f, 1f);
        }

        /// <summary>处理 DPad 值</summary>
        public Vector2 ProcessDPad(Vector2 rawValue)
        {
            // DPad 已经是数字化的 (-1/0/1)，只需归一化对角线
            float mag = rawValue.Length();
            return mag > 1f ? Vector2.Normalize(rawValue) : rawValue;
        }
    }

    // =================================================================
    // #region EnhancedInputSystem — UE6 级增强输入系统 (核心)
    // =================================================================

    /// <summary>
    /// UE6 级增强输入系统 — 山雨引擎的核心输入管理器
    /// 集成并协调以下子系统:
    ///   - 基础 InputSystem (键/轴状态)
    ///   - InputDeviceManager (设备管理/热插拔)
    ///   - PlayerInputComponent (每玩家输入)
    ///   - TouchGestureRecognizer (手势识别)
    ///   - GamepadRumbleController (手柄震动)
    ///   - InputMappingContext (映射上下文叠加)
    /// </summary>
    public class EnhancedInputSystem
    {
        private static EnhancedInputSystem _instance;

        /// <summary>全局单例</summary>
        public static EnhancedInputSystem Instance
        {
            get
            {
                if (_instance == null) _instance = new EnhancedInputSystem();
                return _instance;
            }
        }

        // -- 子系统引用 --
        /// <summary>基础输入系统引用</summary>
        public InputSystem BaseInput { get; private set; }

        /// <summary>设备热插拔检测器</summary>
        public InputDeviceHotplugDetector HotplugDetector { get; private set; }

        /// <summary>触摸手势识别器</summary>
        public TouchGestureRecognizer GestureRecognizer { get; private set; }

        /// <summary>手柄轴处理器</summary>
        public GamepadAxisProcessor AxisProcessor { get; private set; }

        /// <summary>手柄震动控制器 (每个手柄一个)</summary>
        private readonly Dictionary<string, GamepadRumbleController> _rumbleControllers
            = new Dictionary<string, GamepadRumbleController>();

        // -- 玩家管理 --
        /// <summary>已注册的玩家输入组件</summary>
        private readonly Dictionary<int, PlayerInputComponent> _playerInputs
            = new Dictionary<int, PlayerInputComponent>();

        /// <summary>最大支持玩家数</summary>
        public int MaxPlayers { get; set; } = 4;

        // -- 全局映射上下文 --
        /// <summary>全局共享映射上下文池</summary>
        private readonly Dictionary<string, InputMappingContext> _globalContexts
            = new Dictionary<string, InputMappingContext>();

        // -- 输入预处理队列 (系统级) --
        private readonly Queue<Action> _systemPreprocessQueue = new Queue<Action>();

        // -- 事件 --
        /// <summary>玩家注册</summary>
        public event Action<int> OnPlayerRegistered;
        /// <summary>玩家注销</summary>
        public event Action<int> OnPlayerUnregistered;
        /// <summary>设备连接</summary>
        public event Action<InputDeviceType, string, string> OnDeviceConnected;
        /// <summary>设备断开</summary>
        public event Action<InputDeviceType, string, string> OnDeviceDisconnected;

        /// <summary>是否已初始化</summary>
        public bool IsInitialized { get; private set; }

        /// <summary>系统时间 (秒)</summary>
        public float SystemTime { get; private set; }

        public EnhancedInputSystem()
        {
            BaseInput = new InputSystem();
            HotplugDetector = new InputDeviceHotplugDetector();
            GestureRecognizer = new TouchGestureRecognizer();
            AxisProcessor = new GamepadAxisProcessor();

            HotplugDetector.OnDeviceConnected += (type, id, name) => OnDeviceConnected?.Invoke(type, id, name);
            HotplugDetector.OnDeviceDisconnected += (type, id, name) => OnDeviceDisconnected?.Invoke(type, id, name);

            IsInitialized = true;
        }

        // ========== 玩家管理 ==========

        /// <summary>注册玩家输入组件</summary>
        public PlayerInputComponent RegisterPlayer(int playerIndex, string playerName = null)
        {
            if (playerIndex < 0 || playerIndex >= MaxPlayers)
                throw new ArgumentOutOfRangeException(nameof(playerIndex));

            if (_playerInputs.ContainsKey(playerIndex))
                return _playerInputs[playerIndex];

            var component = new PlayerInputComponent(playerIndex, playerName);
            _playerInputs[playerIndex] = component;
            OnPlayerRegistered?.Invoke(playerIndex);
            return component;
        }

        /// <summary>注销玩家</summary>
        public bool UnregisterPlayer(int playerIndex)
        {
            if (_playerInputs.Remove(playerIndex))
            {
                OnPlayerUnregistered?.Invoke(playerIndex);
                return true;
            }
            return false;
        }

        /// <summary>获取玩家输入组件</summary>
        public PlayerInputComponent GetPlayerInput(int playerIndex)
        {
            return _playerInputs.TryGetValue(playerIndex, out var comp) ? comp : null;
        }

        /// <summary>获取或创建玩家输入组件</summary>
        public PlayerInputComponent GetOrCreatePlayerInput(int playerIndex, string playerName = null)
        {
            if (!_playerInputs.TryGetValue(playerIndex, out var comp))
                comp = RegisterPlayer(playerIndex, playerName);
            return comp;
        }

        /// <summary>获取所有玩家输入组件</summary>
        public IReadOnlyDictionary<int, PlayerInputComponent> GetAllPlayerInputs()
            => _playerInputs;

        // ========== 全局映射上下文 ==========

        /// <summary>注册全局映射上下文</summary>
        public void RegisterGlobalContext(InputMappingContext context)
        {
            _globalContexts[context.Name] = context;
        }

        /// <summary>取消注册全局映射上下文</summary>
        public bool UnregisterGlobalContext(string name) => _globalContexts.Remove(name);

        /// <summary>获取全局映射上下文</summary>
        public InputMappingContext GetGlobalContext(string name)
        {
            return _globalContexts.TryGetValue(name, out var ctx) ? ctx : null;
        }

        /// <summary>将全局上下文推入所有玩家</summary>
        public void PushGlobalContextToAll(string contextName)
        {
            if (!_globalContexts.TryGetValue(contextName, out var ctx)) return;
            foreach (var player in _playerInputs.Values)
                player.PushContext(ctx);
        }

        /// <summary>从所有玩家弹出全局上下文</summary>
        public void PopGlobalContextFromAll(string contextName)
        {
            foreach (var player in _playerInputs.Values)
                player.PopContext(contextName);
        }

        // ========== 手柄震动 ==========

        /// <summary>获取指定手柄的震动控制器</summary>
        public GamepadRumbleController GetRumbleController(string deviceId)
        {
            if (!_rumbleControllers.ContainsKey(deviceId))
                _rumbleControllers[deviceId] = new GamepadRumbleController();
            return _rumbleControllers[deviceId];
        }

        /// <summary>对所有手柄触发震动</summary>
        public void RumbleAll((float low, float high, float duration) preset)
        {
            foreach (var controller in _rumbleControllers.Values)
                controller.SetRumble(preset);
        }

        /// <summary>停止所有手柄震动</summary>
        public void StopAllRumble()
        {
            foreach (var controller in _rumbleControllers.Values)
                controller.StopRumble();
        }

        // ========== 系统级输入预处理 ==========

        /// <summary>注入系统级虚拟输入 (在所有玩家更新前执行)</summary>
        public void EnqueueSystemInput(Action action)
        {
            _systemPreprocessQueue.Enqueue(action);
        }

        // ========== 核心更新 ==========

        /// <summary>
        /// 每帧更新整个输入系统
        /// </summary>
        /// <param name="deltaTime">帧间隔</param>
        public void Update(float deltaTime)
        {
            SystemTime += deltaTime;

            // 1. 更新基础输入系统
            BaseInput.Update(deltaTime);

            // 2. 更新热插拔检测
            HotplugDetector.Update(deltaTime);

            // 3. 更新手势识别
            GestureRecognizer.Update(deltaTime, SystemTime);

            // 4. 更新手柄震动
            foreach (var controller in _rumbleControllers.Values)
                controller.Update(deltaTime);

            // 5. 执行系统级预处理
            while (_systemPreprocessQueue.Count > 0)
            {
                var action = _systemPreprocessQueue.Dequeue();
                try { action(); }
                catch (Exception) { /* 忽略 */ }
            }

            // 6. 更新所有玩家输入组件
            foreach (var player in _playerInputs.Values)
            {
                player.Update(EvaluateBinding, deltaTime);
            }
        }

        // ========== 绑定值评估 (原始输入 → float) ==========

        /// <summary>
        /// 评估单个 KeyBinding 的原始输入值
        /// 此方法是 PlayerInputComponent.Update 的回调
        /// </summary>
        private float EvaluateBinding(KeyBinding binding)
        {
            switch (binding.SourceType)
            {
                case BindingSourceType.Keyboard:
                    // 检查修饰键
                    foreach (var mod in binding.ModifierKeys)
                    {
                        if (!BaseInput.IsKeyDown(mod)) return 0f;
                    }
                    return BaseInput.IsKeyDown(binding.Key) ? 1f : 0f;

                case BindingSourceType.Mouse:
                    return BaseInput.IsKeyDown(binding.Key) ? 1f : 0f;

                case BindingSourceType.GamepadButton:
                    return BaseInput.IsKeyDown(binding.Key) ? 1f : 0f;

                case BindingSourceType.GamepadAxis:
                    float axisVal = BaseInput.GetAxis(binding.Axis);
                    float signed = binding.AxisDirection == InputAxisDirection.Positive
                        ? axisVal : -axisVal;
                    return signed > binding.AxisThreshold ? signed : 0f;

                case BindingSourceType.Touch:
                case BindingSourceType.Gesture:
                    // 触摸/手势通过手势识别器的事件系统处理
                    return 0f;

                default:
                    return 0f;
            }
        }

        // ========== 便捷查询方法 ==========

        /// <summary>获取指定玩家的动作值</summary>
        public float GetActionValue(int playerIndex, string actionName)
        {
            if (_playerInputs.TryGetValue(playerIndex, out var player))
                return player.GetActionValue(actionName);
            return 0f;
        }

        /// <summary>获取指定玩家动作是否刚按下</summary>
        public bool WasActionJustPressed(int playerIndex, string actionName)
        {
            if (_playerInputs.TryGetValue(playerIndex, out var player))
                return player.WasActionJustPressed(actionName);
            return false;
        }

        /// <summary>获取指定玩家动作是否刚释放</summary>
        public bool WasActionJustReleased(int playerIndex, string actionName)
        {
            if (_playerInputs.TryGetValue(playerIndex, out var player))
                return player.WasActionJustReleased(actionName);
            return false;
        }

        /// <summary>获取移动向量 (组合 Forward/Back/Left/Right)</summary>
        public Vector2 GetMovementVector(int playerIndex = 0)
        {
            if (_playerInputs.TryGetValue(playerIndex, out var player))
            {
                return new Vector2(
                    player.GetActionValue(InputActionNames.Move) != 0 ? 1 : 0,
                    player.GetActionValue(InputActionNames.Move) != 0 ? 1 : 0
                );
            }
            return BaseInput.GetMovementVector();
        }

        // ========== 生命周期 ==========

        /// <summary>重置整个系统</summary>
        public void Reset()
        {
            foreach (var player in _playerInputs.Values)
                player.Reset();
            BaseInput.ClearAllInput();
            GestureRecognizer.Reset();
            _systemPreprocessQueue.Clear();
            SystemTime = 0f;
        }

        /// <summary>销毁系统</summary>
        public void Shutdown()
        {
            _playerInputs.Clear();
            _globalContexts.Clear();
            _rumbleControllers.Clear();
            _systemPreprocessQueue.Clear();
            IsInitialized = false;
            _instance = null;
        }

        // ========== 预定义映射上下文工厂 ==========

        /// <summary>创建默认游戏玩法映射上下文</summary>
        public static InputMappingContext CreateGameplayContext()
        {
            var ctx = new InputMappingContext("Gameplay", priority: 0, displayName: "Gameplay Controls");

            ctx.BindKeyboard(InputActionNames.Move, InputKey.W);
            ctx.BindKeyboard(InputActionNames.Move, InputKey.A);
            ctx.BindKeyboard(InputActionNames.Move, InputKey.S);
            ctx.BindKeyboard(InputActionNames.Move, InputKey.D);
            ctx.BindGamepadAxis(InputActionNames.Move, InputAxis.GamepadLeftStickX);
            ctx.BindGamepadAxis(InputActionNames.Move, InputAxis.GamepadLeftStickY);

            ctx.BindKeyboard(InputActionNames.Jump, InputKey.Space);
            ctx.BindGamepadButton(InputActionNames.Jump, InputKey.GamepadA);

            ctx.BindKeyboard(InputActionNames.Fire, InputKey.MouseLeft);
            ctx.BindGamepadButton(InputActionNames.Fire, InputKey.GamepadRightShoulder);

            ctx.BindKeyboard(InputActionNames.AltFire, InputKey.MouseRight);
            ctx.BindGamepadButton(InputActionNames.AltFire, InputKey.GamepadY);

            ctx.BindKeyboard(InputActionNames.Interact, InputKey.E);
            ctx.BindGamepadButton(InputActionNames.Interact, InputKey.GamepadX);

            ctx.BindKeyboard(InputActionNames.Reload, InputKey.R);

            ctx.BindKeyboard(InputActionNames.Sprint, InputKey.Shift);
            ctx.BindGamepadButton(InputActionNames.Sprint, InputKey.GamepadLeftShoulder);

            ctx.BindKeyboard(InputActionNames.Crouch, InputKey.Control);
            ctx.BindGamepadButton(InputActionNames.Crouch, InputKey.GamepadB);

            ctx.BindKeyboard(InputActionNames.Pause, InputKey.Escape);
            ctx.BindGamepadButton(InputActionNames.Pause, InputKey.GamepadMenu);

            return ctx;
        }

        /// <summary>创建 UI 映射上下文</summary>
        public static InputMappingContext CreateUIContext()
        {
            var ctx = new InputMappingContext("UI", priority: 10, displayName: "UI Controls");

            ctx.BindKeyboard(InputActionNames.Confirm, InputKey.Enter);
            ctx.BindKeyboard(InputActionNames.Confirm, InputKey.Space);
            ctx.BindGamepadButton(InputActionNames.Confirm, InputKey.GamepadA);

            ctx.BindKeyboard(InputActionNames.Cancel, InputKey.Escape);
            ctx.BindGamepadButton(InputActionNames.Cancel, InputKey.GamepadB);

            ctx.BindKeyboard(InputActionNames.Navigate, InputKey.UpArrow);
            ctx.BindKeyboard(InputActionNames.Navigate, InputKey.DownArrow);
            ctx.BindKeyboard(InputActionNames.Navigate, InputKey.LeftArrow);
            ctx.BindKeyboard(InputActionNames.Navigate, InputKey.RightArrow);
            ctx.BindGamepadAxis(InputActionNames.Navigate, InputAxis.GamepadLeftStickX);
            ctx.BindGamepadAxis(InputActionNames.Navigate, InputAxis.GamepadLeftStickY);

            return ctx;
        }

        /// <summary>创建编辑器映射上下文</summary>
        public static InputMappingContext CreateEditorContext()
        {
            var ctx = new InputMappingContext("Editor", priority: 5, displayName: "Editor Controls");

            ctx.BindKeyboard(InputActionNames.Select, InputKey.MouseLeft);
            ctx.BindKeyboard(InputActionNames.Delete, InputKey.Delete);

            ctx.BindKeyboard(InputActionNames.Undo, InputKey.Z);
            ctx.BindKeyboard(InputActionNames.Undo, InputKey.Control);

            ctx.BindKeyboard(InputActionNames.Redo, InputKey.Y);
            ctx.BindKeyboard(InputActionNames.Redo, InputKey.Control);

            ctx.BindKeyboard(InputActionNames.Duplicate, InputKey.D);
            ctx.BindKeyboard(InputActionNames.Duplicate, InputKey.Control);

            return ctx;
        }
    }
}
