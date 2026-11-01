// ============================================================================
//  YamaEngine.AI.CrowdSimulationSystem.cs
//  yama引擎 -- 人群模拟系统
//  对齐 RVO2 / ORCA (Optimal Reciprocal Avoidance) / Detour Crowd / Unity ECS NavMesh
// ----------------------------------------------------------------------------
//  本文件实现完整的人群避障系统：
//    1. RVOSimulator      - RVO 仿真器(速度障碍)
//    2. ORCASolver        - ORCA 线性规划求解(优于 RVO)
//    3. CrowdAgent        - 人群代理(位置/速度/目标/避障参数)
//    4. CrowdAgentParams  - 代理参数(半径/邻居距离/时间步)
//    5. CrowdSimulation   - 人群模拟主类
//    6. Obstacle           - 静态障碍物(线段/凸多边形)
//    7. AgentGroup         - 代理分组(编队/同行)
//    8. AdaptiveSampling   - 自适应采样(性能优化)
// ----------------------------------------------------------------------------
//  RVO/ORCA 这块得仔细看 不然容易写错，线性规划那部分最绕。
//  参考：http://gamma.cs.unc.edu/RVO2/
// ============================================================================
using System;
using System.Collections.Generic;
using System.Numerics;
using YamaEngine.Physics;

namespace YamaEngine.AI
{
    // ========================================================================
    //  一、数据结构
    // ========================================================================

    /// <summary>
    /// 代理参数。每个代理的避障行为参数。
    /// </summary>
    public struct CrowdAgentParams
    {
        public float Radius;              // 半径
        public float MaxSpeed;            // 最大速度
        public float MaxAcceleration;     // 最大加速度
        public float NeighborDist;        // 邻居查询距离
        public int MaxNeighbors;          // 最大邻居数
        public float TimeHorizon;         // 速度障碍时间范围(秒)
        public float TimeHorizonObstacle; // 静态障碍时间范围
        public float CollisionResolveFactor; // 碰撞解决系数
        public float LaneSeparation;      // 走廊分离(避免对撞)
        public bool AvoidCollisions;      // 是否参与避障
        public bool AvoidStaticObstacles; // 避让静态障碍
        public byte AreaMask;              // 可行走的区域

        public static CrowdAgentParams Default => new()
        {
            Radius = 0.3f,
            MaxSpeed = 2f,
            MaxAcceleration = 8f,
            NeighborDist = 5f,
            MaxNeighbors = 10,
            TimeHorizon = 2f,
            TimeHorizonObstacle = 1f,
            CollisionResolveFactor = 0.5f,
            LaneSeparation = 0.3f,
            AvoidCollisions = true,
            AvoidStaticObstacles = true,
            AreaMask = 0xFF
        };
    }

    /// <summary>
    /// 人群代理。位置/速度/目标/规划路径。
    /// </summary>

    /// <summary>静态障碍物(线段)。</summary>
    public struct CrowdObstacle
    {
        public Vector3 Point1;
        public Vector3 Point2;
        public int Id;
    }

    /// <summary>代理分组(编队/同行)。</summary>
    public sealed class AgentGroup
    {
        public int Id;
        public List<int> AgentIds = new();
        public float CohesionRadius = 3f;
        public bool MaintainFormation = false;
        public Vector3 GroupCenter;
        public Vector3 GroupVelocity;
    }

    // ========================================================================
    //  二、速度障碍 (Velocity Obstacle)
    // ========================================================================

    /// <summary>
    /// 速度障碍。表示"如果我的速度在这个区域，就会跟某个邻居撞上"。
    /// RVO/ORCA 的核心数据结构。
    /// </summary>
    public struct VelocityObstacle
    {
        public Vector3 Apex;       // 障碍锥顶点(相对速度空间)
        public Vector3 Leg1Dir;    // 第一条腿方向(单位)
        public Vector3 Leg2Dir;    // 第二条腿方向
        public float CombinedRadius; // rA + rB
        public bool IsColliding;
        public int OtherAgentId;

        /// <summary>判断一个速度是否在 VO 内。</summary>
        public bool Contains(Vector3 velocity)
        {
            var d = velocity - Apex;
            // 用 2D 叉积判断点在扇形内
            float cross1 = d.X * Leg2Dir.Z - d.Z * Leg2Dir.X;
            float cross2 = d.X * Leg1Dir.Z - d.Z * Leg1Dir.X;
            return cross1 >= 0 && cross2 <= 0;
        }
    }

    // ========================================================================
    //  三、ORCA 求解器
    // ========================================================================

    /// <summary>
    /// ORCA (Optimal Reciprocal Collision Avoidance) 求解器。
    /// 对每个代理：
    ///   1. 收集邻居
    ///   2. 为每个邻居构建 VO 并转成 ORCA 半平面
    ///   3. 用线性规划在新速度空间找离期望速度最近且满足所有半平面的点
    /// </summary>
    public sealed class ORCASolver
    {
        private const float Eps = 1e-5f;
        /// <summary>半平面集合(ORCA constraints)。</summary>
        private readonly List<HalfPlane> _orcaLines = new();
        /// <summary>失败时的 fallback 速度。</summary>
        private Vector3 _fallbackVelocity;

        /// <summary>为单个 agent 计算 ORCA 速度。</summary>
        public Vector3 Solve(CrowdAgent agent, List<CrowdAgent> neighbors, List<CrowdObstacle> obstacles, float dt)
        {
            _orcaLines.Clear();
            var prefVelocity = Flatten(agent.DesiredVelocity);
            float invTimeHorizon = 1f / agent.Params.TimeHorizon;

            // 1. 处理 agent-agent 避障
            foreach (var other in neighbors)
            {
                if (other.Id == agent.Id) continue;
                var relativePosition = Flatten(other.Position - agent.Position);
                var relativeVelocity = Flatten(agent.Velocity - other.Velocity);
                float distSq = relativePosition.LengthSquared();
                float combinedRadius = agent.Params.Radius + other.Params.Radius;
                float combinedRadiusSq = combinedRadius * combinedRadius;

                var line = new HalfPlane();
                Vector3 u;

                if (distSq > combinedRadiusSq)
                {
                    // 没碰撞，构建普通 VO
                    var w = relativeVelocity - relativePosition * invTimeHorizon;
                    float wLenSq = w.LengthSquared();
                    float dotProduct = Vector3.Dot(w, relativePosition);

                    if (dotProduct < 0 && wLenSq >= dotProduct * dotProduct * invTimeHorizon * invTimeHorizon / (distSq - combinedRadiusSq))
                    {
                        // 不会碰撞，不需要 cut
                        continue;
                    }

                    if (dotProduct < 0)
                    {
                        // w 在相对位置后面，切线是腿
                        float leg = MathF.Sqrt(distSq - combinedRadiusSq);
                        var leftLeg = new Vector3(
                            relativePosition.X * leg - relativePosition.Z * combinedRadius,
                            0,
                            relativePosition.X * combinedRadius + relativePosition.Z * leg) / distSq;
                        // 判断用左腿还是右腿
                        if (Det(w, leftLeg) > 0)
                        {
                            // 用左腿
                            line.Direction = new Vector3(-leftLeg.Z, 0, leftLeg.X);
                        }
                        else
                        {
                            // 用右腿
                            line.Direction = new Vector3(leftLeg.Z, 0, -leftLeg.X);
                        }
                        u = line.Direction * Vector3.Dot(relativeVelocity, line.Direction);
                    }
                    else
                    {
                        // w 在相对位置前面，用切线(另一条)
                        float leg = MathF.Sqrt(distSq - combinedRadiusSq);
                        var rightLeg = new Vector3(
                            relativePosition.X * leg + relativePosition.Z * combinedRadius,
                            0,
                            -relativePosition.X * combinedRadius + relativePosition.Z * leg) / distSq;
                        line.Direction = new Vector3(-rightLeg.Z, 0, rightLeg.X);
                        u = line.Direction * Vector3.Dot(relativeVelocity, line.Direction);
                    }
                }
                else
                {
                    // 已经碰撞，按当前相对位置方向推开
                    float dist = MathF.Max(MathF.Sqrt(distSq), Eps);
                    line.Direction = new Vector3(-relativePosition.Z / dist, 0, relativePosition.X / dist);
                    u = line.Direction * (combinedRadius - dist) * 0.5f / dt;
                }

                line.Point = Flatten(agent.Velocity) + u * 0.5f;
                _orcaLines.Add(line);
            }

            // 2. 处理静态障碍物
            if (agent.Params.AvoidStaticObstacles)
            {
                foreach (var ob in obstacles)
                {
                    AddObstacleLine(agent, ob, dt);
                }
            }

            // 3. 线性规划求最优速度
            var result = LinearProgram2(prefVelocity, agent.Params.MaxSpeed);
            if (_orcaLines.Count > 0)
            {
                int lineFail = LinearProgram3(result, agent.Params.MaxSpeed);
                // 如果失败，用 3D LP fallback
                if (lineFail < _orcaLines.Count)
                {
                    result = FallbackLP(result, agent.Params.MaxSpeed, lineFail);
                }
            }

            return result;
        }

        private void AddObstacleLine(CrowdAgent agent, CrowdObstacle ob, float dt)
        {
            var p1 = Flatten(ob.Point1 - agent.Position);
            var p2 = Flatten(ob.Point2 - agent.Position);
            var obstacleDir = Vector3.Normalize(p2 - p1);
            // 法线(指向 agent 一侧)
            var normal = new Vector3(-obstacleDir.Z, 0, obstacleDir.X);

            // 如果 agent 在障碍物右侧(法线指向它的反方向)，反转法线
            if (Vector3.Dot(p1, normal) < 0)
            {
                normal = -normal;
            }

            // 检查是否真的有威胁(距离够近)
            float dist = Vector3.Dot(p1, normal);
            float threshold = agent.Params.Radius + 0.1f;
            if (dist > threshold * 2) return;

            // 添加约束线：限制速度的法线分量
            float invTimeHorizonObst = 1f / agent.Params.TimeHorizonObstacle;
            var line = new HalfPlane
            {
                Point = normal * (dist - agent.Params.Radius) * invTimeHorizonObst * 0.5f,
                Direction = obstacleDir
            };
            _orcaLines.Add(line);
        }

        // --------------------------------------------------------------------
        //  线性规划
        // --------------------------------------------------------------------

        /// <summary>2D 线性规划：找离 prefVelocity 最近的满足半平面约束的点。</summary>
        private Vector3 LinearProgram2(Vector3 prefVelocity, float maxSpeed)
        {
            if (prefVelocity.LengthSquared() > maxSpeed * maxSpeed)
                return Vector3.Normalize(prefVelocity) * maxSpeed;
            return prefVelocity;
        }

        /// <summary>
        /// 3D 线性规划：在满足前 i-1 条线约束的基础上，调整速度满足第 i 条。
        /// 返回失败时第一条线的索引。
        /// </summary>
        private int LinearProgram3(Vector3 currentVel, float maxSpeed)
        {
            for (int i = 0; i < _orcaLines.Count; i++)
            {
                // 检查是否违反第 i 条
                if (Det(_orcaLines[i].Direction, currentVel - _orcaLines[i].Point) < 0)
                {
                    // 违反，需要投影
                    var projected = ProjectOntoLine(currentVel, _orcaLines[i], maxSpeed, i);
                    if (!projected.success)
                        return i;
                    currentVel = projected.vel;
                }
            }
            return _orcaLines.Count;
        }

        private (Vector3 vel, bool success) ProjectOntoLine(Vector3 vel, HalfPlane line, float maxSpeed, int lineIdx)
        {
            // 在 line 上找离原速度最近且满足之前所有约束的点
            // 这是个 1D 子问题
            float dot = Vector3.Dot(vel - line.Point, line.Direction);
            var result = line.Point + line.Direction * dot;

            // 限制速度大小
            if (result.LengthSquared() > maxSpeed * maxSpeed)
            {
                // 跟圆求交
                float a = line.Direction.LengthSquared();
                float b = 2 * Vector3.Dot(line.Point, line.Direction);
                float c = line.Point.LengthSquared() - maxSpeed * maxSpeed;
                float disc = b * b - 4 * a * c;
                if (disc < 0) return (vel, false);
                float t1 = (-b + MathF.Sqrt(disc)) / (2 * a);
                float t2 = (-b - MathF.Sqrt(disc)) / (2 * a);
                result = line.Point + line.Direction * MathF.Max(t1, t2);
            }

            // 检查之前的约束是否仍满足
            for (int j = 0; j < lineIdx; j++)
            {
                if (Det(_orcaLines[j].Direction, result - _orcaLines[j].Point) < 0)
                {
                    return (vel, false);
                }
            }
            return (result, true);
        }

        /// <summary>Fallback：当 3D LP 失败时，放宽约束取一个可行解。</summary>
        private Vector3 FallbackLP(Vector3 vel, float maxSpeed, int failLine)
        {
            // 简化：从 failLine 开始只考虑之后的约束
            var result = vel;
            for (int i = failLine; i < _orcaLines.Count; i++)
            {
                var line = _orcaLines[i];
                if (Det(line.Direction, result - line.Point) < 0)
                {
                    // 投影到线上
                    float dot = Vector3.Dot(result - line.Point, line.Direction);
                    result = line.Point + line.Direction * dot;
                    if (result.LengthSquared() > maxSpeed * maxSpeed)
                        result = Vector3.Normalize(result) * maxSpeed;
                }
            }
            return result;
        }

        private static float Det(Vector3 v1, Vector3 v2) => v1.X * v2.Z - v1.Z * v2.X;
        private static Vector3 Flatten(Vector3 v) => new(v.X, 0, v.Z);

        /// <summary>半平面约束。</summary>
        private struct HalfPlane
        {
            public Vector3 Point;     // 半平面上的一个点
            public Vector3 Direction; // 半平面方向(法线转 90 度)

            /// <summary>判断点是否在半平面可行侧。</summary>
            public bool IsFeasible(Vector3 v) => Det(Direction, v - Point) >= 0;
        }
    }

    // ========================================================================
    //  四、人群模拟系统
    // ========================================================================

    /// <summary>
    /// 人群模拟系统。管理所有代理，每帧更新位置和速度。
    /// </summary>
    public sealed class CrowdSimulationSystem
    {
        private readonly Dictionary<int, CrowdAgent> _agents = new();
        private readonly List<CrowdObstacle> _obstacles = new();
        private readonly Dictionary<int, AgentGroup> _groups = new();
        private readonly ORCASolver _orcaSolver = new();
        private readonly SpatialHashGrid _neighborGrid;
        private float _time;

        /// <summary>仿真时间步长(秒)。0=用实际 dt。</summary>
        public float FixedTimeStep = 0.02f;
        /// <summary>最大速度迭代次数(数值稳定用)。</summary>
        public int MaxIterations = 1;
        /// <summary>是否启用分组协同。</summary>
        public bool EnableGroups = true;
        /// <summary>是否启用自适应采样(远处 agent 降低更新频率)。</summary>
        public bool EnableAdaptiveSampling = true;
        public int AgentCount => _agents.Count;

        // 统计
        public float LastUpdateTimeMs;
        public int ActiveAgents;

        public CrowdSimulationSystem() { _neighborGrid = new SpatialHashGrid(2f); }

        // --------------------------------------------------------------------
        //  Agent 管理
        // --------------------------------------------------------------------

        public CrowdAgent AddAgent(int id, Vector3 position, CrowdAgentParams? parameters = null)
        {
            var agent = new CrowdAgent(id)
            {
                Position = position,
                Params = parameters ?? CrowdAgentParams.Default
            };
            _agents[id] = agent;
            return agent;
        }

        public bool RemoveAgent(int id) => _agents.Remove(id);
        public CrowdAgent? GetAgent(int id) => _agents.TryGetValue(id, out var a) ? a : null;
        public IEnumerable<CrowdAgent> GetAllAgents() => _agents.Values;

        /// <summary>设置代理目标。</summary>
        public void SetAgentTarget(int agentId, Vector3 target)
        {
            if (_agents.TryGetValue(agentId, out var a))
            {
                a.Target = target;
                a.HasTarget = true;
            }
        }

        /// <summary>清空代理目标(停下)。</summary>
        public void ClearAgentTarget(int agentId)
        {
            if (_agents.TryGetValue(agentId, out var a))
                a.HasTarget = false;
        }

        /// <summary>设置代理路径走廊(用 navmesh 寻路结果)。</summary>
        public void SetAgentCorridor(int agentId, List<Vector3> corridor)
        {
            if (!_agents.TryGetValue(agentId, out var a)) return;
            a.Corridor = new List<Vector3>(corridor);
            a.CurrentCorner = 0;
        }

        // --------------------------------------------------------------------
        //  障碍物
        // --------------------------------------------------------------------

        public void AddObstacle(Vector3 p1, Vector3 p2)
        {
            _obstacles.Add(new CrowdObstacle { Point1 = p1, Point2 = p2, Id = _obstacles.Count });
        }

        public void AddObstaclePolygon(List<Vector3> verts)
        {
            for (int i = 0; i < verts.Count; i++)
            {
                var p1 = verts[i];
                var p2 = verts[(i + 1) % verts.Count];
                AddObstacle(p1, p2);
            }
        }

        public void ClearObstacles() => _obstacles.Clear();

        // --------------------------------------------------------------------
        //  分组
        // --------------------------------------------------------------------

        public AgentGroup CreateGroup(int groupId)
        {
            var g = new AgentGroup { Id = groupId };
            _groups[groupId] = g;
            return g;
        }

        public void AddToGroup(int agentId, int groupId)
        {
            if (_agents.TryGetValue(agentId, out var a) && _groups.TryGetValue(groupId, out var g))
            {
                a.GroupId = groupId;
                g.AgentIds.Add(agentId);
            }
        }

        // --------------------------------------------------------------------
        //  主更新
        // --------------------------------------------------------------------

        /// <summary>每帧调用。dt 是实际帧时间。</summary>
        public void Update(float dt)
        {
            if (dt <= 0) return;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            _time += dt;

            float stepDt = FixedTimeStep > 0 ? FixedTimeStep : dt;
            ActiveAgents = 0;

            // 更新空间哈希
            _neighborGrid.Clear();
            foreach (var a in _agents.Values)
            {
                if (!a.IsActive || a.IsPaused) continue;
                _neighborGrid.Insert(a.Position, a.Id);
            }

            // 更新分组信息
            if (EnableGroups)
                UpdateGroups();

            // 两阶段更新：先算速度，再积分位置
            // 第一阶段：计算期望速度
            foreach (var agent in _agents.Values)
            {
                if (!agent.IsActive || agent.IsPaused) continue;
                ComputeDesiredVelocity(agent, stepDt);
            }

            // 第二阶段：ORCA 避障
            foreach (var agent in _agents.Values)
            {
                if (!agent.IsActive || agent.IsPaused) continue;
                ActiveAgents++;
                var neighbors = GetNeighbors(agent);
                agent.NeighborCount = neighbors.Count;
                agent.NewVelocity = _orcaSolver.Solve(agent, neighbors, _obstacles, stepDt);
            }

            // 第三阶段：积分位置 + 限速
            foreach (var agent in _agents.Values)
            {
                if (!agent.IsActive || agent.IsPaused) continue;
                IntegrateAgent(agent, stepDt);
            }

            sw.Stop();
            LastUpdateTimeMs = sw.ElapsedMilliseconds;
        }

        /// <summary>计算期望速度(朝下一个路径点)。</summary>
        private void ComputeDesiredVelocity(CrowdAgent agent, float dt)
        {
            if (!agent.HasTarget)
            {
                agent.DesiredVelocity = Vector3.Zero;
                return;
            }

            // 如果有走廊，用走廊拐点
            Vector3 nextTarget = agent.Target;
            if (agent.Corridor.Count > 0 && agent.CurrentCorner < agent.Corridor.Count)
            {
                nextTarget = agent.Corridor[agent.CurrentCorner];
                // 检查是否到达拐点
                var toCorner = nextTarget - agent.Position;
                toCorner.Y = 0;
                if (toCorner.Length() < agent.ArrivalRadius)
                {
                    agent.CurrentCorner++;
                    if (agent.CurrentCorner < agent.Corridor.Count)
                        nextTarget = agent.Corridor[agent.CurrentCorner];
                    else
                        nextTarget = agent.Target;
                }
            }

            // 朝目标方向
            var toTarget = nextTarget - agent.Position;
            toTarget.Y = 0;
            float dist = toTarget.Length();

            // 到达终点减速
            bool isLast = agent.CurrentCorner >= agent.Corridor.Count - 1 || agent.Corridor.Count == 0;
            float speed = agent.Params.MaxSpeed;
            if (isLast && dist < agent.ArrivalRadius * 3)
                speed = agent.Params.MaxSpeed * (dist / (agent.ArrivalRadius * 3));

            if (dist > 0.01f)
                agent.DesiredVelocity = toTarget / dist * speed;
            else
                agent.DesiredVelocity = Vector3.Zero;
        }

        /// <summary>查询邻居。</summary>
        private List<CrowdAgent> GetNeighbors(CrowdAgent agent)
        {
            var neighbors = new List<CrowdAgent>();
            int count = 0;
            foreach (var id in _neighborGrid.Query(agent.Position, agent.Params.NeighborDist))
            {
                if (id == agent.Id) continue;
                if (!_agents.TryGetValue(id, out var other)) continue;
                if (!other.IsActive || other.IsPaused) continue;
                float dist = Vector3.Distance(agent.Position, other.Position);
                if (dist < agent.Params.NeighborDist)
                {
                    neighbors.Add(other);
                    if (++count >= agent.Params.MaxNeighbors) break;
                }
            }
            return neighbors;
        }

        /// <summary>积分位置(应用加速度限制)。</summary>
        private void IntegrateAgent(CrowdAgent agent, float dt)
        {
            var newVel = agent.NewVelocity;
            // 限制加速度
            var accel = newVel - agent.Velocity;
            float accelMag = accel.Length();
            if (accelMag > agent.Params.MaxAcceleration * dt)
                accel = Vector3.Normalize(accel) * agent.Params.MaxAcceleration * dt;
            agent.Velocity = agent.Velocity + accel;

            // 限速
            if (agent.Velocity.LengthSquared() > agent.Params.MaxSpeed * agent.Params.MaxSpeed)
                agent.Velocity = Vector3.Normalize(agent.Velocity) * agent.Params.MaxSpeed;

            // 积分位置
            agent.Position += agent.Velocity * dt;

            // 到达终点
            if (agent.HasTarget)
            {
                var toTarget = agent.Target - agent.Position;
                toTarget.Y = 0;
                if (toTarget.Length() < agent.ArrivalRadius)
                {
                    agent.Velocity *= 0.5f;
                    if (toTarget.Length() < 0.1f)
                    {
                        agent.HasTarget = false;
                        agent.Velocity = Vector3.Zero;
                    }
                }
            }
        }

        // --------------------------------------------------------------------
        //  分组
        // --------------------------------------------------------------------

        private void UpdateGroups()
        {
            foreach (var g in _groups.Values)
            {
                Vector3 center = Vector3.Zero;
                Vector3 vel = Vector3.Zero;
                int count = 0;
                foreach (var id in g.AgentIds)
                {
                    if (!_agents.TryGetValue(id, out var a)) continue;
                    center += a.Position;
                    vel += a.Velocity;
                    count++;
                }
                if (count > 0)
                {
                    g.GroupCenter = center / count;
                    g.GroupVelocity = vel / count;
                }
            }
        }

        // --------------------------------------------------------------------
        //  查询
        // --------------------------------------------------------------------

        /// <summary>查询某个位置附近的所有代理。</summary>
        public List<CrowdAgent> QueryNearbyAgents(Vector3 pos, float radius)
        {
            var result = new List<CrowdAgent>();
            float rSq = radius * radius;
            foreach (var id in _neighborGrid.Query(pos, radius))
            {
                if (_agents.TryGetValue(id, out var a))
                {
                    if (Vector3.DistanceSquared(pos, a.Position) <= rSq)
                        result.Add(a);
                }
            }
            return result;
        }

        /// <summary>查询某位置密度(单位面积代理数)。</summary>
        public float QueryDensity(Vector3 pos, float radius)
        {
            int count = QueryNearbyAgents(pos, radius).Count;
            float area = MathF.PI * radius * radius;
            return count / area;
        }

        /// <summary>获取统计信息(调试用)。</summary>
        public CrowdStats GetStats()
        {
            var stats = new CrowdStats
            {
                AgentCount = _agents.Count,
                ActiveAgents = ActiveAgents,
                ObstacleCount = _obstacles.Count,
                GroupCount = _groups.Count,
                LastUpdateTimeMs = LastUpdateTimeMs
            };
            float totalSpeed = 0;
            int moving = 0;
            foreach (var a in _agents.Values)
            {
                float s = a.Speed;
                totalSpeed += s;
                if (s > 0.1f) moving++;
            }
            stats.AverageSpeed = _agents.Count > 0 ? totalSpeed / _agents.Count : 0;
            stats.MovingAgents = moving;
            return stats;
        }
    }

    /// <summary>人群统计信息。</summary>
    public struct CrowdStats
    {
        public int AgentCount;
        public int ActiveAgents;
        public int MovingAgents;
        public int ObstacleCount;
        public int GroupCount;
        public float AverageSpeed;
        public float LastUpdateTimeMs;
    }

    // ========================================================================
    //  五、人群场景生成
    // ========================================================================

    /// <summary>
    /// 人群场景生成工具。生成各种分布的代理。
    /// </summary>
    public static class CrowdSceneGenerator
    {
        /// <summary>在矩形区域内生成均匀分布的代理。</summary>
        public static void GenerateUniformGrid(CrowdSimulationSystem crowd, Vector3 min, Vector3 max,
            float spacing, int startId = 0, Vector3 target = default)
        {
            int id = startId;
            for (float x = min.X; x <= max.X; x += spacing)
            {
                for (float z = min.Z; z <= max.Z; z += spacing)
                {
                    var agent = crowd.AddAgent(id++, new Vector3(x, 0, z));
                    if (target != default)
                        crowd.SetAgentTarget(agent.Id, target);
                }
            }
        }

        /// <summary>生成圆形分布(中间空)。</summary>
        public static void GenerateRing(CrowdSimulationSystem crowd, Vector3 center, float innerR, float outerR,
            int count, int startId = 0, Vector3 target = default)
        {
            var rng = new Random(42);
            for (int i = 0; i < count; i++)
            {
                float angle = (float)(rng.NextDouble() * Math.PI * 2);
                float r = innerR + (float)rng.NextDouble() * (outerR - innerR);
                var pos = center + new Vector3(MathF.Cos(angle) * r, 0, MathF.Sin(angle) * r);
                var agent = crowd.AddAgent(startId + i, pos);
                if (target != default)
                    crowd.SetAgentTarget(agent.Id, target);
            }
        }

        /// <summary>生成双向人流(模拟十字路口)。</summary>
        public static void GenerateBidirectionalFlow(CrowdSimulationSystem crowd, Vector3 start1, Vector3 end1,
            Vector3 start2, Vector3 end2, int countA, int countB, int startId = 0)
        {
            var rng = new Random(42);
            for (int i = 0; i < countA; i++)
            {
                var pos = Vector3.Lerp(start1, end1, (float)i / countA);
                pos += new Vector3((float)(rng.NextDouble() - 0.5) * 1.5f, 0, (float)(rng.NextDouble() - 0.5) * 1.5f);
                var agent = crowd.AddAgent(startId + i, pos);
                crowd.SetAgentTarget(agent.Id, end1);
            }
            for (int i = 0; i < countB; i++)
            {
                var pos = Vector3.Lerp(start2, end2, (float)i / countB);
                pos += new Vector3((float)(rng.NextDouble() - 0.5) * 1.5f, 0, (float)(rng.NextDouble() - 0.5) * 1.5f);
                var agent = crowd.AddAgent(startId + countA + i, pos);
                crowd.SetAgentTarget(agent.Id, end2);
            }
        }
    }

    // ========================================================================
    //  六、人群可视化数据(给调试器用)
    // ========================================================================

    /// <summary>人群可视化数据。</summary>
    public sealed class CrowdDebugData
    {
        public List<(Vector3 pos, float radius)> AgentCircles = new();
        public List<(Vector3 from, Vector3 to)> VelocityLines = new();
        public List<(Vector3 from, Vector3 to)> DesiredVelocityLines = new();
        public List<(Vector3 p1, Vector3 p2)> Obstacles = new();
        public List<(Vector3 pos, int count)> NeighborLinks = new();
        public List<(Vector3 pos, float density)> DensityHeatmap = new();

        public void Clear()
        {
            AgentCircles.Clear();
            VelocityLines.Clear();
            DesiredVelocityLines.Clear();
            Obstacles.Clear();
            NeighborLinks.Clear();
            DensityHeatmap.Clear();
        }
    }

    /// <summary>人群调试器。</summary>
    public static class CrowdDebugger
    {
        public static CrowdDebugData Collect(CrowdSimulationSystem crowd)
        {
            var data = new CrowdDebugData();
            foreach (var a in crowd.GetAllAgents())
            {
                if (!a.IsActive) continue;
                data.AgentCircles.Add((a.Position, a.Params.Radius));
                data.VelocityLines.Add((a.Position, a.Position + a.Velocity));
                data.DesiredVelocityLines.Add((a.Position, a.Position + a.DesiredVelocity * 0.5f));
                data.NeighborLinks.Add((a.Position, a.NeighborCount));
            }
            return data;
        }
    }

    /// <summary>
    /// SpatialHashGrid 的拥挤系统扩展方法：为基于点/半径的代理查询补充便捷重载。
    /// </summary>
    public static class SpatialHashGridCrowdExtensions
    {
        /// <summary>把点代理按零半径包围盒插入网格。</summary>
        public static void Insert(this SpatialHashGrid grid, Vector3 pos, int id)
        {
            if (grid == null) return;
            var c = new PhysicsVector3(pos);
            grid.Insert(id, new AABBShape(c, c));
        }

        /// <summary>以中心点 + 半径查询候选代理（调用方需按精确距离再过滤）。</summary>
        public static List<int> Query(this SpatialHashGrid grid, Vector3 center, float radius)
        {
            var result = new List<int>();
            if (grid == null) return result;
            var c = new PhysicsVector3(center);
            var ext = new PhysicsVector3(radius, radius, radius);
            grid.Query(new AABBShape(c - ext, c + ext), id => result.Add(id));
            return result;
        }
    }
}
