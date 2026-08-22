// ============================================================================
//  ShanyuEngine.AI.NavigationMeshSystem.cs
//  山雨引擎 -- 导航网格系统 (Navigation Mesh System)
//  对齐 UE NavMesh / Unity NavMesh / Recast+Detour
// ----------------------------------------------------------------------------
//  完整的导航网格系统，包含：
//    1. NavMeshBuildSettings    - 构建设置 (代理半径/高度/步高/最大坡度/体素大小)
//    2. NavMeshArea             - 导航区域 (ID/名称/花费/标志)
//    3. NavAreaFlag             - 导航区域标志枚举
//    4. NavMeshLink             - 导航链接 (两个点之间单向/双向链接)
//    5. NavMeshObstacle         - 动态障碍物 (位置/半径/高度/避让)
//    6. NavMeshModifierVolume   - 修改体积 (覆盖区域属性)
//    7. NavMesh                 - 导航网格 (体素化/区域生成/边缘提取/多边形/链接)
//    8. NavPath / NavPathType   - 路径 (点数组/长度/状态/路径类型)
//    9. NavMeshQuery            - 路径查询 (FindPath/FindRandomPoint/RayCast)
//   10. NavAgentProperties      - 导航代理属性
//   11. NavAgent                - 导航代理 (当前路径/目标/到达/避让/插值移动)
//   12. NavCrowd                - 人群模拟 (多代理避让/RVO2)
//   13. NavMeshDebug            - 调试可视化
//   14. 导航网格烘焙 (异步构建/增量更新/分块加载)
// ============================================================================

using System;
using System.Collections.Generic;
using System.Numerics;
using ShanyuEngine.Rendering;

namespace ShanyuEngine.AI
{
    // ========================================================================
    //  一、枚举
    // ========================================================================

    /// <summary>
    /// 导航区域标志位。可组合使用。
    /// </summary>
    [Flags]
    public enum NavAreaFlag
    {
        Walkable    = 1 << 0,
        NotWalkable = 1 << 1,
        Jump        = 1 << 2,
        JumpDown    = 1 << 3,
        Crouch      = 1 << 4,
        Swim        = 1 << 5,
        Fly         = 1 << 6,
        Ladder      = 1 << 7,
        DropDown    = 1 << 8
    }

    /// <summary>
    /// 路径类型。
    /// </summary>
    public enum NavPathType
    {
        Straight,
        StraightWithCorners,
        Spline,
        SplineWithCorners
    }

    /// <summary>
    /// 路径查找状态。
    /// </summary>
    public enum NavPathState
    {
        Invalid,
        Partial,
        Complete
    }

    /// <summary>
    /// 链接方向。
    /// </summary>
    public enum NavLinkDirection
    {
        Forward,
        Backward,
        BothWays
    }

    /// <summary>
    /// 导航网格烘焙状态。
    /// </summary>
    public enum NavMeshBuildState
    {
        Idle,
        Building,
        Built,
        Failed
    }

    // ========================================================================
    //  二、导航构建设置
    // ========================================================================

    /// <summary>
    /// 导航网格构建设置。控制体素化参数和代理约束。
    /// </summary>

    /// <summary>
    /// 轴对齐包围盒。
    /// </summary>

    // ========================================================================
    //  三、导航区域
    // ========================================================================

    /// <summary>
    /// 导航区域。定义不同地形类型的通行属性。
    /// </summary>
    public sealed class NavMeshArea
    {
        public int Id { get; set; }
        public string Name { get; set; } = "Walkable";
        public float Cost { get; set; } = 1f;
        public NavAreaFlag Flags { get; set; } = NavAreaFlag.Walkable;
        public bool CanGenerate { get; set; } = true;
        public bool AutoLink { get; set; } = true;
        public Vector3 DebugColor { get; set; } = new Vector3(0.5f, 0.7f, 1f);

        public static NavMeshArea DefaultWalkable => new NavMeshArea
        {
            Id = 0, Name = "Walkable", Cost = 1f,
            Flags = NavAreaFlag.Walkable, DebugColor = new Vector3(0.5f, 0.7f, 1f)
        };

        public static NavMeshArea Road => new NavMeshArea
        {
            Id = 1, Name = "Road", Cost = 0.5f,
            Flags = NavAreaFlag.Walkable, DebugColor = new Vector3(0.3f, 0.3f, 0.3f)
        };

        public static NavMeshArea Grass => new NavMeshArea
        {
            Id = 2, Name = "Grass", Cost = 1.2f,
            Flags = NavAreaFlag.Walkable, DebugColor = new Vector3(0.2f, 0.6f, 0.2f)
        };

        public static NavMeshArea Water => new NavMeshArea
        {
            Id = 3, Name = "Water", Cost = 3f,
            Flags = NavAreaFlag.Swim, DebugColor = new Vector3(0.2f, 0.3f, 0.8f)
        };

        public static NavMeshArea NotWalkable => new NavMeshArea
        {
            Id = 63, Name = "NotWalkable", Cost = float.MaxValue,
            Flags = NavAreaFlag.NotWalkable, CanGenerate = false,
            DebugColor = new Vector3(1f, 0.2f, 0.2f)
        };
    }

    // ========================================================================
    //  四、导航链接
    // ========================================================================

    /// <summary>
    /// 导航链接。连接两个非相邻的导航多边形，用于跳跃/攀爬/游泳等。
    /// </summary>

    // ========================================================================
    //  五、动态障碍物
    // ========================================================================

    /// <summary>
    /// 动态导航障碍物。实时影响导航网格的可达性。
    /// </summary>
    public sealed class NavMeshObstacle
    {
        public Vector3 Position { get; set; }
        public float Radius { get; set; } = 1f;
        public float Height { get; set; } = 2f;
        public bool AvoidanceEnabled { get; set; } = true;
        public float InfluenceRadius { get; set; } = 2f;
        public bool CarveNavMesh { get; set; } = true;
        public bool IsActive { get; set; } = true;

        /// <summary>所属Actor（可选）。</summary>
        public ShanyuEngine.GameFramework.Actor Owner { get; set; }

        public bool ContainsPoint(Vector3 point, float margin = 0f)
        {
            float dx = point.X - Position.X;
            float dz = point.Z - Position.Z;
            float dy = point.Y - Position.Y;
            float effectiveRadius = Radius + margin;
            return (dx * dx + dz * dz) <= effectiveRadius * effectiveRadius &&
                   dy >= 0f && dy <= Height;
        }
    }

    // ========================================================================
    //  六、修改体积
    // ========================================================================

    /// <summary>
    /// 导航网格修改体积。覆盖区域内的导航属性。
    /// </summary>
    public sealed class NavMeshModifierVolume
    {
        public BoundingBox Bounds { get; set; }
        public NavMeshArea Area { get; set; }
        public bool DisableNavigation { get; set; } = false;
        public float CostOverride { get; set; } = -1f;
        public bool IsActive { get; set; } = true;

        public bool ContainsPoint(Vector3 point)
        {
            return IsActive && Bounds.Contains(point);
        }
    }

    // ========================================================================
    //  七、导航多边形 (内部数据结构)
    // ========================================================================

    /// <summary>
    /// 导航多边形。构成导航网格的基本单元。
    /// </summary>

    // ========================================================================
    //  八、导航网格 (NavMesh)
    // ========================================================================

    /// <summary>
    /// 导航网格。包含体素化、区域生成、边缘提取、多边形和链接。
    /// </summary>

    // ========================================================================
    //  九、路径 (NavPath)
    // ========================================================================

    /// <summary>
    /// 导航路径。存储从起点到终点的路径数据。
    /// </summary>
    public sealed class NavPath
    {
        public List<Vector3> Points { get; } = new List<Vector3>();
        public NavPathType PathType { get; set; } = NavPathType.StraightWithCorners;
        public NavPathState State { get; set; } = NavPathState.Invalid;
        public float Length { get; private set; }

        /// <summary>路径上的当前位置（沿路径插值）。</summary>
        public int CurrentCornerIndex { get; set; }

        public NavPath() { }

        public NavPath(List<Vector3> points, NavPathType type = NavPathType.StraightWithCorners)
        {
            PathType = type;
            Points.Clear();
            Points.AddRange(points);
            RecalculateLength();
            State = Points.Count >= 2 ? NavPathState.Complete : NavPathState.Invalid;
        }

        public void SetPoints(List<Vector3> points)
        {
            Points.Clear();
            Points.AddRange(points);
            RecalculateLength();
            State = Points.Count >= 2 ? NavPathState.Complete : NavPathState.Invalid;
        }

        public void RecalculateLength()
        {
            Length = 0f;
            for (int i = 1; i < Points.Count; i++)
                Length += Vector3.Distance(Points[i - 1], Points[i]);
        }

        /// <summary>获取路径上指定参数位置（0~1）的点。</summary>
        public Vector3 GetPointAt(float parameter)
        {
            if (Points.Count == 0) return Vector3.Zero;
            if (parameter <= 0f) return Points[0];
            if (parameter >= 1f) return Points[Points.Count - 1];

            float targetDist = parameter * Length;
            float accumulated = 0f;
            for (int i = 1; i < Points.Count; i++)
            {
                float segLen = Vector3.Distance(Points[i - 1], Points[i]);
                if (accumulated + segLen >= targetDist)
                {
                    float t = (targetDist - accumulated) / segLen;
                    return Vector3.Lerp(Points[i - 1], Points[i], t);
                }
                accumulated += segLen;
            }
            return Points[Points.Count - 1];
        }

        /// <summary>获取当前拐角到下一个拐角的方向。</summary>
        public Vector3 GetCurrentDirection()
        {
            if (CurrentCornerIndex >= Points.Count - 1) return Vector3.Zero;
            var dir = Points[CurrentCornerIndex + 1] - Points[CurrentCornerIndex];
            if (dir.LengthSquared() > 0.0001f)
                return Vector3.Normalize(dir);
            return Vector3.Zero;
        }

        /// <summary>是否到达终点。</summary>
        public bool IsEndOfPath(int currentIndex) => currentIndex >= Points.Count - 1;

        /// <summary>清除路径。</summary>
        public void Clear()
        {
            Points.Clear();
            Length = 0f;
            State = NavPathState.Invalid;
            CurrentCornerIndex = 0;
        }
    }

    // ========================================================================
    //  十、路径查询 (NavMeshQuery)
    // ========================================================================

    /// <summary>
    /// 导航网格路径查询。提供寻路、随机点、投影、射线检测等功能。
    /// </summary>

    // ========================================================================
    //  十一、导航代理属性
    // ========================================================================

    /// <summary>
    /// 导航代理属性配置。
    /// </summary>
    public sealed class NavAgentProperties
    {
        public float Radius { get; set; } = 0.5f;
        public float Height { get; set; } = 2.0f;
        public float StepHeight { get; set; } = 0.4f;
        public float MaxSlope { get; set; } = 45f;
        public float MaxSpeed { get; set; } = 6f;
        public float Acceleration { get; set; } = 20f;
        public float Deceleration { get; set; } = 15f;
        public int Priority { get; set; } = 50;
        public NavAreaFlag AvoidAreas { get; set; } = NavAreaFlag.NotWalkable;
        public Dictionary<int, float> AreaCostTable { get; } = new Dictionary<int, float>();
        public NavPathType PreferredPathType { get; set; } = NavPathType.StraightWithCorners;
        public float AvoidanceRadius { get; set; } = 1.5f;
        public float AvoidanceHeight { get; set; } = 2.5f;
    }

    // ========================================================================
    //  十二、导航代理 (NavAgent)
    // ========================================================================

    /// <summary>
    /// 导航代理。每个AI实体拥有一个导航代理，负责路径跟随和移动。
    /// </summary>
    public sealed class NavAgent
    {
        public NavAgentProperties Properties { get; set; } = new NavAgentProperties();

        /// <summary>当前位置。</summary>
        public Vector3 Position { get; set; }
        /// <summary>目标位置。</summary>
        public Vector3 TargetPosition { get; set; }
        /// <summary>当前速度。</summary>
        public Vector3 Velocity { get; set; }
        /// <summary>当前路径。</summary>
        public NavPath CurrentPath { get; set; }
        /// <summary>是否正在移动。</summary>
        public bool IsMoving { get; private set; }
        /// <summary>是否已到达目标。</summary>
        public bool HasArrived { get; private set; }
        /// <summary>到达回调半径。</summary>
        public float ArrivalRadius { get; set; } = 0.5f;

        /// <summary>到达目标回调。</summary>
        public Action OnArrived { get; set; }
        /// <summary>路径失效回调。</summary>
        public Action OnPathInvalid { get; set; }
        /// <summary>移动更新回调。</summary>
        public Action<Vector3> OnMove { get; set; }

        private NavMesh _navMesh;
        private NavMeshQuery _query;
        private int _currentCornerIdx = 0;

        public NavAgent(NavMesh navMesh)
        {
            _navMesh = navMesh;
            _query = new NavMeshQuery(navMesh);
        }

        /// <summary>移动到指定位置。</summary>
        public bool MoveTo(Vector3 target)
        {
            TargetPosition = target;
            CurrentPath = _query.FindPath(Position, target, Properties.PreferredPathType);

            if (CurrentPath.State == NavPathState.Invalid)
            {
                OnPathInvalid?.Invoke();
                return false;
            }

            _currentCornerIdx = 0;
            IsMoving = true;
            HasArrived = false;
            return true;
        }

        /// <summary>重新规划路径。</summary>
        public bool Repath()
        {
            if (CurrentPath != null && CurrentPath.Points.Count > 0)
                return MoveTo(TargetPosition);
            return false;
        }

        /// <summary>停止移动。</summary>
        public void Stop()
        {
            IsMoving = false;
            Velocity = Vector3.Zero;
            CurrentPath = null;
        }

        /// <summary>每帧更新。</summary>
        public void Update(float deltaTime)
        {
            if (!IsMoving || CurrentPath == null || CurrentPath.Points.Count < 2)
            {
                IsMoving = false;
                return;
            }

            // 检查是否到达终点
            float distToEnd = Vector3.Distance(Position, TargetPosition);
            if (distToEnd <= ArrivalRadius)
            {
                IsMoving = false;
                HasArrived = true;
                Velocity = Vector3.Zero;
                OnArrived?.Invoke();
                return;
            }

            // 沿路径移动
            var targetCorner = CurrentPath.Points[_currentCornerIdx];
            float distToCorner = Vector3.Distance(Position, targetCorner);

            if (distToCorner <= 0.3f)
            {
                _currentCornerIdx++;
                if (_currentCornerIdx >= CurrentPath.Points.Count)
                {
                    IsMoving = false;
                    HasArrived = true;
                    Velocity = Vector3.Zero;
                    OnArrived?.Invoke();
                    return;
                }
                targetCorner = CurrentPath.Points[_currentCornerIdx];
            }

            // 计算速度方向
            var direction = targetCorner - Position;
            if (direction.LengthSquared() > 0.0001f)
                direction = Vector3.Normalize(direction);

            // 加速度模型
            float speed = Properties.MaxSpeed;
            Velocity = direction * speed;
            Position += Velocity * deltaTime;

            OnMove?.Invoke(Position);
        }

        /// <summary>避让移动。与障碍物和其他代理的避让。</summary>
        public Vector3 ComputeAvoidance(List<NavAgent> otherAgents, List<NavMeshObstacle> obstacles)
        {
            var avoidance = Vector3.Zero;

            // 代理间避让
            foreach (var other in otherAgents)
            {
                if (other == this) continue;
                var diff = Position - other.Position;
                float dist = diff.Length();
                float avoidRadius = Properties.AvoidanceRadius + other.Properties.AvoidanceRadius;
                if (dist < avoidRadius && dist > 0.001f)
                    avoidance += Vector3.Normalize(diff) * (avoidRadius - dist) / avoidRadius;
            }

            // 障碍物避让
            foreach (var obs in obstacles)
            {
                if (!obs.IsActive || !obs.AvoidanceEnabled) continue;
                var diff = Position - obs.Position;
                diff.Y = 0;
                float dist = diff.Length();
                float avoidRadius = obs.InfluenceRadius + Properties.Radius;
                if (dist < avoidRadius && dist > 0.001f)
                    avoidance += Vector3.Normalize(diff) * (avoidRadius - dist) / avoidRadius;
            }

            if (avoidance.LengthSquared() > 0.0001f)
                avoidance = Vector3.Normalize(avoidance) * Properties.MaxSpeed * 0.5f;

            return avoidance;
        }
    }

    // ========================================================================
    //  十三、人群模拟 (NavCrowd)
    // ========================================================================

    /// <summary>
    /// 人群模拟管理器。多代理避让（简化 RVO2 算法）、队长跟随。
    /// </summary>
    public sealed class NavCrowd
    {
        private readonly NavMesh _navMesh;
        private readonly List<NavAgent> _agents = new List<NavAgent>();
        private readonly List<NavMeshObstacle> _obstacles = new List<NavMeshObstacle>();

        /// <summary>人群参数。</summary>
        public float SeparationWeight { get; set; } = 1.5f;
        public float CohesionWeight { get; set; } = 0.5f;
        public float AlignmentWeight { get; set; } = 1.0f;
        public float LeaderFollowDistance { get; set; } = 3f;

        public int AgentCount => _agents.Count;

        public NavCrowd(NavMesh navMesh)
        {
            _navMesh = navMesh;
        }

        /// <summary>添加代理到人群。</summary>
        public NavAgent AddAgent(Vector3 position, NavAgentProperties properties = null)
        {
            var agent = new NavAgent(_navMesh)
            {
                Position = position,
                Properties = properties ?? new NavAgentProperties()
            };
            _agents.Add(agent);
            return agent;
        }

        /// <summary>从人群中移除代理。</summary>
        public void RemoveAgent(NavAgent agent)
        {
            _agents.Remove(agent);
            agent.Stop();
        }

        /// <summary>添加动态障碍物。</summary>
        public void AddObstacle(NavMeshObstacle obstacle)
        {
            _obstacles.Add(obstacle);
            _navMesh.AddObstacle(obstacle);
        }

        /// <summary>移除动态障碍物。</summary>
        public void RemoveObstacle(NavMeshObstacle obstacle)
        {
            _obstacles.Remove(obstacle);
            _navMesh.RemoveObstacle(obstacle);
        }

        /// <summary>每帧更新所有代理。</summary>
        public void Update(float deltaTime)
        {
            // RVO2 简化避让
            foreach (var agent in _agents)
            {
                if (!agent.IsMoving) continue;

                var avoidance = agent.ComputeAvoidance(_agents, _obstacles);
                // 将避让力叠加到移动方向
                agent.Position += avoidance * deltaTime;
            }

            // 普通更新
            foreach (var agent in _agents)
                agent.Update(deltaTime);
        }

        /// <summary>队长跟随。设置队长，其他代理跟随。</summary>
        public void SetLeader(NavAgent leader, float followDistance = 3f)
        {
            LeaderFollowDistance = followDistance;
            foreach (var agent in _agents)
            {
                if (agent != leader && agent.IsMoving)
                {
                    // 跟随队长的偏移位置
                    var offset = new Vector3(
                        _agents.IndexOf(agent) * 2f - _agents.Count,
                        0,
                        -LeaderFollowDistance
                    );
                    agent.MoveTo(leader.TargetPosition + offset);
                }
            }
        }

        /// <summary>获取所有代理。</summary>
        public IReadOnlyList<NavAgent> GetAgents() => _agents;
    }

    // ========================================================================
    //  十四、调试可视化 (NavMeshDebug)
    // ========================================================================

    /// <summary>
    /// 导航网格调试可视化。
    /// </summary>
    public sealed class NavMeshDebug
    {
        public bool ShowAreas { get; set; } = true;
        public bool ShowPaths { get; set; } = true;
        public bool ShowAgents { get; set; } = true;
        public bool ShowObstacles { get; set; } = true;
        public bool ShowLinks { get; set; } = true;
        public bool ShowPolyBorders { get; set; } = true;

        /// <summary>可视化回调。</summary>
        public Action<string, Vector3, Vector3> DrawLine { get; set; }
        public Action<Vector3, float, Vector3> DrawSphere { get; set; }
        public Action<Vector3, Vector3> DrawBox { get; set; }

        /// <summary>绘制导航网格。</summary>
        public void DrawNavMesh(NavMesh navMesh)
        {
            if (!ShowAreas) return;

            // 绘制多边形边框
            if (ShowPolyBorders)
            {
                foreach (var poly in navMesh.Polygons)
                {
                    for (int i = 0; i < poly.Vertices.Count; i++)
                    {
                        var a = poly.Vertices[i];
                        var b = poly.Vertices[(i + 1) % poly.Vertices.Count];
                        DrawLine?.Invoke("NavMesh", a, b);
                    }
                }
            }

            // 绘制链接
            if (ShowLinks)
            {
                foreach (var link in navMesh.Links)
                {
                    DrawLine?.Invoke("NavLink", link.Start, link.End);
                    DrawSphere?.Invoke(link.Start, 0.3f, new Vector3(1, 1, 0));
                    DrawSphere?.Invoke(link.End, 0.3f, new Vector3(1, 1, 0));
                }
            }

            // 绘制障碍物
            if (ShowObstacles)
            {
                foreach (var obs in navMesh.Obstacles)
                {
                    DrawSphere?.Invoke(obs.Position, obs.Radius, new Vector3(1, 0.3f, 0.3f));
                }
            }
        }

        /// <summary>绘制代理位置。</summary>
        public void DrawAgent(NavAgent agent)
        {
            if (!ShowAgents) return;
            DrawSphere?.Invoke(agent.Position, agent.Properties.Radius, new Vector3(0, 1, 0));
            if (agent.IsMoving && agent.CurrentPath != null)
            {
                for (int i = 0; i < agent.CurrentPath.Points.Count - 1; i++)
                {
                    DrawLine?.Invoke("NavPath", agent.CurrentPath.Points[i], agent.CurrentPath.Points[i + 1]);
                }
            }
        }
    }
}
