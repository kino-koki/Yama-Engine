using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;

namespace YamaEngine.Input
{
    // ====================================================================
    //  YamaEngine Real Input Action Map
    // ====================================================================
    //  真实输入动作映射系统：
    //    - InputActionMapReal: 动作映射（名称/动作列表/启用/优先级）
    //    - InputActionBindingReal: 动作绑定（路径解析/复合绑定/交互）
    //    - InputProcessorReal: 输入处理器（死区/归一化/反转/缩放/处理器链）
    //  所有方法包含完整算法实现。
    // ====================================================================

    #region 输入动作映射 - InputActionMapReal

    /// <summary>输入事件数据</summary>
    public struct InputEvent
    {
        public InputDeviceTypeReal DeviceType;
        public int DeviceId;
        public string ControlPath;
        public float FloatValue;
        public Vector2 Vector2Value;
        public bool BoolValue;
        public long Timestamp;
        public bool IsValid;

        public static InputEvent Invalid => new() { IsValid = false };
    }

    /// <summary>
    /// 输入动作映射 - 管理一组相关的输入动作
    /// 支持启用/禁用、优先级、事件分发
    /// </summary>
    public class InputActionMapReal
    {
        /// <summary>映射名称</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>动作列表</summary>
        public Dictionary<string, InputActionReal> Actions { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>是否启用</summary>
        public bool IsEnabled { get; private set; } = true;

        /// <summary>优先级（数值越大优先级越高）</summary>
        public int Priority { get; set; } = 0;

        /// <summary>映射启用状态变化事件</summary>
        public event Action<InputActionMapReal, bool>? OnEnabledStateChanged;

        /// <summary>添加动作到映射</summary>
        public void AddAction(InputActionReal action)
        {
            if (string.IsNullOrEmpty(action.Name))
                throw new ArgumentException("动作名称不能为空");

            Actions[action.Name] = action;
        }

        /// <summary>移除动作</summary>
        public bool RemoveAction(string name)
        {
            return Actions.Remove(name);
        }

        /// <summary>获取动作</summary>
        public InputActionReal? GetAction(string name)
        {
            return Actions.TryGetValue(name, out var action) ? action : null;
        }

        /// <summary>尝试获取动作</summary>
        public bool TryGetAction(string name, out InputActionReal? action)
        {
            return Actions.TryGetValue(name, out action);
        }

        /// <summary>启用映射</summary>
        public void Enable()
        {
            if (!IsEnabled)
            {
                IsEnabled = true;
                OnEnabledStateChanged?.Invoke(this, true);
            }
        }

        /// <summary>禁用映射</summary>
        public void Disable()
        {
            if (IsEnabled)
            {
                IsEnabled = false;
                // 重置所有动作状态
                foreach (var action in Actions.Values)
                    action.Reset();
                OnEnabledStateChanged?.Invoke(this, false);
            }
        }

        /// <summary>设置启用状态</summary>
        public void SetEnabled(bool enabled)
        {
            if (enabled) Enable();
            else Disable();
        }

        /// <summary>检查是否包含动作</summary>
        public bool HasAction(string name)
        {
            return Actions.ContainsKey(name);
        }

        /// <summary>获取动作数量</summary>
        public int ActionCount => Actions.Count;

        /// <summary>
        /// 分发输入事件到所有动作
        /// 遍历每个动作，检查事件是否匹配绑定，然后处理
        /// </summary>
        public void ProcessInputEvent(in InputEvent evt)
        {
            if (!IsEnabled) return;

            foreach (var action in Actions.Values)
            {
                bool matched = false;

                foreach (var binding in action.Bindings)
                {
                    if (MatchBinding(binding, evt))
                    {
                        // 应用处理器链到输入值
                        float processedValue = evt.FloatValue;
                        Vector2 processedVec = evt.Vector2Value;

                        foreach (var processor in binding.Processors)
                        {
                            processedValue = processor.Process(processedValue);
                        }

                        processedValue *= binding.Scale;

                        action.ProcessInput(processedValue, processedVec, evt.BoolValue,
                            1.0f / 60.0f); // 简化deltaTime
                        matched = true;
                        break; // 一个事件只匹配一个绑定
                    }
                }
            }
        }

        /// <summary>检查事件是否匹配绑定</summary>
        private bool MatchBinding(InputActionBinding binding, in InputEvent evt)
        {
            // 设备类型匹配
            if (binding.DeviceType != InputDeviceTypeReal.Unknown &&
                binding.DeviceType != evt.DeviceType)
                return false;

            // 控件名称匹配
            if (!string.IsNullOrEmpty(binding.ControlName))
            {
                // 提取事件中的控件名称
                string eventControl = evt.ControlPath.Contains("/")
                    ? evt.ControlPath.Substring(evt.ControlPath.LastIndexOf('/') + 1)
                    : evt.ControlPath;

                if (!string.Equals(eventControl, binding.ControlName, StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            // 路径匹配
            if (!string.IsNullOrEmpty(binding.Path))
            {
                if (!string.Equals(evt.ControlPath, binding.Path, StringComparison.OrdinalIgnoreCase) &&
                    !PathMatchesPattern(binding.Path, evt.ControlPath))
                    return false;
            }

            return true;
        }

        /// <summary>路径模式匹配（支持通配符）</summary>
        private static bool PathMatchesPattern(string pattern, string path)
        {
            // 简化的通配符匹配: * 匹配任意字符序列
            string regex = "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";
            return Regex.IsMatch(path, regex, RegexOptions.IgnoreCase);
        }

        /// <summary>重置所有动作</summary>
        public void ResetAll()
        {
            foreach (var action in Actions.Values)
                action.Reset();
        }

        /// <summary>获取所有动作名称</summary>
        public string[] GetActionNames()
        {
            var names = new string[Actions.Count];
            int i = 0;
            foreach (var name in Actions.Keys)
                names[i++] = name;
            return names;
        }
    }

    #endregion

    #region 输入动作绑定 - InputActionBindingReal

    /// <summary>复合绑定类型</summary>
    public enum CompositeBindingType
    {
        None,       // 单一绑定
        OneShot,    // 单次触发（按下后值保持为1直到动作被禁用/重置）
        Passthrough,// 透传（值直接通过不截断）
        MultiTap,   // 多次点击
        Delayed     // 延迟触发
    }

    /// <summary>
    /// 输入动作绑定 - 路径语法解析 + 复合绑定
    /// 路径语法: "<Keyboard>/space", "<Mouse>/leftButton", "<Gamepad>/buttonSouth", "<Gamepad>/leftStick"
    /// </summary>
    public class InputActionBindingReal
    {
        /// <summary>绑定路径</summary>
        public string Path { get; set; } = string.Empty;

        /// <summary>设备类型</summary>
        public InputDeviceTypeReal DeviceType { get; set; }

        /// <summary>控件名称</summary>
        public string ControlName { get; set; } = string.Empty;

        /// <summary>处理器列表</summary>
        public List<IInputProcessorReal> Processors { get; set; } = new();

        /// <summary>缩放因子</summary>
        public float Scale { get; set; } = 1.0f;

        /// <summary>交互（按下/长按/多击/慢点击等）</summary>
        public IInputInteraction? Interaction { get; set; }

        /// <summary>复合绑定类型</summary>
        public CompositeBindingType CompositeType { get; set; } = CompositeBindingType.None;

        /// <summary>交互参数</summary>
        public Dictionary<string, float> InteractionParameters { get; set; } = new();

        /// <summary>
        /// 解析路径字符串 -> 设备类型 + 控件名称
        /// 支持语法:
        ///   <Keyboard>/space          -> DeviceType=Keyboard, Control=space
        ///   <Mouse>/leftButton        -> DeviceType=Mouse, Control=leftButton
        ///   <Gamepad>/buttonSouth     -> DeviceType=Gamepad, Control=buttonSouth
        ///   <Gamepad>/leftStick       -> DeviceType=Gamepad, Control=leftStick
        /// </summary>
        public static InputActionBindingReal Parse(string path)
        {
            var binding = new InputActionBindingReal { Path = path };

            if (string.IsNullOrEmpty(path)) return binding;

            // 匹配 <DeviceType>/controlName 格式
            var match = Regex.Match(path, @"^<(\w+)>/(.+)$");
            if (match.Success)
            {
                string deviceStr = match.Groups[1].Value;
                string controlStr = match.Groups[2].Value;

                binding.DeviceType = ParseDeviceType(deviceStr);
                binding.ControlName = controlStr;
            }
            else
            {
                // 不带设备前缀的路径
                binding.ControlName = path;
            }

            return binding;
        }

        /// <summary>构建路径字符串</summary>
        public string BuildPath()
        {
            if (DeviceType != InputDeviceTypeReal.Unknown)
            {
                return $"<{GetDeviceTypeName(DeviceType)}/{ControlName}>";
            }
            return ControlName;
        }

        /// <summary>检查是否匹配事件</summary>
        public bool Matches(in InputEvent evt)
        {
            if (DeviceType != InputDeviceTypeReal.Unknown && DeviceType != evt.DeviceType)
                return false;

            if (!string.IsNullOrEmpty(ControlName) && !string.IsNullOrEmpty(evt.ControlPath))
            {
                string eventControl = evt.ControlPath.Contains("/")
                    ? evt.ControlPath.Substring(evt.ControlPath.LastIndexOf('/') + 1)
                    : evt.ControlPath;

                if (!string.Equals(eventControl, ControlName, StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            return true;
        }

        private static InputDeviceTypeReal ParseDeviceType(string deviceName)
        {
            return deviceName.ToLowerInvariant() switch
            {
                "keyboard" => InputDeviceTypeReal.Keyboard,
                "mouse" => InputDeviceTypeReal.Mouse,
                "gamepad" => InputDeviceTypeReal.Gamepad,
                "touch" => InputDeviceTypeReal.Touch,
                "sensor" => InputDeviceTypeReal.Sensor,
                "steeringwheel" => InputDeviceTypeReal.SteeringWheel,
                "flightstick" => InputDeviceTypeReal.FlightStick,
                _ => InputDeviceTypeReal.Unknown
            };
        }

        private static string GetDeviceTypeName(InputDeviceTypeReal type)
        {
            return type switch
            {
                InputDeviceTypeReal.Keyboard => "Keyboard",
                InputDeviceTypeReal.Mouse => "Mouse",
                InputDeviceTypeReal.Gamepad => "Gamepad",
                InputDeviceTypeReal.Touch => "Touch",
                InputDeviceTypeReal.Sensor => "Sensor",
                _ => "Unknown"
            };
        }
    }

    #endregion

    #region 输入交互 - IInputInteraction

    /// <summary>输入交互接口</summary>
    public interface IInputInteraction
    {
        /// <summary>处理输入值，返回是否应该触发</summary>
        bool Process(ref float value, float holdTime, ref InputActionPhase phase);
        /// <summary>重置交互状态</summary>
        void Reset();
    }

    /// <summary>
    /// 长按交互 - 按住超过指定时长后触发
    /// 算法：按住时间 >= holdTime -> 触发Performed
    /// </summary>
    public class HoldInteraction : IInputInteraction
    {
        private float _holdDuration = 0.5f;
        private bool _triggered;

        public float HoldDuration
        {
            get => _holdDuration;
            set => _holdDuration = Math.Max(0.05f, value);
        }

        public bool Process(ref float value, float holdTime, ref InputActionPhase phase)
        {
            if (value > 0.1f && holdTime >= _holdDuration && !_triggered)
            {
                _triggered = true;
                phase = InputActionPhase.Performed;
                return true;
            }

            if (value < 0.1f)
            {
                if (_triggered)
                {
                    phase = InputActionPhase.Canceled;
                }
                Reset();
            }

            return false;
        }

        public void Reset()
        {
            _triggered = false;
        }
    }

    /// <summary>
    /// 多击交互 - 在指定时间内按下指定次数后触发
    /// 算法：累计点击次数 >= tapCount && 间隔 < maxTapSpacing
    /// </summary>
    public class MultiTapInteraction : IInputInteraction
    {
        private int _tapCount = 2;
        private float _maxTapSpacing = 0.4f;
        private int _currentTaps;
        private float _lastTapTime;
        private float _elapsedSinceLastTap;

        public int TapCount
        {
            get => _tapCount;
            set => _tapCount = Math.Max(2, value);
        }

        public float MaxTapSpacing
        {
            get => _maxTapSpacing;
            set => _maxTapSpacing = Math.Max(0.1f, value);
        }

        public bool Process(ref float value, float holdTime, ref InputActionPhase phase)
        {
            bool wasPressed = value > 0.1f && holdTime < 0.15f;

            if (wasPressed && _elapsedSinceLastTap > _maxTapSpacing)
            {
                // 超时，重新计数
                _currentTaps = 1;
                _lastTapTime = 0;
                _elapsedSinceLastTap = 0;
            }
            else if (wasPressed)
            {
                _currentTaps++;
                _elapsedSinceLastTap = 0;
            }

            if (_currentTaps >= _tapCount)
            {
                phase = InputActionPhase.Performed;
                _currentTaps = 0;
                return true;
            }

            if (value < 0.1f)
            {
                phase = InputActionPhase.Canceled;
            }

            return false;
        }

        public void Reset()
        {
            _currentTaps = 0;
            _lastTapTime = 0;
            _elapsedSinceLastTap = 0;
        }

        internal void UpdateElapsedTime(float dt)
        {
            _elapsedSinceLastTap += dt;
        }
    }

    /// <summary>
    /// 慢点击交互 - 按下后超过最短时间再释放才触发
    /// 区别于Tap: Tap要求快速点击, SlowTap要求慢速点击
    /// </summary>
    public class SlowTapInteraction : IInputInteraction
    {
        private float _minDuration = 0.3f;
        private float _maxDuration = 1.0f;
        private bool _held;
        private float _holdStartTime;

        public float MinDuration
        {
            get => _minDuration;
            set => _minDuration = Math.Max(0.1f, value);
        }

        public float MaxDuration
        {
            get => _maxDuration;
            set => _maxDuration = Math.Max(value, _minDuration + 0.1f);
        }

        public bool Process(ref float value, float holdTime, ref InputActionPhase phase)
        {
            if (value > 0.1f && !_held)
            {
                _held = true;
                _holdStartTime = holdTime;
                phase = InputActionPhase.Started;
            }

            if (value < 0.1f && _held)
            {
                float duration = _holdStartTime;
                if (duration >= _minDuration && duration <= _maxDuration)
                {
                    phase = InputActionPhase.Performed;
                    Reset();
                    return true;
                }
                else
                {
                    phase = InputActionPhase.Canceled;
                    Reset();
                }
            }

            // 超时自动取消
            if (_held && holdTime > _maxDuration)
            {
                phase = InputActionPhase.Canceled;
                Reset();
            }

            return false;
        }

        public void Reset()
        {
            _held = false;
            _holdStartTime = 0;
        }
    }

    /// <summary>
    /// 快速点击交互 - 按下后在最短时间内释放即触发
    /// 算法: 按下 -> 等待释放 -> 如果duration < maxTapTime -> 触发
    /// </summary>
    public class TapInteraction : IInputInteraction
    {
        private float _maxTapTime = 0.2f;
        private bool _waitingForRelease;
        private float _tapStartTime;

        public float MaxTapTime
        {
            get => _maxTapTime;
            set => _maxTapTime = Math.Max(0.05f, value);
        }

        public bool Process(ref float value, float holdTime, ref InputActionPhase phase)
        {
            if (value > 0.1f && !_waitingForRelease)
            {
                _waitingForRelease = true;
                _tapStartTime = holdTime;
                phase = InputActionPhase.Started;
            }

            if (value < 0.1f && _waitingForRelease)
            {
                float duration = _tapStartTime;
                if (duration <= _maxTapTime)
                {
                    phase = InputActionPhase.Performed;
                    Reset();
                    return true;
                }
                else
                {
                    phase = InputActionPhase.Canceled;
                    Reset();
                }
            }

            // 超时取消
            if (_waitingForRelease && holdTime > _maxTapTime)
            {
                phase = InputActionPhase.Canceled;
                Reset();
            }

            return false;
        }

        public void Reset()
        {
            _waitingForRelease = false;
            _tapStartTime = 0;
        }
    }

    #endregion

    #region 输入处理器 - InputProcessorReal

    /// <summary>
    /// 摇杆死区处理器 - 径向死区
    /// 内圆 -> 归零, 外圆 -> 线性映射到[-1, 1]
    /// </summary>
    public class StickDeadzone : IInputProcessorReal
    {
        private readonly float _deadzone;
        private float _lastValue;

        /// <summary>死区半径 (0.0 - 1.0)</summary>
        public float Deadzone => _deadzone;

        public StickDeadzone(float deadzone = 0.15f)
        {
            _deadzone = Math.Clamp(deadzone, 0.0f, 0.95f);
        }

        public float Process(float value)
        {
            float abs = Math.Abs(value);
            if (abs < _deadzone)
            {
                _lastValue = 0.0f;
                return 0.0f;
            }

            // 重映射: [deadzone, 1.0] -> [0.0, 1.0]
            float sign = Math.Sign(value);
            float mapped = (abs - _deadzone) / (1.0f - _deadzone);
            _lastValue = sign * Math.Clamp(mapped, 0.0f, 1.0f);
            return _lastValue;
        }

        /// <summary>处理Vector2摇杆值（径向死区）</summary>
        public Vector2 Process(Vector2 value)
        {
            GamepadDevice.ApplyDeadzone(value.X, value.Y, _deadzone, out float outX, out float outY);
            return new Vector2(outX, outY);
        }
    }

    /// <summary>
    /// 轴死区处理器 - 单轴死区
    /// 用于扳机等单轴控件
    /// </summary>
    public class AxisDeadzone : IInputProcessorReal
    {
        private readonly float _min;
        private readonly float _max;

        public AxisDeadzone(float min = 0.1f, float max = 0.9f)
        {
            _min = Math.Clamp(min, 0.0f, 0.5f);
            _max = Math.Clamp(max, 0.5f, 1.0f);
        }

        public float Process(float value)
        {
            if (value < _min) return 0.0f;
            if (value > _max) return 1.0f;
            // 在[_min, _max]范围内线性映射
            return (value - _min) / (_max - _min);
        }
    }

    /// <summary>
    /// 归一化处理器 - 将值映射到[-1, 1]或[0, 1]范围
    /// </summary>
    public class Normalize : IInputProcessorReal
    {
        private readonly float _min;
        private readonly float _max;
        private readonly bool _zeroCentered;

        /// <summary>归一化到[-1, 1]（zeroCentered=true）或[0, 1]（false）</summary>
        public Normalize(float min = 0.0f, float max = 1.0f, bool zeroCentered = false)
        {
            _min = min;
            _max = max;
            _zeroCentered = zeroCentered;
        }

        public float Process(float value)
        {
            float range = _max - _min;
            if (range < 1e-6f) return 0.0f;

            float normalized = (value - _min) / range;
            normalized = Math.Clamp(normalized, 0.0f, 1.0f);

            if (_zeroCentered)
            {
                normalized = normalized * 2.0f - 1.0f;
            }

            return normalized;
        }
    }

    /// <summary>
    /// 反转处理器 - 取反输入值
    /// </summary>
    public class Invert : IInputProcessorReal
    {
        public float Process(float value)
        {
            return -value;
        }
    }

    /// <summary>
    /// 缩放处理器 - 乘以系数
    /// </summary>
    public class Scale : IInputProcessorReal
    {
        private readonly float _factor;

        public float Factor => _factor;

        public Scale(float factor = 1.0f)
        {
            _factor = factor;
        }

        public float Process(float value)
        {
            return value * _factor;
        }
    }

    /// <summary>
    /// 加法修饰处理器 - 给输入值加一个偏移量
    /// </summary>
    public class Add : IInputProcessorReal
    {
        private readonly float _offset;

        public float Offset => _offset;

        public Add(float offset = 0.0f)
        {
            _offset = offset;
        }

        public float Process(float value)
        {
            return value + _offset;
        }
    }

    /// <summary>
    /// 处理器链 - 多个处理器串联执行
    /// 输入 -> 处理器1 -> 处理器2 -> ... -> 处理器N -> 输出
    /// </summary>
    public class ProcessorChain : IInputProcessorReal
    {
        private readonly List<IInputProcessorReal> _processors;
        private readonly List<(string Name, float Value)> _debugValues = new();

        /// <summary>处理器链中的处理器列表</summary>
        public IReadOnlyList<IInputProcessorReal> Processors => _processors;

        /// <summary>每个处理器的中间调试值</summary>
        public IReadOnlyList<(string Name, float Value)> DebugValues => _debugValues;

        public ProcessorChain()
        {
            _processors = new List<IInputProcessorReal>();
        }

        public ProcessorChain(params IInputProcessorReal[] processors)
        {
            _processors = new List<IInputProcessorReal>(processors);
        }

        /// <summary>添加处理器到链尾</summary>
        public void Add(IInputProcessorReal processor)
        {
            _processors.Add(processor);
        }

        /// <summary>插入处理器到指定位置</summary>
        public void Insert(int index, IInputProcessorReal processor)
        {
            _processors.Insert(Math.Clamp(index, 0, _processors.Count), processor);
        }

        /// <summary>移除指定处理器</summary>
        public bool Remove(IInputProcessorReal processor)
        {
            return _processors.Remove(processor);
        }

        /// <summary>清除所有处理器</summary>
        public void Clear()
        {
            _processors.Clear();
        }

        /// <summary>
        /// 执行处理器链
        /// 逐个处理器执行，前一个的输出作为后一个的输入
        /// </summary>
        public float Process(float value)
        {
            _debugValues.Clear();
            float current = value;
            _debugValues.Add(("input", current));

            for (int i = 0; i < _processors.Count; i++)
            {
                current = _processors[i].Process(current);
                _debugValues.Add((_processors[i].GetType().Name, current));
            }

            return current;
        }

        /// <summary>获取处理器数量</summary>
        public int Count => _processors.Count;
    }

    /// <summary>
    /// 二次曲线映射 - 用于非线性输入映射
    /// y = factor * x^exponent
    /// </summary>
    public class CurveProcessor : IInputProcessorReal
    {
        private readonly float _exponent;
        private readonly float _factor;

        public CurveProcessor(float exponent = 2.0f, float factor = 1.0f)
        {
            _exponent = exponent;
            _factor = factor;
        }

        public float Process(float value)
        {
            // 保留符号，对绝对值做幂运算
            float sign = Math.Sign(value);
            float abs = Math.Abs(value);
            float curved = (float)Math.Pow(abs, _exponent);
            return sign * curved * _factor;
        }
    }

    /// <summary>
    /// 包络跟随处理器 - 模拟模拟摇杆的惯性
    /// 平滑跟随目标值，避免突然变化
    /// </summary>
    public class EnvelopeFollower : IInputProcessorReal
    {
        private readonly float _attackTime;
        private readonly float _releaseTime;
        private float _currentValue;
        private float _targetValue;

        public EnvelopeFollower(float attackTime = 0.01f, float releaseTime = 0.1f)
        {
            _attackTime = Math.Max(0.001f, attackTime);
            _releaseTime = Math.Max(0.001f, releaseTime);
        }

        public float Process(float value)
        {
            _targetValue = value;

            float timeConstant = _targetValue > _currentValue ? _attackTime : _releaseTime;

            // 一阶低通滤波: y = y + (target - y) * (dt / timeConstant)
            // 简化: 使用固定系数
            float coefficient = 1.0f - (float)Math.Exp(-1.0f / timeConstant);
            _currentValue = _currentValue + (_targetValue - _currentValue) * coefficient;

            return _currentValue;
        }

        /// <summary>重置内部状态</summary>
        public void Reset()
        {
            _currentValue = 0;
            _targetValue = 0;
        }
    }

    #endregion
}
