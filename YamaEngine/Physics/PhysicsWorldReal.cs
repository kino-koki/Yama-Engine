// ============================================================================================
//  PhysicsWorldReal.cs
//
//  yama引擎 (YamaEngine) 真实物理世界模块 —— 纯手写实现，无外部物理库依赖。
//
//  内容：
//    - PhysicsWorld：物理世界（重力/步长/子步数/碰撞矩阵/所有刚体/所有约束/接触流形缓存）
//    - Step(dt)：完整物理步进流水线
//      1. 广相位碰撞检测 → 候选对
//      2. 窄相位碰撞检测 → 接触流形
//      3. 接触流形持久化
//      4. 构建碰撞岛（IslandBuilder：连通图/DFS）
//      5. 求解约束（每个岛独立求解）
//      6. 积分（速度→位置更新）
//      7. 位置修正（穿透修正）
//      8. 休眠检测
//    - AddBody/RemoveBody / AddConstraint/RemoveConstraint
//    - Raycast/SweepTest/OverlapTest
//    - 事件回调系统
//
//  命名空间：YamaEngine.Physics
//  目标框架：net8.0 / C# 8.0+
//  外部依赖：仅 System.Numerics
// ============================================================================================

using System;
using System.Collections.Generic;
using System.Numerics;

namespace YamaEngine.Physics
{
    /// <summary>碰撞事件信息。</summary>

    /// <summary>触发器事件信息。</summary>
    public struct TriggerEvent
    {
        public int BodyIndexA;
        public int BodyIndexB;
        public PhysicsVector3 ContactPoint;
    }

    /// <summary>物理世界配置。</summary>
    public struct PhysicsWorldConfig
    {
        /// <summary>重力向量（默认 (0, -9.81, 0)）。</summary>
        public PhysicsVector3 Gravity;

        /// <summary>固定步长（默认 1/60）。</summary>
        public float FixedTimestep;

        /// <summary>子步数（每步再细分的次数）。</summary>
        public int SubSteps;

        /// <summary>速度迭代次数。</summary>
        public int VelocityIterations;

        /// <summary>位置迭代次数。</summary>
        public int PositionIterations;

        /// <summary>最大刚体数。</summary>
        public int MaxBodies;

        public static readonly PhysicsWorldConfig Default = new PhysicsWorldConfig
        {
            Gravity = new PhysicsVector3(0, -9.81f, 0),
            FixedTimestep = 1.0f / 60.0f,
            SubSteps = 1,
            VelocityIterations = 8,
            PositionIterations = 4,
            MaxBodies = 4096,
        };
    }

    /// <summary>物理世界 —— 所有物理模拟的容器和调度器。</summary>
    public class PhysicsWorldReal : IPhysicsWorld
    {
        /// <summary>全局物理世界单例。由引擎初始化时设置。</summary>
        public static PhysicsWorldReal? Instance { get; set; }

        #region 字段

        private PhysicsVector3 _gravity;
        private float _fixedTimestep;
        private int _subSteps;
        private SolverSettings _solverSettings;

        private List<RigidBody> _bodies = new List<RigidBody>();
        private Dictionary<int, int> _bodyToIndex = new Dictionary<int, int>();
        private List<Constraint> _constraints = new List<Constraint>();
        private List<ContactManifold> _contactManifolds = new List<ContactManifold>();
        private List<ContactManifold> _prevManifolds = new List<ContactManifold>();

        private DynamicAABBTree _broadphase = new DynamicAABBTree();
        private ShapeCollisionDispatcher _collisionDispatcher = new ShapeCollisionDispatcher();
        private SequentialImpulseSolver _velocitySolver;
        private PositionConstraintSolver _positionSolver;
        private SymplecticEulerIntegrator _integrator = new SymplecticEulerIntegrator();
        private SleepManager _sleepManager;

        private Dictionary<int, int> _proxyToBody = new Dictionary<int, int>();

        private List<CollisionEvent> _collisionEnterEvents = new List<CollisionEvent>();
        private List<CollisionEvent> _collisionStayEvents = new List<CollisionEvent>();
        private List<CollisionEvent> _collisionExitEvents = new List<CollisionEvent>();
        private List<TriggerEvent> _triggerEnterEvents = new List<TriggerEvent>();
        private List<TriggerEvent> _triggerExitEvents = new List<TriggerEvent>();

        private HashSet<(int a, int b)> _activeCollisionPairs = new HashSet<(int a, int b)>();
        private List<IForceField> _forceFields = new List<IForceField>();

        private float _timeAccumulator;
        private float _currentStepTime;

        #endregion

        #region 构造

        public PhysicsWorldReal(PhysicsWorldConfig config = default)
        {
            if (config.FixedTimestep <= 0) config = PhysicsWorldConfig.Default;
            _gravity = config.Gravity;
            _fixedTimestep = config.FixedTimestep;
            _subSteps = config.SubSteps;

            _solverSettings = new SolverSettings
            {
                VelocityIterations = config.VelocityIterations,
                PositionIterations = config.PositionIterations,
                BaumgarteFactor = 0.2f,
                Slop = 0.005f,
                RestitutionVelocityThreshold = 1.0f,
                WarmstartFactor = 0.8f,
            };

            _velocitySolver = new SequentialImpulseSolver(_solverSettings);
            _positionSolver = new PositionConstraintSolver(_solverSettings);
            _sleepManager = new SleepManager(SleepSettings.Default);
        }

        #endregion

        #region 公共属性

        public PhysicsVector3 Gravity { get => _gravity; set => _gravity = value; }
        public float FixedTimestep { get => _fixedTimestep; set => _fixedTimestep = Math.Max(value, 0.0001f); }
        public int SubSteps { get => _subSteps; set => _subSteps = Math.Max(value, 1); }
        public int BodyCount => _bodies.Count;

        /// <summary>当前活跃（非休眠）刚体数。</summary>
        public int ActiveBodyCount
        {
            get
            {
                int active = 0;
                for (int i = 0; i < _bodies.Count; i++)
                    if (!_bodies[i].IsSleeping) active++;
                return active;
            }
        }

        /// <summary>原生系统句柄（供 Unity* 桥接系统使用；纯 C# 世界为空输出且不持有原生系统）。</summary>
        public YamaEngine.Physics.PhysicsSystemHandle NativeHandle => default;

        /// <summary>清空世界（移除所有刚体、约束、接触流形与事件缓存）。</summary>
        public void Clear()
        {
            _bodies.Clear();
            _bodyToIndex.Clear();
            _constraints.Clear();
            _contactManifolds.Clear();
            _prevManifolds.Clear();
            _proxyToBody.Clear();
            _broadphase.Clear();
            _activeCollisionPairs.Clear();
            _collisionEnterEvents.Clear();
            _collisionStayEvents.Clear();
            _collisionExitEvents.Clear();
            _triggerEnterEvents.Clear();
            _triggerExitEvents.Clear();
            _sleepManager?.Reset();
        }

        /// <summary>物理世界暂停状态。暂停时 Step() 为空操作。</summary>
        public bool IsPaused { get; set; }

        public IReadOnlyList<RigidBody> Bodies => _bodies;
        public IReadOnlyList<Constraint> Constraints => _constraints;
        public IReadOnlyList<ContactManifold> ContactManifolds => _contactManifolds;

        #endregion

        #region 物体管理

        /// <summary>添加刚体到物理世界。</summary>
        public int AddBody(RigidBody body)
        {
            if (body == null) return -1;

            int index = _bodies.Count;
            body.UpdateInertiaWorld();
            _bodies.Add(body);
            _bodyToIndex[body.GetHashCode()] = index;

            // 插入宽相位
            if (body.CollisionShape != null)
            {
                AABBShape aabb = body.ComputeWorldAABB();
                int proxyId = _broadphase.Insert(aabb, index);
                _proxyToBody[proxyId] = index;
            }

            return index;
        }

        /// <summary>移除刚体。</summary>
        public void RemoveBody(int bodyIndex)
        {
            if (bodyIndex < 0 || bodyIndex >= _bodies.Count) return;

            // 从宽相位移除
            foreach (var kvp in _proxyToBody)
            {
                if (kvp.Value == bodyIndex)
                {
                    _broadphase.Remove(kvp.Key);
                    break;
                }
            }

            _bodies.RemoveAt(bodyIndex);
            // 重建索引映射
            _bodyToIndex.Clear();
            for (int i = 0; i < _bodies.Count; i++)
                _bodyToIndex[_bodies[i].GetHashCode()] = i;
        }

        /// <summary>根据索引获取刚体。</summary>
        public RigidBody GetBody(int index)
        {
            if (index < 0 || index >= _bodies.Count) return null;
            return _bodies[index];
        }

        #endregion

        #region 约束管理

        /// <summary>添加约束。</summary>
        public void AddConstraint(Constraint constraint)
        {
            if (constraint == null) return;
            _constraints.Add(constraint);
        }

        /// <summary>移除约束。</summary>
        public void RemoveConstraint(Constraint constraint)
        {
            if (constraint == null) return;
            _constraints.Remove(constraint);
        }

        #endregion

        #region 力场

        /// <summary>添加自定义力场。</summary>
        public void AddForceField(IForceField field) => _forceFields.Add(field);

        /// <summary>移除力场。</summary>
        public void RemoveForceField(IForceField field) => _forceFields.Remove(field);

        #endregion

        #region 物理步进

        /// <summary>执行一次物理步进（固定步长）。</summary>
        public void Step()
        {
            Step(_fixedTimestep);
        }

        /// <summary>执行物理步进。</summary>
        public void Step(float dt)
        {
            if (IsPaused) return;
            float subDt = dt / _subSteps;

            for (int sub = 0; sub < _subSteps; sub++)
            {
                StepInternal(subDt);
            }
        }

        /// <summary>物理步进流水线（核心）。</summary>
        private void StepInternal(float dt)
        {
            // 空场景保护：无刚体时直接返回，避免后续流程出错
            if (_bodies.Count == 0)
                return;

            _currentStepTime += dt;

            // ===== 1. 计算力 =====
            for (int i = 0; i < _bodies.Count; i++)
            {
                var body = _bodies[i];
                if (body.IsSleeping || body.IsStatic) continue;
                ForceComputer.ComputeForces(body, _gravity,
                    PhysicsVector3.Zero, _forceFields, _currentStepTime, dt);
            }

            // ===== 2. 广相位碰撞检测 =====
            List<(int a, int b)> candidatePairs = BroadphaseDetection(dt);

            // ===== 3. 窄相位碰撞检测 → 生成接触流形 =====
            _prevManifolds = new List<ContactManifold>(_contactManifolds);
            _contactManifolds.Clear();

            foreach (var pair in candidatePairs)
            {
                var bodyA = _bodies[pair.a];
                var bodyB = _bodies[pair.b];

                // 跳过两个静态体的碰撞
                if (bodyA.IsStatic && bodyB.IsStatic) continue;
                // 跳过两个休眠体的碰撞
                if (bodyA.IsSleeping && bodyB.IsSleeping) continue;

                // 碰撞过滤
                if (!CollisionFilterUtils.ShouldCollide(
                    bodyA.CollisionLayer, bodyA.CollisionMask, bodyA.CollisionGroup,
                    bodyB.CollisionLayer, bodyB.CollisionMask, bodyB.CollisionGroup))
                    continue;

                // 窄相位
                if (bodyA.CollisionShape == null || bodyB.CollisionShape == null) continue;

                var tA = bodyA.GetTransform();
                var tB = bodyB.GetTransform();

                if (_collisionDispatcher.TestCollision(
                    bodyA.CollisionShape, tA, bodyB.CollisionShape, tB,
                    out PhysicsVector3 normal, out float penetration))
                {
                    var manifold = new ContactManifold
                    {
                        BodyIndexA = pair.a,
                        BodyIndexB = pair.b,
                        Normal = normal,
                        Timestamp = _currentStepTime,
                    };
                    manifold.Points.Add(new ContactPoint
                    {
                        Position = (tA.Position + tB.Position) * 0.5f,
                        Normal = normal,
                        PenetrationDepth = penetration,
                        ContactID = (pair.a * 10000 + pair.b),
                        ShapeIndexA = pair.a,
                        ShapeIndexB = pair.b,
                    });

                    // 持久化：从旧流形继承冲量
                    var oldManifold = FindOldManifold(pair.a, pair.b);
                    if (oldManifold != null)
                        manifold.PersistFrom(oldManifold);
                    manifold.Normalize();

                    _contactManifolds.Add(manifold);

                    // 唤醒
                    if (bodyA.IsSleeping || bodyB.IsSleeping)
                        _sleepManager.WakeOnCollision(bodyA, bodyB);
                }
            }

            // ===== 4. 构建碰撞岛 =====
            List<List<int>> islands = BuildIslands();

            // ===== 5. 求解约束（每个岛独立求解） =====
            foreach (var island in islands)
            {
                // 初始化约束
                var islandBodies = new List<RigidBody>();
                var islandManifolds = new List<ContactManifold>();
                var islandConstraints = new List<Constraint>();

                foreach (int bodyIdx in island)
                    islandBodies.Add(_bodies[bodyIdx]);

                foreach (var manifold in _contactManifolds)
                {
                    bool inIsland = false;
                    foreach (int bodyIdx in island)
                    {
                        if (manifold.BodyIndexA == bodyIdx || manifold.BodyIndexB == bodyIdx)
                        { inIsland = true; break; }
                    }
                    if (inIsland) islandManifolds.Add(manifold);
                }

                foreach (var constraint in _constraints)
                {
                    if (constraint.IsBroken) continue;
                    bool inIsland = false;
                    foreach (int bodyIdx in island)
                    {
                        if (constraint.BodyIndexA == bodyIdx || constraint.BodyIndexB == bodyIdx)
                        { inIsland = true; break; }
                    }
                    if (inIsland) islandConstraints.Add(constraint);
                }

                // 速度约束初始化
                var proxyArray = new IRigidBodyProxy[islandBodies.Count];
                for (int i = 0; i < islandBodies.Count; i++)
                    proxyArray[i] = islandBodies[i];

                foreach (var c in islandConstraints)
                    c.InitVelocitySolve(proxyArray, dt);

                // 速度约束求解
                for (int iter = 0; iter < _solverSettings.VelocityIterations; iter++)
                {
                    foreach (var c in islandConstraints)
                        c.SolveVelocity(proxyArray, dt);
                }

                _velocitySolver.SolveVelocityConstraints(islandManifolds, proxyArray, dt);

                // 阻尼
                foreach (var body in islandBodies)
                    _velocitySolver.SolveDamping(body, dt);

                // ===== 6. 积分 =====
                foreach (var body in islandBodies)
                {
                    if (!body.IsStatic && !body.IsKinematic && !body.IsSleeping)
                    {
                        _integrator.Integrate(body, dt);
                        body.ClearForces();
                    }
                    else if (body.IsKinematic)
                    {
                        _integrator.Integrate(body, dt);
                    }
                }

                // 位置约束初始化与求解
                foreach (var c in islandConstraints)
                    c.InitPositionSolve(proxyArray, dt);

                for (int iter = 0; iter < _solverSettings.PositionIterations; iter++)
                {
                    foreach (var c in islandConstraints)
                        c.SolvePosition(proxyArray, dt);
                }

                _positionSolver.SolvePositionConstraints(islandManifolds, proxyArray, dt);

                // 断裂检测
                foreach (var c in islandConstraints)
                    c.CheckBreak();
            }

            // ===== 7. 更新宽相位AABB =====
            for (int i = 0; i < _bodies.Count; i++)
            {
                if (_bodies[i].IsStatic || _bodies[i].IsKinematic) continue;
                if (_bodies[i].CollisionShape == null) continue;
                AABBShape aabb = _bodies[i].ComputeWorldAABB();
                _broadphase.Update(i, aabb);
            }

            // ===== 8. 休眠检测 =====
            _sleepManager.UpdateSleep(_bodies, dt);

            // ===== 9. 事件生成 =====
            GenerateEvents();
        }

        /// <summary>广相位碰撞检测 → 候选对。</summary>
        private List<(int a, int b)> BroadphaseDetection(float dt)
        {
            var pairs = new List<(int a, int b)>();
            _broadphase.OverlapPairs((a, b) =>
            {
                if (!_proxyToBody.TryGetValue(a, out int bodyA)) return;
                if (!_proxyToBody.TryGetValue(b, out int bodyB)) return;
                if (bodyA != bodyB && bodyA < _bodies.Count && bodyB < _bodies.Count)
                    pairs.Add((Math.Min(bodyA, bodyB), Math.Max(bodyA, bodyB)));
            });
            return pairs;
        }

        /// <summary>构建碰撞岛（连通图 DFS）。</summary>
        private List<List<int>> BuildIslands()
        {
            var visited = new HashSet<int>();
            var islands = new List<List<int>>();

            for (int i = 0; i < _bodies.Count; i++)
            {
                if (visited.Contains(i)) continue;
                if (_bodies[i].IsStatic && _bodies[i].IsSleeping) continue;

                var island = new List<int>();
                var stack = new Stack<int>();
                stack.Push(i);

                while (stack.Count > 0)
                {
                    int node = stack.Pop();
                    if (visited.Contains(node)) continue;
                    if (_bodies[node].IsSleeping) continue;
                    visited.Add(node);
                    island.Add(node);
                    _bodies[node].IslandIndex = islands.Count;

                    // 查找邻居（通过接触流形和约束）
                    foreach (var manifold in _contactManifolds)
                    {
                        if (manifold.BodyIndexA == node && !visited.Contains(manifold.BodyIndexB))
                            stack.Push(manifold.BodyIndexB);
                        if (manifold.BodyIndexB == node && !visited.Contains(manifold.BodyIndexA))
                            stack.Push(manifold.BodyIndexA);
                    }

                    foreach (var constraint in _constraints)
                    {
                        if (constraint.IsBroken) continue;
                        if (constraint.BodyIndexA == node && constraint.BodyIndexB >= 0
                            && !visited.Contains(constraint.BodyIndexB))
                            stack.Push(constraint.BodyIndexB);
                        if (constraint.BodyIndexB == node && constraint.BodyIndexA >= 0
                            && !visited.Contains(constraint.BodyIndexA))
                            stack.Push(constraint.BodyIndexA);
                    }
                }

                if (island.Count > 0)
                    islands.Add(island);
            }

            return islands;
        }

        /// <summary>查找旧流形。</summary>
        private ContactManifold FindOldManifold(int bodyA, int bodyB)
        {
            foreach (var m in _prevManifolds)
            {
                if ((m.BodyIndexA == bodyA && m.BodyIndexB == bodyB) ||
                    (m.BodyIndexA == bodyB && m.BodyIndexB == bodyA))
                    return m;
            }
            return null;
        }

        /// <summary>生成碰撞/触发事件。</summary>
        private void GenerateEvents()
        {
            _collisionEnterEvents.Clear();
            _collisionStayEvents.Clear();
            _collisionExitEvents.Clear();
            _triggerEnterEvents.Clear();
            _triggerExitEvents.Clear();

            var currentPairs = new HashSet<(int a, int b)>();
            foreach (var m in _contactManifolds)
            {
                var pair = (Math.Min(m.BodyIndexA, m.BodyIndexB), Math.Max(m.BodyIndexA, m.BodyIndexB));
                currentPairs.Add(pair);

                if (!_activeCollisionPairs.Contains(pair))
                {
                    // Enter 事件
                    foreach (var cp in m.Points)
                    {
                        _collisionEnterEvents.Add(new CollisionEvent
                        {
                            BodyIndexA = m.BodyIndexA,
                            BodyIndexB = m.BodyIndexB,
                            ContactPoint = cp.Position,
                            ContactNormal = cp.Normal,
                            PenetrationDepth = cp.PenetrationDepth,
                        });
                        break;
                    }
                }
                else
                {
                    // Stay 事件
                    foreach (var cp in m.Points)
                    {
                        _collisionStayEvents.Add(new CollisionEvent
                        {
                            BodyIndexA = m.BodyIndexA,
                            BodyIndexB = m.BodyIndexB,
                            ContactPoint = cp.Position,
                            ContactNormal = cp.Normal,
                            PenetrationDepth = cp.PenetrationDepth,
                        });
                        break;
                    }
                }
            }

            // Exit 事件
            foreach (var pair in _activeCollisionPairs)
            {
                if (!currentPairs.Contains(pair))
                {
                    _collisionExitEvents.Add(new CollisionEvent
                    {
                        BodyIndexA = pair.a,
                        BodyIndexB = pair.b,
                    });
                }
            }

            _activeCollisionPairs = currentPairs;
        }

        #endregion

        #region 查询

        /// <summary>射线检测。</summary>
        public bool Raycast(PhysicsVector3 origin, PhysicsVector3 direction, float maxDistance,
            out RaycastHitData hit)
        {
            bool found = false;
            float closestT = maxDistance;
            RaycastHitData closestHit = default;

            _broadphase.Raycast(origin, direction, maxDistance, (proxyId, userData, t) =>
            {
                if (t < closestT)
                {
                    if (!_proxyToBody.TryGetValue(proxyId, out int bodyIdx)) return true;
                    if (bodyIdx >= _bodies.Count) return true;

                    var body = _bodies[bodyIdx];
                    if (body.CollisionShape == null) return true;

                    var transform = body.GetTransform();
                    PhysicsVector3 closestHitPoint = origin + direction * t;

                    closestHit = new RaycastHitData
                    {
                        Point = closestHitPoint,
                        Normal = PhysicsVector3.UnitY,
                        Distance = t,
                        T = t,
                        HitObject = body.UserData,
                    };
                    closestT = t;
                    found = true;
                }
                return true;
            });

            hit = closestHit;
            return found;
        }

        /// <summary>扫掠测试（移动形状检测）。</summary>
        public bool SweepTest(Shape shape, PhysicsVector3 origin, PhysicsVector3 direction,
            float maxDistance, out RaycastHitData hit)
        {
            AABBShape startAABB = shape.ComputeAABB(new PhysicsTransform(origin, PhysicsQuaternion.Identity));
            AABBShape endAABB = shape.ComputeAABB(
                new PhysicsTransform(origin + direction * maxDistance, PhysicsQuaternion.Identity));
            AABBShape sweepAABB = AABBShape.Union(startAABB, endAABB);

            bool found = false;
            float closestT = maxDistance;
            RaycastHitData closestHit = default;

            _broadphase.Query(sweepAABB, (proxyId, userData) =>
            {
                if (!_proxyToBody.TryGetValue(proxyId, out int bodyIdx)) return;
                if (bodyIdx >= _bodies.Count) return;

                var body = _bodies[bodyIdx];
                if (body.CollisionShape == null) return;

                var tBody = body.GetTransform();
                if (_collisionDispatcher.TestCollision(
                    shape, new PhysicsTransform(origin, PhysicsQuaternion.Identity),
                    body.CollisionShape, tBody,
                    out PhysicsVector3 normal, out float penetration))
                {
                    if (penetration < closestT)
                    {
                        closestHit = new RaycastHitData
                        {
                            Point = origin + direction * penetration,
                            Normal = normal,
                            Distance = penetration,
                            T = penetration,
                            HitObject = body.UserData,
                        };
                        closestT = penetration;
                        found = true;
                    }
                }
            });

            hit = closestHit;
            return found;
        }

        /// <summary>重叠测试。</summary>
        public List<int> OverlapTest(AABBShape aabb)
        {
            var results = new List<int>();
            _broadphase.Query(aabb, (proxyId, userData) =>
            {
                if (_proxyToBody.TryGetValue(proxyId, out int bodyIdx))
                {
                    if (bodyIdx < _bodies.Count)
                        results.Add(bodyIdx);
                }
            });
            return results;
        }

        #endregion

        #region 事件回调

        /// <summary>碰撞进入事件。</summary>
        public event Action<CollisionEvent> OnCollisionEnter;
        /// <summary>碰撞持续事件。</summary>
        public event Action<CollisionEvent> OnCollisionStay;
        /// <summary>碰撞退出事件。</summary>
        public event Action<CollisionEvent> OnCollisionExit;
        /// <summary>触发器进入事件。</summary>
        public event Action<TriggerEvent> OnTriggerEnter;
        /// <summary>触发器退出事件。</summary>
        public event Action<TriggerEvent> OnTriggerExit;
        /// <summary>约束断裂事件。</summary>
        public event Action<Constraint> OnConstraintBreak;

        /// <summary>触发所有待处理事件。</summary>
        public void FireEvents()
        {
            foreach (var e in _collisionEnterEvents)
                OnCollisionEnter?.Invoke(e);
            foreach (var e in _collisionStayEvents)
                OnCollisionStay?.Invoke(e);
            foreach (var e in _collisionExitEvents)
                OnCollisionExit?.Invoke(e);
            foreach (var e in _triggerEnterEvents)
                OnTriggerEnter?.Invoke(e);
            foreach (var e in _triggerExitEvents)
                OnTriggerExit?.Invoke(e);
            foreach (var c in _constraints)
            {
                if (c.IsBroken)
                    OnConstraintBreak?.Invoke(c);
            }
        }

        #endregion

        #region 时间累积

        /// <summary>使用时间累积器步进物理（适配可变帧率）。</summary>
        public void StepWithAccumulator(float frameDt)
        {
            _timeAccumulator += frameDt;
            _timeAccumulator = Math.Min(_timeAccumulator, _fixedTimestep * 5);

            while (_timeAccumulator >= _fixedTimestep)
            {
                Step(_fixedTimestep);
                _timeAccumulator -= _fixedTimestep;
            }
        }

        #endregion

        #region 工具

        #region IDisposable

        private bool _physicsDisposed;

        /// <summary>释放物理资源。</summary>
        public void Dispose()
        {
            if (_physicsDisposed) return;
            _physicsDisposed = true;
            Reset();
            _sleepManager = null!;
            _broadphase = null!;
            _collisionDispatcher = null!;
            _velocitySolver = null!;
            _positionSolver = null!;
            _integrator = null!;
        }

        #endregion

        /// <summary>唤醒所有刚体。</summary>
        public void WakeUpAll() => _sleepManager.WakeUpAll(_bodies);

        /// <summary>重置物理世界。</summary>
        public void Reset()
        {
            _bodies.Clear();
            _bodyToIndex.Clear();
            _constraints.Clear();
            _contactManifolds.Clear();
            _prevManifolds.Clear();
            _proxyToBody.Clear();
            _activeCollisionPairs.Clear();
            _collisionEnterEvents.Clear();
            _collisionStayEvents.Clear();
            _collisionExitEvents.Clear();
            _timeAccumulator = 0;
            _currentStepTime = 0;
            _sleepManager.Reset();
            _broadphase = new DynamicAABBTree();
        }

        #endregion
    }
}
