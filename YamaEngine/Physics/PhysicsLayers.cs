using System;

namespace YamaEngine.Physics
{
    // ════════════════════════════════════════════════════════════════════════
    // PhysicsLayers — 碰撞层常量 (兼容类型)
    //
    // 原引擎多处引用 PhysicsLayers.Default / All / Character / Enemy，
    // 但类型缺失。这里补齐最小可用定义，值与典型 16 位层掩码约定一致。
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>碰撞层 / 碰撞掩码常量。</summary>
    public static class PhysicsLayers
    {
        /// <summary>默认层 (位 0)。</summary>
        public const ushort Default = 1 << 0;

        /// <summary>角色层 (位 1)。</summary>
        public const ushort Character = 1 << 1;

        /// <summary>敌人层 (位 2)。</summary>
        public const ushort Enemy = 1 << 2;

        /// <summary>所有层（全 1 掩码）。</summary>
        public const ushort All = 0xFFFF;
    }
}
