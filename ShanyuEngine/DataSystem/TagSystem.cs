using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace ShanyuEngine.DataSystem
{
    // ====================================================================
    // 标签/标记系统（TagSystem）
    // ====================================================================
    // 类似 UE GameplayTags，提供层级标签、标签集合、标签查询和事件。
    // 标签格式：Parent.Child.Grandchild（如 Weapon.Sword.Fire）
    // ====================================================================

    /// <summary>
    /// GameplayTag：层级标签。每个标签可以有父标签。
    /// 例如 "Weapon.Sword.Fire" 的父链为：
    ///   Weapon.Sword.Fire → Weapon.Sword → Weapon → (根)
    /// 标签使用点号分隔层级。
    /// </summary>
    public sealed class GameplayTag : IEquatable<GameplayTag>, IComparable<GameplayTag>
    {
        private static readonly Dictionary<string, GameplayTag> _tagCache =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>标签的完整字符串（如 "Weapon.Sword.Fire"）</summary>
        public string TagName { get; }

        /// <summary>标签的简短名称（最后一段，如 "Fire"）</summary>
        public string ShortName { get; }

        /// <summary>父标签（null 表示根标签）</summary>
        public GameplayTag? ParentTag { get; private set; }

        /// <summary>标签深度（层数，根标签为 1）</summary>
        public int Depth { get; private set; }

        /// <summary>标签请求名称（用于编辑器显示的简短路径）</summary>
        public string RequestedName { get; set; } = "";

        /// <summary>标签注释</summary>
        public string Comment { get; set; } = "";

        private GameplayTag(string tagName)
        {
            TagName = tagName ?? throw new ArgumentNullException(nameof(tagName));

            var parts = tagName.Split('.');
            ShortName = parts[parts.Length - 1];
            Depth = parts.Length;
        }

        /// <summary>
        /// 获取或创建标签实例。使用缓存确保相同字符串返回相同对象。
        /// </summary>
        public static GameplayTag Get(string tagName)
        {
            if (string.IsNullOrEmpty(tagName))
                throw new ArgumentException("Tag name cannot be empty.", nameof(tagName));

            if (_tagCache.TryGetValue(tagName, out var cached))
                return cached;

            var tag = new GameplayTag(tagName);

            // 建立父子关系
            var dotIdx = tagName.LastIndexOf('.');
            if (dotIdx > 0)
            {
                var parentName = tagName.Substring(0, dotIdx);
                tag.ParentTag = Get(parentName);
            }

            _tagCache[tagName] = tag;
            return tag;
        }

        /// <summary>尝试获取标签（不存在返回 false）</summary>
        public static bool TryGet(string tagName, out GameplayTag? tag)
        {
            tag = _tagCache.TryGetValue(tagName, out var t) ? t : null;
            return tag != null;
        }

        /// <summary>获取所有已注册的标签</summary>
        public static IReadOnlyList<GameplayTag> GetAllTags() => _tagCache.Values.ToList();

        /// <summary>判断此标签是否匹配或包含指定标签</summary>
        public bool Matches(GameplayTag other)
        {
            if (other == null) return false;
            if (string.Equals(TagName, other.TagName, StringComparison.OrdinalIgnoreCase))
                return true;
            // 检查是否是对方的祖先
            return other.TagName.StartsWith(TagName + ".", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>判断此标签是否是指定标签的子标签（或自身）</summary>
        public bool IsChildOf(GameplayTag parent)
        {
            if (parent == null) return false;
            if (string.Equals(TagName, parent.TagName, StringComparison.OrdinalIgnoreCase))
                return true;
            // 向上遍历父链
            var current = ParentTag;
            while (current != null)
            {
                if (string.Equals(current.TagName, parent.TagName, StringComparison.OrdinalIgnoreCase))
                    return true;
                current = current.ParentTag;
            }
            return false;
        }

        /// <summary>获取从根到此标签的完整路径</summary>
        public string[] GetTagHierarchy()
        {
            var hierarchy = new List<string>();
            var current = this;
            while (current != null)
            {
                hierarchy.Add(current.TagName);
                current = current.ParentTag;
            }
            hierarchy.Reverse();
            return hierarchy.ToArray();
        }

        public bool Equals(GameplayTag? other) =>
            other != null && string.Equals(TagName, other.TagName, StringComparison.OrdinalIgnoreCase);

        public override bool Equals(object? obj) => Equals(obj as GameplayTag);

        public override int GetHashCode() => StringComparer.OrdinalIgnoreCase.GetHashCode(TagName);

        public override string ToString() => TagName;

        public int CompareTo(GameplayTag? other) =>
            string.Compare(TagName, other?.TagName ?? "", StringComparison.OrdinalIgnoreCase);

        public static bool operator ==(GameplayTag? a, GameplayTag? b) =>
            a?.Equals(b) ?? b is null;
        public static bool operator !=(GameplayTag? a, GameplayTag? b) => !(a == b);
    }

    // ====================================================================
    // GameplayTagContainer：标签集合
    // ====================================================================

    /// <summary>
    /// 标签集合。管理一组 GameplayTag，支持添加/移除/包含/过滤/匹配/
    /// 联合/交集/差集操作。
    /// </summary>
    public sealed class GameplayTagContainer : IEquatable<GameplayTagContainer>
    {
        private readonly HashSet<GameplayTag> _tags = new();

        /// <summary>标签数量</summary>
        public int Count => _tags.Count;

        /// <summary>是否为空</summary>
        public bool IsEmpty => _tags.Count == 0;

        /// <summary>标签添加事件</summary>
        public event Action<GameplayTag>? OnTagAdded;
        /// <summary>标签移除事件</summary>
        public event Action<GameplayTag>? OnTagRemoved;

        /// <summary>添加标签</summary>
        public bool Add(GameplayTag tag)
        {
            if (tag == null) throw new ArgumentNullException(nameof(tag));
            if (_tags.Add(tag))
            {
                OnTagAdded?.Invoke(tag);
                return true;
            }
            return false;
        }

        /// <summary>通过字符串添加标签</summary>
        public bool Add(string tagName) => Add(GameplayTag.Get(tagName));

        /// <summary>添加多个标签</summary>
        public void AddRange(params GameplayTag[] tags)
        {
            foreach (var tag in tags) Add(tag);
        }

        /// <summary>添加字符串标签数组</summary>
        public void AddRange(params string[] tagNames)
        {
            foreach (var name in tagNames) Add(name);
        }

        /// <summary>添加另一个容器的所有标签（联合）</summary>
        public void AddTags(GameplayTagContainer other)
        {
            if (other == null) return;
            foreach (var tag in other._tags) Add(tag);
        }

        /// <summary>移除标签</summary>
        public bool Remove(GameplayTag tag)
        {
            if (_tags.Remove(tag))
            {
                OnTagRemoved?.Invoke(tag);
                return true;
            }
            return false;
        }

        /// <summary>通过字符串移除标签</summary>
        public bool Remove(string tagName)
        {
            if (GameplayTag.TryGet(tagName, out var tag) && tag != null)
                return Remove(tag);
            return false;
        }

        /// <summary>移除另一个容器的所有标签（差集）</summary>
        public void RemoveTags(GameplayTagContainer other)
        {
            if (other == null) return;
            foreach (var tag in other._tags) Remove(tag);
        }

        /// <summary>判断是否包含指定标签（精确匹配）</summary>
        public bool Contains(GameplayTag tag) => _tags.Contains(tag);

        /// <summary>判断是否包含指定标签（字符串匹配）</summary>
        public bool Contains(string tagName)
        {
            return _tags.Any(t => string.Equals(t.TagName, tagName, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// 判断是否包含指定标签或其子标签。
        /// 例如：容器有 "Weapon.Sword"，查询 "Weapon" 返回 true。
        /// </summary>
        public bool ContainsTagOrChild(GameplayTag tag)
        {
            return _tags.Any(t => t.Matches(tag) || tag.Matches(t));
        }

        /// <summary>判断此容器是否匹配指定容器（包含对方的任何标签或子标签）</summary>
        public bool Matches(GameplayTagContainer other)
        {
            if (other == null || other.IsEmpty) return false;
            return other._tags.Any(t => ContainsTagOrChild(t));
        }

        /// <summary>判断此容器是否完全包含指定容器的所有标签</summary>
        public bool ContainsAll(GameplayTagContainer other)
        {
            if (other == null) return true;
            return other._tags.All(Contains);
        }

        /// <summary>判断此容器是否有任何标签与指定容器重叠</summary>
        public bool HasAny(GameplayTagContainer other)
        {
            if (other == null || other.IsEmpty) return false;
            return _tags.Overlaps(other._tags);
        }

        /// <summary>判断此容器是否有任何标签匹配指定标签（包含子标签）</summary>
        public bool HasAnyMatching(GameplayTagContainer other)
        {
            if (other == null || other.IsEmpty) return false;
            return other._tags.Any(ContainsTagOrChild);
        }

        /// <summary>过滤标签（返回新容器，只包含满足条件的标签）</summary>
        public GameplayTagContainer Filter(Func<GameplayTag, bool> predicate)
        {
            var result = new GameplayTagContainer();
            foreach (var tag in _tags)
                if (predicate(tag)) result.Add(tag);
            return result;
        }

        /// <summary>获取所有标签</summary>
        public IReadOnlyList<GameplayTag> GetAllTags() => _tags.ToList();

        /// <summary>获取所有标签名</summary>
        public List<string> GetAllTagNames() => _tags.Select(t => t.TagName).ToList();

        /// <summary>清空所有标签</summary>
        public void Clear()
        {
            var tags = _tags.ToList();
            _tags.Clear();
            foreach (var tag in tags) OnTagRemoved?.Invoke(tag);
        }

        /// <summary>联合（返回新容器 = this + other）</summary>
        public GameplayTagContainer Union(GameplayTagContainer other)
        {
            var result = new GameplayTagContainer();
            result.AddTags(this);
            result.AddTags(other);
            return result;
        }

        /// <summary>交集（返回新容器 = this ∩ other）</summary>
        public GameplayTagContainer Intersect(GameplayTagContainer other)
        {
            var result = new GameplayTagContainer();
            if (other == null) return result;
            foreach (var tag in _tags)
                if (other._tags.Contains(tag)) result.Add(tag);
            return result;
        }

        /// <summary>差集（返回新容器 = this - other）</summary>
        public GameplayTagContainer Except(GameplayTagContainer other)
        {
            var result = new GameplayTagContainer();
            if (other == null) { result.AddTags(this); return result; }
            foreach (var tag in _tags)
                if (!other._tags.Contains(tag)) result.Add(tag);
            return result;
        }

        public bool Equals(GameplayTagContainer? other)
        {
            if (other == null) return false;
            if (_tags.Count != other._tags.Count) return false;
            return _tags.SetEquals(other._tags);
        }

        public override bool Equals(object? obj) => Equals(obj as GameplayTagContainer);

        public override int GetHashCode()
        {
            int hash = 0;
            foreach (var tag in _tags) hash ^= tag.GetHashCode();
            return hash;
        }

        public override string ToString() => string.Join(", ", _tags.Select(t => t.TagName));
    }

    // ====================================================================
    // TagQuery：标签查询
    // ====================================================================

    /// <summary>标签查询匹配模式</summary>
    public enum TagQueryMatchMode
    {
        /// <summary>任意匹配（容器中有任何一个标签满足）</summary>
        AnyMatch,
        /// <summary>全部匹配（容器中所有标签都满足）</summary>
        AllMatch,
        /// <summary>无匹配（容器中没有任何标签满足）</summary>
        NoMatch
    }

    /// <summary>
    /// 标签查询。定义一组标签的匹配条件，用于过滤和查询。
    /// </summary>
    public sealed class TagQuery
    {
        /// <summary>匹配模式</summary>
        public TagQueryMatchMode MatchMode { get; set; } = TagQueryMatchMode.AnyMatch;

        /// <summary>查询标签集合</summary>
        private readonly GameplayTagContainer _requiredTags = new();

        /// <summary>排除标签集合</summary>
        private readonly GameplayTagContainer _excludedTags = new();

        /// <summary>添加必需标签</summary>
        public TagQuery Require(GameplayTag tag) { _requiredTags.Add(tag); return this; }
        public TagQuery Require(string tagName) { _requiredTags.Add(tagName); return this; }

        /// <summary>添加排除标签</summary>
        public TagQuery Exclude(GameplayTag tag) { _excludedTags.Add(tag); return this; }
        public TagQuery Exclude(string tagName) { _excludedTags.Add(tagName); return this; }

        /// <summary>判断指定标签容器是否匹配此查询</summary>
        public bool Matches(GameplayTagContainer container)
        {
            if (container == null) return false;

            // 检查排除
            if (_excludedTags.HasAnyMatching(container))
                return false;

            // 检查必需
            return MatchMode switch
            {
                TagQueryMatchMode.AnyMatch => container.HasAnyMatching(_requiredTags),
                TagQueryMatchMode.AllMatch => container.ContainsAll(_requiredTags),
                TagQueryMatchMode.NoMatch => !container.HasAnyMatching(_requiredTags),
                _ => false
            };
        }

        /// <summary>获取必需标签的只读容器</summary>
        public GameplayTagContainer RequiredTags
        {
            get
            {
                var copy = new GameplayTagContainer();
                copy.AddTags(_requiredTags);
                return copy;
            }
        }

        /// <summary>获取排除标签的只读容器</summary>
        public GameplayTagContainer ExcludedTags
        {
            get
            {
                var copy = new GameplayTagContainer();
                copy.AddTags(_excludedTags);
                return copy;
            }
        }
    }

    // ====================================================================
    // GameplayTagManager：全局标签管理器
    // ====================================================================

    /// <summary>
    /// 全局标签管理器。管理标签注册表、标签树、标签查询和建议。
    /// </summary>
    public sealed class GameplayTagManager
    {
        private static GameplayTagManager? _instance;
        public static GameplayTagManager Instance => _instance ??= new GameplayTagManager();

        private readonly Dictionary<string, GameplayTag> _allTags = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<GameplayTag>> _childrenOf = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>标签注册事件</summary>
        public event Action<GameplayTag>? OnTagRegistered;
        /// <summary>标签变更事件</summary>
        public event Action<GameplayTag>? OnTagChanged;

        private GameplayTagManager() { }

        /// <summary>注册一个标签（等同于 GameplayTag.Get）</summary>
        public GameplayTag RegisterTag(string tagName)
        {
            var tag = GameplayTag.Get(tagName);
            if (!_allTags.ContainsKey(tag.TagName))
            {
                _allTags[tag.TagName] = tag;

                // 建立子标签索引
                if (tag.ParentTag != null)
                {
                    if (!_childrenOf.TryGetValue(tag.ParentTag.TagName, out var children))
                    {
                        children = new List<GameplayTag>();
                        _childrenOf[tag.ParentTag.TagName] = children;
                    }
                    children.Add(tag);
                }

                OnTagRegistered?.Invoke(tag);
            }
            return tag;
        }

        /// <summary>批量注册标签</summary>
        public void RegisterTags(params string[] tagNames)
        {
            foreach (var name in tagNames) RegisterTag(name);
        }

        /// <summary>获取标签</summary>
        public GameplayTag? GetTag(string tagName) =>
            _allTags.TryGetValue(tagName, out var tag) ? tag : null;

        /// <summary>判断标签是否存在</summary>
        public bool HasTag(string tagName) => _allTags.ContainsKey(tagName);

        /// <summary>获取子标签列表</summary>
        public List<GameplayTag> GetChildTags(string parentTagName) =>
            _childrenOf.TryGetValue(parentTagName, out var children)
                ? new List<GameplayTag>(children)
                : new List<GameplayTag>();

        /// <summary>获取所有已注册标签</summary>
        public IReadOnlyList<GameplayTag> GetAllTags() => _allTags.Values.ToList();

        /// <summary>按前缀搜索标签（自动补全建议）</summary>
        public List<GameplayTag> SearchTags(string prefix, int maxResults = 20)
        {
            return _allTags.Values
                .Where(t => t.TagName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .OrderBy(t => t.TagName)
                .Take(maxResults)
                .ToList();
        }

        /// <summary>执行标签查询</summary>
        public List<GameplayTagContainer> Query(TagQuery query, IEnumerable<GameplayTagContainer> sources)
        {
            var results = new List<GameplayTagContainer>();
            foreach (var source in sources)
                if (query.Matches(source)) results.Add(source);
            return results;
        }

        /// <summary>清空所有标签</summary>
        public void Clear()
        {
            _allTags.Clear();
            _childrenOf.Clear();
        }
    }
}
