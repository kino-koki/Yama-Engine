using System;
using System.Collections.Generic;
using System.Numerics;
using ShanyuEngine.ECS;
using ShanyuEngine.Physics;

namespace ShanyuEngine.AI
{
    // ════════════════════════════════════════════════════════════════════════
    //  SmartObjectsSystem.cs — 山雨引擎 智能物体系统（Smart Objects）
    //
    //  对齐 Unreal Engine 5 的 Smart Objects 子系统。在 UE5 中，Smart Objects 用于
    //  将世界中“可被 AI 交互的物体”（座位、掩体、岗哨、工作台、床位、商贩摊位……）
    //  注册到一个全局数据库（USmartObjectSubsystem），物体上定义若干“槽位”
    //  （FSmartObjectSlot），AI 通过“槽位查询”（FSmartObjectSlotQuery）找到合适的
    //  槽位并完成“认领（Claim）-> 使用（Perform）-> 释放（Release）”的三阶段交互。
    //
    //  本文件实现以下内容（全部位于 ShanyuEngine.AI 命名空间）：
    //    1. SmartObjectTag           —— 标签位掩码（[Flags]），对齐 UE5 的 GameplayTag 语义
    //    2. SmartObjectSlotState     —— 槽位状态机（Free/Occupied/Reserved/Disabled）
    //    3. SlotRef                  —— 槽位引用结构体（ObjectId + SlotIndex），对齐 FSmartObjectSlotHandle
    //    4. SmartObjectSlot          —— 智能物体槽位（世界变换 + 标签 + 状态 + 校验委托）
    //    5. SmartObjectEntry         —— 智能物体条目（一个物体，含多个槽位）
    //    6. SmartObjectSlotQuery     —— 槽位查询条件（标签/距离/视线/可达/排序）
    //    7. SmartObjectQueryResult   —— 查询结果
    //    8. SmartObjectDatabase      —— 智能物体数据库（注册/注销/查询/预留/释放，含空间哈希加速）
    //    9. SmartObjectComponent     —— ECS 组件（struct : IComponent），标记实体为智能物体
    //   10. SmartObjectUserComponent —— ECS 组件（struct : IComponent），记录使用者当前占用的槽位
    //   11. SmartObjectsSystem       —— ECS 系统（ISystem），每帧同步变换并清理失效占用
    //   12. ISmartObjectInteraction  —— 交互行为接口（Claim/Perform/Release 三阶段）
    //       SmartObjectInteractionBase —— 抽象基类，提供移动/朝向/动画等通用工具
    //   13. SitInteraction / CoverInteraction / PatrolSentryInteraction —— 内置交互行为
    //   14. BTTask_UseSmartObject    —— 行为树任务，查询并使用智能物体（异步 Running）
    //   15. SmartObjectDebugger      —— 调试快照，供编辑器绘制槽位位置与状态
    //
    //  与引擎系统对接：
    //    - ECS（ShanyuEngine.ECS.EntityComponentSystem）：通过 IComponent / ISystem 集成，
    //      使用 GetComponent/SetComponent/HasComponent/QueryWithComponent<T>() 等 API。
    //    - 导航（ShanyuEngine.AI.NavigationSystem）：FindPath / FindNearestPoly 用于可达性校验与移动。
    //    - 物理（ShanyuEngine.Physics.PhysicsWorld）：Raycast 用于视线校验。
    //    - 行为树（ShanyuEngine.AI.BTNode / BTContext / Blackboard）：BTTask_UseSmartObject 派生自 BTNode。
    // ════════════════════════════════════════════════════════════════════════

    // ──────────────────────────────────────────────────────────────────────
    // 标签（SmartObjectTag）
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 智能物体标签位掩码。使用 [Flags] 以支持位运算组合，对齐 UE5 中通过 GameplayTag
    /// 对智能物体/槽位进行分类的做法（此处用枚举位掩码简化，便于查询时按位筛选）。
    /// 一个槽位或物体可同时拥有多个标签，查询时通过 RequiredTags / ExcludedTags 过滤。
    /// </summary>
    [Flags]
    public enum SmartObjectTag : ulong
    {
        /// <summary>无标签。</summary>
        None    = 0,

        /// <summary>座位：椅子、长凳、马匹等可坐物体。</summary>
        Seat    = 1 << 0,
        /// <summary>掩体：矮墙、箱体等可在战斗中躲避的物体。</summary>
        Cover   = 1 << 1,
        /// <summary>岗哨：巡逻路径上的观察点，AI 到达后驻守并环顾。</summary>
        Sentry  = 1 << 2,
        /// <summary>工作：工作台、锻造炉、农田等可劳动物体。</summary>
        Work    = 1 << 3,
        /// <summary>睡眠：床铺、草席等可睡眠物体。</summary>
        Sleep   = 1 << 4,
        /// <summary>商贩：商店柜台、摊位，AI 可在此交易。</summary>
        Vendor  = 1 << 5,
        /// <summary>制作：炼金台、附魔台等制作类物体。</summary>
        Crafting = 1 << 6,
        /// <summary>治疗：医疗床、神龛等可恢复生命/状态的物体。</summary>
        Healing = 1 << 7,
        /// <summary>伏击：适合埋伏的隐蔽点。</summary>
        Ambush  = 1 << 8,
        /// <summary>自定义：留给项目扩展的通用标签位（可叠加多个 Custom 位）。</summary>
        Custom  = 1 << 9,

        /// <summary>全部标签（用于掩码运算）。</summary>
        All = ulong.MaxValue,
    }

    // ──────────────────────────────────────────────────────────────────────
    // 槽位状态（SmartObjectSlotState）
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 智能物体槽位状态。对齐 UE5 的 ESmartObjectSlotState：
    /// 槽位在 Free 时可被查询命中；被 AI 预留后进入 Reserved；实际开始交互时进入 Occupied；
    /// 被设计者禁用时进入 Disabled，不参与查询。
    /// </summary>
    public enum SmartObjectSlotState
    {
        /// <summary>空闲：可被查询并预留。</summary>
        Free = 0,
        /// <summary>占用：AI 正在执行交互。</summary>
        Occupied = 1,
        /// <summary>预留：AI 已预定但尚未抵达/开始交互。</summary>
        Reserved = 2,
        /// <summary>禁用：被设计者关闭，不参与查询。</summary>
        Disabled = 3,
    }

    // ──────────────────────────────────────────────────────────────────────
    // 槽位引用（SlotRef）
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 槽位引用结构体。通过“物体 ID + 槽位索引”全局唯一标识一个槽位，
    /// 对齐 UE5 的 FSmartObjectSlotHandle。作为值类型可在字典/集合中高效用作键。
    /// </summary>
    public readonly struct SlotRef : IEquatable<SlotRef>
    {
        /// <summary>所属智能物体的唯一 ID。</summary>
        public readonly int ObjectId;
        /// <summary>该物体内槽位的索引。</summary>
        public readonly int SlotIndex;

        public SlotRef(int objectId, int slotIndex)
        {
            ObjectId = objectId;
            SlotIndex = slotIndex;
        }

        /// <summary>是否为有效引用（ObjectId != 0）。</summary>
        public bool IsValid => ObjectId != 0;

        /// <summary>无效引用常量。</summary>
        public static SlotRef Invalid => default;

        public bool Equals(SlotRef other) => ObjectId == other.ObjectId && SlotIndex == other.SlotIndex;
        public override bool Equals(object? obj) => obj is SlotRef s && Equals(s);
        public override int GetHashCode() => HashCode.Combine(ObjectId, SlotIndex);
        public static bool operator ==(SlotRef a, SlotRef b) => a.Equals(b);
        public static bool operator !=(SlotRef a, SlotRef b) => !a.Equals(b);
        public override string ToString() => $"Slot[Obj={ObjectId},Idx={SlotIndex}]";
    }

    // ──────────────────────────────────────────────────────────────────────
    // 智能物体槽位（SmartObjectSlot）
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 智能物体槽位。一个智能物体可包含多个槽位（例如一张长凳有左右两个座位）。
    /// 槽位拥有自己的世界变换（AI 进入此位置进行交互）、标签、状态与校验委托。
    /// 对齐 UE5 的 FSmartObjectSlot。
    /// </summary>
    public sealed class SmartObjectSlot
    {
        // —— 作者态（相对于所属物体的局部变换）——
        // 槽位位置通常以局部偏移定义，物体移动后由数据库重算世界变换。

        /// <summary>槽位相对所属物体的局部位置。</summary>
        public Vector3 LocalPosition;
        /// <summary>槽位相对所属物体的局部朝向。</summary>
        public Quaternion LocalRotation = Quaternion.Identity;
        /// <summary>AI 入口相对物体的局部偏移（AI 应到达此处再开始交互）。</summary>
        public Vector3 LocalUserEntryOffset;
        /// <summary>AI 入口相对物体的局部朝向。</summary>
        public Quaternion LocalUserEntryRotation = Quaternion.Identity;

        // —— 运行态（由局部变换 + 物体世界变换计算得到）——

        /// <summary>槽位世界位置（AI 交互的目标位置）。</summary>
        public Vector3 WorldPosition;
        /// <summary>槽位世界朝向（AI 交互时应面向的方向）。</summary>
        public Quaternion WorldRotation = Quaternion.Identity;
        /// <summary>AI 进入时应到达的入口世界位置。</summary>
        public Vector3 UserEntryWorldPosition;
        /// <summary>AI 进入时应朝向的入口世界朝向。</summary>
        public Quaternion UserEntryWorldRotation = Quaternion.Identity;

        // —— 分类与状态 ——

        /// <summary>槽位标签（与物体级标签取并集参与查询）。</summary>
        public SmartObjectTag Tags = SmartObjectTag.None;
        /// <summary>槽位当前状态。</summary>
        public SmartObjectSlotState State = SmartObjectSlotState.Free;

        // —— 占用者 ——

        /// <summary>当前占用者实体 ID（-1 表示无）。当 MaxUsers>1 时为首个使用者。</summary>
        public int BoundEntityId = -1;
        /// <summary>最大同时使用数（默认 1）。</summary>
        public int MaxUsers = 1;
        /// <summary>当前使用者数量。</summary>
        public int UserCount;
        /// <summary>使用者实体列表（仅当 MaxUsers>1 时分配）。</summary>
        public List<int>? Users;

        // —— 优先级与校验 ——

        /// <summary>槽位优先级（数值越大越优先，用于 Priority 排序）。</summary>
        public float Priority;
        /// <summary>
        /// 自定义校验委托：参数为使用者实体 ID，返回 true 表示该校验通过。
        /// 用于表达“仅限某阵营”“仅限白天”等动态条件。对齐 UE5 的 Slot 校验注解。
        /// </summary>
        public Func<int, bool>? ValidationCondition;

        /// <summary>根据所属物体的世界变换重算槽位世界变换。由数据库在注册/移动时调用。</summary>
        internal void ApplyWorldTransform(in Vector3 ownerPos, in Quaternion ownerRot, in Vector3 ownerScale)
        {
            // 世界旋转 = 物体旋转 * 槽位局部旋转（父->子的标准组合）
            WorldRotation = ownerRot * LocalRotation;
            // 世界位置 = 物体位置 + 物体旋转 * (槽位局部位置 * 物体缩放)
            WorldPosition = ownerPos + Vector3.Transform(LocalPosition * ownerScale, ownerRot);

            UserEntryWorldRotation = ownerRot * LocalUserEntryRotation;
            UserEntryWorldPosition = ownerPos + Vector3.Transform(LocalUserEntryOffset * ownerScale, ownerRot);
        }

        /// <summary>重置占用状态为空闲。</summary>
        internal void ResetOccupancy()
        {
            BoundEntityId = -1;
            UserCount = 0;
            Users?.Clear();
            State = SmartObjectSlotState.Free;
        }
    }

    // ──────────────────────────────────────────────────────────────────────
    // 智能物体条目（SmartObjectEntry）
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 智能物体条目。注册到数据库的一个可交互物体，对齐 UE5 的 USmartObjectComponent
    /// 所描述的物体。包含物体级标签、世界变换、槽位列表与物体级校验委托。
    /// </summary>
    public sealed class SmartObjectEntry
    {
        /// <summary>物体唯一 ID（由数据库分配或作者指定）。0 表示尚未分配。</summary>
        public int ObjectId;
        /// <summary>所属 ECS 实体（物体本身），用于变换同步与销毁清理。</summary>
        public int OwningEntityId = -1;
        /// <summary>物体世界变换（可由 TransformComponent 同步）。</summary>
        public TransformComponent Transform;
        /// <summary>该物体的槽位列表。</summary>
        public List<SmartObjectSlot> Slots = new();
        /// <summary>物体级标签（与每个槽位标签取并集参与查询）。</summary>
        public SmartObjectTag Tags = SmartObjectTag.None;
        /// <summary>是否启用（禁用时整体不参与查询）。</summary>
        public bool Enabled = true;
        /// <summary>物体级校验委托（参数为使用者实体 ID）。</summary>
        public Func<int, bool>? ValidationCondition;

        /// <summary>缓存：该物体当前在空间哈希中占据的格子键集合（用于移动后重定位）。</summary>
        internal HashSet<long>? OccupiedCells;
    }

    // ──────────────────────────────────────────────────────────────────────
    // 查询条件与结果
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>查询结果排序模式，对齐 UE5 的 ESmartObjectSlotQuerySortMode。</summary>
    public enum SmartObjectQuerySortMode
    {
        /// <summary>按到查询者距离升序。</summary>
        Nearest,
        /// <summary>随机顺序（适合分散负载、避免群体同时涌向同一槽位）。</summary>
        Random,
        /// <summary>按槽位优先级降序。</summary>
        Priority,
    }

    /// <summary>
    /// 智能物体槽位查询条件。对齐 UE5 的 FSmartObjectSlotQuery。
    /// 描述“在何处、找具备哪些标签、满足何种空间/可达约束的槽位”。
    /// </summary>
    public sealed class SmartObjectSlotQuery
    {
        /// <summary>必须包含的标签（槽位标签 ∪ 物体标签 须包含全部 RequiredTags）。</summary>
        public SmartObjectTag RequiredTags = SmartObjectTag.None;
        /// <summary>排除的标签（命中任一 ExcludedTags 即排除）。</summary>
        public SmartObjectTag ExcludedTags = SmartObjectTag.None;
        /// <summary>到查询者的最大距离。</summary>
        public float MaxDistance = float.MaxValue;
        /// <summary>是否需要视线（用 PhysicsWorld.Raycast 校验，中间无遮挡）。</summary>
        public bool RequireLineOfSight;
        /// <summary>是否需要可到达（用 NavigationSystem.FindPath != null 校验）。</summary>
        public bool RequireReachable;
        /// <summary>排序模式。</summary>
        public SmartObjectQuerySortMode SortMode = SmartObjectQuerySortMode.Nearest;
        /// <summary>最大返回数（0 表示不限）。</summary>
        public int MaxResults = 1;
        /// <summary>查询者实体 ID（用于评估 ValidationCondition）。</summary>
        public int QuerierEntityId = -1;
    }

    /// <summary>
    /// 查询结果。包含槽位引用、所属物体 ID、槽位世界位置与到查询者的距离。
    /// </summary>
    public readonly struct SmartObjectQueryResult
    {
        /// <summary>槽位引用。</summary>
        public readonly SlotRef Slot;
        /// <summary>所属物体 ID。</summary>
        public readonly int ObjectId;
        /// <summary>槽位世界位置。</summary>
        public readonly Vector3 WorldPosition;
        /// <summary>到查询者的距离。</summary>
        public readonly float Distance;

        public SmartObjectQueryResult(SlotRef slot, int objectId, Vector3 worldPosition, float distance)
        {
            Slot = slot;
            ObjectId = objectId;
            WorldPosition = worldPosition;
            Distance = distance;
        }

        /// <summary>是否有效。</summary>
        public bool IsValid => ObjectId != 0;
    }

    // ════════════════════════════════════════════════════════════════════════
    // 智能物体数据库（SmartObjectDatabase）
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 智能物体数据库。对齐 UE5 的 USmartObjectSubsystem：
    /// 维护所有已注册的智能物体条目，提供注册/注销/查询/预留/释放接口。
    /// 内部使用 XZ 平面空间哈希分桶，查询时仅检查查询者附近的桶，以加速大规模场景。
    /// </summary>
    public sealed class SmartObjectDatabase
    {
        // —— 存储 ——
        private readonly Dictionary<int, SmartObjectEntry> _entries = new();
        // 空间哈希：格子键 -> 该格子内的槽位引用列表
        private readonly Dictionary<long, List<SlotRef>> _slotBuckets = new();
        // 当前处于 Reserved/Occupied 的槽位集合（用于失效占用者清理）
        private readonly HashSet<SlotRef> _activeSlots = new();

        private int _nextObjectId = 1;
        private readonly float _cellSize;

        // —— 外部依赖（用于视线/可达校验，由 SmartObjectsSystem 或调用方绑定）——
        private PhysicsWorld? _physics;
        private NavigationSystem? _navigation;

        /// <summary>
        /// 当前活跃数据库实例（便于行为树任务等无直接引用的代码获取）。
        /// 通常由 SmartObjectsSystem 在初始化时设置。
        /// </summary>
        public static SmartObjectDatabase? Active { get; set; }

        /// <summary>空间哈希格子大小（米）。</summary>
        public float CellSize => _cellSize;

        /// <summary>已注册物体数量。</summary>
        public int ObjectCount => _entries.Count;

        /// <summary>构造数据库，可指定空间哈希格子大小（默认 16 米）。</summary>
        public SmartObjectDatabase(float cellSize = 16f)
        {
            _cellSize = Math.Max(1f, cellSize);
        }

        // ── 依赖绑定 ──────────────────────────────────────────────

        /// <summary>绑定物理世界（启用 RequireLineOfSight 校验）。</summary>
        public void BindPhysics(PhysicsWorld physics) => _physics = physics;

        /// <summary>绑定导航系统（启用 RequireReachable 校验）。</summary>
        public void BindNavigation(NavigationSystem navigation) => _navigation = navigation;

        // ── 注册 / 注销 ───────────────────────────────────────────

        /// <summary>
        /// 注册一个智能物体条目。若 entry.ObjectId 未设置（<=0），由数据库分配新 ID。
        /// 返回最终使用的物体 ID。
        /// </summary>
        public int Register(SmartObjectEntry entry)
        {
            if (entry == null) return 0;
            if (entry.ObjectId <= 0)
                entry.ObjectId = _nextObjectId++;
            else
                _nextObjectId = Math.Max(_nextObjectId, entry.ObjectId + 1);

            _entries[entry.ObjectId] = entry;
            RecomputeWorldTransforms(entry);
            AddToBuckets(entry);
            return entry.ObjectId;
        }

        /// <summary>注销指定物体，并释放其所有槽位占用。</summary>
        public void Unregister(int objectId)
        {
            if (!_entries.TryGetValue(objectId, out var entry)) return;
            RemoveFromBuckets(entry);
            // 释放该物体所有处于活跃状态的槽位引用
            for (int i = 0; i < entry.Slots.Count; i++)
            {
                var sr = new SlotRef(objectId, i);
                _activeSlots.Remove(sr);
            }
            _entries.Remove(objectId);
        }

        /// <summary>物体移动后同步变换：重算所有槽位世界变换并重新分桶。</summary>
        public void UpdateTransform(int objectId, TransformComponent transform)
        {
            if (!_entries.TryGetValue(objectId, out var entry)) return;
            entry.Transform = transform;
            RecomputeWorldTransforms(entry);
            RemoveFromBuckets(entry);
            AddToBuckets(entry);
        }

        /// <summary>根据 SlotRef 取得槽位（不存在返回 null）。</summary>
        public SmartObjectSlot? GetSlot(SlotRef slotRef)
        {
            if (!_entries.TryGetValue(slotRef.ObjectId, out var entry)) return null;
            if (slotRef.SlotIndex < 0 || slotRef.SlotIndex >= entry.Slots.Count) return null;
            return entry.Slots[slotRef.SlotIndex];
        }

        /// <summary>根据 SlotRef 取得所属条目（不存在返回 null）。</summary>
        public SmartObjectEntry? GetEntry(int objectId)
            => _entries.TryGetValue(objectId, out var e) ? e : null;

        /// <summary>枚举所有已注册物体的 ID（供调试器与编辑器遍历）。</summary>
        public IEnumerable<int> EnumerateObjectIds() => _entries.Keys;

        /// <summary>设置槽位状态（例如从 Reserved 切到 Occupied）。</summary>
        public void SetSlotState(SlotRef slotRef, SmartObjectSlotState state)
        {
            var slot = GetSlot(slotRef);
            if (slot == null) return;
            var oldState = slot.State;
            slot.State = state;
            // 维护活跃槽位集合
            if (state == SmartObjectSlotState.Free || state == SmartObjectSlotState.Disabled)
                _activeSlots.Remove(slotRef);
            else if (oldState == SmartObjectSlotState.Free)
                _activeSlots.Add(slotRef);
        }

        // ── 预留 / 释放 ──────────────────────────────────────────

        /// <summary>
        /// 为指定使用者预留槽位。要求槽位为 Free 且有剩余容量。
        /// 成功后状态变为 Reserved。返回是否成功。
        /// </summary>
        public bool Reserve(SlotRef slotRef, int userId)
        {
            var slot = GetSlot(slotRef);
            if (slot == null) return false;
            if (slot.State == SmartObjectSlotState.Disabled) return false;
            if (slot.UserCount >= slot.MaxUsers) return false;

            if (slot.MaxUsers > 1)
            {
                slot.Users ??= new List<int>();
                if (slot.Users.Contains(userId)) return false; // 已预留
                slot.Users.Add(userId);
            }
            slot.UserCount++;
            if (slot.BoundEntityId == -1)
                slot.BoundEntityId = userId;
            slot.State = SmartObjectSlotState.Reserved;
            _activeSlots.Add(slotRef);
            return true;
        }

        /// <summary>
        /// 释放指定使用者对槽位的占用/预留。当使用者数量归零时槽位恢复 Free。
        /// 返回是否成功（槽位存在且确有该使用者）。
        /// </summary>
        public bool Release(SlotRef slotRef, int userId)
        {
            var slot = GetSlot(slotRef);
            if (slot == null) return false;

            bool removed;
            if (slot.MaxUsers > 1 && slot.Users != null)
                removed = slot.Users.Remove(userId);
            else
                removed = slot.BoundEntityId == userId;

            if (!removed) return false;

            slot.UserCount--;
            if (slot.UserCount <= 0)
            {
                slot.ResetOccupancy();
                _activeSlots.Remove(slotRef);
            }
            else
            {
                // 仍有其他使用者，更新主占用者为列表中第一个
                if (slot.Users != null && slot.Users.Count > 0)
                    slot.BoundEntityId = slot.Users[0];
                else if (slot.BoundEntityId == userId)
                    slot.BoundEntityId = -1;
            }
            return true;
        }

        // ── 失效占用者清理 ────────────────────────────────────────

        /// <summary>
        /// 清理已不存在的占用者：遍历所有活跃槽位，若某使用者实体已不存在（谓词返回 false），
        /// 则释放其对槽位的占用。由 SmartObjectsSystem 每帧调用，传入 ecs.HasEntity。
        /// </summary>
        public void CleanupDestroyedUsers(Predicate<int> entityExists)
        {
            if (_activeSlots.Count == 0) return;
            // 复制一份避免在迭代中修改集合
            var snapshot = new List<SlotRef>(_activeSlots);
            foreach (var sr in snapshot)
            {
                var slot = GetSlot(sr);
                if (slot == null) { _activeSlots.Remove(sr); continue; }

                if (slot.MaxUsers > 1 && slot.Users != null)
                {
                    for (int i = slot.Users.Count - 1; i >= 0; i--)
                    {
                        int uid = slot.Users[i];
                        if (!entityExists(uid))
                        {
                            slot.Users.RemoveAt(i);
                            slot.UserCount--;
                        }
                    }
                    if (slot.UserCount <= 0)
                    {
                        slot.ResetOccupancy();
                        _activeSlots.Remove(sr);
                    }
                    else
                    {
                        slot.BoundEntityId = slot.Users[0];
                    }
                }
                else
                {
                    if (slot.BoundEntityId != -1 && !entityExists(slot.BoundEntityId))
                    {
                        slot.ResetOccupancy();
                        _activeSlots.Remove(sr);
                    }
                }
            }
        }

        // ── 查询 ─────────────────────────────────────────────────

        /// <summary>
        /// 查询符合条件且距离 querierPos 最近的槽位列表。
        /// 使用空间哈希仅检查附近格子；依次进行标签、状态、距离、校验委托、视线、可达过滤，
        /// 最后按 SortMode 排序并截断到 MaxResults。
        /// </summary>
        public List<SmartObjectQueryResult> QuerySlots(SmartObjectSlotQuery query, Vector3 querierPos)
        {
            var results = new List<SmartObjectQueryResult>();
            float maxDist = query.MaxDistance;
            int radius = (int)Math.Ceiling(maxDist / _cellSize);
            int qcxi = FloorCell(querierPos.X);
            int qcz = FloorCell(querierPos.Z);

            for (int dx = -radius; dx <= radius; dx++)
            {
                for (int dz = -radius; dz <= radius; dz++)
                {
                    long key = PackCell(qcxi + dx, qcz + dz);
                    if (!_slotBuckets.TryGetValue(key, out var bucket)) continue;

                    for (int i = 0; i < bucket.Count; i++)
                    {
                        var sr = bucket[i];
                        if (!_entries.TryGetValue(sr.ObjectId, out var entry)) continue;
                        if (!entry.Enabled) continue;
                        if (sr.SlotIndex < 0 || sr.SlotIndex >= entry.Slots.Count) continue;

                        var slot = entry.Slots[sr.SlotIndex];

                        // 标签过滤：槽位标签 ∪ 物体标签
                        SmartObjectTag combined = entry.Tags | slot.Tags;
                        if ((combined & query.RequiredTags) != query.RequiredTags) continue;
                        if ((combined & query.ExcludedTags) != 0) continue;

                        // 状态过滤：仅 Free 槽位可被命中
                        if (slot.State != SmartObjectSlotState.Free) continue;

                        // 距离过滤（使用 XZ 平面距离，符合典型 AI 寻路场景）
                        float d = HorizontalDistance(querierPos, slot.WorldPosition);
                        if (d > maxDist) continue;

                        // 校验委托（物体级 + 槽位级）
                        if (entry.ValidationCondition != null && !entry.ValidationCondition(query.QuerierEntityId))
                            continue;
                        if (slot.ValidationCondition != null && !slot.ValidationCondition(query.QuerierEntityId))
                            continue;

                        // 视线校验
                        if (query.RequireLineOfSight && !HasLineOfSight(querierPos, slot.WorldPosition))
                            continue;

                        // 可达校验
                        if (query.RequireReachable && !IsReachable(querierPos, slot.WorldPosition))
                            continue;

                        results.Add(new SmartObjectQueryResult(sr, entry.ObjectId, slot.WorldPosition, d));
                    }
                }
            }

            // 排序
            switch (query.SortMode)
            {
                case SmartObjectQuerySortMode.Nearest:
                    results.Sort((a, b) => a.Distance.CompareTo(b.Distance));
                    break;
                case SmartObjectQuerySortMode.Priority:
                    results.Sort((a, b) => GetSlotPriority(b.Slot).CompareTo(GetSlotPriority(a.Slot)));
                    break;
                case SmartObjectQuerySortMode.Random:
                    Shuffle(results);
                    break;
            }

            // 截断
            if (query.MaxResults > 0 && results.Count > query.MaxResults)
                results.RemoveRange(query.MaxResults, results.Count - query.MaxResults);

            return results;
        }

        // ── 内部工具：视线 / 可达 ────────────────────────────────

        /// <summary>视线校验：从 from 到 to 做射线，若中途有遮挡则返回 false。</summary>
        private bool HasLineOfSight(Vector3 from, Vector3 to)
        {
            if (_physics == null) return true; // 未绑定物理则默认通过
            Vector3 delta = to - from;
            float dist = delta.Length();
            if (dist < 0.01f) return true;
            // 略微缩短以避免命中目标自身的物体
            var hit = _physics.Raycast(from, delta, dist - 0.5f,
                PhysicsLayers.Default, PhysicsLayers.All);
            return !hit.HasHit;
        }

        /// <summary>可达校验：导航系统能找到路径即视为可达。</summary>
        private bool IsReachable(Vector3 from, Vector3 to)
        {
            if (_navigation == null) return true; // 未绑定导航则默认通过
            var path = _navigation.FindPath(from, to);
            return path != null && path.Count > 0;
        }

        /// <summary>取得槽位优先级（用于 Priority 排序）。</summary>
        private float GetSlotPriority(SlotRef sr)
        {
            var slot = GetSlot(sr);
            return slot?.Priority ?? 0f;
        }

        // ── 内部工具：空间哈希 ───────────────────────────────────

        private int FloorCell(float v) => (int)MathF.Floor(v / _cellSize);

        /// <summary>将 (cellX, cellZ) 打包为 64 位键。</summary>
        private static long PackCell(int cx, int cz)
        {
            return ((long)(uint)cx << 32) | (uint)cz;
        }

        /// <summary>重算某条目所有槽位的世界变换。</summary>
        private static void RecomputeWorldTransforms(SmartObjectEntry entry)
        {
            Vector3 pos = entry.Transform.Position;
            Quaternion rot = entry.Transform.Rotation;
            Vector3 scale = entry.Transform.Scale;
            for (int i = 0; i < entry.Slots.Count; i++)
                entry.Slots[i].ApplyWorldTransform(pos, rot, scale);
        }

        /// <summary>将该条目的所有槽位加入空间哈希桶。</summary>
        private void AddToBuckets(SmartObjectEntry entry)
        {
            entry.OccupiedCells ??= new HashSet<long>();
            for (int i = 0; i < entry.Slots.Count; i++)
            {
                var slot = entry.Slots[i];
                long key = PackCell(FloorCell(slot.WorldPosition.X), FloorCell(slot.WorldPosition.Z));
                if (!_slotBuckets.TryGetValue(key, out var bucket))
                {
                    bucket = new List<SlotRef>();
                    _slotBuckets[key] = bucket;
                }
                bucket.Add(new SlotRef(entry.ObjectId, i));
                entry.OccupiedCells.Add(key);
            }
        }

        /// <summary>从空间哈希桶中移除该条目的所有槽位。</summary>
        private void RemoveFromBuckets(SmartObjectEntry entry)
        {
            if (entry.OccupiedCells == null) return;
            foreach (long key in entry.OccupiedCells)
            {
                if (_slotBuckets.TryGetValue(key, out var bucket))
                    bucket.RemoveAll(sr => sr.ObjectId == entry.ObjectId);
            }
            entry.OccupiedCells.Clear();
        }

        /// <summary>XZ 平面距离（忽略 Y）。</summary>
        private static float HorizontalDistance(Vector3 a, Vector3 b)
        {
            float dx = a.X - b.X;
            float dz = a.Z - b.Z;
            return MathF.Sqrt(dx * dx + dz * dz);
        }

        /// <summary>Fisher-Yates 洗牌。</summary>
        private static void Shuffle<T>(List<T> list)
        {
            var rng = new Random();
            int n = list.Count;
            while (n > 1)
            {
                n--;
                int k = rng.Next(n + 1);
                (list[k], list[n]) = (list[n], list[k]);
            }
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // ECS 组件（SmartObjectComponent / SmartObjectUserComponent）
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 智能物体组件。挂载到“作为智能物体”的 ECS 实体上，标记该实体为可交互物体，
    /// 并缓存其 ObjectId 与物体级标签。对齐 UE5 的 USmartObjectComponent。
    /// 注意：引擎约束要求组件为 struct : IComponent。
    /// </summary>
    public struct SmartObjectComponent : IComponent
    {
        /// <summary>在数据库中注册的物体 ID（0 表示尚未注册）。</summary>
        public int ObjectId;
        /// <summary>物体级标签缓存（与数据库条目保持同步，便于快速过滤）。</summary>
        public SmartObjectTag Tags;
        /// <summary>是否启用。</summary>
        public bool Enabled;
    }

    /// <summary>
    /// 智能物体使用者组件。挂载到“使用智能物体”的 AI 实体上，记录当前占用的槽位引用。
    /// 对齐 UE5 中 AI 使用 Smart Object 时的运行时绑定关系。
    /// 注意：引擎约束要求组件为 struct : IComponent。
    /// </summary>
    public struct SmartObjectUserComponent : IComponent
    {
        /// <summary>当前占用的槽位引用（Invalid 表示未占用）。</summary>
        public SlotRef OccupiedSlot;
        /// <summary>交互是否正在进行。</summary>
        public bool IsInteracting;

        /// <summary>是否正占用某槽位。</summary>
        public bool IsOccupied => OccupiedSlot.IsValid;
    }

    // ════════════════════════════════════════════════════════════════════════
    // ECS 系统（SmartObjectsSystem）
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 智能物体系统。实现 ECS 的 ISystem 接口，对齐 UE5 的 SmartObjectsSubsystem 运行时职责：
    ///   1. 每帧将所有 SmartObjectComponent 实体的 TransformComponent 同步到数据库（物体移动后更新槽位世界变换）；
    ///   2. 清理已销毁占用者：若占用某槽位的 AI 实体被销毁，自动释放槽位；
    ///   3. 清理已销毁物体：若智能物体实体被销毁，从数据库注销其条目。
    /// 该系统拥有一个 SmartObjectDatabase 实例，供行为树任务与游戏逻辑查询使用。
    /// </summary>
    public sealed class SmartObjectsSystem : ISystem
    {
        /// <summary>系统名称。</summary>
        public string Name => "SmartObjectsSystem";

        /// <summary>该系统拥有并维护的智能物体数据库。</summary>
        public SmartObjectDatabase Database { get; }

        // 已知的“实体 -> ObjectId”映射，用于检测物体实体被销毁
        private readonly Dictionary<int, int> _entityToObject = new();

        /// <summary>构造系统，创建内部数据库并设为 Active。</summary>
        public SmartObjectsSystem() : this(cellSize: 16f) { }

        /// <summary>构造系统并指定空间哈希格子大小。</summary>
        public SmartObjectsSystem(float cellSize)
        {
            Database = new SmartObjectDatabase(cellSize);
            SmartObjectDatabase.Active = Database;
        }

        /// <summary>绑定物理世界（启用查询的视线校验）。</summary>
        public void BindPhysics(PhysicsWorld physics) => Database.BindPhysics(physics);

        /// <summary>绑定导航系统（启用查询的可达校验）。</summary>
        public void BindNavigation(NavigationSystem navigation) => Database.BindNavigation(navigation);

        /// <summary>
        /// 将一个 ECS 实体注册为智能物体：在数据库中创建条目并挂载 SmartObjectComponent。
        /// 返回分配的 ObjectId。
        /// </summary>
        public int RegisterObject(EntityComponentSystem ecs, int entityId, SmartObjectTag tags,
            List<SmartObjectSlot>? slots = null, Func<int, bool>? validation = null)
        {
            if (!ecs.HasEntity(entityId)) return 0;
            var tf = ecs.GetComponent<TransformComponent>(entityId);

            var entry = new SmartObjectEntry
            {
                OwningEntityId = entityId,
                Transform = tf,
                Tags = tags,
                Enabled = true,
                ValidationCondition = validation,
            };
            if (slots != null) entry.Slots = slots;

            int objectId = Database.Register(entry);
            _entityToObject[entityId] = objectId;

            ecs.AddComponent(entityId, new SmartObjectComponent
            {
                ObjectId = objectId,
                Tags = tags,
                Enabled = true,
            });
            return objectId;
        }

        /// <summary>每帧更新：同步变换、清理失效占用与失效物体。</summary>
        public void Update(float dt, EntityComponentSystem ecs)
        {
            // 1) 同步所有 SmartObjectComponent 实体的变换到数据库
            var smartEntities = ecs.QueryWithComponent<SmartObjectComponent>();
            var currentSet = new HashSet<int>();
            foreach (int eid in smartEntities)
            {
                currentSet.Add(eid);
                var comp = ecs.GetComponent<SmartObjectComponent>(eid);
                if (!comp.Enabled) continue;
                if (comp.ObjectId <= 0) continue;
                if (!ecs.HasComponent<TransformComponent>(eid)) continue;

                var tf = ecs.GetComponent<TransformComponent>(eid);
                Database.UpdateTransform(comp.ObjectId, tf);

                // 记录映射（首次发现或恢复时）
                if (!_entityToObject.ContainsKey(eid))
                    _entityToObject[eid] = comp.ObjectId;
            }

            // 2) 清理已被销毁的物体实体（曾在映射中但本帧不存在）
            if (_entityToObject.Count > 0)
            {
                var stale = new List<int>();
                foreach (var kvp in _entityToObject)
                {
                    if (!ecs.HasEntity(kvp.Key))
                        stale.Add(kvp.Key);
                }
                foreach (int deadEntity in stale)
                {
                    if (_entityToObject.TryGetValue(deadEntity, out int objectId))
                        Database.Unregister(objectId);
                    _entityToObject.Remove(deadEntity);
                }
            }

            // 3) 清理已销毁占用者：若占用槽位的 AI 实体已不存在，释放槽位
            Database.CleanupDestroyedUsers(id => ecs.HasEntity(id));
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // 交互行为（ISmartObjectInteraction / SmartObjectInteractionBase）
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 智能物体交互行为接口。对齐 UE5 Smart Object 的交互三阶段：
    ///   - Claim（认领）：AI 预留槽位并初始化交互状态；
    ///   - Perform（执行，对应 Tick）：每帧推进交互，返回 Running/Success/Failed；
    ///   - Release（释放）：交互结束（成功/失败/中止）时清理状态并释放槽位。
    /// </summary>
    public interface ISmartObjectInteraction
    {
        /// <summary>认领阶段：槽位已被预留，初始化交互（重置内部状态机）。</summary>
        void OnClaim(BTContext ctx, in SlotRef slot, SmartObjectDatabase db);

        /// <summary>执行阶段：每帧推进交互，返回当前状态。</summary>
        BTNodeStatus Tick(BTContext ctx, in SlotRef slot, SmartObjectDatabase db);

        /// <summary>释放阶段：清理状态。无论交互以何种方式结束都会调用。</summary>
        void OnRelease(BTContext ctx, in SlotRef slot, SmartObjectDatabase db);
    }

    /// <summary>
    /// 交互行为抽象基类。提供默认的 OnClaim/OnRelease 实现以及移动、朝向、动画等
    /// 通用工具方法，供内置交互行为复用。
    /// </summary>
    public abstract class SmartObjectInteractionBase : ISmartObjectInteraction
    {
        /// <summary>认领阶段默认实现：将槽位状态切到 Occupied。</summary>
        public virtual void OnClaim(BTContext ctx, in SlotRef slot, SmartObjectDatabase db)
        {
            db.SetSlotState(slot, SmartObjectSlotState.Occupied);
        }

        /// <summary>执行阶段：由子类实现。</summary>
        public abstract BTNodeStatus Tick(BTContext ctx, in SlotRef slot, SmartObjectDatabase db);

        /// <summary>释放阶段默认实现：恢复 Idle 动画并清除动画标志。</summary>
        public virtual void OnRelease(BTContext ctx, in SlotRef slot, SmartObjectDatabase db)
        {
            // 通过黑板通知动画系统回到 Idle（与 BTTaskPlayAnimation 的黑板约定一致）
            ctx.Blackboard.SetString("CurrentAnimation", "Idle");
            ctx.Blackboard.SetBool("AnimationLooping", true);
        }

        // ── 通用工具：移动 ───────────────────────────────────────

        /// <summary>
        /// 沿导航路径或直线移动实体到 target。返回 true 表示已抵达。
        /// 复用 NavigationSystem.FindPath 与 ECS TransformComponent 写回。
        /// </summary>
        protected static bool MoveToPosition(BTContext ctx, Vector3 target, float speed, float acceptance,
            ref List<Vector3>? path, ref int pathIndex)
        {
            if (ctx.ECS == null || !ctx.ECS.HasEntity(ctx.EntityId)) return true;
            var tf = ctx.ECS.GetComponent<TransformComponent>(ctx.EntityId);

            // 首次或目标变化时规划路径
            if (path == null && ctx.Navigation != null)
            {
                path = ctx.Navigation.FindPath(tf.Position, target);
                pathIndex = 0;
            }

            Vector3 waypoint;
            bool usePath = path != null && path.Count > 0;
            if (usePath)
            {
                if (pathIndex >= path!.Count) return true;
                waypoint = path[pathIndex];
            }
            else
            {
                // 无导航或无路径：直线前往
                waypoint = target;
            }

            Vector3 toWp = waypoint - tf.Position;
            toWp.Y = 0f; // 水平面移动
            float dist = toWp.Length();

            if (dist < acceptance)
            {
                if (usePath)
                {
                    pathIndex++;
                    if (pathIndex >= path!.Count) return true;
                }
                else
                {
                    return true;
                }
            }
            else
            {
                Vector3 dir = Vector3.Normalize(toWp);
                tf.Position += dir * speed * ctx.DeltaTime;
                // 朝向移动方向（Forward = -Z，故 yaw = atan2(dir.X, -dir.Z)）
                tf.Rotation = Quaternion.CreateFromYawPitchRoll(
                    MathF.Atan2(dir.X, -dir.Z), 0f, 0f);
                ctx.ECS.SetComponent(ctx.EntityId, tf);
            }
            return false;
        }

        // ── 通用工具：朝向 ───────────────────────────────────────

        /// <summary>
        /// 平滑转向目标朝向 quaternion。返回 true 表示已对齐（角度差小于阈值）。
        /// </summary>
        protected static bool FaceRotation(BTContext ctx, Quaternion targetRot, float turnSpeed)
        {
            if (ctx.ECS == null || !ctx.ECS.HasEntity(ctx.EntityId)) return true;
            var tf = ctx.ECS.GetComponent<TransformComponent>(ctx.EntityId);
            float t = Math.Clamp(turnSpeed * ctx.DeltaTime, 0f, 1f);
            tf.Rotation = Quaternion.Slerp(tf.Rotation, targetRot, t);
            ctx.ECS.SetComponent(ctx.EntityId, tf);

            // 计算当前 Forward 与目标 Forward 的夹角
            Vector3 curF = NormalizeXZ(tf.Forward);
            Vector3 tgtF = NormalizeXZ(Vector3.Transform(new Vector3(0, 0, -1), targetRot));
            float dot = Math.Clamp(Vector3.Dot(curF, tgtF), -1f, 1f);
            return MathF.Acos(dot) < 0.1f;
        }

        /// <summary>转向某一世界方向（水平面）。</summary>
        protected static bool FaceDirection(BTContext ctx, Vector3 direction, float turnSpeed)
        {
            Vector3 d = new(direction.X, 0f, direction.Z);
            if (d.LengthSquared() < 1e-6f) return true;
            d = Vector3.Normalize(d);
            Quaternion targetRot = Quaternion.CreateFromYawPitchRoll(MathF.Atan2(d.X, -d.Z), 0f, 0f);
            return FaceRotation(ctx, targetRot, turnSpeed);
        }

        // ── 通用工具：动画 ───────────────────────────────────────

        /// <summary>通过黑板通知动画系统播放指定动画。</summary>
        protected static void PlayAnimation(Blackboard bb, string animName, bool loop, float duration = 0f)
        {
            bb.SetString("CurrentAnimation", animName);
            bb.SetBool("AnimationLooping", loop);
            if (duration > 0f)
                bb.SetFloat("AnimationDuration", duration);
        }

        /// <summary>归一化 XZ 向量。</summary>
        protected static Vector3 NormalizeXZ(Vector3 v)
        {
            Vector3 xz = new(v.X, 0f, v.Z);
            float len = xz.Length();
            return len > 1e-6f ? xz / len : Vector3.UnitZ;
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // 内置交互行为
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 坐下交互。阶段：走到入口 -> 朝向槽位 -> 播放坐下 -> 保持坐姿 -> 起身。
    /// 对齐 UE5 Smart Object 的“座位”典型用法。
    /// </summary>
    public sealed class SitInteraction : SmartObjectInteractionBase
    {
        private enum Phase { MoveToSlot, FaceSlot, SitDown, Sitting, StandUp, Done }
        private Phase _phase;
        private float _timer;
        private List<Vector3>? _path;
        private int _pathIndex;

        /// <summary>移动速度（米/秒）。</summary>
        public float MoveSpeed = 3.5f;
        /// <summary>到达判定半径。</summary>
        public float AcceptanceRadius = 0.4f;
        /// <summary>转向速度。</summary>
        public float TurnSpeed = 8f;
        /// <summary>坐下动画时长。</summary>
        public float SitDownTime = 0.6f;
        /// <summary>保持坐姿时长。</summary>
        public float SitDuration = 5f;
        /// <summary>起身动画时长。</summary>
        public float StandUpTime = 0.5f;

        public override void OnClaim(BTContext ctx, in SlotRef slot, SmartObjectDatabase db)
        {
            base.OnClaim(ctx, slot, db);
            _phase = Phase.MoveToSlot;
            _timer = 0f;
            _path = null;
            _pathIndex = 0;
        }

        public override BTNodeStatus Tick(BTContext ctx, in SlotRef slot, SmartObjectDatabase db)
        {
            var s = db.GetSlot(slot);
            if (s == null) return BTNodeStatus.Failed;

            switch (_phase)
            {
                case Phase.MoveToSlot:
                    // 走到入口位置
                    if (MoveToPosition(ctx, s.UserEntryWorldPosition, MoveSpeed, AcceptanceRadius,
                        ref _path, ref _pathIndex))
                    {
                        _phase = Phase.FaceSlot;
                    }
                    return BTNodeStatus.Running;

                case Phase.FaceSlot:
                    // 朝向槽位朝向
                    if (FaceRotation(ctx, s.WorldRotation, TurnSpeed))
                    {
                        _phase = Phase.SitDown;
                        _timer = 0f;
                        PlayAnimation(ctx.Blackboard, "SitDown", false, SitDownTime);
                    }
                    return BTNodeStatus.Running;

                case Phase.SitDown:
                    _timer += ctx.DeltaTime;
                    if (_timer >= SitDownTime)
                    {
                        _phase = Phase.Sitting;
                        _timer = 0f;
                        PlayAnimation(ctx.Blackboard, "Sitting", true);
                    }
                    return BTNodeStatus.Running;

                case Phase.Sitting:
                    _timer += ctx.DeltaTime;
                    if (_timer >= SitDuration)
                    {
                        _phase = Phase.StandUp;
                        _timer = 0f;
                        PlayAnimation(ctx.Blackboard, "StandUp", false, StandUpTime);
                    }
                    return BTNodeStatus.Running;

                case Phase.StandUp:
                    _timer += ctx.DeltaTime;
                    if (_timer >= StandUpTime)
                    {
                        _phase = Phase.Done;
                        PlayAnimation(ctx.Blackboard, "Idle", true);
                        return BTNodeStatus.Success;
                    }
                    return BTNodeStatus.Running;

                default:
                    return BTNodeStatus.Success;
            }
        }
    }

    /// <summary>
    /// 掩体交互。阶段：走到掩体入口 -> 蹲伏 -> 朝向威胁方向 -> 驻守掩体 -> 撤离。
    /// 威胁方向从黑板键 "ThreatLocation" 读取；未设置时朝槽位朝向。
    /// </summary>
    public sealed class CoverInteraction : SmartObjectInteractionBase
    {
        private enum Phase { MoveToSlot, Crouch, FaceThreat, HoldCover, LeaveCover, Done }
        private Phase _phase;
        private float _timer;
        private List<Vector3>? _path;
        private int _pathIndex;

        public float MoveSpeed = 4.0f;
        public float AcceptanceRadius = 0.4f;
        public float TurnSpeed = 10f;
        public float CrouchTime = 0.4f;
        public float HoldDuration = 4f;
        public float LeaveDistance = 3f;

        public override void OnClaim(BTContext ctx, in SlotRef slot, SmartObjectDatabase db)
        {
            base.OnClaim(ctx, slot, db);
            _phase = Phase.MoveToSlot;
            _timer = 0f;
            _path = null;
            _pathIndex = 0;
        }

        public override BTNodeStatus Tick(BTContext ctx, in SlotRef slot, SmartObjectDatabase db)
        {
            var s = db.GetSlot(slot);
            if (s == null) return BTNodeStatus.Failed;

            switch (_phase)
            {
                case Phase.MoveToSlot:
                    if (MoveToPosition(ctx, s.UserEntryWorldPosition, MoveSpeed, AcceptanceRadius,
                        ref _path, ref _pathIndex))
                    {
                        _phase = Phase.Crouch;
                        _timer = 0f;
                        ctx.Blackboard.SetBool("Crouching", true);
                        PlayAnimation(ctx.Blackboard, "Crouch", false, CrouchTime);
                    }
                    return BTNodeStatus.Running;

                case Phase.Crouch:
                    _timer += ctx.DeltaTime;
                    if (_timer >= CrouchTime)
                    {
                        _phase = Phase.FaceThreat;
                        PlayAnimation(ctx.Blackboard, "CrouchIdle", true);
                    }
                    return BTNodeStatus.Running;

                case Phase.FaceThreat:
                    Vector3 threatDir;
                    if (ctx.Blackboard.HasKey("ThreatLocation"))
                    {
                        Vector3 threat = ctx.Blackboard.GetVector3("ThreatLocation");
                        threatDir = threat - s.WorldPosition;
                    }
                    else
                    {
                        threatDir = Vector3.Transform(new Vector3(0, 0, -1), s.WorldRotation);
                    }
                    if (FaceDirection(ctx, threatDir, TurnSpeed))
                    {
                        _phase = Phase.HoldCover;
                        _timer = 0f;
                    }
                    return BTNodeStatus.Running;

                case Phase.HoldCover:
                    _timer += ctx.DeltaTime;
                    // 若期间威胁位置更新，持续朝向威胁
                    if (ctx.Blackboard.HasKey("ThreatLocation"))
                    {
                        Vector3 threat = ctx.Blackboard.GetVector3("ThreatLocation");
                        FaceDirection(ctx, threat - s.WorldPosition, TurnSpeed);
                    }
                    if (_timer >= HoldDuration)
                    {
                        _phase = Phase.LeaveCover;
                        _timer = 0f;
                        ctx.Blackboard.SetBool("Crouching", false);
                        PlayAnimation(ctx.Blackboard, "Stand", false, 0.4f);
                        _path = null; _pathIndex = 0;
                    }
                    return BTNodeStatus.Running;

                case Phase.LeaveCover:
                    // 沿槽位背向撤离一段距离
                    Vector3 leaveTarget = s.WorldPosition -
                        NormalizeXZ(Vector3.Transform(new Vector3(0, 0, -1), s.WorldRotation)) * LeaveDistance;
                    if (MoveToPosition(ctx, leaveTarget, MoveSpeed, AcceptanceRadius, ref _path, ref _pathIndex))
                    {
                        _phase = Phase.Done;
                        PlayAnimation(ctx.Blackboard, "Idle", true);
                        return BTNodeStatus.Success;
                    }
                    return BTNodeStatus.Running;

                default:
                    return BTNodeStatus.Success;
            }
        }

        public override void OnRelease(BTContext ctx, in SlotRef slot, SmartObjectDatabase db)
        {
            ctx.Blackboard.SetBool("Crouching", false);
            base.OnRelease(ctx, slot, db);
        }
    }

    /// <summary>
    /// 岗哨巡逻交互。阶段：到达岗哨 -> 环顾四周（左右扫视）-> 驻守时长 -> 离开。
    /// </summary>
    public sealed class PatrolSentryInteraction : SmartObjectInteractionBase
    {
        private enum Phase { MoveToSlot, LookAround, HoldPosition, Leave, Done }
        private Phase _phase;
        private float _timer;
        private float _lookYaw;
        private int _lookDir = 1;
        private List<Vector3>? _path;
        private int _pathIndex;

        public float MoveSpeed = 3.5f;
        public float AcceptanceRadius = 0.4f;
        public float TurnSpeed = 6f;
        public float LookDuration = 4f;
        public float LookSweepAngle = MathF.PI * 0.6f; // 约 108°
        public float HoldDuration = 6f;
        public float LeaveDistance = 2.5f;

        public override void OnClaim(BTContext ctx, in SlotRef slot, SmartObjectDatabase db)
        {
            base.OnClaim(ctx, slot, db);
            _phase = Phase.MoveToSlot;
            _timer = 0f;
            _lookYaw = 0f;
            _lookDir = 1;
            _path = null;
            _pathIndex = 0;
        }

        public override BTNodeStatus Tick(BTContext ctx, in SlotRef slot, SmartObjectDatabase db)
        {
            var s = db.GetSlot(slot);
            if (s == null) return BTNodeStatus.Failed;

            switch (_phase)
            {
                case Phase.MoveToSlot:
                    if (MoveToPosition(ctx, s.UserEntryWorldPosition, MoveSpeed, AcceptanceRadius,
                        ref _path, ref _pathIndex))
                    {
                        _phase = Phase.LookAround;
                        _timer = 0f;
                        PlayAnimation(ctx.Blackboard, "Idle", true);
                    }
                    return BTNodeStatus.Running;

                case Phase.LookAround:
                    _timer += ctx.DeltaTime;
                    // 在 ±LookSweepAngle 之间来回扫视
                    _lookYaw += _lookDir * TurnSpeed * 0.5f * ctx.DeltaTime;
                    if (_lookYaw > LookSweepAngle) { _lookYaw = LookSweepAngle; _lookDir = -1; }
                    else if (_lookYaw < -LookSweepAngle) { _lookYaw = -LookSweepAngle; _lookDir = 1; }

                    // 基于槽位朝向叠加扫视偏航
                    float baseYaw = MathF.Atan2(
                        Vector3.Transform(new Vector3(0, 0, -1), s.WorldRotation).X,
                        -Vector3.Transform(new Vector3(0, 0, -1), s.WorldRotation).Z);
                    Quaternion lookRot = Quaternion.CreateFromYawPitchRoll(baseYaw + _lookYaw, 0f, 0f);
                    FaceRotation(ctx, lookRot, TurnSpeed);

                    if (_timer >= LookDuration)
                    {
                        _phase = Phase.HoldPosition;
                        _timer = 0f;
                        FaceRotation(ctx, s.WorldRotation, TurnSpeed);
                    }
                    return BTNodeStatus.Running;

                case Phase.HoldPosition:
                    _timer += ctx.DeltaTime;
                    if (_timer >= HoldDuration)
                    {
                        _phase = Phase.Leave;
                        _timer = 0f;
                        _path = null; _pathIndex = 0;
                    }
                    return BTNodeStatus.Running;

                case Phase.Leave:
                    Vector3 leaveTarget = s.WorldPosition +
                        NormalizeXZ(Vector3.Transform(new Vector3(0, 0, -1), s.WorldRotation)) * LeaveDistance;
                    if (MoveToPosition(ctx, leaveTarget, MoveSpeed, AcceptanceRadius, ref _path, ref _pathIndex))
                    {
                        _phase = Phase.Done;
                        return BTNodeStatus.Success;
                    }
                    return BTNodeStatus.Running;

                default:
                    return BTNodeStatus.Success;
            }
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // 行为树任务（BTTask_UseSmartObject）
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 行为树任务：查询并使用智能物体。派生自 BTNode，异步返回 Running。
    /// 执行流程：
    ///   1. 首次执行时，根据查询条件在数据库中查找附近符合条件的空闲槽位；
    ///   2. 找到后预留（Reserve）槽位，挂载/更新 SmartObjectUserComponent，调用交互的 OnClaim；
    ///   3. 每帧调用交互的 Tick 推进，返回 Running；
    ///   4. 交互完成（Success/Failed）或被中止（Abort）时，释放槽位并清理使用者组件。
    /// </summary>
    public sealed class BTTask_UseSmartObject : BTNode
    {
        /// <summary>
        /// 数据库引用。若为 null，则回退到 SmartObjectDatabase.Active。
        /// 通常由 SmartObjectsSystem.Database 在构建行为树时注入。
        /// </summary>
        public SmartObjectDatabase? Database;

        /// <summary>必须包含的标签。</summary>
        public SmartObjectTag RequiredTags = SmartObjectTag.None;
        /// <summary>排除的标签。</summary>
        public SmartObjectTag ExcludedTags = SmartObjectTag.None;
        /// <summary>搜索半径（作为查询的 MaxDistance）。</summary>
        public float SearchRadius = 50f;
        /// <summary>是否要求视线。</summary>
        public bool RequireLineOfSight;
        /// <summary>是否要求可达。</summary>
        public bool RequireReachable;
        /// <summary>排序模式。</summary>
        public SmartObjectQuerySortMode SortMode = SmartObjectQuerySortMode.Nearest;

        /// <summary>
        /// 交互行为实例。OnClaim 时会重置其内部状态，因此可复用。
        /// 默认为 SitInteraction；可替换为 CoverInteraction / PatrolSentryInteraction 或自定义。
        /// </summary>
        public ISmartObjectInteraction Interaction = new SitInteraction();

        // —— 运行时状态 ——
        private SlotRef _claimedSlot;
        private bool _hasSlot;
        private bool _released = true;

        /// <summary>执行任务。</summary>
        public override BTNodeStatus Execute(BTContext ctx)
        {
            StartRunning(ctx);

            var db = Database ?? SmartObjectDatabase.Active;
            if (db == null || ctx.ECS == null || !ctx.ECS.HasEntity(ctx.EntityId))
            {
                StopRunning(ctx);
                return BTNodeStatus.Failed;
            }

            // 尚未认领槽位：先查询
            if (!_hasSlot)
            {
                var query = new SmartObjectSlotQuery
                {
                    RequiredTags = RequiredTags,
                    ExcludedTags = ExcludedTags,
                    MaxDistance = SearchRadius,
                    RequireLineOfSight = RequireLineOfSight,
                    RequireReachable = RequireReachable,
                    SortMode = SortMode,
                    MaxResults = 1,
                    QuerierEntityId = ctx.EntityId,
                };

                var tf = ctx.ECS.GetComponent<TransformComponent>(ctx.EntityId);
                var results = db.QuerySlots(query, tf.Position);
                if (results.Count == 0)
                {
                    StopRunning(ctx);
                    return BTNodeStatus.Failed;
                }

                var result = results[0];
                if (!db.Reserve(result.Slot, ctx.EntityId))
                {
                    StopRunning(ctx);
                    return BTNodeStatus.Failed;
                }

                _claimedSlot = result.Slot;
                _hasSlot = true;
                _released = false;

                // 记录使用者组件
                ctx.ECS.AddComponent(ctx.EntityId, new SmartObjectUserComponent
                {
                    OccupiedSlot = _claimedSlot,
                    IsInteracting = true,
                });
                // 将槽位位置写入黑板，便于其他节点（如开火、感知）使用
                ctx.Blackboard.SetVector3("SmartObjectSlotPosition", result.WorldPosition);

                Interaction.OnClaim(ctx, _claimedSlot, db);
            }

            // 推进交互
            var status = Interaction.Tick(ctx, _claimedSlot, db);

            if (status == BTNodeStatus.Running)
                return BTNodeStatus.Running;

            // 完成（成功/失败）：释放并清理
            ReleaseSlot(ctx, db);
            StopRunning(ctx);
            return status;
        }

        /// <summary>中止任务时释放槽位。</summary>
        public override void Abort(BTContext ctx)
        {
            var db = Database ?? SmartObjectDatabase.Active;
            ReleaseSlot(ctx, db);
            base.Abort(ctx);
        }

        /// <summary>释放槽位并清理使用者组件（幂等）。</summary>
        private void ReleaseSlot(BTContext ctx, SmartObjectDatabase? db)
        {
            if (_released || !_hasSlot) return;
            if (db != null)
            {
                Interaction.OnRelease(ctx, _claimedSlot, db);
                db.Release(_claimedSlot, ctx.EntityId);
            }
            if (ctx.ECS != null && ctx.ECS.HasEntity(ctx.EntityId) &&
                ctx.ECS.HasComponent<SmartObjectUserComponent>(ctx.EntityId))
            {
                var uc = ctx.ECS.GetComponent<SmartObjectUserComponent>(ctx.EntityId);
                uc.OccupiedSlot = SlotRef.Invalid;
                uc.IsInteracting = false;
                ctx.ECS.SetComponent(ctx.EntityId, uc);
            }
            _hasSlot = false;
            _released = true;
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // 调试（SmartObjectDebugger）
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 调试快照条目：槽位世界位置、状态、标签，供编辑器绘制（对齐 UE5 的 Smart Object 调试视图）。
    /// </summary>
    public readonly struct SmartObjectDebugSlot
    {
        /// <summary>槽位世界位置。</summary>
        public readonly Vector3 Position;
        /// <summary>槽位朝向（Forward 向量）。</summary>
        public readonly Vector3 Forward;
        /// <summary>槽位状态。</summary>
        public readonly SmartObjectSlotState State;
        /// <summary>槽位标签。</summary>
        public readonly SmartObjectTag Tags;
        /// <summary>所属物体 ID。</summary>
        public readonly int ObjectId;

        public SmartObjectDebugSlot(Vector3 position, Vector3 forward, SmartObjectSlotState state,
            SmartObjectTag tags, int objectId)
        {
            Position = position;
            Forward = forward;
            State = state;
            Tags = tags;
            ObjectId = objectId;
        }
    }

    /// <summary>
    /// 智能物体调试器。从数据库收集所有槽位的位置/状态/标签，供编辑器绘制图元与统计。
    /// </summary>
    public static class SmartObjectDebugger
    {
        /// <summary>收集数据库中所有槽位的调试快照。</summary>
        public static List<SmartObjectDebugSlot> CollectSlots(SmartObjectDatabase db)
        {
            var list = new List<SmartObjectDebugSlot>();
            foreach (var oid in EnumerateObjectIds(db))
            {
                var entry = db.GetEntry(oid);
                if (entry == null) continue;
                for (int i = 0; i < entry.Slots.Count; i++)
                {
                    var slot = entry.Slots[i];
                    Vector3 fwd = Vector3.Transform(new Vector3(0, 0, -1), slot.WorldRotation);
                    list.Add(new SmartObjectDebugSlot(
                        slot.WorldPosition, fwd, slot.State, entry.Tags | slot.Tags, oid));
                }
            }
            return list;
        }

        /// <summary>统计各状态的槽位数量。</summary>
        public static void CountStates(SmartObjectDatabase db,
            out int free, out int occupied, out int reserved, out int disabled)
        {
            free = occupied = reserved = disabled = 0;
            foreach (var oid in EnumerateObjectIds(db))
            {
                var entry = db.GetEntry(oid);
                if (entry == null) continue;
                foreach (var slot in entry.Slots)
                {
                    switch (slot.State)
                    {
                        case SmartObjectSlotState.Free: free++; break;
                        case SmartObjectSlotState.Occupied: occupied++; break;
                        case SmartObjectSlotState.Reserved: reserved++; break;
                        case SmartObjectSlotState.Disabled: disabled++; break;
                    }
                }
            }
        }

        // 通过数据库公开的 EnumerateObjectIds() 枚举所有已注册物体 ID。
        private static IEnumerable<int> EnumerateObjectIds(SmartObjectDatabase db)
        {
            return db.EnumerateObjectIds();
        }
    }
}
