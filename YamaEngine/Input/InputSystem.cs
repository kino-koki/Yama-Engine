using System;
using System.Collections.Generic;
using System.Numerics;

namespace YamaEngine.Input
{
    public enum InputKey
    {
        None = 0,
        Escape, Space, Enter, Backspace, Tab, CapsLock, Shift, Control, Alt,
        LeftArrow, RightArrow, UpArrow, DownArrow, Insert, Delete, Home, End, PageUp, PageDown,
        A, B, C, D, E, F, G, H, I, J, K, L, M, N, O, P, Q, R, S, T, U, V, W, X, Y, Z,
        D0, D1, D2, D3, D4, D5, D6, D7, D8, D9,
        NumPad0, NumPad1, NumPad2, NumPad3, NumPad4, NumPad5, NumPad6, NumPad7, NumPad8, NumPad9,
        NumPadAdd, NumPadSubtract, NumPadMultiply, NumPadDivide, NumPadDecimal, NumPadEnter,
        F1, F2, F3, F4, F5, F6, F7, F8, F9, F10, F11, F12,
        Comma, Period, Slash, Semicolon, Apostrophe, LeftBracket, RightBracket, Backslash, Grave,
        Minus, Equals, PrintScreen, ScrollLock, Pause,
        MouseLeft, MouseRight, MouseMiddle, MouseXButton1, MouseXButton2,
        GamepadA, GamepadB, GamepadX, GamepadY,
        GamepadLeftShoulder, GamepadRightShoulder,
        GamepadLeftTrigger, GamepadRightTrigger,
        GamepadDPadUp, GamepadDPadDown, GamepadDPadLeft, GamepadDPadRight,
        GamepadMenu, GamepadView,
        GamepadLeftStick, GamepadRightStick
    }

    public enum InputAxis
    {
        None,
        MouseX, MouseY, MouseWheel,
        MouseDeltaX, MouseDeltaY,
        GamepadLeftStickX, GamepadLeftStickY,
        GamepadRightStickX, GamepadRightStickY,
        GamepadLeftTrigger, GamepadRightTrigger,
        Touch0X, Touch0Y, Touch1X, Touch1Y,
        ScrollX, ScrollY
    }

    public enum InputAxisDirection
    {
        Positive,
        Negative
    }

    public class InputAction
    {
        public string Name;
        public string Category;
        public string Description;
        public List<(InputKey key, InputKey[] modifiers)> KeyBindings = new();
        public List<(InputAxis axis, InputAxisDirection dir, float threshold)> AxisBindings = new();
        public bool IsPressed;
        public bool WasPressed;
        public bool IsReleased;
        public float HoldTime;
        public float Value;
        public Vector2 AxisValue;
        public event Action OnPressed;
        public event Action OnReleased;
        public event Action<float> OnHeld;
        public event Action<Vector2> OnAxisChanged;

        public InputAction(string name, string category = "Default", string description = "")
        {
            Name = name;
            Category = category;
            Description = description;
        }

        public InputAction BindKey(InputKey key, params InputKey[] modifiers)
        {
            KeyBindings.Add((key, modifiers));
            return this;
        }

        public InputAction BindAxis(InputAxis axis, InputAxisDirection dir = InputAxisDirection.Positive, float threshold = 0.5f)
        {
            AxisBindings.Add((axis, dir, threshold));
            return this;
        }

        internal void UpdateState(bool pressed, float value, Vector2 axisValue, float deltaTime)
        {
            WasPressed = IsPressed;
            IsPressed = pressed;
            Value = value;
            AxisValue = axisValue;

            if (IsPressed && !WasPressed)
            {
                OnPressed?.Invoke();
                HoldTime = 0f;
            }
            else if (!IsPressed && WasPressed)
            {
                OnReleased?.Invoke();
                HoldTime = 0f;
            }

            if (IsPressed)
            {
                HoldTime += deltaTime;
                OnHeld?.Invoke(HoldTime);
            }

            if (axisValue.LengthSquared() > 0.0001f)
                OnAxisChanged?.Invoke(axisValue);
        }
    }

    public class InputContext
    {
        public string Name;
        public bool IsActive;
        public bool BlocksInput;
        public readonly Dictionary<string, InputAction> Actions = new();
        public event Action<InputAction> OnActionTriggered;

        public InputContext(string name, bool blocksInput = false)
        {
            Name = name;
            BlocksInput = blocksInput;
        }

        public InputAction RegisterAction(string name, string category = "Default", string description = "")
        {
            var action = new InputAction(name, category, description);
            Actions[name] = action;
            return action;
        }

        public InputAction GetAction(string name)
        {
            return Actions.TryGetValue(name, out var a) ? a : null;
        }

        public bool TryGetAction(string name, out InputAction action)
        {
            return Actions.TryGetValue(name, out action);
        }

        internal void Update(float deltaTime, InputSystem input)
        {
            if (!IsActive) return;

            foreach (var action in Actions.Values)
            {
                bool pressed = false;
                float value = 0f;
                Vector2 axisValue = Vector2.Zero;

                foreach (var binding in action.KeyBindings)
                {
                    if (input.IsKeyDown(binding.key))
                    {
                        bool modifiersMatch = true;
                        foreach (var mod in binding.modifiers)
                        {
                            if (!input.IsKeyDown(mod)) { modifiersMatch = false; break; }
                        }
                        if (modifiersMatch) { pressed = true; value = 1f; break; }
                    }
                }

                if (!pressed)
                {
                    foreach (var binding in action.AxisBindings)
                    {
                        float axisVal = input.GetAxis(binding.axis);
                        float signed = binding.dir == InputAxisDirection.Positive ? axisVal : -axisVal;
                        if (signed > binding.threshold)
                        {
                            pressed = true;
                            value = Math.Max(value, signed);
                        }
                    }
                }

                action.UpdateState(pressed, value, axisValue, deltaTime);
                if (action.IsPressed && !action.WasPressed)
                    OnActionTriggered?.Invoke(action);
            }
        }
    }

    public class InputMappingProfile
    {
        public string Name;
        public string DisplayName;
        public readonly Dictionary<string, List<(InputKey key, InputKey[] modifiers)>> KeyOverrides = new();
        public readonly Dictionary<string, List<(InputAxis axis, InputAxisDirection dir, float threshold)>> AxisOverrides = new();
    }

    public class InputSystem
    {
        private readonly Dictionary<InputKey, bool> _keyState = new();
        private readonly Dictionary<InputKey, bool> _keyStatePrevious = new();
        private readonly Dictionary<InputAxis, float> _axisState = new();
        private readonly Dictionary<InputAxis, float> _axisStatePrevious = new();
        private readonly List<InputContext> _contexts = new();
        private readonly Dictionary<string, InputMappingProfile> _profiles = new();
        private string _activeProfile;

        public Vector2 MousePosition;
        public Vector2 MouseDelta;
        public Vector2 MouseWheelDelta;
        public int WindowWidth;
        public int WindowHeight;
        public float MouseSensitivity = 0.002f;
        public float WheelSensitivity = 0.1f;
        public bool MouseInViewport;
        public bool IsMouseCaptured;

        public InputContext DefaultContext;
        public InputContext EditorContext;
        public InputContext GameplayContext;
        public InputContext UIContext;

        public event Action<InputKey> OnKeyPressed;
        public event Action<InputKey> OnKeyReleased;
        public event Action<Vector2> OnMouseMoved;

        public InputSystem()
        {
            DefaultContext = new InputContext("Default");
            EditorContext = new InputContext("Editor", blocksInput: false);
            GameplayContext = new InputContext("Gameplay", blocksInput: false) { IsActive = true };
            UIContext = new InputContext("UI", blocksInput: true);

            _contexts.Add(DefaultContext);
            _contexts.Add(EditorContext);
            _contexts.Add(GameplayContext);
            _contexts.Add(UIContext);

            SetupDefaultBindings();
        }

        /// <summary>初始化输入系统 (加载默认绑定与设备)。</summary>
        public void Initialize()
        {
            SetupDefaultBindings();
        }

        private void SetupDefaultBindings()
        {
            GameplayContext.RegisterAction("MoveForward", "Movement", "Move forward").BindKey(InputKey.W).BindAxis(InputAxis.GamepadLeftStickY);
            GameplayContext.RegisterAction("MoveBackward", "Movement", "Move backward").BindKey(InputKey.S).BindAxis(InputAxis.GamepadLeftStickY, InputAxisDirection.Negative);
            GameplayContext.RegisterAction("MoveLeft", "Movement", "Move left").BindKey(InputKey.A).BindAxis(InputAxis.GamepadLeftStickX, InputAxisDirection.Negative);
            GameplayContext.RegisterAction("MoveRight", "Movement", "Move right").BindKey(InputKey.D).BindAxis(InputAxis.GamepadLeftStickX);
            GameplayContext.RegisterAction("Jump", "Movement", "Jump").BindKey(InputKey.Space).BindKey(InputKey.GamepadA);
            GameplayContext.RegisterAction("Crouch", "Movement", "Crouch").BindKey(InputKey.Control).BindKey(InputKey.GamepadB);
            GameplayContext.RegisterAction("Run", "Movement", "Sprint").BindKey(InputKey.Shift).BindKey(InputKey.GamepadLeftShoulder);
            GameplayContext.RegisterAction("Interact", "Gameplay", "Interact").BindKey(InputKey.E).BindKey(InputKey.GamepadX);
            GameplayContext.RegisterAction("Use", "Gameplay", "Use item").BindKey(InputKey.F);
            GameplayContext.RegisterAction("Attack", "Combat", "Primary attack").BindKey(InputKey.MouseLeft).BindKey(InputKey.GamepadRightShoulder);
            GameplayContext.RegisterAction("AltAttack", "Combat", "Secondary attack").BindKey(InputKey.MouseRight).BindKey(InputKey.GamepadY);
            GameplayContext.RegisterAction("Reload", "Combat", "Reload weapon").BindKey(InputKey.R);
            GameplayContext.RegisterAction("Weapon1", "Weapons", "Switch to weapon 1").BindKey(InputKey.D1);
            GameplayContext.RegisterAction("Weapon2", "Weapons", "Switch to weapon 2").BindKey(InputKey.D2);
            GameplayContext.RegisterAction("Weapon3", "Weapons", "Switch to weapon 3").BindKey(InputKey.D3);
            GameplayContext.RegisterAction("LookUp", "Camera", "Look up").BindAxis(InputAxis.GamepadRightStickY);
            GameplayContext.RegisterAction("LookDown", "Camera", "Look down").BindAxis(InputAxis.GamepadRightStickY, InputAxisDirection.Negative);
            GameplayContext.RegisterAction("LookLeft", "Camera", "Turn left").BindAxis(InputAxis.GamepadRightStickX, InputAxisDirection.Negative);
            GameplayContext.RegisterAction("LookRight", "Camera", "Turn right").BindAxis(InputAxis.GamepadRightStickX);
            GameplayContext.RegisterAction("Pause", "Game", "Pause game").BindKey(InputKey.Escape).BindKey(InputKey.GamepadMenu);

            EditorContext.RegisterAction("OrbitForward", "Editor Camera", "Orbit camera forward").BindKey(InputKey.W);
            EditorContext.RegisterAction("OrbitBackward", "Editor Camera", "Orbit camera backward").BindKey(InputKey.S);
            EditorContext.RegisterAction("OrbitLeft", "Editor Camera", "Orbit camera left").BindKey(InputKey.A);
            EditorContext.RegisterAction("OrbitRight", "Editor Camera", "Orbit camera right").BindKey(InputKey.D);
            EditorContext.RegisterAction("OrbitUp", "Editor Camera", "Orbit camera up").BindKey(InputKey.E);
            EditorContext.RegisterAction("OrbitDown", "Editor Camera", "Orbit camera down").BindKey(InputKey.Q);
            EditorContext.RegisterAction("OrbitRotate", "Editor Camera", "Rotate camera (RMB)").BindKey(InputKey.MouseRight);
            EditorContext.RegisterAction("OrbitPan", "Editor Camera", "Pan camera (MMB)").BindKey(InputKey.MouseMiddle);
            EditorContext.RegisterAction("OrbitZoom", "Editor Camera", "Zoom camera (wheel)").BindAxis(InputAxis.MouseWheel);
            EditorContext.RegisterAction("FocusSelection", "Editor", "Focus selected").BindKey(InputKey.F);
            EditorContext.RegisterAction("FrameAll", "Editor", "Frame all objects").BindKey(InputKey.Home);
            EditorContext.RegisterAction("DeleteSelection", "Editor", "Delete selected").BindKey(InputKey.Delete);
            EditorContext.RegisterAction("Duplicate", "Editor", "Duplicate").BindKey(InputKey.D, InputKey.Control);
            EditorContext.RegisterAction("Undo", "Editor", "Undo").BindKey(InputKey.Z, InputKey.Control);
            EditorContext.RegisterAction("Redo", "Editor", "Redo").BindKey(InputKey.Y, InputKey.Control);
            EditorContext.RegisterAction("Save", "Editor", "Save").BindKey(InputKey.S, InputKey.Control);
            EditorContext.RegisterAction("Open", "Editor", "Open").BindKey(InputKey.O, InputKey.Control);
            EditorContext.RegisterAction("Play", "Editor", "Play/Stop").BindKey(InputKey.F5);
            EditorContext.RegisterAction("Pause", "Editor", "Pause").BindKey(InputKey.F6);
            EditorContext.RegisterAction("SelectAll", "Editor", "Select all").BindKey(InputKey.A, InputKey.Control);
            EditorContext.RegisterAction("NewObject", "Editor", "New object").BindKey(InputKey.N, InputKey.Control);
            EditorContext.RegisterAction("ToggleGrid", "Editor", "Toggle grid").BindKey(InputKey.G, InputKey.Control);
            EditorContext.RegisterAction("ToggleWireframe", "Editor", "Toggle wireframe").BindKey(InputKey.W, InputKey.Control);
            EditorContext.RegisterAction("SnapToGrid", "Editor", "Snap to grid").BindKey(InputKey.X, InputKey.Control);
            EditorContext.RegisterAction("ViewFront", "View", "Front view").BindKey(InputKey.NumPad1);
            EditorContext.RegisterAction("ViewBack", "View", "Back view").BindKey(InputKey.NumPad1, InputKey.Control);
            EditorContext.RegisterAction("ViewRight", "View", "Right view").BindKey(InputKey.NumPad3);
            EditorContext.RegisterAction("ViewLeft", "View", "Left view").BindKey(InputKey.NumPad3, InputKey.Control);
            EditorContext.RegisterAction("ViewTop", "View", "Top view").BindKey(InputKey.NumPad7);
            EditorContext.RegisterAction("ViewBottom", "View", "Bottom view").BindKey(InputKey.NumPad7, InputKey.Control);
            EditorContext.RegisterAction("ViewPerspective", "View", "Perspective").BindKey(InputKey.NumPad5);
            EditorContext.RegisterAction("ViewportSelect", "Viewport", "Select").BindKey(InputKey.MouseLeft);

            UIContext.RegisterAction("Confirm", "UI", "Confirm").BindKey(InputKey.Enter).BindKey(InputKey.Space).BindKey(InputKey.GamepadA);
            UIContext.RegisterAction("Cancel", "UI", "Cancel").BindKey(InputKey.Escape).BindKey(InputKey.GamepadB);
            UIContext.RegisterAction("NavigateUp", "UI", "Navigate up").BindKey(InputKey.UpArrow).BindAxis(InputAxis.GamepadLeftStickY);
            UIContext.RegisterAction("NavigateDown", "UI", "Navigate down").BindKey(InputKey.DownArrow).BindAxis(InputAxis.GamepadLeftStickY, InputAxisDirection.Negative);
            UIContext.RegisterAction("NavigateLeft", "UI", "Navigate left").BindKey(InputKey.LeftArrow).BindAxis(InputAxis.GamepadLeftStickX, InputAxisDirection.Negative);
            UIContext.RegisterAction("NavigateRight", "UI", "Navigate right").BindKey(InputKey.RightArrow).BindAxis(InputAxis.GamepadLeftStickX);
            UIContext.RegisterAction("TabNext", "UI", "Next tab").BindKey(InputKey.Tab);
            UIContext.RegisterAction("TabPrev", "UI", "Previous tab").BindKey(InputKey.Tab, InputKey.Shift);
        }

        public void Update(float deltaTime)
        {
            foreach (var kvp in _keyState)
            {
                _keyStatePrevious[kvp.Key] = kvp.Value;
            }
            foreach (var kvp in _axisState)
            {
                _axisStatePrevious[kvp.Key] = kvp.Value;
            }

            if (MouseDelta.LengthSquared() > 0)
                OnMouseMoved?.Invoke(MousePosition);

            foreach (var ctx in _contexts)
            {
                ctx.Update(deltaTime, this);
            }
        }

        public void SetKeyDown(InputKey key)
        {
            _keyState.TryGetValue(key, out var wasDown);
            _keyState[key] = true;
            if (!wasDown) OnKeyPressed?.Invoke(key);
        }

        public void SetKeyUp(InputKey key)
        {
            _keyState[key] = false;
            OnKeyReleased?.Invoke(key);
        }

        public bool IsKeyDown(InputKey key)
        {
            return _keyState.TryGetValue(key, out var down) && down;
        }

        public bool IsKeyPressed(InputKey key)
        {
            _keyState.TryGetValue(key, out var current);
            _keyStatePrevious.TryGetValue(key, out var previous);
            return current && !previous;
        }

        public bool IsKeyReleased(InputKey key)
        {
            _keyState.TryGetValue(key, out var current);
            _keyStatePrevious.TryGetValue(key, out var previous);
            return !current && previous;
        }

        public void SetAxis(InputAxis axis, float value)
        {
            _axisState[axis] = value;
        }

        public float GetAxis(InputAxis axis)
        {
            return _axisState.TryGetValue(axis, out var v) ? v : 0f;
        }

        public Vector2 GetMovementVector()
        {
            float x = 0, z = 0;
            if (GameplayContext.TryGetAction("MoveRight", out var right) && right.IsPressed) x += 1;
            if (GameplayContext.TryGetAction("MoveLeft", out var left) && left.IsPressed) x -= 1;
            if (GameplayContext.TryGetAction("MoveForward", out var forward) && forward.IsPressed) z += 1;
            if (GameplayContext.TryGetAction("MoveBackward", out var back) && back.IsPressed) z -= 1;
            if (x != 0 || z != 0) { float len = MathF.Sqrt(x * x + z * z); x /= len; z /= len; }
            return new Vector2(x, z);
        }

        public Vector2 GetLookVector()
        {
            return new Vector2(
                GetAxis(InputAxis.MouseDeltaX) * MouseSensitivity,
                GetAxis(InputAxis.MouseDeltaY) * MouseSensitivity
            );
        }

        public bool IsActionPressed(string contextName, string actionName)
        {
            foreach (var ctx in _contexts)
            {
                if (ctx.Name == contextName && ctx.IsActive && ctx.TryGetAction(actionName, out var a) && a.IsPressed)
                    return true;
            }
            return false;
        }

        public List<InputContext> GetAllContexts() => new(_contexts);

        public List<string> GetAllActionNames()
        {
            var names = new List<string>();
            foreach (var ctx in _contexts)
                foreach (var name in ctx.Actions.Keys)
                    names.Add($"{ctx.Name}/{name}");
            return names;
        }

        public void LoadProfile(InputMappingProfile profile)
        {
            _profiles[profile.Name] = profile;
            _activeProfile = profile.Name;
            foreach (var ctx in _contexts)
            {
                foreach (var action in ctx.Actions.Values)
                {
                    string key = $"{ctx.Name}/{action.Name}";
                    if (profile.KeyOverrides.TryGetValue(key, out var keys))
                    {
                        action.KeyBindings.Clear();
                        foreach (var kb in keys) action.KeyBindings.Add(kb);
                    }
                    if (profile.AxisOverrides.TryGetValue(key, out var axes))
                    {
                        action.AxisBindings.Clear();
                        foreach (var ab in axes) action.AxisBindings.Add(ab);
                    }
                }
            }
        }

        public InputMappingProfile GetCurrentProfile()
        {
            return string.IsNullOrEmpty(_activeProfile) ? null : _profiles[_activeProfile];
        }

        public void ClearAllInput()
        {
            var keys = new List<InputKey>(_keyState.Keys);
            foreach (var key in keys) _keyState[key] = false;
            var axes = new List<InputAxis>(_axisState.Keys);
            foreach (var axis in axes) _axisState[axis] = 0f;
        }

        public void SetViewportFocus(bool hasFocus)
        {
            MouseInViewport = hasFocus;
            EditorContext.IsActive = hasFocus;
        }

        public void UpdateMousePosition(float x, float y, float dx, float dy, int width, int height)
        {
            MousePosition = new Vector2(x, y);
            MouseDelta = new Vector2(dx, dy);
            WindowWidth = width;
            WindowHeight = height;
            SetAxis(InputAxis.MouseX, x);
            SetAxis(InputAxis.MouseY, y);
            SetAxis(InputAxis.MouseDeltaX, dx);
            SetAxis(InputAxis.MouseDeltaY, dy);
        }

        public void UpdateMouseWheel(float delta)
        {
            MouseWheelDelta = new Vector2(0, delta);
            SetAxis(InputAxis.MouseWheel, delta * WheelSensitivity);
            SetAxis(InputAxis.ScrollY, delta);
        }

        public string GetKeyDisplayName(InputKey key)
        {
            return key switch
            {
                InputKey.Escape => "Esc",
                InputKey.Space => "Space",
                InputKey.Enter => "Enter",
                InputKey.Backspace => "Backspace",
                InputKey.Tab => "Tab",
                InputKey.Shift => "Shift",
                InputKey.Control => "Ctrl",
                InputKey.Alt => "Alt",
                InputKey.LeftArrow => "←",
                InputKey.RightArrow => "→",
                InputKey.UpArrow => "↑",
                InputKey.DownArrow => "↓",
                InputKey.Insert => "Ins",
                InputKey.Delete => "Del",
                InputKey.MouseLeft => "LMB",
                InputKey.MouseRight => "RMB",
                InputKey.MouseMiddle => "MMB",
                InputKey.GamepadA => "GAMEPAD A",
                InputKey.GamepadB => "GAMEPAD B",
                InputKey.GamepadX => "GAMEPAD X",
                InputKey.GamepadY => "GAMEPAD Y",
                _ => key.ToString().Replace("D0", "0").Replace("D1", "1").Replace("D2", "2").Replace("D3", "3").Replace("D4", "4").Replace("D5", "5").Replace("D6", "6").Replace("D7", "7").Replace("D8", "8").Replace("D9", "9")
            };
        }

        /// <summary>释放输入系统资源。</summary>
        public void Dispose()
        {
            // InputSystem 持有纯托管状态，无原生资源需要释放。
        }
    }
}
