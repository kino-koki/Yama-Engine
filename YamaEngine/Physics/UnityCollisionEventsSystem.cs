using System;
using System.Collections.Generic;
using System.Numerics;

namespace YamaEngine.Physics
{
    // ============================================================================================
    //  UnityCollisionEventsSystem.cs
    //
    //  碰撞事件系统 — 类似 Unity OnCollisionEnter/Stay/Exit + OnTriggerEnter/Stay/Exit。
    //
    //  功能：
    //    - 碰撞事件（Enter / Stay / Exit）
    //    - 触发器事件（Enter / Stay / Exit）
    //    - 事件数据结构（CollisionEvent：碰撞体对/接触点/法线/脉冲/相对速度）
    //    - 事件缓冲（帧内收集，帧末分发）
    //    - 事件过滤（忽略层级/自定义回调）
    //    - 持久接触管理器（记录持续接触状态）
    // ============================================================================================

    /// <summary>
    ///   碰撞事件阶段。
    /// </summary>
    public enum CollisionEventStage
    {
        /// <summary>碰撞开始。</summary>
        Enter,
        /// <summary>碰撞持续中（每帧触发）。</summary>
        Stay,
        /// <summary>碰撞结束。</summary>
        Exit,
    }

    /// <summary>
    ///   碰撞事件数据结构。包含完整的碰撞信息。
    /// </summary>
    public struct CollisionEvent
    {
        /// <summary>事件阶段。</summary>
        public CollisionEventStage Stage;

        /// <summary>第一个物体的 BodyId。</summary>
        public uint BodyIdA;

        /// <summary>第二个物体的 BodyId。</summary>
        public uint BodyIdB;

        /// <summary>第一个物体的实体 ID。</summary>
        public int EntityA;

        /// <summary>第二个物体的实体 ID。</summary>
        public int EntityB;

        /// <summary>平均接触点（世界空间）。</summary>
        public Vector3 ContactPoint;

        /// <summary>接触法线（从 B 指向 A）。</summary>
        public Vector3 Normal;

        /// <summary>穿透深度（米）。</summary>
        public float PenetrationDepth;

        /// <summary>接触点数量。</summary>
        public int ContactPointCount;

        /// <summary>相对速度（m/s）。A 相对于 B 的速度。</summary>
        public Vector3 RelativeVelocity;

        /// <summary>碰撞脉冲大小。</summary>
        public float Impulse;

        /// <summary>是否为触发器事件。</summary>
        public bool IsTrigger;

        /// <summary>第一个物体的碰撞层。</summary>
        public PhysicsLayer LayerA;

        /// <summary>第二个物体的碰撞层。</summary>
        public PhysicsLayer LayerB;

        /// <summary>第一个物体的材质（如可用）。</summary>
        public PhysicsMaterial? MaterialA;

        /// <summary>第二个物体的材质（如可用）。</summary>
        public PhysicsMaterial? MaterialB;

        /// <summary>碰撞体A在物理世界中的索引。</summary>
        public int BodyIndexA;

        /// <summary>碰撞体B在物理世界中的索引。</summary>
        public int BodyIndexB;

        /// <summary>接触法线（从B指向A）。</summary>
        public Vector3 ContactNormal;

        public override string ToString()
        {
            return $"[{Stage}] Entity{EntityA} <-> Entity{EntityB} " +
                   $"Contact={ContactPoint} Normal={Normal} " +
                   $"Depth={PenetrationDepth:F4} IsTrigger={IsTrigger}";
        }
    }

    /// <summary>
    ///   碰撞事件过滤回调。返回 true 表示允许事件分发，false 表示丢弃。
    /// </summary>
    public delegate bool CollisionEventFilterCallback(in CollisionEvent evt);

    /// <summary>
    ///   持久接触管理器。记录物体对之间的持续接触状态，
    ///   并区分碰撞体与触发器。
    /// </summary>
    public sealed class PersistentContactManager
    {
        // 当前活跃的接触对 (bodyIdA, bodyIdB) -> isSensor
        private readonly Dictionary<ulong, bool> _activeContacts = new();
        // 上一帧的接触对快照
        private readonly HashSet<ulong> _previousContacts = new();

        /// <summary>当前活跃接触对数。</summary>
        public int ActiveContactCount => _activeContacts.Count;

        /// <summary>
        ///   记录新增接触。
        /// </summary>
        public void AddContact(uint bodyA, uint bodyB, bool isSensor)
        {
            ulong key = MakePairKey(bodyA, bodyB);
            if (!_activeContacts.ContainsKey(key))
                _activeContacts.Add(key, isSensor);
        }

        /// <summary>
        ///   移除接触。
        /// </summary>
        public bool RemoveContact(uint bodyA, uint bodyB)
        {
            ulong key = MakePairKey(bodyA, bodyB);
            return _activeContacts.Remove(key);
        }

        /// <summary>
        ///   检查接触是否已存在。
        /// </summary>
        public bool HasContact(uint bodyA, uint bodyB)
        {
            ulong key = MakePairKey(bodyA, bodyB);
            return _activeContacts.ContainsKey(key);
        }

        /// <summary>
        ///   检查接触是否为触发器。
        /// </summary>
        public bool IsSensorContact(uint bodyA, uint bodyB)
        {
            ulong key = MakePairKey(bodyA, bodyB);
            return _activeContacts.TryGetValue(key, out bool isSensor) && isSensor;
        }

        /// <summary>
        ///   开始新帧：将当前快照保存为上一帧。
        /// </summary>
        public void BeginFrame()
        {
            _previousContacts.Clear();
            foreach (var kvp in _activeContacts)
                _previousContacts.Add(kvp.Key);
        }

        /// <summary>
        ///   获取本帧新增的接触。
        /// </summary>
        public List<(uint a, uint b, bool isSensor)> GetNewContacts()
        {
            var result = new List<(uint, uint, bool)>();
            foreach (var kvp in _activeContacts)
            {
                if (!_previousContacts.Contains(kvp.Key))
                    result.Add((UnpackA(kvp.Key), UnpackB(kvp.Key), kvp.Value));
            }
            return result;
        }

        /// <summary>
        ///   获取本帧结束的接触。
        /// </summary>
        public List<(uint a, uint b)> GetEndedContacts()
        {
            var result = new List<(uint, uint)>();
            foreach (var prev in _previousContacts)
            {
                if (!_activeContacts.ContainsKey(prev))
                    result.Add((UnpackA(prev), UnpackB(prev)));
            }
            return result;
        }

        /// <summary>
        ///   清空所有记录。
        /// </summary>
        public void Clear()
        {
            _activeContacts.Clear();
            _previousContacts.Clear();
        }

        private static ulong MakePairKey(uint a, uint b)
        {
            uint lo = Math.Min(a, b);
            uint hi = Math.Max(a, b);
            return ((ulong)hi << 32) | lo;
        }

        private static uint UnpackA(ulong key) => (uint)(key & 0xFFFFFFFF);
        private static uint UnpackB(ulong key) => (uint)(key >> 32);
    }

    /// <summary>
    ///   碰撞事件系统。收集碰撞事件并分发。
    ///   在每帧物理步进后调用 DispatchEvents 触发的回调。
    /// </summary>
    public sealed class CollisionEventSystem
    {
        private readonly PhysicsWorld _world;
        private readonly PersistentContactManager _contactMgr = new();
        private readonly List<CollisionEvent> _eventBuffer = new();

        /// <summary>碰撞事件过滤回调（可选）。返回 false 以丢弃事件。</summary>
        public CollisionEventFilterCallback? EventFilter { get; set; }

        // ---- 碰撞事件 ----

        /// <summary>碰撞开始 (entityA, entityB)。</summary>
        public event Action<int, int, CollisionEvent>? OnCollisionEnter;

        /// <summary>碰撞持续 (entityA, entityB, event)。</summary>
        public event Action<int, int, CollisionEvent>? OnCollisionStay;

        /// <summary>碰撞结束 (entityA, entityB)。</summary>
        public event Action<int, int>? OnCollisionExit;

        // ---- 触发器事件 ----

        /// <summary>触发器开始 (entityA, entityB)。</summary>
        public event Action<int, int, CollisionEvent>? OnTriggerEnter;

        /// <summary>触发器持续 (entityA, entityB, event)。</summary>
        public event Action<int, int, CollisionEvent>? OnTriggerStay;

        /// <summary>触发器结束 (entityA, entityB)。</summary>
        public event Action<int, int>? OnTriggerExit;

        // ---- 原始 BodyId 事件（低级 access） ----

        /// <summary>碰撞开始（BodyId 版本）。</summary>
        public event Action<uint, uint, CollisionEvent>? OnRawCollisionEnter;

        /// <summary>碰撞结束（BodyId 版本）。</summary>
        public event Action<uint, uint>? OnRawCollisionExit;

        /// <summary>当前活跃接触对数量。</summary>
        public int ActiveContactCount => _contactMgr.ActiveContactCount;

        /// <summary>本帧缓冲中的事件数量。</summary>
        public int BufferedEventCount => _eventBuffer.Count;

        internal CollisionEventSystem(PhysicsWorld world)
        {
            _world = world ?? throw new ArgumentNullException(nameof(world));
        }

        /// <summary>
        ///   由 PhysicsWorld 的接触监听器调用，在本帧物理步进期间收集事件。
        /// </summary>
        internal void OnContactAdded(NativeContactManifold manifold)
        {
            uint bodyA = manifold.BodyIdA;
            uint bodyB = manifold.BodyIdB;

            bool isSensorA = IsTrackedSensor(bodyA);
            bool isSensorB = IsTrackedSensor(bodyB);
            bool isSensor = isSensorA || isSensorB;

            int entityA = _world.GetEntityId(bodyA);
            int entityB = _world.GetEntityId(bodyB);

            bool isNew = !_contactMgr.HasContact(bodyA, bodyB);
            _contactMgr.AddContact(bodyA, bodyB, isSensor);

            var evt = BuildEvent(CollisionEventStage.Enter, bodyA, bodyB,
                entityA, entityB, manifold, isSensor);

            // 过滤
            if (EventFilter != null && !EventFilter(evt))
                return;

            _eventBuffer.Add(evt);

            if (isNew)
            {
                if (isSensor)
                    OnTriggerEnter?.Invoke(entityA, entityB, evt);
                else
                    OnCollisionEnter?.Invoke(entityA, entityB, evt);

                OnRawCollisionEnter?.Invoke(bodyA, bodyB, evt);
            }
            else
            {
                if (isSensor)
                    OnTriggerStay?.Invoke(entityA, entityB, evt);
                else
                    OnCollisionStay?.Invoke(entityA, entityB, evt);
            }
        }

        /// <summary>
        ///   由 PhysicsWorld 的接触移除监听器调用。
        /// </summary>
        internal void OnContactRemoved(uint bodyIdA, uint bodyIdB)
        {
            bool wasSensor = _contactMgr.IsSensorContact(bodyIdA, bodyIdB);
            _contactMgr.RemoveContact(bodyIdA, bodyIdB);

            int entityA = _world.GetEntityId(bodyIdA);
            int entityB = _world.GetEntityId(bodyIdB);

            if (wasSensor)
                OnTriggerExit?.Invoke(entityA, entityB);
            else
                OnCollisionExit?.Invoke(entityA, entityB);

            OnRawCollisionExit?.Invoke(bodyIdA, bodyIdB);
        }

        /// <summary>
        ///   由 PhysicsWorld 的接触持久化监听器调用（每帧持续接触）。
        /// </summary>
        internal void OnContactPersisted(NativeContactManifold manifold)
        {
            uint bodyA = manifold.BodyIdA;
            uint bodyB = manifold.BodyIdB;

            bool isSensorA = IsTrackedSensor(bodyA);
            bool isSensorB = IsTrackedSensor(bodyB);
            bool isSensor = isSensorA || isSensorB;

            int entityA = _world.GetEntityId(bodyA);
            int entityB = _world.GetEntityId(bodyB);

            var evt = BuildEvent(CollisionEventStage.Stay, bodyA, bodyB,
                entityA, entityB, manifold, isSensor);

            if (EventFilter != null && !EventFilter(evt))
                return;

            _eventBuffer.Add(evt);

            if (isSensor)
                OnTriggerStay?.Invoke(entityA, entityB, evt);
            else
                OnCollisionStay?.Invoke(entityA, entityB, evt);
        }

        /// <summary>
        ///   在物理步进后分发所有缓冲事件并清理。
        /// </summary>
        public void DispatchEvents()
        {
            // 事件已经在 OnContactAdded/Removed/Persisted 中实时分发，
            // 此方法清空缓冲区以便下一帧使用。
            _eventBuffer.Clear();
        }

        /// <summary>
        ///   清空所有状态。
        /// </summary>
        public void Clear()
        {
            _contactMgr.Clear();
            _eventBuffer.Clear();
        }

        /// <summary>
        ///   获取持久接触管理器（用于外部查询）。
        /// </summary>
        public PersistentContactManager GetContactManager() => _contactMgr;

        // ========================================================================================
        //  内部辅助
        // ========================================================================================

        private CollisionEvent BuildEvent(
            CollisionEventStage stage,
            uint bodyA, uint bodyB,
            int entityA, int entityB,
            NativeContactManifold manifold,
            bool isSensor)
        {
            return new CollisionEvent
            {
                Stage = stage,
                BodyIdA = bodyA,
                BodyIdB = bodyB,
                EntityA = entityA,
                EntityB = entityB,
                ContactPoint = manifold.AverageContactPoint,
                Normal = manifold.AverageNormal,
                PenetrationDepth = manifold.PenetrationDepth,
                ContactPointCount = manifold.ContactPointCount,
                RelativeVelocity = Vector3.Zero, // 由外部计算
                Impulse = 0f,
                IsTrigger = isSensor,
                LayerA = PhysicsLayer.Default,
                LayerB = PhysicsLayer.Default,
            };
        }

        private bool IsTrackedSensor(uint bodyId)
        {
            // 通过 PhysicsWorld 内部字段检查
            try
            {
                var field = typeof(PhysicsWorld).GetField("_sensors",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (field?.GetValue(_world) is HashSet<uint> sensors)
                    return sensors.Contains(bodyId);
            }
            catch { }
            return false;
        }
    }
}
