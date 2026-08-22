using System;
using System.Collections.Generic;
using System.Numerics;

namespace ShanyuEngine.Input.Enhanced
{
    #region 设备类型枚举

    /// <summary>
    /// 输入设备类型
    /// </summary>
    public enum InputDeviceType
    {
        /// <summary>未知</summary>
        Unknown,
        /// <summary>键盘</summary>
        Keyboard,
        /// <summary>鼠标</summary>
        Mouse,
        /// <summary>游戏手柄</summary>
        Gamepad,
        /// <summary>触摸屏</summary>
        Touch,
        /// <summary>空间设备 (VR/AR 控制器)</summary>
        Spatial,
        /// <summary>Switch Pro 手柄</summary>
        SwitchPro,
        /// <summary>DualSense (PS5 手柄)</summary>
        DualSense,
        /// <summary>DualShock (PS4 手柄)</summary>
        DualShock,
        /// <summary>Xbox 手柄</summary>
        XboxController,
        /// <summary>方向盘</summary>
        SteeringWheel,
        /// <summary>飞行摇杆</summary>
        FlightStick,
        /// <summary>通用 HID 设备</summary>
        GenericHID
    }

    /// <summary>
    /// 设备连接状态
    /// </summary>
    public enum DeviceConnectionState
    {
        /// <summary>已断开</summary>
        Disconnected,
        /// <summary>已连接</summary>
        Connected,
        /// <summary>正在连接中</summary>
        Connecting,
        /// <summary>失去焦点 (系统层面，如窗口失焦)</summary>
        LostFocus
    }

    #endregion

    #region InputDevice 基类

    /// <summary>
    /// 输入设备基类 —— 所有输入设备的抽象基类
    /// 提供设备标识、连接状态、能力描述等公共接口
    /// </summary>
    public abstract class InputDevice
    {
        /// <summary>设备名称</summary>
        public string Name { get; set; }

        /// <summary>设备类型</summary>
        public InputDeviceType DeviceType { get; set; } = InputDeviceType.Unknown;

        /// <summary>设备唯一标识 (GUID 或系统分配的 ID)</summary>
        public string DeviceId { get; set; }

        /// <summary>供应商/制造商</summary>
        public string Vendor { get; set; }

        /// <summary>产品名称</summary>
        public string ProductName { get; set; }

        /// <summary>设备能力标志</summary>
        public InputDeviceCapabilities Capabilities { get; set; } = new InputDeviceCapabilities();

        /// <summary>连接状态</summary>
        public DeviceConnectionState ConnectionState { get; private set; } = DeviceConnectionState.Disconnected;

        /// <summary>是否已连接</summary>
        public bool IsConnected => ConnectionState == DeviceConnectionState.Connected;

        /// <summary>是否活跃 (最近有输入)</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public bool IsActive { get; protected set; }

        /// <summary>上次活跃时间</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public float LastActiveTime { get; protected set; }

        // ========== 事件 ==========
        /// <summary>设备已连接</summary>
        public event Action<InputDevice> OnConnected;
        /// <summary>设备已断开</summary>
        public event Action<InputDevice> OnDisconnected;
        /// <summary>设备获取焦点</summary>
        public event Action<InputDevice> OnFocusGained;
        /// <summary>设备失去焦点</summary>
        public event Action<InputDevice> OnFocusLost;

        // ========== 使用统计 ==========
        /// <summary>总输入事件数</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public long TotalInputEvents { get; protected set; }
        /// <summary>使用时长 (秒)</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public float TotalUsageTime { get; protected set; }

        protected InputDevice(string name, InputDeviceType type, string deviceId = null)
        {
            Name = name;
            DeviceType = type;
            DeviceId = deviceId ?? Guid.NewGuid().ToString("N").Substring(0, 8);
        }

        /// <summary>标记设备为已连接</summary>
        internal void SetConnected()
        {
            var oldState = ConnectionState;
            ConnectionState = DeviceConnectionState.Connected;
            if (oldState != DeviceConnectionState.Connected)
                OnConnected?.Invoke(this);
        }

        /// <summary>标记设备为已断开</summary>
        internal void SetDisconnected()
        {
            var oldState = ConnectionState;
            ConnectionState = DeviceConnectionState.Disconnected;
            if (oldState == DeviceConnectionState.Connected)
                OnDisconnected?.Invoke(this);
        }

        /// <summary>设备获取焦点</summary>
        internal void GainFocus()
        {
            ConnectionState = DeviceConnectionState.Connected;
            OnFocusGained?.Invoke(this);
        }

        /// <summary>设备失去焦点</summary>
        internal void LoseFocus()
        {
            ConnectionState = DeviceConnectionState.LostFocus;
            OnFocusLost?.Invoke(this);
        }

        /// <summary>每帧更新 (子类可重写)</summary>
        public virtual void Update(float deltaTime)
        {
            if (IsConnected && IsActive)
                TotalUsageTime += deltaTime;
        }

        /// <summary>记录输入事件</summary>
        protected void RecordInput()
        {
            TotalInputEvents++;
            IsActive = true;
        }

        /// <summary>重置设备状态</summary>
        public virtual void Reset()
        {
            TotalInputEvents = 0;
            TotalUsageTime = 0f;
            IsActive = false;
            LastActiveTime = 0f;
        }

        /// <summary>获取设备信息字符串</summary>
        public override string ToString() => $"[{DeviceType}] {Name} ({DeviceId}) - {ConnectionState}";
    }

    #endregion

    #region InputDeviceCapabilities —— 设备能力

    /// <summary>
    /// 输入设备能力描述
    /// </summary>
    public class InputDeviceCapabilities
    {
        /// <summary>是否有按钮</summary>
        public bool HasButtons { get; set; }
        /// <summary>是否有模拟轴</summary>
        public bool HasAxes { get; set; }
        /// <summary>是否有摇杆 (2D)</summary>
        public bool HasSticks { get; set; }
        /// <summary>是否有扳机</summary>
        public bool HasTriggers { get; set; }
        /// <summary>是否有振动 (Rumble)</summary>
        public bool HasRumble { get; set; }
        /// <summary>是否有触摸板</summary>
        public bool HasTouchpad { get; set; }
        /// <summary>是否有陀螺仪</summary>
        public bool HasGyroscope { get; set; }
        /// <summary>是否有加速计</summary>
        public bool HasAccelerometer { get; set; }
        /// <summary>是否有 LED/灯光</summary>
        public bool HasLED { get; set; }
        /// <summary>是否有指针 (鼠标/触控笔)</summary>
        public bool HasPointer { get; set; }
        /// <summary>是否有文本输入</summary>
        public bool HasTextInput { get; set; }
        /// <summary>是否有力反馈 (高级)</summary>
        public bool HasForceFeedback { get; set; }
        /// <summary>是否有空间追踪 (VR/AR)</summary>
        public bool HasSpatialTracking { get; set; }
        /// <summary>是否有自适应扳机 (DualSense)</summary>
        public bool HasAdaptiveTriggers { get; set; }
        /// <summary>是否有 HD Rumble (Switch Pro)</summary>
        public bool HasHDRumble { get; set; }
        /// <summary>按钮数量</summary>
        public int ButtonCount { get; set; }
        /// <summary>轴数量</summary>
        public int AxisCount { get; set; }
    }

    #endregion

    #region KeyboardDevice —— 键盘设备

    /// <summary>
    /// 键盘设备 —— 追踪键盘状态、修饰键和文本输入
    /// </summary>
    public class KeyboardDevice : InputDevice
    {
        /// <summary>按键状态表</summary>
        public Dictionary<string, bool> KeyStates { get; } = new Dictionary<string, bool>();

        /// <summary>上一帧按键状态</summary>
        public Dictionary<string, bool> PreviousKeyStates { get; } = new Dictionary<string, bool>();

        /// <summary>修饰键状态</summary>
        public bool CtrlPressed { get; private set; }
        public bool ShiftPressed { get; private set; }
        public bool AltPressed { get; private set; }
        public bool SuperPressed { get; private set; }

        /// <summary>文本输入缓冲 (当前帧输入的字符)</summary>
        public string TextInputBuffer { get; private set; } = "";

        /// <summary>当前按下的键列表</summary>
        public List<string> PressedKeys { get; } = new List<string>();

        /// <summary>本帧刚按下的键</summary>
        public List<string> JustPressedKeys { get; } = new List<string>();

        /// <summary>本帧刚释放的键</summary>
        public List<string> JustReleasedKeys { get; } = new List<string>();

        public KeyboardDevice() : base("Keyboard", InputDeviceType.Keyboard)
        {
            Capabilities.HasButtons = true;
            Capabilities.HasTextInput = true;
        }

        /// <summary>设置按键按下</summary>
        public void SetKeyDown(string key)
        {
            KeyStates.TryGetValue(key, out bool wasDown);
            KeyStates[key] = true;
            UpdateModifiers();

            if (!wasDown)
                JustPressedKeys.Add(key);

            if (!PressedKeys.Contains(key))
                PressedKeys.Add(key);

            RecordInput();
        }

        /// <summary>设置按键释放</summary>
        public void SetKeyUp(string key)
        {
            KeyStates[key] = false;
            UpdateModifiers();
            PressedKeys.Remove(key);
            JustReleasedKeys.Add(key);
        }

        /// <summary>添加文本输入</summary>
        public void AddTextInput(char c)
        {
            TextInputBuffer += c;
            RecordInput();
        }

        private void UpdateModifiers()
        {
            CtrlPressed = GetKeyState("LeftCtrl") || GetKeyState("RightCtrl");
            ShiftPressed = GetKeyState("LeftShift") || GetKeyState("RightShift");
            AltPressed = GetKeyState("LeftAlt") || GetKeyState("RightAlt");
            SuperPressed = GetKeyState("LeftSuper") || GetKeyState("RightSuper");
        }

        /// <summary>获取键状态</summary>
        public bool GetKeyState(string key) => KeyStates.TryGetValue(key, out var state) && state;

        /// <summary>是否刚按下</summary>
        public bool WasJustPressed(string key)
        {
            return KeyStates.TryGetValue(key, out var current) && current &&
                   PreviousKeyStates.TryGetValue(key, out var prev) && !prev;
        }

        /// <summary>是否刚释放</summary>
        public bool WasJustReleased(string key)
        {
            return PreviousKeyStates.TryGetValue(key, out var prev) && prev &&
                   KeyStates.TryGetValue(key, out var current) && !current;
        }

        public override void Update(float deltaTime)
        {
            base.Update(deltaTime);

            // 更新上一帧状态
            PreviousKeyStates.Clear();
            foreach (var kvp in KeyStates)
                PreviousKeyStates[kvp.Key] = kvp.Value;

            JustPressedKeys.Clear();
            JustReleasedKeys.Clear();
            TextInputBuffer = "";
        }

        /// <summary>清除所有按键</summary>
        public void ClearAllKeys()
        {
            KeyStates.Clear();
            PressedKeys.Clear();
            JustPressedKeys.Clear();
            JustReleasedKeys.Clear();
            TextInputBuffer = "";
            CtrlPressed = ShiftPressed = AltPressed = SuperPressed = false;
        }
    }

    #endregion

    #region MouseDevice —— 鼠标设备

    /// <summary>
    /// 鼠标设备 —— 位置、滚轮、按键、指针锁定
    /// </summary>
    public class MouseDevice : InputDevice
    {
        /// <summary>鼠标位置 (屏幕坐标)</summary>
        public Vector2 Position { get; set; }

        /// <summary>鼠标增量 (本帧移动量)</summary>
        public Vector2 Delta { get; set; }

        /// <summary>滚轮值</summary>
        public float ScrollWheel { get; set; }

        /// <summary>水平滚轮</summary>
        public float ScrollWheelHorizontal { get; set; }

        /// <summary>按键状态</summary>
        public Dictionary<int, bool> ButtonStates { get; } = new Dictionary<int, bool>();

        /// <summary>按钮点击次数 (用于双击/三击检测)</summary>
        public Dictionary<int, int> ButtonClickCount { get; } = new Dictionary<int, int>();

        /// <summary>指针是否被锁定</summary>
        public bool IsPointerLocked { get; private set; }

        /// <summary>指针锁定位置 (锁定时的虚拟位置)</summary>
        public Vector2 LockPosition { get; private set; }

        public MouseDevice() : base("Mouse", InputDeviceType.Mouse)
        {
            Capabilities.HasButtons = true;
            Capabilities.HasPointer = true;
            Capabilities.HasAxes = true;
            Capabilities.ButtonCount = 5;
        }

        /// <summary>更新位置</summary>
        public void SetPosition(float x, float y)
        {
            if (IsPointerLocked)
            {
                Delta = new Vector2(x - LockPosition.X, y - LockPosition.Y);
            }
            else
            {
                Delta = new Vector2(x - Position.X, y - Position.Y);
            }
            Position = new Vector2(x, y);
            RecordInput();
        }

        /// <summary>设置按钮状态</summary>
        public void SetButton(int button, bool pressed)
        {
            ButtonStates[button] = pressed;
            if (pressed)
            {
                ButtonClickCount.TryGetValue(button, out int count);
                ButtonClickCount[button] = count + 1;
                RecordInput();
            }
        }

        /// <summary>设置滚轮</summary>
        public void SetScrollWheel(float vertical, float horizontal = 0f)
        {
            ScrollWheel = vertical;
            ScrollWheelHorizontal = horizontal;
            RecordInput();
        }

        /// <summary>锁定指针</summary>
        public void LockPointer()
        {
            IsPointerLocked = true;
            LockPosition = Position;
        }

        /// <summary>解锁指针</summary>
        public void UnlockPointer()
        {
            IsPointerLocked = false;
        }

        /// <summary>获取按钮状态</summary>
        public bool GetButton(int button) => ButtonStates.TryGetValue(button, out var s) && s;

        public override void Update(float deltaTime)
        {
            base.Update(deltaTime);
            // 滚轮值衰减
            ScrollWheel *= 0.9f;
            ScrollWheelHorizontal *= 0.9f;
        }
    }

    #endregion

    #region GamepadDevice —— 游戏手柄设备

    /// <summary>
    /// 游戏手柄设备 —— 左摇杆、右摇杆、扳机、按钮、十字键、触摸板、陀螺仪、振动
    /// 支持 Xbox/PlayStation/Switch Pro 通用接口
    /// </summary>
    public class GamepadDevice : InputDevice
    {
        /// <summary>左摇杆值 [-1, 1]</summary>
        public Vector2 LeftStick { get; set; } = Vector2.Zero;

        /// <summary>右摇杆值 [-1, 1]</summary>
        public Vector2 RightStick { get; set; } = Vector2.Zero;

        /// <summary>左扳机值 [0, 1]</summary>
        public float LeftTrigger { get; set; }

        /// <summary>右扳机值 [0, 1]</summary>
        public float RightTrigger { get; set; }

        /// <summary>按钮状态</summary>
        public Dictionary<string, bool> Buttons { get; } = new Dictionary<string, bool>();

        /// <summary>按钮值 (模拟按钮如扳机)</summary>
        public Dictionary<string, float> ButtonValues { get; } = new Dictionary<string, float>();

        /// <summary>十字键值</summary>
        public Vector2 DPad { get; set; } = Vector2.Zero;

        // 常用按钮名称
        public const string ButtonA = "ButtonSouth";
        public const string ButtonB = "ButtonEast";
        public const string ButtonX = "ButtonWest";
        public const string ButtonY = "ButtonNorth";
        public const string ButtonLB = "LeftShoulder";
        public const string ButtonRB = "RightShoulder";
        public const string ButtonBack = "Select";
        public const string ButtonStart = "Start";
        public const string ButtonHome = "Home";
        public const string ButtonLS = "LeftStick";
        public const string ButtonRS = "RightStick";

        /// <summary>左摇杆死区 (自动应用)</summary>
        public float LeftStickDeadzone { get; set; } = 0.15f;
        /// <summary>右摇杆死区</summary>
        public float RightStickDeadzone { get; set; } = 0.15f;

        // 振动
        /// <summary>左马达振动强度 [0, 1]</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public float LeftRumble { get; set; }
        /// <summary>右马达振动强度 [0, 1]</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public float RightRumble { get; set; }
        /// <summary>振动持续时间 (秒, 0 = 持续)</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public float RumbleDuration { get; set; }
        [System.Text.Json.Serialization.JsonIgnore]
        private float _rumbleTimer;

        // 触摸板
        /// <summary>是否有触摸板</summary>
        public bool HasTouchpad { get; set; }
        /// <summary>触摸板是否被触摸</summary>
        public bool TouchpadTouched { get; set; }
        /// <summary>触摸板位置</summary>
        public Vector2 TouchpadPosition { get; set; }

        // 陀螺仪
        /// <summary>陀螺仪旋转速率 (弧度/秒)</summary>
        public Vector3 GyroscopeRate { get; set; }
        /// <summary>加速计 (重力加速度)</summary>
        public Vector3 Accelerometer { get; set; }
        /// <summary>陀螺仪是否启用</summary>
        public bool GyroscopeEnabled { get; set; }

        public GamepadDevice() : base("Gamepad", InputDeviceType.Gamepad)
        {
            Capabilities.HasButtons = true;
            Capabilities.HasSticks = true;
            Capabilities.HasTriggers = true;
            Capabilities.HasRumble = true;
            Capabilities.HasAxes = true;
            Capabilities.ButtonCount = 17;
            Capabilities.AxisCount = 8;
        }

        /// <summary>设置左摇杆值 (带死区)</summary>
        public void SetLeftStick(float x, float y)
        {
            LeftStick = ApplyDeadzone(x, y, LeftStickDeadzone);
            RecordInput();
        }

        /// <summary>设置右摇杆值 (带死区)</summary>
        public void SetRightStick(float x, float y)
        {
            RightStick = ApplyDeadzone(x, y, RightStickDeadzone);
            RecordInput();
        }

        /// <summary>设置按钮状态</summary>
        public void SetButton(string button, bool pressed)
        {
            Buttons[button] = pressed;
            ButtonValues[button] = pressed ? 1f : 0f;
            RecordInput();
        }

        /// <summary>设置按钮值 (模拟)</summary>
        public void SetButtonValue(string button, float value)
        {
            ButtonValues[button] = Math.Clamp(value, 0f, 1f);
            Buttons[button] = value > 0.5f;
        }

        /// <summary>设置扳机值</summary>
        public void SetTriggers(float left, float right)
        {
            LeftTrigger = Math.Clamp(left, 0f, 1f);
            RightTrigger = Math.Clamp(right, 0f, 1f);
            RecordInput();
        }

        /// <summary>设置 DPad 值</summary>
        public void SetDPad(float x, float y)
        {
            DPad = new Vector2(
                Math.Clamp(x, -1f, 1f),
                Math.Clamp(y, -1f, 1f)
            );
            RecordInput();
        }

        /// <summary>触发振动</summary>
        public void SetRumble(float left, float right, float duration = 0f)
        {
            LeftRumble = Math.Clamp(left, 0f, 1f);
            RightRumble = Math.Clamp(right, 0f, 1f);
            RumbleDuration = duration;
            _rumbleTimer = 0f;
        }

        /// <summary>停止振动</summary>
        public void StopRumble()
        {
            LeftRumble = 0f;
            RightRumble = 0f;
            RumbleDuration = 0f;
        }

        /// <summary>获取按钮状态</summary>
        public bool GetButton(string button) => Buttons.TryGetValue(button, out var s) && s;

        public override void Update(float deltaTime)
        {
            base.Update(deltaTime);

            // 振动计时
            if (RumbleDuration > 0f)
            {
                _rumbleTimer += deltaTime;
                if (_rumbleTimer >= RumbleDuration)
                {
                    StopRumble();
                }
            }
        }

        /// <summary>重置所有输入</summary>
        public override void Reset()
        {
            base.Reset();
            LeftStick = Vector2.Zero;
            RightStick = Vector2.Zero;
            LeftTrigger = 0f;
            RightTrigger = 0f;
            DPad = Vector2.Zero;
            Buttons.Clear();
            ButtonValues.Clear();
            GyroscopeRate = Vector3.Zero;
            Accelerometer = Vector3.Zero;
        }

        private Vector2 ApplyDeadzone(float x, float y, float deadzone)
        {
            var value = new Vector2(x, y);
            float mag = value.Length();
            if (mag <= deadzone) return Vector2.Zero;
            float scale = (mag - deadzone) / (1f - deadzone);
            return Vector2.Normalize(value) * Math.Min(scale, 1f);
        }
    }

    #endregion

    #region TouchDevice —— 触摸设备

    /// <summary>
    /// 触摸设备 —— 多点触控支持 (最多 10 指)
    /// </summary>
    public class TouchDevice : InputDevice
    {
        /// <summary>最大支持触摸点数</summary>
        public const int MaxTouches = 10;

        /// <summary>触摸点信息</summary>
        public class TouchPoint
        {
            /// <summary>触摸 ID</summary>
            public int Id;
            /// <summary>位置 (屏幕坐标)</summary>
            public Vector2 Position;
            /// <summary>上一帧位置</summary>
            public Vector2 PreviousPosition;
            /// <summary>增量 (本帧移动量)</summary>
            public Vector2 Delta;
            /// <summary>压力 [0, 1]</summary>
            public float Pressure;
            /// <summary>触摸半径</summary>
            public float Radius;
            /// <summary>相位 (开始/移动/结束)</summary>
            public TouchPhase Phase;
            /// <summary>开始时间</summary>
            public float StartTime;
            /// <summary>持续时间</summary>
            public float Duration;
        }

        /// <summary>触摸相位</summary>
        public enum TouchPhase
        {
            None,
            Began,
            Moved,
            Ended,
            Canceled,
            Stationary
        }

        /// <summary>当前触摸点列表</summary>
        public Dictionary<int, TouchPoint> ActiveTouches { get; } = new Dictionary<int, TouchPoint>();

        /// <summary>本帧新增的触摸</summary>
        public List<TouchPoint> StartedTouches { get; } = new List<TouchPoint>();

        /// <summary>本帧结束的触摸</summary>
        public List<TouchPoint> EndedTouches { get; } = new List<TouchPoint>();

        public TouchDevice() : base("Touch", InputDeviceType.Touch)
        {
            Capabilities.HasPointer = true;
            Capabilities.HasButtons = true;
        }

        /// <summary>添加触摸开始</summary>
        public void AddTouchStart(int id, float x, float y, float pressure = 1f, float radius = 0f, float time = 0f)
        {
            var touch = new TouchPoint
            {
                Id = id,
                Position = new Vector2(x, y),
                PreviousPosition = new Vector2(x, y),
                Pressure = pressure,
                Radius = radius,
                Phase = TouchPhase.Began,
                StartTime = time,
                Duration = 0f
            };
            ActiveTouches[id] = touch;
            StartedTouches.Add(touch);
            RecordInput();
        }

        /// <summary>更新触摸位置</summary>
        public void UpdateTouch(int id, float x, float y, float pressure = 1f, float radius = 0f)
        {
            if (ActiveTouches.TryGetValue(id, out var touch))
            {
                touch.PreviousPosition = touch.Position;
                touch.Position = new Vector2(x, y);
                touch.Delta = touch.Position - touch.PreviousPosition;
                touch.Pressure = pressure;
                touch.Radius = radius;
                touch.Phase = touch.Delta.LengthSquared() > 0.0001f ? TouchPhase.Moved : TouchPhase.Stationary;
                RecordInput();
            }
        }

        /// <summary>触摸结束</summary>
        public void EndTouch(int id)
        {
            if (ActiveTouches.TryGetValue(id, out var touch))
            {
                touch.Phase = TouchPhase.Ended;
                EndedTouches.Add(touch);
                ActiveTouches.Remove(id);
                RecordInput();
            }
        }

        /// <summary>取消所有触摸</summary>
        public void CancelAllTouches()
        {
            foreach (var touch in ActiveTouches.Values)
            {
                touch.Phase = TouchPhase.Canceled;
                EndedTouches.Add(touch);
            }
            ActiveTouches.Clear();
        }

        /// <summary>获取触摸点</summary>
        public TouchPoint GetTouch(int id) =>
            ActiveTouches.TryGetValue(id, out var t) ? t : null;

        /// <summary>获取第一个触摸点</summary>
        public TouchPoint PrimaryTouch =>
            ActiveTouches.Count > 0 ? ActiveTouches.Values.GetEnumerator().Current : null;

        public override void Update(float deltaTime)
        {
            base.Update(deltaTime);
            StartedTouches.Clear();
            EndedTouches.Clear();
            foreach (var touch in ActiveTouches.Values)
            {
                touch.Duration += deltaTime;
            }
        }
    }

    #endregion

    #region SpatialDevice —— 空间设备 (VR/AR)

    /// <summary>
    /// 空间设备 —— VR/AR 控制器/头显
    /// 支持手柄姿态、手指追踪、按钮、震动
    /// </summary>
    public class SpatialDevice : InputDevice
    {
        /// <summary>手柄角色</summary>
        public enum HandRole
        {
            /// <summary>左手</summary>
            Left,
            /// <summary>右手</summary>
            Right,
            /// <summary>头显</summary>
            Head,
            /// <summary>未知</summary>
            Unknown
        }

        /// <summary>手柄角色</summary>
        public HandRole Role { get; set; } = HandRole.Unknown;

        /// <summary>设备位置 (世界坐标)</summary>
        public Vector3 Position { get; set; }

        /// <summary>设备旋转 (四元数)</summary>
        public Quaternion Rotation { get; set; } = Quaternion.Identity;

        /// <summary>设备速度</summary>
        public Vector3 Velocity { get; set; }

        /// <summary>设备角速度</summary>
        public Vector3 AngularVelocity { get; set; }

        /// <summary>手指弯曲值 [0, 1] (5 根手指)</summary>
        public float[] FingerCurls { get; } = new float[5]; // Thumb, Index, Middle, Ring, Pinky

        /// <summary>手指是否触碰 (5 根手指)</summary>
        public bool[] FingerTouches { get; } = new bool[5];

        /// <summary>按钮状态</summary>
        public Dictionary<string, bool> Buttons { get; } = new Dictionary<string, bool>();

        /// <summary>触发器值 (食指扳机) [0, 1]</summary>
        public float TriggerValue { get; set; }

        /// <summary>握把值 [0, 1]</summary>
        public float GripValue { get; set; }

        /// <summary>主摇杆/触摸板值</summary>
        public Vector2 Thumbstick { get; set; }

        public SpatialDevice(HandRole role = HandRole.Unknown) : base("Spatial", InputDeviceType.Spatial)
        {
            Role = role;
            Name = role switch
            {
                HandRole.Left => "Left Controller",
                HandRole.Right => "Right Controller",
                HandRole.Head => "Headset",
                _ => "Spatial Device"
            };
            Capabilities.HasButtons = true;
            Capabilities.HasTriggers = true;
            Capabilities.HasSticks = true;
            Capabilities.HasSpatialTracking = true;
            Capabilities.HasRumble = true;
        }

        /// <summary>设置姿态</summary>
        public void SetPose(Vector3 position, Quaternion rotation, Vector3 velocity, Vector3 angularVelocity)
        {
            Position = position;
            Rotation = rotation;
            Velocity = velocity;
            AngularVelocity = angularVelocity;
            RecordInput();
        }

        /// <summary>设置手指弯曲</summary>
        public void SetFingerCurl(int finger, float curl)
        {
            if (finger >= 0 && finger < 5)
                FingerCurls[finger] = Math.Clamp(curl, 0f, 1f);
        }

        public override void Reset()
        {
            base.Reset();
            Position = Vector3.Zero;
            Rotation = Quaternion.Identity;
            Velocity = Vector3.Zero;
            AngularVelocity = Vector3.Zero;
            TriggerValue = 0f;
            GripValue = 0f;
            Thumbstick = Vector2.Zero;
            Array.Clear(FingerCurls);
            Array.Clear(FingerTouches);
        }
    }

    #endregion

    #region SwitchProDevice —— Switch Pro 手柄

    /// <summary>
    /// Switch Pro 手柄 —— 支持 HD Rumble 和特定布局
    /// </summary>
    public class SwitchProDevice : GamepadDevice
    {
        /// <summary>HD Rumble 强度 (高级振动模式)</summary>
        public float HDRumbleIntensity { get; set; }

        /// <summary>HD Rumble 频率</summary>
        public float HDRumbleFrequency { get; set; } = 160f;

        public SwitchProDevice() : base()
        {
            DeviceType = InputDeviceType.SwitchPro;
            Name = "Switch Pro Controller";
            Capabilities.HasHDRumble = true;
        }

        /// <summary>设置 HD Rumble</summary>
        public void SetHDRumble(float intensity, float frequency)
        {
            HDRumbleIntensity = Math.Clamp(intensity, 0f, 1f);
            HDRumbleFrequency = frequency;
        }
    }

    #endregion

    #region DualSenseDevice —— DualSense 手柄 (PS5)

    /// <summary>
    /// DualSense 手柄 —— 支持 HD Rumble、自适应扳机和触摸板
    /// </summary>
    public class DualSenseDevice : GamepadDevice
    {
        /// <summary>左自适应扳机效果</summary>
        public TriggerEffect LeftTriggerEffect { get; set; }
        /// <summary>右自适应扳机效果</summary>
        public TriggerEffect RightTriggerEffect { get; set; }

        /// <summary>触摸板 (双指支持)</summary>
        public Vector2 Touchpad1Position { get; set; }
        public bool Touchpad1Touched { get; set; }
        public Vector2 Touchpad2Position { get; set; }
        public bool Touchpad2Touched { get; set; }

        /// <summary>LED 颜色</summary>
        public Vector3 LEDColor { get; set; } = new Vector3(0f, 0f, 1f); // 默认蓝色

        /// <summary>麦克风状态</summary>
        public bool MicrophoneMuted { get; set; } = true;

        public DualSenseDevice() : base()
        {
            DeviceType = InputDeviceType.DualSense;
            Name = "DualSense Controller";
            Capabilities.HasAdaptiveTriggers = true;
            Capabilities.HasTouchpad = true;
            Capabilities.HasLED = true;
            HasTouchpad = true;
        }

        /// <summary>设置自适应扳机效果</summary>
        public void SetTriggerEffect(int side, TriggerEffect effect)
        {
            if (side == 0) LeftTriggerEffect = effect;
            else RightTriggerEffect = effect;
        }

        /// <summary>清除扳机效果</summary>
        public void ClearTriggerEffects()
        {
            LeftTriggerEffect = null;
            RightTriggerEffect = null;
        }

        /// <summary>设置 LED 颜色</summary>
        public void SetLED(float r, float g, float b)
        {
            LEDColor = new Vector3(
                Math.Clamp(r, 0f, 1f),
                Math.Clamp(g, 0f, 1f),
                Math.Clamp(b, 0f, 1f)
            );
        }
    }

    /// <summary>
    /// 自适应扳机效果
    /// </summary>
    public class TriggerEffect
    {
        /// <summary>效果类型</summary>
        public enum EffectType
        {
            /// <summary>无效果</summary>
            None,
            /// <summary>恒定阻力</summary>
            Constant,
            /// <summary>分段 (如枪械上膛感)</summary>
            Section,
            /// <summary>震动反馈</summary>
            Vibration,
            /// <summary>武器后坐力</summary>
            Weapon
        }

        /// <summary>效果类型</summary>
        public EffectType Type { get; set; } = EffectType.None;
        /// <summary>起始位置 [0, 1]</summary>
        public float StartPosition { get; set; }
        /// <summary>力度 [0, 1]</summary>
        public float Strength { get; set; } = 0.5f;
        /// <summary>分段数量</summary>
        public int SectionCount { get; set; } = 1;
        /// <summary>分段力度列表</summary>
        public float[] SectionStrengths { get; set; }
    }

    #endregion

    #region InputDeviceManager —— 设备管理器

    /// <summary>
    /// 输入设备管理器 —— 管理所有输入设备的注册、热插拔、更新
    /// </summary>
    public class InputDeviceManager
    {
        private static InputDeviceManager _instance;
        /// <summary>全局单例</summary>
        public static InputDeviceManager Instance
        {
            get
            {
                if (_instance == null) _instance = new InputDeviceManager();
                return _instance;
            }
        }

        /// <summary>已注册的设备列表</summary>
        private readonly List<InputDevice> _devices = new List<InputDevice>();

        /// <summary>按类型分组的设备</summary>
        private readonly Dictionary<InputDeviceType, List<InputDevice>> _devicesByType = new Dictionary<InputDeviceType, List<InputDevice>>();

        /// <summary>默认设备</summary>
        public KeyboardDevice Keyboard { get; private set; }
        public MouseDevice Mouse { get; private set; }
        public GamepadDevice PrimaryGamepad { get; private set; }
        public TouchDevice Touch { get; private set; }

        // ========== 事件 ==========
        /// <summary>设备连接</summary>
        public event Action<InputDevice> OnDeviceConnected;
        /// <summary>设备断开</summary>
        public event Action<InputDevice> OnDeviceDisconnected;

        public InputDeviceManager()
        {
            // 创建默认设备
            Keyboard = new KeyboardDevice();
            Mouse = new MouseDevice();
            Touch = new TouchDevice();

            RegisterDevice(Keyboard);
            RegisterDevice(Mouse);
            RegisterDevice(Touch);
        }

        /// <summary>注册设备</summary>
        public void RegisterDevice(InputDevice device)
        {
            if (_devices.Contains(device)) return;

            _devices.Add(device);

            if (!_devicesByType.ContainsKey(device.DeviceType))
                _devicesByType[device.DeviceType] = new List<InputDevice>();
            _devicesByType[device.DeviceType].Add(device);

            device.OnConnected += d => OnDeviceConnected?.Invoke(d);
            device.OnDisconnected += d => OnDeviceDisconnected?.Invoke(d);

            // 标记为已连接
            device.SetConnected();

            // 自动设置默认设备
            if (device is GamepadDevice gp && PrimaryGamepad == null)
                PrimaryGamepad = gp;
        }

        /// <summary>取消注册设备</summary>
        public void UnregisterDevice(InputDevice device)
        {
            if (_devices.Remove(device))
            {
                device.SetDisconnected();

                if (_devicesByType.TryGetValue(device.DeviceType, out var list))
                    list.Remove(device);

                if (device == PrimaryGamepad)
                {
                    PrimaryGamepad = _devicesByType.TryGetValue(InputDeviceType.Gamepad, out var gamepads)
                        ? gamepads.Count > 0 ? gamepads[0] : null : null;
                }
            }
        }

        /// <summary>获取所有设备</summary>
        public IReadOnlyList<InputDevice> GetAllDevices() => _devices.AsReadOnly();

        /// <summary>按类型获取设备</summary>
        public IReadOnlyList<InputDevice> GetDevicesByType(InputDeviceType type)
        {
            if (_devicesByType.TryGetValue(type, out var list))
                return list.AsReadOnly();
            return Array.Empty<InputDevice>().ToList().AsReadOnly();
        }

        /// <summary>更新所有设备</summary>
        public void Update(float deltaTime)
        {
            foreach (var device in _devices)
            {
                if (device.IsConnected)
                    device.Update(deltaTime);
            }
        }

        /// <summary>按 ID 查找设备</summary>
        public InputDevice FindDevice(string deviceId)
        {
            foreach (var device in _devices)
            {
                if (device.DeviceId == deviceId) return device;
            }
            return null;
        }

        /// <summary>按名称查找设备</summary>
        public InputDevice FindDeviceByName(string name)
        {
            foreach (var device in _devices)
            {
                if (device.Name == name) return device;
            }
            return null;
        }

        /// <summary>获取设备统计</summary>
        public int ConnectedDeviceCount => _devices.Count(d => d.IsConnected);

        /// <summary>窗口获得焦点</summary>
        public void OnWindowFocusGained()
        {
            foreach (var device in _devices)
                if (device.IsConnected)
                    device.GainFocus();
        }

        /// <summary>窗口失去焦点</summary>
        public void OnWindowFocusLost()
        {
            foreach (var device in _devices)
                if (device.IsConnected)
                    device.LoseFocus();
        }

        /// <summary>清除所有设备输入</summary>
        public void ClearAllInput()
        {
            foreach (var device in _devices)
                device.Reset();
        }
    }

    #endregion
}
