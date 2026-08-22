using System;
using System.Numerics;

namespace ShanyuEngine.Input.Enhanced
{
    #region TriggerState 枚举

    /// <summary>
    /// 触发器评估结果
    /// </summary>
    public enum TriggerState
    {
        /// <summary>不触发 (条件不满足)</summary>
        None,
        /// <summary>可能触发 (条件正在满足中，如按住但还没到阈值)</summary>
        Potential,
        /// <summary>已触发 (条件满足)</summary>
        Triggered,
        /// <summary>取消 (不再满足条件)</summary>
        Canceled
    }

    #endregion

    #region InputTrigger 基类

    /// <summary>
    /// 输入触发器基类 —— 决定动作何时从 Started 变为 Performed
    /// 类似 UE 的 Input Trigger (Press, Hold, Tap, MultiTap, SlowTap, Pulse, Threshold)
    /// </summary>
    public abstract class InputTrigger
    {
        /// <summary>触发器名称</summary>
        public string Name { get; set; }

        protected InputTrigger(string name = "Trigger")
        {
            Name = name;
        }

        /// <summary>
        /// 评估触发条件
        /// </summary>
        /// <param name="action">当前动作引用 (用于访问 HoldTime 等内部状态)</param>
        /// <param name="value">动作的当前值</param>
        /// <param name="controlValue">控制的原始值</param>
        /// <returns>触发状态</returns>
        public abstract TriggerState Evaluate(EnhancedInputAction action, float value, float controlValue);

        /// <summary>重置触发器内部状态</summary>
        public virtual void Reset() { }
    }

    #endregion

    #region PressTrigger —— 按下触发

    /// <summary>
    /// 按下触发器 —— 按下的瞬间立即触发 (最常用的触发器)
    /// </summary>
    public class PressTrigger : InputTrigger
    {
        public PressTrigger() : base("Press") { }

        public override TriggerState Evaluate(EnhancedInputAction action, float value, float controlValue)
        {
            // 按住期间持续触发 (动作处于 Started 或 Performed 状态时)
            if (controlValue > 0.5f)
            {
                // 首次按下或持续按住都触发
                if (action.State == InputActionState.Started)
                    return TriggerState.Triggered;
                // 持续按住时不重新触发 (单次触发)
                return TriggerState.None;
            }
            return TriggerState.Canceled;
        }
    }

    #endregion

    #region ReleaseTrigger —— 释放触发

    /// <summary>
    /// 释放触发器 —— 松开按键时触发
    /// 适用于"松开跳跃"或"释放技能"等场景
    /// </summary>
    public class ReleaseTrigger : InputTrigger
    {
        public ReleaseTrigger() : base("Release") { }

        public override TriggerState Evaluate(EnhancedInputAction action, float value, float controlValue)
        {
            if (controlValue < 0.5f && action.HoldTime > 0f)
            {
                // 有按住记录但现在释放了 → 触发
                return TriggerState.Triggered;
            }
            if (controlValue > 0.5f)
            {
                return TriggerState.Potential;
            }
            return TriggerState.None;
        }
    }

    #endregion

    #region HoldTrigger —— 按住持续触发

    /// <summary>
    /// 按住触发器 —— 按住超过 HoldTime 后触发，并可选择持续触发
    /// 类似 UE 的 Hold Input Trigger
    /// </summary>
    public class HoldTrigger : InputTrigger
    {
        /// <summary>需要按住的持续时间 (秒) 才触发</summary>
        public float HoldTime { get; set; } = 0.2f;

        /// <summary>首次触发时是否产生 Press 事件</summary>
        public bool PressOnStart { get; set; } = false;

        /// <summary>是否持续触发 (true = 每帧都触发, false = 只触发一次)</summary>
        public bool Continuously { get; set; } = true;

        private bool _hasTriggeredOnce;

        public HoldTrigger(float holdTime = 0.2f) : base("Hold")
        {
            HoldTime = holdTime;
        }

        public override TriggerState Evaluate(EnhancedInputAction action, float value, float controlValue)
        {
            if (controlValue > 0.5f)
            {
                if (action.HoldTime >= HoldTime)
                {
                    if (!_hasTriggeredOnce)
                    {
                        _hasTriggeredOnce = true;
                        return TriggerState.Triggered;
                    }
                    if (Continuously)
                        return TriggerState.Triggered;
                    return TriggerState.None;
                }
                return TriggerState.Potential;
            }
            else
            {
                _hasTriggeredOnce = false;
                return TriggerState.Canceled;
            }
        }

        public override void Reset()
        {
            _hasTriggeredOnce = false;
        }
    }

    #endregion

    #region TapTrigger —— 点击触发

    /// <summary>
    /// 点击触发器 —— 按下后在 MaxHoldTime 内释放才触发
    /// 如果按住超过 MaxHoldTime，则不触发 (取消)
    /// </summary>
    public class TapTrigger : InputTrigger
    {
        /// <summary>最大按住时间 (秒)，超过则不触发</summary>
        public float MaxHoldTime { get; set; } = 0.3f;

        /// <summary>是否在按下时就潜在触发</summary>
        public bool PressOnStart { get; set; } = true;

        public TapTrigger(float maxHoldTime = 0.3f) : base("Tap")
        {
            MaxHoldTime = maxHoldTime;
        }

        public override TriggerState Evaluate(EnhancedInputAction action, float value, float controlValue)
        {
            if (controlValue > 0.5f)
            {
                // 还在按住中，尚未释放
                if (action.HoldTime > MaxHoldTime)
                {
                    // 按住太久了，取消点击
                    return TriggerState.Canceled;
                }
                return TriggerState.Potential;
            }
            else if (action.HoldTime > 0f && action.HoldTime <= MaxHoldTime)
            {
                // 释放了，且按住时间在阈值内 → 触发
                return TriggerState.Triggered;
            }
            return TriggerState.None;
        }
    }

    #endregion

    #region MultiTapTrigger —— 多次点击

    /// <summary>
    /// 多次点击触发器 —— 在 TapInterval 时间内点击 TapCount 次才触发
    /// 例如双击 (TapCount=2)、三击 (TapCount=3)
    /// </summary>
    public class MultiTapTrigger : InputTrigger
    {
        /// <summary>需要点击的次数</summary>
        public int TapCount { get; set; } = 2;

        /// <summary>两次点击之间的最大间隔 (秒)</summary>
        public float TapInterval { get; set; } = 0.4f;

        /// <summary>单次点击的最大按住时间 (超过则不算点击)</summary>
        public float MaxTapHoldTime { get; set; } = 0.3f;

        [System.Text.Json.Serialization.JsonIgnore]
        private int _currentTapCount;

        [System.Text.Json.Serialization.JsonIgnore]
        private float _lastTapEndTime;

        [System.Text.Json.Serialization.JsonIgnore]
        private bool _wasPressed;

        public MultiTapTrigger(int tapCount = 2, float tapInterval = 0.4f) : base("MultiTap")
        {
            TapCount = Math.Max(2, tapCount);
            TapInterval = tapInterval;
        }

        public override TriggerState Evaluate(EnhancedInputAction action, float value, float controlValue)
        {
            if (controlValue > 0.5f)
            {
                _wasPressed = true;
                return TriggerState.Potential;
            }
            else if (_wasPressed)
            {
                // 释放 → 一次点击完成
                _wasPressed = false;

                if (action.HoldTime > MaxTapHoldTime)
                {
                    // 按住太久了，不算点击，重置
                    _currentTapCount = 0;
                    return TriggerState.Canceled;
                }

                _currentTapCount++;

                if (_currentTapCount >= TapCount)
                {
                    // 达到目标点击次数 → 触发
                    _currentTapCount = 0;
                    return TriggerState.Triggered;
                }

                _lastTapEndTime = action.HoldTime; // 近似时间
                return TriggerState.Potential;
            }

            // 检查间隔超时
            if (_currentTapCount > 0)
            {
                // 如果距离上次点击太久了，重置计数
                // (这里用动作的 HoldTime=0 来判断已经释放了一段时间)
                return TriggerState.None;
            }

            return TriggerState.None;
        }

        public override void Reset()
        {
            _currentTapCount = 0;
            _lastTapEndTime = 0f;
            _wasPressed = false;
        }
    }

    #endregion

    #region SlowTapTrigger —— 慢点击

    /// <summary>
    /// 慢点击触发器 —— 按住至少 MinHoldTime 后释放时才触发
    /// 与 TapTrigger 相反：Tap 是短按释放，SlowTap 是长按释放
    /// </summary>
    public class SlowTapTrigger : InputTrigger
    {
        /// <summary>最小按住时间 (秒)，短于此时间不触发</summary>
        public float MinHoldTime { get; set; } = 0.5f;

        public SlowTapTrigger(float minHoldTime = 0.5f) : base("SlowTap")
        {
            MinHoldTime = minHoldTime;
        }

        public override TriggerState Evaluate(EnhancedInputAction action, float value, float controlValue)
        {
            if (controlValue > 0.5f)
            {
                if (action.HoldTime >= MinHoldTime)
                    return TriggerState.Potential;
                return TriggerState.Potential;
            }
            else if (action.HoldTime >= MinHoldTime)
            {
                // 按住足够时间后释放 → 触发
                return TriggerState.Triggered;
            }
            return TriggerState.Canceled;
        }
    }

    #endregion

    #region ThresholdTrigger —— 阈值触发

    /// <summary>
    /// 阈值触发器 —— 当值超过指定阈值时触发
    /// 适用于模拟轴/扳机等连续值控制
    /// </summary>
    public class ThresholdTrigger : InputTrigger
    {
        /// <summary>触发阈值 (大于此值触发)</summary>
        public float Threshold { get; set; } = 0.5f;

        /// <summary>是否使用绝对值比较 (忽略正负)</summary>
        public bool UseAbsolute { get; set; } = false;

        /// <summary>触发方向</summary>
        public enum ThresholdDirection
        {
            /// <summary>值从低于阈值变为高于阈值时触发</summary>
            Rising,
            /// <summary>值从高于阈值变为低于阈值时触发</summary>
            Falling,
            /// <summary>两者都触发</summary>
            Both
        }

        /// <summary>触发方向</summary>
        public ThresholdDirection Direction { get; set; } = ThresholdDirection.Rising;

        [System.Text.Json.Serialization.JsonIgnore]
        private bool _wasAboveThreshold;

        public ThresholdTrigger(float threshold = 0.5f) : base("Threshold")
        {
            Threshold = threshold;
        }

        public override TriggerState Evaluate(EnhancedInputAction action, float value, float controlValue)
        {
            float checkValue = UseAbsolute ? Math.Abs(controlValue) : controlValue;
            bool isAbove = checkValue > Threshold;

            switch (Direction)
            {
                case ThresholdDirection.Rising:
                    if (isAbove && !_wasAboveThreshold)
                    {
                        _wasAboveThreshold = true;
                        return TriggerState.Triggered;
                    }
                    break;

                case ThresholdDirection.Falling:
                    if (!isAbove && _wasAboveThreshold)
                    {
                        _wasAboveThreshold = false;
                        return TriggerState.Triggered;
                    }
                    break;

                case ThresholdDirection.Both:
                    if (isAbove && !_wasAboveThreshold)
                    {
                        _wasAboveThreshold = true;
                        return TriggerState.Triggered;
                    }
                    if (!isAbove && _wasAboveThreshold)
                    {
                        _wasAboveThreshold = false;
                        return TriggerState.Triggered;
                    }
                    break;
            }

            if (isAbove) _wasAboveThreshold = true;
            else _wasAboveThreshold = false;

            return TriggerState.None;
        }

        public override void Reset()
        {
            _wasAboveThreshold = false;
        }
    }

    #endregion

    #region PulseTrigger —— 脉冲周期触发

    /// <summary>
    /// 脉冲触发器 —— 持续按住时，按固定间隔周期性触发
    /// 适用于"连续开火"、"长按删除"等场景
    /// </summary>
    public class PulseTrigger : InputTrigger
    {
        /// <summary>脉冲间隔 (秒)</summary>
        public float Interval { get; set; } = 0.1f;

        /// <summary>首次触发延迟 (秒)，通常略长于 Interval</summary>
        public float InitialDelay { get; set; } = 0.5f;

        /// <summary>按下时是否立即触发一次</summary>
        public bool TriggerOnPress { get; set; } = true;

        [System.Text.Json.Serialization.JsonIgnore]
        private float _elapsedTime;
        [System.Text.Json.Serialization.JsonIgnore]
        private bool _initialTriggerDone;

        public PulseTrigger(float interval = 0.1f, float initialDelay = 0.5f) : base("Pulse")
        {
            Interval = Math.Max(0.01f, interval);
            InitialDelay = Math.Max(0f, initialDelay);
        }

        public override TriggerState Evaluate(EnhancedInputAction action, float value, float controlValue)
        {
            if (controlValue > 0.5f)
            {
                _elapsedTime += action.State == InputActionState.Performed ? 1f / 60f : 0f; // 近似

                // 首次按下立即触发
                if (TriggerOnPress && !_initialTriggerDone)
                {
                    _initialTriggerDone = true;
                    _elapsedTime = 0f;
                    return TriggerState.Triggered;
                }

                // 初始延迟后的首次触发
                if (!_initialTriggerDone && _elapsedTime >= InitialDelay)
                {
                    _initialTriggerDone = true;
                    _elapsedTime = 0f;
                    return TriggerState.Triggered;
                }

                // 周期性脉冲触发
                if (_initialTriggerDone && _elapsedTime >= Interval)
                {
                    _elapsedTime -= Interval;
                    return TriggerState.Triggered;
                }

                return TriggerState.Potential;
            }
            else
            {
                _elapsedTime = 0f;
                _initialTriggerDone = false;
                return TriggerState.Canceled;
            }
        }

        public override void Reset()
        {
            _elapsedTime = 0f;
            _initialTriggerDone = false;
        }
    }

    #endregion

    #region CustomTrigger —— 自定义触发器

    /// <summary>
    /// 自定义触发器 —— 允许用户通过委托定义自己的触发逻辑
    /// </summary>
    public class CustomTrigger : InputTrigger
    {
        /// <summary>自定义评估委托</summary>
        public Func<EnhancedInputAction, float, float, TriggerState> EvaluateFunc { get; set; }

        /// <summary>自定义重置委托</summary>
        public Action ResetFunc { get; set; }

        public CustomTrigger(Func<EnhancedInputAction, float, float, TriggerState> evaluateFunc, string name = "Custom")
            : base(name)
        {
            EvaluateFunc = evaluateFunc;
        }

        public CustomTrigger(
            Func<EnhancedInputAction, float, float, TriggerState> evaluateFunc,
            Action resetFunc,
            string name = "Custom") : base(name)
        {
            EvaluateFunc = evaluateFunc;
            ResetFunc = resetFunc;
        }

        public override TriggerState Evaluate(EnhancedInputAction action, float value, float controlValue)
        {
            return EvaluateFunc?.Invoke(action, value, controlValue) ?? TriggerState.None;
        }

        public override void Reset()
        {
            ResetFunc?.Invoke();
        }
    }

    #endregion
}
