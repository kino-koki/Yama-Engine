// ════════════════════════════════════════════════════════════════════════════
// yama引擎 - 组件系统组 (ComponentSystemGroup)
//
// 把相关联的 ECS 系统组织成组，统一管理更新顺序。
// 系统组可以嵌套，形成树形结构。
//
// 参考 Unity ECS 的 ComponentSystemGroup:
//   * SimulationSystemGroup
//   * PresentationSystemGroup
//   * FixedStepSimulationSystemGroup
//   等等
//
// 主要功能:
//   * 系统组树形结构
//   * 组内系统排序
//   * 支持固定步长更新
//   * 支持排序回调
//   * 支持启用/禁用整个组
//
// FIXME: 排序回调还没实现
// TODO: 加个可视化工具显示系统组树
// ════════════════════════════════════════════════════════════════════════════

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

namespace YamaEngine.ECS
{
    /// <summary>
    /// 组件系统组：管理一组相关系统的更新。
    /// 可以嵌套形成树形结构。
    /// </summary>
    public class ComponentSystemGroup : SystemBaseReal
    {
        // ──────────────────────────────────────────────────────────────────────
        // 字段
        // ──────────────────────────────────────────────────────────────────────

        private readonly List<SystemBaseReal> _systems = new();
        private readonly List<ComponentSystemGroup> _subGroups = new();
        private ComponentSystemGroup _parent;
        private bool _enabled = true;
        private bool _needsSort = true;
        private readonly List<SortCallbackEntry> _sortCallbacks = new();

        /// <summary>组名称</summary>
        public string GroupName { get; }

        /// <summary>父组</summary>
        public ComponentSystemGroup Parent => _parent;

        /// <summary>是否启用</summary>
        public override bool Enabled
        {
            get => _enabled;
            set => _enabled = value;
        }

        /// <summary>子系统数</summary>
        public int SystemCount => _systems.Count;

        /// <summary>子组数</summary>
        public int SubGroupCount => _subGroups.Count;

        /// <summary>是否需要排序</summary>
        public bool NeedsSort => _needsSort;

        // ──────────────────────────────────────────────────────────────────────
        // 构造
        // ──────────────────────────────────────────────────────────────────────

        public ComponentSystemGroup(string name = null)
        {
            GroupName = name ?? GetType().Name;
            Name = GroupName;
        }

        // ──────────────────────────────────────────────────────────────────────
        // 系统管理
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// 添加系统到组。
        /// </summary>
        public T AddSystem<T>(T system) where T : SystemBaseReal
        {
            if (system == null) throw new ArgumentNullException(nameof(system));
            if (system is ComponentSystemGroup subgroup)
            {
                _subGroups.Add(subgroup);
                subgroup._parent = this;
            }
            _systems.Add(system);
            _needsSort = true;
            return system;
        }

        /// <summary>
        /// 从组中移除系统。
        /// </summary>
        public bool RemoveSystem<T>(T system) where T : SystemBaseReal
        {
            if (system == null) return false;
            if (system is ComponentSystemGroup subgroup)
            {
                _subGroups.Remove(subgroup);
                subgroup._parent = null;
            }
            var removed = _systems.Remove(system);
            if (removed) _needsSort = true;
            return removed;
        }

        /// <summary>
        /// 获取指定类型的系统。
        /// </summary>
        public T GetSystem<T>() where T : SystemBaseReal
        {
            foreach (var sys in _systems)
            {
                if (sys is T typed) return typed;
            }
            return null;
        }

        /// <summary>
        /// 获取所有系统。
        /// </summary>
        public IReadOnlyList<SystemBaseReal> GetSystems() => _systems;

        /// <summary>
        /// 获取所有子组。
        /// </summary>
        public IReadOnlyList<ComponentSystemGroup> GetSubGroups() => _subGroups;

        /// <summary>
        /// 启用/禁用组内所有系统。
        /// </summary>
        public void SetAllEnabled(bool enabled)
        {
            _enabled = enabled;
            foreach (var sys in _systems)
            {
                sys.Enabled = enabled;
                if (sys is ComponentSystemGroup group)
                    group.SetAllEnabled(enabled);
            }
        }

        // ──────────────────────────────────────────────────────────────────────
        // 排序
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// 设置排序回调。回调返回负数表示 a 在 b 前。
        /// </summary>
        public void AddSortCallback(Comparison<SystemBaseReal> comparison)
        {
            _sortCallbacks.Add(new SortCallbackEntry { Comparison = comparison });
            _needsSort = true;
        }

        /// <summary>
        /// 强制要求某系统在另一系统之前执行。
        /// </summary>
        public void UpdateBefore<TBefore, TAfter>()
            where TBefore : SystemBaseReal
            where TAfter : SystemBaseReal
        {
            AddSortCallback((a, b) =>
            {
                if (a is TBefore && b is TAfter) return -1;
                if (a is TAfter && b is TBefore) return 1;
                return 0;
            });
        }

        /// <summary>
        /// 强制要求某系统在另一系统之后执行。
        /// </summary>
        public void UpdateAfter<TAfter, TBefore>()
            where TAfter : SystemBaseReal
            where TBefore : SystemBaseReal
        {
            UpdateBefore<TBefore, TAfter>();
        }

        /// <summary>
        /// 执行排序。
        /// </summary>
        private void SortSystems()
        {
            if (!_needsSort) return;

            // 按 UpdateBefore/UpdateAfter 约束排序
            // 简单实现：用排序回调
            if (_sortCallbacks.Count > 0)
            {
                _systems.Sort((a, b) =>
                {
                    foreach (var cb in _sortCallbacks)
                    {
                        int result = cb.Comparison(a, b);
                        if (result != 0) return result;
                    }
                    return 0;
                });
            }

            _needsSort = false;
        }

        // ──────────────────────────────────────────────────────────────────────
        // 更新
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// 更新组内所有系统。
        /// </summary>
        public override void OnUpdate(float deltaTime)
        {
            if (!_enabled) return;

            if (_needsSort) SortSystems();

            foreach (var sys in _systems)
            {
                if (!sys.Enabled) continue;
                sys.OnUpdate(deltaTime);
            }
        }

        // ──────────────────────────────────────────────────────────────────────
        // 诊断
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// 生成系统组树的可视化描述。
        /// </summary>
        public string ToTreeString(int indent = 0)
        {
            var sb = new StringBuilder();
            string prefix = new string(' ', indent * 2);
            sb.AppendLine($"{prefix}[Group] {GroupName} ({(_enabled ? "ON" : "OFF")})");

            foreach (var sys in _systems)
            {
                if (sys is ComponentSystemGroup subgroup)
                {
                    sb.Append(subgroup.ToTreeString(indent + 1));
                }
                else
                {
                    string sysPrefix = new string(' ', (indent + 1) * 2);
                    sb.AppendLine($"{sysPrefix}- {sys.Name ?? sys.GetType().Name} " +
                                  $"({(sys.Enabled ? "ON" : "OFF")})");
                }
            }
            return sb.ToString();
        }

        private struct SortCallbackEntry
        {
            public Comparison<SystemBaseReal> Comparison;
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 预定义系统组
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 根系统组：所有系统的根。
    /// </summary>
    public sealed class RootSystemGroup : ComponentSystemGroup
    {
        public RootSystemGroup() : base("Root") { }
    }

    /// <summary>
    /// 初始化系统组：在帧开始时执行。
    /// </summary>
    public sealed class InitializationSystemGroup : ComponentSystemGroup
    {
        public InitializationSystemGroup() : base("Initialization") { }
    }

    /// <summary>
    /// 模拟系统组：主逻辑更新。
    /// </summary>
    public sealed class SimulationSystemGroup : ComponentSystemGroup
    {
        public SimulationSystemGroup() : base("Simulation") { }
    }

    /// <summary>
    /// 表现系统组：渲染相关的系统。
    /// </summary>
    public sealed class PresentationSystemGroup : ComponentSystemGroup
    {
        public PresentationSystemGroup() : base("Presentation") { }
    }

    /// <summary>
    /// 固定步长模拟系统组：以固定频率更新。
    /// 用于物理等需要固定步长的系统。
    /// </summary>
    public sealed class FixedStepSimulationSystemGroup : ComponentSystemGroup
    {
        private float _fixedDeltaTime = 1f / 60f; // 60fps
        private float _accumulator;

        /// <summary>固定步长（秒）</summary>
        public float FixedDeltaTime
        {
            get => _fixedDeltaTime;
            set => _fixedDeltaTime = value > 0 ? value : _fixedDeltaTime;
        }

        /// <summary>最大每帧步数（防止死循环）</summary>
        public int MaxStepsPerFrame { get; set; } = 5;

        public FixedStepSimulationSystemGroup() : base("FixedStepSimulation") { }

        public override void OnUpdate(float deltaTime)
        {
            if (!Enabled) return;

            _accumulator += deltaTime;
            int steps = 0;

            while (_accumulator >= _fixedDeltaTime && steps < MaxStepsPerFrame)
            {
                base.OnUpdate(_fixedDeltaTime);
                _accumulator -= _fixedDeltaTime;
                steps++;
            }

            // 如果累积太多，丢弃
            if (_accumulator > _fixedDeltaTime * MaxStepsPerFrame)
            {
                _accumulator = 0;
            }
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 系统组构建器
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 系统组构建器：简化系统组的创建和配置。
    /// </summary>
    public sealed class SystemGroupBuilder
    {
        private readonly ComponentSystemGroup _group;

        public SystemGroupBuilder(string name = null)
        {
            _group = new ComponentSystemGroup(name);
        }

        /// <summary>添加系统</summary>
        public SystemGroupBuilder Add<T>() where T : SystemBaseReal, new()
        {
            _group.AddSystem(new T());
            return this;
        }

        /// <summary>添加系统实例</summary>
        public SystemGroupBuilder Add(SystemBaseReal system)
        {
            _group.AddSystem(system);
            return this;
        }

        /// <summary>添加子组</summary>
        public SystemGroupBuilder AddSubGroup(ComponentSystemGroup subgroup)
        {
            _group.AddSystem(subgroup);
            return this;
        }

        /// <summary>设置排序约束</summary>
        public SystemGroupBuilder UpdateBefore<TBefore, TAfter>()
            where TBefore : SystemBaseReal
            where TAfter : SystemBaseReal
        {
            _group.UpdateBefore<TBefore, TAfter>();
            return this;
        }

        /// <summary>构建</summary>
        public ComponentSystemGroup Build()
        {
            return _group;
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 默认系统组配置
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 默认系统组配置：创建标准的三层系统组结构。
    /// </summary>
    public static class DefaultSystemGroupSetup
    {
        /// <summary>
        /// 创建默认的系统组结构。
        /// Root
        ///   ├── Initialization
        ///   ├── Simulation
        ///   │   ├── FixedStepSimulation (物理等)
        ///   │   └── (其他模拟系统)
        ///   └── Presentation
        /// </summary>
        public static RootSystemGroup CreateDefault()
        {
            var root = new RootSystemGroup();
            root.AddSystem(new InitializationSystemGroup());
            root.AddSystem(new SimulationSystemGroup());
            root.AddSystem(new PresentationSystemGroup());

            var sim = root.GetSystem<SimulationSystemGroup>();
            sim.AddSystem(new FixedStepSimulationSystemGroup());

            return root;
        }
    }
}
