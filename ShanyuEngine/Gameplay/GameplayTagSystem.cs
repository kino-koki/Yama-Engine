// ShanyuEngine - 山雨游戏引擎
// GameplayTagSystem.cs - 标签系统 层级标签
// 标签是GAS的灵魂 所有状态都用标签表达 比如State.Stunned
// 层级标签用点号分隔 比如 Character.Hero.Warrior
// 标签查询超快 这是核心

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace ShanyuEngine.Gameplay
{
    #region 标签节点 树形结构

    /// <summary>
    /// 标签节点 树形结构的一个节点
    /// 标签是层级式的 比如 State.Stunned.Hard
    /// State 是根节点 Stunned 是子节点 Hard 是孙节点
    /// 子节点隐含父节点 有Stunned.Hard就自动有Stunned
    /// </summary>
    public class GameplayTagNode
    {
        // 节点名字 不含完整路径 比如 "Stunned"
        public string Name { get; }

        // 完整标签路径 比如 "State.Stunned"
        public string FullPath { get; }

        // 父节点 根节点的父为null
        public GameplayTagNode? Parent { get; internal set; }

        // 子节点 按名字索引
        private readonly Dictionary<string, GameplayTagNode> _children = new();

        // 所有子节点 只读访问
        public IReadOnlyDictionary<string, GameplayTagNode> Children => _children;

        // 节点深度 根节点为0
        public int Depth { get; internal set; }

        // 节点哈希值 缓存用
        public int HashCode { get; internal set; }

        // 是否是叶子节点 没有子节点
        public bool IsLeaf => _children.Count == 0;

        // 元数据 扩展用
        public Dictionary<string, string> Metadata { get; } = new();

        public GameplayTagNode(string name, string fullPath, GameplayTagNode? parent)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
            FullPath = fullPath ?? throw new ArgumentNullException(nameof(fullPath));
            Parent = parent;
            Depth = parent == null ? 0 : parent.Depth + 1;
            HashCode = fullPath.GetHashCode(StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 获取或创建子节点
        /// </summary>
        internal GameplayTagNode GetOrCreateChild(string childName)
        {
            if (_children.TryGetValue(childName, out var existing))
                return existing;

            string childPath = string.IsNullOrEmpty(FullPath)
                ? childName
                : $"{FullPath}.{childName}";

            var child = new GameplayTagNode(childName, childPath, this)
            {
                Depth = Depth + 1,
            };

            _children[childName] = child;
            return child;
        }

        /// <summary>
        /// 查找子节点
        /// </summary>
        public GameplayTagNode? FindChild(string childName)
        {
            _children.TryGetValue(childName, out var child);
            return child;
        }

        /// <summary>
        /// 获取所有后代节点
        /// </summary>
        public List<GameplayTagNode> GetAllDescendants()
        {
            var result = new List<GameplayTagNode>();
            CollectDescendants(result);
            return result;
        }

        private void CollectDescendants(List<GameplayTagNode> result)
        {
            foreach (var child in _children.Values)
            {
                result.Add(child);
                child.CollectDescendants(result);
            }
        }

        /// <summary>
        /// 获取所有祖先节点
        /// </summary>
        public List<GameplayTagNode> GetAllAncestors()
        {
            var result = new List<GameplayTagNode>();
            var current = Parent;
            while (current != null)
            {
                result.Add(current);
                current = current.Parent;
            }
            return result;
        }

        /// <summary>
        /// 是否是另一个节点的祖先
        /// </summary>
        public bool IsAncestorOf(GameplayTagNode other)
        {
            var current = other.Parent;
            while (current != null)
            {
                if (ReferenceEquals(current, this))
                    return true;
                current = current.Parent;
            }
            return false;
        }

        /// <summary>
        /// 是否是另一个节点的后代
        /// </summary>
        public bool IsDescendantOf(GameplayTagNode other)
        {
            return other.IsAncestorOf(this);
        }

        public override string ToString()
        {
            return FullPath;
        }

        public override int GetHashCode()
        {
            return HashCode;
        }

        public override bool Equals(object? obj)
        {
            if (obj is GameplayTagNode other)
            {
                return string.Equals(FullPath, other.FullPath, StringComparison.OrdinalIgnoreCase);
            }
            return false;
        }
    }

    #endregion

    #region 标签 树中的引用

    /// <summary>
    /// 标签 树中节点的引用
    /// 一个标签包含完整路径和节点引用
    /// </summary>

    #endregion

    #region 标签树

    /// <summary>
    /// 标签树 管理所有标签的层级结构
    /// 所有标签必须先注册到树里 才能使用
    /// </summary>
    public class GameplayTagTree
    {
        // 根节点集合 顶级标签的父
        private readonly Dictionary<string, GameplayTagNode> _roots = new();

        // 所有节点的缓存 按完整路径索引
        private readonly Dictionary<string, GameplayTagNode> _allNodes = new();

        // 锁
        private readonly object _lock = new();

        // 标签数量
        public int Count
        {
            get
            {
                lock (_lock)
                {
                    return _allNodes.Count;
                }
            }
        }

        /// <summary>
        /// 注册一个标签 返回标签结构体
        /// 如果已存在返回已有的
        /// </summary>
        public GameplayTag Register(string tagPath)
        {
            if (string.IsNullOrEmpty(tagPath))
                return GameplayTag.None;

            // 规范化 去掉前后空格和点
            tagPath = tagPath.Trim().Trim('.');

            if (string.IsNullOrEmpty(tagPath))
                return GameplayTag.None;

            lock (_lock)
            {
                // 检查缓存
                if (_allNodes.TryGetValue(tagPath, out var existing))
                    return new GameplayTag(tagPath, existing);

                // 按点分割创建层级
                var parts = tagPath.Split('.');
                GameplayTagNode? current = null;
                string currentPath = string.Empty;

                for (int i = 0; i < parts.Length; i++)
                {
                    var part = parts[i].Trim();
                    if (string.IsNullOrEmpty(part))
                        continue;

                    currentPath = string.IsNullOrEmpty(currentPath) ? part : $"{currentPath}.{part}";

                    if (current == null)
                    {
                        // 根节点
                        if (!_roots.TryGetValue(part, out var root))
                        {
                            root = new GameplayTagNode(part, currentPath, null);
                            _roots[part] = root;
                            _allNodes[currentPath] = root;
                        }
                        current = root;
                    }
                    else
                    {
                        current = current.GetOrCreateChild(part);
                        if (!_allNodes.ContainsKey(currentPath))
                        {
                            _allNodes[currentPath] = current;
                        }
                    }
                }

                return current != null ? new GameplayTag(tagPath, current) : GameplayTag.None;
            }
        }

        /// <summary>
        /// 批量注册标签
        /// </summary>
        public int RegisterRange(IEnumerable<string> tagPaths)
        {
            if (tagPaths == null) return 0;
            int count = 0;
            foreach (var path in tagPaths)
            {
                if (Register(path).IsValid)
                    count++;
            }
            return count;
        }

        /// <summary>
        /// 根据路径查找标签
        /// </summary>
        public GameplayTag Find(string tagPath)
        {
            if (string.IsNullOrEmpty(tagPath))
                return GameplayTag.None;

            tagPath = tagPath.Trim().Trim('.');
            if (string.IsNullOrEmpty(tagPath))
                return GameplayTag.None;

            lock (_lock)
            {
                _allNodes.TryGetValue(tagPath, out var node);
                return new GameplayTag(tagPath, node);
            }
        }

        /// <summary>
        /// 根据路径查找节点
        /// </summary>
        public GameplayTagNode? FindNode(string tagPath)
        {
            if (string.IsNullOrEmpty(tagPath)) return null;
            tagPath = tagPath.Trim().Trim('.');
            if (string.IsNullOrEmpty(tagPath)) return null;

            lock (_lock)
            {
                _allNodes.TryGetValue(tagPath, out var node);
                return node;
            }
        }

        /// <summary>
        /// 获取所有根节点
        /// </summary>
        public List<GameplayTagNode> GetRoots()
        {
            lock (_lock)
            {
                return _roots.Values.ToList();
            }
        }

        /// <summary>
        /// 获取所有标签路径
        /// </summary>
        public List<string> GetAllTagPaths()
        {
            lock (_lock)
            {
                return _allNodes.Keys.ToList();
            }
        }

        /// <summary>
        /// 获取某个节点的所有后代标签
        /// </summary>
        public List<GameplayTag> GetDescendantTags(string tagPath)
        {
            var result = new List<GameplayTag>();
            var node = FindNode(tagPath);
            if (node == null) return result;

            var descendants = node.GetAllDescendants();
            foreach (var d in descendants)
            {
                result.Add(new GameplayTag(d.FullPath, d));
            }
            return result;
        }

        /// <summary>
        /// 获取某个节点的所有祖先标签
        /// </summary>
        public List<GameplayTag> GetAncestorTags(string tagPath)
        {
            var result = new List<GameplayTag>();
            var node = FindNode(tagPath);
            if (node == null) return result;

            var ancestors = node.GetAllAncestors();
            foreach (var a in ancestors)
            {
                result.Add(new GameplayTag(a.FullPath, a));
            }
            return result;
        }

        /// <summary>
        /// 检查标签A是否匹配标签B A是B的祖先或相同
        /// </summary>
        public bool Matches(string tagA, string tagB)
        {
            if (string.IsNullOrEmpty(tagA) || string.IsNullOrEmpty(tagB))
                return false;

            // 快速检查 前缀匹配
            if (string.Equals(tagA, tagB, StringComparison.OrdinalIgnoreCase))
                return true;

            // A必须是B的前缀
            if (tagB.Length > tagA.Length &&
                tagB[tagA.Length] == '.' &&
                tagB.StartsWith(tagA, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return false;
        }

        /// <summary>
        /// 清空所有标签
        /// </summary>
        public void Clear()
        {
            lock (_lock)
            {
                _roots.Clear();
                _allNodes.Clear();
            }
        }

        /// <summary>
        /// 导出树为字符串 方便调试
        /// </summary>
        public string ExportTree()
        {
            var sb = new StringBuilder();
            lock (_lock)
            {
                foreach (var root in _roots.Values)
                {
                    ExportNode(sb, root, 0);
                }
            }
            return sb.ToString();
        }

        private void ExportNode(StringBuilder sb, GameplayTagNode node, int indent)
        {
            sb.Append(' ', indent * 2);
            sb.AppendLine(node.FullPath);

            foreach (var child in node.Children.Values)
            {
                ExportNode(sb, child, indent + 1);
            }
        }
    }

    #endregion

    #region 标签容器 完整实现

    /// <summary>
    /// 标签容器 这个是完整的实现 替代GAS V2里的占位
    /// 管理一组标签 提供快速查询
    /// 容器同时维护显式标签和隐式标签(祖先标签)
    /// </summary>
    public class GameplayTagContainerFull
    {
        // 所属的标签树
        private readonly GameplayTagTree _tree;

        // 显式添加的标签 按路径索引
        private readonly Dictionary<string, GameplayTag> _explicitTags = new();

        // 引用计数 同一个标签被加多次 引用计数追踪
        private readonly Dictionary<string, int> _refCounts = new();

        // 锁
        private readonly object _lock = new();

        // 标签变化事件
        public event Action<GameplayTagContainerFull, GameplayTag>? TagAdded;
        public event Action<GameplayTagContainerFull, GameplayTag>? TagRemoved;

        public GameplayTagContainerFull(GameplayTagTree tree)
        {
            _tree = tree ?? throw new ArgumentNullException(nameof(tree));
        }

        /// <summary>
        /// 当前显式标签数量
        /// </summary>
        public int Count
        {
            get
            {
                lock (_lock)
                {
                    return _explicitTags.Count;
                }
            }
        }

        /// <summary>
        /// 添加标签
        /// </summary>
        public bool AddTag(string tagPath)
        {
            if (string.IsNullOrEmpty(tagPath)) return false;

            var tag = _tree.Register(tagPath);
            if (!tag.IsValid) return false;

            lock (_lock)
            {
                if (_explicitTags.ContainsKey(tag.FullPath))
                {
                    // 已经有了 增加引用计数
                    _refCounts[tag.FullPath] = _refCounts.GetValueOrDefault(tag.FullPath) + 1;
                    return true;
                }

                _explicitTags[tag.FullPath] = tag;
                _refCounts[tag.FullPath] = 1;
            }

            TagAdded?.Invoke(this, tag);
            return true;
        }

        /// <summary>
        /// 批量添加标签
        /// </summary>
        public int AddTags(IEnumerable<string> tagPaths)
        {
            if (tagPaths == null) return 0;
            int count = 0;
            foreach (var path in tagPaths)
            {
                if (AddTag(path))
                    count++;
            }
            return count;
        }

        /// <summary>
        /// 添加标签结构体
        /// </summary>
        public bool AddTag(GameplayTag tag)
        {
            if (!tag.IsValid) return false;

            lock (_lock)
            {
                if (_explicitTags.ContainsKey(tag.FullPath))
                {
                    _refCounts[tag.FullPath] = _refCounts.GetValueOrDefault(tag.FullPath) + 1;
                    return true;
                }

                _explicitTags[tag.FullPath] = tag;
                _refCounts[tag.FullPath] = 1;
            }

            TagAdded?.Invoke(this, tag);
            return true;
        }

        /// <summary>
        /// 移除标签 减少引用计数
        /// </summary>
        public bool RemoveTag(string tagPath)
        {
            if (string.IsNullOrEmpty(tagPath)) return false;

            lock (_lock)
            {
                if (!_explicitTags.TryGetValue(tagPath, out var tag))
                    return false;

                int count = _refCounts.GetValueOrDefault(tagPath);
                if (count > 1)
                {
                    _refCounts[tagPath] = count - 1;
                    return true;
                }

                _explicitTags.Remove(tagPath);
                _refCounts.Remove(tagPath);
            }

            var removedTag = _tree.Find(tagPath);
            TagRemoved?.Invoke(this, removedTag);
            return true;
        }

        /// <summary>
        /// 批量移除标签
        /// </summary>
        public int RemoveTags(IEnumerable<string> tagPaths)
        {
            if (tagPaths == null) return 0;
            int count = 0;
            foreach (var path in tagPaths)
            {
                if (RemoveTag(path))
                    count++;
            }
            return count;
        }

        /// <summary>
        /// 移除标签结构体
        /// </summary>
        public bool RemoveTag(GameplayTag tag)
        {
            if (!tag.IsValid) return false;
            return RemoveTag(tag.FullPath);
        }

        /// <summary>
        /// 是否有某个标签 精确匹配
        /// </summary>
        public bool HasTag(string tagPath)
        {
            if (string.IsNullOrEmpty(tagPath)) return false;
            lock (_lock)
            {
                return _explicitTags.ContainsKey(tagPath);
            }
        }

        /// <summary>
        /// 是否有某个标签 精确匹配
        /// </summary>
        public bool HasTag(GameplayTag tag)
        {
            if (!tag.IsValid) return false;
            return HasTag(tag.FullPath);
        }

        /// <summary>
        /// 是否有任意一个标签 匹配模式
        /// hasAny表示容器里是否有任意一个标签匹配给定的任意一个
        /// </summary>
        public bool HasAnyTag(IEnumerable<string> tagPaths)
        {
            if (tagPaths == null) return false;

            List<GameplayTag> snapshot;
            lock (_lock)
            {
                snapshot = _explicitTags.Values.ToList();
            }

            foreach (var path in tagPaths)
            {
                if (string.IsNullOrEmpty(path)) continue;

                // 先检查精确匹配
                if (HasTag(path))
                    return true;

                // 检查层级匹配 容器里有这个标签的子标签也算
                foreach (var existing in snapshot)
                {
                    if (_tree.Matches(path, existing.FullPath))
                        return true;
                }
            }
            return false;
        }

        /// <summary>
        /// 是否有所有标签 匹配模式
        /// </summary>
        public bool HasAllTags(IEnumerable<string> tagPaths)
        {
            if (tagPaths == null) return true;

            List<GameplayTag> snapshot;
            lock (_lock)
            {
                snapshot = _explicitTags.Values.ToList();
            }

            foreach (var path in tagPaths)
            {
                if (string.IsNullOrEmpty(path)) continue;

                bool found = false;

                // 检查精确匹配
                if (HasTag(path))
                {
                    found = true;
                }
                else
                {
                    // 检查层级匹配
                    foreach (var existing in snapshot)
                    {
                        if (_tree.Matches(path, existing.FullPath))
                        {
                            found = true;
                            break;
                        }
                    }
                }

                if (!found)
                    return false;
            }
            return true;
        }

        /// <summary>
        /// 是否没有任何给定的标签
        /// </summary>
        public bool HasNoneOfTags(IEnumerable<string> tagPaths)
        {
            return !HasAnyTag(tagPaths);
        }

        /// <summary>
        /// 获取所有显式标签
        /// </summary>
        public List<GameplayTag> GetAllTags()
        {
            lock (_lock)
            {
                return _explicitTags.Values.ToList();
            }
        }

        /// <summary>
        /// 获取所有标签路径
        /// </summary>
        public List<string> GetAllTagPaths()
        {
            lock (_lock)
            {
                return _explicitTags.Keys.ToList();
            }
        }

        /// <summary>
        /// 获取所有标签 包含隐式祖先标签
        /// </summary>
        public List<GameplayTag> GetAllTagsIncludingAncestors()
        {
            var result = new HashSet<string>();
            List<GameplayTag> snapshot;

            lock (_lock)
            {
                snapshot = _explicitTags.Values.ToList();
            }

            foreach (var tag in snapshot)
            {
                result.Add(tag.FullPath);

                // 添加所有祖先
                if (tag.Node != null)
                {
                    var ancestors = tag.Node.GetAllAncestors();
                    foreach (var a in ancestors)
                    {
                        result.Add(a.FullPath);
                    }
                }
            }

            return result.Select(p => _tree.Find(p)).Where(t => t.IsValid).ToList();
        }

        /// <summary>
        /// 获取匹配给定标签的所有标签
        /// </summary>
        public List<GameplayTag> GetMatchingTags(string tagPath)
        {
            var result = new List<GameplayTag>();
            List<GameplayTag> snapshot;

            lock (_lock)
            {
                snapshot = _explicitTags.Values.ToList();
            }

            foreach (var existing in snapshot)
            {
                if (_tree.Matches(tagPath, existing.FullPath))
                {
                    result.Add(existing);
                }
            }

            return result;
        }

        /// <summary>
        /// 清空所有标签
        /// </summary>
        public void Clear()
        {
            List<GameplayTag> snapshot;
            lock (_lock)
            {
                snapshot = _explicitTags.Values.ToList();
                _explicitTags.Clear();
                _refCounts.Clear();
            }

            foreach (var tag in snapshot)
            {
                TagRemoved?.Invoke(this, tag);
            }
        }

        /// <summary>
        /// 获取标签引用计数
        /// </summary>
        public int GetRefCount(string tagPath)
        {
            if (string.IsNullOrEmpty(tagPath)) return 0;
            lock (_lock)
            {
                return _refCounts.GetValueOrDefault(tagPath);
            }
        }

        /// <summary>
        /// 强制移除标签 不管引用计数
        /// </summary>
        public void ForceRemoveTag(string tagPath)
        {
            if (string.IsNullOrEmpty(tagPath)) return;

            GameplayTag? removed = null;
            lock (_lock)
            {
                if (_explicitTags.TryGetValue(tagPath, out var tag))
                {
                    removed = tag;
                    _explicitTags.Remove(tagPath);
                    _refCounts.Remove(tagPath);
                }
            }

            if (removed.HasValue)
            {
                TagRemoved?.Invoke(this, removed.Value);
            }
        }

        /// <summary>
        /// 转换为字符串
        /// </summary>
        public override string ToString()
        {
            lock (_lock)
            {
                return $"[{string.Join(", ", _explicitTags.Keys)}]";
            }
        }
    }

    #endregion

    #region 标签查询过滤器

    /// <summary>
    /// 标签查询过滤器 描述一组标签的匹配规则
    /// 用于能力激活条件 效果应用条件等
    /// </summary>
    public class GameplayTagQuery
    {
        // 必须有的标签 全部满足
        public List<string> AllOfTags { get; set; } = new();

        // 任意一个满足即可
        public List<string> AnyOfTags { get; set; } = new();

        // 不能有的标签
        public List<string> NoneOfTags { get; set; } = new();

        /// <summary>
        /// 检查容器是否满足查询
        /// </summary>
        public bool Matches(GameplayTagContainerFull container)
        {
            if (container == null) return false;

            // AllOf 必须全部满足
            if (AllOfTags.Count > 0 && !container.HasAllTags(AllOfTags))
                return false;

            // AnyOf 至少一个满足
            if (AnyOfTags.Count > 0 && !container.HasAnyTag(AnyOfTags))
                return false;

            // NoneOf 不能有任何一个
            if (NoneOfTags.Count > 0 && container.HasAnyTag(NoneOfTags))
                return false;

            return true;
        }

        /// <summary>
        /// 创建查询
        /// </summary>
        public static GameplayTagQuery Create(
            IEnumerable<string>? allOf = null,
            IEnumerable<string>? anyOf = null,
            IEnumerable<string>? noneOf = null)
        {
            var query = new GameplayTagQuery();
            if (allOf != null) query.AllOfTags.AddRange(allOf);
            if (anyOf != null) query.AnyOfTags.AddRange(anyOf);
            if (noneOf != null) query.NoneOfTags.AddRange(noneOf);
            return query;
        }

        public override string ToString()
        {
            var sb = new StringBuilder();
            sb.Append("Query(");
            if (AllOfTags.Count > 0)
                sb.Append($"AllOf:[{string.Join(",", AllOfTags)}] ");
            if (AnyOfTags.Count > 0)
                sb.Append($"AnyOf:[{string.Join(",", AnyOfTags)}] ");
            if (NoneOfTags.Count > 0)
                sb.Append($"NoneOf:[{string.Join(",", NoneOfTags)}]");
            sb.Append(")");
            return sb.ToString();
        }
    }

    /// <summary>
    /// 标签查询构建器 流式API
    /// </summary>
    public class GameplayTagQueryBuilder
    {
        private readonly GameplayTagQuery _query = new();

        public GameplayTagQueryBuilder AllOf(params string[] tags)
        {
            _query.AllOfTags.AddRange(tags);
            return this;
        }

        public GameplayTagQueryBuilder AnyOf(params string[] tags)
        {
            _query.AnyOfTags.AddRange(tags);
            return this;
        }

        public GameplayTagQueryBuilder NoneOf(params string[] tags)
        {
            _query.NoneOfTags.AddRange(tags);
            return this;
        }

        public GameplayTagQuery Build()
        {
            return _query;
        }
    }

    #endregion

    #region 标签关系 额外的标签关系

    /// <summary>
    /// 标签关系类型
    /// </summary>
    public enum TagRelationType : byte
    {
        // 互斥 有A就不能有B
        MutuallyExclusive = 0,
        // 隐含 有A就自动有B
        Implies = 1,
        // 阻止 有A时阻止B添加
        Blocks = 2,
    }

    /// <summary>
    /// 标签关系 两个标签之间的额外关系
    /// </summary>
    public class GameplayTagRelation
    {
        public TagRelationType RelationType { get; set; }
        public string TagA { get; set; } = string.Empty;
        public string TagB { get; set; } = string.Empty;
    }

    /// <summary>
    /// 标签关系管理器
    /// </summary>
    public class GameplayTagRelationManager
    {
        private readonly List<GameplayTagRelation> _relations = new();
        private readonly object _lock = new();

        /// <summary>
        /// 添加关系
        /// </summary>
        public void AddRelation(TagRelationType type, string tagA, string tagB)
        {
            if (string.IsNullOrEmpty(tagA) || string.IsNullOrEmpty(tagB)) return;

            lock (_lock)
            {
                _relations.Add(new GameplayTagRelation
                {
                    RelationType = type,
                    TagA = tagA,
                    TagB = tagB,
                });
            }
        }

        /// <summary>
        /// 设置互斥关系
        /// </summary>
        public void SetMutuallyExclusive(string tagA, string tagB)
        {
            AddRelation(TagRelationType.MutuallyExclusive, tagA, tagB);
            AddRelation(TagRelationType.MutuallyExclusive, tagB, tagA);
        }

        /// <summary>
        /// 检查是否可以添加标签 考虑关系
        /// </summary>
        public bool CanAddTag(GameplayTagContainerFull container, string tagPath)
        {
            if (string.IsNullOrEmpty(tagPath)) return false;

            lock (_lock)
            {
                foreach (var rel in _relations)
                {
                    if (rel.TagA == tagPath)
                    {
                        switch (rel.RelationType)
                        {
                            case TagRelationType.MutuallyExclusive:
                                if (container.HasTag(rel.TagB))
                                    return false;
                                break;

                            case TagRelationType.Blocks:
                                if (container.HasTag(rel.TagB))
                                    return false;
                                break;
                        }
                    }
                }
            }

            return true;
        }

        /// <summary>
        /// 获取标签添加后应该隐含添加的标签
        /// </summary>
        public List<string> GetImpliedTags(string tagPath)
        {
            var result = new List<string>();

            lock (_lock)
            {
                foreach (var rel in _relations)
                {
                    if (rel.TagA == tagPath && rel.RelationType == TagRelationType.Implies)
                    {
                        result.Add(rel.TagB);
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// 清空所有关系
        /// </summary>
        public void Clear()
        {
            lock (_lock)
            {
                _relations.Clear();
            }
        }
    }

    #endregion

    #region 标签目录 预定义标签集

    /// <summary>
    /// 标签目录 预定义常用标签
    /// 方便快速引用 避免拼写错误
    /// </summary>
    public static class GameplayTagCatalog
    {
        // 状态类标签
        public const string State = "State";
        public const string State_Stunned = "State.Stunned";
        public const string State_Silenced = "State.Silenced";
        public const string State_Rooted = "State.Rooted";
        public const string State_Slowed = "State.Slowed";
        public const string State_Hasted = "State.Hasted";
        public const string State_Poisoned = "State.Poisoned";
        public const string State_Burning = "State.Burning";
        public const string State_Frozen = "State.Frozen";
        public const string State_Shielded = "State.Shielded";
        public const string State_Stealthed = "State.Stealthed";
        public const string State_Dead = "State.Dead";
        public const string State_Stunned_Hard = "State.Stunned.Hard";
        public const string State_Immune = "State.Immune";
        public const string State_Immune_Stun = "State.Immune.Stun";
        public const string State_Immune_Poison = "State.Immune.Poison";
        public const string State_Invulnerable = "State.Invulnerable";

        // 角色类标签
        public const string Character = "Character";
        public const string Character_Hero = "Character.Hero";
        public const string Character_Enemy = "Character.Enemy";
        public const string Character_NPC = "Character.NPC";
        public const string Character_Hero_Warrior = "Character.Hero.Warrior";
        public const string Character_Hero_Mage = "Character.Hero.Mage";
        public const string Character_Hero_Rogue = "Character.Hero.Rogue";
        public const string Character_Enemy_Boss = "Character.Enemy.Boss";
        public const string Character_Enemy_Minion = "Character.Enemy.Minion";

        // 伤害类标签
        public const string Damage = "Damage";
        public const string Damage_Physical = "Damage.Physical";
        public const string Damage_Magical = "Damage.Magical";
        public const string Damage_True = "Damage.True";
        public const string Damage_Fire = "Damage.Fire";
        public const string Damage_Ice = "Damage.Ice";
        public const string Damage_Lightning = "Damage.Lightning";
        public const string Damage_Poison = "Damage.Poison";

        // 能力类标签
        public const string Ability = "Ability";
        public const string Ability_Attack = "Ability.Attack";
        public const string Ability_Spell = "Ability.Spell";
        public const string Ability_Heal = "Ability.Heal";
        public const string Ability_Buff = "Ability.Buff";
        public const string Ability_Debuff = "Ability.Debuff";
        public const string Ability_Movement = "Ability.Movement";
        public const string Ability_Ultimate = "Ability.Ultimate";

        // 装备类标签
        public const string Item = "Item";
        public const string Item_Weapon = "Item.Weapon";
        public const string Item_Armor = "Item.Armor";
        public const string Item_Consumable = "Item.Consumable";
        public const string Item_Material = "Item.Material";
        public const string Item_Quest = "Item.Quest";

        // 队伍类标签
        public const string Team = "Team";
        public const string Team_Player = "Team.Player";
        public const string Team_Enemy = "Team.Enemy";
        public const string Team_Neutral = "Team.Neutral";

        // 所有预定义标签
        public static readonly string[] AllTags = new[]
        {
            State, State_Stunned, State_Silenced, State_Rooted, State_Slowed,
            State_Hasted, State_Poisoned, State_Burning, State_Frozen,
            State_Shielded, State_Stealthed, State_Dead, State_Stunned_Hard,
            State_Immune, State_Immune_Stun, State_Immune_Poison, State_Invulnerable,
            Character, Character_Hero, Character_Enemy, Character_NPC,
            Character_Hero_Warrior, Character_Hero_Mage, Character_Hero_Rogue,
            Character_Enemy_Boss, Character_Enemy_Minion,
            Damage, Damage_Physical, Damage_Magical, Damage_True,
            Damage_Fire, Damage_Ice, Damage_Lightning, Damage_Poison,
            Ability, Ability_Attack, Ability_Spell, Ability_Heal,
            Ability_Buff, Ability_Debuff, Ability_Movement, Ability_Ultimate,
            Item, Item_Weapon, Item_Armor, Item_Consumable, Item_Material, Item_Quest,
            Team, Team_Player, Team_Enemy, Team_Neutral,
        };

        /// <summary>
        /// 把所有预定义标签注册到树
        /// </summary>
        public static void RegisterAll(GameplayTagTree tree)
        {
            if (tree == null) return;
            tree.RegisterRange(AllTags);
        }
    }

    #endregion

    #region 标签系统管理器

    /// <summary>
    /// 标签系统管理器 全局入口
    /// 管理标签树和关系
    /// </summary>
    public class GameplayTagSystem
    {
        // 单例
        private static GameplayTagSystem? _instance;
        private static readonly object _instanceLock = new();

        public static GameplayTagSystem Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_instanceLock)
                    {
                        _instance ??= new GameplayTagSystem();
                    }
                }
                return _instance;
            }
        }

        // 标签树
        public GameplayTagTree Tree { get; } = new GameplayTagTree();

        // 关系管理器
        public GameplayTagRelationManager Relations { get; } = new();

        // 所有创建的容器 跟踪
        private readonly List<GameplayTagContainerFull> _containers = new();
        private readonly object _containersLock = new();

        private GameplayTagSystem() { }

        /// <summary>
        /// 创建标签容器
        /// </summary>
        public GameplayTagContainerFull CreateContainer()
        {
            var container = new GameplayTagContainerFull(Tree);

            lock (_containersLock)
            {
                _containers.Add(container);
            }

            return container;
        }

        /// <summary>
        /// 销毁容器
        /// </summary>
        public void DestroyContainer(GameplayTagContainerFull container)
        {
            if (container == null) return;

            lock (_containersLock)
            {
                _containers.Remove(container);
            }

            container.Clear();
        }

        /// <summary>
        /// 获取所有容器
        /// </summary>
        public List<GameplayTagContainerFull> GetAllContainers()
        {
            lock (_containersLock)
            {
                return _containers.ToList();
            }
        }

        /// <summary>
        /// 注册标签
        /// </summary>
        public GameplayTag RegisterTag(string tagPath)
        {
            return Tree.Register(tagPath);
        }

        /// <summary>
        /// 批量注册标签
        /// </summary>
        public int RegisterTags(IEnumerable<string> tagPaths)
        {
            return Tree.RegisterRange(tagPaths);
        }

        /// <summary>
        /// 查找标签
        /// </summary>
        public GameplayTag FindTag(string tagPath)
        {
            return Tree.Find(tagPath);
        }

        /// <summary>
        /// 设置互斥标签
        /// </summary>
        public void SetMutuallyExclusive(string tagA, string tagB)
        {
            Relations.SetMutuallyExclusive(tagA, tagB);
        }

        /// <summary>
        /// 初始化 注册预定义标签
        /// </summary>
        public void Initialize()
        {
            GameplayTagCatalog.RegisterAll(Tree);

            // 设置一些默认的互斥关系
            Relations.SetMutuallyExclusive(State_Stunned, State_Immune_Stun);
            Relations.SetMutuallyExclusive(State_Poisoned, State_Immune_Poison);
            Relations.SetMutuallyExclusive(Character_Hero, Character_Enemy);
            Relations.SetMutuallyExclusive(State_Dead, State_Invulnerable);
        }

        /// <summary>
        /// 关闭系统
        /// </summary>
        public void Shutdown()
        {
            lock (_containersLock)
            {
                foreach (var c in _containers)
                {
                    c.Clear();
                }
                _containers.Clear();
            }

            Tree.Clear();
            Relations.Clear();
        }

        /// <summary>
        /// 导出标签树 用于调试
        /// </summary>
        public string DumpTree()
        {
            return Tree.ExportTree();
        }
    }

    #endregion

    #region 标签事件系统

    /// <summary>
    /// 标签变化事件参数
    /// </summary>
    public class TagChangedEventArgs : EventArgs
    {
        public GameplayTagContainerFull Container { get; }
        public GameplayTag Tag { get; }
        public bool WasAdded { get; }

        public TagChangedEventArgs(GameplayTagContainerFull container, GameplayTag tag, bool wasAdded)
        {
            Container = container;
            Tag = tag;
            WasAdded = wasAdded;
        }
    }

    /// <summary>
    /// 标签事件总线 全局标签事件分发
    /// </summary>
    public class TagEventBus
    {
        private readonly Dictionary<string, List<EventHandler<TagChangedEventArgs>>> _listeners = new();
        private readonly object _lock = new();

        /// <summary>
        /// 监听标签变化
        /// </summary>
        public void Subscribe(string tagPath, EventHandler<TagChangedEventArgs> handler)
        {
            if (string.IsNullOrEmpty(tagPath) || handler == null) return;

            lock (_lock)
            {
                if (!_listeners.TryGetValue(tagPath, out var list))
                {
                    list = new List<EventHandler<TagChangedEventArgs>>();
                    _listeners[tagPath] = list;
                }
                list.Add(handler);
            }
        }

        /// <summary>
        /// 取消监听
        /// </summary>
        public void Unsubscribe(string tagPath, EventHandler<TagChangedEventArgs> handler)
        {
            if (string.IsNullOrEmpty(tagPath) || handler == null) return;

            lock (_lock)
            {
                if (_listeners.TryGetValue(tagPath, out var list))
                {
                    list.Remove(handler);
                    if (list.Count == 0)
                        _listeners.Remove(tagPath);
                }
            }
        }

        /// <summary>
        /// 派发事件
        /// </summary>
        public void Publish(TagChangedEventArgs args)
        {
            if (args == null) return;

            List<EventHandler<TagChangedEventArgs>>? handlers;
            lock (_lock)
            {
                if (!_listeners.TryGetValue(args.Tag.FullPath, out var list))
                    return;
                handlers = new List<EventHandler<TagChangedEventArgs>>(list);
            }

            foreach (var h in handlers)
            {
                try
                {
                    h(this, args);
                }
                catch
                {
                    // 吞掉异常
                }
            }
        }

        /// <summary>
        /// 清空所有监听
        /// </summary>
        public void Clear()
        {
            lock (_lock)
            {
                _listeners.Clear();
            }
        }
    }

    #endregion

    #region 标签目录扩展 动态标签

    /// <summary>
    /// 动态标签生成器 运行时动态生成标签
    /// 比如根据物品ID生成 Item.Weapon.12345
    /// </summary>
    public class DynamicTagGenerator
    {
        private readonly GameplayTagTree _tree;
        private readonly object _lock = new();

        public DynamicTagGenerator(GameplayTagTree tree)
        {
            _tree = tree ?? throw new ArgumentNullException(nameof(tree));
        }

        /// <summary>
        /// 生成物品标签
        /// </summary>
        public GameplayTag GenerateItemTag(string category, int itemId)
        {
            return _tree.Register($"Item.{category}.{itemId}");
        }

        /// <summary>
        /// 生成能力标签
        /// </summary>
        public GameplayTag GenerateAbilityTag(string category, int abilityId)
        {
            return _tree.Register($"Ability.{category}.{abilityId}");
        }

        /// <summary>
        /// 生成角色标签
        /// </summary>
        public GameplayTag GenerateCharacterTag(string type, int characterId)
        {
            return _tree.Register($"Character.{type}.{characterId}");
        }

        /// <summary>
        /// 生成自定义标签
        /// </summary>
        public GameplayTag GenerateCustomTag(params string[] parts)
        {
            if (parts == null || parts.Length == 0)
                return GameplayTag.None;

            return _tree.Register(string.Join(".", parts));
        }
    }

    #endregion

    #region 标签统计和分析

    /// <summary>
    /// 标签统计信息
    /// </summary>
    public class TagStatistics
    {
        public int TotalTags { get; set; }
        public int TotalContainers { get; set; }
        public Dictionary<string, int> TagUsageCount { get; set; } = new();
        public List<string> MostUsedTags { get; set; } = new();
    }

    /// <summary>
    /// 标签分析器
    /// </summary>
    public class TagAnalyzer
    {
        private readonly GameplayTagSystem _system;

        public TagAnalyzer(GameplayTagSystem system)
        {
            _system = system ?? throw new ArgumentNullException(nameof(system));
        }

        /// <summary>
        /// 收集统计信息
        /// </summary>
        public TagStatistics CollectStatistics()
        {
            var stats = new TagStatistics
            {
                TotalTags = _system.Tree.Count,
            };

            var usage = new Dictionary<string, int>();

            foreach (var container in _system.GetAllContainers())
            {
                stats.TotalContainers++;
                foreach (var path in container.GetAllTagPaths())
                {
                    usage[path] = usage.GetValueOrDefault(path) + 1;
                }
            }

            stats.TagUsageCount = usage;
            stats.MostUsedTags = usage
                .OrderByDescending(kv => kv.Value)
                .Take(10)
                .Select(kv => $"{kv.Key}({kv.Value})")
                .ToList();

            return stats;
        }

        /// <summary>
        /// 导出统计报告
        /// </summary>
        public string ExportReport()
        {
            var stats = CollectStatistics();
            var sb = new StringBuilder();

            sb.AppendLine("=== Tag System Statistics ===");
            sb.AppendLine($"Total Tags: {stats.TotalTags}");
            sb.AppendLine($"Total Containers: {stats.TotalContainers}");
            sb.AppendLine();

            sb.AppendLine("--- Most Used Tags ---");
            foreach (var tag in stats.MostUsedTags)
            {
                sb.AppendLine($"  {tag}");
            }

            sb.AppendLine();
            sb.AppendLine("--- Tag Tree ---");
            sb.AppendLine(_system.DumpTree());

            return sb.ToString();
        }
    }

    #endregion

    #region 标签系统初始化辅助

    /// <summary>
    /// 标签系统启动器
    /// </summary>
    public static class TagSystemBootstrap
    {
        public static GameplayTagSystem Initialize()
        {
            var system = GameplayTagSystem.Instance;
            system.Initialize();
            return system;
        }

        public static string RunSmokeTest()
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== Tag System Smoke Test ===");

            var system = Initialize();
            sb.AppendLine($"System initialized with {system.Tree.Count} tags");

            // 创建容器
            var container = system.CreateContainer();
            sb.AppendLine($"Container created");

            // 添加标签
            container.AddTag(GameplayTagCatalog.State_Stunned);
            container.AddTag(GameplayTagCatalog.Character_Hero_Warrior);
            container.AddTag(GameplayTagCatalog.Damage_Physical);

            sb.AppendLine($"Container has {container.Count} tags");
            sb.AppendLine($"Tags: {container}");

            // 测试层级匹配
            bool hasState = container.HasTag(GameplayTagCatalog.State);
            sb.AppendLine($"HasTag(State) [exact]: {hasState}");

            bool matchesState = container.HasAnyTag(new[] { GameplayTagCatalog.State });
            sb.AppendLine($"HasAnyTag(State) [hierarchical]: {matchesState}");

            bool matchesCharacter = container.HasAnyTag(new[] { GameplayTagCatalog.Character });
            sb.AppendLine($"HasAnyTag(Character) [hierarchical]: {matchesCharacter}");

            // 测试查询
            var query = new GameplayTagQueryBuilder()
                .AllOf(GameplayTagCatalog.State_Stunned, GameplayTagCatalog.Character_Hero)
                .NoneOf(GameplayTagCatalog.State_Dead)
                .Build();

            bool matches = query.Matches(container);
            sb.AppendLine($"Query matches: {matches}");

            // 移除标签
            container.RemoveTag(GameplayTagCatalog.State_Stunned);
            sb.AppendLine($"After removing Stunned: {container}");

            sb.AppendLine();
            sb.AppendLine("--- Tag Tree Dump ---");
            sb.AppendLine(system.DumpTree());

            system.Shutdown();
            sb.AppendLine("Test complete");
            return sb.ToString();
        }
    }

    #endregion

} // namespace ShanyuEngine.Gameplay
