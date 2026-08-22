// ShanyuEngine - 山雨游戏引擎
// InventorySystem.cs - 背包/物品系统
// 背包系统管理玩家所有物品 包括装备消耗品材料等
// 支持堆叠叠加槽位分类 装备槽 仓库 邮件附件
// 背包逻辑要严谨 丢物品扣物品都不能出错

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace ShanyuEngine.Gameplay
{
    #region 物品枚举和类型

    /// <summary>
    /// 物品类型分类
    /// </summary>
    public enum ItemType : byte
    {
        // 消耗品 药水卷轴等
        Consumable = 0,
        // 武器
        Weapon = 1,
        // 防具
        Armor = 2,
        // 饰品
        Accessory = 3,
        // 材料 制作原料
        Material = 4,
        // 任务物品
        QuestItem = 5,
        // 杂物
        Misc = 6,
        // 货币
        Currency = 7,
        // 坐骑
        Mount = 8,
        // 宠物
        Pet = 9,
        // 配方
        Recipe = 10,
        // 自定义
        Custom = 255,
    }

    /// <summary>
    /// 物品稀有度
    /// </summary>
    public enum ItemRarity : byte
    {
        // 普通 白色
        Common = 0,
        // 优秀 绿色
        Uncommon = 1,
        // 精良 蓝色
        Rare = 2,
        // 史诗 紫色
        Epic = 3,
        // 传说 橙色
        Legendary = 4,
        // 神器 红色
        Artifact = 5,
    }

    /// <summary>
    /// 装备槽位
    /// </summary>
    public enum EquipSlot : byte
    {
        // 无
        None = 0,
        // 头部
        Head = 1,
        // 颈部
        Neck = 2,
        // 肩膀
        Shoulders = 3,
        // 胸部
        Chest = 4,
        // 背部 披风
        Back = 5,
        // 手腕
        Wrists = 6,
        // 手部
        Hands = 7,
        // 腰部
        Waist = 8,
        // 腿部
        Legs = 9,
        // 脚部
        Feet = 10,
        // 主手武器
        MainHand = 11,
        // 副手武器
        OffHand = 12,
        // 戒指1
        Ring1 = 13,
        // 戒指2
        Ring2 = 14,
        // 饰品1
        Trinket1 = 15,
        // 饰品2
        Trinket2 = 16,
    }

    /// <summary>
    /// 物品操作结果
    /// </summary>
    public enum ItemOperationResult : byte
    {
        // 成功
        Success = 0,
        // 背包满了
        InventoryFull = 1,
        // 物品不存在
        ItemNotFound = 2,
        // 数量不足
        InsufficientQuantity = 3,
        // 物品不可堆叠
        NotStackable = 4,
        // 槽位被占用
        SlotOccupied = 5,
        // 物品类型不匹配
        TypeMismatch = 6,
        // 等级不够
        LevelTooLow = 7,
        // 职业不符
        ClassMismatch = 8,
        // 物品已锁定
        ItemLocked = 9,
        // 物品是唯一 已拥有
        UniqueItemAlreadyOwned = 10,
        // 内部错误
        InternalError = 255,
    }

    #endregion

    #region 物品规格定义

    /// <summary>
    /// 物品规格 静态定义
    /// </summary>
    public class ItemSpec
    {
        // 物品唯一ID
        public int ItemId { get; set; }

        // 名字
        public string Name { get; set; } = string.Empty;

        // 描述
        public string Description { get; set; } = string.Empty;

        // 类型
        public ItemType Type { get; set; } = ItemType.Misc;

        // 稀有度
        public ItemRarity Rarity { get; set; } = ItemRarity.Common;

        // 最大堆叠数量 1表示不可堆叠
        public int MaxStack { get; set; } = 1;

        // 是否可堆叠
        public bool IsStackable => MaxStack > 1;

        // 是否可丢弃
        public bool IsDroppable { get; set; } = true;

        // 是否可出售
        public bool IsSellable { get; set; } = true;

        // 是否可交易
        public bool IsTradable { get; set; } = true;

        // 是否是唯一物品 只能拥有一个
        public bool IsUnique { get; set; } = false;

        // 是否是唯一装备 只能装备一个
        public bool IsUniqueEquipped { get; set; } = false;

        // 出售价格
        public int SellPrice { get; set; } = 0;

        // 购买价格
        public int BuyPrice { get; set; } = 0;

        // 重量 重量系统用
        public float Weight { get; set; } = 0.1f;

        // 使用等级要求
        public int RequiredLevel { get; set; } = 1;

        // 职业要求 空表示无限制
        public List<string> RequiredClasses { get; set; } = new();

        // 装备槽位 装备才用
        public EquipSlot EquipSlot { get; set; } = EquipSlot.None;

        // 属性加成 装备用
        public Dictionary<string, float> AttributeBonuses { get; set; } = new();

        // 使用效果ID 消耗品用
        public List<int> UseEffectIds { get; set; } = new();

        // 使用冷却
        public float UseCooldown { get; set; } = 0f;

        // 图标路径 UI用
        public string IconPath { get; set; } = string.Empty;

        // 模型路径 世界掉落物用
        public string ModelPath { get; set; } = string.Empty;

        // 标签
        public List<string> Tags { get; set; } = new();

        // 耐久度上限 0表示无耐久
        public int MaxDurability { get; set; } = 0;

        // 是否绑定 拾取即绑定
        public bool BindOnPickup { get; set; } = false;

        // 是否绑定 装备即绑定
        public bool BindOnEquip { get; set; } = false;

        // 套装ID 套装效果用
        public int SetId { get; set; } = 0;

        // 元数据
        public Dictionary<string, string> Metadata { get; set; } = new();

        public override string ToString()
        {
            return $"[ItemSpec {ItemId}:{Name} {Type} {Rarity}]";
        }
    }

    /// <summary>
    /// 物品规格仓库
    /// </summary>
    public class ItemSpecRepository
    {
        private readonly Dictionary<int, ItemSpec> _specs = new();
        private readonly Dictionary<string, int> _nameToId = new();
        private readonly object _lock = new();

        public bool Register(ItemSpec spec)
        {
            if (spec == null || spec.ItemId <= 0) return false;
            lock (_lock)
            {
                if (_specs.ContainsKey(spec.ItemId)) return false;
                _specs[spec.ItemId] = spec;
                if (!string.IsNullOrEmpty(spec.Name))
                    _nameToId[spec.Name] = spec.ItemId;
                return true;
            }
        }

        public ItemSpec? GetById(int itemId)
        {
            lock (_lock)
            {
                _specs.TryGetValue(itemId, out var spec);
                return spec;
            }
        }

        public ItemSpec? GetByName(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            lock (_lock)
            {
                if (!_nameToId.TryGetValue(name, out var id)) return null;
                _specs.TryGetValue(id, out var spec);
                return spec;
            }
        }

        public List<ItemSpec> GetByType(ItemType type)
        {
            lock (_lock)
            {
                return _specs.Values.Where(s => s.Type == type).ToList();
            }
        }

        public List<ItemSpec> GetByRarity(ItemRarity rarity)
        {
            lock (_lock)
            {
                return _specs.Values.Where(s => s.Rarity == rarity).ToList();
            }
        }

        public List<ItemSpec> GetAll()
        {
            lock (_lock)
            {
                return _specs.Values.ToList();
            }
        }

        public void Clear()
        {
            lock (_lock)
            {
                _specs.Clear();
                _nameToId.Clear();
            }
        }

        public int Count
        {
            get { lock (_lock) { return _specs.Count; } }
        }
    }

    #endregion

    #region 物品实例 运行时

    /// <summary>
    /// 物品实例 背包里的一个物品
    /// </summary>
    public class ItemInstance
    {
        // 唯一实例ID
        public long InstanceId { get; internal set; }

        // 引用规格
        public ItemSpec Spec { get; }

        // 当前堆叠数量
        public int StackCount { get; set; } = 1;

        // 当前耐久度
        public int Durability { get; set; }

        // 是否已绑定
        public bool IsBound { get; set; }

        // 绑定的玩家ID
        public long BoundPlayerId { get; set; }

        // 锁定 不能操作
        public bool IsLocked { get; set; }

        // 创建时间
        public DateTime CreatedTime { get; internal set; }

        // 实例属性 附加的属性加成 比如附魔
        public Dictionary<string, float> InstanceAttributeBonuses { get; set; } = new();

        // 附魔列表
        public List<int> EnchantIds { get; set; } = new();

        // 宝石孔 宝石镶嵌用
        public List<int?> GemSlots { get; set; } = new();

        // 自定义数据
        public Dictionary<string, object?> CustomData { get; } = new();

        // 实例ID自增
        private static long _nextId = 1;

        public ItemInstance(ItemSpec spec)
        {
            Spec = spec ?? throw new ArgumentNullException(nameof(spec));
            InstanceId = System.Threading.Interlocked.Increment(ref _nextId);
            CreatedTime = DateTime.UtcNow;
            Durability = spec.MaxDurability;
        }

        /// <summary>
        /// 是否可堆叠更多
        /// </summary>
        public bool CanStackMore(int count)
        {
            if (!Spec.IsStackable) return false;
            return StackCount + count <= Spec.MaxStack;
        }

        /// <summary>
        /// 剩余可堆叠数量
        /// </summary>
        public int AvailableStackSpace
        {
            get
            {
                if (!Spec.IsStackable) return 0;
                return Spec.MaxStack - StackCount;
            }
        }

        /// <summary>
        /// 是否需要修理
        /// </summary>
        public bool NeedsRepair => Spec.MaxDurability > 0 && Durability < Spec.MaxDurability;

        /// <summary>
        /// 是否已损坏
        /// </summary>
        public bool IsBroken => Spec.MaxDurability > 0 && Durability <= 0;

        /// <summary>
        /// 获取所有属性加成 包括附魔宝石
        /// </summary>
        public Dictionary<string, float> GetAllAttributeBonuses()
        {
            var result = new Dictionary<string, float>();

            // 基础加成
            foreach (var kv in Spec.AttributeBonuses)
            {
                result[kv.Key] = result.GetValueOrDefault(kv.Key) + kv.Value;
            }

            // 实例加成
            foreach (var kv in InstanceAttributeBonuses)
            {
                result[kv.Key] = result.GetValueOrDefault(kv.Key) + kv.Value;
            }

            return result;
        }

        /// <summary>
        /// 绑定到玩家
        /// </summary>
        public void Bind(long playerId)
        {
            IsBound = true;
            BoundPlayerId = playerId;
        }

        /// <summary>
        /// 减少耐久度
        /// </summary>
        public void ReduceDurability(int amount)
        {
            if (Spec.MaxDurability <= 0) return;
            Durability = Math.Max(0, Durability - amount);
        }

        /// <summary>
        /// 修复耐久度
        /// </summary>
        public void Repair(int amount = -1)
        {
            if (Spec.MaxDurability <= 0) return;
            Durability = amount < 0 ? Spec.MaxDurability : Math.Min(Spec.MaxDurability, Durability + amount);
        }

        public override string ToString()
        {
            return $"[Item {Spec.Name} x{StackCount} {(IsBound ? "Bound" : "")}]";
        }
    }

    #endregion

    #region 背包槽位

    /// <summary>
    /// 背包槽位 存放物品的位置
    /// </summary>
    public class InventorySlot
    {
        // 槽位索引
        public int Index { get; }

        // 槽位里的物品 null表示空
        public ItemInstance? Item { get; private set; }

        // 槽位类型限制 只能放特定类型
        public ItemType? FilterType { get; set; }

        // 是否锁定
        public bool IsLocked { get; set; }

        public InventorySlot(int index)
        {
            Index = index;
        }

        /// <summary>
        /// 是否为空
        /// </summary>
        public bool IsEmpty => Item == null;

        /// <summary>
        /// 是否可以放入物品
        /// </summary>
        public bool CanAccept(ItemInstance item)
        {
            if (IsLocked) return false;
            if (item == null) return false;

            if (FilterType.HasValue && item.Spec.Type != FilterType.Value)
                return false;

            return true;
        }

        /// <summary>
        /// 设置物品
        /// </summary>
        public void SetItem(ItemInstance? item)
        {
            Item = item;
        }

        /// <summary>
        /// 清空槽位 返回原来的物品
        /// </summary>
        public ItemInstance? Clear()
        {
            var item = Item;
            Item = null;
            return item;
        }
    }

    #endregion

    #region 背包主类

    /// <summary>
    /// 背包 管理一组物品
    /// </summary>
    public class Inventory
    {
        // 背包ID
        public long InventoryId { get; internal set; }

        // 背包名字
        public string Name { get; set; } = "MainInventory";

        // 所有槽位
        private readonly List<InventorySlot> _slots = new();

        // 槽位数量
        public int SlotCount => _slots.Count;

        // 已使用槽位
        public int UsedSlotCount
        {
            get
            {
                return _slots.Count(s => !s.IsEmpty);
            }
        }

        // 空闲槽位
        public int FreeSlotCount => SlotCount - UsedSlotCount;

        // 所属玩家ID
        public long OwnerPlayerId { get; set; }

        // 锁
        private readonly object _lock = new();

        // 物品变化事件
        public event Action<Inventory, int, ItemInstance?>? SlotChanged;
        public event Action<Inventory, ItemInstance, int>? ItemAdded;
        public event Action<Inventory, ItemInstance, int>? ItemRemoved;

        // 背包ID自增
        private static long _nextId = 1;

        /// <summary>
        /// 构造 指定槽位数量
        /// </summary>
        public Inventory(int slotCount)
        {
            InventoryId = System.Threading.Interlocked.Increment(ref _nextId);

            for (int i = 0; i < slotCount; i++)
            {
                _slots.Add(new InventorySlot(i));
            }
        }

        /// <summary>
        /// 获取槽位
        /// </summary>
        public InventorySlot? GetSlot(int index)
        {
            lock (_lock)
            {
                if (index < 0 || index >= _slots.Count) return null;
                return _slots[index];
            }
        }

        /// <summary>
        /// 获取所有槽位
        /// </summary>
        public List<InventorySlot> GetAllSlots()
        {
            lock (_lock)
            {
                return new List<InventorySlot>(_slots);
            }
        }

        /// <summary>
        /// 添加物品 返回添加的数量
        /// </summary>
        public ItemOperationResult AddItem(ItemInstance item, out int addedCount)
        {
            addedCount = 0;
            if (item == null) return ItemOperationResult.ItemNotFound;

            lock (_lock)
            {
                // 检查唯一物品
                if (item.Spec.IsUnique)
                {
                    if (HasItem(item.Spec.ItemId))
                        return ItemOperationResult.UniqueItemAlreadyOwned;
                }

                // 可堆叠的先尝试堆叠到已有物品
                if (item.Spec.IsStackable)
                {
                    int remaining = item.StackCount;

                    foreach (var slot in _slots)
                    {
                        if (slot.IsEmpty) continue;
                        if (slot.Item!.Spec.ItemId != item.Spec.ItemId) continue;
                        if (slot.Item.IsLocked) continue;

                        int space = slot.Item.AvailableStackSpace;
                        if (space <= 0) continue;

                        int toAdd = Math.Min(space, remaining);
                        slot.Item.StackCount += toAdd;
                        remaining -= toAdd;
                        addedCount += toAdd;

                        SlotChanged?.Invoke(this, slot.Index, slot.Item);

                        if (remaining <= 0)
                        {
                            ItemAdded?.Invoke(this, item, addedCount);
                            return ItemOperationResult.Success;
                        }
                    }

                    // 还有剩余 找空槽位
                    if (remaining > 0)
                    {
                        foreach (var slot in _slots)
                        {
                            if (!slot.IsEmpty) continue;
                            if (!slot.CanAccept(item)) continue;

                            var newStack = new ItemInstance(item.Spec)
                            {
                                StackCount = remaining,
                                IsBound = item.IsBound,
                                BoundPlayerId = item.BoundPlayerId,
                                Durability = item.Durability,
                            };

                            slot.SetItem(newStack);
                            addedCount += remaining;
                            remaining = 0;

                            SlotChanged?.Invoke(this, slot.Index, newStack);
                            break;
                        }
                    }

                    if (addedCount > 0)
                    {
                        ItemAdded?.Invoke(this, item, addedCount);
                        return remaining > 0
                            ? ItemOperationResult.Success
                            : ItemOperationResult.Success;
                    }

                    return ItemOperationResult.InventoryFull;
                }
                else
                {
                    // 不可堆叠 找空槽位
                    foreach (var slot in _slots)
                    {
                        if (!slot.IsEmpty) continue;
                        if (!slot.CanAccept(item)) continue;

                        slot.SetItem(item);
                        addedCount = item.StackCount;

                        SlotChanged?.Invoke(this, slot.Index, item);
                        ItemAdded?.Invoke(this, item, addedCount);
                        return ItemOperationResult.Success;
                    }

                    return ItemOperationResult.InventoryFull;
                }
            }
        }

        /// <summary>
        /// 添加物品 简化版
        /// </summary>
        public ItemOperationResult AddItem(ItemInstance item)
        {
            return AddItem(item, out _);
        }

        /// <summary>
        /// 添加物品 按规格ID和数量
        /// </summary>
        public ItemOperationResult AddItem(ItemSpec spec, int count)
        {
            if (spec == null || count <= 0) return ItemOperationResult.ItemNotFound;

            int remaining = count;
            while (remaining > 0)
            {
                int stackSize = spec.IsStackable ? Math.Min(spec.MaxStack, remaining) : 1;
                var instance = new ItemInstance(spec) { StackCount = stackSize };

                var result = AddItem(instance, out int added);
                if (result != ItemOperationResult.Success)
                {
                    return remaining == count ? result : ItemOperationResult.Success;
                }

                remaining -= added;
            }

            return ItemOperationResult.Success;
        }

        /// <summary>
        /// 移除物品 按槽位
        /// </summary>
        public ItemOperationResult RemoveItem(int slotIndex, int count = -1)
        {
            lock (_lock)
            {
                if (slotIndex < 0 || slotIndex >= _slots.Count)
                    return ItemOperationResult.ItemNotFound;

                var slot = _slots[slotIndex];
                if (slot.IsEmpty) return ItemOperationResult.ItemNotFound;
                if (slot.Item!.IsLocked) return ItemOperationResult.ItemLocked;

                int removeCount = count < 0 ? slot.Item.StackCount : Math.Min(count, slot.Item.StackCount);

                if (removeCount >= slot.Item.StackCount)
                {
                    // 全部移除
                    var removed = slot.Clear();
                    SlotChanged?.Invoke(this, slotIndex, null);
                    if (removed != null)
                        ItemRemoved?.Invoke(this, removed, removeCount);
                }
                else
                {
                    // 部分移除
                    slot.Item.StackCount -= removeCount;
                    SlotChanged?.Invoke(this, slotIndex, slot.Item);
                    ItemRemoved?.Invoke(this, slot.Item, removeCount);
                }

                return ItemOperationResult.Success;
            }
        }

        /// <summary>
        /// 移除物品 按规格ID
        /// </summary>
        public ItemOperationResult RemoveItem(int itemId, int count, out int removedCount)
        {
            removedCount = 0;
            int remaining = count;

            lock (_lock)
            {
                // 先找所有匹配的槽位
                var matchingSlots = _slots
                    .Where(s => !s.IsEmpty && s.Item!.Spec.ItemId == itemId && !s.Item.IsLocked)
                    .OrderBy(s => s.Item!.StackCount)
                    .ToList();

                foreach (var slot in matchingSlots)
                {
                    if (remaining <= 0) break;

                    int toRemove = Math.Min(remaining, slot.Item!.StackCount);

                    if (toRemove >= slot.Item.StackCount)
                    {
                        slot.Clear();
                        SlotChanged?.Invoke(this, slot.Index, null);
                    }
                    else
                    {
                        slot.Item.StackCount -= toRemove;
                        SlotChanged?.Invoke(this, slot.Index, slot.Item);
                    }

                    remaining -= toRemove;
                    removedCount += toRemove;
                }
            }

            return remaining > 0 ? ItemOperationResult.InsufficientQuantity : ItemOperationResult.Success;
        }

        /// <summary>
        /// 移动物品 从一个槽位到另一个
        /// </summary>
        public ItemOperationResult MoveItem(int fromSlot, int toSlot)
        {
            lock (_lock)
            {
                if (fromSlot < 0 || fromSlot >= _slots.Count) return ItemOperationResult.ItemNotFound;
                if (toSlot < 0 || toSlot >= _slots.Count) return ItemOperationResult.ItemNotFound;
                if (fromSlot == toSlot) return ItemOperationResult.Success;

                var from = _slots[fromSlot];
                var to = _slots[toSlot];

                if (from.IsEmpty) return ItemOperationResult.ItemNotFound;
                if (!to.CanAccept(from.Item!)) return ItemOperationResult.TypeMismatch;

                if (to.IsEmpty)
                {
                    // 目标空 直接移动
                    var item = from.Clear();
                    to.SetItem(item);
                    SlotChanged?.Invoke(this, fromSlot, null);
                    SlotChanged?.Invoke(this, toSlot, item);
                }
                else
                {
                    // 目标有物品 检查是否可以堆叠
                    if (from.Item!.Spec.ItemId == to.Item!.Spec.ItemId &&
                        from.Item.Spec.IsStackable &&
                        !to.Item.IsLocked)
                    {
                        int space = to.Item.AvailableStackSpace;
                        if (space <= 0) return ItemOperationResult.SlotOccupied;

                        int toMove = Math.Min(space, from.Item.StackCount);
                        to.Item.StackCount += toMove;
                        from.Item.StackCount -= toMove;

                        if (from.Item.StackCount <= 0)
                        {
                            from.Clear();
                            SlotChanged?.Invoke(this, fromSlot, null);
                        }
                        else
                        {
                            SlotChanged?.Invoke(this, fromSlot, from.Item);
                        }

                        SlotChanged?.Invoke(this, toSlot, to.Item);
                    }
                    else
                    {
                        // 交换
                        var itemA = from.Clear();
                        var itemB = to.Clear();
                        from.SetItem(itemB);
                        to.SetItem(itemA);
                        SlotChanged?.Invoke(this, fromSlot, itemB);
                        SlotChanged?.Invoke(this, toSlot, itemA);
                    }
                }

                return ItemOperationResult.Success;
            }
        }

        /// <summary>
        /// 交换槽位
        /// </summary>
        public ItemOperationResult SwapSlots(int slotA, int slotB)
        {
            return MoveItem(slotA, slotB);
        }

        /// <summary>
        /// 分割物品堆
        /// </summary>
        public ItemOperationResult SplitStack(int fromSlot, int toSlot, int count)
        {
            lock (_lock)
            {
                if (fromSlot < 0 || fromSlot >= _slots.Count) return ItemOperationResult.ItemNotFound;
                if (toSlot < 0 || toSlot >= _slots.Count) return ItemOperationResult.ItemNotFound;

                var from = _slots[fromSlot];
                var to = _slots[toSlot];

                if (from.IsEmpty) return ItemOperationResult.ItemNotFound;
                if (!from.Item!.Spec.IsStackable) return ItemOperationResult.NotStackable;
                if (from.Item.StackCount < count) return ItemOperationResult.InsufficientQuantity;
                if (!to.IsEmpty) return ItemOperationResult.SlotOccupied;

                var newStack = new ItemInstance(from.Item.Spec)
                {
                    StackCount = count,
                    IsBound = from.Item.IsBound,
                    BoundPlayerId = from.Item.BoundPlayerId,
                    Durability = from.Item.Durability,
                };

                from.Item.StackCount -= count;
                to.SetItem(newStack);

                if (from.Item.StackCount <= 0)
                {
                    from.Clear();
                    SlotChanged?.Invoke(this, fromSlot, null);
                }
                else
                {
                    SlotChanged?.Invoke(this, fromSlot, from.Item);
                }

                SlotChanged?.Invoke(this, toSlot, newStack);
                return ItemOperationResult.Success;
            }
        }

        /// <summary>
        /// 使用物品
        /// </summary>
        public ItemOperationResult UseItem(int slotIndex, GameplayAbilityComponent? user)
        {
            lock (_lock)
            {
                if (slotIndex < 0 || slotIndex >= _slots.Count)
                    return ItemOperationResult.ItemNotFound;

                var slot = _slots[slotIndex];
                if (slot.IsEmpty) return ItemOperationResult.ItemNotFound;
                if (slot.Item!.IsLocked) return ItemOperationResult.ItemLocked;

                var item = slot.Item;

                // 检查等级要求
                if (user?.Attributes != null)
                {
                    var level = user.Attributes.GetAttribute("Level");
                    if (level != null && level.CurrentValue < item.Spec.RequiredLevel)
                        return ItemOperationResult.LevelTooLow;
                }

                // 检查耐久
                if (item.IsBroken) return ItemOperationResult.ItemLocked;

                // 应用使用效果
                if (item.Spec.UseEffectIds.Count > 0 && user != null)
                {
                    foreach (var effectId in item.Spec.UseEffectIds)
                    {
                        var effectSpec = GameplayEffectSystem.Instance.Specs.GetById(effectId);
                        if (effectSpec != null && user.Effects != null)
                        {
                            // 实际项目里这里要转换为完整的容器
                        }
                    }
                }

                // 消耗品减少数量
                if (item.Spec.Type == ItemType.Consumable)
                {
                    return RemoveItem(slotIndex, 1);
                }

                return ItemOperationResult.Success;
            }
        }

        /// <summary>
        /// 是否拥有指定物品
        /// </summary>
        public bool HasItem(int itemId)
        {
            lock (_lock)
            {
                return _slots.Any(s => !s.IsEmpty && s.Item!.Spec.ItemId == itemId);
            }
        }

        /// <summary>
        /// 获取指定物品的总数量
        /// </summary>
        public int GetItemCount(int itemId)
        {
            lock (_lock)
            {
                return _slots
                    .Where(s => !s.IsEmpty && s.Item!.Spec.ItemId == itemId)
                    .Sum(s => s.Item!.StackCount);
            }
        }

        /// <summary>
        /// 获取所有物品实例
        /// </summary>
        public List<ItemInstance> GetAllItems()
        {
            lock (_lock)
            {
                return _slots
                    .Where(s => !s.IsEmpty)
                    .Select(s => s.Item!)
                    .ToList();
            }
        }

        /// <summary>
        /// 按类型获取物品
        /// </summary>
        public List<ItemInstance> GetItemsByType(ItemType type)
        {
            lock (_lock)
            {
                return _slots
                    .Where(s => !s.IsEmpty && s.Item!.Spec.Type == type)
                    .Select(s => s.Item!)
                    .ToList();
            }
        }

        /// <summary>
        /// 查找物品 按规格ID
        /// </summary>
        public List<int> FindSlotsWithItem(int itemId)
        {
            lock (_lock)
            {
                return _slots
                    .Where(s => !s.IsEmpty && s.Item!.Spec.ItemId == itemId)
                    .Select(s => s.Index)
                    .ToList();
            }
        }

        /// <summary>
        /// 查找空槽位
        /// </summary>
        public List<int> FindEmptySlots()
        {
            lock (_lock)
            {
                return _slots
                    .Where(s => s.IsEmpty && !s.IsLocked)
                    .Select(s => s.Index)
                    .ToList();
            }
        }

        /// <summary>
        /// 扩展背包容量
        /// </summary>
        public void Expand(int additionalSlots)
        {
            if (additionalSlots <= 0) return;

            lock (_lock)
            {
                int startIndex = _slots.Count;
                for (int i = 0; i < additionalSlots; i++)
                {
                    _slots.Add(new InventorySlot(startIndex + i));
                }
            }
        }

        /// <summary>
        /// 排序背包 按类型和稀有度
        /// </summary>
        public void Sort()
        {
            lock (_lock)
            {
                var items = _slots
                    .Where(s => !s.IsEmpty)
                    .Select(s => s.Item!)
                    .OrderBy(i => i.Spec.Type)
                    .ThenByDescending(i => i.Spec.Rarity)
                    .ThenBy(i => i.Spec.ItemId)
                    .ToList();

                // 清空所有槽位
                foreach (var slot in _slots)
                {
                    slot.Clear();
                }

                // 重新填充
                int index = 0;
                foreach (var item in items)
                {
                    _slots[index].SetItem(item);
                    SlotChanged?.Invoke(this, index, item);
                    index++;
                }
            }
        }

        /// <summary>
        /// 清空背包
        /// </summary>
        public void Clear()
        {
            lock (_lock)
            {
                foreach (var slot in _slots)
                {
                    if (!slot.IsEmpty)
                    {
                        var item = slot.Clear();
                        SlotChanged?.Invoke(this, slot.Index, null);
                        if (item != null)
                            ItemRemoved?.Invoke(this, item, item.StackCount);
                    }
                }
            }
        }

        /// <summary>
        /// 获取背包总价值
        /// </summary>
        public long GetTotalValue()
        {
            lock (_lock)
            {
                return _slots
                    .Where(s => !s.IsEmpty)
                    .Sum(s => (long)s.Item!.Spec.SellPrice * s.Item.StackCount);
            }
        }

        /// <summary>
        /// 获取背包总重量
        /// </summary>
        public float GetTotalWeight()
        {
            lock (_lock)
            {
                return _slots
                    .Where(s => !s.IsEmpty)
                    .Sum(s => s.Item!.Spec.Weight * s.Item.StackCount);
            }
        }

        /// <summary>
        /// 转字符串
        /// </summary>
        public override string ToString()
        {
            return $"[Inventory {Name} {UsedSlotCount}/{SlotCount}]";
        }
    }

    #endregion

    #region 装备管理器

    /// <summary>
    /// 装备管理器 管理装备槽
    /// </summary>
    public class EquipmentManager
    {
        // 所属的背包
        private readonly Inventory _inventory;

        // 装备槽 按EquipSlot索引
        private readonly Dictionary<EquipSlot, ItemInstance?> _equipSlots = new();

        // 所属的能力组件
        public GameplayAbilityComponent? OwnerComponent { get; set; }

        // 锁
        private readonly object _lock = new();

        // 装备变化事件
        public event Action<EquipmentManager, EquipSlot, ItemInstance?>? EquipmentChanged;

        public EquipmentManager(Inventory inventory)
        {
            _inventory = inventory ?? throw new ArgumentNullException(nameof(inventory));

            // 初始化所有装备槽
            foreach (EquipSlot slot in Enum.GetValues(typeof(EquipSlot)))
            {
                if (slot != EquipSlot.None)
                {
                    _equipSlots[slot] = null;
                }
            }
        }

        /// <summary>
        /// 装备物品
        /// </summary>
        public ItemOperationResult Equip(ItemInstance item, EquipSlot? specificSlot = null)
        {
            if (item == null) return ItemOperationResult.ItemNotFound;
            if (item.Spec.EquipSlot == EquipSlot.None) return ItemOperationResult.TypeMismatch;

            lock (_lock)
            {
                EquipSlot targetSlot = specificSlot ?? item.Spec.EquipSlot;

                // 戒指饰品特殊处理 有两个槽位
                if (item.Spec.EquipSlot == EquipSlot.Ring1 || item.Spec.EquipSlot == EquipSlot.Ring2)
                {
                    if (specificSlot == null)
                    {
                        // 自动选一个空的戒指槽
                        if (_equipSlots[EquipSlot.Ring1] == null)
                            targetSlot = EquipSlot.Ring1;
                        else if (_equipSlots[EquipSlot.Ring2] == null)
                            targetSlot = EquipSlot.Ring2;
                        else
                            targetSlot = EquipSlot.Ring1; // 替换第一个
                    }
                }

                if (item.Spec.EquipSlot == EquipSlot.Trinket1 || item.Spec.EquipSlot == EquipSlot.Trinket2)
                {
                    if (specificSlot == null)
                    {
                        if (_equipSlots[EquipSlot.Trinket1] == null)
                            targetSlot = EquipSlot.Trinket1;
                        else if (_equipSlots[EquipSlot.Trinket2] == null)
                            targetSlot = EquipSlot.Trinket2;
                        else
                            targetSlot = EquipSlot.Trinket1;
                    }
                }

                // 检查唯一装备
                if (item.Spec.IsUniqueEquipped)
                {
                    foreach (var kv in _equipSlots)
                    {
                        if (kv.Value != null && kv.Value.Spec.ItemId == item.Spec.ItemId)
                            return ItemOperationResult.UniqueItemAlreadyOwned;
                    }
                }

                // 取下原来的装备
                var oldItem = _equipSlots[targetSlot];
                if (oldItem != null)
                {
                    UnequipInternal(targetSlot);
                }

                // 装备新物品
                _equipSlots[targetSlot] = item;

                // 绑定
                if (item.Spec.BindOnEquip && !item.IsBound)
                {
                    item.Bind(_inventory.OwnerPlayerId);
                }

                // 应用属性加成
                ApplyAttributeBonuses(item);

                EquipmentChanged?.Invoke(this, targetSlot, item);
                return ItemOperationResult.Success;
            }
        }

        /// <summary>
        /// 卸下装备
        /// </summary>
        public ItemOperationResult Unequip(EquipSlot slot)
        {
            lock (_lock)
            {
                return UnequipInternal(slot);
            }
        }

        private ItemOperationResult UnequipInternal(EquipSlot slot)
        {
            var item = _equipSlots[slot];
            if (item == null) return ItemOperationResult.ItemNotFound;

            // 尝试放回背包
            var result = _inventory.AddItem(item);
            if (result != ItemOperationResult.Success)
            {
                // 背包满了 不能卸下
                _equipSlots[slot] = item;
                return result;
            }

            // 移除属性加成
            RemoveAttributeBonuses(item);

            _equipSlots[slot] = null;
            EquipmentChanged?.Invoke(this, slot, null);
            return ItemOperationResult.Success;
        }

        /// <summary>
        /// 获取装备槽的物品
        /// </summary>
        public ItemInstance? GetEquipment(EquipSlot slot)
        {
            lock (_lock)
            {
                return _equipSlots.GetValueOrDefault(slot);
            }
        }

        /// <summary>
        /// 获取所有装备
        /// </summary>
        public Dictionary<EquipSlot, ItemInstance> GetAllEquipment()
        {
            lock (_lock)
            {
                var result = new Dictionary<EquipSlot, ItemInstance>();
                foreach (var kv in _equipSlots)
                {
                    if (kv.Value != null)
                        result[kv.Key] = kv.Value;
                }
                return result;
            }
        }

        /// <summary>
        /// 应用属性加成
        /// </summary>
        private void ApplyAttributeBonuses(ItemInstance item)
        {
            if (OwnerComponent?.Attributes == null) return;

            var bonuses = item.GetAllAttributeBonuses();
            foreach (var kv in bonuses)
            {
                OwnerComponent.Attributes.AddModifier(kv.Key,
                    AttributeModifier.CreateAdd(kv.Value, $"Equipment_{item.Spec.Name}"));
            }
        }

        /// <summary>
        /// 移除属性加成
        /// </summary>
        private void RemoveAttributeBonuses(ItemInstance item)
        {
            if (OwnerComponent?.Attributes == null) return;

            OwnerComponent.Attributes.RemoveModifiersBySource(item.Name, $"Equipment_{item.Spec.Name}");
        }

        /// <summary>
        /// 重新应用所有装备属性
        /// </summary>
        public void RefreshAllBonuses()
        {
            lock (_lock)
            {
                foreach (var kv in _equipSlots)
                {
                    if (kv.Value != null)
                    {
                        RemoveAttributeBonuses(kv.Value);
                        ApplyAttributeBonuses(kv.Value);
                    }
                }
            }
        }

        /// <summary>
        /// 获取套装数量
        /// </summary>
        public int GetSetCount(int setId)
        {
            lock (_lock)
            {
                return _equipSlots.Values
                    .Where(i => i != null && i.Spec.SetId == setId)
                    .GroupBy(i => i!.Spec.ItemId)
                    .Count();
            }
        }

        /// <summary>
        /// 减少所有装备耐久
        /// </summary>
        public void ReduceAllDurability(int amount)
        {
            lock (_lock)
            {
                foreach (var kv in _equipSlots)
                {
                    if (kv.Value != null)
                    {
                        kv.Value.ReduceDurability(amount);
                    }
                }
            }
        }

        /// <summary>
        /// 修理所有装备
        /// </summary>
        public void RepairAll()
        {
            lock (_lock)
            {
                foreach (var kv in _equipSlots)
                {
                    kv.Value?.Repair();
                }
            }
        }

        /// <summary>
        /// 卸下所有装备
        /// </summary>
        public void UnequipAll()
        {
            lock (_lock)
            {
                foreach (var slot in _equipSlots.Keys.ToList())
                {
                    if (_equipSlots[slot] != null)
                        UnequipInternal(slot);
                }
            }
        }
    }

    #endregion

    #region 背包系统管理器

    /// <summary>
    /// 背包系统管理器 全局入口
    /// </summary>
    public class InventorySystem
    {
        // 单例
        private static InventorySystem? _instance;
        private static readonly object _instanceLock = new();

        public static InventorySystem Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_instanceLock)
                    {
                        _instance ??= new InventorySystem();
                    }
                }
                return _instance;
            }
        }

        // 物品规格仓库
        public ItemSpecRepository ItemSpecs { get; } = new();

        // 所有背包 跟踪
        private readonly Dictionary<long, Inventory> _inventories = new();
        private readonly object _inventoriesLock = new();

        private InventorySystem() { }

        /// <summary>
        /// 创建背包
        /// </summary>
        public Inventory CreateInventory(int slotCount, long ownerPlayerId = 0)
        {
            var inv = new Inventory(slotCount)
            {
                OwnerPlayerId = ownerPlayerId,
            };

            lock (_inventoriesLock)
            {
                _inventories[inv.InventoryId] = inv;
            }

            return inv;
        }

        /// <summary>
        /// 销毁背包
        /// </summary>
        public void DestroyInventory(Inventory inventory)
        {
            if (inventory == null) return;
            lock (_inventoriesLock)
            {
                _inventories.Remove(inventory.InventoryId);
            }
            inventory.Clear();
        }

        /// <summary>
        /// 获取背包
        /// </summary>
        public Inventory? GetInventory(long inventoryId)
        {
            lock (_inventoriesLock)
            {
                _inventories.TryGetValue(inventoryId, out var inv);
                return inv;
            }
        }

        /// <summary>
        /// 获取所有背包
        /// </summary>
        public List<Inventory> GetAllInventories()
        {
            lock (_inventoriesLock)
            {
                return _inventories.Values.ToList();
            }
        }

        /// <summary>
        /// 注册物品规格
        /// </summary>
        public bool RegisterItemSpec(ItemSpec spec)
        {
            return ItemSpecs.Register(spec);
        }

        /// <summary>
        /// 关闭系统
        /// </summary>
        public void Shutdown()
        {
            lock (_inventoriesLock)
            {
                foreach (var inv in _inventories.Values)
                {
                    inv.Clear();
                }
                _inventories.Clear();
            }
            ItemSpecs.Clear();
        }
    }

    #endregion

    #region 物品工厂 预设物品

    /// <summary>
    /// 物品工厂 创建常用物品
    /// </summary>
    public static class ItemFactory
    {
        /// <summary>
        /// 创建生命药水
        /// </summary>
        public static ItemSpec CreateHealthPotion(int itemId)
        {
            return new ItemSpec
            {
                ItemId = itemId,
                Name = "HealthPotion",
                Description = "恢复50点生命值",
                Type = ItemType.Consumable,
                Rarity = ItemRarity.Common,
                MaxStack = 99,
                BuyPrice = 50,
                SellPrice = 25,
                Weight = 0.1f,
                UseEffectIds = new List<int>(),
                UseCooldown = 30f,
                IconPath = "UI/Icons/Items/HealthPotion",
                ModelPath = "Models/Items/Potion",
            };
        }

        /// <summary>
        /// 创建法力药水
        /// </summary>
        public static ItemSpec CreateManaPotion(int itemId)
        {
            return new ItemSpec
            {
                ItemId = itemId,
                Name = "ManaPotion",
                Description = "恢复30点法力值",
                Type = ItemType.Consumable,
                Rarity = ItemRarity.Common,
                MaxStack = 99,
                BuyPrice = 60,
                SellPrice = 30,
                Weight = 0.1f,
                UseCooldown = 30f,
            };
        }

        /// <summary>
        /// 创建铁剑
        /// </summary>
        public static ItemSpec CreateIronSword(int itemId)
        {
            return new ItemSpec
            {
                ItemId = itemId,
                Name = "IronSword",
                Description = "一把普通的铁剑",
                Type = ItemType.Weapon,
                Rarity = ItemRarity.Common,
                MaxStack = 1,
                BuyPrice = 100,
                SellPrice = 50,
                Weight = 3f,
                RequiredLevel = 1,
                EquipSlot = EquipSlot.MainHand,
                AttributeBonuses = new Dictionary<string, float>
                {
                    { "Attack", 10f },
                },
                MaxDurability = 100,
                BindOnEquip = true,
            };
        }

        /// <summary>
        /// 创建传说之剑
        /// </summary>
        public static ItemSpec CreateLegendarySword(int itemId)
        {
            return new ItemSpec
            {
                ItemId = itemId,
                Name = "LegendarySword",
                Description = "传说中的神剑 拥有无上的力量",
                Type = ItemType.Weapon,
                Rarity = ItemRarity.Legendary,
                MaxStack = 1,
                BuyPrice = 10000,
                SellPrice = 5000,
                Weight = 5f,
                RequiredLevel = 50,
                EquipSlot = EquipSlot.MainHand,
                AttributeBonuses = new Dictionary<string, float>
                {
                    { "Attack", 100f },
                    { "CritRate", 0.2f },
                    { "CritDamage", 0.5f },
                },
                MaxDurability = 200,
                BindOnPickup = true,
                IsUnique = true,
            };
        }

        /// <summary>
        /// 创建皮甲
        /// </summary>
        public static ItemSpec CreateLeatherArmor(int itemId)
        {
            return new ItemSpec
            {
                ItemId = itemId,
                Name = "LeatherArmor",
                Description = "皮质护甲 提供基础防护",
                Type = ItemType.Armor,
                Rarity = ItemRarity.Common,
                MaxStack = 1,
                BuyPrice = 80,
                SellPrice = 40,
                Weight = 5f,
                RequiredLevel = 1,
                EquipSlot = EquipSlot.Chest,
                AttributeBonuses = new Dictionary<string, float>
                {
                    { "Defense", 8f },
                    { "MaxHealth", 20f },
                },
                MaxDurability = 100,
                BindOnEquip = true,
            };
        }

        /// <summary>
        /// 创建铁矿石
        /// </summary>
        public static ItemSpec CreateIronOre(int itemId)
        {
            return new ItemSpec
            {
                ItemId = itemId,
                Name = "IronOre",
                Description = "铁矿石 制作原料",
                Type = ItemType.Material,
                Rarity = ItemRarity.Common,
                MaxStack = 999,
                SellPrice = 5,
                Weight = 0.5f,
            };
        }

        /// <summary>
        /// 创建任务物品
        /// </summary>
        public static ItemSpec CreateQuestItem(int itemId, string name)
        {
            return new ItemSpec
            {
                ItemId = itemId,
                Name = name,
                Description = "任务物品 无法丢弃",
                Type = ItemType.QuestItem,
                Rarity = ItemRarity.Common,
                MaxStack = 1,
                IsDroppable = false,
                IsSellable = false,
                IsTradable = false,
            };
        }

        /// <summary>
        /// 创建金币
        /// </summary>
        public static ItemSpec CreateGold(int itemId)
        {
            return new ItemSpec
            {
                ItemId = itemId,
                Name = "Gold",
                Description = "金币 通用货币",
                Type = ItemType.Currency,
                Rarity = ItemRarity.Common,
                MaxStack = 999999,
                Weight = 0f,
            };
        }

        /// <summary>
        /// 获取所有预设
        /// </summary>
        public static List<ItemSpec> GetAllPresets()
        {
            return new List<ItemSpec>
            {
                CreateHealthPotion(4001),
                CreateManaPotion(4002),
                CreateIronSword(4003),
                CreateLegendarySword(4004),
                CreateLeatherArmor(4005),
                CreateIronOre(4006),
                CreateQuestItem(4007, "AncientRelic"),
                CreateGold(4008),
            };
        }

        /// <summary>
        /// 注册所有预设
        /// </summary>
        public static void RegisterAll(InventorySystem system)
        {
            foreach (var spec in GetAllPresets())
            {
                system.RegisterItemSpec(spec);
            }
        }
    }

    #endregion

    #region 物品UI数据

    /// <summary>
    /// 物品UI数据
    /// </summary>
    public class ItemUIData
    {
        public int ItemId { get; set; }
        public long InstanceId { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public ItemType Type { get; set; }
        public ItemRarity Rarity { get; set; }
        public int StackCount { get; set; }
        public string IconPath { get; set; } = string.Empty;
        public bool IsBound { get; set; }
        public bool IsEquipped { get; set; }
        public EquipSlot? EquipSlot { get; set; }
        public int Durability { get; set; }
        public int MaxDurability { get; set; }
        public Dictionary<string, float> AttributeBonuses { get; set; } = new();
        public int RequiredLevel { get; set; }
        public int SellPrice { get; set; }
    }

    /// <summary>
    /// 背包UI数据
    /// </summary>
    public class InventoryUIData
    {
        public long InventoryId { get; set; }
        public string Name { get; set; } = string.Empty;
        public int SlotCount { get; set; }
        public int UsedSlotCount { get; set; }
        public List<ItemUIData?> Slots { get; set; } = new();
        public float TotalWeight { get; set; }
        public long TotalValue { get; set; }
    }

    /// <summary>
    /// 物品UI数据收集器
    /// </summary>
    public static class InventoryUIDataCollector
    {
        public static InventoryUIData Collect(Inventory inventory)
        {
            var data = new InventoryUIData
            {
                InventoryId = inventory.InventoryId,
                Name = inventory.Name,
                SlotCount = inventory.SlotCount,
                UsedSlotCount = inventory.UsedSlotCount,
                TotalWeight = inventory.GetTotalWeight(),
                TotalValue = inventory.GetTotalValue(),
            };

            foreach (var slot in inventory.GetAllSlots())
            {
                if (slot.IsEmpty)
                {
                    data.Slots.Add(null);
                }
                else
                {
                    var item = slot.Item!;
                    data.Slots.Add(new ItemUIData
                    {
                        ItemId = item.Spec.ItemId,
                        InstanceId = item.InstanceId,
                        Name = item.Spec.Name,
                        Description = item.Spec.Description,
                        Type = item.Spec.Type,
                        Rarity = item.Spec.Rarity,
                        StackCount = item.StackCount,
                        IconPath = item.Spec.IconPath,
                        IsBound = item.IsBound,
                        Durability = item.Durability,
                        MaxDurability = item.Spec.MaxDurability,
                        AttributeBonuses = item.GetAllAttributeBonuses(),
                        RequiredLevel = item.Spec.RequiredLevel,
                        SellPrice = item.Spec.SellPrice,
                    });
                }
            }

            return data;
        }
    }

    #endregion

    #region 物品交易系统

    /// <summary>
    /// 物品交易 在两个背包之间
    /// </summary>
    public class ItemTrade
    {
        public Inventory FromInventory { get; }
        public Inventory ToInventory { get; }

        public ItemTrade(Inventory from, Inventory to)
        {
            FromInventory = from ?? throw new ArgumentNullException(nameof(from));
            ToInventory = to ?? throw new ArgumentNullException(nameof(to));
        }

        /// <summary>
        /// 交易物品
        /// </summary>
        public ItemOperationResult Trade(int fromSlot, int count = -1)
        {
            var slot = FromInventory.GetSlot(fromSlot);
            if (slot == null || slot.IsEmpty) return ItemOperationResult.ItemNotFound;

            var item = slot.Item!;
            if (item.IsLocked) return ItemOperationResult.ItemLocked;
            if (!item.Spec.IsTradable) return ItemOperationResult.ItemLocked;

            int tradeCount = count < 0 ? item.StackCount : Math.Min(count, item.StackCount);

            // 创建新的物品实例
            var newItem = new ItemInstance(item.Spec)
            {
                StackCount = tradeCount,
                IsBound = false, // 交易后解绑
                Durability = item.Durability,
            };

            var result = ToInventory.AddItem(newItem);
            if (result != ItemOperationResult.Success)
                return result;

            // 从源背包移除
            FromInventory.RemoveItem(fromSlot, tradeCount);

            return ItemOperationResult.Success;
        }

        /// <summary>
        /// 购买物品
        /// </summary>
        public ItemOperationResult Buy(int itemId, int count, int price, long buyerPlayerId)
        {
            // 检查金币
            int goldItemId = 4008; // 假设金币的ID
            int goldCount = FromInventory.GetItemCount(goldItemId);

            int totalCost = price * count;
            if (goldCount < totalCost)
                return ItemOperationResult.InsufficientQuantity;

            // 扣金币
            FromInventory.RemoveItem(goldItemId, totalCost, out _);

            // 加物品
            var spec = InventorySystem.Instance.ItemSpecs.GetById(itemId);
            if (spec == null) return ItemOperationResult.ItemNotFound;

            return ToInventory.AddItem(spec, count);
        }

        /// <summary>
        /// 出售物品
        /// </summary>
        public ItemOperationResult Sell(int fromSlot, int count, long sellerPlayerId)
        {
            var slot = FromInventory.GetSlot(fromSlot);
            if (slot == null || slot.IsEmpty) return ItemOperationResult.ItemNotFound;

            var item = slot.Item!;
            if (!item.Spec.IsSellable) return ItemOperationResult.ItemLocked;
            if (item.IsBound) return ItemOperationResult.ItemLocked;

            int sellCount = count < 0 ? item.StackCount : Math.Min(count, item.StackCount);
            int totalValue = item.Spec.SellPrice * sellCount;

            // 移除物品
            var result = FromInventory.RemoveItem(fromSlot, sellCount);
            if (result != ItemOperationResult.Success) return result;

            // 加金币
            var goldSpec = InventorySystem.Instance.ItemSpecs.GetById(4008);
            if (goldSpec != null)
            {
                ToInventory.AddItem(goldSpec, totalValue);
            }

            return ItemOperationResult.Success;
        }
    }

    #endregion

    #region 背包系统初始化辅助

    /// <summary>
    /// 背包系统启动器
    /// </summary>
    public static class InventorySystemBootstrap
    {
        public static InventorySystem Initialize()
        {
            var system = InventorySystem.Instance;
            ItemFactory.RegisterAll(system);
            return system;
        }

        public static string RunSmokeTest()
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== Inventory System Smoke Test ===");

            var system = Initialize();
            sb.AppendLine($"System initialized with {system.ItemSpecs.Count} item specs");

            // 创建背包
            var inv = system.CreateInventory(20, 1);
            sb.AppendLine($"Created inventory: {inv}");

            // 添加物品
            var potionSpec = system.ItemSpecs.GetByName("HealthPotion");
            if (potionSpec != null)
            {
                inv.AddItem(potionSpec, 10);
                inv.AddItem(potionSpec, 5);
                sb.AppendLine($"Added 15 health potions: count = {inv.GetItemCount(potionSpec.ItemId)}");
            }

            var swordSpec = system.ItemSpecs.GetByName("IronSword");
            if (swordSpec != null)
            {
                inv.AddItem(swordSpec, 1);
                sb.AppendLine($"Added iron sword: count = {inv.GetItemCount(swordSpec.ItemId)}");
            }

            // 创建装备管理器
            var equipment = new EquipmentManager(inv);
            sb.AppendLine($"Created equipment manager");

            // 装备武器
            if (swordSpec != null)
            {
                var sword = inv.GetAllItems().FirstOrDefault(i => i.Spec.ItemId == swordSpec.ItemId);
                if (sword != null)
                {
                    var result = equipment.Equip(sword);
                    sb.AppendLine($"Equip sword: {result}");
                }
            }

            // 使用药水
            if (potionSpec != null)
            {
                var slots = inv.FindSlotsWithItem(potionSpec.ItemId);
                if (slots.Count > 0)
                {
                    var result = inv.UseItem(slots[0], null);
                    sb.AppendLine($"Use potion: {result}, remaining = {inv.GetItemCount(potionSpec.ItemId)}");
                }
            }

            // 排序
            inv.Sort();
            sb.AppendLine($"After sort: {inv.UsedSlotCount} slots used");

            // UI数据
            var uiData = InventoryUIDataCollector.Collect(inv);
            sb.AppendLine($"UI data: {uiData.SlotCount} slots, weight {uiData.TotalWeight:F1}, value {uiData.TotalValue}");

            system.Shutdown();
            sb.AppendLine("Test complete");
            return sb.ToString();
        }
    }

    #endregion

} // namespace ShanyuEngine.Gameplay
