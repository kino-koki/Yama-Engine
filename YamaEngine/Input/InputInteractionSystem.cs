using System;
using System.Numerics;

namespace YamaEngine.Input.Enhanced
{
    #region InputInteraction 基类

    /// <summary>
    /// 输入交互基类 —— 修改动作的触发行为，替代触发器进行更高级的状态控制
    /// 交互定义了"按住→等一段时间→触发"等多阶段行为
    /// 类似 UE 的 Input Interactions (Hold, Tap, SlowTap, MultiTap, Wait)
    /// </summary>
    public abstract class InputInteraction
    {
        /// <summary>交互名称</summary>
        public string Name { get; set; }

        /// <summary>交互阶段 (由子类定义)</summary>
        public InteractionPhase CurrentPhase { get; protected set; } = InteractionPhase.Idle;

        protected InputInteraction(string name = "Interaction")
        {
            Name = name;
        }

        /// <summary>
        /// 处理交互逻辑
        /// </summary>
        /// <param name="action">动作引用</param>
        /// <param name="controlValue">控制值</param>
        /// <param name="deltaTime">帧间隔</param>
        /// <returns>处理结果 (影响动作状态的转换)</returns>
        public abstract InteractionResult Process(EnhancedInputAction action, float controlValue, float deltaTime);

        /// <summary>重置交互状态</summary>
        public abstract void Reset();
    }

    #endregion

    #region InteractionPhase 枚举

    /// <summary>
    /// 交互阶段
    /// </summary>
    public enum InteractionPhase
    {
        /// <summary>空闲</summary>
        Idle,
        /// <summary>等待中 (如 Hold 等待时间)</summary>
        Waiting,
        /// <summary>已触发</summary>
        Triggered,
        /// <summary>进行中</summary>
        InProgress,
        /// <summary>已取消</summary>
        Canceled,
        /// <summary>已完成</summary>
        Completed
    }

    #endregion

    #region InteractionResult 结构体

    /// <summary>
    /// 交互处理结果 —— 返回给动作状态机的指令
    /// </summary>
    public struct InteractionResult
    {
        /// <summary>是否已触发 (请求动作进入 Performed 状态)</summary>
        public bool Triggered;
        /// <summary>是否已取消 (请求动作进入 Canceled 状态)</summary>
        /// </summary>
        public bool Canceled;
        /// <summary>是否正在等待 (不改变状态)</summary>
        public bool Waiting;
        /// <summary>交互输出值 (如果非零，覆盖控制值)</summary>
        public float OutputValue;

        public static InteractionResult None => new InteractionResult();
        public static InteractionResult Trigger => new InteractionResult { Triggered = true, OutputValue = 1f };
        public static InteractionResult TriggerWithValue(float value) => new InteractionResult { Triggered = true, OutputValue = value };
        public static InteractionResult Cancel => new InteractionResult { Canceled = true };
        public static InteractionResult Wait => new InteractionResult { Waiting = true };
    }

    #endregion

    #region HoldInteraction —— 按住交互

    /// <summary>
    /// 按住交互 —— 按下后等待 HoldTime，然后触发
    /// 按住期间可以产生 Press 事件
    /// 释放时可以产生 Release 事件
    /// </summary>
    public class HoldInteraction : InputInteraction
    {
        /// <summary>触发需要的按住时间 (秒)</summary>
        public float HoldTime { get; set; } = 0.5f;

        /// <summary>按住瞬间是否触发 Press 行为</summary>
        public bool PressOnStart { get; set; } = false;

        /// <summary>持续按住是否持续触发</summary>
        public bool ContinuousTrigger { get; set; } = true;

        private float _holdTimer;
        private bool _hasTriggered;
        private bool _pressTriggered;

        public HoldInteraction(float holdTime = 0.5f) : base("Hold")
        {
            HoldTime = holdTime;
        }

        public override InteractionResult Process(EnhancedInputAction action, float controlValue, float deltaTime)
        {
            if (controlValue > 0.5f)
            {
                _holdTimer += deltaTime;

                // 按下瞬间
                if (action.State == InputActionState.Started || _holdTimer < 0.02f)
                {
                    if (PressOnStart && !_pressTriggered)
                    {
                        _pressTriggered = true;
                        CurrentPhase = InteractionPhase.InProgress;
                        return InteractionResult.TriggerWithValue(0f); // 仅通知开始
                    }
                }

                // 等待中
                if (_holdTimer < HoldTime)
                {
                    CurrentPhase = InteractionPhase.Waiting;
                    return InteractionResult.Wait;
                }

                // 达到阈值
                if (!_hasTriggered)
                {
                    _hasTriggered = true;
                    CurrentPhase = InteractionPhase.Triggered;
                    return InteractionResult.Trigger;
                }

                // 持续触发
                if (ContinuousTrigger)
                {
                    CurrentPhase = InteractionPhase.Triggered;
                    return InteractionResult.Trigger;
                }

                CurrentPhase = InteractionPhase.InProgress;
                return InteractionResult.None;
            }
            else
            {
                // 释放
                CurrentPhase = InteractionPhase.Canceled;
                Reset();
                return InteractionResult.Cancel;
            }
        }

        public override void Reset()
        {
            _holdTimer = 0f;
            _hasTriggered = false;
            _pressTriggered = false;
            CurrentPhase = InteractionPhase.Idle;
        }
    }

    #endregion

    #region TapInteraction —— 点击交互

    /// <summary>
    /// 点击交互 —— 在 MaxDuration 内按下并释放才触发
    /// 超时则取消
    /// </summary>
    public class TapInteraction : InputInteraction
    {
        /// <summary>最大持续时间 (秒)</summary>
        public float MaxDuration { get; set; } = 0.2f;

        /// <summary>是否在按下时就产生 Started 事件</summary>
        public bool TriggerOnStart { get; set; } = false;

        private float _holdTimer;
        private bool _waitingForRelease;

        public TapInteraction(float maxDuration = 0.2f) : base("Tap")
        {
            MaxDuration = maxDuration;
        }

        public override InteractionResult Process(EnhancedInputAction action, float controlValue, float deltaTime)
        {
            if (controlValue > 0.5f)
            {
                _holdTimer += deltaTime;
                _waitingForRelease = true;

                if (_holdTimer > MaxDuration)
                {
                    // 超时，取消
                    CurrentPhase = InteractionPhase.Canceled;
                    Reset();
                    return InteractionResult.Cancel;
                }

                if (TriggerOnStart && _holdTimer < 0.02f)
                {
                    CurrentPhase = InteractionPhase.InProgress;
                    return InteractionResult.TriggerWithValue(0f);
                }

                CurrentPhase = InteractionPhase.Waiting;
                return InteractionResult.Wait;
            }
            else if (_waitingForRelease)
            {
                // 释放，且未超时 → 触发
                _waitingForRelease = false;
                CurrentPhase = InteractionPhase.Triggered;
                Reset();
                return InteractionResult.Trigger;
            }

            return InteractionResult.None;
        }

        public override void Reset()
        {
            _holdTimer = 0f;
            _waitingForRelease = false;
            CurrentPhase = InteractionPhase.Idle;
        }
    }

    #endregion

    #region SlowTapInteraction —— 慢点击交互

    /// <summary>
    /// 慢点击交互 —— 按住至少 MinDuration 后释放时触发
    /// 与 TapInteraction 相反
    /// </summary>
    public class SlowTapInteraction : InputInteraction
    {
        /// <summary>最小按住时间 (秒)</summary>
        public float MinDuration { get; set; } = 0.5f;

        private float _holdTimer;
        private bool _waitingForRelease;
        private bool _satisfiedMinDuration;

        public SlowTapInteraction(float minDuration = 0.5f) : base("SlowTap")
        {
            MinDuration = minDuration;
        }

        public override InteractionResult Process(EnhancedInputAction action, float controlValue, float deltaTime)
        {
            if (controlValue > 0.5f)
            {
                _holdTimer += deltaTime;
                _waitingForRelease = true;

                if (_holdTimer >= MinDuration)
                    _satisfiedMinDuration = true;

                CurrentPhase = InteractionPhase.Waiting;
                return InteractionResult.Wait;
            }
            else if (_waitingForRelease)
            {
                _waitingForRelease = false;

                if (_satisfiedMinDuration)
                {
                    CurrentPhase = InteractionPhase.Triggered;
                    Reset();
                    return InteractionResult.Trigger;
                }
                else
                {
                    CurrentPhase = InteractionPhase.Canceled;
                    Reset();
                    return InteractionResult.Cancel;
                }
            }

            return InteractionResult.None;
        }

        public override void Reset()
        {
            _holdTimer = 0f;
            _waitingForRelease = false;
            _satisfiedMinDuration = false;
            CurrentPhase = InteractionPhase.Idle;
        }
    }

    #endregion

    #region MultiTapInteraction —— 多次点击交互

    /// <summary>
    /// 多次点击交互 —— 在 TapInterval 时间内连续点击 TapCount 次才触发
    /// </summary>
    public class MultiTapInteraction : InputInteraction
    {
        /// <summary>需要点击的次数</summary>
        public int TapCount { get; set; } = 2;

        /// <summary>两次点击的最大间隔 (秒)</summary>
        public float TapInterval { get; set; } = 0.4f;

        /// <summary>单次点击的最大持续时间 (秒)</summary>
        public float MaxTapDuration { get; set; } = 0.3f;

        private int _currentTapCount;
        private float _releaseTimer;
        private float _holdTimer;
        private bool _isHolding;

        public MultiTapInteraction(int tapCount = 2, float tapInterval = 0.4f) : base("MultiTap")
        {
            TapCount = Math.Max(2, tapCount);
            TapInterval = tapInterval;
        }

        public override InteractionResult Process(EnhancedInputAction action, float controlValue, float deltaTime)
        {
            if (controlValue > 0.5f)
            {
                _isHolding = true;
                _holdTimer += deltaTime;

                // 按住太久，不算一次点击
                if (_holdTimer > MaxTapDuration)
                {
                    CurrentPhase = InteractionPhase.Canceled;
                    Reset();
                    return InteractionResult.Cancel;
                }

                CurrentPhase = InteractionPhase.Waiting;
                return InteractionResult.Wait;
            }
            else
            {
                if (_isHolding)
                {
                    // 释放 → 完成一次点击
                    _isHolding = false;
                    _currentTapCount++;
                    _releaseTimer = 0f;

                    if (_currentTapCount >= TapCount)
                    {
                        // 达到目标次数 → 触发
                        CurrentPhase = InteractionPhase.Triggered;
                        Reset();
                        return InteractionResult.Trigger;
                    }

                    CurrentPhase = InteractionPhase.Waiting;
                    return InteractionResult.Wait;
                }
                else
                {
                    // 未按住，检查间隔超时
                    _releaseTimer += deltaTime;
                    if (_releaseTimer > TapInterval && _currentTapCount > 0)
                    {
                        // 间隔超时，重置
                        CurrentPhase = InteractionPhase.Canceled;
                        Reset();
                        return InteractionResult.Cancel;
                    }
                }
            }

            return InteractionResult.None;
        }

        public override void Reset()
        {
            _currentTapCount = 0;
            _releaseTimer = 0f;
            _holdTimer = 0f;
            _isHolding = false;
            CurrentPhase = InteractionPhase.Idle;
        }
    }

    #endregion

    #region WaitInteraction —— 等待交互

    /// <summary>
    /// 等待交互 —— 按下后等待指定 Duration，然后直接触发
    /// 类似 HoldInteraction 但没有持续触发，只触发一次
    /// </summary>
    public class WaitInteraction : InputInteraction
    {
        /// <summary>等待时间 (秒)</summary>
        public float Duration { get; set; } = 1.0f;

        /// <summary>等待期间是否阻止其他交互</summary>
        public bool BlockDuringWait { get; set; } = true;

        private float _waitTimer;
        private bool _isWaiting;

        public WaitInteraction(float duration = 1.0f) : base("Wait")
        {
            Duration = Math.Max(0f, duration);
        }

        public override InteractionResult Process(EnhancedInputAction action, float controlValue, float deltaTime)
        {
            if (controlValue > 0.5f)
            {
                if (!_isWaiting)
                {
                    _isWaiting = true;
                    _waitTimer = 0f;
                }

                _waitTimer += deltaTime;

                if (_waitTimer >= Duration)
                {
                    CurrentPhase = InteractionPhase.Triggered;
                    Reset();
                    return InteractionResult.Trigger;
                }

                CurrentPhase = InteractionPhase.Waiting;
                return InteractionResult.Wait;
            }
            else
            {
                if (_isWaiting)
                {
                    // 等待期间释放 → 取消
                    CurrentPhase = InteractionPhase.Canceled;
                    Reset();
                    return InteractionResult.Cancel;
                }
            }

            return InteractionResult.None;
        }

        public override void Reset()
        {
            _waitTimer = 0f;
            _isWaiting = false;
            CurrentPhase = InteractionPhase.Idle;
        }
    }

    #endregion
}
