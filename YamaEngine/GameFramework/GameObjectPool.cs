using System;
using System.Collections.Generic;

namespace YamaEngine.GameFramework
{
    // ════════════════════════════════════════════════════════════════════════
    // yama引擎 - 游戏对象池 (GameObjectPool)
    //
    //   复用 GameObjectReal 实例以避免频繁 Alloc/GC。
    //   Get() → 取出（隐藏、复位变换）；Release() → 归还（隐藏）。
    //   对应 Godot 的场景实例化池 / UE 的对象池。
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 对象池：预生成 GameObjectReal 实例并在 Get/Release 间复用。
    /// 池中对象取出时自动激活，归还时自动停用并复位到池的根下。
    /// </summary>
    public sealed class GameObjectPool
    {
        private readonly GameObjectReal _prototype;
        private readonly List<GameObjectReal> _available = new();
        private readonly HashSet<GameObjectReal> _leased = new();
        private readonly int _maxSize;

        /// <summary>当前池中可复用的空闲对象数。</summary>
        public int AvailableCount => _available.Count;

        /// <summary>当前已被租借（活跃）的对象数。</summary>
        public int LeasedCount => _leased.Count;

        /// <summary>池中对象总数（空闲+租借）。</summary>
        public int TotalCount => _available.Count + _leased.Count;

        /// <summary>池容量上限。超出后 Release 的对象将被直接销毁（丢弃）。</summary>
        public int MaxSize => _maxSize;

        /// <summary>
        /// 构造对象池。
        /// </summary>
        /// <param name="prototype">用于克隆的模板对象（不会被直接租出）。</param>
        /// <param name="initialCapacity">预生成的对象数量。</param>
        /// <param name="maxSize">池容量上限（0 = 无上限）。</param>
        public GameObjectPool(GameObjectReal prototype, int initialCapacity = 0, int maxSize = 0)
        {
            _prototype = prototype ?? throw new ArgumentNullException(nameof(prototype));
            _maxSize = maxSize;
            for (int i = 0; i < initialCapacity; i++)
                _available.Add(CreateInstance());
        }

        private GameObjectReal CreateInstance()
        {
            var inst = _prototype.Instantiate();
            inst.SetActive(false); // 空闲对象保持停用
            return inst;
        }

        /// <summary>
        /// 从池中取出一个对象（激活状态）。池空时按需新建（若未超上限）。
        /// 返回 null 表示已达到 MaxSize 上限且无空闲对象。
        /// </summary>
        public GameObjectReal Get()
        {
            GameObjectReal inst;
            if (_available.Count > 0)
            {
                int last = _available.Count - 1;
                inst = _available[last];
                _available.RemoveAt(last);
            }
            else
            {
                if (_maxSize > 0 && TotalCount >= _maxSize)
                    return null; // 达到上限且无空闲
                inst = CreateInstance();
            }
            inst.SetActive(true);
            _leased.Add(inst);
            return inst;
        }

        /// <summary>
        /// 归还对象到池中。对象被停用。
        /// 若池已满（达到 MaxSize），对象被丢弃（不进入空闲列表）。
        /// </summary>
        public bool Release(GameObjectReal instance)
        {
            if (instance == null || !_leased.Remove(instance))
                return false; // 不是本池租出的对象

            instance.SetActive(false);

            if (_maxSize > 0 && _available.Count >= _maxSize)
                return false; // 超出容量，丢弃（保持停用，由调用方处置）

            _available.Add(instance);
            return true;
        }

        /// <summary>清空池中所有空闲对象（租借中的对象不受影响）。</summary>
        public int ClearAvailable()
        {
            int n = _available.Count;
            _available.Clear();
            return n;
        }
    }
}
