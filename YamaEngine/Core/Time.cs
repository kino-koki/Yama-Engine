// ============================================================================================
//  Time.cs
//  yama引擎 (YamaEngine) 全局时间静态访问入口
// ============================================================================================
//  提供全局静态 Time 类型，作为对 TimeManager 的便捷封装，供各个子系统统一使用。
//  用法：
//    float dt    = Time.DeltaTime;
//    double t    = Time.GameTime;
//    var manager = Time.TimeManager;
// ============================================================================================

namespace YamaEngine.Core
{
    /// <summary>
    /// 全局时间访问静态类。统一封装对 <see cref="YamaEngine.Core.TimeManager"/> 的访问。
    /// </summary>
    public static class Time
    {
        /// <summary>底层时间管理器实例。</summary>
        public static TimeManager TimeManager => YamaEngine.Core.TimeManager.Instance;

        /// <summary>当前帧与上一帧的时间差（受时间缩放影响）。</summary>
        public static float DeltaTime => TimeManager.DeltaTime;

        /// <summary>不受时间缩放影响的原始帧时间差。</summary>
        public static float UnscaledDeltaTime => TimeManager.UnscaledDeltaTime;

        /// <summary>固定步进 DeltaTime（受时间缩放影响）。</summary>
        public static float FixedDelta => TimeManager.FixedDelta;

        /// <summary>当前游戏运行总时间（受时间缩放）。</summary>
        public static double GameTime => TimeManager.GameTime;

        /// <summary>当前运行总时间（不受时间缩放）。</summary>
        public static double UnscaledTime => TimeManager.UnscaledTime;

        /// <summary>真实世界已流逝时间。</summary>
        public static double RealTime => TimeManager.RealTime;

        /// <summary>已流逝时间（等价于 GameTime，便于习惯使用 Elapsed 的代码）。</summary>
        public static double Elapsed => TimeManager.GameTime;

        /// <summary>总帧数。</summary>
        public static ulong FrameCount => TimeManager.FrameCount;

        /// <summary>当前 FPS。</summary>
        public static int FPS => TimeManager.FPS;

        /// <summary>时间缩放系数（1.0 为正常速度）。</summary>
        public static float TimeScale
        {
            get => TimeManager.TimeScale;
            set => TimeManager.TimeScale = value;
        }

        /// <summary>是否暂停（接受游戏时间推进）。</summary>
        public static bool IsPaused
        {
            get => TimeManager.IsPaused;
            set => TimeManager.IsPaused = value;
        }

        /// <summary>固定步进时间步长（秒）。</summary>
        public static float FixedDeltaTime
        {
            get => TimeManager.FixedDeltaTime;
            set => TimeManager.FixedDeltaTime = value;
        }
    }
}