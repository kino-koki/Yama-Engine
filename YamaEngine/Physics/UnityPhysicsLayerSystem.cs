using System;
using System.Runtime.CompilerServices;

namespace YamaEngine.Physics
{
    // ============================================================================================
    //  UnityPhysicsLayerSystem.cs
    //
    //  物理层级系统 — 类似 Unity Layer Collision Matrix。
    //  提供 32 个可配置物理层，每层可独立控制碰撞对象，通过位掩码实现高效过滤。
    //
    //  功能：
    //    - 32 个 PhysicsLayer 枚举值（0-31）
    //    - Layer Collision Matrix：每层可配置与哪些层碰撞
    //    - 层级查询过滤器（用于 Raycast/Overlap/ShapeCast）
    //    - 预设层级配置（Default/Player/NPC/Environment/Weapon/Trigger/Ragdoll/
    //      Vehicle/Cloth/Water/Raycast/UI 等）
    //    - GetCollisionMask(layer) / LayerCollides(layerA, layerB)
    //    - 与 Jolt 原生 Object Layer 的映射
    // ============================================================================================

    /// <summary>
    ///   物理层枚举。提供 32 个标准层（0-31）。
    ///   低 12 位为游戏常用预设层，高 20 位可由用户自定义。
    /// </summary>
    public enum PhysicsLayer : byte
    {
        /// <summary>默认层，通用动态物体。</summary>
        Default = 0,
        /// <summary>玩家角色。</summary>
        Player = 1,
        /// <summary>NPC / AI 角色。</summary>
        NPC = 2,
        /// <summary>环境/场景静态物体。</summary>
        Environment = 3,
        /// <summary>武器/投射物。</summary>
        Weapon = 4,
        /// <summary>触发器/传感器区域。</summary>
        Trigger = 5,
        /// <summary>布娃娃/角色死亡。</summary>
        Ragdoll = 6,
        /// <summary>载具。</summary>
        Vehicle = 7,
        /// <summary>布料模拟。</summary>
        Cloth = 8,
        /// <summary>水体/流体。</summary>
        Water = 9,
        /// <summary>光线投射专用层（仅在 Raycast/ShapeCast 中使用）。</summary>
        Raycast = 10,
        /// <summary>UI 交互层。</summary>
        UI = 11,
        // 12-31 为自定义层，由项目定义

        /// <summary>用户自定义层 0。</summary>
        Custom0 = 12,
        /// <summary>用户自定义层 1。</summary>
        Custom1 = 13,
        /// <summary>用户自定义层 2。</summary>
        Custom2 = 14,
        /// <summary>用户自定义层 3。</summary>
        Custom3 = 15,
        /// <summary>用户自定义层 4。</summary>
        Custom4 = 16,
        /// <summary>用户自定义层 5。</summary>
        Custom5 = 17,
        /// <summary>用户自定义层 6。</summary>
        Custom6 = 18,
        /// <summary>用户自定义层 7。</summary>
        Custom7 = 19,
        /// <summary>用户自定义层 8。</summary>
        Custom8 = 20,
        /// <summary>用户自定义层 9。</summary>
        Custom9 = 21,
        /// <summary>用户自定义层 10。</summary>
        Custom10 = 22,
        /// <summary>用户自定义层 11。</summary>
        Custom11 = 23,
        /// <summary>用户自定义层 12。</summary>
        Custom12 = 24,
        /// <summary>用户自定义层 13。</summary>
        Custom13 = 25,
        /// <summary>用户自定义层 14。</summary>
        Custom14 = 26,
        /// <summary>用户自定义层 15。</summary>
        Custom15 = 27,
        /// <summary>用户自定义层 16。</summary>
        Custom16 = 28,
        /// <summary>用户自定义层 17。</summary>
        Custom17 = 29,
        /// <summary>用户自定义层 18。</summary>
        Custom18 = 30,
        /// <summary>用户自定义层 19。</summary>
        Custom19 = 31,
    }

    /// <summary>
    ///   物理层级掩码工具。提供位掩码与层枚举之间的便捷转换。
    /// </summary>
    public static class PhysicsLayerMask
    {
        /// <summary>所有 32 层全部开启的掩码。</summary>
        public const uint Everything = 0xFFFFFFFF;

        /// <summary>所有层全部关闭的掩码。</summary>
        public const uint Nothing = 0x00000000;

        /// <summary>将单个物理层转换为位掩码。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static uint GetMask(PhysicsLayer layer) => 1u << (int)layer;

        /// <summary>将多个物理层合并为位掩码。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static uint GetMask(params PhysicsLayer[] layers)
        {
            uint mask = 0;
            for (int i = 0; i < layers.Length; i++)
                mask |= 1u << (int)layers[i];
            return mask;
        }

        /// <summary>将整数层索引 (0-31) 转换为位掩码。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static uint LayerToMask(int layerIndex)
        {
            if ((uint)layerIndex >= 32)
                throw new ArgumentOutOfRangeException(nameof(layerIndex), "Layer index must be 0-31.");
            return 1u << layerIndex;
        }

        /// <summary>检查位掩码中是否包含指定层。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool ContainsLayer(uint mask, PhysicsLayer layer) =>
            (mask & (1u << (int)layer)) != 0;

        /// <summary>检查位掩码中是否包含指定层索引。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool ContainsLayerIndex(uint mask, int layerIndex) =>
            (mask & (1u << layerIndex)) != 0;
    }

    /// <summary>
    ///   层级碰撞矩阵。管理 32 个物理层之间的碰撞关系，
    ///   与 Unity 的 Layer Collision Matrix 功能一致。
    ///   
    ///   每层维护一个 32 位掩码，标记该层应当与哪些层发生碰撞。
    ///   默认配置适用于大多数游戏场景。
    /// </summary>
    public sealed class PhysicsLayerCollisionMatrix
    {
        // 32 层的碰撞掩码数组
        private readonly uint[] _layerMasks;

        /// <summary>获取某层的碰撞掩码。</summary>
        public uint this[int layerIndex]
        {
            get => (uint)layerIndex < 32 ? _layerMasks[layerIndex] : 0;
            set
            {
                if ((uint)layerIndex < 32)
                    _layerMasks[layerIndex] = value;
            }
        }

        /// <summary>获取某层的碰撞掩码。</summary>
        public uint this[PhysicsLayer layer]
        {
            get => _layerMasks[(int)layer];
            set => _layerMasks[(int)layer] = value;
        }

        /// <summary>创建层级碰撞矩阵，使用默认配置。</summary>
        public PhysicsLayerCollisionMatrix()
        {
            _layerMasks = new uint[32];
            InitializeDefaults();
        }

        /// <summary>从现有掩码数组创建。</summary>
        public PhysicsLayerCollisionMatrix(uint[] masks)
        {
            if (masks == null || masks.Length != 32)
                throw new ArgumentException("Must provide exactly 32 layer masks.", nameof(masks));
            _layerMasks = (uint[])masks.Clone();
        }

        /// <summary>
        ///   初始化默认层级碰撞配置。
        ///   
        ///   规则概述：
        ///     - Default 与大多数层碰撞（除了特定忽略层）
        ///     - Player 与 Environment、Weapon、Vehicle 碰撞
        ///     - NPC 与 Environment、Player、Weapon 碰撞
        ///     - Environment（静态世界）与所有层碰撞
        ///     - Weapon 与 Default、Player、NPC、Environment、Vehicle 碰撞
        ///     - Trigger 与大多数层碰撞（触发事件，无物理响应）
        ///     - Water 仅与 Default、Player、NPC、Vehicle、Cloth 碰撞
        /// </summary>
        private void InitializeDefaults()
        {
            // 先全部置零
            for (int i = 0; i < 32; i++)
                _layerMasks[i] = 0;

            // Default: 与 Player, NPC, Environment, Weapon, Vehicle, Ragdoll, Cloth 碰撞
            _layerMasks[(int)PhysicsLayer.Default] = MakeMask(
                PhysicsLayer.Player, PhysicsLayer.NPC,
                PhysicsLayer.Environment, PhysicsLayer.Weapon,
                PhysicsLayer.Vehicle, PhysicsLayer.Ragdoll,
                PhysicsLayer.Cloth);

            // Player: 与 Default, NPC, Environment, Weapon, Vehicle, Trigger, Water
            _layerMasks[(int)PhysicsLayer.Player] = MakeMask(
                PhysicsLayer.Default, PhysicsLayer.NPC,
                PhysicsLayer.Environment, PhysicsLayer.Weapon,
                PhysicsLayer.Vehicle, PhysicsLayer.Trigger,
                PhysicsLayer.Water);

            // NPC: 与 Default, Player, Environment, Weapon, Vehicle, Trigger
            _layerMasks[(int)PhysicsLayer.NPC] = MakeMask(
                PhysicsLayer.Default, PhysicsLayer.Player,
                PhysicsLayer.Environment, PhysicsLayer.Weapon,
                PhysicsLayer.Vehicle, PhysicsLayer.Trigger);

            // Environment (静态): 与所有层碰撞 (除了 Raycast, UI)
            _layerMasks[(int)PhysicsLayer.Environment] = MakeMask(
                PhysicsLayer.Default, PhysicsLayer.Player,
                PhysicsLayer.NPC, PhysicsLayer.Weapon,
                PhysicsLayer.Trigger, PhysicsLayer.Ragdoll,
                PhysicsLayer.Vehicle, PhysicsLayer.Cloth,
                PhysicsLayer.Water);

            // Weapon: 与 Default, Player, NPC, Environment, Vehicle
            _layerMasks[(int)PhysicsLayer.Weapon] = MakeMask(
                PhysicsLayer.Default, PhysicsLayer.Player,
                PhysicsLayer.NPC, PhysicsLayer.Environment,
                PhysicsLayer.Vehicle);

            // Trigger: 与 Default, Player, NPC, Environment, Weapon, Vehicle, Ragdoll, Water
            _layerMasks[(int)PhysicsLayer.Trigger] = MakeMask(
                PhysicsLayer.Default, PhysicsLayer.Player,
                PhysicsLayer.NPC, PhysicsLayer.Environment,
                PhysicsLayer.Weapon, PhysicsLayer.Vehicle,
                PhysicsLayer.Ragdoll, PhysicsLayer.Water,
                PhysicsLayer.Cloth);

            // Ragdoll: 与 Default, Environment, Trigger, Water
            _layerMasks[(int)PhysicsLayer.Ragdoll] = MakeMask(
                PhysicsLayer.Default, PhysicsLayer.Environment,
                PhysicsLayer.Trigger, PhysicsLayer.Water);

            // Vehicle: 与 Default, Player, NPC, Environment, Weapon, Vehicle, Water
            _layerMasks[(int)PhysicsLayer.Vehicle] = MakeMask(
                PhysicsLayer.Default, PhysicsLayer.Player,
                PhysicsLayer.NPC, PhysicsLayer.Environment,
                PhysicsLayer.Weapon, PhysicsLayer.Vehicle,
                PhysicsLayer.Water);

            // Cloth: 与 Default, Environment, Trigger, Water, Cloth
            _layerMasks[(int)PhysicsLayer.Cloth] = MakeMask(
                PhysicsLayer.Default, PhysicsLayer.Environment,
                PhysicsLayer.Trigger, PhysicsLayer.Water,
                PhysicsLayer.Cloth);

            // Water: 与 Default, Player, NPC, Vehicle, Cloth, Ragdoll
            _layerMasks[(int)PhysicsLayer.Water] = MakeMask(
                PhysicsLayer.Default, PhysicsLayer.Player,
                PhysicsLayer.NPC, PhysicsLayer.Vehicle,
                PhysicsLayer.Cloth, PhysicsLayer.Ragdoll);

            // Raycast: 不与任何层碰撞（仅用于查询过滤）
            _layerMasks[(int)PhysicsLayer.Raycast] = 0;

            // UI: 不与任何层碰撞
            _layerMasks[(int)PhysicsLayer.UI] = 0;

            // 自定义层默认全部相互碰撞
            uint customMask = 0;
            for (int i = (int)PhysicsLayer.Custom0; i <= (int)PhysicsLayer.Custom19; i++)
                customMask |= 1u << i;

            for (int i = (int)PhysicsLayer.Custom0; i <= (int)PhysicsLayer.Custom19; i++)
                _layerMasks[i] = customMask;
        }

        /// <summary>
        ///   启用两层之间的碰撞。
        /// </summary>
        public void EnableCollision(PhysicsLayer layerA, PhysicsLayer layerB)
        {
            int a = (int)layerA;
            int b = (int)layerB;
            _layerMasks[a] |= 1u << b;
            _layerMasks[b] |= 1u << a;
        }

        /// <summary>
        ///   禁用两层之间的碰撞。
        /// </summary>
        public void DisableCollision(PhysicsLayer layerA, PhysicsLayer layerB)
        {
            int a = (int)layerA;
            int b = (int)layerB;
            _layerMasks[a] &= ~(1u << b);
            _layerMasks[b] &= ~(1u << a);
        }

        /// <summary>
        ///   设置某层与另一层的碰撞状态。
        /// </summary>
        public void SetCollision(PhysicsLayer layerA, PhysicsLayer layerB, bool collide)
        {
            if (collide) EnableCollision(layerA, layerB);
            else DisableCollision(layerA, layerB);
        }

        /// <summary>
        ///   检查两层之间是否允许碰撞。
        ///   碰撞矩阵是对称的：只要 A 允许与 B 或 B 允许与 A 碰撞即视为可碰撞。
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool LayerCollides(PhysicsLayer layerA, PhysicsLayer layerB)
        {
            uint maskA = _layerMasks[(int)layerA];
            uint maskB = _layerMasks[(int)layerB];
            uint bitA = 1u << (int)layerA;
            uint bitB = 1u << (int)layerB;
            return (maskA & bitB) != 0 || (maskB & bitA) != 0;
        }

        /// <summary>
        ///   检查两层之间是否允许碰撞（使用层索引）。
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool LayerCollides(int layerA, int layerB)
        {
            if ((uint)layerA >= 32 || (uint)layerB >= 32) return false;
            uint maskA = _layerMasks[layerA];
            uint maskB = _layerMasks[layerB];
            uint bitA = 1u << layerA;
            uint bitB = 1u << layerB;
            return (maskA & bitB) != 0 || (maskB & bitA) != 0;
        }

        /// <summary>
        ///   获取某层的碰撞掩码（哪些层与此层碰撞）。
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public uint GetCollisionMask(PhysicsLayer layer) => _layerMasks[(int)layer];

        /// <summary>
        ///   获取某层的碰撞掩码（使用层索引）。
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public uint GetCollisionMask(int layerIndex) =>
            (uint)layerIndex < 32 ? _layerMasks[layerIndex] : 0;

        /// <summary>
        ///   将碰撞矩阵转换为 Jolt 兼容的 Object Layer 映射配置。
        ///   每个 PhysicsLayer 对应一个 Jolt Object Layer。
        ///   返回每个层的碰撞掩码数组（与 Jolt 的 ObjectLayerPairFilter 配合使用）。
        /// </summary>
        internal ushort[] ToJoltLayerMasks()
        {
            var result = new ushort[32];
            for (int i = 0; i < 32; i++)
                result[i] = (ushort)(_layerMasks[i] & 0xFFFF);
            return result;
        }

        /// <summary>
        ///   将层掩码从 uint (32位) 转换为 ushort (16位)，供 Jolt 使用。
        ///   Jolt 使用 16 位 Object Layer，取低 16 位。
        /// </summary>
        internal static ushort ToJoltLayerMask(uint mask) => (ushort)(mask & 0xFFFF);

        /// <summary>
        ///   批量设置多个层的碰撞关系。
        /// </summary>
        public void ConfigureLayer(PhysicsLayer layer, uint collisionMask)
        {
            _layerMasks[(int)layer] = collisionMask;
        }

        /// <summary>
        ///   重置为默认配置。
        /// </summary>
        public void ResetToDefaults()
        {
            for (int i = 0; i < 32; i++)
                _layerMasks[i] = 0;
            InitializeDefaults();
        }

        /// <summary>
        ///   获取所有层的掩码数组（只读副本）。
        /// </summary>
        public uint[] GetAllMasks() => (uint[])_layerMasks.Clone();

        /// <summary>
        ///   将多个层合并为位掩码。
        /// </summary>
        private static uint MakeMask(params PhysicsLayer[] layers)
        {
            uint mask = 0;
            for (int i = 0; i < layers.Length; i++)
                mask |= 1u << (int)layers[i];
            return mask;
        }
    }
}
