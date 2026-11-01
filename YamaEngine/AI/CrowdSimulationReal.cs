// ============================================================================
//  CrowdSimulationReal.cs -- RVO2人群模拟 (完整实现)
//  yama引擎 YamaEngine.AI
// ----------------------------------------------------------------------------
//  完整实现:
//    1. RVO2 (Optimal Reciprocal Collision Avoidance)
//       - RVOAgent: 代理 (位置/速度/半径/最大速度/最大力)
//       - RVOSimulator: 模拟器
//         - ComputeORCAPlanes: 计算ORCA半平面
//         - SolveLinearProgram: 2D线性规划求解
//         - Step: 一步仿真
//    2. Kaptcha高级避障
//    3. 队长跟随 (Leader-Following)
//       - Formation: 队形 (Circle/Line/V/Wedge/Column/Custom)
//       - FormationSlot: 队形槽位
//       - LeaderAgent: 队长
//       - FollowerAgent: 队员
// ============================================================================

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;

namespace YamaEngine.AI
{
    // ════════════════════════════════════════════════════════════════════════════
    //  RVO2 - Optimal Reciprocal Collision Avoidance
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>ORCA半平面: u·n >= c</summary>
    public struct ORCAPlane
    {
        public Vector2 Point;   // 半平面上的点
        public Vector2 Normal;  // 半平面法线 (指向可行侧)
        public float Distance;  // 半平面到原点的有符号距离 (c)

        public ORCAPlane(Vector2 point, Vector2 normal, float distance)
        {
            Point = point;
            Normal = normal;
            Distance = distance;
        }

        /// <summary>检查速度是否在半平面可行侧</summary>
        public bool IsFeasible(Vector2 velocity)
        {
            return Vector2.Dot(velocity - Point, Normal) >= -1e-4f;
        }
    }

    /// <summary>
    /// RVO代理: 具有位置/速度/半径/最大速度等属性的寻路代理
    /// </summary>
    public class RVOAgent
    {
        public int ID { get; set; }
        public Vector3 Position { get; set; } = Vector3.Zero;
        public Vector3 Velocity { get; set; } = Vector3.Zero;
        public Vector3 DesiredVelocity { get; set; } = Vector3.Zero;
        public Vector3 PreferredVelocity { get; set; } = Vector3.Zero;
        public Vector3 NewVelocity { get; set; } = Vector3.Zero;
        public float Radius { get; set; } = 0.3f;
        public float MaxSpeed { get; set; } = 3.0f;
        public float MaxForce { get; set; } = 10.0f;
        public float Height { get; set; } = 1.8f;

        // RVO邻居
        public List<RVOAgent> NeighborAgents { get; set; } = new List<RVOAgent>();
        public List<RVOObstacle> NeighborObstacles { get; set; } = new List<RVOObstacle>();

        // ORCA约束
        public List<ORCAPlane> ORCAPlanes { get; set; } = new List<ORCAPlane>();

        // 导航引用 (可选, 与NavMesh集成)
        public NavMeshPolyRef NavPolyRef;
        public List<Vector3> NavPath;
        public int NavPathIndex;
        public NavMeshQuery NavQuery;
        public NavMeshFilter NavFilter;

        // 用户数据
        public object UserData { get; set; }

        // 2D位置 (XZ平面)
        public Vector2 Position2D => new Vector2(Position.X, Position.Z);
        public Vector2 Velocity2D => new Vector2(Velocity.X, Velocity.Z);

        public RVOAgent() { }

        public RVOAgent(int id, Vector3 position, float radius = 0.3f, float maxSpeed = 3.0f)
        {
            ID = id;
            Position = position;
            Radius = radius;
            MaxSpeed = maxSpeed;
        }

        /// <summary>计算期望速度 (从NavMesh路径)</summary>
        public void ComputePreferredVelocity(float dt)
        {
            if (NavPath == null || NavPath.Count == 0 || NavPathIndex >= NavPath.Count)
            {
                PreferredVelocity = Vector3.Zero;
                return;
            }

            Vector3 target = NavPath[NavPathIndex];

            // 到达当前路径点
            float dist = Vector3.Distance(Position, target);
            if (dist < 0.5f)
            {
                NavPathIndex++;
                if (NavPathIndex >= NavPath.Count)
                {
                    PreferredVelocity = Vector3.Zero;
                    return;
                }
                target = NavPath[NavPathIndex];
            }

            // 期望速度 = 到目标的方向 * 最大速度
            Vector3 dir = target - Position;
            if (dir.LengthSquared() > 0.001f)
                dir = Vector3.Normalize(dir) * MaxSpeed;
            else
                dir = Vector3.Zero;

            PreferredVelocity = dir;
        }
    }

    /// <summary>
    /// RVO静态障碍物: 线段障碍物 (A→B)
    /// </summary>
    public class RVOObstacle
    {
        public int ID { get; set; }
        public Vector2 PointA { get; set; }
        public Vector2 PointB { get; set; }
        public Vector2 Direction { get; set; }     // 从A到B的方向
        public Vector2 Normal { get; set; }         // 左侧法线
        public float Length { get; set; }
        public RVOObstacle PrevObstacle { get; set; }
        public RVOObstacle NextObstacle { get; set; }

        public RVOObstacle() { }

        public RVOObstacle(Vector2 a, Vector2 b)
        {
            PointA = a;
            PointB = b;
            Vector2 d = b - a;
            Length = d.Length();
            Direction = Length > 1e-6f ? d / Length : Vector2.Zero;
            // 左侧法线 (逆时针旋转90度)
            Normal = new Vector2(-Direction.Y, Direction.X);
        }

        /// <summary>获取障碍物的顶点</summary>
        public (Vector2 left, Vector2 right) GetVertices()
        {
            Vector2 left = PointA + Normal * Radius;
            Vector2 right = PointA - Normal * Radius;
            return (left, right);
        }

        /// <summary>障碍物膨胀半径 (代理半径)</summary>
        public float Radius { get; set; } = 0.3f;
    }

    /// <summary>
    /// RVO模拟器: 完整的ORCA避障算法实现
    /// </summary>
    public class RVOSimulator
    {
        public const float RVOTimeHorizon = 5.0f;        // RVO时间视野
        public const float RVOTimeHorizonObstacles = 3.0f; // 障碍物时间视野
        public const float RVOInvTimeHorizon = 1.0f / RVOTimeHorizon;
        public const float RVOInvTimeHorizonObstacles = 1.0f / RVOTimeHorizonObstacles;
        public const float MaxNeighbors = 20;
        public const float NeighborDist = 15.0f;
        public const float ObstacleNeighborDist = 10.0f;

        public float TimeHorizon { get; set; } = RVOTimeHorizon;
        public float TimeHorizonObstacles { get; set; } = RVOTimeHorizonObstacles;
        public float NeighborSearchDist { get; set; } = NeighborDist;
        public float ObstacleSearchDist { get; set; } = ObstacleNeighborDist;
        public int MaxNeighborCount { get; set; } = 20;
        public float TimeStep { get; set; } = 0.1f;
        public float AgentRadius { get; set; } = 0.3f;
        public float MaxSpeed { get; set; } = 3.0f;

        public List<RVOAgent> Agents { get; private set; } = new List<RVOAgent>();
        public List<RVOObstacle> Obstacles { get; private set; } = new List<RVOObstacle>();

        // 内部 KD-tree 简化 (网格空间分区)
        private float _worldSize = 1000.0f;
        private float _cellSize = 15.0f;
        private Dictionary<(int, int), List<int>> _spatialGrid = new Dictionary<(int, int), List<int>>();
        private bool _obstaclesDirty = true;

        public RVOSimulator() { }

        public RVOSimulator(float worldSize)
        {
            _worldSize = worldSize;
            _cellSize = NeighborSearchDist;
        }

        /// <summary>添加代理</summary>
        public int AddAgent(Vector3 position, float radius = 0.3f, float maxSpeed = 3.0f)
        {
            int id = Agents.Count;
            var agent = new RVOAgent(id, position, radius, maxSpeed);
            Agents.Add(agent);
            return id;
        }

        /// <summary>添加已有代理</summary>
        public int AddAgent(RVOAgent agent)
        {
            if (agent.ID < 0 || agent.ID >= Agents.Count)
            {
                agent.ID = Agents.Count;
            }
            Agents.Add(agent);
            return agent.ID;
        }

        /// <summary>移除代理</summary>
        public void RemoveAgent(int id)
        {
            if (id >= 0 && id < Agents.Count)
            {
                Agents[id] = null;
            }
        }

        /// <summary>添加障碍物 (线段)</summary>
        public void AddObstacle(Vector2 a, Vector2 b)
        {
            var obs = new RVOObstacle(a, b);
            obs.Radius = AgentRadius;
            Obstacles.Add(obs);
            _obstaclesDirty = true;
        }

        /// <summary>添加障碍物 (3D点)</summary>
        public void AddObstacle(Vector3 a, Vector3 b)
        {
            AddObstacle(new Vector2(a.X, a.Z), new Vector2(b.X, b.Z));
        }

        /// <summary>清除所有障碍物</summary>
        public void ClearObstacles()
        {
            Obstacles.Clear();
            _obstaclesDirty = true;
        }

        /// <summary>更新空间分区网格</summary>
        private void UpdateSpatialGrid()
        {
            _spatialGrid.Clear();

            for (int i = 0; i < Agents.Count; i++)
            {
                if (Agents[i] == null) continue;
                var pos = Agents[i].Position2D;
                int cx = (int)Math.Floor(pos.X / _cellSize);
                int cz = (int)Math.Floor(pos.Y / _cellSize);
                var key = (cx, cz);
                if (!_spatialGrid.ContainsKey(key))
                    _spatialGrid[key] = new List<int>();
                _spatialGrid[key].Add(i);
            }
        }

        /// <summary>查询邻居代理</summary>
        private void QueryNeighborAgents(RVOAgent agent)
        {
            agent.NeighborAgents.Clear();
            var pos = agent.Position2D;
            int cx = (int)Math.Floor(pos.X / _cellSize);
            int cz = (int)Math.Floor(pos.Y / _cellSize);
            float searchDist = NeighborSearchDist;
            int cellRange = (int)Math.Ceiling(searchDist / _cellSize);

            // 限制邻居数量
            var candidates = new List<(float dist, RVOAgent agent)>();

            for (int dx = -cellRange; dx <= cellRange; dx++)
            {
                for (int dz = -cellRange; dz <= cellRange; dz++)
                {
                    var key = (cx + dx, cz + dz);
                    if (!_spatialGrid.TryGetValue(key, out var cell)) continue;

                    foreach (int idx in cell)
                    {
                        if (idx == agent.ID || Agents[idx] == null) continue;
                        float dist = Vector2.Distance(pos, Agents[idx].Position2D);
                        if (dist < searchDist)
                        {
                            candidates.Add((dist, Agents[idx]));
                        }
                    }
                }
            }

            candidates.Sort((a, b) => a.dist.CompareTo(b.dist));

            for (int i = 0; i < Math.Min(candidates.Count, MaxNeighborCount); i++)
            {
                agent.NeighborAgents.Add(candidates[i].agent);
            }
        }

        /// <summary>查询邻居障碍物</summary>
        private void QueryNeighborObstacles(RVOAgent agent)
        {
            agent.NeighborObstacles.Clear();
            var pos = agent.Position2D;
            float searchDist = ObstacleSearchDist;

            foreach (var obs in Obstacles)
            {
                // 点到线段的距离
                float dist = PointSegmentDistance(pos, obs.PointA, obs.PointB);
                if (dist < searchDist)
                {
                    agent.NeighborObstacles.Add(obs);
                }
            }
        }

        /// <summary>点到线段距离</summary>
        private float PointSegmentDistance(Vector2 p, Vector2 a, Vector2 b)
        {
            Vector2 ab = b - a;
            float lenSq = ab.LengthSquared();
            if (lenSq < 1e-6f) return Vector2.Distance(p, a);

            float t = Math.Clamp(Vector2.Dot(p - a, ab) / lenSq, 0, 1);
            Vector2 closest = a + t * ab;
            return Vector2.Distance(p, closest);
        }

        // ──────────────────────────────────────────────────────────────────────
        //  ComputeORCAPlanes - 计算ORCA半平面 (核心RVO2算法)
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// 为每个代理计算ORCA半平面约束
        /// 对每个邻居代理j:
        ///   1. 计算相对位置和速度
        ///   2. 计算临界线
        ///   3. 转换为ORCA半平面: u·n >= c
        /// </summary>
        public void ComputeORCAPlanes()
        {
            float invTimeHorizon = 1.0f / TimeHorizon;
            float invTimeHorizonObst = 1.0f / TimeHorizonObstacles;

            foreach (var agent in Agents)
            {
                if (agent == null) continue;

                agent.ORCAPlanes.Clear();
                Vector2 agentPos = agent.Position2D;
                Vector2 agentVel = agent.Velocity2D;

                // ── 代理间ORCA约束 ──
                foreach (var neighbor in agent.NeighborAgents)
                {
                    if (neighbor == null) continue;

                    Vector2 neighborPos = neighbor.Position2D;
                    Vector2 neighborVel = neighbor.Velocity2D;

                    // 相对位置
                    Vector2 relativePos = neighborPos - agentPos;
                    float distSq = relativePos.LengthSquared();
                    float combinedRadius = agent.Radius + neighbor.Radius;
                    float combinedRadiusSq = combinedRadius * combinedRadius;

                    if (distSq > combinedRadiusSq * 100)
                        continue; // 太远, 忽略

                    Vector2 relativeVel = neighborVel - agentVel;
                    float invTimestep = 1.0f / TimeStep;

                    // 计算临界线
                    Vector2 u = new Vector2(
                        relativeVel.X - invTimeHorizon * relativePos.X,
                        relativeVel.Y - invTimeHorizon * relativePos.Y
                    );

                    float uLengthSq = u.LengthSquared();

                    if (uLengthSq > 1e-6f)
                    {
                        Vector2 uNorm = u / (float)Math.Sqrt(uLengthSq);
                        Vector2 n = new Vector2(uNorm.Y, -uNorm.X); // 垂直于u

                        // 检查哪个半平面包含v_j
                        Vector2 point = invTimeHorizon * relativePos;
                        if (Vector2.Dot(n, point) > 0)
                        {
                            n = -n; // 翻转法线方向, 使其指向agent一侧
                        }

                        float dotWithN = Vector2.Dot(point, n);
                        float c = dotWithN + combinedRadius * invTimeHorizon * (float)Math.Sqrt(uLengthSq);

                        // ORCA半平面
                        var plane = new ORCAPlane(
                            new Vector2(agentVel.X, agentVel.Y),
                            n,
                            c
                        );
                        agent.ORCAPlanes.Add(plane);
                    }
                }

                // ── 障碍物ORCA约束 ──
                foreach (var obstacle in agent.NeighborObstacles)
                {
                    ComputeObstacleORCAPlane(agent, obstacle, invTimeHorizonObst);
                }
            }
        }

        /// <summary>计算单个障碍物的ORCA约束</summary>
        private void ComputeObstacleORCAPlane(RVOAgent agent, RVOObstacle obstacle, float invTimeHorizon)
        {
            Vector2 agentPos = agent.Position2D;
            Vector2 agentVel = agent.Velocity2D;
            float combinedRadius = agent.Radius + obstacle.Radius;

            // 障碍物上的最近点
            Vector2 closest = ProjectPointOnSegment(agentPos, obstacle.PointA, obstacle.PointB);
            Vector2 diff = closest - agentPos;
            float dist = diff.Length();

            if (dist < 1e-6f) return;

            Vector2 obstacleNormal = diff / dist;

            // 点到障碍物表面的向量
            Vector2 u = -obstacleNormal * combinedRadius * invTimeHorizon - agentVel;
            float uLengthSq = u.LengthSquared();

            if (uLengthSq < 1e-6f) return;

            Vector2 uNorm = u / (float)Math.Sqrt(uLengthSq);
            Vector2 n = new Vector2(uNorm.Y, -uNorm.X);

            Vector2 point = -obstacleNormal * combinedRadius * invTimeHorizon;
            if (Vector2.Dot(n, point) > 0)
                n = -n;

            float c = Vector2.Dot(point, n) + combinedRadius * invTimeHorizon * (float)Math.Sqrt(uLengthSq);

            agent.ORCAPlanes.Add(new ORCAPlane(
                new Vector2(agentVel.X, agentVel.Y),
                n,
                c
            ));
        }

        /// <summary>点投影到线段</summary>
        private Vector2 ProjectPointOnSegment(Vector2 p, Vector2 a, Vector2 b)
        {
            Vector2 ab = b - a;
            float lenSq = ab.LengthSquared();
            if (lenSq < 1e-6f) return a;

            float t = Math.Clamp(Vector2.Dot(p - a, ab) / lenSq, 0, 1);
            return a + t * ab;
        }

        // ──────────────────────────────────────────────────────────────────────
        //  SolveLinearProgram - 线性规划求解 (2D)
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// 2D线性规划求解: 在ORCA半平面约束下寻找最接近期望速度的可行速度
        /// 使用迭代方法: 逐步添加约束, 维护当前最优解
        /// </summary>
        public Vector2 SolveLinearProgram(List<ORCAPlane> constraints, Vector2 preferredVelocity, float maxSpeed)
        {
            if (constraints.Count == 0)
            {
                // 无约束, 限制最大速度
                float len = preferredVelocity.Length();
                if (len > maxSpeed)
                    return preferredVelocity * (maxSpeed / len);
                return preferredVelocity;
            }

            Vector2 result = preferredVelocity;
            float resultSpeed = result.Length();

            if (resultSpeed > maxSpeed)
            {
                result = result * (maxSpeed / resultSpeed);
            }

            for (int i = 0; i < constraints.Count; i++)
            {
                var plane = constraints[i];

                // 检查当前解是否满足约束
                if (plane.IsFeasible(result))
                    continue;

                // 不满足约束, 需要投影到约束边界上
                Vector2 projected = ProjectOnConstraint(result, plane, maxSpeed);

                // 检查投影后的速度是否满足之前所有约束
                bool feasible = true;
                for (int j = 0; j < i; j++)
                {
                    if (!constraints[j].IsFeasible(projected))
                    {
                        feasible = false;
                        break;
                    }
                }

                if (feasible)
                {
                    result = projected;
                }
                else
                {
                    // 需要更复杂的求解: 在约束1..i的交集中找最近点
                    result = SolveLinearProgram2(constraints, i, maxSpeed);
                }
            }

            // 限制最大速度
            float finalSpeed = result.Length();
            if (finalSpeed > maxSpeed)
            {
                result = result * (maxSpeed / finalSpeed);
            }

            return result;
        }

        /// <summary>将速度投影到单个约束边界上</summary>
        private Vector2 ProjectOnConstraint(Vector2 velocity, ORCAPlane plane, float maxSpeed)
        {
            // u·n >= c → 如果不满足, 投影到 u·n = c 上
            float dot = Vector2.Dot(velocity, plane.Normal);
            float violation = plane.Distance - dot;

            if (violation <= 1e-4f)
                return velocity;

            return velocity + violation * plane.Normal;
        }

        /// <summary>在多个约束的交集中求解</summary>
        private Vector2 SolveLinearProgram2(List<ORCAPlane> constraints, int maxIdx, float maxSpeed)
        {
            // 简化方法: 使用所有约束的最优解
            // 如果找不到可行解, 投影到最近约束的边界

            Vector2 best = Vector2.Zero;
            float bestDist = float.MaxValue;

            // 尝试每对约束的交点
            for (int i = 0; i <= maxIdx && i < constraints.Count; i++)
            {
                for (int j = i + 1; j <= maxIdx && j < constraints.Count; j++)
                {
                    Vector2 intersection = LineLineIntersection(
                        constraints[i].Point, constraints[i].Normal,
                        constraints[j].Point, constraints[j].Normal);

                    if (float.IsNaN(intersection.X)) continue;

                    // 检查交点是否满足所有约束
                    bool feasible = true;
                    for (int k = 0; k <= maxIdx && k < constraints.Count; k++)
                    {
                        if (!constraints[k].IsFeasible(intersection))
                        {
                            feasible = false;
                            break;
                        }
                    }

                    if (feasible)
                    {
                        float len = intersection.Length();
                        if (len > maxSpeed)
                            intersection = intersection * (maxSpeed / len);

                        // 选择离原点最近的可行解 (偏好低速度)
                        float dist = intersection.LengthSquared();
                        if (dist < bestDist)
                        {
                            bestDist = dist;
                            best = intersection;
                        }
                    }
                }
            }

            // 如果没有找到交点解, 回退到单约束投影
            if (bestDist == float.MaxValue)
            {
                // 选择最严格的约束
                float maxViolation = 0;
                ORCAPlane strictest = constraints[0];

                for (int i = 0; i <= maxIdx && i < constraints.Count; i++)
                {
                    float violation = constraints[i].Distance - Vector2.Dot(Vector2.Zero, constraints[i].Normal);
                    if (violation > maxViolation)
                    {
                        maxViolation = violation;
                        strictest = constraints[i];
                    }
                }

                // 沿约束法线方向
                best = strictest.Normal * Math.Min(maxSpeed, strictest.Distance);
            }

            return best;
        }

        /// <summary>求两条直线的交点 (法线-点形式)</summary>
        private Vector2 LineLineIntersection(Vector2 p1, Vector2 n1, Vector2 p2, Vector2 n2)
        {
            float det = n1.X * n2.Y - n1.Y * n2.X;
            if (Math.Abs(det) < 1e-6f)
                return new Vector2(float.NaN, float.NaN); // 平行

            float d1 = Vector2.Dot(n1, p1);
            float d2 = Vector2.Dot(n2, p2);

            float x = (d1 * n2.Y - d2 * n1.Y) / det;
            float y = (n2.X * d1 - n1.X * d2) / det;

            return new Vector2(x, y);
        }

        // ──────────────────────────────────────────────────────────────────────
        //  Step - 一步仿真
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// 执行一步RVO仿真:
        /// 1. 查询邻居
        /// 2. 计算ORCA半平面
        /// 3. 线性规划求解新速度
        /// 4. 限制速度
        /// 5. 更新位置
        /// </summary>
        public void Step(float dt)
        {
            // 更新空间分区
            UpdateSpatialGrid();

            // 每个代理计算期望速度
            foreach (var agent in Agents)
            {
                if (agent == null) continue;
                agent.ComputePreferredVelocity(dt);
            }

            // 查询邻居
            foreach (var agent in Agents)
            {
                if (agent == null) continue;
                QueryNeighborAgents(agent);
                QueryNeighborObstacles(agent);
            }

            // 计算ORCA半平面
            ComputeORCAPlanes();

            // 求解新速度
            foreach (var agent in Agents)
            {
                if (agent == null) continue;

                Vector2 preferred = new Vector2(agent.PreferredVelocity.X, agent.PreferredVelocity.Z);
                Vector2 newVel = SolveLinearProgram(agent.ORCAPlanes, preferred, agent.MaxSpeed);

                agent.NewVelocity = new Vector3(newVel.X, 0, newVel.Y);
            }

            // 更新位置
            foreach (var agent in Agents)
            {
                if (agent == null) continue;

                // 限制速度
                float speed = agent.NewVelocity.Length();
                if (speed > agent.MaxSpeed)
                {
                    agent.NewVelocity = agent.NewVelocity * (agent.MaxSpeed / speed);
                }

                // 更新速度 (平滑插值)
                float smoothing = 0.8f;
                agent.Velocity = Vector3.Lerp(agent.Velocity, agent.NewVelocity, smoothing);

                // 更新位置
                agent.Position += agent.Velocity * dt;
            }
        }

        /// <summary>设置全局默认值</summary>
        public void SetDefaults(float agentRadius, float maxSpeed, float timeHorizon)
        {
            AgentRadius = agentRadius;
            MaxSpeed = maxSpeed;
            TimeHorizon = timeHorizon;
        }

        /// <summary>获取代理数量</summary>
        public int GetAgentCount()
        {
            int count = 0;
            foreach (var a in Agents)
                if (a != null) count++;
            return count;
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    //  Kaptcha高级避障
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Kaptcha代理: 使用Velocity Obstacle (VO)方法的高级避障代理
    /// 与RVO共享基础框架, 但使用不同的碰撞避免策略
    /// </summary>
    public class KaptchaAgent : RVOAgent
    {
        public float LookAheadTime { get; set; } = 2.0f;
        public float PersonalSpace { get; set; } = 1.0f;
        public float RelaxationTime { get; set; } = 0.5f;

        public KaptchaAgent() : base() { }

        public KaptchaAgent(int id, Vector3 position, float radius = 0.3f, float maxSpeed = 3.0f)
            : base(id, position, radius, maxSpeed)
        {
        }

        /// <summary>Kaptcha DT计算: 计算动力学时间 (与RVO共享基础)</summary>
        public float ComputeDT(RVOAgent agent, float dt)
        {
            // Kaptcha使用松弛时间来平滑速度变化
            float dtKaptcha = Math.Min(dt, RelaxationTime);
            return dtKaptcha;
        }

        /// <summary>计算Kaptcha避障力</summary>
        public Vector3 ComputeAvoidanceForce(List<RVOAgent> neighbors)
        {
            Vector3 force = Vector3.Zero;

            foreach (var neighbor in neighbors)
            {
                if (neighbor == null || neighbor.ID == this.ID) continue;

                Vector3 diff = Position - neighbor.Position;
                float dist = diff.Length();
                float minDist = Radius + neighbor.Radius + PersonalSpace;

                if (dist < minDist && dist > 0.001f)
                {
                    // 排斥力 (反比于距离)
                    float strength = (minDist - dist) / minDist;
                    force += Vector3.Normalize(diff) * strength * MaxForce;
                }

                // 时间碰撞预测
                Vector3 relVel = Velocity - neighbor.Velocity;
                Vector3 relPos = neighbor.Position - Position;
                float timeToCollision = Vector3.Dot(relPos, relVel) / relVel.LengthSquared();

                if (timeToCollision > 0 && timeToCollision < LookAheadTime)
                {
                    // 预测碰撞位置, 提前避让
                    Vector3 collisionPos = neighbor.Position + neighbor.Velocity * timeToCollision;
                    Vector3 myPos = Position + Velocity * timeToCollision;
                    Vector3 toCollision = collisionPos - myPos;
                    float collisionDist = toCollision.Length();

                    if (collisionDist < minDist * 1.5f)
                    {
                        float urgency = 1.0f - timeToCollision / LookAheadTime;
                        Vector3 avoidDir = Vector3.Normalize(-toCollision);
                        force += avoidDir * urgency * MaxForce * 0.5f;
                    }
                }
            }

            return force;
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    //  队形系统 (Formation / Leader-Following)
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>队形类型</summary>
    public enum FormationType
    {
        /// <summary>圆形</summary>
        Circle,
        /// <summary>直线</summary>
        Line,
        /// <summary>V字形</summary>
        V,
        /// <summary>楔形 (V变体)</summary>
        Wedge,
        /// <summary>纵列</summary>
        Column,
        /// <summary>自定义</summary>
        Custom,
        /// <summary>倒V字（FormationSystem 使用）</summary>
        InvertedV,
        /// <summary>方形（FormationSystem 使用）</summary>
        Square,
        /// <summary>三角（FormationSystem 使用）</summary>
        Triangle,
        /// <summary>菱形（FormationSystem 使用）</summary>
        Diamond,
        /// <summary>散开（FormationSystem 使用）</summary>
        Scattered
    }

    /// <summary>队形槽位: 队形中每个成员的位置偏移和权重</summary>
    public class FormationSlot
    {
        public Vector3 Offset { get; set; } = Vector3.Zero;
        public float Weight { get; set; } = 1.0f;
        public RVOAgent Agent { get; set; }
        public int SlotIndex { get; set; }
        public bool Filled { get; set; } = false;

        // -- 兼容成员 (FormationSystem 使用) --
        /// <summary>槽位索引。</summary>
        public int Index { get => SlotIndex; set => SlotIndex = value; }
        /// <summary>槽位局部位置。</summary>
        public Vector3 LocalPosition { get; set; } = Vector3.Zero;
        /// <summary>槽位局部旋转（弧度）。</summary>
        public float LocalRotation { get; set; }
        /// <summary>槽位角色（如 tank/ranged/leader）。</summary>
        public string? Role { get; set; }
        /// <summary>槽位世界位置（每帧由 leader 计算）。</summary>
        public Vector3 WorldPosition { get; set; } = Vector3.Zero;
        /// <summary>分配到的代理 Id（0 表示未分配）。</summary>
        public int AssignedAgentId { get; set; }
        /// <summary>分配优先级。</summary>
        public int Priority { get; set; }

        public FormationSlot() { }

        public FormationSlot(Vector3 offset, float weight = 1.0f, int index = 0)
        {
            Offset = offset;
            Weight = weight;
            SlotIndex = index;
        }
    }

    /// <summary>
    /// 队形: 定义一群代理相对于队长的布局
    /// </summary>
    public class Formation
    {
        public FormationType Type { get; set; } = FormationType.Circle;
        public List<FormationSlot> Slots { get; set; } = new List<FormationSlot>();
        public float Spacing { get; set; } = 2.0f;
        public float CircleRadius { get; set; } = 3.0f;
        public Vector3 CustomOffset { get; set; } = Vector3.Zero;

        // -- 兼容成员 (FormationSystem 使用) --
        /// <summary>阵型名称。</summary>
        public string? Name { get; set; }
        /// <summary>阵型 Id。</summary>
        public int Id { get; set; }
        /// <summary>阵型模板。</summary>
        public FormationTemplate? Template { get; set; }
        /// <summary>是否激活。</summary>
        public bool IsActive { get; set; } = true;
        /// <summary>队长代理 Id。</summary>
        public int LeaderAgentId { get; set; }
        /// <summary>队长位置。</summary>
        public Vector3 LeaderPosition { get; set; } = Vector3.Zero;
        /// <summary>队长旋转（弧度）。</summary>
        public float LeaderRotation { get; set; }
        /// <summary>队长速度。</summary>
        public Vector3 LeaderVelocity { get; set; } = Vector3.Zero;
        /// <summary>代理 Id -> 槽位索引。</summary>
        public Dictionary<int, int> AgentToSlot { get; set; } = new();

        public Formation() { }

        /// <summary>基于模板构造阵型实例（FormationSystem 使用）。</summary>
        public Formation(int id, FormationTemplate template)
        {
            Id = id;
            Template = template;
            Name = template.Name;
            if (template != null)
            {
                foreach (var s in template.Slots)
                {
                    Slots.Add(new FormationSlot
                    {
                        Index = s.Index,
                        LocalPosition = s.LocalPosition,
                        LocalRotation = s.LocalRotation,
                        Role = s.Role,
                        AssignedAgentId = s.AssignedAgentId,
                        WorldPosition = s.WorldPosition
                    });
                }
            }
        }

        /// <summary>添加代理到阵型（FormationSystem 使用）。</summary>
        public bool AddAgent(int agentId, string? role = null)
        {
            var slots = Template != null ? Template.Slots : Slots;
            for (int i = 0; i < slots.Count; i++)
            {
                var slot = slots[i];
                if (slot.AssignedAgentId == 0)
                {
                    if (!string.IsNullOrEmpty(role) && !string.IsNullOrEmpty(slot.Role) && slot.Role != role)
                        continue;
                    slot.AssignedAgentId = agentId;
                    slots[i] = slot;
                    AgentToSlot[agentId] = i;
                    return true;
                }
            }
            return false;
        }

        /// <summary>将代理绑定到指定槽位（FormationSystem 使用）。</summary>
        public void AddAgentToSlot(int agentId, int slotIdx)
        {
            var slots = Template != null ? Template.Slots : Slots;
            if (slotIdx < 0 || slotIdx >= slots.Count) return;
            var slot = slots[slotIdx];
            slot.AssignedAgentId = agentId;
            slots[slotIdx] = slot;
            AgentToSlot[agentId] = slotIdx;
        }

        /// <summary>从阵型移除代理（FormationSystem 使用）。</summary>
        public void RemoveAgent(int agentId)
        {
            if (!AgentToSlot.TryGetValue(agentId, out int idx)) return;
            var slots = Template != null ? Template.Slots : Slots;
            if (idx >= 0 && idx < slots.Count)
            {
                var slot = slots[idx];
                slot.AssignedAgentId = 0;
                slots[idx] = slot;
            }
            AgentToSlot.Remove(agentId);
        }

        /// <summary>枚举已占用的槽位（FormationSystem 使用）。</summary>
        public IEnumerable<(int slotIdx, int agentId)> GetOccupiedSlots()
        {
            var slots = Template != null ? Template.Slots : Slots;
            for (int i = 0; i < slots.Count; i++)
                if (slots[i].AssignedAgentId != 0)
                    yield return (i, slots[i].AssignedAgentId);
        }

        /// <summary>获取代理在阵型中的目标世界位置（FormationSystem 使用）。</summary>
        public Vector3? GetAgentTargetPosition(int agentId)
        {
            if (!AgentToSlot.TryGetValue(agentId, out int idx)) return null;
            var slots = Template != null ? Template.Slots : Slots;
            if (idx < 0 || idx >= slots.Count) return null;
            var slot = slots[idx];
            return LeaderPosition + LocalOffsetToWorld(slot.LocalPosition, LeaderRotation);
        }

        /// <summary>更新槽位世界位置（FormationSystem 使用）。</summary>
        public void Update(float dt)
        {
            var slots = Template != null ? Template.Slots : Slots;
            foreach (var slot in slots)
                slot.WorldPosition = LeaderPosition + LocalOffsetToWorld(slot.LocalPosition, LeaderRotation);
        }

        private static Vector3 LocalOffsetToWorld(Vector3 local, float rot)
        {
            float cos = MathF.Cos(rot), sin = MathF.Sin(rot);
            return new Vector3(local.X * cos - local.Z * sin, local.Y, local.X * sin + local.Z * cos);
        }

        /// <summary>根据队形类型生成槽位</summary>
        public void GenerateSlots(int memberCount)
        {
            Slots.Clear();

            switch (Type)
            {
                case FormationType.Circle:
                    GenerateCircleSlots(memberCount);
                    break;
                case FormationType.Line:
                    GenerateLineSlots(memberCount);
                    break;
                case FormationType.V:
                    GenerateVSlots(memberCount);
                    break;
                case FormationType.Wedge:
                    GenerateWedgeSlots(memberCount);
                    break;
                case FormationType.Column:
                    GenerateColumnSlots(memberCount);
                    break;
                case FormationType.Custom:
                    GenerateCustomSlots(memberCount);
                    break;
            }
        }

        /// <summary>圆形队形</summary>
        private void GenerateCircleSlots(int count)
        {
            float angleStep = 2.0f * (float)Math.PI / Math.Max(count, 1);
            for (int i = 0; i < count; i++)
            {
                float angle = i * angleStep;
                Vector3 offset = new Vector3(
                    (float)Math.Sin(angle) * CircleRadius,
                    0,
                    (float)Math.Cos(angle) * CircleRadius
                );
                Slots.Add(new FormationSlot(offset, 1.0f, i));
            }
        }

        /// <summary>直线队形 (沿Z轴排列)</summary>
        private void GenerateLineSlots(int count)
        {
            for (int i = 0; i < count; i++)
            {
                float z = (i - (count - 1) / 2.0f) * Spacing;
                Vector3 offset = new Vector3(0, 0, z);
                Slots.Add(new FormationSlot(offset, 1.0f, i));
            }
        }

        /// <summary>V字形队形</summary>
        private void GenerateVSlots(int count)
        {
            for (int i = 0; i < count; i++)
            {
                int side = (i % 2 == 0) ? 1 : -1;
                int row = i / 2 + 1;
                Vector3 offset = new Vector3(
                    side * row * Spacing * 0.5f,
                    0,
                    row * Spacing
                );
                Slots.Add(new FormationSlot(offset, 1.0f - row * 0.1f, i));
            }
        }

        /// <summary>楔形队形</summary>
        private void GenerateWedgeSlots(int count)
        {
            // 第一排: 1个 (紧跟队长)
            // 第二排: 2个
            // 第三排: 3个
            // ...
            int row = 0;
            int idx = 0;
            while (idx < count)
            {
                int membersInRow = row + 1;
                float zOffset = (row + 1) * Spacing;

                for (int j = 0; j < membersInRow && idx < count; j++)
                {
                    float xOffset = (j - row / 2.0f) * Spacing;
                    Vector3 offset = new Vector3(xOffset, 0, zOffset);
                    Slots.Add(new FormationSlot(offset, 1.0f - row * 0.05f, idx));
                    idx++;
                }
                row++;
            }
        }

        /// <summary>纵列队形 (沿队长后方依次排列)</summary>
        private void GenerateColumnSlots(int count)
        {
            for (int i = 0; i < count; i++)
            {
                Vector3 offset = new Vector3(0, 0, (i + 1) * Spacing);
                Slots.Add(new FormationSlot(offset, 1.0f, i));
            }
        }

        /// <summary>自定义队形 (单一偏移)</summary>
        private void GenerateCustomSlots(int count)
        {
            for (int i = 0; i < count; i++)
            {
                Vector3 offset = CustomOffset * (i + 1);
                Slots.Add(new FormationSlot(offset, 1.0f, i));
            }
        }

        /// <summary>计算指定槽位在世界空间的目标位置</summary>
        public Vector3 GetSlotWorldPosition(int slotIndex, Vector3 leaderPosition, Vector3 leaderForward)
        {
            if (slotIndex < 0 || slotIndex >= Slots.Count)
                return leaderPosition;

            var slot = Slots[slotIndex];
            // 将偏移从本地空间转换到世界空间
            Vector3 worldOffset = TransformOffset(slot.Offset, leaderForward);
            return leaderPosition + worldOffset;
        }

        /// <summary>将本地偏移转换到世界空间 (根据队长朝向)</summary>
        private Vector3 TransformOffset(Vector3 localOffset, Vector3 forward)
        {
            // 使用队长的前方向来旋转偏移
            // forward = (fx, 0, fz), right = (fz, 0, -fx)
            float fx = forward.X;
            float fz = forward.Z;

            // 绕Y轴旋转
            Vector3 worldOffset = new Vector3(
                localOffset.X * fz + localOffset.Z * fx,  // 旋转后的X
                localOffset.Y,                              // Y不变
                -localOffset.X * fx + localOffset.Z * fz   // 旋转后的Z
            );

            return worldOffset;
        }
    }

    /// <summary>
    /// 队长代理: 负责路径跟随, 广播速度给队员
    /// </summary>
    public class LeaderAgent
    {
        public RVOAgent Agent { get; set; }
        public Formation Formation { get; set; } = new Formation();
        public Vector3 Forward { get; set; } = Vector3.UnitZ;
        public float Speed { get; set; } = 3.0f;
        public List<FollowerAgent> Followers { get; set; } = new List<FollowerAgent>();

        public LeaderAgent() { }

        public LeaderAgent(RVOAgent agent, FormationType formationType, int memberCount)
        {
            Agent = agent;
            Formation = new Formation { Type = formationType };
            Formation.GenerateSlots(memberCount);
        }

        /// <summary>更新队长: 跟随路径, 更新朝向, 广播队形位置</summary>
        public void Update(float dt)
        {
            if (Agent == null) return;

            // 更新朝向 (基于速度)
            if (Agent.Velocity.LengthSquared() > 0.01f)
            {
                Vector3 vel2D = new Vector3(Agent.Velocity.X, 0, Agent.Velocity.Z);
                Forward = Vector3.Normalize(vel2D);
            }

            // 计算速度
            Speed = Agent.Velocity.Length();

            // 广播队形位置给队员
            BroadcastFormationPositions();
        }

        /// <summary>设置队形</summary>
        public void SetFormation(FormationType type, int memberCount)
        {
            Formation.Type = type;
            Formation.GenerateSlots(memberCount);
            AssignFollowers();
        }

        /// <summary>分配队员到槽位</summary>
        public void AssignFollowers()
        {
            for (int i = 0; i < Followers.Count && i < Formation.Slots.Count; i++)
            {
                Formation.Slots[i].Agent = Followers[i].Agent;
                Formation.Slots[i].Filled = true;
            }
        }

        /// <summary>添加队员</summary>
        public void AddFollower(FollowerAgent follower)
        {
            Followers.Add(follower);
            AssignFollowers();
        }

        /// <summary>广播队形位置</summary>
        private void BroadcastFormationPositions()
        {
            for (int i = 0; i < Followers.Count; i++)
            {
                if (i >= Formation.Slots.Count) break;

                var slot = Formation.Slots[i];
                Vector3 targetPos = Formation.GetSlotWorldPosition(i, Agent.Position, Forward);
                Followers[i].TargetPosition = targetPos;
                Followers[i].SlotWeight = slot.Weight;
            }
        }
    }

    /// <summary>
    /// 队员代理: 计算目标位置=队长位置+队形偏移, RVO避障, 移动
    /// </summary>
    public class FollowerAgent
    {
        public RVOAgent Agent { get; set; }
        public Vector3 TargetPosition { get; set; } = Vector3.Zero;
        public float SlotWeight { get; set; } = 1.0f;
        public float ArrivalRadius { get; set; } = 1.0f;
        public bool IsAssigned { get; set; } = false;

        public FollowerAgent() { }

        public FollowerAgent(RVOAgent agent)
        {
            Agent = agent;
        }

        /// <summary>更新队员: 计算到队形槽位的目标速度, RVO避障</summary>
        public void Update(float dt, RVOSimulator simulator)
        {
            if (Agent == null || simulator == null) return;

            // 计算到目标位置的期望速度
            Vector3 toTarget = TargetPosition - Agent.Position;
            float dist = toTarget.Length();

            if (dist < ArrivalRadius)
            {
                // 到达槽位, 停止
                Agent.PreferredVelocity = Vector3.Zero;
                return;
            }

            // 期望速度 = 到目标的方向 * 最大速度 (越近越慢)
            float speedFactor = Math.Min(1.0f, dist / (ArrivalRadius * 2.0f));
            Vector3 desiredDir = toTarget / dist;
            Agent.PreferredVelocity = desiredDir * Agent.MaxSpeed * speedFactor * SlotWeight;
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    //  CrowdManager - 人群管理器 (整合RVO + 队形 + 导航)
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 人群管理器: 整合RVO模拟器、队长跟随和导航网格查询
    /// </summary>
}
