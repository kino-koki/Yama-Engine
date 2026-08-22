using System;
using System.Collections.Generic;
using System.Numerics;

namespace ShanyuEngine.Input.Enhanced
{
    #region BrushType 枚举

    /// <summary>
    /// 笔刷类型 —— 定义输入笔刷的操作模式
    /// </summary>
    public enum BrushType
    {
        /// <summary>绘制/上色</summary>
        Paint,
        /// <summary>擦除</summary>
        Erase,
        /// <summary>平滑</summary>
        Smooth,
        /// <summary>压平</summary>
        Flatten,
        /// <summary>抓取/移动</summary>
        Grab,
        /// <summary>缩放</summary>
        Scale,
        /// <summary>旋转</summary>
        Rotate,
        /// <summary>地形笔刷</summary>
        Terrain,
        /// <summary>蒙版</summary>
        Mask,
        /// <summary>选择</summary>
        Select,
        /// <summary>雕刻</summary>
        Sculpt,
        /// <summary>填充</summary>
        Fill
    }

    #endregion

    #region InputBrush —— 输入笔刷

    /// <summary>
    /// 输入笔刷 —— 定义输入如何驱动编辑器/工具操作
    /// 用于编辑器的地形绘制、模型雕刻、粒子放置等场景
    /// </summary>
    public class InputBrush
    {
        /// <summary>笔刷名称</summary>
        public string Name { get; set; }

        /// <summary>笔刷类型</summary>
        public BrushType Type { get; set; } = BrushType.Paint;

        /// <summary>是否启用</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>笔刷力度 [0, 1]</summary>
        public float Strength { get; set; } = 0.5f;

        /// <summary>笔刷半径 (世界单位)</summary>
        public float Radius { get; set; } = 1.0f;

        /// <summary>笔刷衰减曲线</summary>
        public BrushFalloff Falloff { get; set; } = BrushFalloff.Smooth;

        /// <summary>输入模式 (连续/脉冲)</summary>
        public BrushInputMode InputMode { get; set; } = BrushInputMode.Continuous;

        /// <summary>是否支持压力感应</summary>
        public bool PressureSensitive { get; set; } = true;

        /// <summary>压力映射 (如何将输入压力转换为笔刷力度)</summary>
        public float PressureScale { get; set; } = 1.0f;

        /// <summary>是否镜像</summary>
        public bool Mirrored { get; set; } = false;

        /// <summary>流量 (连续输入时的每秒应用量)</summary>
        public float Flow { get; set; } = 1.0f;

        /// <summary>间距 (连续输入时每次应用的间隔)</summary>
        public float Spacing { get; set; } = 0.1f;

        // ========== 运行时状态 ==========
        [System.Text.Json.Serialization.JsonIgnore]
        private float _flowAccumulator;
        [System.Text.Json.Serialization.JsonIgnore]
        private Vector3 _lastApplyPosition;
        [System.Text.Json.Serialization.JsonIgnore]
        private bool _isActive;
        [System.Text.Json.Serialization.JsonIgnore]
        private float _currentPressure;

        /// <summary>当前有效力度 (考虑压力感应后的值)</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public float EffectiveStrength => Strength * (PressureSensitive ? _currentPressure : 1f) * PressureScale;

        /// <summary>笔刷是否正在应用</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public bool IsActive => _isActive;

        /// <summary>当前压力值</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public float CurrentPressure => _currentPressure;

        // ========== 事件 ==========
        /// <summary>笔刷开始应用</summary>
        public event Action<InputBrush, Vector3> OnBrushStart;
        /// <summary>笔刷持续应用</summary>
        public event Action<InputBrush, Vector3, float> OnBrushStroke;
        /// <summary>笔刷结束应用</summary>
        public event Action<InputBrush> OnBrushEnd;

        public InputBrush(string name, BrushType type = BrushType.Paint)
        {
            Name = name;
            Type = type;
        }

        /// <summary>开始笔刷应用</summary>
        public void Begin(Vector3 worldPosition, float pressure = 1f)
        {
            if (!Enabled) return;
            _isActive = true;
            _currentPressure = Math.Clamp(pressure, 0f, 1f);
            _flowAccumulator = 0f;
            _lastApplyPosition = worldPosition;
            OnBrushStart?.Invoke(this, worldPosition);
        }

        /// <summary>更新笔刷应用 (每帧调用)</summary>
        public void UpdateStroke(Vector3 worldPosition, float pressure, float deltaTime)
        {
            if (!_isActive || !Enabled) return;

            _currentPressure = Math.Clamp(pressure, 0f, 1f);

            switch (InputMode)
            {
                case BrushInputMode.Continuous:
                    UpdateContinuous(worldPosition, deltaTime);
                    break;

                case BrushInputMode.Pulse:
                    // 脉冲模式每帧都触发
                    OnBrushStroke?.Invoke(this, worldPosition, EffectiveStrength);
                    break;

                case BrushInputMode.Distance:
                    UpdateDistance(worldPosition);
                    break;
            }

            _lastApplyPosition = worldPosition;
        }

        /// <summary>结束笔刷应用</summary>
        public void End()
        {
            _isActive = false;
            _currentPressure = 0f;
            _flowAccumulator = 0f;
            OnBrushEnd?.Invoke(this);
        }

        private void UpdateContinuous(Vector3 position, float deltaTime)
        {
            _flowAccumulator += Flow * deltaTime;
            if (_flowAccumulator >= 1f)
            {
                _flowAccumulator -= 1f;
                OnBrushStroke?.Invoke(this, position, EffectiveStrength);

                // 镜像
                if (Mirrored)
                {
                    var mirroredPos = new Vector3(-position.X, position.Y, position.Z);
                    OnBrushStroke?.Invoke(this, mirroredPos, EffectiveStrength);
                }
            }
        }

        private void UpdateDistance(Vector3 position)
        {
            float distance = Vector3.Distance(position, _lastApplyPosition);
            if (distance >= Spacing)
            {
                OnBrushStroke?.Invoke(this, position, EffectiveStrength);
            }
        }

        /// <summary>重置笔刷状态</summary>
        public void Reset()
        {
            _isActive = false;
            _currentPressure = 0f;
            _flowAccumulator = 0f;
        }

        /// <summary>计算衰减值 (0~1, 根据距离笔刷中心的距离)</summary>
        public float CalculateFalloff(float distance)
        {
            if (distance >= Radius) return 0f;
            float normalized = distance / Radius;

            switch (Falloff)
            {
                case BrushFalloff.Linear:
                    return 1f - normalized;

                case BrushFalloff.Smooth:
                    return (1f - normalized) * (1f - normalized) * (3f - 2f * normalized);

                case BrushFalloff.Sphere:
                    return MathF.Sqrt(Math.Max(0f, 1f - normalized * normalized));

                case BrushFalloff.Tip:
                    return 1f - normalized * normalized;

                case BrushFalloff.Bell:
                    float t = normalized * 2f - 1f;
                    return MathF.Exp(-t * t * 2f);

                case BrushFalloff.Constant:
                    return normalized < 1f ? 1f : 0f;

                default:
                    return 1f - normalized;
            }
        }
    }

    #endregion

    #region BrushFalloff 枚举

    /// <summary>
    /// 笔刷衰减曲线类型
    /// </summary>
    public enum BrushFalloff
    {
        /// <summary>线性衰减</summary>
        Linear,
        /// <summary>平滑衰减 (Hermite)</summary>
        Smooth,
        /// <summary>球形衰减</summary>
        Sphere,
        /// <summary>尖端衰减 (二次方)</summary>
        Tip,
        /// <summary>钟形衰减 (高斯)</summary>
        Bell,
        /// <summary>恒定 (无衰减)</summary>
        Constant
    }

    #endregion

    #region BrushInputMode 枚举

    /// <summary>
    /// 笔刷输入模式
    /// </summary>
    public enum BrushInputMode
    {
        /// <summary>连续模式 (按住持续应用)</summary>
        Continuous,
        /// <summary>脉冲模式 (每帧应用一次)</summary>
        Pulse,
        /// <summary>距离模式 (移动一定距离后应用)</summary>
        Distance
    }

    #endregion

    #region InputPenBrush —— 输入笔 (支持压感)

    /// <summary>
    /// 输入笔 —— 扩展 InputBrush，支持压感笔/画板的特定输入
    /// </summary>
    public class InputPenBrush : InputBrush
    {
        /// <summary>倾斜角度 (X/Y)</summary>
        public Vector2 Tilt { get; set; }

        /// <summary>旋转角度 (度)</summary>
        public float Twist { get; set; }

        /// <summary>笔类型</summary>
        public enum PenType
        {
            /// <summary>圆笔</summary>
            Round,
            /// <summary>平笔 (方形)</summary>
            Flat,
            /// <summary>喷枪</summary>
            Airbrush,
            /// <summary>纹理笔</summary>
            Texture
        }

        /// <summary>笔形状</summary>
        public PenType Shape { get; set; } = PenType.Round;

        /// <summary>笔刷不透明度 [0, 1]</summary>
        public float Opacity { get; set; } = 1.0f;

        /// <summary>笔刷硬度 (边缘锐利度) [0, 1]</summary>
        public float Hardness { get; set; } = 0.5f;

        /// <summary>喷枪扩散范围</summary>
        public float AirbrushSpread { get; set; } = 0.3f;

        public InputPenBrush(string name, PenType shape = PenType.Round) : base(name, BrushType.Paint)
        {
            Shape = shape;
            PressureSensitive = true;
        }

        /// <summary>更新倾斜信息</summary>
        public void SetTilt(float x, float y)
        {
            Tilt = new Vector2(
                Math.Clamp(x, -1f, 1f),
                Math.Clamp(y, -1f, 1f)
            );
        }
    }

    #endregion

    #region InputGestureSystem —— 输入手势识别

    /// <summary>
    /// 输入手势识别器 —— 识别多点触控手势
    /// 支持双指缩放、三指旋转、滑动方向等
    /// </summary>
    public class InputGestureSystem
    {
        /// <summary>手势类型</summary>
        public enum GestureType
        {
            /// <summary>无手势</summary>
            None,
            /// <summary>双指缩放</summary>
            Pinch,
            /// <summary>双指平移</summary>
            Pan,
            /// <summary>三指旋转</summary>
            Rotate,
            /// <summary>滑动 (四方向)</summary>
            Swipe,
            /// <summary>长按</summary>
            LongPress,
            /// <summary>拖拽</summary>
            Drag,
            /// <summary>自定义手势</summary>
            Custom
        }

        /// <summary>滑动方向</summary>
        public enum SwipeDirection
        {
            /// <summary>未检测到</summary>
            None,
            /// <summary>上</summary>
            Up,
            /// <summary>下</summary>
            Down,
            /// <summary>左</summary>
            Left,
            /// <summary>右</summary>
            Right
        }

        /// <summary>手势信息</summary>
        public struct GestureInfo
        {
            /// <summary>手势类型</summary>
            public GestureType Type;
            /// <summary>手势值 (缩放倍率/旋转角度/滑动距离等)</summary>
            public float Value;
            /// <summary>手势方向</summary>
            public SwipeDirection Direction;
            /// <summary>手势中心位置</summary>
            public Vector2 Center;
            /// <summary>参与的手指数量</summary>
            public int FingerCount;
            /// <summary>是否刚开始</summary>
            public bool IsStarting;
            /// <summary>是否已结束</summary>
            public bool IsEnding;
            /// <summary>持续时间</summary>
            public float Duration;
        }

        // ========== 配置 ==========
        /// <summary>缩放灵敏度</summary>
        public float PinchSensitivity { get; set; } = 1f;
        /// <summary>旋转灵敏度 (度)</summary>
        public float RotateSensitivity { get; set; } = 1f;
        /// <summary>滑动的最小距离 (像素)</summary>
        public float SwipeMinDistance { get; set; } = 50f;
        /// <summary>滑动的最大持续时间 (秒)</summary>
        public float SwipeMaxDuration { get; set; } = 0.5f;
        /// <summary>长按的最短时间 (秒)</summary>
        public float LongPressMinDuration { get; set; } = 0.5f;
        /// <summary>长按的最大移动距离 (像素)</summary>
        public float LongPressMaxMovement { get; set; } = 10f;

        // ========== 事件 ==========
        /// <summary>手势开始</summary>
        public event Action<GestureInfo> OnGestureStarted;
        /// <summary>手势进行中</summary>
        public event Action<GestureInfo> OnGestureChanged;
        /// <summary>手势结束</summary>
        public event Action<GestureInfo> OnGestureEnded;

        // ========== 运行时状态 ==========
        private Vector2 _initialPinchDistance;
        private float _initialPinchAngle;
        private Vector2 _swipeStartPosition;
        private float _swipeStartTime;
        private Vector2 _longPressPosition;
        private float _longPressStartTime;
        private bool _longPressActive;
        private bool _pinchActive;
        private bool _rotateActive;
        private bool _swipeActive;
        private bool _panActive;

        /// <summary>当前手势</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public GestureType CurrentGesture { get; private set; } = GestureType.None;

        public InputGestureSystem() { }

        /// <summary>处理触摸输入 (由 TouchDevice 驱动)</summary>
        public void ProcessTouches(Dictionary<int, TouchDevice.TouchPoint> touches, float currentTime)
        {
            if (touches.Count == 0)
            {
                EndAllGestures();
                return;
            }

            if (touches.Count == 2)
            {
                ProcessTwoFingerGesture(touches, currentTime);
            }
            else if (touches.Count == 3)
            {
                ProcessThreeFingerGesture(touches, currentTime);
            }
            else if (touches.Count == 1)
            {
                ProcessSingleFingerGesture(touches, currentTime);
            }
        }

        private void ProcessTwoFingerGesture(Dictionary<int, TouchDevice.TouchPoint> touches, float currentTime)
        {
            var pts = new List<TouchDevice.TouchPoint>(touches.Values);

            if (pts.Count < 2) return;

            Vector2 center = (pts[0].Position + pts[1].Position) * 0.5f;
            float distance = Vector2.Distance(pts[0].Position, pts[1].Position);
            float angle = MathF.Atan2(pts[1].Position.Y - pts[0].Position.Y, pts[1].Position.X - pts[0].Position.X);

            // 检测缩放
            float pinchDelta = 0f;
            if (!_pinchActive)
            {
                _initialPinchDistance = new Vector2(distance, 0f);
                _pinchActive = true;
                EmitGestureStarted(GestureType.Pinch, 1f, SwipeDirection.None, center, 2);
            }
            else
            {
                pinchDelta = distance / _initialPinchDistance.X * PinchSensitivity;
                EmitGestureChanged(GestureType.Pinch, pinchDelta, SwipeDirection.None, center, 2);
            }

            // 检测旋转 (如果手指在移动)
            float angleDelta = 0f;
            if (!_rotateActive && pts[0].Phase == TouchDevice.TouchPhase.Moved)
            {
                _initialPinchAngle = angle;
                _rotateActive = true;
            }
            else if (_rotateActive)
            {
                angleDelta = (angle - _initialPinchAngle) * (180f / MathF.PI) * RotateSensitivity;
                EmitGestureChanged(GestureType.Rotate, angleDelta, SwipeDirection.None, center, 3);
            }
        }

        private void ProcessThreeFingerGesture(Dictionary<int, TouchDevice.TouchPoint> touches, float currentTime)
        {
            var pts = new List<TouchDevice.TouchPoint>(touches.Values);
            if (pts.Count < 3) return;

            Vector2 center = Vector2.Zero;
            foreach (var pt in pts) center += pt.Position;
            center /= pts.Count;

            // 三指旋转
            float angle = MathF.Atan2(pts[1].Position.Y - pts[0].Position.Y, pts[1].Position.X - pts[0].Position.X);

            if (!_rotateActive)
            {
                _initialPinchAngle = angle;
                _rotateActive = true;
                EmitGestureStarted(GestureType.Rotate, 0f, SwipeDirection.None, center, 3);
            }
            else
            {
                float angleDelta = (angle - _initialPinchAngle) * (180f / MathF.PI) * RotateSensitivity;
                EmitGestureChanged(GestureType.Rotate, angleDelta, SwipeDirection.None, center, 3);
            }
        }

        private void ProcessSingleFingerGesture(Dictionary<int, TouchDevice.TouchPoint> touches, float currentTime)
        {
            var pts = new List<TouchDevice.TouchPoint>(touches.Values);
            if (pts.Count != 1) return;

            var touch = pts[0];

            // 滑动检测
            if (touch.Phase == TouchDevice.TouchPhase.Began)
            {
                _swipeStartPosition = touch.Position;
                _swipeStartTime = currentTime;
                _swipeActive = true;
            }
            else if (touch.Phase == TouchDevice.TouchPhase.Moved && _swipeActive)
            {
                Vector2 delta = touch.Position - _swipeStartPosition;
                float dist = delta.Length();

                if (dist > SwipeMinDistance)
                {
                    SwipeDirection dir = DetectSwipeDirection(delta);
                    float speed = dist / Math.Max(0.001f, currentTime - _swipeStartTime);

                    EmitGestureStarted(GestureType.Swipe, dist, dir, touch.Position, 1);

                    if (touch.Phase == TouchDevice.TouchPhase.Ended || currentTime - _swipeStartTime > SwipeMaxDuration)
                    {
                        EmitGestureEnded(GestureType.Swipe, dist, dir, touch.Position, 1, currentTime - _swipeStartTime);
                        _swipeActive = false;
                    }
                }
            }
            else if (touch.Phase == TouchDevice.TouchPhase.Ended && _swipeActive)
            {
                _swipeActive = false;
            }

            // 长按检测
            if (touch.Phase == TouchDevice.TouchPhase.Began)
            {
                _longPressPosition = touch.Position;
                _longPressStartTime = currentTime;
                _longPressActive = true;
            }
            else if (_longPressActive && touch.Phase == TouchDevice.TouchPhase.Stationary)
            {
                float duration = currentTime - _longPressStartTime;
                float dist = Vector2.Distance(touch.Position, _longPressPosition);

                if (duration >= LongPressMinDuration && dist < LongPressMaxMovement)
                {
                    EmitGestureStarted(GestureType.LongPress, duration, SwipeDirection.None, touch.Position, 1);
                }
            }
        }

        private SwipeDirection DetectSwipeDirection(Vector2 delta)
        {
            if (Math.Abs(delta.X) > Math.Abs(delta.Y))
                return delta.X > 0 ? SwipeDirection.Right : SwipeDirection.Left;
            else
                return delta.Y > 0 ? SwipeDirection.Down : SwipeDirection.Up;
        }

        private void EmitGestureStarted(GestureType type, float value, SwipeDirection dir, Vector2 center, int fingers)
        {
            CurrentGesture = type;
            OnGestureStarted?.Invoke(new GestureInfo
            {
                Type = type, Value = value, Direction = dir,
                Center = center, FingerCount = fingers, IsStarting = true
            });
        }

        private void EmitGestureChanged(GestureType type, float value, SwipeDirection dir, Vector2 center, int fingers)
        {
            CurrentGesture = type;
            OnGestureChanged?.Invoke(new GestureInfo
            {
                Type = type, Value = value, Direction = dir,
                Center = center, FingerCount = fingers
            });
        }

        private void EmitGestureEnded(GestureType type, float value, SwipeDirection dir, Vector2 center, int fingers, float duration)
        {
            CurrentGesture = GestureType.None;
            OnGestureEnded?.Invoke(new GestureInfo
            {
                Type = type, Value = value, Direction = dir,
                Center = center, FingerCount = fingers, IsEnding = true, Duration = duration
            });
        }

        private void EndAllGestures()
        {
            _pinchActive = false;
            _rotateActive = false;
            _swipeActive = false;
            _panActive = false;
            _longPressActive = false;
            CurrentGesture = GestureType.None;
        }

        /// <summary>重置手势状态</summary>
        public void Reset()
        {
            EndAllGestures();
        }
    }

    #endregion

    #region BrushManager —— 笔刷管理器

    /// <summary>
    /// 笔刷管理器 —— 管理多个笔刷实例的切换和使用
    /// </summary>
    public class BrushManager
    {
        /// <summary>笔刷集合</summary>
        public Dictionary<string, InputBrush> Brushes { get; } = new Dictionary<string, InputBrush>();

        /// <summary>当前活动笔刷</summary>
        public InputBrush ActiveBrush { get; private set; }

        /// <summary>笔刷切换事件</summary>
        public event Action<InputBrush, InputBrush> OnBrushChanged;

        /// <summary>手势识别器</summary>
        public InputGestureSystem GestureSystem { get; } = new InputGestureSystem();

        public BrushManager() { }

        /// <summary>添加笔刷</summary>
        public InputBrush AddBrush(string name, BrushType type = BrushType.Paint)
        {
            var brush = new InputBrush(name, type);
            Brushes[name] = brush;
            if (ActiveBrush == null) ActiveBrush = brush;
            return brush;
        }

        /// <summary>移除笔刷</summary>
        public bool RemoveBrush(string name)
        {
            if (Brushes.Remove(name))
            {
                if (ActiveBrush?.Name == name)
                    ActiveBrush = Brushes.Count > 0 ? Brushes.Values.GetEnumerator().Current : null;
                return true;
            }
            return false;
        }

        /// <summary>切换活动笔刷</summary>
        public void SetActiveBrush(string name)
        {
            if (Brushes.TryGetValue(name, out var brush))
            {
                var oldBrush = ActiveBrush;
                ActiveBrush = brush;
                OnBrushChanged?.Invoke(oldBrush, brush);
            }
        }

        /// <summary>更新当前笔刷</summary>
        public void UpdateActiveBrush(Vector3 worldPosition, float pressure, float deltaTime)
        {
            ActiveBrush?.UpdateStroke(worldPosition, pressure, deltaTime);
        }

        /// <summary>开始使用当前笔刷</summary>
        public void BeginActiveBrush(Vector3 worldPosition, float pressure = 1f)
        {
            ActiveBrush?.Begin(worldPosition, pressure);
        }

        /// <summary>结束使用当前笔刷</summary>
        public void EndActiveBrush()
        {
            ActiveBrush?.End();
        }

        /// <summary>获取笔刷名称列表</summary>
        public IEnumerable<string> GetBrushNames() => Brushes.Keys;
    }

    #endregion
}
