using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;

namespace YamaEngine.Physics
{
    // ============================================================================================
    //  PhysicsWorldV2.cs
    //
    //  V2版本的物理世界。V1那套是单线程的，刚体一多就卡得要死，V2主要是把多线程并行模拟、
    //  岛屿分解(Island Decomposition)、休眠机制(Sleeping)这三块给补上了。整体参考了Box2D v2
    //  和Bullet的架构，但完全用C#重写，没用任何native库。
    //
    //  主要流程：
    //    1. 施加外力、积分速度
    //    2. 宽相位找出可能碰撞的AABB对
    //    3. 窄相位精确检测，生成接触点
    //    4. 岛屿分解：把相互连接的刚体分组，不连通的岛屿可以并行求解
    //    5. 约束/接触求解（每个岛屿独立并行）
    //    6. 积分位置
    //    7. 休眠检测：低速岛屿整体进入睡眠，跳过后续步骤
    //
    //  注意：质量不能为0，会用一个小阈值兜底，否则逆质量NaN直接炸
    //  TODO: 大量刚体时性能不行，岛屿分解的负载均衡还得再调
    // ============================================================================================

    /// <summary>
    /// V2物理世界的配置参数。调参全在这里。
    /// </summary>
    public sealed class PhysicsWorldV2Settings
    {
        /// <summary>固定时间步长。默认1/60。改这个会影响整体确定性。</summary>
        public float FixedTimeStep = 1f / 60f;

        /// <summary>最大子步数，防止帧率掉得太狠出现"死亡螺旋"。</summary>
        public int MaxSubSteps = 8;

        /// <summary>重力。</summary>
        public Vector3 Gravity = new(0f, -9.81f, 0f);

        /// <summary>速度求解迭代次数。越多越稳但越慢。8次一般够用。</summary>
        public int VelocityIterations = 8;

        /// <summary>位置求解迭代次数（位置修正）。</summary>
        public int PositionIterations = 3;

        /// <summary>是否启用多线程并行模拟。</summary>
        public bool EnableMultithreading = true;

        /// <summary>是否启用岛屿分解。关了就退化成全局串行。</summary>
        public bool EnableIslandDecomposition = true;

        /// <summary>是否启用休眠机制。</summary>
        public bool EnableSleeping = true;

        /// <summary>休眠线速度阈值(m/s)。低于这个且持续一段时间就睡。</summary>
        public float SleepLinearVelocityThreshold = 0.05f;

        /// <summary>休眠角速度阈值(rad/s)。</summary>
        public float SleepAngularVelocityThreshold = 0.05f;

        /// <summary>进入睡眠前需要保持低速的秒数。</summary>
        public float SleepTimeThreshold = 1.0f;

        /// <summary>Baumgarte位置修正系数，0~1。太大会抖，太小穿透回不来。</summary>
        public float Baumgarte = 0.2f;

        /// <summary>穿透容忍度。小于这个值的穿透不修正，避免抖动。</summary>
        public float PenetrationSlop = 0.005f;

        /// <summary>最大并行岛屿数。</summary>
        public int MaxParallelIslands = 16;

        /// <summary>是否开启CCD(连续碰撞检测)。</summary>
        public bool EnableCCD = false;

        /// <summary>是否开启确定性模式（排序+定点累加，慢一点点）。</summary>
        public bool Deterministic = true;

        public static PhysicsWorldV2Settings Default => new();
    }

    /// <summary>
    /// 物理体句柄。外部代码拿这个引用刚体，不直接碰内部数据。
    /// </summary>
    public readonly struct BodyHandle : IEquatable<BodyHandle>
    {
        public readonly int Index;
        public readonly int Generation;
        public static readonly BodyHandle Null = new(-1, 0);

        public BodyHandle(int index, int generation)
        {
            Index = index;
            Generation = generation;
        }

        public bool IsValid => Index >= 0;
        public bool Equals(BodyHandle other) => Index == other.Index && Generation == other.Generation;
        public override bool Equals(object? obj) => obj is BodyHandle h && Equals(h);
        public override int GetHashCode() => HashCode.Combine(Index, Generation);
        public static bool operator ==(BodyHandle a, BodyHandle b) => a.Equals(b);
        public static bool operator !=(BodyHandle a, BodyHandle b) => !a.Equals(b);
    }

    /// <summary>
    /// 刚体运动类型。静态/运动学/动态。
    /// </summary>
    public enum BodyMotionType : byte
    {
        Static = 0,
        Kinematic = 1,
        Dynamic = 2
    }

    /// <summary>
    /// V2物理世界里单个刚体的运行时数据。
    /// 用class的话GC压力太大，这里拆成SOA(Structure of Arrays)存。
    /// BodyData是其中一个槽位的所有字段。
    /// </summary>
    public struct BodySlot
    {
        public bool Active;              // 是否参与模拟
        public BodyMotionType MotionType;
        public int Generation;           // 用于句柄校验，删了又加会换代
        public Vector3 Position;
        public Quaternion Orientation;
        public Vector3 LinearVelocity;
        public Vector3 AngularVelocity;
        public Vector3 ForceAccumulator;
        public Vector3 TorqueAccumulator;
        public float Mass;
        public float InvMass;
        public Matrix4x4 InvInertiaLocal;   // 局部系下的逆惯性张量
        public Matrix4x4 InvInertiaWorld;   // 世界系下的逆惯性张量（每帧更新）
        public float LinearDamping;
        public float AngularDamping;
        public float SleepTimer;         // 持续低速的时间
        public bool IsSleeping;
        public int IslandId;             // 所属岛屿编号，-1表示未分配
        public int ShapeId;              // 形状ID，指向ShapeDatabase
        public int CollisionLayer;       // 碰撞层
        public int CollisionMask;        // 碰撞掩码
        public float Restitution;        // 弹性系数
        public float Friction;           // 摩擦系数
        public bool IsTrigger;           // 触发器不产生碰撞响应
        public bool AutoSleep;           // 是否允许自动休眠
        public Vector3 AABBMin;          // 世界AABB（宽相位用）
        public Vector3 AABBMax;
        public bool AABBDirty;
        public float GravityScale;       // 重力缩放，1为正常
        public object? UserData;         // 用户数据，引擎层挂EntityId之类
    }

    /// <summary>
    /// 岛屿。一组通过约束/接触连通的刚体。岛屿之间没有依赖，可并行求解。
    /// </summary>
    public struct ConstraintData
    {
        public int Id;
        public readonly List<int> BodyIndices = new(16);
        public readonly List<int> ContactIndices = new(16);
        public readonly List<int> ConstraintIndices = new(16);
        public bool AllSleepingCandidate = true;  // 全员低速则整岛睡眠

        // ---------- 约束参数（求解器消费） ----------
        public int BodyAIndex;
        public int BodyBIndex;
        public ConstraintType Type;
        public bool Enabled = true;
        public int IslandId = -1;
        public Vector3 AnchorA;      // A局部系锚点
        public Vector3 AnchorB;
        public Vector3 ExtraAxis;    // 各种约束的额外轴参数
        public float ExtraMin;
        public float ExtraMax;
        public float Softness;
        public float Bias;

        public ConstraintData(int capacity)
        {
            Id = 0;
            BodyIndices = new List<int>(capacity);
            ContactIndices = new List<int>(capacity);
            ConstraintIndices = new List<int>(capacity);
            AllSleepingCandidate = true;
        }

        public void Clear()
        {
            BodyIndices.Clear();
            ContactIndices.Clear();
            ConstraintIndices.Clear();
            AllSleepingCandidate = true;
        }
    }

    /// <summary>
    /// 接触点（运行时，由窄相位生成，求解器消费）。
    /// </summary>

    /// <summary>
    /// 约束（运行时）。约束求解器消费。
    /// </summary>
    public class PhysicsIsland
    {
        public int BodyAIndex;
        public int BodyBIndex;
        public ConstraintType Type;
        public Vector3 AnchorA;          // A局部系锚点
        public Vector3 AnchorB;
        public float Bias;
        public float Softness;
        public bool Enabled;
        public int IslandId;
        // 各种约束的额外参数塞在这，偷懒了，正经实现应该分类型存
        public Vector3 ExtraAxis;
        public float ExtraMin;
        public float ExtraMax;

        // ---------- 岛屿内容（连通集合） ----------
        /// <summary>岛屿唯一ID。</summary>
        public int Id;
        /// <summary>本岛屿包含的刚体索引集合。</summary>
        public readonly List<int> BodyIndices = new(16);
        /// <summary>本岛屿包含的接触索引集合。</summary>
        public readonly List<int> ContactIndices = new(16);
        /// <summary>本岛屿包含的约束索引集合。</summary>
        public readonly List<int> ConstraintIndices = new(16);

        /// <summary>全员低速则整岛睡眠。</summary>
        public bool AllSleepingCandidate = true;

        /// <summary>清空岛屿内容（保留ID）。</summary>
        public void Clear()
        {
            BodyIndices.Clear();
            ContactIndices.Clear();
            ConstraintIndices.Clear();
            AllSleepingCandidate = true;
        }
    }

    /// <summary>
    /// 约束类型枚举。
    /// </summary>
    public enum ConstraintType : byte
    {
        Point = 0,        // 点对点(球铰)
        Hinge = 1,        // 铰链
        Slider = 2,       // 滑动
        ConeTwist = 3,    // 圆锥扭转
        Generic6Dof = 4,  // 通用6自由度
        Distance = 5,     // 距离
        Fixed = 6,        // 固定(焊接)
        Motor = 7         // 马达
    }

    /// <summary>
    /// 物理世界V2主类。整个物理系统的入口。
    /// 注意：此实现已标记为遗留代码。新代码应使用 PhysicsWorldReal
    /// （YamaEngine.Physics.PhysicsWorldReal），
    /// 它提供了更完善的纯 C# 物理实现。
    /// 如需保留 V2 的功能，可考虑通过 IPhysicsWorld 适配器桥接。
    /// </summary>
    [Obsolete("PhysicsWorldV2 已弃用，请改用 PhysicsWorldReal（更完善的纯 C# 物理实现）")]
    public sealed class PhysicsWorldV2 : IDisposable
    {
        // ---------- 刚体SOA存储 ----------
        // 用List<BodySlot>而不是Body[]是因为增删方便，访问还是O(1)
        private readonly List<BodySlot> _bodies = new(256);
        private readonly Queue<int> _freeSlots = new();   // 删除后空出来的槽位

        // ---------- 接触和约束 ----------
        private readonly List<ContactPoint> _contacts = new(1024);
        private readonly List<ConstraintData> _constraints = new(64);

        // ---------- 岛屿 ----------
        private readonly List<PhysicsIsland> _islands = new(16);
        // 并查集(Union-Find)用于岛屿分解
        private int[] _islandParent = Array.Empty<int>();
        private int[] _islandRank = Array.Empty<int>();

        // ---------- 配置 ----------
        private readonly PhysicsWorldV2Settings _settings;
        private float _accumulator;

        // ---------- 多线程 ----------
        private readonly PhysicsJobSystem _jobSystem;
        private readonly object _bodyLock = new();
        private readonly object _contactLock = new();

        // ---------- 子系统引用 ----------
        // V2自己跑宽相位窄相位，但具体算法委托给独立模块
        internal BroadphaseSystem? Broadphase;
        internal NarrowphaseSystem? Narrowphase;
        internal ContactSolver? ContactSolver;
        internal ConstraintSolver? ConstraintSolver;
        internal ShapeDatabase? Shapes;

        // ---------- 统计 ----------
        public PhysicsWorldStats Stats = new();

        /// <summary>统计信息，给Profiler用。</summary>
        public sealed class PhysicsWorldStats
        {
            public int BodyCount;
            public int DynamicBodyCount;
            public int SleepingBodyCount;
            public int ContactCount;
            public int ConstraintCount;
            public int IslandCount;
            public int ActiveIslandCount;
            public long LastStepMicroseconds;
            public int SubStepsLastFrame;
        }

        public PhysicsWorldV2() : this(PhysicsWorldV2Settings.Default) { }

        public PhysicsWorldV2(PhysicsWorldV2Settings settings)
        {
            _settings = settings;
            _jobSystem = new PhysicsJobSystem(settings.EnableMultithreading ? Environment.ProcessorCount : 1);

            // 初始化默认的几个子系统。具体实现在各自的文件里
            Shapes = new ShapeDatabase();
            Broadphase = new BroadphaseSystem();
            Narrowphase = new NarrowphaseSystem();
            ContactSolver = new ContactSolver(settings.VelocityIterations, settings.PositionIterations);
            ConstraintSolver = new ConstraintSolver();

            // 预创建几个岛屿池
            for (int i = 0; i < 8; i++)
                _islands.Add(new PhysicsIsland { Id = i });
        }

        // ===========================================================================
        //  刚体增删改
        // ===========================================================================

        /// <summary>创建一个刚体并加入世界。</summary>
        public BodyHandle CreateBody(BodyDesc desc)
        {
            if (desc.Mass <= 0f)
                throw new ArgumentException("质量不能为0或负数，传进来的是" + desc.Mass, nameof(desc));

            int index;
            lock (_bodyLock)
            {
                if (_freeSlots.Count > 0)
                {
                    index = _freeSlots.Dequeue();
                    var slot = _bodies[index];
                    slot.Generation++;
                    _bodies[index] = InitializeSlot(slot, desc);
                }
                else
                {
                    index = _bodies.Count;
                    var slot = new BodySlot();
                    slot.Generation = 1;
                    _bodies.Add(InitializeSlot(slot, desc));
                }
                Stats.BodyCount = _bodies.Count;
            }

            var gen = _bodies[index].Generation;
            return new BodyHandle(index, gen);
        }

        private static BodySlot InitializeSlot(BodySlot slot, BodyDesc desc)
        {
            slot.Active = true;
            slot.MotionType = desc.MotionType;
            slot.Position = desc.Position;
            slot.Orientation = desc.Orientation;
            slot.LinearVelocity = desc.LinearVelocity;
            slot.AngularVelocity = desc.AngularVelocity;
            slot.Mass = MathF.Max(desc.Mass, 0.001f); // 兜底，0会除零
            slot.InvMass = desc.MotionType == BodyMotionType.Static ? 0f : 1f / slot.Mass;
            slot.InvInertiaLocal = desc.InvInertiaTensor;
            slot.LinearDamping = desc.LinearDamping;
            slot.AngularDamping = desc.AngularDamping;
            slot.Restitution = desc.Restitution;
            slot.Friction = desc.Friction;
            slot.IsTrigger = desc.IsTrigger;
            slot.AutoSleep = desc.AutoSleep;
            slot.CollisionLayer = desc.CollisionLayer;
            slot.CollisionMask = desc.CollisionMask;
            slot.GravityScale = desc.GravityScale;
            slot.SleepTimer = 0f;
            slot.IsSleeping = false;
            slot.IslandId = -1;
            slot.AABBDirty = true;
            slot.UserData = desc.UserData;
            return slot;
        }

        /// <summary>删除刚体。槽位不真正删除，标记换代复用。</summary>
        public bool DestroyBody(BodyHandle handle)
        {
            if (!ValidateHandle(handle)) return false;
            lock (_bodyLock)
            {
                ref var slot = ref _bodies.AsSpan()[handle.Index];
                slot.Active = false;
                slot.Generation++;   // 让旧句柄失效
                _freeSlots.Enqueue(handle.Index);
                Stats.BodyCount = _bodies.Count - _freeSlots.Count;
            }
            return true;
        }

        private bool ValidateHandle(BodyHandle h)
        {
            return h.Index >= 0 && h.Index < _bodies.Count &&
                   _bodies[h.Index].Generation == h.Generation &&
                   _bodies[h.Index].Active;
        }

        public ref readonly BodySlot GetBodyRef(BodyHandle h)
        {
            if (!ValidateHandle(h))
                throw new InvalidOperationException("无效的BodyHandle");
            return ref _bodies.AsSpan()[h.Index];
        }

        // ===========================================================================
        //  主循环
        // ===========================================================================

        /// <summary>
        /// 推进物理模拟。外部按帧调用，内部用累加器跑固定步长。
        /// </summary>
        public void Step(float deltaTime)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            _accumulator += deltaTime;
            int subSteps = 0;

            float dt = _settings.FixedTimeStep;
            while (_accumulator >= dt && subSteps < _settings.MaxSubSteps)
            {
                StepFixed(dt);
                _accumulator -= dt;
                subSteps++;
            }

            // 帧率太低时清掉累积，避免死亡螺旋
            if (_accumulator > dt * _settings.MaxSubSteps)
                _accumulator = 0f;

            sw.Stop();
            Stats.LastStepMicroseconds = sw.ElapsedTicks * 1_000_000 / System.Diagnostics.Stopwatch.Frequency;
            Stats.SubStepsLastFrame = subSteps;
        }

        /// <summary>固定步长的单步模拟。所有逻辑都在这。</summary>
        private void StepFixed(float dt)
        {
            // 1) 清掉上一帧的接触和岛屿
            _contacts.Clear();
            foreach (var island in _islands)
                island.Clear();

            // 2) 施加外力，更新世界系逆惯性张量
            ApplyForcesAndPrepare(dt);

            // 3) 宽相位：找出AABB重叠对
            var candidatePairs = Broadphase!.FindPairs(_bodies);

            // 4) 窄相位：精确检测生成接触点
            Narrowphase!.GenerateContacts(_bodies, candidatePairs, _contacts, Shapes!);

            Stats.ContactCount = _contacts.Count;

            // 5) 岛屿分解
            if (_settings.EnableIslandDecomposition)
                DecomposeIslands();
            else
                BuildSingleIsland();

            Stats.IslandCount = _islands.Count(isl => isl.BodyIndices.Count > 0);
            Stats.ActiveIslandCount = Stats.IslandCount;

            // 6) 求解（多线程并行跑岛屿）
            SolveIslands(dt);

            // 7) 积分位置
            IntegratePositions(dt);

            // 8) 休眠检测
            if (_settings.EnableSleeping)
                UpdateSleeping(dt);

            // 9) 更新AABB
            UpdateAABBs();
        }

        // ===========================================================================
        //  施加外力 & 准备数据
        // ===========================================================================

        private void ApplyForcesAndPrepare(float dt)
        {
            var bodies = _bodies.AsSpan();
            var gravity = _settings.Gravity;

            // 多线程施加外力。每个body独立，无依赖
            if (_settings.EnableMultithreading && bodies.Length > 64)
            {
                _jobSystem.ParallelFor("ApplyForces", bodies.Length, (i) =>
                {
                    ref var b = ref _bodies.AsSpan()[i];
                    if (!b.Active || b.IsSleeping) return;
                    PrepareBody(ref b, gravity, dt);
                });
            }
            else
            {
                for (int i = 0; i < bodies.Length; i++)
                {
                    ref var b = ref bodies[i];
                    if (!b.Active || b.IsSleeping) continue;
                    PrepareBody(ref b, gravity, dt);
                }
            }
        }

        private static void PrepareBody(ref BodySlot b, Vector3 gravity, float dt)
        {
            // 静态体不参与
            if (b.MotionType == BodyMotionType.Static) return;

            // 重力
            if (b.MotionType == BodyMotionType.Dynamic)
            {
                b.ForceAccumulator += gravity * b.Mass * b.GravityScale;
            }

            // 更新世界系逆惯性张量: Iw = R * Il * R^T
            var R = Matrix4x4.CreateFromQuaternion(b.Orientation);
            var Rt = Matrix4x4.Transpose(R);
            b.InvInertiaWorld = Matrix4x4.Multiply(Matrix4x4.Multiply(R, b.InvInertiaLocal), Rt);

            // 速度积分(半隐式欧拉)：v += (F/m) * dt
            b.LinearVelocity += b.ForceAccumulator * b.InvMass * dt;
            b.AngularVelocity += TransformByInertia(b.TorqueAccumulator, b.InvInertiaWorld) * dt;

            // 阻尼
            float linearDamp = MathF.Pow(1f - b.LinearDamping, dt);
            float angularDamp = MathF.Pow(1f - b.AngularDamping, dt);
            b.LinearVelocity *= linearDamp;
            b.AngularVelocity *= angularDamp;

            // 清力
            b.ForceAccumulator = Vector3.Zero;
            b.TorqueAccumulator = Vector3.Zero;
        }

        private static Vector3 TransformByInertia(Vector3 v, Matrix4x4 m)
        {
            return new Vector3(
                m.M11 * v.X + m.M12 * v.Y + m.M13 * v.Z,
                m.M21 * v.X + m.M22 * v.Y + m.M23 * v.Z,
                m.M31 * v.X + m.M32 * v.Y + m.M33 * v.Z);
        }

        // ===========================================================================
        //  岛屿分解 (Island Decomposition)
        //  用Union-Find把连通的body合并成岛屿
        // ===========================================================================

        private void DecomposeIslands()
        {
            int n = _bodies.Count;
            if (n == 0) return;

            // 扩容并查集
            if (_islandParent.Length < n)
            {
                _islandParent = new int[n];
                _islandRank = new int[n];
            }
            for (int i = 0; i < n; i++)
            {
                _islandParent[i] = i;
                _islandRank[i] = 0;
            }

            // 接触连边
            var contacts = _contacts.AsSpan();
            for (int i = 0; i < contacts.Length; i++)
            {
                ref var c = ref contacts[i];
                if (!c.Valid) continue;
                Union(c.BodyAIndex, c.BodyBIndex);
            }

            // 约束连边
            foreach (var con in _constraints)
            {
                if (!con.Enabled) continue;
                Union(con.BodyAIndex, con.BodyBIndex);
            }

            // 收集岛屿成员
            // 用字典把root映射到island id
            var rootToIsland = new Dictionary<int, int>(n);
            int nextIslandId = 0;
            var bodies = _bodies.AsSpan();
            for (int i = 0; i < n; i++)
            {
                if (!bodies[i].Active || bodies[i].MotionType == BodyMotionType.Static) continue;
                int root = Find(i);
                if (!rootToIsland.TryGetValue(root, out int islandId))
                {
                    islandId = nextIslandId++;
                    rootToIsland[root] = islandId;
                }
                bodies[i].IslandId = islandId;
            }

            // 确保岛屿池够大
            while (_islands.Count < nextIslandId)
                _islands.Add(new PhysicsIsland { Id = _islands.Count });

            // 分配body到岛屿
            for (int i = 0; i < n; i++)
            {
                ref var b = ref bodies[i];
                if (!b.Active) continue;
                if (b.MotionType == BodyMotionType.Static) continue;
                if (b.IslandId < 0) continue;
                _islands[b.IslandId].BodyIndices.Add(i);
            }

            // 分配接触到岛屿（用A的岛屿）
            for (int i = 0; i < contacts.Length; i++)
            {
                ref var c = ref contacts[i];
                if (!c.Valid) continue;
                int islandId = bodies[c.BodyAIndex].IslandId;
                if (islandId >= 0)
                {
                    c.IslandId = islandId;
                    _islands[islandId].ContactIndices.Add(i);
                }
            }

            // 分配约束到岛屿
            for (int i = 0; i < _constraints.Count; i++)
            {
                if (!_constraints[i].Enabled) continue;
                int islandId = bodies[_constraints[i].BodyAIndex].IslandId;
                if (islandId >= 0)
                {
                    var cd = _constraints[i];
                    cd.IslandId = islandId;
                    _constraints[i] = cd;
                    _islands[islandId].ConstraintIndices.Add(i);
                }
            }
        }

        private void BuildSingleIsland()
        {
            // 关闭岛屿分解时，所有body塞一个岛
            var island = _islands[0];
            island.Clear();
            var bodies = _bodies.AsSpan();
            for (int i = 0; i < bodies.Length; i++)
            {
                if (!bodies[i].Active) continue;
                if (bodies[i].MotionType == BodyMotionType.Static) continue;
                bodies[i].IslandId = 0;
                island.BodyIndices.Add(i);
            }
            var contacts = _contacts.AsSpan();
            for (int i = 0; i < contacts.Length; i++)
            {
                if (!contacts[i].Valid) continue;
                contacts[i].IslandId = 0;
                island.ContactIndices.Add(i);
            }
            for (int i = 0; i < _constraints.Count; i++)
            {
                if (!_constraints[i].Enabled) continue;
                var cd = _constraints[i];
                cd.IslandId = 0;
                _constraints[i] = cd;
                island.ConstraintIndices.Add(i);
            }
        }

        private int Find(int x)
        {
            // 路径压缩
            while (_islandParent[x] != x)
            {
                _islandParent[x] = _islandParent[_islandParent[x]];
                x = _islandParent[x];
            }
            return x;
        }

        private void Union(int a, int b)
        {
            int ra = Find(a), rb = Find(b);
            if (ra == rb) return;
            if (_islandRank[ra] < _islandRank[rb]) (ra, rb) = (rb, ra);
            _islandParent[rb] = ra;
            if (_islandRank[ra] == _islandRank[rb]) _islandRank[ra]++;
        }

        // ===========================================================================
        //  求解
        // ===========================================================================

        private void SolveIslands(float dt)
        {
            // 收集非空岛屿
            var workIslands = new List<PhysicsIsland>(_islands.Count);
            foreach (var isl in _islands)
            {
                if (isl.BodyIndices.Count == 0) continue;

                // 整岛睡眠的话跳过求解
                bool allSleep = true;
                foreach (int bi in isl.BodyIndices)
                {
                    if (!_bodies[bi].IsSleeping) { allSleep = false; break; }
                }
                if (allSleep) continue;

                workIslands.Add(isl);
            }

            if (workIslands.Count == 0) return;

            // 多线程并行跑各岛屿
            if (_settings.EnableMultithreading && workIslands.Count > 1)
            {
                int maxParallel = Math.Min(_settings.MaxParallelIslands, workIslands.Count);
                var batches = PartitionWork(workIslands, maxParallel);

                var tasks = new Task[batches.Count];
                for (int i = 0; i < batches.Count; i++)
                {
                    int batchIdx = i;
                    tasks[i] = Task.Run(() =>
                    {
                        foreach (var isl in batches[batchIdx])
                            SolveIsland(isl, dt);
                    });
                }
                Task.WaitAll(tasks);
            }
            else
            {
                foreach (var isl in workIslands)
                    SolveIsland(isl, dt);
            }
        }

        private static List<List<PhysicsIsland>> PartitionWork(List<PhysicsIsland> islands, int partitions)
        {
            // 贪心负载均衡：按body数量从大到小，轮流分到当前最闲的分区
            // 这玩意调了好久才对，简单round-robin在大岛+一堆小岛时负载很不均
            islands.Sort((a, b) => b.BodyIndices.Count.CompareTo(a.BodyIndices.Count));
            var loads = new int[partitions];
            var result = new List<List<PhysicsIsland>>(partitions);
            for (int i = 0; i < partitions; i++) result.Add(new List<PhysicsIsland>());
            foreach (var isl in islands)
            {
                int minIdx = 0;
                for (int i = 1; i < partitions; i++)
                    if (loads[i] < loads[minIdx]) minIdx = i;
                result[minIdx].Add(isl);
                loads[minIdx] += isl.BodyIndices.Count;
            }
            return result;
        }

        private void SolveIsland(PhysicsIsland island, float dt)
        {
            // 委托给ContactSolver/ConstraintSolver
            // 这里只做岛屿内的调度，具体算法在各自的文件里
            ContactSolver!.SolveIsland(_bodies, _contacts, island, dt, _settings.Baumgarte, _settings.PenetrationSlop);
            ConstraintSolver!.SolveIsland(_bodies, _constraints, island, dt);
        }

        // ===========================================================================
        //  位置积分
        // ===========================================================================

        private void IntegratePositions(float dt)
        {
            var bodies = _bodies.AsSpan();
            if (_settings.EnableMultithreading && bodies.Length > 64)
            {
                _jobSystem.ParallelFor("IntegratePositions", bodies.Length, (i) =>
                    IntegratePosition(ref _bodies.AsSpan()[i], dt));
            }
            else
            {
                for (int i = 0; i < bodies.Length; i++)
                    IntegratePosition(ref bodies[i], dt);
            }
        }

        private static void IntegratePosition(ref BodySlot b, float dt)
        {
            if (!b.Active || b.IsSleeping) return;
            if (b.MotionType == BodyMotionType.Static) return;

            b.Position += b.LinearVelocity * dt;

            // 角速度积分四元数
            // q' = q + 0.5 * (omega_quat) * q * dt，然后归一化
            var omega = b.AngularVelocity;
            if (omega.LengthSquared() > 1e-10f)
            {
                float halfDt = 0.5f * dt;
                var dq = new Quaternion(omega.X * halfDt, omega.Y * halfDt, omega.Z * halfDt, 0f);
                dq = Quaternion.Multiply(dq, b.Orientation);
                b.Orientation = Quaternion.Normalize(b.Orientation + dq);
            }
            b.AABBDirty = true;
        }

        // ===========================================================================
        //  休眠机制
        // ===========================================================================

        private void UpdateSleeping(float dt)
        {
            var bodies = _bodies.AsSpan();
            float linThresholdSq = _settings.SleepLinearVelocityThreshold * _settings.SleepLinearVelocityThreshold;
            float angThresholdSq = _settings.SleepAngularVelocityThreshold * _settings.SleepAngularVelocityThreshold;

            int sleepingCount = 0;
            for (int i = 0; i < bodies.Length; i++)
            {
                ref var b = ref bodies[i];
                if (!b.Active || b.MotionType != BodyMotionType.Dynamic) continue;
                if (!b.AutoSleep) continue;

                if (b.IsSleeping)
                {
                    sleepingCount++;
                    continue;
                }

                float linSq = b.LinearVelocity.LengthSquared();
                float angSq = b.AngularVelocity.LengthSquared();

                if (linSq < linThresholdSq && angSq < angThresholdSq)
                {
                    b.SleepTimer += dt;
                    if (b.SleepTimer >= _settings.SleepTimeThreshold)
                    {
                        b.IsSleeping = true;
                        b.LinearVelocity = Vector3.Zero;
                        b.AngularVelocity = Vector3.Zero;
                        sleepingCount++;
                    }
                }
                else
                {
                    b.SleepTimer = 0f;
                }
            }
            Stats.SleepingBodyCount = sleepingCount;
        }

        /// <summary>唤醒一个刚体。外部施加力或修改位置时调用。</summary>
        public void WakeUp(BodyHandle handle)
        {
            if (!ValidateHandle(handle)) return;
            ref var b = ref _bodies.AsSpan()[handle.Index];
            b.IsSleeping = false;
            b.SleepTimer = 0f;
            // 把约束连着的也唤醒
            WakeConnected(handle.Index);
        }

        private void WakeConnected(int bodyIndex)
        {
            // 简单实现：扫一遍约束，把和这个body连着的也唤醒
            // 复杂图的话可以做BFS，这里偷懒了
            var bodies = _bodies.AsSpan();
            foreach (var con in _constraints)
            {
                if (!con.Enabled) continue;
                int other = -1;
                if (con.BodyAIndex == bodyIndex) other = con.BodyBIndex;
                else if (con.BodyBIndex == bodyIndex) other = con.BodyAIndex;
                if (other >= 0 && bodies[other].IsSleeping)
                {
                    bodies[other].IsSleeping = false;
                    bodies[other].SleepTimer = 0f;
                }
            }
        }

        // ===========================================================================
        //  AABB更新
        // ===========================================================================

        private void UpdateAABBs()
        {
            var bodies = _bodies.AsSpan();
            for (int i = 0; i < bodies.Length; i++)
            {
                ref var b = ref bodies[i];
                if (!b.Active) continue;
                if (!b.AABBDirty) continue;
                UpdateBodyAABB(ref b);
                b.AABBDirty = false;
            }

            // 把AABB更新推给宽相位
            Broadphase!.SyncBounds(_bodies);
        }

        private void UpdateBodyAABB(ref BodySlot b)
        {
            // 简化：用形状的局部AABB旋转+平移得到世界AABB
            // 正经实现应该考虑形状每个顶点，这里用shape database存的local aabb
            var localAABB = Shapes!.GetLocalAABB(b.ShapeId);
            // 旋转AABB（保守放大）
            var R = Matrix4x4.CreateFromQuaternion(b.Orientation);
            // 取8个角点旋转
            Vector3 min = Vector3.Transform(localAABB.Min, R) + b.Position;
            Vector3 max = min;
            Span<Vector3> corners = stackalloc Vector3[8];
            corners[0] = new Vector3(localAABB.Min.X, localAABB.Min.Y, localAABB.Min.Z);
            corners[1] = new Vector3(localAABB.Max.X, localAABB.Min.Y, localAABB.Min.Z);
            corners[2] = new Vector3(localAABB.Min.X, localAABB.Max.Y, localAABB.Min.Z);
            corners[3] = new Vector3(localAABB.Max.X, localAABB.Max.Y, localAABB.Min.Z);
            corners[4] = new Vector3(localAABB.Min.X, localAABB.Min.Y, localAABB.Max.Z);
            corners[5] = new Vector3(localAABB.Max.X, localAABB.Min.Y, localAABB.Max.Z);
            corners[6] = new Vector3(localAABB.Min.X, localAABB.Max.Y, localAABB.Max.Z);
            corners[7] = new Vector3(localAABB.Max.X, localAABB.Max.Y, localAABB.Max.Z);
            for (int i = 0; i < 8; i++)
            {
                var wp = Vector3.Transform(corners[i], R) + b.Position;
                min = Vector3.Min(min, wp);
                max = Vector3.Max(max, wp);
            }
            b.AABBMin = min;
            b.AABBMax = max;
        }

        // ===========================================================================
        //  约束管理
        // ===========================================================================

        public int AddPointConstraint(BodyHandle a, BodyHandle b, Vector3 worldAnchor)
        {
            if (!ValidateHandle(a) || !ValidateHandle(b))
                throw new InvalidOperationException("无效的BodyHandle");

            var con = new ConstraintData
            {
                BodyAIndex = a.Index,
                BodyBIndex = b.Index,
                Type = ConstraintType.Point,
                Enabled = true,
                AnchorA = WorldToLocal(a.Index, worldAnchor),
                AnchorB = WorldToLocal(b.Index, worldAnchor),
            };
            _constraints.Add(con);
            WakeUp(a);
            WakeUp(b);
            return _constraints.Count - 1;
        }

        public int AddDistanceConstraint(BodyHandle a, BodyHandle b, float distance)
        {
            if (!ValidateHandle(a) || !ValidateHandle(b))
                throw new InvalidOperationException("无效的BodyHandle");

            var con = new ConstraintData
            {
                BodyAIndex = a.Index,
                BodyBIndex = b.Index,
                Type = ConstraintType.Distance,
                Enabled = true,
                ExtraMin = distance,
                ExtraMax = distance,
            };
            _constraints.Add(con);
            WakeUp(a);
            WakeUp(b);
            return _constraints.Count - 1;
        }

        public int AddHingeConstraint(BodyHandle a, BodyHandle b, Vector3 worldAnchor, Vector3 axis)
        {
            if (!ValidateHandle(a) || !ValidateHandle(b))
                throw new InvalidOperationException("无效的BodyHandle");

            var con = new ConstraintData
            {
                BodyAIndex = a.Index,
                BodyBIndex = b.Index,
                Type = ConstraintType.Hinge,
                Enabled = true,
                AnchorA = WorldToLocal(a.Index, worldAnchor),
                AnchorB = WorldToLocal(b.Index, worldAnchor),
                ExtraAxis = axis,
            };
            _constraints.Add(con);
            WakeUp(a);
            WakeUp(b);
            return _constraints.Count - 1;
        }

        public bool RemoveConstraint(int constraintId)
        {
            if (constraintId < 0 || constraintId >= _constraints.Count) return false;
            var con = _constraints[constraintId];
            _constraints[constraintId] = con with { Enabled = false };
            return true;
        }

        private Vector3 WorldToLocal(int bodyIndex, Vector3 worldPoint)
        {
            ref var b = ref _bodies.AsSpan()[bodyIndex];
            var invRot = Quaternion.Inverse(b.Orientation);
            return Vector3.Transform(worldPoint - b.Position, invRot);
        }

        // ===========================================================================
        //  查询
        // ===========================================================================

        /// <summary>射线检测。返回最近命中。</summary>
        public bool Raycast(Vector3 origin, Vector3 direction, float maxDistance, out RaycastResult result)
        {
            return Broadphase!.Raycast(_bodies, Shapes!, origin, direction, maxDistance, out result);
        }

        /// <summary>重叠查询。返回所有AABB重叠的body。</summary>
        public List<BodyHandle> OverlapBox(Vector3 center, Vector3 halfExtents, int layerMask = ~0)
        {
            var results = new List<BodyHandle>();
            var min = center - halfExtents;
            var max = center + halfExtents;
            var candidates = Broadphase!.QueryAABB(min, max);
            var bodies = _bodies.AsSpan();
            foreach (int idx in candidates)
            {
                if (!bodies[idx].Active) continue;
                if ((bodies[idx].CollisionLayer & layerMask) == 0) continue;
                results.Add(new BodyHandle(idx, bodies[idx].Generation));
            }
            return results;
        }

        /// <summary>设置刚体的位置（会唤醒）。</summary>
        public void SetPosition(BodyHandle handle, Vector3 position)
        {
            if (!ValidateHandle(handle)) return;
            ref var b = ref _bodies.AsSpan()[handle.Index];
            b.Position = position;
            b.AABBDirty = true;
            if (b.IsSleeping) WakeUp(handle);
        }

        /// <summary>设置刚体的速度（会唤醒）。</summary>
        public void SetLinearVelocity(BodyHandle handle, Vector3 velocity)
        {
            if (!ValidateHandle(handle)) return;
            ref var b = ref _bodies.AsSpan()[handle.Index];
            b.LinearVelocity = velocity;
            if (b.IsSleeping) WakeUp(handle);
        }

        /// <summary>施加冲量（会唤醒）。</summary>
        public void ApplyLinearImpulse(BodyHandle handle, Vector3 impulse)
        {
            if (!ValidateHandle(handle)) return;
            ref var b = ref _bodies.AsSpan()[handle.Index];
            if (b.MotionType == BodyMotionType.Static) return;
            if (b.IsSleeping) WakeUp(handle);
            b.LinearVelocity += impulse * b.InvMass;
        }

        /// <summary>施加角冲量。</summary>
        public void ApplyAngularImpulse(BodyHandle handle, Vector3 angularImpulse)
        {
            if (!ValidateHandle(handle)) return;
            ref var b = ref _bodies.AsSpan()[handle.Index];
            if (b.MotionType == BodyMotionType.Static) return;
            if (b.IsSleeping) WakeUp(handle);
            b.AngularVelocity += TransformByInertia(angularImpulse, b.InvInertiaWorld);
        }

        /// <summary>在指定世界点施加冲量。</summary>
        public void ApplyImpulseAtPoint(BodyHandle handle, Vector3 impulse, Vector3 worldPoint)
        {
            if (!ValidateHandle(handle)) return;
            ref var b = ref _bodies.AsSpan()[handle.Index];
            if (b.MotionType == BodyMotionType.Static) return;
            if (b.IsSleeping) WakeUp(handle);

            b.LinearVelocity += impulse * b.InvMass;
            var r = worldPoint - b.Position;
            var angularImpulse = Vector3.Cross(r, impulse);
            b.AngularVelocity += TransformByInertia(angularImpulse, b.InvInertiaWorld);
        }

        /// <summary>施加持续力（持续到下一帧清零）。</summary>
        public void ApplyForce(BodyHandle handle, Vector3 force)
        {
            if (!ValidateHandle(handle)) return;
            ref var b = ref _bodies.AsSpan()[handle.Index];
            if (b.MotionType != BodyMotionType.Dynamic) return;
            if (b.IsSleeping) WakeUp(handle);
            b.ForceAccumulator += force;
        }

        /// <summary>施加持续扭矩。</summary>
        public void ApplyTorque(BodyHandle handle, Vector3 torque)
        {
            if (!ValidateHandle(handle)) return;
            ref var b = ref _bodies.AsSpan()[handle.Index];
            if (b.MotionType != BodyMotionType.Dynamic) return;
            if (b.IsSleeping) WakeUp(handle);
            b.TorqueAccumulator += torque;
        }

        // ===========================================================================
        //  统计 & 调试
        // ===========================================================================

        public int GetBodyCount() => _bodies.Count - _freeSlots.Count;
        public int GetContactCount() => _contacts.Count;
        public int GetConstraintCount() => _constraints.Count;
        public int GetSleepingCount() => Stats.SleepingBodyCount;

        /// <summary>获取所有body的快照（调试用，慎用，会alloc很多）。</summary>
        public IEnumerable<(BodyHandle handle, Vector3 pos, Quaternion rot)> GetBodySnapshot()
        {
            var bodies = _bodies;
            for (int i = 0; i < bodies.Count; i++)
            {
                if (!bodies[i].Active) continue;
                yield return (new BodyHandle(i, bodies[i].Generation), bodies[i].Position, bodies[i].Orientation);
            }
        }

        /// <summary>获取所有接触点（调试可视化用）。</summary>
        public IEnumerable<(Vector3 point, Vector3 normal, float penetration)> GetContactSnapshot()
        {
            var contacts = _contacts;
            for (int i = 0; i < contacts.Count; i++)
            {
                if (!contacts[i].Valid) continue;
                yield return (contacts[i].Point, contacts[i].Normal, contacts[i].Penetration);
            }
        }

        // ===========================================================================
        //  IDisposable
        // ===========================================================================

        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _jobSystem.Dispose();
            _bodies.Clear();
            _contacts.Clear();
            _constraints.Clear();
            _islands.Clear();
            _freeSlots.Clear();
        }
    }

    /// <summary>
    /// 创建刚体用的描述结构。
    /// </summary>
    public struct BodyDesc
    {
        public BodyMotionType MotionType;
        public Vector3 Position;
        public Quaternion Orientation;
        public Vector3 LinearVelocity;
        public Vector3 AngularVelocity;
        public float Mass;
        public Matrix4x4 InvInertiaTensor;
        public float LinearDamping;
        public float AngularDamping;
        public float Restitution;
        public float Friction;
        public bool IsTrigger;
        public bool AutoSleep;
        public int CollisionLayer;
        public int CollisionMask;
        public float GravityScale;
        public int ShapeId;
        public object? UserData;

        public static BodyDesc DefaultDynamic => new()
        {
            MotionType = BodyMotionType.Dynamic,
            Mass = 1f,
            LinearDamping = 0.05f,
            AngularDamping = 0.05f,
            Restitution = 0.3f,
            Friction = 0.5f,
            AutoSleep = true,
            CollisionLayer = unchecked((int)0xFFFFFFFF),
            CollisionMask = unchecked((int)0xFFFFFFFF),
            GravityScale = 1f,
            InvInertiaTensor = Matrix4x4.Identity,
        };

        public static BodyDesc DefaultStatic => new()
        {
            MotionType = BodyMotionType.Static,
            Mass = 0f,    // 静态体质量可以是0
            LinearDamping = 0f,
            AngularDamping = 0f,
            Restitution = 0.3f,
            Friction = 0.5f,
            AutoSleep = false,
            CollisionLayer = unchecked((int)0xFFFFFFFF),
            CollisionMask = unchecked((int)0xFFFFFFFF),
            GravityScale = 0f,
            InvInertiaTensor = Matrix4x4.Identity,
        };
    }

    /// <summary>
    /// 射线检测结果。
    /// </summary>
    public struct RaycastResult
    {
        public BodyHandle Body;
        public Vector3 Point;
        public Vector3 Normal;
        public float Distance;
        public bool Hit;
    }
}
