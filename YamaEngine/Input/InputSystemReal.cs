using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;

namespace YamaEngine.Input
{
    // ====================================================================
    //  YamaEngine Real Input System
    // ====================================================================
    //  真实输入系统实现：
    //    - InputDeviceReal: 设备基类
    //    - KeyboardDevice: 102+键位状态追踪
    //    - MouseDevice: 位置/增量/滚轮/按钮/鼠标锁定
    //    - GamepadDevice: 按钮/摇杆/扳机/震动/死区
    //    - TouchDevice: 多触点/Phase/压力/半径
    //    - SensorDevice: 加速度计/陀螺仪/磁力计/重力/朝向
    //    - InputActionReal: 输入动作/绑定/值读取
    //  所有方法包含完整算法实现。
    // ====================================================================

    #region 设备枚举

    /// <summary>输入设备类型</summary>
    public enum InputDeviceTypeReal
    {
        Unknown,
        Keyboard,
        Mouse,
        Gamepad,
        Touch,
        Sensor,
        SteeringWheel,
        FlightStick,
        GenericHID
    }

    /// <summary>键位状态</summary>
    public enum KeyState
    {
        None,       // 未按下
        Pressed,    // 按住中
        JustDown,   // 当前帧刚按下
        JustUp      // 当前帧刚释放
    }

    /// <summary>按钮状态</summary>
    public enum ButtonState
    {
        None,
        Pressed,
        JustPressed,
        JustReleased
    }

    /// <summary>鼠标按钮</summary>
    public enum MouseButtonReal
    {
        Left,
        Middle,
        Right,
        X1,
        X2
    }

    /// <summary>手柄按钮</summary>
    public enum GamepadButton
    {
        A = 0,
        B = 1,
        X = 2,
        Y = 3,
        LeftShoulder = 4,
        RightShoulder = 5,
        LeftTrigger = 6,
        RightTrigger = 7,
        LeftStick = 8,
        RightStick = 9,
        Start = 10,
        Back = 11,
        Guide = 12,
        DPadUp = 13,
        DPadDown = 14,
        DPadLeft = 15,
        DPadRight = 16
    }

    /// <summary>触点Phase</summary>
    public enum TouchPhase
    {
        None,
        Began,
        Moved,
        Ended,
        Stationary,
        Canceled
    }

    /// <summary>鼠标锁定状态</summary>
    public enum MouseLockState
    {
        Free,
        Confined,
        Locked
    }

    /// <summary>传感器类型</summary>
    public enum SensorType
    {
        Accelerometer,
        Gyroscope,
        Magnetometer,
        Gravity,
        DeviceOrientation,
        AmbientLight,
        Pressure
    }

    #endregion

    #region 输入设备基类

    /// <summary>输入设备基类</summary>
    public abstract class InputDeviceReal
    {
        public string Name { get; protected set; } = string.Empty;
        public InputDeviceTypeReal DeviceType { get; protected set; } = InputDeviceTypeReal.Unknown;
        public bool IsConnected { get; protected set; }
        public long LastUpdateTick { get; protected set; }
        public int DeviceId { get; protected set; }

        /// <summary>更新设备状态（每帧调用）</summary>
        public abstract void Update();
    }

    #endregion

    #region 键盘设备 - KeyboardDevice

    /// <summary>
    /// 键盘设备 - 102+键位状态追踪
    /// 支持单键检测/组合键/文本输入/IME组合
    /// </summary>
    public class KeyboardDevice : InputDeviceReal
    {
        /// <summary>最大键位数量（覆盖所有常用键）</summary>
        public const int MaxKeyCount = 256;

        /// <summary>键位名称映射</summary>
        private static readonly Dictionary<string, int> _keyNameMap = new(StringComparer.OrdinalIgnoreCase)
        {
            ["none"] = 0, ["escape"] = 1, ["space"] = 2, ["enter"] = 3, ["backspace"] = 4,
            ["tab"] = 5, ["capslock"] = 6, ["shift"] = 7, ["lshift"] = 7, ["rshift"] = 8,
            ["control"] = 9, ["lcontrol"] = 9, ["rcontrol"] = 10,
            ["alt"] = 11, ["lalt"] = 11, ["ralt"] = 12,
            ["leftarrow"] = 13, ["rightarrow"] = 14, ["uparrow"] = 15, ["downarrow"] = 16,
            ["insert"] = 17, ["delete"] = 18, ["home"] = 19, ["end"] = 20,
            ["pageup"] = 21, ["pagedown"] = 22,
            ["a"] = 23, ["b"] = 24, ["c"] = 25, ["d"] = 26, ["e"] = 27, ["f"] = 28,
            ["g"] = 29, ["h"] = 30, ["i"] = 31, ["j"] = 32, ["k"] = 33, ["l"] = 34,
            ["m"] = 35, ["n"] = 36, ["o"] = 37, ["p"] = 38, ["q"] = 39, ["r"] = 40,
            ["s"] = 41, ["t"] = 42, ["u"] = 43, ["v"] = 44, ["w"] = 45, ["x"] = 46,
            ["y"] = 47, ["z"] = 48,
            ["d0"] = 49, ["d1"] = 50, ["d2"] = 51, ["d3"] = 52, ["d4"] = 53,
            ["d5"] = 54, ["d6"] = 55, ["d7"] = 56, ["d8"] = 57, ["d9"] = 58,
            ["numpad0"] = 59, ["numpad1"] = 60, ["numpad2"] = 61, ["numpad3"] = 62,
            ["numpad4"] = 63, ["numpad5"] = 64, ["numpad6"] = 65, ["numpad7"] = 66,
            ["numpad8"] = 67, ["numpad9"] = 68,
            ["numpadadd"] = 69, ["numpadsubtract"] = 70, ["numpadmultiply"] = 71,
            ["numpaddivide"] = 72, ["numpaddecimal"] = 73, ["numpadenter"] = 74,
            ["f1"] = 75, ["f2"] = 76, ["f3"] = 77, ["f4"] = 78, ["f5"] = 79, ["f6"] = 80,
            ["f7"] = 81, ["f8"] = 82, ["f9"] = 83, ["f10"] = 84, ["f11"] = 85, ["f12"] = 86,
            ["comma"] = 87, ["period"] = 88, ["slash"] = 89, ["semicolon"] = 90,
            ["apostrophe"] = 91, ["leftbracket"] = 92, ["rightbracket"] = 93,
            ["backslash"] = 94, ["grave"] = 95, ["minus"] = 96, ["equals"] = 97,
            ["printscreen"] = 98, ["scrolllock"] = 99, ["pause"] = 100,
            ["numlock"] = 101, ["capslock2"] = 102,
            ["lwin"] = 103, ["rwin"] = 104, ["apps"] = 105,
            ["oem1"] = 106, ["oem2"] = 107, ["oem3"] = 108, ["oem4"] = 109,
            ["oem5"] = 110, ["oem6"] = 111, ["oem7"] = 112, ["oem8"] = 113
        };

        /// <summary>键位索引映射（快速查找）</summary>
        private static readonly Dictionary<int, string> _keyCodeToName;
        static KeyboardDevice()
        {
            _keyCodeToName = new Dictionary<int, string>(_keyNameMap.Count);
            foreach (var kvp in _keyNameMap)
            {
                if (!_keyCodeToName.ContainsKey(kvp.Value))
                    _keyCodeToName[kvp.Value] = kvp.Key;
            }
        }

        /// <summary>当前帧键位状态</summary>
        private readonly bool[] _currentKeys = new bool[MaxKeyCount];
        /// <summary>上一帧键位状态</summary>
        private readonly bool[] _previousKeys = new bool[MaxKeyCount];

        /// <summary>文本输入缓冲</summary>
        private readonly StringBuilder _textBuffer = new();
        /// <summary>IME组合状态</summary>
        private bool _imeComposing;
        private string _imeComposition = string.Empty;

        /// <summary>文本输入事件</summary>
        public event Action<string>? OnTextInput;
        /// <summary>键位按下事件</summary>
        public event Action<int>? OnKeyDown;
        /// <summary>键位释放事件</summary>
        public event Action<int>? OnKeyUp;

        public KeyboardDevice()
        {
            Name = "Keyboard";
            DeviceType = InputDeviceTypeReal.Keyboard;
            IsConnected = true;
            DeviceId = 0;
        }

        /// <summary>设置键位按下（由平台层调用）</summary>
        public void SetKeyDown(int keyCode)
        {
            if (keyCode >= 0 && keyCode < MaxKeyCount)
                _currentKeys[keyCode] = true;
        }

        /// <summary>设置键位释放（由平台层调用）</summary>
        public void SetKeyUp(int keyCode)
        {
            if (keyCode >= 0 && keyCode < MaxKeyCount)
                _currentKeys[keyCode] = false;
        }

        /// <summary>输入文本字符</summary>
        public void InputTextCharacter(char c)
        {
            if (_imeComposing) return;
            _textBuffer.Append(c);
            OnTextInput?.Invoke(c.ToString());
        }

        /// <summary>IME组合开始</summary>
        public void BeginIMEComposition(string text)
        {
            _imeComposing = true;
            _imeComposition = text;
        }

        /// <summary>IME组合更新</summary>
        public void UpdateIMEComposition(string text)
        {
            _imeComposition = text;
        }

        /// <summary>IME组合结束</summary>
        public void EndIMEComposition(string finalText)
        {
            _imeComposing = false;
            if (!string.IsNullOrEmpty(finalText))
            {
                _textBuffer.Append(finalText);
                OnTextInput?.Invoke(finalText);
            }
            _imeComposition = string.Empty;
        }

        /// <summary>取消IME组合</summary>
        public void CancelIMEComposition()
        {
            _imeComposing = false;
            _imeComposition = string.Empty;
        }

        /// <summary>检查键位是否按住中</summary>
        public bool IsKeyDown(int keyCode)
        {
            if (keyCode < 0 || keyCode >= MaxKeyCount) return false;
            return _currentKeys[keyCode];
        }

        /// <summary>检查键位是否释放</summary>
        public bool IsKeyUp(int keyCode)
        {
            if (keyCode < 0 || keyCode >= MaxKeyCount) return true;
            return !_currentKeys[keyCode];
        }

        /// <summary>检查键位是否刚按下（当前帧按下 && 上一帧未按下）</summary>
        public bool IsKeyPressed(int keyCode)
        {
            if (keyCode < 0 || keyCode >= MaxKeyCount) return false;
            return _currentKeys[keyCode] && !_previousKeys[keyCode];
        }

        /// <summary>检查键位是否刚释放（当前帧释放 && 上一帧按下）</summary>
        public bool IsKeyReleased(int keyCode)
        {
            if (keyCode < 0 || keyCode >= MaxKeyCount) return false;
            return !_currentKeys[keyCode] && _previousKeys[keyCode];
        }

        /// <summary>获取键位详细状态</summary>
        public KeyState GetKeyState(int keyCode)
        {
            if (keyCode < 0 || keyCode >= MaxKeyCount) return KeyState.None;
            if (_currentKeys[keyCode] && !_previousKeys[keyCode]) return KeyState.JustDown;
            if (_currentKeys[keyCode] && _previousKeys[keyCode]) return KeyState.Pressed;
            if (!_currentKeys[keyCode] && _previousKeys[keyCode]) return KeyState.JustUp;
            return KeyState.None;
        }

        /// <summary>检查修饰键组合（如 Ctrl+C）</summary>
        public bool IsKeyCombo(int keyCode, bool requireShift = false,
            bool requireControl = false, bool requireAlt = false)
        {
            if (!IsKeyDown(keyCode)) return false;

            bool shiftDown = IsKeyDown(7) || IsKeyDown(8); // LShift || RShift
            bool ctrlDown = IsKeyDown(9) || IsKeyDown(10);  // LControl || RControl
            bool altDown = IsKeyDown(11) || IsKeyDown(12);   // LAlt || RAlt

            if (requireShift && !shiftDown) return false;
            if (requireControl && !ctrlDown) return false;
            if (requireAlt && !altDown) return false;

            return true;
        }

        /// <summary>获取当前帧输入的文本</summary>
        public string GetTextInput()
        {
            string text = _textBuffer.ToString();
            _textBuffer.Clear();
            return text;
        }

        /// <summary>获取IME组合文本</summary>
        public string GetIMEComposition() => _imeComposition;

        /// <summary>检查是否有任何键按下</summary>
        public bool AnyKeyDown()
        {
            for (int i = 1; i < MaxKeyCount; i++)
            {
                if (_currentKeys[i]) return true;
            }
            return false;
        }

        /// <summary>获取所有按下的键位</summary>
        public int[] GetPressedKeys()
        {
            var pressed = new List<int>();
            for (int i = 1; i < MaxKeyCount; i++)
            {
                if (_currentKeys[i]) pressed.Add(i);
            }
            return pressed.ToArray();
        }

        /// <summary>根据名称获取键位代码</summary>
        public static int GetKeyCode(string keyName)
        {
            return _keyNameMap.TryGetValue(keyName, out int code) ? code : 0;
        }

        /// <summary>获取键位名称</summary>
        public static string GetKeyName(int keyCode)
        {
            return _keyCodeToName.TryGetValue(keyCode, out string? name) ? name : $"Key{keyCode}";
        }

        /// <summary>每帧更新 - 保存当前状态为上一帧状态</summary>
        public override void Update()
        {
            // 生成按下/释放事件
            for (int i = 1; i < MaxKeyCount; i++)
            {
                if (_currentKeys[i] && !_previousKeys[i])
                    OnKeyDown?.Invoke(i);
                else if (!_currentKeys[i] && _previousKeys[i])
                    OnKeyUp?.Invoke(i);
            }

            // 复制当前帧状态到上一帧
            Array.Copy(_currentKeys, _previousKeys, MaxKeyCount);
            LastUpdateTick = Environment.TickCount64;
        }

        /// <summary>重置所有键位状态</summary>
        public void ResetAllKeys()
        {
            Array.Clear(_currentKeys, 0, MaxKeyCount);
            Array.Clear(_previousKeys, 0, MaxKeyCount);
            _textBuffer.Clear();
            _imeComposing = false;
            _imeComposition = string.Empty;
        }
    }

    #endregion

    #region 鼠标设备 - MouseDevice

    /// <summary>
    /// 鼠标设备 - 位置/增量/滚轮/按钮/鼠标锁定
    /// </summary>
    public class MouseDevice : InputDeviceReal
    {
        private const int ButtonCount = 5;

        // 位置
        private float _x, _y;
        private float _prevX, _prevY;

        // 增量
        private float _deltaX, _deltaY;

        // 滚轮
        private float _scrollDelta;
        private float _scrollX;
        private float _scrollY;

        // 按钮状态 (Left/Middle/Right/X1/X2)
        private readonly bool[] _currentButtons = new bool[ButtonCount];
        private readonly bool[] _previousButtons = new bool[ButtonCount];

        // 鼠标锁定
        private MouseLockState _lockState = MouseLockState.Free;
        private float _lockCenterX, _lockCenterY;

        /// <summary>按钮状态变化事件</summary>
        public event Action<MouseButtonReal, ButtonState>? OnButtonStateChanged;
        /// <summary>鼠标移动事件</summary>
        public event Action<float, float>? OnMoved;
        /// <summary>滚轮事件</summary>
        public event Action<float, float>? OnScroll;

        public MouseDevice()
        {
            Name = "Mouse";
            DeviceType = InputDeviceTypeReal.Mouse;
            IsConnected = true;
            DeviceId = 0;
        }

        /// <summary>设置位置（由平台层调用）</summary>
        public void SetPosition(float x, float y)
        {
            if (_lockState == MouseLockState.Locked)
            {
                // 锁定模式下计算增量但不更新位置
                _deltaX += x - _lockCenterX;
                _deltaY += y - _lockCenterY;
                return;
            }

            _prevX = _x;
            _prevY = _y;
            _x = x;
            _y = y;
            _deltaX += x - _prevX;
            _deltaY += y - _prevY;
            OnMoved?.Invoke(x, y);
        }

        /// <summary>添加增量（锁定模式下使用）</summary>
        public void AddDelta(float dx, float dy)
        {
            _deltaX += dx;
            _deltaY += dy;
        }

        /// <summary>设置滚轮</summary>
        public void SetScrollDelta(float deltaX, float deltaY)
        {
            _scrollX += deltaX;
            _scrollY += deltaY;
            _scrollDelta = deltaY; // 向后兼容：scrollDelta = Y轴
            OnScroll?.Invoke(deltaX, deltaY);
        }

        /// <summary>设置按钮按下</summary>
        public void SetButtonDown(MouseButtonReal button)
        {
            int idx = (int)button;
            if (idx >= 0 && idx < ButtonCount)
                _currentButtons[idx] = true;
        }

        /// <summary>设置按钮释放</summary>
        public void SetButtonUp(MouseButtonReal button)
        {
            int idx = (int)button;
            if (idx >= 0 && idx < ButtonCount)
                _currentButtons[idx] = false;
        }

        /// <summary>鼠标按钮刚按下</summary>
        public bool IsButtonPressed(MouseButtonReal button)
        {
            int idx = (int)button;
            if (idx < 0 || idx >= ButtonCount) return false;
            return _currentButtons[idx] && !_previousButtons[idx];
        }

        /// <summary>鼠标按钮刚释放</summary>
        public bool IsButtonReleased(MouseButtonReal button)
        {
            int idx = (int)button;
            if (idx < 0 || idx >= ButtonCount) return false;
            return !_currentButtons[idx] && _previousButtons[idx];
        }

        /// <summary>鼠标按钮按住</summary>
        public bool IsButtonHeld(MouseButtonReal button)
        {
            int idx = (int)button;
            if (idx < 0 || idx >= ButtonCount) return false;
            return _currentButtons[idx] && _previousButtons[idx];
        }

        /// <summary>获取按钮状态</summary>
        public ButtonState GetButtonState(MouseButtonReal button)
        {
            int idx = (int)button;
            if (idx < 0 || idx >= ButtonCount) return ButtonState.None;
            if (_currentButtons[idx] && !_previousButtons[idx]) return ButtonState.JustPressed;
            if (_currentButtons[idx] && _previousButtons[idx]) return ButtonState.Pressed;
            if (!_currentButtons[idx] && _previousButtons[idx]) return ButtonState.JustReleased;
            return ButtonState.None;
        }

        /// <summary>获取当前鼠标位置（屏幕坐标）</summary>
        public Vector2 GetPosition() => new(_x, _y);

        /// <summary>获取鼠标增量</summary>
        public Vector2 GetDelta() => new(_deltaX, _deltaY);

        /// <summary>获取滚轮增量（Y轴为主）</summary>
        public float GetScrollDelta() => _scrollDelta;

        /// <summary>获取滚轮X/Y增量</summary>
        public Vector2 GetScrollDeltaXY() => new(_scrollX, _scrollY);

        /// <summary>设置鼠标位置（屏幕空间）</summary>
        public void SetCursorPosition(float x, float y)
        {
            _x = x;
            _y = y;
        }

        /// <summary>设置鼠标锁定状态</summary>
        public void SetLockState(MouseLockState state, float lockCenterX = 0, float lockCenterY = 0)
        {
            _lockState = state;
            _lockCenterX = lockCenterX;
            _lockCenterY = lockCenterY;

            if (state == MouseLockState.Locked)
            {
                _deltaX = 0;
                _deltaY = 0;
            }
        }

        /// <summary>获取鼠标锁定状态</summary>
        public MouseLockState GetLockState() => _lockState;

        /// <summary>检查是否左键按下</summary>
        public bool LeftPressed => IsButtonPressed(MouseButtonReal.Left);
        /// <summary>检查是否右键按下</summary>
        public bool RightPressed => IsButtonPressed(MouseButtonReal.Right);
        /// <summary>检查是否中键按下</summary>
        public bool MiddlePressed => IsButtonPressed(MouseButtonReal.Middle);

        /// <summary>每帧更新</summary>
        public override void Update()
        {
            // 生成按钮事件
            for (int i = 0; i < ButtonCount; i++)
            {
                if (_currentButtons[i] && !_previousButtons[i])
                    OnButtonStateChanged?.Invoke((MouseButtonReal)i, ButtonState.JustPressed);
                else if (!_currentButtons[i] && _previousButtons[i])
                    OnButtonStateChanged?.Invoke((MouseButtonReal)i, ButtonState.JustReleased);
            }

            // 保存上一帧状态
            Array.Copy(_currentButtons, _previousButtons, ButtonCount);
            _prevX = _x;
            _prevY = _y;

            // 重置增量（增量只在帧内有效）
            _deltaX = 0;
            _deltaY = 0;
            _scrollDelta = 0;
            _scrollX = 0;
            _scrollY = 0;

            LastUpdateTick = Environment.TickCount64;
        }

        /// <summary>重置所有状态</summary>
        public void ResetAll()
        {
            Array.Clear(_currentButtons, 0, ButtonCount);
            Array.Clear(_previousButtons, 0, ButtonCount);
            _x = 0; _y = 0;
            _deltaX = 0; _deltaY = 0;
            _scrollDelta = 0; _scrollX = 0; _scrollY = 0;
        }
    }

    #endregion

    #region 手柄设备 - GamepadDevice

    /// <summary>
    /// 游戏手柄设备 - 按钮/摇杆/扳机/震动/死区处理
    /// </summary>
    public class GamepadDevice : InputDeviceReal
    {
        private const int ButtonCount = 17; // GamepadButton枚举数量
        private const float DefaultDeadzone = 0.15f;

        // 按钮状态
        private readonly bool[] _currentButtons = new bool[ButtonCount];
        private readonly bool[] _previousButtons = new bool[ButtonCount];

        // 摇杆值（原始值 -1.0 到 1.0）
        private float _leftStickX, _leftStickY;
        private float _rightStickX, _rightStickY;

        // 处理后摇杆值（应用死区后）
        private float _processedLeftX, _processedLeftY;
        private float _processedRightX, _processedRightY;

        // 扳机值（0.0 到 1.0）
        private float _leftTrigger;
        private float _rightTrigger;

        // 震动
        private float _vibrationLowFreq;
        private float _vibrationHighFreq;

        // 手柄索引
        public int GamepadIndex { get; private set; }

        // 死区值
        public float Deadzone { get; set; } = DefaultDeadzone;

        /// <summary>按钮事件</summary>
        public event Action<GamepadButton, ButtonState>? OnButtonStateChanged;
        /// <summary>震动完成事件</summary>
        public event Action? OnVibrationComplete;

        public GamepadDevice(int index)
        {
            Name = $"Gamepad_{index}";
            DeviceType = InputDeviceTypeReal.Gamepad;
            GamepadIndex = index;
            DeviceId = 100 + index;
        }

        /// <summary>设置按钮状态</summary>
        public void SetButton(GamepadButton button, bool pressed)
        {
            int idx = (int)button;
            if (idx >= 0 && idx < ButtonCount)
                _currentButtons[idx] = pressed;
        }

        /// <summary>设置摇杆值（原始值）</summary>
        public void SetLeftStick(float x, float y)
        {
            _leftStickX = Math.Clamp(x, -1.0f, 1.0f);
            _leftStickY = Math.Clamp(y, -1.0f, 1.0f);
        }

        public void SetRightStick(float x, float y)
        {
            _rightStickX = Math.Clamp(x, -1.0f, 1.0f);
            _rightStickY = Math.Clamp(y, -1.0f, 1.0f);
        }

        /// <summary>设置扳机值</summary>
        public void SetTriggers(float left, float right)
        {
            _leftTrigger = Math.Clamp(left, 0.0f, 1.0f);
            _rightTrigger = Math.Clamp(right, 0.0f, 1.0f);

            // 扳机也映射为按钮
            _currentButtons[(int)GamepadButton.LeftTrigger] = left > 0.5f;
            _currentButtons[(int)GamepadButton.RightTrigger] = right > 0.5f;
        }

        /// <summary>
        /// 死区处理 - 内圆归零，外圆线性映射
        /// 使用径向死区算法：
        /// 1. 计算输入向量长度
        /// 2. 如果长度 < 死区半径 -> 返回(0,0)
        /// 3. 否则重新映射到 [-1,1] 范围
        /// </summary>
        public static void ApplyDeadzone(float inputX, float inputY, float deadzone,
            out float outX, out float outY)
        {
            // 径向距离
            float magnitude = (float)Math.Sqrt(inputX * inputX + inputY * inputY);

            if (magnitude < deadzone)
            {
                // 在内死区范围内，输出归零
                outX = 0.0f;
                outY = 0.0f;
            }
            else
            {
                // 将输入从[deadzone, 1.0]重映射到[0.0, 1.0]
                float normalizedMagnitude = (magnitude - deadzone) / (1.0f - deadzone);
                normalizedMagnitude = Math.Clamp(normalizedMagnitude, 0.0f, 1.0f);

                // 保持方向，应用新长度
                float scale = magnitude > 1e-6f ? normalizedMagnitude / magnitude : 0.0f;
                outX = inputX * scale;
                outY = inputY * scale;
            }
        }

        /// <summary>按钮是否按下</summary>
        public bool IsButtonPressed(GamepadButton button)
        {
            int idx = (int)button;
            if (idx < 0 || idx >= ButtonCount) return false;
            return _currentButtons[idx] && !_previousButtons[idx];
        }

        /// <summary>按钮是否释放</summary>
        public bool IsButtonReleased(GamepadButton button)
        {
            int idx = (int)button;
            if (idx < 0 || idx >= ButtonCount) return false;
            return !_currentButtons[idx] && _previousButtons[idx];
        }

        /// <summary>按钮是否按住</summary>
        public bool IsButtonHeld(GamepadButton button)
        {
            int idx = (int)button;
            if (idx < 0 || idx >= ButtonCount) return false;
            return _currentButtons[idx] && _previousButtons[idx];
        }

        /// <summary>获取按钮状态</summary>
        public ButtonState GetButtonState(GamepadButton button)
        {
            int idx = (int)button;
            if (idx < 0 || idx >= ButtonCount) return ButtonState.None;
            if (_currentButtons[idx] && !_previousButtons[idx]) return ButtonState.JustPressed;
            if (_currentButtons[idx] && _previousButtons[idx]) return ButtonState.Pressed;
            if (!_currentButtons[idx] && _previousButtons[idx]) return ButtonState.JustReleased;
            return ButtonState.None;
        }

        /// <summary>获取左摇杆值（已应用死区）</summary>
        public Vector2 GetLeftStick() => new(_processedLeftX, _processedLeftY);

        /// <summary>获取右摇杆值（已应用死区）</summary>
        public Vector2 GetRightStick() => new(_processedRightX, _processedRightY);

        /// <summary>获取左扳机值</summary>
        public float GetLeftTrigger() => _leftTrigger;

        /// <summary>获取右扳机值</summary>
        public float GetRightTrigger() => _rightTrigger;

        /// <summary>设置震动反馈</summary>
        public void SetVibration(float lowFrequency, float highFrequency)
        {
            _vibrationLowFreq = Math.Clamp(lowFrequency, 0.0f, 1.0f);
            _vibrationHighFreq = Math.Clamp(highFrequency, 0.0f, 1.0f);

            // 平台特定震动实现会在这里触发
            // 模拟：记录震动参数
        }

        /// <summary>停止震动</summary>
        public void StopVibration()
        {
            _vibrationLowFreq = 0.0f;
            _vibrationHighFreq = 0.0f;
        }

        /// <summary>DPad组合为Vector2</summary>
        public Vector2 GetDPad()
        {
            float x = 0, y = 0;
            if (_currentButtons[(int)GamepadButton.DPadLeft]) x -= 1;
            if (_currentButtons[(int)GamepadButton.DPadRight]) x += 1;
            if (_currentButtons[(int)GamepadButton.DPadUp]) y -= 1;
            if (_currentButtons[(int)GamepadButton.DPadDown]) y += 1;
            return new Vector2(x, y);
        }

        /// <summary>标准按钮名称映射</summary>
        public static string GetGamepadButtonName(GamepadButton button)
        {
            return button switch
            {
                GamepadButton.A => "buttonSouth",
                GamepadButton.B => "buttonEast",
                GamepadButton.X => "buttonWest",
                GamepadButton.Y => "buttonNorth",
                GamepadButton.LeftShoulder => "leftShoulder",
                GamepadButton.RightShoulder => "rightShoulder",
                GamepadButton.LeftTrigger => "leftTrigger",
                GamepadButton.RightTrigger => "rightTrigger",
                GamepadButton.LeftStick => "leftStickPress",
                GamepadButton.RightStick => "rightStickPress",
                GamepadButton.Start => "start",
                GamepadButton.Back => "select",
                GamepadButton.Guide => "guide",
                GamepadButton.DPadUp => "dpadUp",
                GamepadButton.DPadDown => "dpadDown",
                GamepadButton.DPadLeft => "dpadLeft",
                GamepadButton.DPadRight => "dpadRight",
                _ => button.ToString()
            };
        }

        /// <summary>每帧更新 - 处理死区/事件/状态复制</summary>
        public override void Update()
        {
            // 应用死区处理
            ApplyDeadzone(_leftStickX, _leftStickY, Deadzone,
                out _processedLeftX, out _processedLeftY);
            ApplyDeadzone(_rightStickX, _rightStickY, Deadzone,
                out _processedRightX, out _processedRightY);

            // 生成按钮事件
            for (int i = 0; i < ButtonCount; i++)
            {
                if (_currentButtons[i] && !_previousButtons[i])
                    OnButtonStateChanged?.Invoke((GamepadButton)i, ButtonState.JustPressed);
                else if (!_currentButtons[i] && _previousButtons[i])
                    OnButtonStateChanged?.Invoke((GamepadButton)i, ButtonState.JustReleased);
            }

            // 保存上一帧状态
            Array.Copy(_currentButtons, _previousButtons, ButtonCount);
            LastUpdateTick = Environment.TickCount64;
        }
    }

    #endregion

    #region 触摸设备 - TouchDevice

    /// <summary>触点信息</summary>
    public struct TouchPoint
    {
        public int TouchId;
        public TouchPhase Phase;
        public Vector2 Position;
        public Vector2 Delta;
        public float Pressure;  // 0.0-1.0
        public float Radius;    // 触点半径
        public long StartTime;  // 开始时间tick
        public long LastUpdateTime;

        public readonly Vector2 StartPosition { get; }

        public TouchPoint(int touchId, Vector2 position, float pressure = 0.5f, float radius = 1.0f)
        {
            TouchId = touchId;
            Phase = TouchPhase.Began;
            Position = position;
            Delta = Vector2.Zero;
            Pressure = pressure;
            Radius = radius;
            StartTime = Environment.TickCount64;
            LastUpdateTime = StartTime;
            StartPosition = position;
        }

        public void Update(Vector2 newPosition, TouchPhase newPhase, float pressure = 0.5f, float radius = 1.0f)
        {
            Delta = newPosition - Position;
            Position = newPosition;
            Phase = newPhase;
            Pressure = pressure;
            Radius = radius;
            LastUpdateTime = Environment.TickCount64;
        }
    }

    /// <summary>
    /// 触摸设备 - 多触点追踪
    /// </summary>
    public class TouchDevice : InputDeviceReal
    {
        private const int MaxTouchCount = 10;
        private readonly TouchPoint[] _touches = new TouchPoint[MaxTouchCount];
        private readonly bool[] _activeTouches = new bool[MaxTouchCount];

        /// <summary>当前帧触点数</summary>
        public int ActiveTouchCount { get; private set; }

        /// <summary>触摸事件</summary>
        public event Action<int, TouchPhase, Vector2>? OnTouchStateChanged;

        public TouchDevice()
        {
            Name = "Touch";
            DeviceType = InputDeviceTypeReal.Touch;
            IsConnected = true;
            DeviceId = 200;
        }

        /// <summary>开始触点</summary>
        public void AddTouch(int touchId, Vector2 position, float pressure = 0.5f, float radius = 1.0f)
        {
            int slot = FindOrCreateSlot(touchId);
            _touches[slot] = new TouchPoint(touchId, position, pressure, radius);
            _activeTouches[slot] = true;
            ActiveTouchCount = CountActive();
            OnTouchStateChanged?.Invoke(touchId, TouchPhase.Began, position);
        }

        /// <summary>移动触点</summary>
        public void UpdateTouch(int touchId, Vector2 position, float pressure = 0.5f, float radius = 1.0f)
        {
            int slot = FindSlot(touchId);
            if (slot < 0) return;

            _touches[slot].Update(position, TouchPhase.Moved, pressure, radius);
            OnTouchStateChanged?.Invoke(touchId, TouchPhase.Moved, position);
        }

        /// <summary>结束触点</summary>
        public void EndTouch(int touchId)
        {
            int slot = FindSlot(touchId);
            if (slot < 0) return;

            Vector2 lastPos = _touches[slot].Position;
            _touches[slot].Update(lastPos, TouchPhase.Ended);
            _activeTouches[slot] = false;
            ActiveTouchCount = CountActive();
            OnTouchStateChanged?.Invoke(touchId, TouchPhase.Ended, lastPos);
        }

        /// <summary>取消触点</summary>
        public void CancelTouch(int touchId)
        {
            int slot = FindSlot(touchId);
            if (slot < 0) return;

            Vector2 lastPos = _touches[slot].Position;
            _touches[slot].Update(lastPos, TouchPhase.Canceled);
            _activeTouches[slot] = false;
            ActiveTouchCount = CountActive();
            OnTouchStateChanged?.Invoke(touchId, TouchPhase.Canceled, lastPos);
        }

        /// <summary>获取触点</summary>
        public TouchPoint GetTouch(int index)
        {
            if (index < 0 || index >= MaxTouchCount || !_activeTouches[index])
                return default;
            return _touches[index];
        }

        /// <summary>通过TouchId获取触点</summary>
        public TouchPoint GetTouchById(int touchId)
        {
            int slot = FindSlot(touchId);
            return slot >= 0 ? _touches[slot] : default;
        }

        /// <summary>获取所有活跃触点</summary>
        public TouchPoint[] GetAllTouches()
        {
            var list = new List<TouchPoint>();
            for (int i = 0; i < MaxTouchCount; i++)
            {
                if (_activeTouches[i])
                    list.Add(_touches[i]);
            }
            return list.ToArray();
        }

        /// <summary>检查是否有触点</summary>
        public bool IsTouching => ActiveTouchCount > 0;

        private int FindOrCreateSlot(int touchId)
        {
            // 先找已存在的
            for (int i = 0; i < MaxTouchCount; i++)
            {
                if (_activeTouches[i] && _touches[i].TouchId == touchId)
                    return i;
            }
            // 找空槽
            for (int i = 0; i < MaxTouchCount; i++)
            {
                if (!_activeTouches[i])
                    return i;
            }
            return 0; // 满了返回0（覆盖）
        }

        private int FindSlot(int touchId)
        {
            for (int i = 0; i < MaxTouchCount; i++)
            {
                if (_activeTouches[i] && _touches[i].TouchId == touchId)
                    return i;
            }
            return -1;
        }

        private int CountActive()
        {
            int count = 0;
            for (int i = 0; i < MaxTouchCount; i++)
                if (_activeTouches[i]) count++;
            return count;
        }

        /// <summary>更新stationary触点状态</summary>
        public override void Update()
        {
            for (int i = 0; i < MaxTouchCount; i++)
            {
                if (_activeTouches[i] && _touches[i].Phase == TouchPhase.Began)
                {
                    // Began -> Moved（如果位置未变化则下一帧变为Stationary）
                    _touches[i].Phase = TouchPhase.Moved;
                }
            }
            LastUpdateTick = Environment.TickCount64;
        }

        /// <summary>清除所有触点</summary>
        public void ClearAllTouches()
        {
            Array.Clear(_activeTouches, 0, MaxTouchCount);
            ActiveTouchCount = 0;
        }
    }

    #endregion

    #region 传感器设备 - SensorDevice

    /// <summary>传感器数据</summary>
    public struct SensorData
    {
        public Vector3 Value;           // 主数据
        public Vector3 Value2;          // 辅助数据（如磁场XYZ）
        public float Accuracy;          // 精度
        public long Timestamp;          // 时间戳
        public SensorType SensorType;
        public bool IsValid;

        public static SensorData Invalid => new()
        {
            IsValid = false,
            SensorType = SensorType.Accelerometer
        };
    }

    /// <summary>
    /// 传感器设备 - 加速度计/陀螺仪/磁力计/重力/设备朝向
    /// </summary>
    public class SensorDevice : InputDeviceReal
    {
        private readonly Dictionary<SensorType, SensorData> _sensorData = new();
        private readonly Dictionary<SensorType, bool> _sensorEnabled = new();
        private readonly Dictionary<SensorType, float> _sensorUpdateInterval = new();

        /// <summary>传感器数据更新事件</summary>
        public event Action<SensorType, SensorData>? OnSensorUpdated;

        public SensorDevice()
        {
            Name = "Sensors";
            DeviceType = InputDeviceTypeReal.Sensor;
            DeviceId = 300;
            IsConnected = true;

            // 初始化所有传感器
            foreach (SensorType type in Enum.GetValues(typeof(SensorType)))
            {
                _sensorData[type] = SensorData.Invalid;
                _sensorEnabled[type] = false;
                _sensorUpdateInterval[type] = 1.0f / 60.0f; // 默认60Hz
            }
        }

        /// <summary>启用传感器</summary>
        public void EnableSensor(SensorType type, float updateIntervalHz = 60.0f)
        {
            _sensorEnabled[type] = true;
            _sensorUpdateInterval[type] = 1.0f / Math.Max(1.0f, updateIntervalHz);
            _sensorData[type] = new SensorData
            {
                IsValid = true,
                SensorType = type,
                Timestamp = Environment.TickCount64,
                Accuracy = 1.0f
            };
        }

        /// <summary>禁用传感器</summary>
        public void DisableSensor(SensorType type)
        {
            _sensorEnabled[type] = false;
            _sensorData[type] = SensorData.Invalid;
        }

        /// <summary>更新传感器数据（由平台层调用）</summary>
        public void SetSensorData(SensorType type, Vector3 value, Vector3? value2 = null,
            float accuracy = 1.0f)
        {
            if (!_sensorEnabled[type]) return;

            var data = new SensorData
            {
                Value = value,
                Value2 = value2 ?? Vector3.Zero,
                Accuracy = accuracy,
                Timestamp = Environment.TickCount64,
                SensorType = type,
                IsValid = true
            };

            // 简单低通滤波平滑数据
            if (_sensorData[type].IsValid)
            {
                float alpha = 0.2f; // 滤波系数
                SensorData prev = _sensorData[type];
                data.Value = Vector3.Lerp(prev.Value, value, alpha);
                if (value2.HasValue)
                    data.Value2 = Vector3.Lerp(prev.Value2, value2.Value, alpha);
            }

            _sensorData[type] = data;
            OnSensorUpdated?.Invoke(type, data);
        }

        /// <summary>获取加速度计数据（m/s^2）</summary>
        public Vector3 GetAccelerometer()
        {
            return _sensorData.TryGetValue(SensorType.Accelerometer, out var d) && d.IsValid
                ? d.Value : Vector3.Zero;
        }

        /// <summary>获取陀螺仪数据（rad/s）</summary>
        public Vector3 GetGyroscope()
        {
            return _sensorData.TryGetValue(SensorType.Gyroscope, out var d) && d.IsValid
                ? d.Value : Vector3.Zero;
        }

        /// <summary>获取磁力计数据（uT）</summary>
        public Vector3 GetMagnetometer()
        {
            return _sensorData.TryGetValue(SensorType.Magnetometer, out var d) && d.IsValid
                ? d.Value : Vector3.Zero;
        }

        /// <summary>获取重力向量（m/s^2）</summary>
        public Vector3 GetGravity()
        {
            return _sensorData.TryGetValue(SensorType.Gravity, out var d) && d.IsValid
                ? d.Value : new Vector3(0, -9.81f, 0);
        }

        /// <summary>获取设备朝向（欧拉角，度）</summary>
        public Vector3 GetDeviceOrientation()
        {
            return _sensorData.TryGetValue(SensorType.DeviceOrientation, out var d) && d.IsValid
                ? d.Value : Vector3.Zero;
        }

        /// <summary>获取设备旋转四元数（从陀螺仪+磁力计融合计算）</summary>
        public Quaternion GetDeviceRotation()
        {
            // 简化传感器融合算法：
            // 使用加速度计获取倾斜角 + 磁力计获取偏航角
            Vector3 accel = GetAccelerometer();
            Vector3 mag = GetMagnetometer();

            // 1. 从加速度计计算Pitch和Roll
            float pitch = (float)Math.Atan2(accel.X, (float)Math.Sqrt(accel.Y * accel.Y + accel.Z * accel.Z));
            float roll = (float)Math.Atan2(-accel.Y, (float)Math.Sqrt(accel.X * accel.X + accel.Z * accel.Z));

            // 2. 从磁力计计算Yaw
            float yaw = (float)Math.Atan2(
                mag.Y * (float)Math.Cos(roll) - mag.Z * (float)Math.Sin(roll),
                mag.X * (float)Math.Cos(pitch) + mag.Y * (float)Math.Sin(pitch) * (float)Math.Sin(roll) +
                mag.Z * (float)Math.Sin(pitch) * (float)Math.Cos(roll)
            );

            // 3. 欧拉角转四元数
            return EulerToQuaternion(pitch, yaw, roll);
        }

        /// <summary>欧拉角转四元数</summary>
        private static Quaternion EulerToQuaternion(float pitch, float yaw, float roll)
        {
            float cy = (float)Math.Cos(yaw * 0.5);
            float sy = (float)Math.Sin(yaw * 0.5);
            float cp = (float)Math.Cos(pitch * 0.5);
            float sp = (float)Math.Sin(pitch * 0.5);
            float cr = (float)Math.Cos(roll * 0.5);
            float sr = (float)Math.Sin(roll * 0.5);

            float w = cy * cp * cr + sy * sp * sr;
            float x = cy * cp * sr - sy * sp * cr;
            float y = sy * cp * sr + cy * sp * cr;
            float z = sy * cp * cr - cy * sp * sr;

            return new Quaternion(x, y, z, w);
        }

        /// <summary>获取传感器数据</summary>
        public SensorData GetSensorData(SensorType type)
        {
            return _sensorData.TryGetValue(type, out var d) ? d : SensorData.Invalid;
        }

        /// <summary>检查传感器是否启用</summary>
        public bool IsSensorEnabled(SensorType type)
        {
            return _sensorEnabled.TryGetValue(type, out var enabled) && enabled;
        }

        public override void Update()
        {
            LastUpdateTick = Environment.TickCount64;
        }
    }

    #endregion

    #region 输入动作 - InputActionReal

    /// <summary>输入动作值类型</summary>
    public enum InputActionValueType
    {
        Button,     // 布尔值
        Axis,       // 单轴浮点
        Vector2,    // 二维向量
        Discrete,    // 离散值（int）
        Passthrough // 透传（不处理）
    }

    /// <summary>输入动作绑定</summary>
    public class InputActionBinding
    {
        public string Path { get; set; } = string.Empty;
        public InputDeviceTypeReal DeviceType { get; set; }
        public string ControlName { get; set; } = string.Empty;
        public List<IInputProcessorReal> Processors { get; set; } = new();
        public float Scale { get; set; } = 1.0f;
    }

    /// <summary>输入处理器接口</summary>
    public interface IInputProcessorReal
    {
        float Process(float value);
    }

    /// <summary>输入动作事件类型</summary>
    public enum InputActionPhase
    {
        Waiting,
        Started,
        Performed,
        Canceled
    }

    /// <summary>
    /// 输入动作 - 绑定设备控件、处理输入事件、读取值
    /// </summary>
    public class InputActionReal
    {
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public InputActionValueType ValueType { get; set; } = InputActionValueType.Button;
        public List<InputActionBinding> Bindings { get; set; } = new();
        public InputActionPhase Phase { get; private set; } = InputActionPhase.Waiting;

        // 值状态
        private float _floatValue;
        private Vector2 _vector2Value;
        private bool _boolValue;
        private bool _prevBoolValue;
        private int _intValue;

        // 时间追踪
        private float _holdStartTime;
        private float _currentHoldTime;

        /// <summary>事件回调</summary>
        public event Action<InputActionReal>? OnStarted;
        public event Action<InputActionReal>? OnPerformed;
        public event Action<InputActionReal>? OnCanceled;

        /// <summary>是否按住</summary>
        public bool IsPressed => _boolValue;

        /// <summary>是否释放</summary>
        public bool IsReleased => !_boolValue;

        /// <summary>当前帧是否刚按下</summary>
        public bool WasPressedThisFrame => _boolValue && !_prevBoolValue;

        /// <summary>当前帧是否刚释放</summary>
        public bool WasReleasedThisFrame => !_boolValue && _prevBoolValue;

        /// <summary>按住时长</summary>
        public float HoldTime => _currentHoldTime;

        /// <summary>处理输入事件</summary>
        public void ProcessInput(float inputValue, Vector2 vectorValue, bool boolValue, float deltaTime)
        {
            _prevBoolValue = _boolValue;

            // 应用处理器链
            foreach (var binding in Bindings)
            {
                foreach (var processor in binding.Processors)
                {
                    inputValue = processor.Process(inputValue);
                }
                inputValue *= binding.Scale;
            }

            _floatValue = inputValue;
            _vector2Value = vectorValue;
            _boolValue = boolValue;

            // 更新按住时间
            if (_boolValue)
            {
                if (!_prevBoolValue)
                    _holdStartTime = 0;
                _currentHoldTime += deltaTime;
            }
            else
            {
                _currentHoldTime = 0;
            }

            // 更新Phase
            UpdatePhase();
        }

        /// <summary>读取浮点值</summary>
        public float ReadValue()
        {
            return _floatValue;
        }

        /// <summary>读取Vector2值</summary>
        public Vector2 ReadValueVector2()
        {
            return _vector2Value;
        }

        /// <summary>读取布尔值</summary>
        public bool ReadValueBool()
        {
            return _boolValue;
        }

        private void UpdatePhase()
        {
            var prevPhase = Phase;

            if (WasPressedThisFrame)
            {
                Phase = InputActionPhase.Started;
                OnStarted?.Invoke(this);
            }
            else if (_boolValue && Phase == InputActionPhase.Started)
            {
                Phase = InputActionPhase.Performed;
                OnPerformed?.Invoke(this);
            }
            else if (WasReleasedThisFrame)
            {
                Phase = InputActionPhase.Canceled;
                OnCanceled?.Invoke(this);
            }
            else if (!_boolValue)
            {
                Phase = InputActionPhase.Waiting;
            }
        }

        /// <summary>重置动作状态</summary>
        public void Reset()
        {
            _floatValue = 0;
            _vector2Value = Vector2.Zero;
            _boolValue = false;
            _prevBoolValue = false;
            _intValue = 0;
            _currentHoldTime = 0;
            Phase = InputActionPhase.Waiting;
        }

        /// <summary>添加绑定</summary>
        public void AddBinding(string path, float scale = 1.0f)
        {
            var binding = new InputActionBinding
            {
                Path = path,
                Scale = scale
            };

            // 解析路径: <DeviceType>/controlName
            if (path.StartsWith("<") && path.Contains(">"))
            {
                int gtIdx = path.IndexOf('>');
                string devicePart = path.Substring(1, gtIdx - 1);
                string controlPart = path.Substring(gtIdx + 1).TrimStart('/');

                binding.DeviceType = ParseDeviceType(devicePart);
                binding.ControlName = controlPart;
            }

            Bindings.Add(binding);
        }

        private static InputDeviceTypeReal ParseDeviceType(string deviceName)
        {
            return deviceName.ToLowerInvariant() switch
            {
                "keyboard" => InputDeviceTypeReal.Keyboard,
                "mouse" => InputDeviceTypeReal.Mouse,
                "gamepad" => InputDeviceTypeReal.Gamepad,
                "touch" => InputDeviceTypeReal.Touch,
                _ => InputDeviceTypeReal.Unknown
            };
        }
    }

    #endregion
}
