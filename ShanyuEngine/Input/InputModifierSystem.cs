using System;
using System.Collections.Generic;
using System.Numerics;

namespace ShanyuEngine.Input.Enhanced
{
    #region InputModifier 基类

    /// <summary>
    /// 输入修改器基类 —— 在绑定值传递给动作之前对值进行变换
    /// 修改器管线：绑定原始值 → 修改器1 → 修改器2 → ... → 最终值
    /// 类似 UE 的 Input Modifier (Hold, Tap, Dead Zone, Scale, Invert, Swizzle...)
    /// </summary>
    public abstract class InputModifier
    {
        /// <summary>修改器名称 (用于序列化和调试)</summary>
        public string Name { get; set; }

        /// <summary>是否启用</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>处理 float 值</summary>
        public abstract float ProcessValue(float value);

        /// <summary>处理 Vector2 值 (可选重写)</summary>
        public virtual Vector2 ProcessValue(Vector2 value)
        {
            return new Vector2(ProcessValue(value.X), ProcessValue(value.Y));
        }

        /// <summary>处理 Vector3 值 (可选重写)</summary>
        public virtual Vector3 ProcessValue(Vector3 value)
        {
            return new Vector3(ProcessValue(value.X), ProcessValue(value.Y), ProcessValue(value.Z));
        }

        protected InputModifier(string name = "Modifier")
        {
            Name = name;
        }
    }

    #endregion

    #region HoldModifier —— 按住超过阈值才触发

    /// <summary>
    /// 按住修改器 —— 只有按住时间超过 HoldTime 阈值时，值才通过
    /// 按住不足阈值时返回 0
    /// </summary>
    public class HoldModifier : InputModifier
    {
        /// <summary>需要按住的持续时间 (秒)</summary>
        public float HoldTime { get; set; } = 0.2f;

        /// <summary>当前按住时间 (由外部更新)</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public float CurrentHoldTime { get; set; }

        /// <summary>是否等待中（尚未达到阈值）</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public bool IsWaiting => CurrentHoldTime < HoldTime;

        public HoldModifier(float holdTime = 0.2f) : base("Hold")
        {
            HoldTime = holdTime;
        }

        public override float ProcessValue(float value)
        {
            if (!Enabled) return value;
            return CurrentHoldTime >= HoldTime ? value : 0f;
        }

        public override Vector2 ProcessValue(Vector2 value)
        {
            if (!Enabled) return value;
            return CurrentHoldTime >= HoldTime ? value : Vector2.Zero;
        }

        public override Vector3 ProcessValue(Vector3 value)
        {
            if (!Enabled) return value;
            return CurrentHoldTime >= HoldTime ? value : Vector3.Zero;
        }
    }

    #endregion

    #region TapModifier —— 短按触发

    /// <summary>
    /// 点击修改器 —— 只有短按时值才通过，长按则取消
    /// 按住超过 MaxDuration 时值变为 0
    /// </summary>
    public class TapModifier : InputModifier
    {
        /// <summary>最大按住时间 (秒)，超过则视为长按</summary>
        public float MaxDuration { get; set; } = 0.3f;

        /// <summary>当前按住时间 (由外部更新)</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public float CurrentHoldTime { get; set; }

        /// <summary>是否已超时 (长按)</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public bool IsTimedOut => CurrentHoldTime > MaxDuration;

        public TapModifier(float maxDuration = 0.3f) : base("Tap")
        {
            MaxDuration = maxDuration;
        }

        public override float ProcessValue(float value)
        {
            if (!Enabled) return value;
            return CurrentHoldTime <= MaxDuration ? value : 0f;
        }
    }

    #endregion

    #region DoubleTapModifier —— 双击检测

    /// <summary>
    /// 双击修改器 —— 只有在短时间内连续两次点击时值才通过
    /// </summary>
    public class DoubleTapModifier : InputModifier
    {
        /// <summary>两次点击之间的最大间隔 (秒)</summary>
        public float MaxInterval { get; set; } = 0.4f;

        /// <summary>第一次点击时间</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public float FirstTapTime { get; set; } = -1f;

        /// <summary>是否已完成双击</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public bool DoubleTapCompleted { get; set; }

        /// <summary>当前时间 (由外部更新)</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public float CurrentTime { get; set; }

        /// <summary>是否收到过至少一次点击</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public bool HasFirstTap => FirstTapTime >= 0f;

        public DoubleTapModifier(float maxInterval = 0.4f) : base("DoubleTap")
        {
            MaxInterval = maxInterval;
        }

        /// <summary>记录一次点击</summary>
        public void RecordTap(float currentTime)
        {
            CurrentTime = currentTime;

            if (FirstTapTime < 0f)
            {
                // 第一次点击
                FirstTapTime = currentTime;
                DoubleTapCompleted = false;
            }
            else if (currentTime - FirstTapTime <= MaxInterval)
            {
                // 在间隔内第二次点击 → 双击完成
                DoubleTapCompleted = true;
                FirstTapTime = -1f;
            }
            else
            {
                // 间隔超时，重新开始
                FirstTapTime = currentTime;
                DoubleTapCompleted = false;
            }
        }

        /// <summary>重置双击状态</summary>
        public void Reset()
        {
            FirstTapTime = -1f;
            DoubleTapCompleted = false;
        }

        public override float ProcessValue(float value)
        {
            if (!Enabled) return value;
            return DoubleTapCompleted ? value : 0f;
        }
    }

    #endregion

    #region PressModifier —— 长按延迟触发

    /// <summary>
    /// 长按修改器 —— 按住超过 PressTime 后才产生非零值
    /// 在 PressTime 之前值保持为 0，之后值正常通过
    /// </summary>
    public class PressModifier : InputModifier
    {
        /// <summary>需要按住的持续时间 (秒) 才触发</summary>
        public float PressTime { get; set; } = 0.5f;

        /// <summary>当前按住时间</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public float CurrentHoldTime { get; set; }

        /// <summary>是否已满足长按条件</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public bool IsPressed => CurrentHoldTime >= PressTime;

        /// <summary>长按触发瞬间 (刚满足条件的那一帧)</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public bool JustPressed { get; set; }

        public PressModifier(float pressTime = 0.5f) : base("Press")
        {
            PressTime = pressTime;
        }

        public override float ProcessValue(float value)
        {
            if (!Enabled) return value;
            JustPressed = false;
            if (CurrentHoldTime >= PressTime)
            {
                // 检测是否刚好在这一帧满足
                if (CurrentHoldTime - PressTime < 0.02f)
                    JustPressed = true;
                return value;
            }
            return 0f;
        }

        public override Vector2 ProcessValue(Vector2 value)
        {
            if (!Enabled) return value;
            return CurrentHoldTime >= PressTime ? value : Vector2.Zero;
        }

        public override Vector3 ProcessValue(Vector3 value)
        {
            if (!Enabled) return value;
            return CurrentHoldTime >= PressTime ? value : Vector3.Zero;
        }
    }

    #endregion

    #region DeadZoneModifier —— 死区

    /// <summary>
    /// 死区类型
    /// </summary>
    public enum DeadZoneType
    {
        /// <summary>内死区：值小于最小阈值时归零</summary>
        Inner,
        /// <summary>外死区：值大于最大阈值时裁剪</summary>
        Outer,
        /// <summary>内外死区：低于最小归零，高于最大裁剪</summary>
        InnerAndOuter
    }

    /// <summary>
    /// 死区修改器 —— 过滤摇杆/扳机的微小偏移
    /// 支持内死区、外死区以及死区内的曲线映射
    /// </summary>
    public class DeadZoneModifier : InputModifier
    {
        /// <summary>死区类型</summary>
        public DeadZoneType Type { get; set; } = DeadZoneType.Inner;

        /// <summary>最小值 (内死区半径/下限)</summary>
        public float Min { get; set; } = 0.1f;

        /// <summary>最大值 (外死区/上限)</summary>
        public float Max { get; set; } = 1.0f;

        /// <summary>是否对 Vector2 使用径向死区 (否则分别对每个分量应用)</summary>
        public bool Radial { get; set; } = true;

        public DeadZoneModifier(float min = 0.1f, float max = 1.0f, DeadZoneType type = DeadZoneType.Inner)
            : base("DeadZone")
        {
            Min = Math.Max(0f, min);
            Max = Math.Max(Min, max);
            Type = type;
        }

        public override float ProcessValue(float value)
        {
            if (!Enabled) return value;

            switch (Type)
            {
                case DeadZoneType.Inner:
                    if (Math.Abs(value) <= Min) return 0f;
                    // 重新映射 [Min, Max] → [0, 1]
                    if (value > 0) return (value - Min) / (Max - Min);
                    return -(Math.Abs(value) - Min) / (Max - Min);

                case DeadZoneType.Outer:
                    return Math.Clamp(value, -Max, Max);

                case DeadZoneType.InnerAndOuter:
                    if (Math.Abs(value) <= Min) return 0f;
                    if (Math.Abs(value) >= Max) return Math.Sign(value) * Max;
                    return value;

                default:
                    return value;
            }
        }

        public override Vector2 ProcessValue(Vector2 value)
        {
            if (!Enabled) return value;

            if (Radial)
            {
                float magnitude = value.Length();
                if (magnitude <= Min) return Vector2.Zero;
                if (magnitude >= Max)
                {
                    float scale = Max / magnitude;
                    return value * scale;
                }
                // 重新映射径向值
                float newMag = (magnitude - Min) / (Max - Min);
                if (magnitude > 0.001f)
                    return value * (newMag / magnitude);
                return Vector2.Zero;
            }
            else
            {
                return new Vector2(ProcessValue(value.X), ProcessValue(value.Y));
            }
        }

        public override Vector3 ProcessValue(Vector3 value)
        {
            if (!Enabled) return value;

            if (Radial)
            {
                float magnitude = value.Length();
                if (magnitude <= Min) return Vector3.Zero;
                if (magnitude >= Max)
                {
                    float scale = Max / magnitude;
                    return value * scale;
                }
                float newMag = (magnitude - Min) / (Max - Min);
                if (magnitude > 0.001f)
                    return value * (newMag / magnitude);
                return Vector3.Zero;
            }
            else
            {
                return new Vector3(ProcessValue(value.X), ProcessValue(value.Y), ProcessValue(value.Z));
            }
        }
    }

    #endregion

    #region ScaleModifier —— 缩放系数

    /// <summary>
    /// 缩放修改器 —— 将输入值乘以一个系数
    /// </summary>
    public class ScaleModifier : InputModifier
    {
        /// <summary>缩放系数</summary>
        public float Factor { get; set; } = 1.0f;

        /// <summary>是否对 Vector 使用统一缩放 (true) 或分别缩放各分量 (false)</summary>
        public bool Uniform { get; set; } = true;

        /// <summary>分别缩放各分量的系数 (仅当 Uniform == false 时使用)</summary>
        public Vector3 PerAxisFactor { get; set; } = Vector3.One;

        public ScaleModifier(float factor = 1.0f) : base("Scale")
        {
            Factor = factor;
        }

        public ScaleModifier(float x, float y, float z) : base("Scale")
        {
            Uniform = false;
            PerAxisFactor = new Vector3(x, y, z);
        }

        public override float ProcessValue(float value)
        {
            return Enabled ? value * Factor : value;
        }

        public override Vector2 ProcessValue(Vector2 value)
        {
            if (!Enabled) return value;
            return Uniform
                ? value * Factor
                : new Vector2(value.X * PerAxisFactor.X, value.Y * PerAxisFactor.Y);
        }

        public override Vector3 ProcessValue(Vector3 value)
        {
            if (!Enabled) return value;
            return Uniform
                ? value * Factor
                : value * PerAxisFactor;
        }
    }

    #endregion

    #region InvertModifier —— 反转

    /// <summary>
    /// 反转修改器 —— 取反输入值
    /// </summary>
    public class InvertModifier : InputModifier
    {
        /// <summary>是否反转 X (或一维值)</summary>
        public bool InvertX { get; set; } = true;
        /// <summary>是否反转 Y</summary>
        public bool InvertY { get; set; } = false;
        /// <summary>是否反转 Z</summary>
        public bool InvertZ { get; set; } = false;

        public InvertModifier(bool invertX = true, bool invertY = false, bool invertZ = false) : base("Invert")
        {
            InvertX = invertX;
            InvertY = invertY;
            InvertZ = invertZ;
        }

        public override float ProcessValue(float value)
        {
            return Enabled && InvertX ? -value : value;
        }

        public override Vector2 ProcessValue(Vector2 value)
        {
            if (!Enabled) return value;
            return new Vector2(
                InvertX ? -value.X : value.X,
                InvertY ? -value.Y : value.Y
            );
        }

        public override Vector3 ProcessValue(Vector3 value)
        {
            if (!Enabled) return value;
            return new Vector3(
                InvertX ? -value.X : value.X,
                InvertY ? -value.Y : value.Y,
                InvertZ ? -value.Z : value.Z
            );
        }
    }

    #endregion

    #region SwizzleModifier —— 分量重排

    /// <summary>
    /// 分量重排修改器 —— 重新排列向量的分量顺序
    /// 例如将摇杆的 XY→YX 实现反转 X/Y 轴映射
    /// </summary>
    public class SwizzleModifier : InputModifier
    {
        /// <summary>
        /// 重排顺序字符串
        /// 一维: "X" (无变化) 或 "-X" (反转)
        /// 二维: "XY", "YX", "-XY", "X-Y" 等
        /// 三维: "XYZ", "ZYX", "-XZY" 等
        /// </summary>
        public string Order { get; set; } = "XY";

        public SwizzleModifier(string order = "XY") : base("Swizzle")
        {
            Order = order.ToUpperInvariant();
        }

        private float GetComponent(Vector3 source, char component)
        {
            bool invert = false;
            char c = component;
            if (c == '-')
            {
                // 只在构造时处理，运行时 Order 已大写
                invert = true;
            }
            if (component == 'X' || component == '-'
                && Order.Length > 1 && Order[Order.IndexOf(component) + 1] == 'X')
            {
                return invert ? -source.X : source.X;
            }

            // 解析字符
            switch (char.ToUpperInvariant(component))
            {
                case 'X': return source.X;
                case 'Y': return source.Y;
                case 'Z': return source.Z;
                default: return 0f;
            }
        }

        public override float ProcessValue(float value)
        {
            if (!Enabled || Order.Length == 0) return value;
            var first = Order[0];
            if (first == '-' && Order.Length > 1)
            {
                // 负号表示反转
                return (char.ToUpperInvariant(Order[1]) switch
                {
                    'X' => value,
                    'Y' => value,
                    'Z' => value,
                    _ => value
                }) * -1f;
            }
            return value;
        }

        public override Vector2 ProcessValue(Vector2 value)
        {
            if (!Enabled) return value;

            if (Order.Length >= 2)
            {
                return new Vector2(
                    ParseSwizzleChar(Order[0], value),
                    ParseSwizzleChar(Order[1], value)
                );
            }

            return value;
        }

        public override Vector3 ProcessValue(Vector3 value)
        {
            if (!Enabled) return value;

            if (Order.Length >= 3)
            {
                return new Vector3(
                    ParseSwizzleChar(Order[0], value),
                    ParseSwizzleChar(Order[1], value),
                    ParseSwizzleChar(Order[2], value)
                );
            }
            else if (Order.Length == 2)
            {
                return new Vector3(
                    ParseSwizzleChar(Order[0], value),
                    ParseSwizzleChar(Order[1], value),
                    0f
                );
            }

            return value;
        }

        private float ParseSwizzleChar(char c, Vector2 v2)
        {
            switch (char.ToUpperInvariant(c))
            {
                case 'X': return v2.X;
                case 'Y': return v2.Y;
                case '-':
                    return 0f; // 单独的 '-' 被跳过
                default: return 0f;
            }
        }

        private float ParseSwizzleChar(char c, Vector3 v3)
        {
            switch (char.ToUpperInvariant(c))
            {
                case 'X': return v3.X;
                case 'Y': return v3.Y;
                case 'Z': return v3.Z;
                case '-':
                    return 0f;
                default: return 0f;
            }
        }

        /// <summary>带符号解析 (处理 "-X" 这种写法)</summary>
        private float ParseSwizzleSigned(ref int index, Vector3 v3)
        {
            if (index >= Order.Length) return 0f;
            float sign = 1f;
            if (Order[index] == '-')
            {
                sign = -1f;
                index++;
            }
            if (index >= Order.Length) return 0f;
            float result = ParseSwizzleChar(Order[index], v3);
            index++;
            return result * sign;
        }
    }

    /// <summary>
    /// 简化版分量重排修改器 —— 直接指定输出各分量来源
    /// </summary>
    public class SwizzleModifierV2 : InputModifier
    {
        /// <summary>输出 X 分量来源: 0=X, 1=Y, 2=Z, -1=反转X, -2=反转Y, -3=反转Z</summary>
        public int OutputX { get; set; } = 0;
        /// <summary>输出 Y 分量来源</summary>
        public int OutputY { get; set; } = 1;
        /// <summary>输出 Z 分量来源</summary>
        public int OutputZ { get; set; } = 2;

        public SwizzleModifierV2(int outX = 0, int outY = 1, int outZ = 2) : base("SwizzleV2")
        {
            OutputX = outX;
            OutputY = outY;
            OutputZ = outZ;
        }

        private float Extract(Vector3 v, int source)
        {
            return Math.Abs(source) switch
            {
                0 => v.X,
                1 => v.Y,
                2 => v.Z,
                _ => 0f
            } * (source < 0 ? -1f : 1f);
        }

        public override Vector2 ProcessValue(Vector2 value)
        {
            if (!Enabled) return value;
            var v3 = new Vector3(value.X, value.Y, 0f);
            return new Vector2(Extract(v3, OutputX), Extract(v3, OutputY));
        }

        public override Vector3 ProcessValue(Vector3 value)
        {
            if (!Enabled) return value;
            return new Vector3(Extract(value, OutputX), Extract(value, OutputY), Extract(value, OutputZ));
        }
    }

    #endregion

    #region SmoothModifier —— 平滑滤波

    /// <summary>
    /// 平滑算法类型
    /// </summary>
    public enum SmoothAlgorithm
    {
        /// <summary>指数移动平均 (EMA)</summary>
        Exponential,
        /// <summary>加权平均 (最近N帧)</summary>
        WeightedAverage,
        /// <summary>简单移动平均 (最近N帧)</summary>
        MovingAverage
    }

    /// <summary>
    /// 平滑修改器 —— 对输入值进行时间维度的平滑滤波
    /// 用于消除摇杆抖动或实现缓动效果
    /// </summary>
    public class SmoothModifier : InputModifier
    {
        /// <summary>平滑算法</summary>
        public SmoothAlgorithm Algorithm { get; set; } = SmoothAlgorithm.Exponential;

        /// <summary>平滑因子 (EMA 的 alpha, 0~1, 越小越平滑)</summary>
        public float SmoothingFactor { get; set; } = 0.15f;

        /// <summary>采样帧数 (用于移动平均)</summary>
        public int SampleCount { get; set; } = 5;

        // 运行时缓存
        [System.Text.Json.Serialization.JsonIgnore]
        private float _smoothedValue;
        [System.Text.Json.Serialization.JsonIgnore]
        private Vector2 _smoothedVec2;
        [System.Text.Json.Serialization.JsonIgnore]
        private Vector3 _smoothedVec3;
        [System.Text.Json.Serialization.JsonIgnore]
        private readonly List<float> _sampleBuffer = new List<float>();
        [System.Text.Json.Serialization.JsonIgnore]
        private readonly List<Vector2> _vec2Buffer = new List<Vector2>();
        [System.Text.Json.Serialization.JsonIgnore]
        private readonly List<Vector3> _vec3Buffer = new List<Vector3>();
        [System.Text.Json.Serialization.JsonIgnore]
        private bool _initialized;

        public SmoothModifier(float smoothingFactor = 0.15f, SmoothAlgorithm algorithm = SmoothAlgorithm.Exponential)
            : base("Smooth")
        {
            SmoothingFactor = Math.Clamp(smoothingFactor, 0.01f, 0.99f);
            Algorithm = algorithm;
        }

        public SmoothModifier(int sampleCount, SmoothAlgorithm algorithm = SmoothAlgorithm.WeightedAverage)
            : base("Smooth")
        {
            SampleCount = Math.Max(2, sampleCount);
            Algorithm = algorithm;
        }

        public override float ProcessValue(float value)
        {
            if (!Enabled) return value;

            switch (Algorithm)
            {
                case SmoothAlgorithm.Exponential:
                    if (!_initialized) { _smoothedValue = value; _initialized = true; }
                    else _smoothedValue = _smoothedValue + (value - _smoothedValue) * SmoothingFactor;
                    return _smoothedValue;

                case SmoothAlgorithm.WeightedAverage:
                case SmoothAlgorithm.MovingAverage:
                    _sampleBuffer.Add(value);
                    while (_sampleBuffer.Count > SampleCount) _sampleBuffer.RemoveAt(0);

                    if (Algorithm == SmoothAlgorithm.WeightedAverage)
                    {
                        float totalWeight = 0f;
                        float weightedSum = 0f;
                        int n = _sampleBuffer.Count;
                        for (int i = 0; i < n; i++)
                        {
                            float weight = i + 1; // 越新权重越高
                            weightedSum += _sampleBuffer[i] * weight;
                            totalWeight += weight;
                        }
                        return totalWeight > 0f ? weightedSum / totalWeight : value;
                    }
                    else
                    {
                        float sum = 0f;
                        foreach (var s in _sampleBuffer) sum += s;
                        return _sampleBuffer.Count > 0 ? sum / _sampleBuffer.Count : value;
                    }

                default:
                    return value;
            }
        }

        public override Vector2 ProcessValue(Vector2 value)
        {
            if (!Enabled) return value;

            switch (Algorithm)
            {
                case SmoothAlgorithm.Exponential:
                    if (!_initialized) { _smoothedVec2 = value; _initialized = true; }
                    else _smoothedVec2 = _smoothedVec2 + (value - _smoothedVec2) * SmoothingFactor;
                    return _smoothedVec2;

                case SmoothAlgorithm.WeightedAverage:
                case SmoothAlgorithm.MovingAverage:
                    _vec2Buffer.Add(value);
                    while (_vec2Buffer.Count > SampleCount) _vec2Buffer.RemoveAt(0);

                    if (Algorithm == SmoothAlgorithm.WeightedAverage)
                    {
                        float totalWeight = 0f;
                        Vector2 weightedSum = Vector2.Zero;
                        int n = _vec2Buffer.Count;
                        for (int i = 0; i < n; i++)
                        {
                            float weight = i + 1f;
                            weightedSum += _vec2Buffer[i] * weight;
                            totalWeight += weight;
                        }
                        return totalWeight > 0f ? weightedSum / totalWeight : value;
                    }
                    else
                    {
                        Vector2 sum = Vector2.Zero;
                        foreach (var s in _vec2Buffer) sum += s;
                        return _vec2Buffer.Count > 0 ? sum / _vec2Buffer.Count : value;
                    }

                default:
                    return value;
            }
        }

        public override Vector3 ProcessValue(Vector3 value)
        {
            if (!Enabled) return value;

            switch (Algorithm)
            {
                case SmoothAlgorithm.Exponential:
                    if (!_initialized) { _smoothedVec3 = value; _initialized = true; }
                    else _smoothedVec3 = _smoothedVec3 + (value - _smoothedVec3) * SmoothingFactor;
                    return _smoothedVec3;

                case SmoothAlgorithm.WeightedAverage:
                case SmoothAlgorithm.MovingAverage:
                    _vec3Buffer.Add(value);
                    while (_vec3Buffer.Count > SampleCount) _vec3Buffer.RemoveAt(0);

                    if (Algorithm == SmoothAlgorithm.WeightedAverage)
                    {
                        float totalWeight = 0f;
                        Vector3 weightedSum = Vector3.Zero;
                        int n = _vec3Buffer.Count;
                        for (int i = 0; i < n; i++)
                        {
                            float weight = i + 1f;
                            weightedSum += _vec3Buffer[i] * weight;
                            totalWeight += weight;
                        }
                        return totalWeight > 0f ? weightedSum / totalWeight : value;
                    }
                    else
                    {
                        Vector3 sum = Vector3.Zero;
                        foreach (var s in _vec3Buffer) sum += s;
                        return _vec3Buffer.Count > 0 ? sum / _vec3Buffer.Count : value;
                    }

                default:
                    return value;
            }
        }

        /// <summary>重置平滑状态</summary>
        public void Reset()
        {
            _initialized = false;
            _smoothedValue = 0f;
            _smoothedVec2 = Vector2.Zero;
            _smoothedVec3 = Vector3.Zero;
            _sampleBuffer.Clear();
            _vec2Buffer.Clear();
            _vec3Buffer.Clear();
        }
    }

    #endregion

    #region ClampModifier —— 值域裁剪

    /// <summary>
    /// 裁剪修改器 —— 将输入值限制在指定范围内
    /// </summary>
    public class ClampModifier : InputModifier
    {
        /// <summary>最小值</summary>
        public float MinValue { get; set; } = -1f;
        /// <summary>最大值</summary>
        public float MaxValue { get; set; } = 1f;

        /// <summary>是否对每个分量分别裁剪</summary>
        public bool PerAxis { get; set; } = false;

        public ClampModifier(float min = -1f, float max = 1f) : base("Clamp")
        {
            MinValue = min;
            MaxValue = Math.Max(min, max);
        }

        public override float ProcessValue(float value)
        {
            return Enabled ? Math.Clamp(value, MinValue, MaxValue) : value;
        }

        public override Vector2 ProcessValue(Vector2 value)
        {
            if (!Enabled) return value;
            return new Vector2(
                Math.Clamp(value.X, MinValue, MaxValue),
                Math.Clamp(value.Y, MinValue, MaxValue)
            );
        }

        public override Vector3 ProcessValue(Vector3 value)
        {
            if (!Enabled) return value;
            return new Vector3(
                Math.Clamp(value.X, MinValue, MaxValue),
                Math.Clamp(value.Y, MinValue, MaxValue),
                Math.Clamp(value.Z, MinValue, MaxValue)
            );
        }
    }

    #endregion

    #region CustomModifier —— 用户自定义修改器

    /// <summary>
    /// 自定义修改器 —— 允许用户通过委托定义自己的值变换逻辑
    /// </summary>
    public class CustomModifier : InputModifier
    {
        /// <summary>自定义 float 处理委托</summary>
        public Func<float, float> ProcessFloat { get; set; }

        /// <summary>自定义 Vector2 处理委托</summary>
        public Func<Vector2, Vector2> ProcessVector2 { get; set; }

        /// <summary>自定义 Vector3 处理委托</summary>
        public Func<Vector3, Vector3> ProcessVector3 { get; set; }

        public CustomModifier(Func<float, float> processFloat) : base("Custom")
        {
            ProcessFloat = processFloat;
        }

        public CustomModifier(string name, Func<float, float> processFloat) : base(name)
        {
            ProcessFloat = processFloat;
        }

        public CustomModifier(
            Func<float, float> processFloat,
            Func<Vector2, Vector2> processVec2,
            Func<Vector3, Vector3> processVec3) : base("Custom")
        {
            ProcessFloat = processFloat;
            ProcessVector2 = processVec2;
            ProcessVector3 = processVec3;
        }

        public override float ProcessValue(float value)
        {
            return (!Enabled || ProcessFloat == null) ? value : ProcessFloat(value);
        }

        public override Vector2 ProcessValue(Vector2 value)
        {
            return (!Enabled || ProcessVector2 == null) ? base.ProcessValue(value) : ProcessVector2(value);
        }

        public override Vector3 ProcessValue(Vector3 value)
        {
            return (!Enabled || ProcessVector3 == null) ? base.ProcessValue(value) : ProcessVector3(value);
        }
    }

    #endregion

    #region ModifierPipeline —— 修改器管线

    /// <summary>
    /// 修改器管线 —— 链式组合多个修改器，按顺序执行
    /// 值流: input → modifier[0] → modifier[1] → ... → modifier[N] → output
    /// </summary>
    public class ModifierPipeline
    {
        /// <summary>修改器列表 (按执行顺序)</summary>
        public List<InputModifier> Modifiers { get; } = new List<InputModifier>();

        /// <summary>管线名称</summary>
        public string Name { get; set; } = "Pipeline";

        public ModifierPipeline() { }

        public ModifierPipeline(string name)
        {
            Name = name;
        }

        /// <summary>添加修改器到管线末尾 (链式调用)</summary>
        public ModifierPipeline Add(InputModifier modifier)
        {
            Modifiers.Add(modifier);
            return this;
        }

        /// <summary>在指定位置插入修改器</summary>
        public ModifierPipeline Insert(int index, InputModifier modifier)
        {
            Modifiers.Insert(Math.Clamp(index, 0, Modifiers.Count), modifier);
            return this;
        }

        /// <summary>移除修改器</summary>
        public bool Remove(InputModifier modifier) => Modifiers.Remove(modifier);

        /// <summary>清空所有修改器</summary>
        public void Clear() => Modifiers.Clear();

        /// <summary>处理 float 值</summary>
        public float Process(float value)
        {
            foreach (var modifier in Modifiers)
            {
                if (modifier.Enabled)
                    value = modifier.ProcessValue(value);
            }
            return value;
        }

        /// <summary>处理 Vector2 值</summary>
        public Vector2 Process(Vector2 value)
        {
            foreach (var modifier in Modifiers)
            {
                if (modifier.Enabled)
                    value = modifier.ProcessValue(value);
            }
            return value;
        }

        /// <summary>处理 Vector3 值</summary>
        public Vector3 Process(Vector3 value)
        {
            foreach (var modifier in Modifiers)
            {
                if (modifier.Enabled)
                    value = modifier.ProcessValue(value);
            }
            return value;
        }

        /// <summary>重置所有修改器的运行时状态</summary>
        public void ResetAll()
        {
            foreach (var mod in Modifiers)
            {
                if (mod is HoldModifier hm) hm.CurrentHoldTime = 0f;
                if (mod is TapModifier tm) tm.CurrentHoldTime = 0f;
                if (mod is PressModifier pm) pm.CurrentHoldTime = 0f;
                if (mod is DoubleTapModifier dtm) dtm.Reset();
                if (mod is SmoothModifier sm) sm.Reset();
            }
        }
    }

    #endregion

    #region Utility Modifiers —— 实用修改器

    /// <summary>
    /// 四次方曲线修改器 —— 应用 gamma 曲线使摇杆输入更线性
    /// </summary>
    public class CurveModifier : InputModifier
    {
        /// <summary>曲线指数 (1=线性, <1=向中间集中, >1=向两端集中)</summary>
        public float Exponent { get; set; } = 1.0f;

        public CurveModifier(float exponent = 1.0f) : base("Curve")
        {
            Exponent = Math.Max(0.01f, exponent);
        }

        public override float ProcessValue(float value)
        {
            if (!Enabled) return value;
            return MathF.Pow(Math.Abs(value), Exponent) * MathF.Sign(value);
        }

        public override Vector2 ProcessValue(Vector2 value)
        {
            if (!Enabled) return value;
            float mag = value.Length();
            if (mag < 0.001f) return Vector2.Zero;
            float newMag = MathF.Pow(mag, Exponent);
            return value * (newMag / mag);
        }

        public override Vector3 ProcessValue(Vector3 value)
        {
            if (!Enabled) return value;
            float mag = value.Length();
            if (mag < 0.001f) return Vector3.Zero;
            float newMag = MathF.Pow(mag, Exponent);
            return value * (newMag / mag);
        }
    }

    /// <summary>
    /// 步进修改器 —— 将连续值量化为离散等级
    /// </summary>
    public class StepModifier : InputModifier
    {
        /// <summary>步进数量 (离散等级数)</summary>
        public int Steps { get; set; } = 4;

        public StepModifier(int steps = 4) : base("Step")
        {
            Steps = Math.Max(2, steps);
        }

        public override float ProcessValue(float value)
        {
            if (!Enabled) return value;
            float clamped = Math.Clamp(value, -1f, 1f);
            return MathF.Round(clamped * (Steps - 1)) / (Steps - 1);
        }
    }

    #endregion
}
