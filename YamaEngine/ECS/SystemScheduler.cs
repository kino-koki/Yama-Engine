// ════════════════════════════════════════════════════════════════════════════
// yama引擎 - 系统调度器 (SystemScheduler)
//
// 负责 ECS 系统的调度执行。支持依赖图、并行执行、顺序约束。
//
// 核心概念:
//   * SystemBase: 系统基类，每个系统有 OnUpdate 方法
//   * 依赖图: 系统之间通过依赖关系构建 DAG
//   * 调度: 按拓扑序执行，无依赖的系统可以并行
//   * 系统组: 把相关系统组织成组，统一调度
//
// 参考 Unity ECS 的 ComponentSystemGroup + PlayerLoop，
// 但我们的调度器更灵活，支持真正的并行执行。
//
// FIXME: 依赖检测目前是基于手动声明，应该自动从查询推断
// TODO: 加个可视化工具显示调度图
// ════════════════════════════════════════════════════════════════════════════

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace YamaEngine.ECS
{
    /// <summary>
    /// 系统调度器：管理系统的注册、依赖和调度执行。
    /// </summary>
    public sealed class SystemScheduler : IDisposable
    {
        // ──────────────────────────────────────────────────────────────────────
        // 字段
        // ──────────────────────────────────────────────────────────────────────

        private readonly Dictionary<Type, SystemEntry> _systems = new();
        private readonly List<SystemEntry> _sortedSystems = new();
        private readonly Dictionary<Type, List<Type>> _dependencyGraph = new();
        private bool _needsRebuild = true;
        private readonly object _lock = new();

        /// <summary>已注册系统数</summary>
        public int SystemCount => _systems.Count;

        /// <summary>是否需要重建排序</summary>
        public bool NeedsRebuild => _needsRebuild;

        // 统计
        public SchedulerStats Stats { get; } = new SchedulerStats();

        /// <summary>是否启用并行执行</summary>
        public bool EnableParallelExecution { get; set; } = true;

        /// <summary>最大并行度</summary>
        public int MaxDegreeOfParallelism { get; set; } = Environment.ProcessorCount;

        /// <summary>当前帧时间</summary>
        public float DeltaTime { get; set; }

        /// <summary>当前世界时间</summary>
        public float WorldTime { get; set; }

        /// <summary>当前帧号</summary>
        public ulong FrameCount { get; private set; }

        // ──────────────────────────────────────────────────────────────────────
        // 构造/析构
        // ──────────────────────────────────────────────────────────────────────

        public SystemScheduler()
        {
        }

        public void Dispose()
        {
            lock (_lock)
            {
                foreach (var entry in _systems.Values)
                {
                    if (entry.System is IDisposable disposable)
                        disposable.Dispose();
                }
                _systems.Clear();
                _sortedSystems.Clear();
                _dependencyGraph.Clear();
            }
        }

        // ──────────────────────────────────────────────────────────────────────
        // 系统注册
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// 注册一个系统。
        /// </summary>
        /// <typeparam name="T">系统类型</typeparam>
        /// <param name="system">系统实例</param>
        public T AddSystem<T>(T system) where T : SystemBaseReal
        {
            if (system == null) throw new ArgumentNullException(nameof(system));
            var type = typeof(T);
            lock (_lock)
            {
                if (_systems.ContainsKey(type))
                {
                    throw new InvalidOperationException($"系统 {type.Name} 已注册");
                }

                var entry = new SystemEntry
                {
                    System = system,
                    Type = type,
                    Name = system.Name ?? type.Name,
                    Enabled = true,
                };
                _systems[type] = entry;
                _dependencyGraph[type] = new List<Type>();
                _needsRebuild = true;

                // 调用系统的 OnCreate
                system.OnCreate();
            }
            return system;
        }

        /// <summary>
        /// 注册一个系统（非泛型版本）。
        /// </summary>
        public SystemBaseReal AddSystem(SystemBaseReal system)
        {
            if (system == null) throw new ArgumentNullException(nameof(system));
            var type = system.GetType();
            lock (_lock)
            {
                if (_systems.ContainsKey(type))
                {
                    throw new InvalidOperationException($"系统 {type.Name} 已注册");
                }
                var entry = new SystemEntry
                {
                    System = system,
                    Type = type,
                    Name = system.Name ?? type.Name,
                    Enabled = true,
                };
                _systems[type] = entry;
                _dependencyGraph[type] = new List<Type>();
                _needsRebuild = true;
                system.OnCreate();
            }
            return system;
        }

        /// <summary>
        /// 移除一个系统。
        /// </summary>
        public bool RemoveSystem<T>() where T : SystemBaseReal
        {
            var type = typeof(T);
            lock (_lock)
            {
                if (!_systems.TryGetValue(type, out var entry))
                    return false;

                entry.System.OnDestroy();
                _systems.Remove(type);
                _dependencyGraph.Remove(type);

                // 从其他系统的依赖中移除
                foreach (var deps in _dependencyGraph.Values)
                    deps.Remove(type);

                _needsRebuild = true;
                return true;
            }
        }

        /// <summary>
        /// 获取系统实例。
        /// </summary>
        public T GetSystem<T>() where T : SystemBaseReal
        {
            var type = typeof(T);
            lock (_lock)
            {
                if (_systems.TryGetValue(type, out var entry))
                    return (T)entry.System;
            }
            return null;
        }

        /// <summary>
        /// 启用/禁用系统。
        /// </summary>
        public void SetSystemEnabled<T>(bool enabled) where T : SystemBaseReal
        {
            var type = typeof(T);
            lock (_lock)
            {
                if (_systems.TryGetValue(type, out var entry))
                {
                    entry.Enabled = enabled;
                }
            }
        }

        // ──────────────────────────────────────────────────────────────────────
        // 依赖管理
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// 声明系统依赖：A 依赖 B，表示 B 必须在 A 之前执行。
        /// </summary>
        /// <typeparam name="TDep">被依赖的系统（先执行）</typeparam>
        /// <typeparam name="T">依赖系统（后执行）</typeparam>
        public void AddDependency<TDep, T>()
            where TDep : SystemBaseReal
            where T : SystemBaseReal
        {
            var depType = typeof(TDep);
            var type = typeof(T);
            lock (_lock)
            {
                if (!_dependencyGraph.ContainsKey(type))
                    _dependencyGraph[type] = new List<Type>();
                if (!_dependencyGraph[type].Contains(depType))
                    _dependencyGraph[type].Add(depType);
                _needsRebuild = true;
            }
        }

        /// <summary>
        /// 声明系统必须在另一个系统之前执行。
        /// </summary>
        public void AddDependency(Type before, Type after)
        {
            lock (_lock)
            {
                if (!_dependencyGraph.ContainsKey(after))
                    _dependencyGraph[after] = new List<Type>();
                if (!_dependencyGraph[after].Contains(before))
                    _dependencyGraph[after].Add(before);
                _needsRebuild = true;
            }
        }

        /// <summary>
        /// 移除依赖关系。
        /// </summary>
        public bool RemoveDependency<TDep, T>()
            where TDep : SystemBaseReal
            where T : SystemBaseReal
        {
            var depType = typeof(TDep);
            var type = typeof(T);
            lock (_lock)
            {
                if (_dependencyGraph.TryGetValue(type, out var deps))
                {
                    var removed = deps.Remove(depType);
                    if (removed) _needsRebuild = true;
                    return removed;
                }
            }
            return false;
        }

        // ──────────────────────────────────────────────────────────────────────
        // 调度执行
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// 更新所有系统。按拓扑序执行，无依赖的并行。
        /// </summary>
        public void Update(float deltaTime)
        {
            DeltaTime = deltaTime;
            WorldTime += deltaTime;
            FrameCount++;

            // 重建排序（如果需要）
            if (_needsRebuild)
            {
                RebuildSortOrder();
                _needsRebuild = false;
            }

            if (EnableParallelExecution)
            {
                UpdateParallel();
            }
            else
            {
                UpdateSequential();
            }

            Stats.TotalFrames++;
        }

        /// <summary>
        /// 顺序执行所有系统。
        /// </summary>
        private void UpdateSequential()
        {
            foreach (var entry in _sortedSystems)
            {
                if (!entry.Enabled) continue;

                var sw = Stopwatch.StartNew();
                try
                {
                    entry.System.OnUpdate(DeltaTime);
                    entry.LastExecutionTime = sw.Elapsed.TotalMilliseconds;
                    Stats._totalSystemUpdates++;
                }
                catch (Exception ex)
                {
                    entry.ErrorCount++;
                    Stats._totalErrors++;
                    Console.WriteLine($"[SystemScheduler] 系统 {entry.Name} 执行出错: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// 并行执行系统。同层级的系统（无相互依赖）并行执行。
        /// </summary>
        private void UpdateParallel()
        {
            // 按层级分组
            var layers = ComputeExecutionLayers();

            foreach (var layer in layers)
            {
                var enabledSystems = layer.Where(e => e.Enabled).ToList();
                if (enabledSystems.Count == 0) continue;
                if (enabledSystems.Count == 1)
                {
                    // 单个系统直接执行
                    ExecuteSystem(enabledSystems[0]);
                }
                else
                {
                    // 并行执行同层系统
                    Parallel.ForEach(enabledSystems, new ParallelOptions
                    {
                        MaxDegreeOfParallelism = MaxDegreeOfParallelism,
                    }, ExecuteSystem);
                }
            }
        }

        /// <summary>
        /// 执行单个系统。
        /// </summary>
        private void ExecuteSystem(SystemEntry entry)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                entry.System.OnUpdate(DeltaTime);
                entry.LastExecutionTime = sw.Elapsed.TotalMilliseconds;
                Interlocked.Increment(ref Stats._totalSystemUpdates);
            }
            catch (Exception ex)
            {
                entry.ErrorCount++;
                Interlocked.Increment(ref Stats._totalErrors);
                Console.WriteLine($"[SystemScheduler] 系统 {entry.Name} 执行出错: {ex.Message}");
            }
        }

        // ──────────────────────────────────────────────────────────────────────
        // 拓扑排序
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// 重建系统执行顺序（拓扑排序）。
        /// </summary>
        private void RebuildSortOrder()
        {
            _sortedSystems.Clear();

            // Kahn 算法拓扑排序
            var inDegree = new Dictionary<Type, int>();
            var adjacency = new Dictionary<Type, List<Type>>();

            foreach (var type in _systems.Keys)
            {
                inDegree[type] = 0;
                adjacency[type] = new List<Type>();
            }

            // 构建邻接表
            foreach (var kv in _dependencyGraph)
            {
                var type = kv.Key;
                var deps = kv.Value;
                if (!inDegree.ContainsKey(type)) continue;

                foreach (var dep in deps)
                {
                    if (!inDegree.ContainsKey(dep)) continue;
                    // dep -> type (dep 必须在 type 之前)
                    adjacency[dep].Add(type);
                    inDegree[type]++;
                }
            }

            // 入度为 0 的先入队
            var queue = new Queue<Type>();
            foreach (var kv in inDegree)
            {
                if (kv.Value == 0)
                    queue.Enqueue(kv.Key);
            }

            while (queue.Count > 0)
            {
                var type = queue.Dequeue();
                _sortedSystems.Add(_systems[type]);

                if (adjacency.TryGetValue(type, out var neighbors))
                {
                    foreach (var n in neighbors)
                    {
                        if (--inDegree[n] == 0)
                            queue.Enqueue(n);
                    }
                }
            }

            // 检查环
            if (_sortedSystems.Count != _systems.Count)
            {
                Console.WriteLine("[SystemScheduler] 警告: 检测到循环依赖！部分系统可能不会执行。");
                // 把没排到的也加进去
                foreach (var kv in _systems)
                {
                    if (!_sortedSystems.Contains(kv.Value))
                    {
                        _sortedSystems.Add(kv.Value);
                    }
                }
            }
        }

        /// <summary>
        /// 计算执行层级。同一层级的系统可以并行执行。
        /// </summary>
        private List<List<SystemEntry>> ComputeExecutionLayers()
        {
            var layers = new List<List<SystemEntry>>();
            var processed = new HashSet<Type>();

            while (processed.Count < _sortedSystems.Count)
            {
                var layer = new List<SystemEntry>();

                foreach (var entry in _sortedSystems)
                {
                    if (processed.Contains(entry.Type)) continue;

                    // 检查所有依赖是否已处理
                    bool canRun = true;
                    if (_dependencyGraph.TryGetValue(entry.Type, out var deps))
                    {
                        foreach (var dep in deps)
                        {
                            if (!processed.Contains(dep))
                            {
                                canRun = false;
                                break;
                            }
                        }
                    }

                    if (canRun)
                    {
                        layer.Add(entry);
                    }
                }

                if (layer.Count == 0)
                {
                    // 死锁了，强制加入剩余
                    foreach (var entry in _sortedSystems)
                    {
                        if (!processed.Contains(entry.Type))
                            layer.Add(entry);
                    }
                }

                foreach (var entry in layer)
                    processed.Add(entry.Type);

                layers.Add(layer);
            }

            return layers;
        }

        // ──────────────────────────────────────────────────────────────────────
        // 诊断
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// 生成调度图描述（用于调试）。
        /// </summary>
        public string GenerateScheduleGraph()
        {
            var sb = new StringBuilder();
            sb.AppendLine("═══ 系统调度图 ═══");
            sb.AppendLine($"系统数: {_systems.Count}");
            sb.AppendLine($"并行执行: {EnableParallelExecution} (最大并行度: {MaxDegreeOfParallelism})");
            sb.AppendLine();

            sb.AppendLine("── 执行顺序 ──");
            for (int i = 0; i < _sortedSystems.Count; i++)
            {
                var entry = _sortedSystems[i];
                var status = entry.Enabled ? "ON" : "OFF";
                sb.AppendLine($"  [{i}] {entry.Name} ({status}) " +
                             $"- {entry.LastExecutionTime:F3}ms" +
                             $"{(entry.ErrorCount > 0 ? $" [错误:{entry.ErrorCount}]" : "")}");
            }

            sb.AppendLine();
            sb.AppendLine("── 依赖关系 ──");
            foreach (var kv in _dependencyGraph)
            {
                if (kv.Value.Count > 0)
                {
                    var deps = string.Join(", ", kv.Value.Select(t => t.Name));
                    sb.AppendLine($"  {kv.Key.Name} 依赖: {deps}");
                }
            }

            sb.AppendLine();
            sb.AppendLine("── 统计 ──");
            sb.AppendLine($"  总帧数: {Stats.TotalFrames}");
            sb.AppendLine($"  系统更新次数: {Stats.TotalSystemUpdates}");
            sb.AppendLine($"  错误次数: {Stats.TotalErrors}");

            return sb.ToString();
        }

        /// <summary>
        /// 获取每个系统的执行时间。
        /// </summary>
        public List<(string name, double timeMs, int errorCount)> GetExecutionTimes()
        {
            var result = new List<(string, double, int)>(_sortedSystems.Count);
            foreach (var entry in _sortedSystems)
            {
                result.Add((entry.Name, entry.LastExecutionTime, entry.ErrorCount));
            }
            return result;
        }

        // ──────────────────────────────────────────────────────────────────────
        // 内部类型
        // ──────────────────────────────────────────────────────────────────────

        private class SystemEntry
        {
            public SystemBaseReal System;
            public Type Type;
            public string Name;
            public bool Enabled;
            public double LastExecutionTime;
            public int ErrorCount;
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 调度器统计
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 调度器统计信息。
    /// </summary>
    public class SchedulerStats
    {
        internal int _totalSystemUpdates;
        internal int _totalErrors;

        /// <summary>总帧数</summary>
        public long TotalFrames;

        /// <summary>系统更新总次数</summary>
        public int TotalSystemUpdates => _totalSystemUpdates;

        /// <summary>错误总次数</summary>
        public int TotalErrors => _totalErrors;

        public override string ToString()
        {
            return $"帧数={TotalFrames}, 更新={TotalSystemUpdates}, 错误={TotalErrors}";
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 系统构建器
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 系统构建器：简化系统的注册和依赖声明。
    /// </summary>
    public sealed class SystemBuilder
    {
        private readonly SystemScheduler _scheduler;
        private readonly List<(Type system, Type[] deps)> _pending = new();

        public SystemBuilder(SystemScheduler scheduler)
        {
            _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        }

        /// <summary>添加系统</summary>
        public SystemBuilder Add<T>() where T : SystemBaseReal, new()
        {
            _pending.Add((typeof(T), Array.Empty<Type>()));
            return this;
        }

        /// <summary>添加系统并指定依赖</summary>
        public SystemBuilder Add<T>(params Type[] dependencies) where T : SystemBaseReal, new()
        {
            _pending.Add((typeof(T), dependencies));
            return this;
        }

        /// <summary>构建并注册所有系统</summary>
        public void Build()
        {
            // 先创建所有系统
            var instances = new Dictionary<Type, SystemBaseReal>();
            foreach (var (type, _) in _pending)
            {
                var instance = (SystemBaseReal)Activator.CreateInstance(type);
                instances[type] = instance;
                _scheduler.AddSystem(instance);
            }

            // 再设置依赖
            foreach (var (type, deps) in _pending)
            {
                foreach (var dep in deps)
                {
                    _scheduler.AddDependency(dep, type);
                }
            }

            _pending.Clear();
        }
    }
}
