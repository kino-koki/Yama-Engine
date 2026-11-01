// ============================================================================
//  YamaEngine.AI.SteeringSystem.cs
//  yama引擎 -- 转向系统
//  对齐 Reynolds Boids / OpenSteer / Unity NavMesh Agent steering
// ----------------------------------------------------------------------------
//  实现经典的 steering behaviors：
//    1. Seek / Flee        - 追逐/逃跑
//    2. Arrival            - 减速到达
//    3. Wander             - 随机游走
//    4. Pursuit / Evade    - 预测追击/逃避
//    5. Offset Pursuit     - 偏移追击(编队用)
//    6. Obstacle Avoidance - 障碍物避让(基于射线检测)
//    7. Wall Avoidance     - 墙壁避让(基于 whiskers)
//    8. Separation         - 群体分离
//    9. Alignment          - 群体对齐
//   10. Cohesion           - 群体聚合
//   11. Flocking           - boids 三件套合一
//   12. FlowField          - 流场跟随
//   13. PathFollowing      - 路径跟随
//   14. Queue              - 排队行为
// ----------------------------------------------------------------------------
//  注意：steering 行为的 weight 调参是门玄学，得反复试。
// ============================================================================
using System;
using System.Collections.Generic;
using System.Numerics;
using YamaEngine.Physics;

namespace YamaEngine.AI
{
    // ========================================================================
    //  一、基础数据结构
    // ========================================================================

    /// <summary>转向代理。一个能在空间移动的实体。</summary>
    public sealed class SteeringAgent
    {
        public int Id;
        public Vector3 Position;
        public Vector3 Velocity;
        public Vector3 Forward => Velocity == Vector3.Zero ? Vector3.UnitZ : Vector3.Normalize(Velocity);
        public float MaxSpeed = 5f;
        public float MaxForce = 10f;
        public float MaxAcceleration = 8f;
        /// <summary>转向速率(弧度/秒)。限制角速度。</summary>
        public float MaxTurnRate = 8f;
        public float Radius = 0.3f;
        public float Mass = 1f;
        public bool Is2D = true; // 2D 模式只在 XZ 平面计算
        public Vector3 Up = Vector3.UnitY;

        /// <summary>当前所有 steering 力的累加。</summary>
        public Vector3 SteeringForce;
        /// <summary>当前目标速度。</summary>
        public Vector3 DesiredVelocity;

        public SteeringAgent(int id) { Id = id; }

        /// <summary>限制力的大小。</summary>
        public Vector3 Truncate(Vector3 v, float max)
        {
            float len = v.Length();
            if (len > max && len > 1e-6f) return v / len * max;
            return v;
        }

        /// <summary>更新位置和速度。</summary>
        public void Integrate(float dt)
        {
            // 力转加速度
            var accel = SteeringForce / Mass;
            // 限制加速度
            accel = Truncate(accel, MaxAcceleration);
            // 速度更新
            Velocity = Truncate(Velocity + accel * dt, MaxSpeed);
            // 转向限制(2D 模式下限制水平角速度)
            if (MaxTurnRate < float.MaxValue && Velocity.LengthSquared() > 0.01f)
            {
                // TODO: 实际转向限制需要保存上一帧朝向，这里简化
            }
            // 位置更新
            Position += Velocity * dt;
            // 清空力
            SteeringForce = Vector3.Zero;
        }
    }

    /// <summary>障碍物(球体)。</summary>
    public struct SteeringObstacle
    {
        public Vector3 Center;
        public float Radius;
        public int Id;
    }

    /// <summary>墙壁(平面)。</summary>
    public struct SteeringWall
    {
        public SteeringWall() { }
        public Vector3 Normal;
        public Vector3 Point;
        public float DetectionRange = 2f;
    }

    /// <summary>steering 行为权重配置。</summary>
    public sealed class SteeringBehaviorsConfig
    {
        public float SeekWeight = 1f;
        public float FleeWeight = 1f;
        public float ArrivalWeight = 1f;
        public float WanderWeight = 0.6f;
        public float PursuitWeight = 1f;
        public float EvadeWeight = 1f;
        public float ObstacleAvoidanceWeight = 2.5f;
        public float WallAvoidanceWeight = 2f;
        public float SeparationWeight = 1.5f;
        public float AlignmentWeight = 1f;
        public float CohesionWeight = 1f;
        public float FlowFieldWeight = 1f;
        public float PathFollowWeight = 1.2f;
        public float QueueWeight = 1f;
    }

    // ========================================================================
    //  二、单个 steering behavior 接口
    // ========================================================================

    /// <summary>所有 steering behavior 的基接口。</summary>
    public interface ISteeringBehavior
    {
        Vector3 Calculate(SteeringAgent agent, float dt);
        float Weight { get; set; }
        bool Enabled { get; set; }
    }

    /// <summary>抽象基类。</summary>
    public abstract class SteeringBehaviorBase : ISteeringBehavior
    {
        public float Weight { get; set; } = 1f;
        public bool Enabled { get; set; } = true;
        public abstract Vector3 Calculate(SteeringAgent agent, float dt);

        /// <summary>2D 模式下把 Y 分量清零。</summary>
        protected static Vector3 Flatten(Vector3 v, bool is2D)
            => is2D ? new Vector3(v.X, 0, v.Z) : v;
    }

    // --------------------------------------------------------------------
    //  Seek / Flee
    // --------------------------------------------------------------------

    /// <summary>
    /// 追寻行为：朝目标全速移动。
    /// </summary>
    public sealed class SeekBehavior : SteeringBehaviorBase
    {
        public Vector3 Target;
        public SeekBehavior(Vector3 target) { Target = target; }
        public override Vector3 Calculate(SteeringAgent agent, float dt)
        {
            var desired = Target - agent.Position;
            desired = Flatten(desired, agent.Is2D);
            desired = Vector3.Normalize(desired) * agent.MaxSpeed;
            return desired - agent.Velocity;
        }
    }

    // --------------------------------------------------------------------
    //  Arrival
    // --------------------------------------------------------------------

    public sealed class ArrivalBehavior : SteeringBehaviorBase
    {
        public Vector3 Target;
        public float SlowRadius = 3f;
        public float StopRadius = 0.2f;
        public float Deceleration = 1f; // 1=fast, 2=normal, 3=slow
        public ArrivalBehavior(Vector3 target) { Target = target; }
        public override Vector3 Calculate(SteeringAgent agent, float dt)
        {
            var to = Target - agent.Position;
            to = Flatten(to, agent.Is2D);
            float dist = to.Length();
            if (dist < StopRadius)
            {
                // 到了，减速到 0
                return -agent.Velocity;
            }
            float speed = dist < SlowRadius ? agent.MaxSpeed * (dist / SlowRadius) : agent.MaxSpeed;
            speed /= Deceleration;
            var desired = Vector3.Normalize(to) * speed;
            return desired - agent.Velocity;
        }
    }

    // --------------------------------------------------------------------
    //  Wander
    // --------------------------------------------------------------------

    /// <summary>
    /// 随机游走。用一个 projected circle 在 agent 前方，
    /// 每帧在 circle 上随机抖动一个目标点。
    /// </summary>
    public sealed class WanderBehavior : SteeringBehaviorBase
    {
        public float WanderRadius = 1.2f;
        public float WanderDistance = 2f;
        public float WanderJitter = 0.5f;
        public Vector3 WanderTarget = new(0, 0, 1);
        private Random _rng = new();

        public override Vector3 Calculate(SteeringAgent agent, float dt)
        {
            // 加随机抖动
            WanderTarget += new Vector3(
                (float)(_rng.NextDouble() * 2 - 1) * WanderJitter,
                0,
                (float)(_rng.NextDouble() * 2 - 1) * WanderJitter
            ) * dt;
            // 投影到圆上
            float len = WanderTarget.Length();
            if (len > 1e-4f) WanderTarget /= len;
            WanderTarget *= WanderRadius;

            // 转到 agent 前方
            var forward = agent.Forward;
            var targetWorld = agent.Position + forward * WanderDistance + WanderTarget;
            var desired = targetWorld - agent.Position;
            desired = Flatten(desired, agent.Is2D);
            if (desired.LengthSquared() < 1e-6f) return Vector3.Zero;
            desired = Vector3.Normalize(desired) * agent.MaxSpeed;
            return desired - agent.Velocity;
        }
    }

    // --------------------------------------------------------------------
    //  Pursuit / Evade
    // --------------------------------------------------------------------

    public sealed class PursuitBehavior : SteeringBehaviorBase
    {
        public SteeringAgent Evader;
        public PursuitBehavior(SteeringAgent evader) { Evader = evader; }
        public override Vector3 Calculate(SteeringAgent agent, float dt)
        {
            var toEvader = Evader.Position - agent.Position;
            toEvader = Flatten(toEvader, agent.Is2D);
            float dist = toEvader.Length();
            // 预测时间：距离越远预测越长，但不能超过对方跑完一圈的时间
            float lookAhead = dist / agent.MaxSpeed;
            var predicted = Evader.Position + Evader.Velocity * lookAhead;
            var desired = predicted - agent.Position;
            desired = Flatten(desired, agent.Is2D);
            if (desired.LengthSquared() < 1e-6f) return Vector3.Zero;
            desired = Vector3.Normalize(desired) * agent.MaxSpeed;
            return desired - agent.Velocity;
        }
    }

    public sealed class EvadeBehavior : SteeringBehaviorBase
    {
        public SteeringAgent Pursuer;
        public float SafeDistance = 10f;
        public EvadeBehavior(SteeringAgent pursuer) { Pursuer = pursuer; }
        public override Vector3 Calculate(SteeringAgent agent, float dt)
        {
            var fromPursuer = agent.Position - Pursuer.Position;
            fromPursuer = Flatten(fromPursuer, agent.Is2D);
            float dist = fromPursuer.Length();
            if (dist > SafeDistance) return Vector3.Zero;
            float lookAhead = dist / agent.MaxSpeed;
            var predicted = Pursuer.Position + Pursuer.Velocity * lookAhead;
            var desired = agent.Position - predicted;
            desired = Flatten(desired, agent.Is2D);
            if (desired.LengthSquared() < 1e-6f) return Vector3.Zero;
            desired = Vector3.Normalize(desired) * agent.MaxSpeed;
            return desired - agent.Velocity;
        }
    }

    // --------------------------------------------------------------------
    //  Offset Pursuit (编队用)
    // --------------------------------------------------------------------

    public sealed class OffsetPursuitBehavior : SteeringBehaviorBase
    {
        public SteeringAgent Leader;
        public Vector3 Offset;
        public OffsetPursuitBehavior(SteeringAgent leader, Vector3 offset) { Leader = leader; Offset = offset; }
        public override Vector3 Calculate(SteeringAgent agent, float dt)
        {
            // 把 offset 旋转到 leader 的朝向
            var leaderForward = Leader.Forward;
            // 简化：假设 offset 已经是 leader 局部坐标
            var target = Leader.Position + Offset;
            var toTarget = target - agent.Position;
            toTarget = Flatten(toTarget, agent.Is2D);
            float dist = toTarget.Length();
            if (dist < 0.5f) return -agent.Velocity * 0.5f;
            float speed = MathF.Min(agent.MaxSpeed, dist / dt);
            var desired = Vector3.Normalize(toTarget) * speed;
            return desired - agent.Velocity;
        }
    }

    // --------------------------------------------------------------------
    //  Obstacle Avoidance
    // --------------------------------------------------------------------

    /// <summary>
    /// 障碍物避让。用前方 box/ray 检测最近障碍，
    /// 然后施加侧向力推开。
    /// </summary>
    public sealed class ObstacleAvoidanceBehavior : SteeringBehaviorBase
    {
        public List<SteeringObstacle> Obstacles = new();
        public float DetectionLength = 3f;
        public float BrakingWeight = 0.2f;

        public override Vector3 Calculate(SteeringAgent agent, float dt)
        {
            if (Obstacles.Count == 0 || agent.Velocity.LengthSquared() < 0.01f) return Vector3.Zero;
            var forward = Vector3.Normalize(agent.Velocity);
            // 找最近的有威胁的障碍
            SteeringObstacle? closest = null;
            float closestDist = float.MaxValue;
            Vector3 localPos = Vector3.Zero;
            foreach (var ob in Obstacles)
            {
                var toObs = ob.Center - agent.Position;
                toObs = Flatten(toObs, agent.Is2D);
                float forwardDist = Vector3.Dot(toObs, forward);
                if (forwardDist < 0 || forwardDist > DetectionLength + ob.Radius) continue;
                // 侧向距离
                var side = new Vector3(-forward.Z, 0, forward.X);
                float sideDist = MathF.Abs(Vector3.Dot(toObs, side));
                float expandedRadius = ob.Radius + agent.Radius;
                if (sideDist > expandedRadius) continue;
                if (forwardDist < closestDist)
                {
                    closestDist = forwardDist;
                    closest = ob;
                    localPos = new Vector3(sideDist, 0, forwardDist);
                }
            }
            if (closest == null) return Vector3.Zero;
            // 算避让力
            var closestObs = closest.Value;
            var expanded = closestObs.Radius + agent.Radius;
            float lateral = (expanded - MathF.Abs(localPos.X)) * (localPos.X > 0 ? -1 : 1);
            float braking = (DetectionLength - localPos.Z) * BrakingWeight;
            // 转回世界坐标
            var sideWorld = new Vector3(-forward.Z, 0, forward.X);
            return sideWorld * lateral + forward * braking;
        }
    }

    // --------------------------------------------------------------------
    //  Wall Avoidance (whiskers)
    // --------------------------------------------------------------------

    /// <summary>
    /// 墙壁避让。用三根"胡须"探测前方左右中，
    /// 撞墙就施加反向法线力。
    /// </summary>
    public sealed class WallAvoidanceBehavior : SteeringBehaviorBase
    {
        public List<SteeringWall> Walls = new();
        public float WhiskerLength = 2f;
        public float SideWhiskerAngle = 0.7f; // 弧度
        public override Vector3 Calculate(SteeringAgent agent, float dt)
        {
            if (Walls.Count == 0 || agent.Velocity.LengthSquared() < 0.01f) return Vector3.Zero;
            var forward = Vector3.Normalize(Flatten(agent.Velocity, agent.Is2D));
            var side = new Vector3(-forward.Z, 0, forward.X);

            // 三根胡须
            var whiskers = new[]
            {
                forward * WhiskerLength,
                (forward * MathF.Cos(SideWhiskerAngle) + side * MathF.Sin(SideWhiskerAngle)) * (WhiskerLength * 0.7f),
                (forward * MathF.Cos(SideWhiskerAngle) - side * MathF.Sin(SideWhiskerAngle)) * (WhiskerLength * 0.7f)
            };

            Vector3 force = Vector3.Zero;
            foreach (var w in whiskers)
            {
                if (RayWallIntersect(agent.Position, w, out var hitNormal, out var hitDist))
                {
                    // 力 = 法线 * (1 - dist/length)
                    float strength = (WhiskerLength - hitDist) / WhiskerLength;
                    force += hitNormal * strength * agent.MaxForce;
                }
            }
            return force;
        }

        private bool RayWallIntersect(Vector3 origin, Vector3 dir, out Vector3 normal, out float dist)
        {
            normal = Vector3.Zero;
            dist = 0;
            float minDist = float.MaxValue;
            bool hit = false;
            foreach (var wall in Walls)
            {
                var toPoint = wall.Point - origin;
                float denom = Vector3.Dot(dir, wall.Normal);
                if (MathF.Abs(denom) < 1e-6f) continue;
                float t = Vector3.Dot(toPoint, wall.Normal) / denom;
                if (t < 0 || t > 1) continue;
                if (t < minDist)
                {
                    minDist = t;
                    normal = wall.Normal;
                    dist = t * dir.Length();
                    hit = true;
                }
            }
            return hit;
        }
    }

    // --------------------------------------------------------------------
    //  Flocking: Separation / Alignment / Cohesion
    // --------------------------------------------------------------------

    /// <summary>群体行为基类，提供邻居查询。</summary>
    public abstract class FlockingBehaviorBase : SteeringBehaviorBase
    {
        public List<SteeringAgent> Neighbors = new();
        public float NeighborRadius = 5f;
        public float ViewAngle = 270f; // 视野角度(度)
    }

    public sealed class SeparationBehavior : FlockingBehaviorBase
    {
        public override Vector3 Calculate(SteeringAgent agent, float dt)
        {
            Vector3 force = Vector3.Zero;
            int count = 0;
            foreach (var n in Neighbors)
            {
                if (n.Id == agent.Id) continue;
                var from = agent.Position - n.Position;
                from = Flatten(from, agent.Is2D);
                float dist = from.Length();
                if (dist > 0 && dist < NeighborRadius)
                {
                    // 距离越近力越大(反平方)
                    from /= dist * dist;
                    force += from;
                    count++;
                }
            }
            if (count == 0) return Vector3.Zero;
            force /= count;
            if (force.LengthSquared() < 1e-6f) return Vector3.Zero;
            force = Vector3.Normalize(force) * agent.MaxSpeed;
            return force - agent.Velocity;
        }
    }

    public sealed class AlignmentBehavior : FlockingBehaviorBase
    {
        public override Vector3 Calculate(SteeringAgent agent, float dt)
        {
            Vector3 avgVel = Vector3.Zero;
            int count = 0;
            foreach (var n in Neighbors)
            {
                if (n.Id == agent.Id) continue;
                float dist = Vector3.Distance(agent.Position, n.Position);
                if (dist < NeighborRadius)
                {
                    avgVel += n.Velocity;
                    count++;
                }
            }
            if (count == 0) return Vector3.Zero;
            avgVel /= count;
            avgVel = Flatten(avgVel, agent.Is2D);
            if (avgVel.LengthSquared() < 1e-6f) return Vector3.Zero;
            avgVel = Vector3.Normalize(avgVel) * agent.MaxSpeed;
            return avgVel - agent.Velocity;
        }
    }

    public sealed class CohesionBehavior : FlockingBehaviorBase
    {
        public override Vector3 Calculate(SteeringAgent agent, float dt)
        {
            Vector3 center = Vector3.Zero;
            int count = 0;
            foreach (var n in Neighbors)
            {
                if (n.Id == agent.Id) continue;
                float dist = Vector3.Distance(agent.Position, n.Position);
                if (dist < NeighborRadius)
                {
                    center += n.Position;
                    count++;
                }
            }
            if (count == 0) return Vector3.Zero;
            center /= count;
            // seek 到中心
            var to = center - agent.Position;
            to = Flatten(to, agent.Is2D);
            if (to.LengthSquared() < 1e-6f) return Vector3.Zero;
            to = Vector3.Normalize(to) * agent.MaxSpeed;
            return to - agent.Velocity;
        }
    }

    // --------------------------------------------------------------------
    //  Flow Field Follow
    // --------------------------------------------------------------------

    /// <summary>
    /// 流场跟随。场景预先算好每个格子到目标的最佳方向，
    /// agent 只需要采样当前位置的方向向量。
    /// </summary>
    public sealed class FlowFieldFollowBehavior : SteeringBehaviorBase
    {
        public Vector3[,]? Field;
        public int GridW, GridH;
        public Vector3 Origin;
        public float CellSize;

        public void SetField(Vector3[,] field, int w, int h, Vector3 origin, float cellSize)
        {
            Field = field; GridW = w; GridH = h; Origin = origin; CellSize = cellSize;
        }

        public override Vector3 Calculate(SteeringAgent agent, float dt)
        {
            if (Field == null) return Vector3.Zero;
            int x = (int)((agent.Position.X - Origin.X) / CellSize);
            int z = (int)((agent.Position.Z - Origin.Z) / CellSize);
            x = Math.Clamp(x, 0, GridW - 1);
            z = Math.Clamp(z, 0, GridH - 1);
            var dir = Field[x, z];
            dir = Flatten(dir, agent.Is2D);
            if (dir.LengthSquared() < 1e-6f) return Vector3.Zero;
            dir = Vector3.Normalize(dir) * agent.MaxSpeed;
            return dir - agent.Velocity;
        }
    }

    // --------------------------------------------------------------------
    //  Path Following
    // --------------------------------------------------------------------

    /// <summary>
    /// 路径跟随。沿路径走，超出时拉回，到达终点附近减速。
    /// </summary>
    public sealed class PathFollowBehavior : SteeringBehaviorBase
    {
        public List<Vector3> Path = new();
        public int CurrentIdx = 0;
        public float WaypointRadius = 0.5f;
        public bool Loop = false;
        public float ArrivalSlowRadius = 2f;

        public override Vector3 Calculate(SteeringAgent agent, float dt)
        {
            if (Path.Count == 0) return Vector3.Zero;
            // 推进 waypoint
            while (CurrentIdx < Path.Count)
            {
                float d = Vector3.Distance(agent.Position, Path[CurrentIdx]);
                if (d > WaypointRadius) break;
                CurrentIdx++;
                if (CurrentIdx >= Path.Count && Loop)
                    CurrentIdx = 0;
            }
            if (CurrentIdx >= Path.Count) return -agent.Velocity; // 到终点减速
            var target = Path[CurrentIdx];
            var to = target - agent.Position;
            to = Flatten(to, agent.Is2D);
            float dist = to.Length();
            // 最后一段减速
            bool isLast = CurrentIdx >= Path.Count - 1;
            float speed = agent.MaxSpeed;
            if (isLast && dist < ArrivalSlowRadius)
                speed = agent.MaxSpeed * (dist / ArrivalSlowRadius);
            if (to.LengthSquared() < 1e-6f) return Vector3.Zero;
            to = Vector3.Normalize(to) * speed;
            return to - agent.Velocity;
        }
    }

    // --------------------------------------------------------------------
    //  Queue (排队)
    // --------------------------------------------------------------------

    /// <summary>
    /// 排队行为。前方有同向移动的 agent 时减速。
    /// </summary>

    // ========================================================================
    //  三、转向系统主类
    // ========================================================================

    /// <summary>
    /// 转向系统。给每个 agent 管理一组 behavior，每帧累加力。
    /// </summary>
    public sealed class SteeringSystem
    {
        private readonly Dictionary<int, SteeringAgent> _agents = new();
        private readonly Dictionary<int, List<ISteeringBehavior>> _behaviors = new();
        private readonly SteeringBehaviorsConfig _config = new();
        private readonly SpatialHashGrid _neighborGrid = new(2f);

        public SteeringBehaviorsConfig Config => _config;
        public int AgentCount => _agents.Count;
        public bool AutoUpdateNeighbors { get; set; } = true;
        public float NeighborQueryRadius { get; set; } = 5f;

        public SteeringSystem() { }

        // --------------------------------------------------------------------
        //  Agent 管理
        // --------------------------------------------------------------------

        public SteeringAgent CreateAgent(int id)
        {
            var agent = new SteeringAgent(id);
            _agents[id] = agent;
            _behaviors[id] = new List<ISteeringBehavior>();
            return agent;
        }

        public void RemoveAgent(int id)
        {
            _agents.Remove(id);
            _behaviors.Remove(id);
        }

        public SteeringAgent? GetAgent(int id) => _agents.TryGetValue(id, out var a) ? a : null;

        public void AddBehavior(int agentId, ISteeringBehavior behavior)
        {
            if (_behaviors.TryGetValue(agentId, out var list))
                list.Add(behavior);
        }

        public void ClearBehaviors(int agentId)
        {
            if (_behaviors.TryGetValue(agentId, out var list))
                list.Clear();
        }

        public IEnumerable<SteeringAgent> GetAllAgents() => _agents.Values;

        // --------------------------------------------------------------------
        //  快捷添加 behavior
        // --------------------------------------------------------------------

        public SeekBehavior AddSeek(int agentId, Vector3 target)
        {
            var b = new SeekBehavior(target) { Weight = _config.SeekWeight };
            AddBehavior(agentId, b);
            return b;
        }

        public ArrivalBehavior AddArrival(int agentId, Vector3 target)
        {
            var b = new ArrivalBehavior(target) { Weight = _config.ArrivalWeight };
            AddBehavior(agentId, b);
            return b;
        }

        public WanderBehavior AddWander(int agentId)
        {
            var b = new WanderBehavior { Weight = _config.WanderWeight };
            AddBehavior(agentId, b);
            return b;
        }

        public ObstacleAvoidanceBehavior AddObstacleAvoidance(int agentId, List<SteeringObstacle> obstacles)
        {
            var b = new ObstacleAvoidanceBehavior { Weight = _config.ObstacleAvoidanceWeight };
            b.Obstacles = obstacles;
            AddBehavior(agentId, b);
            return b;
        }

        /// <summary>给 agent 加一整套 flocking(分离+对齐+聚合)。</summary>
        public void AddFlocking(int agentId, float sepW, float aliW, float cohW)
        {
            var sep = new SeparationBehavior { Weight = sepW * _config.SeparationWeight, NeighborRadius = NeighborQueryRadius };
            var ali = new AlignmentBehavior { Weight = aliW * _config.AlignmentWeight, NeighborRadius = NeighborQueryRadius };
            var coh = new CohesionBehavior { Weight = cohW * _config.CohesionWeight, NeighborRadius = NeighborQueryRadius };
            AddBehavior(agentId, sep);
            AddBehavior(agentId, ali);
            AddBehavior(agentId, coh);
        }

        public PathFollowBehavior AddPathFollow(int agentId, List<Vector3> path)
        {
            var b = new PathFollowBehavior { Weight = _config.PathFollowWeight, Path = path };
            AddBehavior(agentId, b);
            return b;
        }

        // --------------------------------------------------------------------
        //  主更新
        // --------------------------------------------------------------------

        /// <summary>每帧调用。更新所有 agent。</summary>
        public void Update(float dt)
        {
            if (dt <= 0) return;

            // 1. 更新空间哈希(邻居查询用)
            if (AutoUpdateNeighbors)
            {
                _neighborGrid.Clear();
                foreach (var a in _agents.Values)
                    _neighborGrid.Insert(a.Position, a.Id);
            }

            // 2. 每个 agent 累加 steering force
            foreach (var (id, agent) in _agents)
            {
                var behaviors = _behaviors[id];
                Vector3 total = Vector3.Zero;

                // 更新 flocking 类的邻居列表
                if (AutoUpdateNeighbors)
                    UpdateFlockingNeighbors(agent, behaviors);

                foreach (var b in behaviors)
                {
                    if (!b.Enabled) continue;
                    var force = b.Calculate(agent, dt) * b.Weight;
                    total += force;
                }

                // 限制总力
                agent.SteeringForce = agent.Truncate(total, agent.MaxForce);
            }

            // 3. 积分
            foreach (var agent in _agents.Values)
                agent.Integrate(dt);
        }

        private void UpdateFlockingNeighbors(SteeringAgent agent, List<ISteeringBehavior> behaviors)
        {
            var neighbors = _neighborGrid.Query(agent.Position, NeighborQueryRadius)
                .Select(id => id != agent.Id && _agents.TryGetValue(id, out var a) ? a : null)
                .Where(a => a != null)
                .Cast<SteeringAgent>()
                .ToList();
            foreach (var b in behaviors)
            {
                if (b is FlockingBehaviorBase fb)
                    fb.Neighbors = neighbors;
            }
        }

        /// <summary>更新单个 agent(用于 crowd sim 里穿插调用)。</summary>
        public void UpdateAgent(SteeringAgent agent, float dt, List<SteeringAgent>? neighbors = null)
        {
            if (!_behaviors.TryGetValue(agent.Id, out var behaviors)) return;
            Vector3 total = Vector3.Zero;
            if (neighbors != null)
                UpdateFlockingNeighbors(agent, behaviors);
            foreach (var b in behaviors)
            {
                if (!b.Enabled) continue;
                total += b.Calculate(agent, dt) * b.Weight;
            }
            agent.SteeringForce = agent.Truncate(total, agent.MaxForce);
            agent.Integrate(dt);
        }
    }

    // ========================================================================
    //  四、空间哈希(邻居查询加速)
    // ========================================================================

    /// <summary>
    /// 简单的空间哈希网格。给 steering/crowd 查邻居用。
    /// </summary>

    // ========================================================================
    //  五、流场生成器
    // ========================================================================

    /// <summary>
    /// 流场生成器。给一个目标点，用 Dijkstra/BFS 算出每个格子到目标的最短方向。
    /// 大量同目标 agent 用流场比每个都 A* 高效得多。
    /// </summary>
    public sealed class FlowFieldGenerator
    {
        private readonly GridGraph _grid;

        public FlowFieldGenerator(GridGraph grid) { _grid = grid; }

        /// <summary>生成流场。返回每个格子的方向向量。</summary>
        public Vector3[,] Generate(int targetX, int targetZ)
        {
            int w = _grid.Width, h = _grid.Height;
            var dist = new float[w, h];
            for (int x = 0; x < w; x++)
                for (int z = 0; z < h; z++)
                    dist[x, z] = float.MaxValue;
            var queue = new PriorityQueue<(int x, int z), float>();
            dist[targetX, targetZ] = 0;
            queue.Enqueue((targetX, targetZ), 0);

            int[][] dirs = { new[] { 1, 0 }, new[] { -1, 0 }, new[] { 0, 1 }, new[] { 0, -1 },
                             new[] { 1, 1 }, new[] { 1, -1 }, new[] { -1, 1 }, new[] { -1, -1 } };

            while (queue.Count > 0)
            {
                var (cx, cz) = queue.Dequeue();
                foreach (var d in dirs)
                {
                    int nx = cx + d[0], nz = cz + d[1];
                    if (!_grid.IsWalkable(nx, nz)) continue;
                    float cost = (d[0] != 0 && d[1] != 0) ? 1.41421356f : 1f;
                    float newDist = dist[cx, cz] + cost * (_grid.Costs[_grid.NodeIndex(nx, nz)]);
                    if (newDist < dist[nx, nz])
                    {
                        dist[nx, nz] = newDist;
                        queue.Enqueue((nx, nz), newDist);
                    }
                }
            }

            // 转方向场
            var field = new Vector3[w, h];
            for (int x = 0; x < w; x++)
            {
                for (int z = 0; z < h; z++)
                {
                    if (dist[x, z] >= float.MaxValue)
                    {
                        field[x, z] = Vector3.Zero;
                        continue;
                    }
                    // 找 8 邻居中 dist 最小的
                    float minD = dist[x, z];
                    int bx = 0, bz = 0;
                    bool found = false;
                    foreach (var d in dirs)
                    {
                        int nx = x + d[0], nz = z + d[1];
                        if (nx < 0 || nx >= w || nz < 0 || nz >= h) continue;
                        if (dist[nx, nz] < minD) { minD = dist[nx, nz]; bx = d[0]; bz = d[1]; found = true; }
                    }
                    if (found)
                    {
                        field[x, z] = Vector3.Normalize(new Vector3(bx, 0, bz));
                    }
                    else
                    {
                        field[x, z] = Vector3.Zero;
                    }
                }
            }
            return field;
        }
    }
}
