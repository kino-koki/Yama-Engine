// ==================================================================================================
//  PhysicsBridge.cs — 山雨引擎物理引擎桥接系统 (重写)
// ==================================================================================================
//  命名空间: ShanyuEngine.Runtime.Physics
//  依赖:     ShanyuEngine.Physics, ShanyuEngine.ECS
//
//  功能:
//    Bridge 模式连接 ECS 组件系统与 PhysicsWorld 物理模拟。
//    每帧从 ECS Transform 同步到物理体 → 步进物理 → 结果写回 ECS Transform → 触发碰撞事件。
//
//  架构:
//    PhysicsBridgeConfig   — 配置参数（重力/步长/求解器/CCD等）
//    PhysicsBodyMapping    — Entity ↔ Body 映射关系
//    PhysicsBridge        — 核心桥接器（ECS↔PhysicsWorld 同步）
//    RaycastHit            — 射线检测结果
//    ContactPoint          — 接触点数据
// ==================================================================================================

using System;
using System.Collections.Generic;
using System.Numerics;
using ShanyuEngine.ECS;
using ShanyuEngine.Physics;

namespace ShanyuEngine.Runtime.Physics
{
    // ════════════════════════════════════════════════════════════════════════════
    //  PhysicsBridgeConfig
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 物理桥接配置参数。
    /// </summary>
    public sealed class PhysicsBridgeConfig
    {
        /// <summary>重力向量（默认 (0, -9.81, 0)）。</summary>
        public Vector3 Gravity { get; set; } = new Vector3(0, -9.81f, 0);

        /// <summary>固定步长（默认 1/60 秒）。</summary>
        public float FixedTimestep { get; set; } = 1f / 60f;

        /// <summary>每帧最大子步数。</summary>
        public int MaxSubSteps { get; set; } = 4;

        /// <summary>速度迭代次数。</summary>
        public int VelocityIterations { get; set; } = 8;

        /// <summary>位置迭代次数。</summary>
        public int PositionIterations { get; set; } = 4;

        /// <summary>是否启用连续碰撞检测 (CCD)。</summary>
        public bool EnableCCD { get; set; } = false;

        /// <summary>是否自动同步 ECS → Physics（运动学和静态体）。</summary>
        public bool AutoSyncToPhysics { get; set; } = true;

        /// <summary>是否自动同步 Physics → ECS（动态体）。</summary>
        public bool AutoSyncFromPhysics { get; set; } = true;

        /// <summary>静态体是否参与同步（通常不需要）。</summary>
        public bool SyncStaticBodies { get; set; } = false;

        public static PhysicsBridgeConfig Default => new();
    }

    // ════════════════════════════════════════════════════════════════════════════
    //  RaycastHit & ContactPoint
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>射线检测结果。</summary>
    public struct RaycastHit
    {
        /// <summary>是否命中。</summary>
        public bool HasHit;

        /// <summary>命中点。</summary>
        public Vector3 Point;

        /// <summary>命中法线。</summary>
        public Vector3 Normal;

        /// <summary>命中距离。</summary>
        public float Distance;

        /// <summary>命中的刚体索引。</summary>
        public int BodyIndex;

        /// <summary>命中的 ECS 实体 ID（如果已注册映射）。</summary>
        public int EntityId;

        public static RaycastHit Miss => new() { HasHit = false };
    }

    /// <summary>接触点数据。</summary>

    // ════════════════════════════════════════════════════════════════════════════
    //  PhysicsBodyMapping
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// ECS Entity 与 PhysicsWorld RigidBody 之间的映射记录。
    /// </summary>
    public sealed class PhysicsBodyMapping
    {
        /// <summary>ECS 实体 ID。</summary>
        public int EntityId { get; set; }

        /// <summary>PhysicsWorld 中的刚体索引。</summary>
        public int BodyIndex { get; set; }

        /// <summary>刚体引用（缓存，避免每次查询）。</summary>
        public RigidBody? Body { get; set; }

        /// <summary>是否在运动学模式下由 ECS 驱动位置。</summary>
        public bool IsKinematicDriven { get; set; }

        /// <summary>是否在上次同步时处于休眠状态。</summary>
        public bool WasSleeping { get; set; }
    }

    // ════════════════════════════════════════════════════════════════════════════
    //  PhysicsBridge — 核心桥接器
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 物理引擎桥接器。管理 ECS Entity 与 PhysicsWorld RigidBody 之间的双向同步，
    /// 并提供射线检测、碰撞事件转发等高级功能。
    /// </summary>
    public sealed class PhysicsBridge : IDisposable
    {
        // ── 子系统引用 ────────────────────────────────────────────────────────────

        private readonly EntityComponentSystem _ecs;
        private readonly PhysicsWorldReal _physicsWorld;
        private readonly PhysicsBridgeConfig _config;

        // ── 映射表 ────────────────────────────────────────────────────────────────

        private readonly Dictionary<int, PhysicsBodyMapping> _entityToBody = new();
        private readonly Dictionary<int, PhysicsBodyMapping> _bodyIndexToMapping = new();

        // ── 时间累积 ──────────────────────────────────────────────────────────────

        private float _timeAccumulator;

        // ── 事件 ──────────────────────────────────────────────────────────────────

        /// <summary>碰撞进入事件。参数为 (EntityA, EntityB, ContactPoint)。</summary>
        public event Action<int, int, ContactPoint>? OnCollisionEnter;

        /// <summary>碰撞持续事件。</summary>
        public event Action<int, int, ContactPoint>? OnCollisionStay;

        /// <summary>碰撞退出事件。</summary>
        public event Action<int, int>? OnCollisionExit;

        /// <summary>触发器进入事件。</summary>
        public event Action<int, int, Vector3>? OnTriggerEnter;

        /// <summary>触发器退出事件。</summary>
        public event Action<int, int>? OnTriggerExit;

        // ── 属性 ──────────────────────────────────────────────────────────────────

        /// <summary>物理世界实例。</summary>
        public PhysicsWorldReal PhysicsWorld => _physicsWorld;

        /// <summary>已注册的映射数量。</summary>
        public int MappingCount => _entityToBody.Count;

        /// <summary>当前配置。</summary>
        public PhysicsBridgeConfig Config => _config;

        // ─────────────────────────────────────────────────────────────────────────
        //  构造 / 释放
        // ─────────────────────────────────────────────────────────────────────────

        public PhysicsBridge(EntityComponentSystem ecs, PhysicsWorldReal? physicsWorld = null, PhysicsBridgeConfig? config = null)
        {
            _ecs = ecs ?? throw new ArgumentNullException(nameof(ecs));
            _physicsWorld = physicsWorld ?? new PhysicsWorldReal(PhysicsWorldConfig.Default);
            _config = config ?? PhysicsBridgeConfig.Default;

            // 应用配置到物理世界
            _physicsWorld.Gravity = (PhysicsVector3)_config.Gravity;
            _physicsWorld.FixedTimestep = _config.FixedTimestep;

            // 订阅物理世界事件
            _physicsWorld.OnCollisionEnter += ForwardCollisionEnter;
            _physicsWorld.OnCollisionStay += ForwardCollisionStay;
            _physicsWorld.OnCollisionExit += ForwardCollisionExit;
            _physicsWorld.OnTriggerEnter += ForwardTriggerEnter;
            _physicsWorld.OnTriggerExit += ForwardTriggerExit;
        }

        public void Dispose()
        {
            // 取消订阅
            _physicsWorld.OnCollisionEnter -= ForwardCollisionEnter;
            _physicsWorld.OnCollisionStay -= ForwardCollisionStay;
            _physicsWorld.OnCollisionExit -= ForwardCollisionExit;
            _physicsWorld.OnTriggerEnter -= ForwardTriggerEnter;
            _physicsWorld.OnTriggerExit -= ForwardTriggerExit;

            // 清除所有映射（但不销毁 ECS 实体）
            ClearMappings();
        }

        // ─────────────────────────────────────────────────────────────────────────
        //  注册 / 注销 刚体映射
        // ─────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// 为指定 ECS 实体创建一个刚体并注册到物理世界。
        /// 自动从 ECS TransformComponent 读取初始位置和旋转。
        /// </summary>
        /// <param name="entityId">ECS 实体 ID。</param>
        /// <param name="shape">碰撞形状。</param>
        /// <param name="bodyType">刚体类型。</param>
        /// <param name="mass">质量（Dynamic 类型时有效）。</param>
        /// <returns>刚体索引，失败返回 -1。</returns>
        public int CreateBody(int entityId, Shape shape, BodyType bodyType = BodyType.Dynamic, float mass = 1f)
        {
            if (!_ecs.HasEntity(entityId)) return -1;
            if (_entityToBody.ContainsKey(entityId))
            {
                // 已存在，先销毁旧的
                DestroyBody(entityId);
            }

            var transform = _ecs.GetComponent<TransformComponent>(entityId);

            var body = new RigidBody(shape, bodyType == BodyType.Dynamic ? mass : 0f)
            {
                Position = (PhysicsVector3)transform.Position,
                Rotation = (PhysicsQuaternion)transform.Rotation,
                BodyType = bodyType,
                UserData = entityId,
                Tag = _ecs.GetEntityName(entityId) ?? $"Entity_{entityId}",
            };

            // 初始化惯性
            body.UpdateInertiaWorld();

            int bodyIndex = _physicsWorld.AddBody(body);
            if (bodyIndex < 0) return -1;

            var mapping = new PhysicsBodyMapping
            {
                EntityId = entityId,
                BodyIndex = bodyIndex,
                Body = body,
                IsKinematicDriven = bodyType == BodyType.Kinematic,
            };

            _entityToBody[entityId] = mapping;
            _bodyIndexToMapping[bodyIndex] = mapping;

            return bodyIndex;
        }

        /// <summary>
        /// 注册一个已存在于 PhysicsWorld 的刚体到桥接器。
        /// 用于外部创建的刚体手动注册映射关系。
        /// </summary>
        public bool RegisterExistingBody(int entityId, int bodyIndex)
        {
            if (!_ecs.HasEntity(entityId)) return false;
            if (bodyIndex < 0 || bodyIndex >= _physicsWorld.BodyCount) return false;

            var body = _physicsWorld.GetBody(bodyIndex);
            if (body == null) return false;

            if (_entityToBody.ContainsKey(entityId))
                _entityToBody.Remove(entityId);
            if (_bodyIndexToMapping.ContainsKey(bodyIndex))
                _bodyIndexToMapping.Remove(bodyIndex);

            var mapping = new PhysicsBodyMapping
            {
                EntityId = entityId,
                BodyIndex = bodyIndex,
                Body = body,
                IsKinematicDriven = body.IsKinematic,
            };

            _entityToBody[entityId] = mapping;
            _bodyIndexToMapping[bodyIndex] = mapping;
            body.UserData = entityId;
            return true;
        }

        /// <summary>注销并销毁指定实体的刚体映射。</summary>
        public bool DestroyBody(int entityId)
        {
            if (!_entityToBody.TryGetValue(entityId, out var mapping))
                return false;

            _bodyIndexToMapping.Remove(mapping.BodyIndex);
            _entityToBody.Remove(entityId);

            _physicsWorld.RemoveBody(mapping.BodyIndex);

            // 重建索引映射（因为 RemoveBody 会导致索引变化，PhysicsWorld 内部已处理）
            RebuildBodyIndexMapping();
            return true;
        }

        /// <summary>清除所有映射。</summary>
        public void ClearMappings()
        {
            foreach (var mapping in _entityToBody.Values)
            {
                _physicsWorld.RemoveBody(mapping.BodyIndex);
            }
            _entityToBody.Clear();
            _bodyIndexToMapping.Clear();
        }

        /// <summary>获取实体的刚体映射。</summary>
        public PhysicsBodyMapping? GetMapping(int entityId)
        {
            _entityToBody.TryGetValue(entityId, out var mapping);
            return mapping;
        }

        /// <summary>获取刚体索引对应的实体 ID。</summary>
        public int GetEntityIdForBody(int bodyIndex)
        {
            return _bodyIndexToMapping.TryGetValue(bodyIndex, out var mapping)
                ? mapping.EntityId : -1;
        }

        // ─────────────────────────────────────────────────────────────────────────
        //  同步
        // ─────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// ECS → PhysicsWorld 同步。
        /// 将运动学体和静态体的 Transform 写入物理世界。
        /// 通常在物理步进之前调用。
        /// </summary>
        public void SyncToPhysics()
        {
            if (!_config.AutoSyncToPhysics) return;

            foreach (var mapping in _entityToBody.Values)
            {
                var body = mapping.Body;
                if (body == null) continue;
                if (body.IsStatic && !_config.SyncStaticBodies) continue;

                // 运动学体：ECS 驱动物理
                if (body.IsKinematic || mapping.IsKinematicDriven)
                {
                    if (!_ecs.HasEntity(mapping.EntityId)) continue;
                    var transform = _ecs.GetComponent<TransformComponent>(mapping.EntityId);
                    body.Position = (PhysicsVector3)transform.Position;
                    body.Rotation = (PhysicsQuaternion)transform.Rotation;
                    body.UpdateInertiaWorld();
                }
            }
        }

        /// <summary>
        /// PhysicsWorld → ECS 同步。
        /// 将动态体的 Transform 写回 ECS。
        /// 通常在物理步进之后调用。
        /// </summary>
        public void SyncFromPhysics()
        {
            if (!_config.AutoSyncFromPhysics) return;

            foreach (var mapping in _entityToBody.Values)
            {
                var body = mapping.Body;
                if (body == null) continue;
                if (body.IsStatic) continue;
                if (body.IsKinematic && mapping.IsKinematicDriven) continue;

                if (!_ecs.HasEntity(mapping.EntityId)) continue;

                var transform = _ecs.GetComponent<TransformComponent>(mapping.EntityId);
                transform.Position = (Vector3)body.Position;
                transform.Rotation = (Quaternion)body.Rotation;
                _ecs.SetComponent(mapping.EntityId, transform);

                // 更新休眠状态记录
                mapping.WasSleeping = body.IsSleeping;
            }
        }

        // ─────────────────────────────────────────────────────────────────────────
        //  物理步进
        // ─────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// 执行完整的物理帧更新：同步到物理 → 步进 → 同步回 ECS。
        /// 使用固定步长累积器，确保物理模拟的稳定性。
        /// </summary>
        /// <param name="deltaTime">帧间隔时间（秒）。</param>
        public void Step(float deltaTime)
        {
            _timeAccumulator += deltaTime;
            float fixedDt = _config.FixedTimestep;
            int steps = 0;

            while (_timeAccumulator >= fixedDt && steps < _config.MaxSubSteps)
            {
                SyncToPhysics();
                _physicsWorld.Step(fixedDt);
                SyncFromPhysics();

                _timeAccumulator -= fixedDt;
                steps++;
            }

            // 防止时间累积过多（卡顿后的螺旋死亡）
            if (_timeAccumulator > fixedDt * _config.MaxSubSteps)
            {
                _timeAccumulator = fixedDt * _config.MaxSubSteps;
            }
        }

        /// <summary>强制执行一次物理步进（不检查累积器）。用于编辑器单步调试。</summary>
        public void StepImmediate(float dt)
        {
            SyncToPhysics();
            _physicsWorld.Step(dt);
            SyncFromPhysics();
        }

        // ─────────────────────────────────────────────────────────────────────────
        //  查询 API
        // ─────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// 射线检测。返回最近的命中结果。
        /// </summary>
        public RaycastHit Raycast(Vector3 origin, Vector3 direction, float maxDistance = 1000f)
        {
            var hitData = new RaycastHitData();
            bool hit = _physicsWorld.Raycast(
                (PhysicsVector3)origin,
                (PhysicsVector3)direction,
                maxDistance,
                out hitData);

            if (!hit) return RaycastHit.Miss;

            int entityId = hitData.HitObject is int eid ? eid : -1;
            return new RaycastHit
            {
                HasHit = true,
                Point = (Vector3)hitData.Point,
                Normal = (Vector3)hitData.Normal,
                Distance = hitData.Distance,
                BodyIndex = entityId >= 0 ? GetMapping(entityId)?.BodyIndex ?? -1 : -1,
                EntityId = entityId,
            };
        }

        /// <summary>
        /// 射线检测所有命中（排序由近到远）。
        /// </summary>
        public List<RaycastHit> RaycastAll(Vector3 origin, Vector3 direction, float maxDistance = 1000f)
        {
            var results = new List<RaycastHit>();
            // PhysicsWorld 目前只提供单命中 Raycast，这里简单多次采样
            // 实际项目中应扩展 PhysicsWorld 支持 AllHits
            var hit = Raycast(origin, direction, maxDistance);
            if (hit.HasHit) results.Add(hit);
            return results;
        }

        /// <summary>
        /// AABB 重叠测试。返回所有与给定 AABB 重叠的实体 ID。
        /// </summary>
        public List<int> OverlapTest(Vector3 center, Vector3 halfExtents)
        {
            var aabb = new AABBShape(
                (PhysicsVector3)(center - halfExtents),
                (PhysicsVector3)(center + halfExtents));

            var bodyIndices = _physicsWorld.OverlapTest(aabb);
            var entityIds = new List<int>();
            foreach (var idx in bodyIndices)
            {
                int entityId = GetEntityIdForBody(idx);
                if (entityId >= 0) entityIds.Add(entityId);
            }
            return entityIds;
        }

        // ─────────────────────────────────────────────────────────────────────────
        //  力/冲量 辅助
        // ─────────────────────────────────────────────────────────────────────────

        /// <summary>对指定实体施加中心力。</summary>
        public void ApplyForce(int entityId, Vector3 force)
        {
            if (!_entityToBody.TryGetValue(entityId, out var mapping)) return;
            mapping.Body?.ApplyCentralForce((PhysicsVector3)force);
        }

        /// <summary>对指定实体施加中心冲量。</summary>
        public void ApplyImpulse(int entityId, Vector3 impulse)
        {
            if (!_entityToBody.TryGetValue(entityId, out var mapping)) return;
            mapping.Body?.ApplyImpulse((PhysicsVector3)impulse, mapping.Body.Position);
        }

        /// <summary>对指定实体在世界点施加力。</summary>
        public void ApplyForceAtPoint(int entityId, Vector3 force, Vector3 worldPoint)
        {
            if (!_entityToBody.TryGetValue(entityId, out var mapping)) return;
            mapping.Body?.ApplyForceAtPoint((PhysicsVector3)force, (PhysicsVector3)worldPoint);
        }

        /// <summary>设置实体线速度。</summary>
        public void SetLinearVelocity(int entityId, Vector3 velocity)
        {
            if (!_entityToBody.TryGetValue(entityId, out var mapping)) return;
            if (mapping.Body != null)
                mapping.Body.LinearVelocity = (PhysicsVector3)velocity;
        }

        /// <summary>获取实体线速度。</summary>
        public Vector3 GetLinearVelocity(int entityId)
        {
            if (!_entityToBody.TryGetValue(entityId, out var mapping)) return Vector3.Zero;
            return mapping.Body != null ? (Vector3)mapping.Body.LinearVelocity : Vector3.Zero;
        }

        /// <summary>唤醒实体。</summary>
        public void WakeUp(int entityId)
        {
            if (!_entityToBody.TryGetValue(entityId, out var mapping)) return;
            if (mapping.Body != null)
                mapping.Body.State = BodyState.Awake;
        }

        // ─────────────────────────────────────────────────────────────────────────
        //  事件转发 (PhysicsWorld → ECS/脚本)
        // ─────────────────────────────────────────────────────────────────────────

        private void ForwardCollisionEnter(CollisionEvent ev)
        {
            int entityA = GetEntityIdForBody(ev.BodyIndexA);
            int entityB = GetEntityIdForBody(ev.BodyIndexB);
            if (entityA < 0 && entityB < 0) return;

            var contact = new ContactPoint
            {
                Position = (Vector3)ev.ContactPoint,
                Normal = (Vector3)ev.ContactNormal,
                PenetrationDepth = ev.PenetrationDepth,
                BodyIndexA = ev.BodyIndexA,
                BodyIndexB = ev.BodyIndexB,
                EntityIdA = entityA,
                EntityIdB = entityB,
            };
            OnCollisionEnter?.Invoke(entityA, entityB, contact);
        }

        private void ForwardCollisionStay(CollisionEvent ev)
        {
            int entityA = GetEntityIdForBody(ev.BodyIndexA);
            int entityB = GetEntityIdForBody(ev.BodyIndexB);
            if (entityA < 0 && entityB < 0) return;

            var contact = new ContactPoint
            {
                Position = (Vector3)ev.ContactPoint,
                Normal = (Vector3)ev.ContactNormal,
                PenetrationDepth = ev.PenetrationDepth,
                BodyIndexA = ev.BodyIndexA,
                BodyIndexB = ev.BodyIndexB,
                EntityIdA = entityA,
                EntityIdB = entityB,
            };
            OnCollisionStay?.Invoke(entityA, entityB, contact);
        }

        private void ForwardCollisionExit(CollisionEvent ev)
        {
            int entityA = GetEntityIdForBody(ev.BodyIndexA);
            int entityB = GetEntityIdForBody(ev.BodyIndexB);
            if (entityA >= 0 || entityB >= 0)
                OnCollisionExit?.Invoke(entityA, entityB);
        }

        private void ForwardTriggerEnter(TriggerEvent ev)
        {
            int entityA = GetEntityIdForBody(ev.BodyIndexA);
            int entityB = GetEntityIdForBody(ev.BodyIndexB);
            if (entityA >= 0 || entityB >= 0)
                OnTriggerEnter?.Invoke(entityA, entityB, (Vector3)ev.ContactPoint);
        }

        private void ForwardTriggerExit(TriggerEvent ev)
        {
            int entityA = GetEntityIdForBody(ev.BodyIndexA);
            int entityB = GetEntityIdForBody(ev.BodyIndexB);
            if (entityA >= 0 || entityB >= 0)
                OnTriggerExit?.Invoke(entityA, entityB);
        }

        // ─────────────────────────────────────────────────────────────────────────
        //  辅助
        // ─────────────────────────────────────────────────────────────────────────

        private void RebuildBodyIndexMapping()
        {
            _bodyIndexToMapping.Clear();
            foreach (var mapping in _entityToBody.Values)
            {
                // 重新查找 body 索引（因为 RemoveBody 后索引可能变化）
                var body = mapping.Body;
                if (body == null) continue;

                // PhysicsWorld 内部不保证索引不变，但我们可以通过遍历查找
                for (int i = 0; i < _physicsWorld.BodyCount; i++)
                {
                    if (_physicsWorld.GetBody(i) == body)
                    {
                        mapping.BodyIndex = i;
                        _bodyIndexToMapping[i] = mapping;
                        break;
                    }
                }
            }
        }
    }
}
