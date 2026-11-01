// YamaEngine - yama游戏引擎
// GameplayTargetingSystem.cs - 目标选择系统
// 目标选择决定了技能打谁 治疗谁 影响谁
// 范围技能要选范围 圆形锥形矩形 单体要选最近 随机
// 这套系统要快 每帧都在跑 大规模战斗时目标很多

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace YamaEngine.Gameplay
{
    #region 目标类型枚举

    /// <summary>
    /// 目标选择类型 怎么选目标
    /// </summary>
    public enum TargetingType : byte
    {
        // 自身 只选自己
        Self = 0,
        // 单体 选一个目标
        Single = 1,
        // 范围圆形
        Circle = 2,
        // 范围球形 3D
        Sphere = 3,
        // 范围矩形 长方形
        Box = 4,
        // 范围锥形 扇形
        Cone = 5,
        // 范围胶囊
        Capsule = 6,
        // 直线
        Line = 7,
        // 多目标 按数量
        Multiple = 8,
        // 所有友方
        AllAllies = 9,
        // 所有敌方
        AllEnemies = 10,
        // 随机
        Random = 11,
        // 自定义脚本
        Custom = 12,
    }

    /// <summary>
    /// 目标筛选方式
    /// </summary>
    public enum TargetFilterType : byte
    {
        // 不过滤
        None = 0,
        // 只选友方
        FriendlyOnly = 1,
        // 只选敌方
        EnemyOnly = 2,
        // 只选友方和自身
        FriendlyAndSelf = 3,
        // 所有人
        All = 4,
        // 自定义
        Custom = 5,
    }

    /// <summary>
    /// 目标排序方式
    /// </summary>
    public enum TargetSortType : byte
    {
        // 不排序
        None = 0,
        // 按距离近到远
        NearestFirst = 1,
        // 按距离远到近
        FarthestFirst = 2,
        // 按生命值低到高
        LowestHealthFirst = 3,
        // 按生命值高到低
        HighestHealthFirst = 4,
        // 随机
        Random = 5,
        // 按角度排序 锥形选目标用
        ByAngle = 6,
    }

    #endregion

    #region 目标接口

    /// <summary>
    /// 可被选为目标的对象接口
    /// 任何想被技能影响的对象都要实现这个接口
    /// </summary>
    public interface ITargetable
    {
        // 唯一ID
        long TargetId { get; }

        // 世界位置
        Vector3 Position { get; }

        // 朝向
        Vector3 Forward { get; }

        // 半径 用于碰撞检测
        float Radius { get; }

        // 是否存活
        bool IsAlive { get; }

        // 队伍ID 0表示中立
        int TeamId { get; }

        // 能力组件 如果有
        GameplayAbilityComponent? AbilityComponent { get; }

        // 标签容器
        GameplayTagContainerFull? Tags { get; }

        // 高度 用于3D检测
        float Height { get; }
    }

    /// <summary>
    /// 目标信息 选择结果
    /// </summary>
    public class TargetInfo
    {
        public ITargetable Target { get; set; }
        public float Distance { get; set; }
        public float Angle { get; set; }
        public Vector3 HitLocation { get; set; }
        public Vector3 HitNormal { get; set; } = Vector3.UnitZ;

        public TargetInfo(ITargetable target)
        {
            Target = target ?? throw new ArgumentNullException(nameof(target));
        }
    }

    /// <summary>
    /// 目标选择结果
    /// </summary>
    public class TargetingResult
    {
        // 来源
        public ITargetable? Source { get; set; }

        // 选中的所有目标
        public List<TargetInfo> Targets { get; } = new();

        // 选择位置
        public Vector3 Origin { get; set; }

        // 选择方向
        public Vector3 Direction { get; set; } = Vector3.UnitZ;

        // 选择类型
        public TargetingType TargetingType { get; set; }

        // 是否成功
        public bool Success => Targets.Count > 0;

        // 目标数量
        public int Count => Targets.Count;

        /// <summary>
        /// 添加目标
        /// </summary>
        public void AddTarget(ITargetable target, Vector3 origin)
        {
            var info = new TargetInfo(target)
            {
                Distance = Vector3.Distance(origin, target.Position),
                HitLocation = target.Position,
            };
            Targets.Add(info);
        }

        /// <summary>
        /// 获取所有目标的能力组件
        /// </summary>
        public List<GameplayAbilityComponent> GetTargetComponents()
        {
            var result = new List<GameplayAbilityComponent>();
            foreach (var info in Targets)
            {
                if (info.Target.AbilityComponent != null)
                    result.Add(info.Target.AbilityComponent);
            }
            return result;
        }

        /// <summary>
        /// 清空
        /// </summary>
        public void Clear()
        {
            Targets.Clear();
        }
    }

    #endregion

    #region 目标选择配置

    /// <summary>
    /// 目标选择配置 描述一次选择的所有参数
    /// </summary>
    public class TargetingConfig
    {
        // 配置名字
        public string Name { get; set; } = string.Empty;

        // 选择类型
        public TargetingType Type { get; set; } = TargetingType.Self;

        // 筛选方式
        public TargetFilterType FilterType { get; set; } = TargetFilterType.All;

        // 排序方式
        public TargetSortType SortType { get; set; } = TargetSortType.NearestFirst;

        // 范围 半径 距离等
        public float Range { get; set; } = 5f;

        // 范围半径 圆形用
        public float Radius { get; set; } = 3f;

        // 高度 球形/胶囊用
        public float Height { get; set; } = 2f;

        // 宽度 矩形用
        public float Width { get; set; } = 2f;

        // 角度 锥形用 度数
        public float Angle { get; set; } = 60f;

        // 最大目标数量 -1表示无限
        public int MaxTargets { get; set; } = -1;

        // 最小距离 太近的不选
        public float MinDistance { get; set; } = 0f;

        // 最大距离 太远的不选
        public float MaxDistance { get; set; } = float.MaxValue;

        // 是否包含自身
        public bool IncludeSelf { get; set; } = false;

        // 是否需要视线
        public bool RequireLineOfSight { get; set; } = false;

        // 视线检测高度偏移
        public float LineOfSightOffset { get; set; } = 1f;

        // 需要的标签 目标必须有
        public List<string> RequiredTags { get; set; } = new();

        // 排除的标签 目标不能有
        public List<string> ExcludedTags { get; set; } = new();

        // 优先选择的标签
        public List<string> PreferredTags { get; set; } = new();

        // 属性条件 属性必须满足
        public Dictionary<string, float> MinAttributeValues { get; set; } = new();

        // 最大属性值
        public Dictionary<string, float> MaxAttributeValues { get; set; } = new();

        // 自定义筛选函数
        public Func<ITargetable, bool>? CustomFilter { get; set; }

        // 自定义排序函数
        public Comparison<ITargetable>? CustomSort { get; set; }

        // 偏移位置 相对于来源
        public Vector3 Offset { get; set; }

        // 是否使用鼠标位置 作为选择中心
        public bool UseCursorLocation { get; set; } = false;

        public override string ToString()
        {
            return $"[TargetingConfig {Name} {Type}]";
        }
    }

    /// <summary>
    /// 目标选择配置仓库
    /// </summary>
    public class TargetingConfigRepository
    {
        private readonly Dictionary<string, TargetingConfig> _configs = new();
        private readonly object _lock = new();

        public bool Register(TargetingConfig config)
        {
            if (config == null || string.IsNullOrEmpty(config.Name)) return false;
            lock (_lock)
            {
                _configs[config.Name] = config;
                return true;
            }
        }

        public TargetingConfig? Get(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            lock (_lock)
            {
                _configs.TryGetValue(name, out var c);
                return c;
            }
        }

        public List<TargetingConfig> GetAll()
        {
            lock (_lock)
            {
                return _configs.Values.ToList();
            }
        }

        public void Clear()
        {
            lock (_lock)
            {
                _configs.Clear();
            }
        }

        public int Count
        {
            get { lock (_lock) { return _configs.Count; } }
        }
    }

    #endregion

    #region 目标选择器

    /// <summary>
    /// 目标选择器 执行目标选择逻辑
    /// </summary>
    public class GameplayTargetSelector
    {
        // 候选目标池 所有可被选为目标的对象
        private readonly List<ITargetable> _candidatePool = new();
        private readonly object _poolLock = new();

        // 空间哈希 加速范围查询 实际项目里对接SpatialHashSystem
        private TargetSpatialHash? _spatialHash;
        private readonly object _spatialHashLock = new();

        // 随机数生成器
        private readonly Random _random = new();

        /// <summary>
        /// 注册候选目标
        /// </summary>
        public void RegisterTarget(ITargetable target)
        {
            if (target == null) return;
            lock (_poolLock)
            {
                if (!_candidatePool.Contains(target))
                {
                    _candidatePool.Add(target);
                }
            }

            lock (_spatialHashLock)
            {
                _spatialHash?.Add(target);
            }
        }

        /// <summary>
        /// 注销候选目标
        /// </summary>
        public void UnregisterTarget(ITargetable target)
        {
            if (target == null) return;
            lock (_poolLock)
            {
                _candidatePool.Remove(target);
            }

            lock (_spatialHashLock)
            {
                _spatialHash?.Remove(target);
            }
        }

        /// <summary>
        /// 更新目标位置 空间哈希用
        /// </summary>
        public void UpdateTargetPosition(ITargetable target)
        {
            if (target == null) return;
            lock (_spatialHashLock)
            {
                _spatialHash?.Update(target);
            }
        }

        /// <summary>
        /// 设置空间哈希 加速查询
        /// </summary>
        public void SetSpatialHash(TargetSpatialHash hash)
        {
            lock (_spatialHashLock)
            {
                _spatialHash = hash;

                // 把现有目标都加进去
                lock (_poolLock)
                {
                    foreach (var t in _candidatePool)
                    {
                        _spatialHash.Add(t);
                    }
                }
            }
        }

        /// <summary>
        /// 执行目标选择
        /// </summary>
        public TargetingResult SelectTargets(TargetingConfig config, ITargetable? source, Vector3? cursorLocation = null)
        {
            var result = new TargetingResult
            {
                Source = source,
                TargetingType = config.Type,
            };

            // 自身类型 直接返回自己
            if (config.Type == TargetingType.Self)
            {
                if (source != null)
                {
                    result.Origin = source.Position;
                    result.AddTarget(source, source.Position);
                }
                return result;
            }

            // 确定选择中心
            Vector3 origin = DetermineOrigin(config, source, cursorLocation);
            result.Origin = origin;

            // 确定方向
            Vector3 direction = DetermineDirection(config, source, origin);
            result.Direction = direction;

            // 获取候选列表
            List<ITargetable> candidates = GetCandidates(config, source, origin);

            // 按类型筛选
            candidates = FilterByShape(config, candidates, origin, direction);

            // 按条件筛选
            candidates = FilterByConditions(config, candidates, source);

            // 排序
            candidates = SortTargets(config, candidates, origin);

            // 限制数量
            if (config.MaxTargets > 0 && candidates.Count > config.MaxTargets)
            {
                candidates = candidates.Take(config.MaxTargets).ToList();
            }

            // 构建结果
            foreach (var target in candidates)
            {
                result.AddTarget(target, origin);
            }

            return result;
        }

        /// <summary>
        /// 确定选择中心位置
        /// </summary>
        private Vector3 DetermineOrigin(TargetingConfig config, ITargetable? source, Vector3? cursorLocation)
        {
            if (config.UseCursorLocation && cursorLocation.HasValue)
            {
                return cursorLocation.Value + config.Offset;
            }

            if (source != null)
            {
                return source.Position + config.Offset;
            }

            return config.Offset;
        }

        /// <summary>
        /// 确定选择方向
        /// </summary>
        private Vector3 DetermineDirection(TargetingConfig config, ITargetable? source, Vector3 origin)
        {
            if (source != null)
            {
                return source.Forward;
            }

            return Vector3.UnitZ;
        }

        /// <summary>
        /// 获取候选目标列表
        /// </summary>
        private List<ITargetable> GetCandidates(TargetingConfig config, ITargetable? source, Vector3 origin)
        {
            // 优先使用空间哈希加速
            if (_spatialHash != null)
            {
                float queryRadius = GetQueryRadius(config);
                lock (_spatialHashLock)
                {
                    return _spatialHash.Query(origin, queryRadius);
                }
            }

            // 退化到遍历
            List<ITargetable> snapshot;
            lock (_poolLock)
            {
                snapshot = _candidatePool.ToList();
            }

            // 快速距离剔除
            float maxDist = GetQueryRadius(config);
            var filtered = new List<ITargetable>();

            foreach (var target in snapshot)
            {
                float dist = Vector3.Distance(origin, target.Position);
                if (dist <= maxDist + target.Radius)
                {
                    filtered.Add(target);
                }
            }

            return filtered;
        }

        /// <summary>
        /// 获取查询半径
        /// </summary>
        private float GetQueryRadius(TargetingConfig config)
        {
            return config.Type switch
            {
                TargetingType.Circle => config.Radius,
                TargetingType.Sphere => config.Radius,
                TargetingType.Box => Math.Max(config.Range, config.Width),
                TargetingType.Cone => config.Range,
                TargetingType.Capsule => Math.Max(config.Range, config.Radius),
                TargetingType.Line => config.Range,
                TargetingType.Single => config.Range,
                TargetingType.Multiple => config.Range,
                _ => config.Range,
            };
        }

        /// <summary>
        /// 按形状筛选
        /// </summary>
        private List<ITargetable> FilterByShape(TargetingConfig config, List<ITargetable> candidates, Vector3 origin, Vector3 direction)
        {
            var result = new List<ITargetable>();

            foreach (var target in candidates)
            {
                if (!target.IsAlive) continue;

                bool inShape = config.Type switch
                {
                    TargetingType.Single => IsInSingle(config, target, origin, direction),
                    TargetingType.Circle => IsInCircle(config, target, origin),
                    TargetingType.Sphere => IsInSphere(config, target, origin),
                    TargetingType.Box => IsInBox(config, target, origin, direction),
                    TargetingType.Cone => IsInCone(config, target, origin, direction),
                    TargetingType.Capsule => IsInCapsule(config, target, origin, direction),
                    TargetingType.Line => IsInLine(config, target, origin, direction),
                    TargetingType.Multiple => IsInRange(config, target, origin),
                    TargetingType.AllAllies => IsInRange(config, target, origin),
                    TargetingType.AllEnemies => IsInRange(config, target, origin),
                    TargetingType.Random => IsInRange(config, target, origin),
                    _ => true,
                };

                if (inShape)
                {
                    result.Add(target);
                }
            }

            return result;
        }

        /// <summary>
        /// 单体选择 最近的
        /// </summary>
        private bool IsInSingle(TargetingConfig config, ITargetable target, Vector3 origin, Vector3 direction)
        {
            float dist = Vector3.Distance(origin, target.Position);
            return dist <= config.Range + target.Radius && dist >= config.MinDistance;
        }

        /// <summary>
        /// 圆形检测 2D
        /// </summary>
        private bool IsInCircle(TargetingConfig config, ITargetable target, Vector3 origin)
        {
            Vector3 targetPos = target.Position;
            float distXZ = Vector2.Distance(
                new Vector2(origin.X, origin.Z),
                new Vector2(targetPos.X, targetPos.Z));

            return distXZ <= config.Radius + target.Radius;
        }

        /// <summary>
        /// 球形检测 3D
        /// </summary>
        private bool IsInSphere(TargetingConfig config, ITargetable target, Vector3 origin)
        {
            float dist = Vector3.Distance(origin, target.Position);
            return dist <= config.Radius + target.Radius;
        }

        /// <summary>
        /// 矩形检测
        /// </summary>
        private bool IsInBox(TargetingConfig config, ITargetable target, Vector3 origin, Vector3 direction)
        {
            Vector3 toTarget = target.Position - origin;

            // 投影到方向上
            float forwardDist = Vector3.Dot(toTarget, direction);

            // 检查是否在范围内
            if (forwardDist < -target.Radius || forwardDist > config.Range + target.Radius)
                return false;

            // 计算侧向距离
            Vector3 right = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, direction));
            float sideDist = Math.Abs(Vector3.Dot(toTarget, right));

            return sideDist <= config.Width * 0.5f + target.Radius;
        }

        /// <summary>
        /// 锥形检测
        /// </summary>
        private bool IsInCone(TargetingConfig config, ITargetable target, Vector3 origin, Vector3 direction)
        {
            Vector3 toTarget = target.Position - origin;
            float dist = toTarget.Length();

            if (dist > config.Range + target.Radius || dist < config.MinDistance)
                return false;

            if (dist < 0.001f) return true;

            // 计算夹角
            Vector3 normalizedToTarget = toTarget / dist;
            float dot = Vector3.Dot(direction, normalizedToTarget);
            float angle = MathF.Acos(Math.Clamp(dot, -1f, 1f)) * (180f / MathF.PI);

            return angle <= config.Angle * 0.5f + target.Radius * (180f / MathF.PI) / Math.Max(dist, 0.1f);
        }

        /// <summary>
        /// 胶囊检测
        /// </summary>
        private bool IsInCapsule(TargetingConfig config, ITargetable target, Vector3 origin, Vector3 direction)
        {
            Vector3 toTarget = target.Position - origin;
            float forwardDist = Vector3.Dot(toTarget, direction);

            if (forwardDist < -target.Radius || forwardDist > config.Range + target.Radius)
                return false;

            // 侧向距离
            Vector3 projected = direction * forwardDist;
            Vector3 lateral = toTarget - projected;
            float lateralDist = lateral.Length();

            return lateralDist <= config.Radius + target.Radius;
        }

        /// <summary>
        /// 直线检测
        /// </summary>
        private bool IsInLine(TargetingConfig config, ITargetable target, Vector3 origin, Vector3 direction)
        {
            Vector3 toTarget = target.Position - origin;
            float dist = toTarget.Length();

            if (dist > config.Range + target.Radius)
                return false;

            if (dist < 0.001f) return true;

            Vector3 normalized = toTarget / dist;
            float dot = Vector3.Dot(direction, normalized);
            float angle = MathF.Acos(Math.Clamp(dot, -1f, 1f)) * (180f / MathF.PI);

            // 直线宽度由Radius决定 转成角度
            float maxAngle = MathF.Atan2(config.Width * 0.5f + target.Radius, Math.Max(dist, 0.1f)) * (180f / MathF.PI);

            return angle <= maxAngle;
        }

        /// <summary>
        /// 范围内
        /// </summary>
        private bool IsInRange(TargetingConfig config, ITargetable target, Vector3 origin)
        {
            float dist = Vector3.Distance(origin, target.Position);
            return dist <= config.Range + target.Radius && dist >= config.MinDistance;
        }

        /// <summary>
        /// 按条件筛选
        /// </summary>
        private List<ITargetable> FilterByConditions(TargetingConfig config, List<ITargetable> candidates, ITargetable? source)
        {
            var result = new List<ITargetable>();

            foreach (var target in candidates)
            {
                // 是否包含自身
                if (source != null && target.TargetId == source.TargetId && !config.IncludeSelf)
                    continue;

                // 队伍筛选
                if (!PassesTeamFilter(config, target, source))
                    continue;

                // 标签筛选
                if (!PassesTagFilter(config, target))
                    continue;

                // 属性筛选
                if (!PassesAttributeFilter(config, target))
                    continue;

                // 自定义筛选
                if (config.CustomFilter != null && !config.CustomFilter(target))
                    continue;

                // 距离筛选
                if (source != null)
                {
                    float dist = Vector3.Distance(source.Position, target.Position);
                    if (dist < config.MinDistance || dist > config.MaxDistance)
                        continue;
                }

                result.Add(target);
            }

            return result;
        }

        /// <summary>
        /// 队伍筛选
        /// </summary>
        private bool PassesTeamFilter(TargetingConfig config, ITargetable target, ITargetable? source)
        {
            if (source == null) return true;

            switch (config.FilterType)
            {
                case TargetFilterType.None:
                    return true;

                case TargetFilterType.FriendlyOnly:
                    return target.TeamId == source.TeamId && target.TeamId != 0;

                case TargetFilterType.EnemyOnly:
                    return target.TeamId != source.TeamId || target.TeamId == 0 || source.TeamId == 0;

                case TargetFilterType.FriendlyAndSelf:
                    return target.TeamId == source.TeamId;

                case TargetFilterType.All:
                    return true;

                default:
                    return true;
            }
        }

        /// <summary>
        /// 标签筛选
        /// </summary>
        private bool PassesTagFilter(TargetingConfig config, ITargetable target)
        {
            if (target.Tags == null) return config.RequiredTags.Count == 0;

            // 检查需要的标签
            foreach (var tag in config.RequiredTags)
            {
                if (!target.Tags.HasTag(tag))
                    return false;
            }

            // 检查排除的标签
            foreach (var tag in config.ExcludedTags)
            {
                if (target.Tags.HasTag(tag))
                    return false;
            }

            return true;
        }

        /// <summary>
        /// 属性筛选
        /// </summary>
        private bool PassesAttributeFilter(TargetingConfig config, ITargetable target)
        {
            if (target.AbilityComponent?.Attributes == null) return true;

            foreach (var kv in config.MinAttributeValues)
            {
                var attr = target.AbilityComponent.Attributes.GetAttribute(kv.Key);
                if (attr == null || attr.CurrentValue < kv.Value)
                    return false;
            }

            foreach (var kv in config.MaxAttributeValues)
            {
                var attr = target.AbilityComponent.Attributes.GetAttribute(kv.Key);
                if (attr == null || attr.CurrentValue > kv.Value)
                    return false;
            }

            return true;
        }

        /// <summary>
        /// 排序目标
        /// </summary>
        private List<ITargetable> SortTargets(TargetingConfig config, List<ITargetable> candidates, Vector3 origin)
        {
            if (candidates.Count <= 1) return candidates;

            switch (config.SortType)
            {
                case TargetSortType.NearestFirst:
                    return candidates.OrderBy(t => Vector3.Distance(origin, t.Position)).ToList();

                case TargetSortType.FarthestFirst:
                    return candidates.OrderByDescending(t => Vector3.Distance(origin, t.Position)).ToList();

                case TargetSortType.LowestHealthFirst:
                    return candidates.OrderBy(t => GetHealth(t)).ToList();

                case TargetSortType.HighestHealthFirst:
                    return candidates.OrderByDescending(t => GetHealth(t)).ToList();

                case TargetSortType.Random:
                    return candidates.OrderBy(t => _random.Next()).ToList();

                case TargetSortType.ByAngle:
                    // 按与方向的夹角排序
                    return candidates.OrderBy(t =>
                    {
                        Vector3 toTarget = t.Position - origin;
                        if (toTarget.LengthSquared() < 0.001f) return 0f;
                        return Vector3.Dot(Vector3.Normalize(toTarget), Vector3.Normalize(origin - t.Position + Vector3.UnitZ));
                    }).ToList();

                default:
                    if (config.CustomSort != null)
                    {
                        var list = new List<ITargetable>(candidates);
                        list.Sort(config.CustomSort);
                        return list;
                    }
                    return candidates;
            }
        }

        /// <summary>
        /// 获取生命值
        /// </summary>
        private float GetHealth(ITargetable target)
        {
            return target.AbilityComponent?.Attributes?.GetAttribute("Health")?.CurrentValue ?? 0f;
        }

        /// <summary>
        /// 获取候选数量
        /// </summary>
        public int CandidateCount
        {
            get
            {
                lock (_poolLock)
                {
                    return _candidatePool.Count;
                }
            }
        }

        /// <summary>
        /// 清空所有候选
        /// </summary>
        public void ClearAllCandidates()
        {
            lock (_poolLock)
            {
                _candidatePool.Clear();
            }

            lock (_spatialHashLock)
            {
                _spatialHash?.Clear();
            }
        }
    }

    #endregion

    #region 空间哈希 加速范围查询

    /// <summary>
    /// 目标空间哈希 加速范围查询
    /// 实际项目里可以对接World模块的SpatialHashSystem
    /// </summary>
    public class TargetSpatialHash
    {
        // 格子大小
        private readonly float _cellSize;

        // 哈希表 格子坐标到目标列表
        private readonly Dictionary<(int, int), List<ITargetable>> _cells = new();

        // 目标到格子的映射 方便更新
        private readonly Dictionary<long, (int, int)> _targetCells = new();

        private readonly object _lock = new();

        public TargetSpatialHash(float cellSize = 10f)
        {
            _cellSize = cellSize;
        }

        /// <summary>
        /// 添加目标
        /// </summary>
        public void Add(ITargetable target)
        {
            if (target == null) return;

            var cell = GetCell(target.Position);

            lock (_lock)
            {
                if (!_cells.TryGetValue(cell, out var list))
                {
                    list = new List<ITargetable>();
                    _cells[cell] = list;
                }
                list.Add(target);
                _targetCells[target.TargetId] = cell;
            }
        }

        /// <summary>
        /// 移除目标
        /// </summary>
        public void Remove(ITargetable target)
        {
            if (target == null) return;

            lock (_lock)
            {
                if (_targetCells.TryGetValue(target.TargetId, out var cell))
                {
                    if (_cells.TryGetValue(cell, out var list))
                    {
                        list.Remove(target);
                        if (list.Count == 0)
                            _cells.Remove(cell);
                    }
                    _targetCells.Remove(target.TargetId);
                }
            }
        }

        /// <summary>
        /// 更新目标位置
        /// </summary>
        public void Update(ITargetable target)
        {
            if (target == null) return;

            var newCell = GetCell(target.Position);

            lock (_lock)
            {
                if (_targetCells.TryGetValue(target.TargetId, out var oldCell))
                {
                    if (oldCell == newCell) return;

                    // 从旧格子移除
                    if (_cells.TryGetValue(oldCell, out var oldList))
                    {
                        oldList.Remove(target);
                        if (oldList.Count == 0)
                            _cells.Remove(oldCell);
                    }
                }

                // 加到新格子
                if (!_cells.TryGetValue(newCell, out var newList))
                {
                    newList = new List<ITargetable>();
                    _cells[newCell] = newList;
                }
                newList.Add(target);
                _targetCells[target.TargetId] = newCell;
            }
        }

        /// <summary>
        /// 范围查询
        /// </summary>
        public List<ITargetable> Query(Vector3 center, float radius)
        {
            var result = new List<ITargetable>();

            int minX = (int)MathF.Floor((center.X - radius) / _cellSize);
            int maxX = (int)MathF.Floor((center.X + radius) / _cellSize);
            int minZ = (int)MathF.Floor((center.Z - radius) / _cellSize);
            int maxZ = (int)MathF.Floor((center.Z + radius) / _cellSize);

            lock (_lock)
            {
                for (int x = minX; x <= maxX; x++)
                {
                    for (int z = minZ; z <= maxZ; z++)
                    {
                        if (_cells.TryGetValue((x, z), out var list))
                        {
                            result.AddRange(list);
                        }
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// 获取格子坐标
        /// </summary>
        private (int, int) GetCell(Vector3 position)
        {
            int x = (int)MathF.Floor(position.X / _cellSize);
            int z = (int)MathF.Floor(position.Z / _cellSize);
            return (x, z);
        }

        /// <summary>
        /// 清空
        /// </summary>
        public void Clear()
        {
            lock (_lock)
            {
                _cells.Clear();
                _targetCells.Clear();
            }
        }

        /// <summary>
        /// 格子数量
        /// </summary>
        public int CellCount
        {
            get
            {
                lock (_lock)
                {
                    return _cells.Count;
                }
            }
        }

        /// <summary>
        /// 目标总数
        /// </summary>
        public int TargetCount
        {
            get
            {
                lock (_lock)
                {
                    return _targetCells.Count;
                }
            }
        }
    }

    #endregion

    #region 目标选择系统管理器

    /// <summary>
    /// 目标选择系统管理器
    /// </summary>
    public class GameplayTargetingSystem
    {
        // 单例
        private static GameplayTargetingSystem? _instance;
        private static readonly object _instanceLock = new();

        public static GameplayTargetingSystem Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_instanceLock)
                    {
                        _instance ??= new GameplayTargetingSystem();
                    }
                }
                return _instance;
            }
        }

        // 目标选择器
        public GameplayTargetSelector Selector { get; } = new GameplayTargetSelector();

        // 配置仓库
        public TargetingConfigRepository Configs { get; } = new();

        // 空间哈希
        public TargetSpatialHash SpatialHash { get; } = new TargetSpatialHash(10f);

        private GameplayTargetingSystem()
        {
            Selector.SetSpatialHash(SpatialHash);
        }

        /// <summary>
        /// 选择目标 按配置名
        /// </summary>
        public TargetingResult SelectTargets(string configName, ITargetable? source, Vector3? cursorLocation = null)
        {
            var config = Configs.Get(configName);
            if (config == null)
            {
                return new TargetingResult();
            }

            return Selector.SelectTargets(config, source, cursorLocation);
        }

        /// <summary>
        /// 选择目标 按配置
        /// </summary>
        public TargetingResult SelectTargets(TargetingConfig config, ITargetable? source, Vector3? cursorLocation = null)
        {
            return Selector.SelectTargets(config, source, cursorLocation);
        }

        /// <summary>
        /// 注册目标
        /// </summary>
        public void RegisterTarget(ITargetable target)
        {
            Selector.RegisterTarget(target);
        }

        /// <summary>
        /// 注销目标
        /// </summary>
        public void UnregisterTarget(ITargetable target)
        {
            Selector.UnregisterTarget(target);
        }

        /// <summary>
        /// 更新目标位置
        /// </summary>
        public void UpdateTargetPosition(ITargetable target)
        {
            Selector.UpdateTargetPosition(target);
        }

        /// <summary>
        /// 注册配置
        /// </summary>
        public bool RegisterConfig(TargetingConfig config)
        {
            return Configs.Register(config);
        }

        /// <summary>
        /// 关闭系统
        /// </summary>
        public void Shutdown()
        {
            Selector.ClearAllCandidates();
            SpatialHash.Clear();
            Configs.Clear();
        }
    }

    #endregion

    #region 目标选择预设工厂

    /// <summary>
    /// 目标选择预设工厂
    /// </summary>
    public static class TargetingPresetFactory
    {
        /// <summary>
        /// 自身
        /// </summary>
        public static TargetingConfig CreateSelf()
        {
            return new TargetingConfig
            {
                Name = "Self",
                Type = TargetingType.Self,
                FilterType = TargetFilterType.None,
            };
        }

        /// <summary>
        /// 单体最近敌方
        /// </summary>
        public static TargetingConfig CreateSingleEnemy(float range = 10f)
        {
            return new TargetingConfig
            {
                Name = "SingleEnemy",
                Type = TargetingType.Single,
                FilterType = TargetFilterType.EnemyOnly,
                SortType = TargetSortType.NearestFirst,
                Range = range,
                MaxTargets = 1,
            };
        }

        /// <summary>
        /// 圆形范围敌方
        /// </summary>
        public static TargetingConfig CreateCircleEnemies(float radius = 5f)
        {
            return new TargetingConfig
            {
                Name = "CircleEnemies",
                Type = TargetingType.Circle,
                FilterType = TargetFilterType.EnemyOnly,
                SortType = TargetSortType.NearestFirst,
                Radius = radius,
                Range = radius,
            };
        }

        /// <summary>
        /// 球形范围所有
        /// </summary>
        public static TargetingConfig CreateSphereAll(float radius = 5f)
        {
            return new TargetingConfig
            {
                Name = "SphereAll",
                Type = TargetingType.Sphere,
                FilterType = TargetFilterType.All,
                Radius = radius,
            };
        }

        /// <summary>
        /// 矩形范围敌方 前方直线
        /// </summary>
        public static TargetingConfig CreateBoxEnemies(float range = 8f, float width = 3f)
        {
            return new TargetingConfig
            {
                Name = "BoxEnemies",
                Type = TargetingType.Box,
                FilterType = TargetFilterType.EnemyOnly,
                Range = range,
                Width = width,
            };
        }

        /// <summary>
        /// 锥形范围敌方 扇形攻击
        /// </summary>
        public static TargetingConfig CreateConeEnemies(float range = 6f, float angle = 90f)
        {
            return new TargetingConfig
            {
                Name = "ConeEnemies",
                Type = TargetingType.Cone,
                FilterType = TargetFilterType.EnemyOnly,
                Range = range,
                Angle = angle,
            };
        }

        /// <summary>
        /// 所有友方
        /// </summary>
        public static TargetingConfig CreateAllAllies(float range = 20f)
        {
            return new TargetingConfig
            {
                Name = "AllAllies",
                Type = TargetingType.AllAllies,
                FilterType = TargetFilterType.FriendlyOnly,
                Range = range,
                IncludeSelf = true,
            };
        }

        /// <summary>
        /// 所有敌方
        /// </summary>
        public static TargetingConfig CreateAllEnemies(float range = 20f)
        {
            return new TargetingConfig
            {
                Name = "AllEnemies",
                Type = TargetingType.AllEnemies,
                FilterType = TargetFilterType.EnemyOnly,
                Range = range,
            };
        }

        /// <summary>
        /// 随机敌方
        /// </summary>
        public static TargetingConfig CreateRandomEnemy(float range = 10f, int count = 1)
        {
            return new TargetingConfig
            {
                Name = "RandomEnemy",
                Type = TargetingType.Random,
                FilterType = TargetFilterType.EnemyOnly,
                SortType = TargetSortType.Random,
                Range = range,
                MaxTargets = count,
            };
        }

        /// <summary>
        /// 多目标最近敌方
        /// </summary>
        public static TargetingConfig CreateMultipleEnemies(float range = 10f, int maxTargets = 3)
        {
            return new TargetingConfig
            {
                Name = "MultipleEnemies",
                Type = TargetingType.Multiple,
                FilterType = TargetFilterType.EnemyOnly,
                SortType = TargetSortType.NearestFirst,
                Range = range,
                MaxTargets = maxTargets,
            };
        }

        /// <summary>
        /// 低血量友方 治疗用
        /// </summary>
        public static TargetingConfig CreateLowestHealthAlly(float range = 15f)
        {
            return new TargetingConfig
            {
                Name = "LowestHealthAlly",
                Type = TargetingType.Multiple,
                FilterType = TargetFilterType.FriendlyAndSelf,
                SortType = TargetSortType.LowestHealthFirst,
                Range = range,
                MaxTargets = 1,
                MinAttributeValues = new Dictionary<string, float>
                {
                    { "Health", 0f }
                },
                MaxAttributeValues = new Dictionary<string, float>
                {
                    { "Health", 50f }
                },
            };
        }

        /// <summary>
        /// 胶囊范围 前方
        /// </summary>
        public static TargetingConfig CreateCapsuleForward(float range = 8f, float radius = 2f)
        {
            return new TargetingConfig
            {
                Name = "CapsuleForward",
                Type = TargetingType.Capsule,
                FilterType = TargetFilterType.EnemyOnly,
                Range = range,
                Radius = radius,
            };
        }

        /// <summary>
        /// 直线穿透
        /// </summary>
        public static TargetingConfig CreateLinePiercing(float range = 15f, float width = 1f)
        {
            return new TargetingConfig
            {
                Name = "LinePiercing",
                Type = TargetingType.Line,
                FilterType = TargetFilterType.EnemyOnly,
                Range = range,
                Width = width,
            };
        }

        /// <summary>
        /// 获取所有预设
        /// </summary>
        public static List<TargetingConfig> GetAllPresets()
        {
            return new List<TargetingConfig>
            {
                CreateSelf(),
                CreateSingleEnemy(),
                CreateCircleEnemies(),
                CreateSphereAll(),
                CreateBoxEnemies(),
                CreateConeEnemies(),
                CreateAllAllies(),
                CreateAllEnemies(),
                CreateRandomEnemy(),
                CreateMultipleEnemies(),
                CreateLowestHealthAlly(),
                CreateCapsuleForward(),
                CreateLinePiercing(),
            };
        }

        /// <summary>
        /// 注册所有预设
        /// </summary>
        public static void RegisterAll(GameplayTargetingSystem system)
        {
            foreach (var config in GetAllPresets())
            {
                system.RegisterConfig(config);
            }
        }
    }

    #endregion

    #region 测试目标实现

    /// <summary>
    /// 测试用目标实现
    /// </summary>
    public class TestTarget : ITargetable
    {
        public long TargetId { get; }
        public Vector3 Position { get; set; }
        public Vector3 Forward { get; set; } = Vector3.UnitZ;
        public float Radius { get; set; } = 0.5f;
        public bool IsAlive { get; set; } = true;
        public int TeamId { get; set; } = 1;
        public GameplayAbilityComponent? AbilityComponent { get; set; }
        public GameplayTagContainerFull? Tags { get; set; }
        public float Height { get; set; } = 2f;

        private static long _nextId = 1;

        public TestTarget()
        {
            TargetId = System.Threading.Interlocked.Increment(ref _nextId);
        }

        public TestTarget(Vector3 position, int teamId) : this()
        {
            Position = position;
            TeamId = teamId;
        }
    }

    #endregion

    #region 目标选择系统初始化辅助

    /// <summary>
    /// 目标选择系统启动器
    /// </summary>
    public static class TargetingSystemBootstrap
    {
        public static GameplayTargetingSystem Initialize()
        {
            var system = GameplayTargetingSystem.Instance;
            TargetingPresetFactory.RegisterAll(system);
            return system;
        }

        public static string RunSmokeTest()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("=== Targeting System Smoke Test ===");

            var system = Initialize();
            sb.AppendLine($"System initialized with {system.Configs.Count} configs");

            // 创建测试目标
            var player = new TestTarget(new Vector3(0, 0, 0), 1);
            player.AbilityComponent = new GameplayAbilityComponent();
            player.AbilityComponent.Attributes = new GameplayAttributeSet();
            player.AbilityComponent.Attributes.SetAttribute("Health", 100f);

            var enemy1 = new TestTarget(new Vector3(3, 0, 0), 2);
            var enemy2 = new TestTarget(new Vector3(5, 0, 0), 2);
            var enemy3 = new TestTarget(new Vector3(8, 0, 0), 2);
            var ally1 = new TestTarget(new Vector3(2, 0, 2), 1);

            system.RegisterTarget(player);
            system.RegisterTarget(enemy1);
            system.RegisterTarget(enemy2);
            system.RegisterTarget(enemy3);
            system.RegisterTarget(ally1);

            sb.AppendLine($"Registered {system.Selector.CandidateCount} targets");

            // 测试自身选择
            var result = system.SelectTargets("Self", player);
            sb.AppendLine($"Self select: {result.Count} targets");

            // 测试单体最近敌方
            result = system.SelectTargets("SingleEnemy", player);
            sb.AppendLine($"SingleEnemy select: {result.Count} targets, distance: {(result.Count > 0 ? result.Targets[0].Distance : 0):F1}");

            // 测试圆形范围敌方
            result = system.SelectTargets("CircleEnemies", player);
            sb.AppendLine($"CircleEnemies select: {result.Count} targets");

            // 测试锥形范围敌方
            result = system.SelectTargets("ConeEnemies", player);
            sb.AppendLine($"ConeEnemies select: {result.Count} targets");

            // 测试所有友方
            result = system.SelectTargets("AllAllies", player);
            sb.AppendLine($"AllAllies select: {result.Count} targets");

            // 测试多目标
            result = system.SelectTargets("MultipleEnemies", player);
            sb.AppendLine($"MultipleEnemies select: {result.Count} targets");

            // 测试鼠标位置选择
            result = system.SelectTargets("CircleEnemies", player, new Vector3(5, 0, 0));
            sb.AppendLine($"CircleEnemies at cursor (5,0,0): {result.Count} targets");

            system.Shutdown();
            sb.AppendLine("Test complete");
            return sb.ToString();
        }
    }

    #endregion

} // namespace YamaEngine.Gameplay
