using System;
using System.Collections.Generic;
using System.Linq;

namespace YamaEngine.ECS
{
    // ════════════════════════════════════════════════════════════════════════
    // yama引擎 - 真实World管理 (WorldManagementReal)
    //
    //   * WorldReal: World容器（EntityManager/SystemGroups/时间/DeltaTime/ElapsedTime）
    //   * WorldSystemManager: 多World管理（Create/Destroy/默认World/跨World传输）
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 固定步长更新模式。
    /// </summary>
    public enum FixedStepMode
    {
        /// <summary>可变步长（直接使用传入的dt）。</summary>
        Variable,
        /// <summary>固定步长（累积器模式）。</summary>
        Fixed
    }

    /// <summary>
    /// World：ECS的最高级容器。包含EntityManager、系统组和时间管理。
    /// 类似Unity的World或UE的UWorld。
    /// </summary>
    public sealed class WorldReal : IDisposable
    {
        /// <summary>World名称。</summary>
        public string Name { get; }

        /// <summary>World是否正在运行。</summary>
        public bool IsRunning { get; private set; }

        /// <summary>实体管理器。</summary>
        public EntityManagerReal EntityManager { get; }

        /// <summary>当前帧的DeltaTime。</summary>
        public float DeltaTime { get; private set; }

        /// <summary>从World创建以来的总时间。</summary>
        public float ElapsedTime { get; private set; }

        /// <summary>当前帧的时间（秒）。</summary>
        public float Time { get; private set; }

        /// <summary>帧计数。</summary>
        public ulong FrameCount { get; private set; }

        /// <summary>固定步长模式。</summary>
        public FixedStepMode StepMode { get; set; } = FixedStepMode.Variable;

        /// <summary>固定步长大小（秒）。</summary>
        public float FixedDeltaTime { get; set; } = 1.0f / 60.0f;

        /// <summary>最大允许的DeltaTime（防止大跳帧）。</summary>
        public float MaxDeltaTime { get; set; } = 0.1f;

        /// <summary>时间缩放。</summary>
        public float TimeScale { get; set; } = 1.0f;

        /// <summary>是否暂停。</summary>
        public bool IsPaused { get; set; }

        /// <summary>系统组列表（按名称索引）。</summary>
        public IReadOnlyDictionary<string, SystemGroupReal> SystemGroups => _systemGroups;

        /// <summary>所有系统组的更新顺序。</summary>
        public IReadOnlyList<string> SystemGroupOrder => _systemGroupOrder;

        /// <summary>实体数量快捷访问。</summary>
        public int EntityCount => EntityManager.AliveEntityCount;

        // 系统组
        private readonly Dictionary<string, SystemGroupReal> _systemGroups = new();
        private readonly List<string> _systemGroupOrder = new();

        // 查询缓存
        private readonly Dictionary<string, EntityQueryReal> _queryCache = new();

        // 固定步长累积器
        private float _fixedAccumulator;

        // 事件
        public event Action<WorldReal, float>? WorldUpdated;
        public event Action<WorldReal>? WorldStarted;
        public event Action<WorldReal>? WorldStopped;

        /// <summary>
        /// 创建World。
        /// </summary>
        public WorldReal(string name = "DefaultWorld", int initialEntityCapacity = 4096)
        {
            Name = name;
            EntityManager = new EntityManagerReal(initialEntityCapacity);

            // 创建默认系统组（按执行顺序）
            AddSystemGroupInternal("Initialization");
            AddSystemGroupInternal("PreUpdate");
            AddSystemGroupInternal("Update");
            AddSystemGroupInternal("PostUpdate");
            AddSystemGroupInternal("Presentation");
        }

        /// <summary>
        /// 静态工厂方法：创建并初始化World。
        /// </summary>
        public static WorldReal Create(string name = "DefaultWorld", int initialEntityCapacity = 4096)
        {
            var world = new WorldReal(name, initialEntityCapacity);
            return world;
        }

        /// <summary>
        /// 添加系统到指定的系统组。
        /// </summary>
        public void AddSystem(SystemBaseReal system, string groupName = "Update")
        {
            if (system == null) throw new ArgumentNullException(nameof(system));

            if (!_systemGroups.TryGetValue(groupName, out var group))
            {
                group = AddSystemGroupInternal(groupName);
            }

            group.AddSystem(system);
            // 如果World已经在运行，立即初始化系统
            if (IsRunning)
                system.SetWorld(this);
        }

        /// <summary>
        /// 从所有组中移除系统。
        /// </summary>
        public void RemoveSystem(SystemBaseReal system)
        {
            if (system == null) return;
            foreach (var group in _systemGroups.Values)
            {
                group.RemoveSystem(system);
            }
        }

        /// <summary>
        /// 获取或创建实体查询（带缓存）。
        /// </summary>
        public EntityQueryReal GetOrCreateQuery(
            IReadOnlyList<Type> allTypes = null,
            IReadOnlyList<Type> anyTypes = null,
            IReadOnlyList<Type> noneTypes = null)
        {
            var key = BuildQueryKey(allTypes, anyTypes, noneTypes);
            if (_queryCache.TryGetValue(key, out var query))
                return query;

            query = EntityManager.CreateQuery(allTypes, anyTypes, noneTypes);
            _queryCache[key] = query;
            return query;
        }

        /// <summary>
        /// 推送时间（外部驱动模式）。
        /// </summary>
        public void PushTime(float newTime)
        {
            DeltaTime = Math.Min(newTime - Time, MaxDeltaTime);
            Time = newTime;
        }

        /// <summary>
        /// 主更新循环：执行所有系统组。
        /// </summary>
        public void Update(float dt)
        {
            if (IsPaused)
            {
                DeltaTime = 0;
                return;
            }

            // 应用时间缩放
            float scaledDt = dt * TimeScale;

            // 限制最大dt
            scaledDt = Math.Min(scaledDt, MaxDeltaTime);
            DeltaTime = scaledDt;
            ElapsedTime += scaledDt;
            Time += scaledDt;
            FrameCount++;

            switch (StepMode)
            {
                case FixedStepMode.Variable:
                    // 可变步长：直接更新一次
                    ExecuteAllGroups(scaledDt);
                    break;

                case FixedStepMode.Fixed:
                    // 固定步长：累积器模式
                    _fixedAccumulator += scaledDt;
                    int maxSteps = 5; // 防止死循环
                    while (_fixedAccumulator >= FixedDeltaTime && maxSteps-- > 0)
                    {
                        ExecuteAllGroups(FixedDeltaTime);
                        _fixedAccumulator -= FixedDeltaTime;
                    }
                    // 如果还有余量，不超过一个FixedDeltaTime
                    if (_fixedAccumulator > FixedDeltaTime)
                        _fixedAccumulator = FixedDeltaTime;
                    break;
            }

            WorldUpdated?.Invoke(this, DeltaTime);
        }

        /// <summary>
        /// 启动World。
        /// </summary>
        public void Start()
        {
            if (IsRunning) return;
            IsRunning = true;

            // 初始化所有系统
            foreach (var group in _systemGroupOrder)
            {
                var sg = _systemGroups[group];
                // 系统在第一次Update时会通过SetWorld自动初始化
            }

            WorldStarted?.Invoke(this);
        }

        /// <summary>
        /// 停止World。
        /// </summary>
        public void Stop()
        {
            if (!IsRunning) return;
            IsRunning = false;
            WorldStopped?.Invoke(this);
        }

        /// <summary>
        /// 重置World时间和帧计数。
        /// </summary>
        public void ResetTime()
        {
            DeltaTime = 0;
            ElapsedTime = 0;
            Time = 0;
            FrameCount = 0;
            _fixedAccumulator = 0;
        }

        /// <summary>
        /// 释放World资源。
        /// </summary>
        public void Dispose()
        {
            Stop();

            foreach (var group in _systemGroups.Values)
                group.Dispose();
            _systemGroups.Clear();
            _systemGroupOrder.Clear();
            _queryCache.Clear();
            EntityManager.Dispose();
        }

        // ═══════════════════════════════════════════════════════════════════
        // 内部方法
        // ═══════════════════════════════════════════════════════════════════

        private void ExecuteAllGroups(float dt)
        {
            foreach (var groupName in _systemGroupOrder)
            {
                if (_systemGroups.TryGetValue(groupName, out var group))
                {
                    group.Update(dt, this);
                }
            }
        }

        private SystemGroupReal AddSystemGroupInternal(string name)
        {
            if (_systemGroups.TryGetValue(name, out var existing))
                return existing;

            var group = new SystemGroupReal(name);
            _systemGroups[name] = group;
            _systemGroupOrder.Add(name);
            return group;
        }

        private static string BuildQueryKey(IReadOnlyList<Type> all, IReadOnlyList<Type> any, IReadOnlyList<Type> none)
        {
            var sb = new System.Text.StringBuilder();
            if (all != null)
            {
                sb.Append("A:");
                foreach (var t in all.OrderBy(t => t.FullName)) sb.Append(t.FullName).Append(',');
            }
            if (any != null)
            {
                sb.Append("Y:");
                foreach (var t in any.OrderBy(t => t.FullName)) sb.Append(t.FullName).Append(',');
            }
            if (none != null)
            {
                sb.Append("N:");
                foreach (var t in none.OrderBy(t => t.FullName)) sb.Append(t.FullName).Append(',');
            }
            return sb.ToString();
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // WorldSystemManager: 多World管理
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 多World管理器：管理多个World实例的创建、销毁和数据传输。
    /// </summary>
    public sealed class WorldSystemManager
    {
        /// <summary>所有活跃的World。</summary>
        public IReadOnlyList<WorldReal> Worlds => _worlds;

        /// <summary>默认World。</summary>
        public WorldReal DefaultWorld { get; private set; }

        /// <summary>当前活跃World（用于编辑器切换）。</summary>
        public WorldReal ActiveWorld { get; set; }

        private readonly List<WorldReal> _worlds = new();
        private readonly Dictionary<string, WorldReal> _worldByName = new();
        private readonly object _lock = new();

        /// <summary>World数量。</summary>
        public int WorldCount => _worlds.Count;

        public event Action<WorldReal>? WorldCreated;
        public event Action<WorldReal>? WorldDestroyed;

        public WorldSystemManager()
        {
            // 自动创建默认World
            DefaultWorld = CreateWorld("DefaultWorld");
            ActiveWorld = DefaultWorld;
        }

        /// <summary>
        /// 创建新World。
        /// </summary>
        public WorldReal CreateWorld(string name, int initialEntityCapacity = 4096)
        {
            lock (_lock)
            {
                // 确保名称唯一
                string uniqueName = name;
                int suffix = 1;
                while (_worldByName.ContainsKey(uniqueName))
                {
                    uniqueName = $"{name}_{suffix++}";
                }

                var world = WorldReal.Create(uniqueName, initialEntityCapacity);
                _worlds.Add(world);
                _worldByName[uniqueName] = world;

                WorldCreated?.Invoke(world);
                return world;
            }
        }

        /// <summary>
        /// 销毁World。
        /// </summary>
        public bool DestroyWorld(WorldReal world)
        {
            if (world == null) return false;
            lock (_lock)
            {
                if (!_worlds.Contains(world))
                    return false;

                if (world == DefaultWorld)
                    return false; // 不允许销毁默认World

                world.Dispose();
                _worlds.Remove(world);
                _worldByName.Remove(world.Name);

                if (ActiveWorld == world)
                    ActiveWorld = DefaultWorld;

                WorldDestroyed?.Invoke(world);
                return true;
            }
        }

        /// <summary>
        /// 按名称获取World。
        /// </summary>
        public WorldReal GetWorld(string name)
        {
            lock (_lock)
            {
                return _worldByName.TryGetValue(name, out var world) ? world : null;
            }
        }

        /// <summary>
        /// 按索引获取World。
        /// </summary>
        public WorldReal GetWorldAt(int index)
        {
            lock (_lock)
            {
                if (index < 0 || index >= _worlds.Count)
                    return null;
                return _worlds[index];
            }
        }

        /// <summary>
        /// 更新所有活跃的World。
        /// </summary>
        public void UpdateAll(float dt)
        {
            lock (_lock)
            {
                foreach (var world in _worlds)
                {
                    if (world.IsRunning)
                        world.Update(dt);
                }
            }
        }

        /// <summary>
        /// 跨World复制实体：将源World的实体序列化后复制到目标World。
        /// </summary>
        public EntityReal TransferEntity(WorldReal sourceWorld, EntityReal sourceEntity, WorldReal destWorld)
        {
            if (sourceWorld == null || destWorld == null)
                throw new ArgumentNullException("World参数不能为null");
            if (!sourceWorld.EntityManager.IsValid(sourceEntity))
                throw new InvalidOperationException("源实体无效");

            // 获取源实体的组件类型和数据
            var compTypes = sourceWorld.EntityManager.GetComponentTypes(sourceEntity);
            var destEntity = destWorld.EntityManager.CreateEntity(sourceWorld.EntityManager.GetName(sourceEntity));

            // 逐组件拷贝
            foreach (var ct in compTypes)
            {
                // 通过反射获取并设置组件数据
                TransferComponent(sourceWorld.EntityManager, sourceEntity, destWorld.EntityManager, destEntity, ct.Type);
            }

            return destEntity;
        }

        /// <summary>
        /// 使用反射跨World传输单个组件。
        /// </summary>
        private static void TransferComponent(
            EntityManagerReal source, EntityReal srcEntity,
            EntityManagerReal dest, EntityReal destEntity,
            Type componentType)
        {
            // 获取泛型方法
            var getSourceMethod = typeof(EntityManagerReal).GetMethod(nameof(EntityManagerReal.GetComponentData));
            var getTyped = getSourceMethod.MakeGenericMethod(componentType);
            var data = getTyped.Invoke(source, new object[] { srcEntity });

            var setDestMethod = typeof(EntityManagerReal).GetMethod(nameof(EntityManagerReal.AddComponent));
            var setTyped = setDestMethod.MakeGenericMethod(componentType);
            setTyped.Invoke(dest, new object[] { destEntity, data });
        }

        /// <summary>
        /// 释放所有World。
        /// </summary>
        public void Dispose()
        {
            lock (_lock)
            {
                foreach (var world in _worlds)
                    world.Dispose();
                _worlds.Clear();
                _worldByName.Clear();
                DefaultWorld = null;
                ActiveWorld = null;
            }
        }
    }
}
