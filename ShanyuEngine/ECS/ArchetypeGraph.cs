// ════════════════════════════════════════════════════════════════════════════
// 山雨引擎 - 原型迁移图 (ArchetypeGraph)
//
// 当给实体添加/移除组件时，实体会从一个 archetype 迁移到另一个 archetype。
// 迁移图记录了这些迁移关系，可以快速查找目标 archetype。
//
// 核心概念:
//   * 节点 = Archetype
//   * 边 = 添加/移除一个组件的迁移
//   * AddEdge(A, T) -> B: 从 A 添加组件 T 后到达 B
//   * RemoveEdge(A, T) -> B: 从 A 移除组件 T 后到达 B
//
// 有了迁移图，添加/移除组件时不需要每次都计算新的类型集合并查找，
// 只需要查图的邻接表，O(1) 复杂度。
//
// 参考 Unity ECS 和 Bevy ECS 的 archetype graph。
//
// FIXME: 图的节点和边没有序列化，重启后会丢失缓存
// TODO: 加个图的可视化
// ════════════════════════════════════════════════════════════════════════════

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;

namespace ShanyuEngine.ECS
{
    /// <summary>
    /// 原型迁移图：记录 archetype 之间的迁移关系。
    /// </summary>
    public sealed class ArchetypeGraph : IDisposable
    {
        // ──────────────────────────────────────────────────────────────────────
        // 字段
        // ──────────────────────────────────────────────────────────────────────

        // 邻接表：archetype -> (操作, 组件类型) -> 目标 archetype
        private readonly Dictionary<ArchetypeReal, Dictionary<GraphEdge, ArchetypeReal>> _addEdges = new();
        private readonly Dictionary<ArchetypeReal, Dictionary<GraphEdge, ArchetypeReal>> _removeEdges = new();

        // 所有节点
        private readonly HashSet<ArchetypeReal> _nodes = new();

        // 反向索引：类型集 -> archetype（用于查找）
        private readonly Dictionary<TypeSet, ArchetypeReal> _typeSetToArchetype = new();

        private readonly ArchetypeManager _archetypeManager;
        private bool _disposed;

        /// <summary>节点数</summary>
        public int NodeCount => _nodes.Count;

        /// <summary>Add 边数</summary>
        public int AddEdgeCount => _addEdges.Values.Sum(d => d.Count);

        /// <summary>Remove 边数</summary>
        public int RemoveEdgeCount => _removeEdges.Values.Sum(d => d.Count);

        // 统计
        public GraphStats Stats { get; } = new GraphStats();

        // ──────────────────────────────────────────────────────────────────────
        // 构造
        // ──────────────────────────────────────────────────────────────────────

        public ArchetypeGraph(ArchetypeManager archetypeManager)
        {
            _archetypeManager = archetypeManager ?? throw new ArgumentNullException(nameof(archetypeManager));
        }

        public void Dispose()
        {
            if (_disposed) return;
            _addEdges.Clear();
            _removeEdges.Clear();
            _nodes.Clear();
            _typeSetToArchetype.Clear();
            _disposed = true;
        }

        // ──────────────────────────────────────────────────────────────────────
        // 节点管理
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// 注册一个 archetype 到图中。
        /// </summary>
        public void RegisterNode(ArchetypeReal archetype)
        {
            if (archetype == null) throw new ArgumentNullException(nameof(archetype));
            _nodes.Add(archetype);

            // 建立类型集索引
            var typeSet = new TypeSet(archetype.ComponentTypes);
            _typeSetToArchetype[typeSet] = archetype;

            // 初始化邻接表
            if (!_addEdges.ContainsKey(archetype))
                _addEdges[archetype] = new Dictionary<GraphEdge, ArchetypeReal>();
            if (!_removeEdges.ContainsKey(archetype))
                _removeEdges[archetype] = new Dictionary<GraphEdge, ArchetypeReal>();
        }

        // ──────────────────────────────────────────────────────────────────────
        // 迁移查询
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// 查找从指定 archetype 添加一个组件后的目标 archetype。
        /// 如果图中没有缓存，会动态计算并缓存。
        /// </summary>
        public ArchetypeReal GetAddTarget(ArchetypeReal source, ComponentTypeReal componentToAdd)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));

            // 检查是否已有组件
            if (source.HasComponent(componentToAdd))
            {
                return source; // 已有，不需要迁移
            }

            var edge = new GraphEdge { ComponentType = componentToAdd };

            // 查缓存
            if (_addEdges.TryGetValue(source, out var edges) && edges.TryGetValue(edge, out var cached))
            {
                Stats.CacheHits++;
                return cached;
            }

            // 计算新的类型集合
            var newTypes = new List<ComponentTypeReal>(source.ComponentTypes) { componentToAdd };
            var targetTypeSet = new TypeSet(newTypes);

            // 查找或创建目标 archetype
            ArchetypeReal target;
            if (_typeSetToArchetype.TryGetValue(targetTypeSet, out var existing))
            {
                target = existing;
            }
            else
            {
                // FIXME: 实际应该调用 ArchetypeManager 创建
                // target = _archetypeManager.GetOrCreateArchetype(newTypes);
                // 这里简化处理
                target = null; // 占位
                Stats.Misses++;
                return target;
            }

            // 缓存边
            if (!_addEdges.ContainsKey(source))
                _addEdges[source] = new Dictionary<GraphEdge, ArchetypeReal>();
            _addEdges[source][edge] = target;

            // 同时缓存反向的 remove 边
            var removeEdge = new GraphEdge { ComponentType = componentToAdd };
            if (!_removeEdges.ContainsKey(target))
                _removeEdges[target] = new Dictionary<GraphEdge, ArchetypeReal>();
            _removeEdges[target][removeEdge] = source;

            Stats.CacheMisses++;
            return target;
        }

        /// <summary>
        /// 查找从指定 archetype 移除一个组件后的目标 archetype。
        /// </summary>
        public ArchetypeReal GetRemoveTarget(ArchetypeReal source, ComponentTypeReal componentToRemove)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));

            // 检查是否有该组件
            if (!source.HasComponent(componentToRemove))
            {
                return source; // 没有，不需要迁移
            }

            var edge = new GraphEdge { ComponentType = componentToRemove };

            // 查缓存
            if (_removeEdges.TryGetValue(source, out var edges) && edges.TryGetValue(edge, out var cached))
            {
                Stats.CacheHits++;
                return cached;
            }

            // 计算新的类型集合
            var newTypes = new List<ComponentTypeReal>(source.ComponentTypes);
            newTypes.RemoveAll(t => t.TypeIndex == componentToRemove.TypeIndex);
            var targetTypeSet = new TypeSet(newTypes);

            // 查找目标 archetype
            ArchetypeReal target;
            if (_typeSetToArchetype.TryGetValue(targetTypeSet, out var existing))
            {
                target = existing;
            }
            else
            {
                // 如果目标类型集为空，返回 null（表示空 archetype）
                if (newTypes.Count == 0)
                {
                    target = null;
                }
                else
                {
                    // FIXME: 创建新 archetype
                    target = null;
                }
                Stats.Misses++;
                return target;
            }

            // 缓存边
            if (!_removeEdges.ContainsKey(source))
                _removeEdges[source] = new Dictionary<GraphEdge, ArchetypeReal>();
            _removeEdges[source][edge] = target;

            // 缓存反向 add 边
            var addEdge = new GraphEdge { ComponentType = componentToRemove };
            if (target != null)
            {
                if (!_addEdges.ContainsKey(target))
                    _addEdges[target] = new Dictionary<GraphEdge, ArchetypeReal>();
                _addEdges[target][addEdge] = source;
            }

            Stats.CacheMisses++;
            return target;
        }

        /// <summary>
        /// 批量添加组件的迁移。
        /// </summary>
        public ArchetypeReal GetAddMultipleTarget(ArchetypeReal source, params ComponentTypeReal[] componentsToAdd)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (componentsToAdd == null || componentsToAdd.Length == 0) return source;

            ArchetypeReal current = source;
            foreach (var comp in componentsToAdd)
            {
                current = GetAddTarget(current, comp);
                if (current == null) break;
            }
            return current;
        }

        /// <summary>
        /// 批量移除组件的迁移。
        /// </summary>
        public ArchetypeReal GetRemoveMultipleTarget(ArchetypeReal source, params ComponentTypeReal[] componentsToRemove)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (componentsToRemove == null || componentsToRemove.Length == 0) return source;

            ArchetypeReal current = source;
            foreach (var comp in componentsToRemove)
            {
                current = GetRemoveTarget(current, comp);
                if (current == null) break;
            }
            return current;
        }

        // ──────────────────────────────────────────────────────────────────────
        // 路径查找
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// 查找从 source 到 target 的最短迁移路径。
        /// 返回需要添加/移除的组件序列。
        /// </summary>
        public List<MigrationStep> FindPath(ArchetypeReal source, ArchetypeReal target)
        {
            if (source == target) return new List<MigrationStep>();

            // BFS
            var queue = new Queue<(ArchetypeReal archetype, List<MigrationStep> path)>();
            var visited = new HashSet<ArchetypeReal>();
            queue.Enqueue((source, new List<MigrationStep>()));
            visited.Add(source);

            while (queue.Count > 0)
            {
                var (current, path) = queue.Dequeue();

                if (current == target)
                    return path;

                // 遍历 add 边
                if (_addEdges.TryGetValue(current, out var addEdges))
                {
                    foreach (var kv in addEdges)
                    {
                        var next = kv.Value;
                        if (next != null && !visited.Contains(next))
                        {
                            visited.Add(next);
                            var newPath = new List<MigrationStep>(path)
                            {
                                new MigrationStep
                                {
                                    Operation = MigrationOp.Add,
                                    Component = kv.Key.ComponentType,
                                }
                            };
                            queue.Enqueue((next, newPath));
                        }
                    }
                }

                // 遍历 remove 边
                if (_removeEdges.TryGetValue(current, out var removeEdges))
                {
                    foreach (var kv in removeEdges)
                    {
                        var next = kv.Value;
                        if (next != null && !visited.Contains(next))
                        {
                            visited.Add(next);
                            var newPath = new List<MigrationStep>(path)
                            {
                                new MigrationStep
                                {
                                    Operation = MigrationOp.Remove,
                                    Component = kv.Key.ComponentType,
                                }
                            };
                            queue.Enqueue((next, newPath));
                        }
                    }
                }
            }

            return null; // 找不到路径
        }

        /// <summary>
        /// 计算两个 archetype 之间的差异（需要添加/移除的组件）。
        /// </summary>
        public void ComputeDiff(ArchetypeReal source, ArchetypeReal target,
                                 out List<ComponentTypeReal> toAdd,
                                 out List<ComponentTypeReal> toRemove)
        {
            toAdd = new List<ComponentTypeReal>();
            toRemove = new List<ComponentTypeReal>();

            var sourceTypes = new HashSet<int>(source.ComponentTypes.Select(t => t.TypeIndex));
            var targetTypes = new HashSet<int>(target.ComponentTypes.Select(t => t.TypeIndex));

            // 需要 add 的：在 target 但不在 source
            foreach (var t in target.ComponentTypes)
            {
                if (!sourceTypes.Contains(t.TypeIndex))
                    toAdd.Add(t);
            }

            // 需要 remove 的：在 source 但不在 target
            foreach (var t in source.ComponentTypes)
            {
                if (!targetTypes.Contains(t.TypeIndex))
                    toRemove.Add(t);
            }
        }

        // ──────────────────────────────────────────────────────────────────────
        // 诊断
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// 生成图的描述。
        /// </summary>
        public string ToDebugString()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"═══ ArchetypeGraph ═══");
            sb.AppendLine($"节点: {NodeCount}, Add边: {AddEdgeCount}, Remove边: {RemoveEdgeCount}");
            sb.AppendLine($"缓存命中: {Stats.CacheHits}, 未命中: {Stats.CacheMisses}");
            sb.AppendLine();

            sb.AppendLine("── 节点 ──");
            foreach (var node in _nodes)
            {
                var types = string.Join(",", node.ComponentTypes.Select(t => t.Type?.Name ?? "?"));
                sb.AppendLine($"  [{types}]");
            }

            sb.AppendLine();
            sb.AppendLine("── Add 边 ──");
            foreach (var kv in _addEdges)
            {
                var sourceTypes = string.Join(",", kv.Key.ComponentTypes.Select(t => t.Type?.Name ?? "?"));
                foreach (var edge in kv.Value)
                {
                    var targetTypes = edge.Value != null
                        ? string.Join(",", edge.Value.ComponentTypes.Select(t => t.Type?.Name ?? "?"))
                        : "null";
                    sb.AppendLine($"  [{sourceTypes}] +{edge.Key.ComponentType.Type?.Name} -> [{targetTypes}]");
                }
            }

            sb.AppendLine();
            sb.AppendLine("── Remove 边 ──");
            foreach (var kv in _removeEdges)
            {
                var sourceTypes = string.Join(",", kv.Key.ComponentTypes.Select(t => t.Type?.Name ?? "?"));
                foreach (var edge in kv.Value)
                {
                    var targetTypes = edge.Value != null
                        ? string.Join(",", edge.Value.ComponentTypes.Select(t => t.Type?.Name ?? "?"))
                        : "null";
                    sb.AppendLine($"  [{sourceTypes}] -{edge.Key.ComponentType.Type?.Name} -> [{targetTypes}]");
                }
            }

            return sb.ToString();
        }

        // ──────────────────────────────────────────────────────────────────────
        // 内部类型
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>图的边</summary>
        private struct GraphEdge : IEquatable<GraphEdge>
        {
            public ComponentTypeReal ComponentType;

            public bool Equals(GraphEdge other) => ComponentType.TypeIndex == other.ComponentType.TypeIndex;
            public override bool Equals(object obj) => obj is GraphEdge e && Equals(e);
            public override int GetHashCode() => ComponentType.TypeIndex;
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 迁移步骤
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>迁移操作类型</summary>
    public enum MigrationOp
    {
        Add,
        Remove,
    }

    /// <summary>迁移步骤</summary>
    public struct MigrationStep
    {
        public MigrationOp Operation;
        public ComponentTypeReal Component;

        public override string ToString() => $"{Operation} {Component.Type?.Name ?? "?"}";
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 图统计
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>迁移图统计</summary>
    public class GraphStats
    {
        public long CacheHits;
        public long CacheMisses;

        public float HitRate => (CacheHits + CacheMisses) > 0
            ? (float)CacheHits / (CacheHits + CacheMisses) * 100f
            : 0f;

        public override string ToString() => $"命中={CacheHits}, 未命中={CacheMisses}, 命中率={HitRate:F1}%";
    }
}
