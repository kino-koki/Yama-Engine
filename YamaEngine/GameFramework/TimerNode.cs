using System;
using YamaEngine.Core;

namespace YamaEngine.GameFramework
{
    // ════════════════════════════════════════════════════════════════════════
    // yama引擎 - 计时器节点 (TimerNode)
    //
    //   对应 Godot 的 Timer 节点：倒数计时，归零时触发一次 timeout 信号。
    //   支持 one_shot（单次 vs 循环）、autostart（加入场景即启动）、
    //   时间缩放（time_scale 影响，与引擎时间同步）。
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 计时器节点：倒数计时并在归零时触发 timeout 信号。
    /// 用法：
    /// <code>
    /// var timer = go.AddComponent<TimerNode>();
    /// timer.WaitTime = 1.5f;
    /// timer.OneShot = true;
    /// timer.Start();
    /// timer.GetSignal("timeout").Connect(() => Debug.Log("done"));
    /// </code>
    /// </summary>
    public sealed class TimerNode : ComponentReal
    {
        private float _waitTime = 1.0f;
        private float _timeLeft;
        private bool _running;
        private int _timeoutCount;

        /// <summary>倒计时的时长（秒）。</summary>
        public float WaitTime
        {
            get => _waitTime;
            set
            {
                _waitTime = Math.Max(0f, value);
                if (_running)
                    _timeLeft = _waitTime; // 运行中改变时长：重置本次倒计时
            }
        }

        /// <summary>是否单次触发（true: 触发后停止；false: 自动重启循环）。</summary>
        public bool OneShot { get; set; }

        private bool _autostart;
        private bool _autostartConsumed;

        /// <summary>
        /// 是否自动启动（对应 Godot autostart）。为 true 时，首次 Update 自动 Start()
        /// （对应 Godot 加入场景树即启动）。
        /// </summary>
        public bool Autostart
        {
            get => _autostart;
            set
            {
                _autostart = value;
                if (value) _autostartConsumed = false; // 重新开启：允许再次自动启动
            }
        }

        private float _timeScale = 1f;

        /// <summary>时间缩放（对应 Godot time_scale）。影响本计时器倒计时速度。</summary>
        public float TimeScale
        {
            get => _timeScale;
            set => _timeScale = Math.Max(0f, value);
        }

        /// <summary>计时器是否正在运行。</summary>
        public bool IsRunning => _running;

        /// <summary>剩余时间（秒）。</summary>
        public float TimeLeft => _running ? _timeLeft : 0f;

        /// <summary>timeout 信号累计触发次数。</summary>
        public int TimeoutCount => _timeoutCount;

        // ── 生命周期 ──
        public TimerNode()
        {
            // 加入场景时自动启动（对应 Godot 的 autostart=true 默认行为）
        }

        /// <summary>启动计时器。重置剩余时间到 WaitTime。</summary>
        public void Start()
        {
            _running = true;
            _timeLeft = _waitTime;
        }

        /// <summary>停止计时器（不触发 timeout）。</summary>
        public void Stop() => _running = false;

        /// <summary>
        /// 每帧倒计时。归零时触发 timeout 信号：
        /// OneShot=true → 停止；OneShot=false → 重启。
        /// </summary>
        public override void Update(float deltaTime)
        {
            // autostart：首次 Update 自动启动（对应 Godot 加入场景树即启动）
            if (_autostart && !_autostartConsumed)
            {
                _autostartConsumed = true;
                if (!_running) Start();
            }

            if (!_running) return;
            _timeLeft -= deltaTime * _timeScale; // 时间缩放：仅影响倒计时速度
            if (_timeLeft <= 0f)
            {
                _timeoutCount++;
                GameObject.GetSignal("timeout").Emit(_timeoutCount);
                if (OneShot)
                    _running = false;
                else
                    _timeLeft += _waitTime; // 循环：保留溢出时间避免漂移
            }
        }
    }
}
