// ============================================================================================
//  PerformanceMonitor.cs
//  山雨引擎 (ShanyuEngine) 实时性能监控
// ============================================================================================
//  提供运行时性能数据的收集与查询，包括 CPU/GPU 帧时间、内存使用、模块耗时。
//  可被编辑器 Profiler 面板或游戏内 Overlay 消费。
// ============================================================================================

using System;
using System.Diagnostics;
using System.Threading;

namespace ShanyuEngine.Diagnostics
{
    public static class PerformanceMonitor
    {
        private static readonly Stopwatch _frameTimer = new();
        private static float _lastFrameTimeMs;
        private static float _minFrameTime = float.MaxValue;
        private static float _maxFrameTime;
        private static int _frameCount;
        private static int _fps;
        private static int _fpsAccum;
        private static double _fpsTimer;

        // 各模块耗时
        private static float _renderTimeMs;
        private static float _physicsTimeMs;
        private static float _audioTimeMs;
        private static float _scriptTimeMs;

        public static float FrameTimeMs => _lastFrameTimeMs;
        public static int FPS => _fps;
        public static float MinFrameTimeMs => _minFrameTime;
        public static float MaxFrameTimeMs => _maxFrameTime;
        public static float RenderTimeMs { get => _renderTimeMs; set => _renderTimeMs = value; }
        public static float PhysicsTimeMs { get => _physicsTimeMs; set => _physicsTimeMs = value; }
        public static float AudioTimeMs { get => _audioTimeMs; set => _audioTimeMs = value; }
        public static float ScriptTimeMs { get => _scriptTimeMs; set => _scriptTimeMs = value; }

        public static void BeginFrame()
        {
            _frameTimer.Restart();
        }

        public static void EndFrame()
        {
            _lastFrameTimeMs = (float)_frameTimer.Elapsed.TotalMilliseconds;
            if (_lastFrameTimeMs < _minFrameTime) _minFrameTime = _lastFrameTimeMs;
            if (_lastFrameTimeMs > _maxFrameTime) _maxFrameTime = _lastFrameTimeMs;
            _frameCount++;

            // FPS 计算
            _fpsAccum++;
            _fpsTimer += _lastFrameTimeMs / 1000.0;
            if (_fpsTimer >= 1.0)
            {
                _fps = _fpsAccum;
                _fpsAccum = 0;
                _fpsTimer -= 1.0;
            }
        }

        public static void Reset()
        {
            _minFrameTime = float.MaxValue;
            _maxFrameTime = 0;
            _frameCount = 0;
        }

        public static string GetFrameReport()
        {
            return $"Frame: {FPS} FPS, " +
                   $"{FrameTimeMs:F1}ms ({MinFrameTimeMs:F1}/{MaxFrameTimeMs:F1}) " +
                   $"Render: {RenderTimeMs:F1}ms " +
                   $"Physics: {PhysicsTimeMs:F1}ms " +
                   $"Audio: {AudioTimeMs:F1}ms";
        }
    }
}
