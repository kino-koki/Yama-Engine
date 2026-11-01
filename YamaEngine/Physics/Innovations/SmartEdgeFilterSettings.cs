// ============================================================================================
//  SmartEdgeFilterSettings.cs — 自研创新 1 的 C# 配置助手。
//
//  SmartEdgeFilter 的核心逻辑在 native（YamaJolt.dll），C# 侧仅提供易用配置：
//  - 角度阈值（度/弧度互转）
//  - 预设（保守/标准/激进）
//  - 批量应用到世界中的体
// ============================================================================================
using System;

namespace YamaEngine.Physics
{
    /// <summary>SmartEdgeFilter 预设。</summary>
    public enum EdgeFilterPreset
    {
        /// <summary>保守：仅压极近共面边（1°），几乎不影响真实边。</summary>
        Conservative,
        /// <summary>标准：5°，覆盖多数地板三角面化的鬼影边。</summary>
        Standard,
        /// <summary>激进：15°，强力平滑，可能误压缓坡真实边。</summary>
        Aggressive,
    }

    /// <summary>SmartEdgeFilter 配置助手。</summary>
    public static class SmartEdgeFilterSettings
    {
        public const float DefaultThresholdRadians = 5f * MathF.PI / 180f;

        public static float PresetToRadians(EdgeFilterPreset preset) => preset switch
        {
            EdgeFilterPreset.Conservative => 1f * MathF.PI / 180f,
            EdgeFilterPreset.Standard => 5f * MathF.PI / 180f,
            EdgeFilterPreset.Aggressive => 15f * MathF.PI / 180f,
            _ => DefaultThresholdRadians,
        };

        public static float DegreesToRadians(float degrees) => degrees * MathF.PI / 180f;
        public static float RadiansToDegrees(float radians) => radians * 180f / MathF.PI;

        /// <summary>对世界中所有已注册动态体批量应用阈值。</summary>
        public static void ApplyToWorld(PhysicsWorld world, float angleThresholdRadians, bool enableFilter = true)
        {
            if (world == null) return;
            world.EnableEdgeFilter(enableFilter);
            foreach (uint bodyId in world.GetAllBodyIds())
            {
                // 仅对动态体设阈值（静态网格是"被碰撞"方，阈值设在与之交互的动态体上）
                if (world.IsBodyActive(bodyId))
                    world.SetEdgeFilterThreshold(bodyId, angleThresholdRadians);
            }
        }

        /// <summary>按预设批量应用。</summary>
        public static void ApplyPreset(PhysicsWorld world, EdgeFilterPreset preset)
            => ApplyToWorld(world, PresetToRadians(preset));
    }
}
