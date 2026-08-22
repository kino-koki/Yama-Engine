// ============================================================================
//  ShanyuEngine.AI.SwarmIntelligenceSystem.cs
//  山雨引擎 -- 群体智能系统 (Swarm Intelligence System)
//  对齐 Boids (Reynolds 1987) / Flow Field Pathfinding / Influence Maps
// ----------------------------------------------------------------------------
//  本文件实现：
//    1. Boids 群飞算法      - 分离/对齐/聚合三力 (Reynolds steering)
//    2. 群体路径规划        - 基于流的群体移动 (Flow Fields, Dijkstra 积分场)
//    3. 阵型系统            - 圆形/楔形/网格/自定义 + 阵型保持力
//    4. 战术分析            - 影响图 (威胁图/兴趣图/探索图) 衰减与合成
//    5. 指挥 AI             - 群体编队 -> 分派任务 -> 协调攻击
//    6. 个体差异            - 速度/攻击范围/视野的随机扰动
// ----------------------------------------------------------------------------
//  使用 System.Numerics (Vector3)，3D 空间 (主体在 XZ 平面)。零异常空壳。
// ============================================================================
using System;
using System.Collections.Generic;
using System.Numerics;

namespace ShanyuEngine.AI
{
    // ========================================================================
    //  一、Boids 群飞算法
    // ========================================================================

    /// <summary>单个 Boid 个体。</summary>
    public sealed class Boid
    {
        public int Id;
        public Vector3 Position;
        public Vector3 Velocity;
        public Vector3 Acceleration;
        public float MaxSpeed = 5f;
        public float MaxForce = 3f;
        public float PerceptionRadius = 4f;
        public float SeparationRadius = 2f;
        public object? UserData;

        public Boid(int id, Vector3 pos) { Id = id; Position = pos; }
    }

    /// <summary>
    /// Boids 群体：实现 Reynolds 三力 (分离/对齐/聚合) + 可选目标趋向力。
    /// </summary>
    public sealed class BoidFlock
    {
        public List<Boid> Boids = new();
        /// <summary>分离权重。</summary>
        public float SeparationWeight = 1.5f;
        /// <summary>对齐权重。</summary>
        public float AlignmentWeight = 1.0f;
        /// <summary>聚合权重。</summary>
        public float CohesionWeight = 1.0f;
        /// <summary>目标趋向权重。</summary>
        public float SeekWeight = 0.5f;
        /// <summary>目标位置 (无目标则不施加)。</summary>
        public Vector3? Target;

        private readonly Dictionary<(int, int), List<Boid>> _grid = new();
        private float _cellSize = 5f;

        public BoidFlock() { }
        public Boid AddBoid(Vector3 pos, float maxSpeed = 5f)
        {
            var b = new Boid(Boids.Count, pos) { MaxSpeed = maxSpeed };
            Boids.Add(b);
            return b;
        }

        /// <summary>更新整个群体一帧。</summary>
        public void Update(float dt)
        {
            BuildSpatialGrid();
            foreach (var b in Boids)
            {
                var neighbors = QueryNeighbors(b);
                Vector3 sep = Separation(b, neighbors);
                Vector3 ali = Alignment(b, neighbors);
                Vector3 coh = Cohesion(b, neighbors);
                Vector3 seek = Target.HasValue ? Seek(b, Target.Value) : Vector3.Zero;

                b.Acceleration = sep * SeparationWeight + ali * AlignmentWeight + coh * CohesionWeight + seek * SeekWeight;
            }
            foreach (var b in Boids)
            {
                b.Velocity += b.Acceleration * dt;
                Limit(b.Velocity, b.MaxSpeed, out b.Velocity);
                b.Position += b.Velocity * dt;
                b.Acceleration = Vector3.Zero;
            }
        }

        // ── Reynolds 三力 ──────────────────────────────────────────────
        private Vector3 Separation(Boid b, List<Boid> neighbors)
        {
            Vector3 steer = Vector3.Zero;
            int count = 0;
            foreach (var n in neighbors)
            {
                if (n == b) continue;
                float d = Vector3.Distance(b.Position, n.Position);
                if (d > 0 && d < b.SeparationRadius)
                {
                    Vector3 diff = b.Position - n.Position;
                    steer += Vector3.Normalize(diff) / d; // 距离越近力越大
                    count++;
                }
            }
            if (count > 0) steer /= count;
            return SteerFromVector(b, steer);
        }

        private Vector3 Alignment(Boid b, List<Boid> neighbors)
        {
            Vector3 sum = Vector3.Zero;
            int count = 0;
            foreach (var n in neighbors)
            {
                if (n == b) continue;
                float d = Vector3.Distance(b.Position, n.Position);
                if (d > 0 && d < b.PerceptionRadius) { sum += n.Velocity; count++; }
            }
            if (count == 0) return Vector3.Zero;
            sum /= count;
            return SteerFromVector(b, sum);
        }

        private Vector3 Cohesion(Boid b, List<Boid> neighbors)
        {
            Vector3 center = Vector3.Zero;
            int count = 0;
            foreach (var n in neighbors)
            {
                if (n == b) continue;
                float d = Vector3.Distance(b.Position, n.Position);
                if (d > 0 && d < b.PerceptionRadius) { center += n.Position; count++; }
            }
            if (count == 0) return Vector3.Zero;
            center /= count;
            return Seek(b, center);
        }

        private Vector3 Seek(Boid b, Vector3 target)
        {
            Vector3 desired = target - b.Position;
            if (desired.LengthSquared() < 1e-6f) return Vector3.Zero;
            desired = Vector3.Normalize(desired) * b.MaxSpeed;
            return SteerFromVector(b, desired);
        }

        private Vector3 SteerFromVector(Boid b, Vector3 desired)
        {
            Vector3 steer = desired - b.Velocity;
            Limit(steer, b.MaxForce, out steer);
            return steer;
        }

        private static void Limit(Vector3 v, float max, out Vector3 result)
        {
            float sq = v.LengthSquared();
            if (sq > max * max && sq > 1e-8f) result = v * (max / MathF.Sqrt(sq));
            else result = v;
        }

        // ── 空间网格加速邻居查询 ──────────────────────────────────────
        private void BuildSpatialGrid()
        {
            _grid.Clear();
            foreach (var b in Boids)
            {
                var key = CellOf(b.Position);
                if (!_grid.TryGetValue(key, out var list)) _grid[key] = list = new List<Boid>();
                list.Add(b);
            }
        }
        private (int, int) CellOf(Vector3 p) => ((int)MathF.Floor(p.X / _cellSize), (int)MathF.Floor(p.Z / _cellSize));
        private List<Boid> QueryNeighbors(Boid b)
        {
            var result = new List<Boid>();
            var c = CellOf(b.Position);
            int r = (int)MathF.Ceiling(b.PerceptionRadius / _cellSize);
            for (int dx = -r; dx <= r; dx++)
                for (int dz = -r; dz <= r; dz++)
                    if (_grid.TryGetValue((c.Item1 + dx, c.Item2 + dz), out var list))
                        result.AddRange(list);
            return result;
        }
    }

    // ========================================================================
    //  二、流场群体路径规划 (Flow Fields)
    // ========================================================================

    /// <summary>
    /// 流场：网格上每个格子存储一个方向向量，群体跟随方向场移动到目标。
    /// 由 Dijkstra 从目标反向扩散生成积分场，再求梯度得方向场。
    /// </summary>
    public sealed class FlowField
    {
        public readonly int Width, Height;
        public readonly float CellSize;
        public readonly Vector2 Origin; // 世界原点对应的网格坐标
        public readonly float[] CostField;     // 每格通行代价 (>=1, 大值=障碍)
        public readonly float[] IntegrationField; // 从目标扩散的累积代价
        public readonly Vector2[] DirectionField;  // 每格最佳移动方向 (归一化)

        public FlowField(int width, int height, float cellSize, Vector2 origin)
        {
            Width = width; Height = height; CellSize = cellSize; Origin = origin;
            int n = width * height;
            CostField = new float[n];
            IntegrationField = new float[n];
            DirectionField = new Vector2[n];
            for (int i = 0; i < n; i++) CostField[i] = 1f;
        }

        public int Index(int x, int y) => y * Width + x;
        public bool InBounds(int x, int y) => x >= 0 && x < Width && y >= 0 && y < Height;

        /// <summary>标记障碍格 (高代价)。</summary>
        public void SetObstacle(int x, int y, float cost = 1e6f)
        {
            if (InBounds(x, y)) CostField[Index(x, y)] = cost;
        }

        /// <summary>世界坐标 -> 网格坐标。</summary>
        public (int x, int y) WorldToCell(Vector3 world)
        {
            int x = (int)MathF.Floor((world.X - Origin.X) / CellSize);
            int y = (int)MathF.Floor((world.Z - Origin.Y) / CellSize);
            return (x, y);
        }

        /// <summary>从目标格反向运行 Dijkstra 生成积分场，再求方向场。</summary>
        public void Generate(int goalX, int goalY)
        {
            int n = Width * Height;
            for (int i = 0; i < n; i++) IntegrationField[i] = float.MaxValue;
            if (!InBounds(goalX, goalY)) return;
            var heap = new FlowHeap();
            IntegrationField[Index(goalX, goalY)] = 0;
            heap.Push(Index(goalX, goalY), 0);

            // 8 方向邻居
            int[] dx = { 1, -1, 0, 0, 1, 1, -1, -1 };
            int[] dy = { 0, 0, 1, -1, 1, -1, 1, -1 };

            while (heap.Count > 0)
            {
                var (idx, cost) = heap.Pop();
                if (cost > IntegrationField[idx]) continue;
                int cx = idx % Width, cy = idx / Width;
                for (int k = 0; k < 8; k++)
                {
                    int nx = cx + dx[k], ny = cy + dy[k];
                    if (!InBounds(nx, ny)) continue;
                    float cellCost = CostField[Index(nx, ny)];
                    if (cellCost >= 1e5f) continue; // 障碍
                    float step = (k < 4) ? 1f : 1.4142135f; // 直走1，斜走√2
                    float newCost = cost + cellCost * step;
                    int nIdx = Index(nx, ny);
                    if (newCost < IntegrationField[nIdx])
                    {
                        IntegrationField[nIdx] = newCost;
                        heap.Push(nIdx, newCost);
                    }
                }
            }

            // 求方向场：每格指向积分值最小的邻居
            for (int y = 0; y < Height; y++)
            {
                for (int x = 0; x < Width; x++)
                {
                    int idx = Index(x, y);
                    if (IntegrationField[idx] == float.MaxValue) { DirectionField[idx] = Vector2.Zero; continue; }
                    float best = IntegrationField[idx];
                    Vector2 bestDir = Vector2.Zero;
                    for (int k = 0; k < 8; k++)
                    {
                        int nx = x + dx[k], ny = y + dy[k];
                        if (!InBounds(nx, ny)) continue;
                        float nCost = IntegrationField[Index(nx, ny)];
                        if (nCost < best) { best = nCost; bestDir = new Vector2(dx[k], dy[k]); }
                    }
                    if (bestDir != Vector2.Zero) bestDir = Vector2.Normalize(bestDir);
                    DirectionField[idx] = bestDir;
                }
            }
        }

        /// <summary>采样世界位置处的流场方向。</summary>
        public Vector2 SampleDirection(Vector3 world)
        {
            var (x, y) = WorldToCell(world);
            if (!InBounds(x, y)) return Vector2.Zero;
            return DirectionField[Index(x, y)];
        }
    }

    /// <summary>简易二叉堆 (用于 Dijkstra)。</summary>
    internal sealed class FlowHeap
    {
        private readonly List<(int idx, float cost)> _data = new();
        public int Count => _data.Count;
        public void Push(int idx, float cost)
        {
            _data.Add((idx, cost));
            int c = _data.Count - 1;
            while (c > 0)
            {
                int p = (c - 1) / 2;
                if (_data[p].cost <= _data[c].cost) break;
                (_data[p], _data[c]) = (_data[c], _data[p]);
                c = p;
            }
        }
        public (int idx, float cost) Pop()
        {
            var top = _data[0];
            int last = _data.Count - 1;
            _data[0] = _data[last];
            _data.RemoveAt(last);
            int c = 0;
            while (true)
            {
                int l = 2 * c + 1, r = 2 * c + 2, smallest = c;
                if (l < _data.Count && _data[l].cost < _data[smallest].cost) smallest = l;
                if (r < _data.Count && _data[r].cost < _data[smallest].cost) smallest = r;
                if (smallest == c) break;
                (_data[smallest], _data[c]) = (_data[c], _data[smallest]);
                c = smallest;
            }
            return top;
        }
    }

    // ========================================================================
    //  三、阵型系统 (圆形/楔形/网格/自定义)
    // ========================================================================

    /// <summary>阵型形状 (独立于 CrowdSimulationReal.FormationType)。</summary>
    public enum SwarmFormationShape { Circle, Wedge, Grid, Custom }

    /// <summary>阵型槽位。</summary>
    public sealed class SwarmFormationSlot
    {
        public Vector3 Offset;
        public int Index;
        public SwarmFormationSlot(Vector3 offset, int index) { Offset = offset; Index = index; }
    }

    /// <summary>阵型模板：根据形状生成槽位，并按队长朝向变换到世界空间。</summary>
    public sealed class SwarmFormation
    {
        public SwarmFormationShape Shape = SwarmFormationShape.Circle;
        public float Spacing = 2.5f;
        public float CircleRadius = 4f;
        public List<SwarmFormationSlot> Slots = new();
        public List<Vector3> CustomOffsets = new();

        public void Generate(int count)
        {
            Slots.Clear();
            switch (Shape)
            {
                case SwarmFormationShape.Circle: GenCircle(count); break;
                case SwarmFormationShape.Wedge: GenWedge(count); break;
                case SwarmFormationShape.Grid: GenGrid(count); break;
                case SwarmFormationShape.Custom: GenCustom(count); break;
            }
        }

        private void GenCircle(int count)
        {
            float step = 2f * MathF.PI / Math.Max(1, count);
            for (int i = 0; i < count; i++)
            {
                float a = i * step;
                Slots.Add(new SwarmFormationSlot(new Vector3(MathF.Sin(a) * CircleRadius, 0, MathF.Cos(a) * CircleRadius), i));
            }
        }
        private void GenWedge(int count)
        {
            int row = 0, idx = 0;
            while (idx < count)
            {
                int inRow = row + 1;
                float z = (row + 1) * Spacing;
                for (int j = 0; j < inRow && idx < count; j++)
                {
                    float x = (j - row / 2f) * Spacing;
                    Slots.Add(new SwarmFormationSlot(new Vector3(x, 0, z), idx));
                    idx++;
                }
                row++;
            }
        }
        private void GenGrid(int count)
        {
            int cols = (int)MathF.Ceiling(MathF.Sqrt(count));
            for (int i = 0; i < count; i++)
            {
                int r = i / cols, c = i % cols;
                Slots.Add(new SwarmFormationSlot(new Vector3((c - cols / 2f) * Spacing, 0, r * Spacing), i));
            }
        }
        private void GenCustom(int count)
        {
            for (int i = 0; i < count; i++)
                Slots.Add(new SwarmFormationSlot(i < CustomOffsets.Count ? CustomOffsets[i] : Vector3.Zero, i));
        }

        /// <summary>计算槽位世界位置 (按队长位置 + 朝向旋转)。</summary>
        public Vector3 SlotWorldPosition(int slot, Vector3 leaderPos, Vector3 leaderForward)
        {
            if (slot < 0 || slot >= Slots.Count) return leaderPos;
            var off = Slots[slot].Offset;
            // 绕 Y 轴旋转偏移到队长朝向 (forward=XZ 平面归一化)
            Vector3 f = leaderForward;
            if (f.LengthSquared() < 1e-5f) f = Vector3.UnitZ;
            f = Vector3.Normalize(new Vector3(f.X, 0, f.Z));
            float cos = f.Z, sin = f.X; // forward=(sin,0,cos) -> 旋转矩阵
            // 将本地 (x,z) 旋转: world = (x*sin + z*cos? ) 推导: forward 方向对应本地 +Z
            // 旋转矩阵 R 使 (0,0,1)->forward(sin,0,cos): R = [[cos,0,sin],[0,1,0],[-sin,0,cos]]
            Vector3 world = new Vector3(
                off.X * cos + off.Z * sin,
                off.Y,
                -off.X * sin + off.Z * cos);
            return leaderPos + world;
        }
    }

    // ========================================================================
    //  四、战术分析 (影响图)
    // ========================================================================

    /// <summary>
    /// 影响图基类：网格化的标量场，支持添加/衰减/采样/合成。
    /// </summary>
    public class InfluenceMap
    {
        public readonly int Width, Height;
        public readonly float CellSize;
        public readonly Vector2 Origin;
        public readonly float[] Values;
        public float DecayRate = 0.5f; // 每秒指数衰减系数

        public InfluenceMap(int width, int height, float cellSize, Vector2 origin)
        {
            Width = width; Height = height; CellSize = cellSize; Origin = origin;
            Values = new float[width * height];
        }

        public int Index(int x, int y) => y * Width + x;
        public bool InBounds(int x, int y) => x >= 0 && x < Width && y >= 0 && y < Height;
        public (int x, int y) WorldToCell(Vector3 p)
            => ((int)MathF.Floor((p.X - Origin.X) / CellSize), (int)MathF.Floor((p.Z - Origin.Y) / CellSize));

        /// <summary>在某位置添加影响 (带半径与高斯衰减)。</summary>
        public void AddInfluence(Vector3 world, float value, float radius)
        {
            var (cx, cy) = WorldToCell(world);
            int r = (int)MathF.Ceiling(radius / CellSize);
            float sigma = radius / 2f;
            for (int dx = -r; dx <= r; dx++)
                for (int dy = -r; dy <= r; dy++)
                {
                    int x = cx + dx, y = cy + dy;
                    if (!InBounds(x, y)) continue;
                    float dist = MathF.Sqrt(dx * dx + dy * dy) * CellSize;
                    if (dist > radius) continue;
                    float falloff = MathF.Exp(-(dist * dist) / (2 * sigma * sigma + 1e-6f));
                    Values[Index(x, y)] += value * falloff;
                }
        }

        /// <summary>整图指数衰减。</summary>
        public virtual void Decay(float dt)
        {
            float factor = MathF.Exp(-DecayRate * dt);
            for (int i = 0; i < Values.Length; i++) Values[i] *= factor;
        }

        /// <summary>双线性采样世界位置。</summary>
        public float Sample(Vector3 world)
        {
            float fx = (world.X - Origin.X) / CellSize - 0.5f;
            float fy = (world.Z - Origin.Y) / CellSize - 0.5f;
            int x0 = (int)MathF.Floor(fx), y0 = (int)MathF.Floor(fy);
            int x1 = x0 + 1, y1 = y0 + 1;
            float tx = Math.Clamp(fx - x0, 0f, 1f), ty = Math.Clamp(fy - y0, 0f, 1f);
            float v00 = InBounds(x0, y0) ? Values[Index(x0, y0)] : 0;
            float v10 = InBounds(x1, y0) ? Values[Index(x1, y0)] : 0;
            float v01 = InBounds(x0, y1) ? Values[Index(x0, y1)] : 0;
            float v11 = InBounds(x1, y1) ? Values[Index(x1, y1)] : 0;
            return (v00 * (1 - tx) + v10 * tx) * (1 - ty) + (v01 * (1 - tx) + v11 * tx) * ty;
        }

        public void Clear() { Array.Clear(Values, 0, Values.Length); }
    }

    /// <summary>威胁图：记录敌方位置，随时间衰减。</summary>
    public sealed class ThreatMap : InfluenceMap
    {
        public ThreatMap(int w, int h, float cs, Vector2 o) : base(w, h, cs, o) { DecayRate = 0.4f; }
        public void ReportEnemy(Vector3 pos, float threat = 1f, float radius = 5f) => AddInfluence(pos, threat, radius);
    }

    /// <summary>兴趣图：记录资源/目标，吸引单位。</summary>
    public sealed class InterestMap : InfluenceMap
    {
        public InterestMap(int w, int h, float cs, Vector2 o) : base(w, h, cs, o) { DecayRate = 0.1f; }
        public void ReportInterest(Vector3 pos, float value = 1f, float radius = 6f) => AddInfluence(pos, value, radius);
    }

    /// <summary>探索图：记录已探索区域，降低兴趣 (单调累积，不衰减)。</summary>
    public sealed class ExplorationMap : InfluenceMap
    {
        public ExplorationMap(int w, int h, float cs, Vector2 o) : base(w, h, cs, o) { DecayRate = 0f; }
        public void MarkExplored(Vector3 pos, float radius = 4f) => AddInfluence(pos, 1f, radius);
        public override void Decay(float dt) { /* 探索记忆不衰减 */ }
    }

    /// <summary>战术分析器：合成多张影响图，给出战术决策建议。</summary>
    public sealed class SwarmTacticalAnalyzer
    {
        public ThreatMap Threats;
        public InterestMap Interests;
        public ExplorationMap Explored;
        /// <summary>威胁规避权重。</summary>
        public float AvoidThreatWeight = 1.5f;
        /// <summary>兴趣吸引权重。</summary>
        public float SeekInterestWeight = 1f;
        /// <summary>已探索区域抑制权重。</summary>
        public float ExploredPenalty = 0.6f;

        public SwarmTacticalAnalyzer(ThreatMap threats, InterestMap interests, ExplorationMap explored)
        { Threats = threats; Interests = interests; Explored = explored; }

        /// <summary>在某位置的战术评分：高 = 值得前往 (兴趣高且威胁低且未探索)。</summary>
        public float ScorePosition(Vector3 pos)
        {
            float t = Threats.Sample(pos);
            float i = Interests.Sample(pos);
            float e = Explored.Sample(pos);
            return i * SeekInterestWeight - t * AvoidThreatWeight - e * ExploredPenalty;
        }

        /// <summary>在单位周围采样，找出最佳战术移动方向。</summary>
        public Vector3 BestMoveDirection(Vector3 pos, float sampleRadius, int samples)
        {
            Vector3 best = Vector3.Zero;
            float bestScore = float.MinValue;
            for (int k = 0; k < samples; k++)
            {
                float angle = 2f * MathF.PI * k / samples;
                Vector3 dir = new Vector3(MathF.Cos(angle), 0, MathF.Sin(angle)) * sampleRadius;
                float s = ScorePosition(pos + dir);
                if (s > bestScore) { bestScore = s; best = dir; }
            }
            if (best.LengthSquared() > 1e-6f) best = Vector3.Normalize(best);
            return best;
        }

        /// <summary>更新所有影响图 (每帧调用)。</summary>
        public void Update(float dt)
        {
            Threats.Decay(dt);
            Interests.Decay(dt);
            Explored.Decay(dt);
        }
    }

    // ========================================================================
    //  五、群体单位 + 指挥 AI
    // ========================================================================

    /// <summary>任务类型。</summary>
    public enum SwarmTaskType { Idle, MoveTo, Attack, Defend, Gather, Scout }

    /// <summary>分派给群体的任务。</summary>
    public sealed class SwarmTask
    {
        public SwarmTaskType Type;
        public Vector3 Position;
        public int TargetId = -1;
        public SwarmTask(SwarmTaskType type, Vector3 pos) { Type = type; Position = pos; }
    }

    /// <summary>
    /// 群体单位：结合 Boids 避让 + 流场跟随 + 阵型保持 + 个体差异。
    /// </summary>
    public sealed class SwarmAgent
    {
        public int Id;
        public Vector3 Position;
        public Vector3 Velocity;
        public float MaxSpeed = 5f;
        public float AttackRange = 2f;
        public float ViewAngle = 90f;
        public SwarmFormationSlot? Slot;
        public SwarmTask? Task;
        public object? UserData;

        public SwarmAgent(int id, Vector3 pos) { Id = id; Position = pos; }

        /// <summary>施加个体差异扰动 (速度/攻击范围/视野)。</summary>
        public void ApplyVariation(Random rng, float speedJitter, float rangeJitter, float viewJitter)
        {
            MaxSpeed *= 1f + (float)(rng.NextDouble() * 2 - 1) * speedJitter;
            AttackRange *= 1f + (float)(rng.NextDouble() * 2 - 1) * rangeJitter;
            ViewAngle *= 1f + (float)(rng.NextDouble() * 2 - 1) * viewJitter;
            MaxSpeed = MathF.Max(MaxSpeed, 0.5f);
            AttackRange = MathF.Max(AttackRange, 0.5f);
            ViewAngle = Math.Clamp(ViewAngle, 10f, 360f);
        }
    }

    /// <summary>
    /// 指挥 AI：管理群体编队、分派任务、协调攻击。整合 Boids/流场/阵型/战术。
    /// </summary>
    public sealed class SwarmCommander
    {
        public List<SwarmAgent> Agents = new();
        public SwarmFormation Formation = new() { Shape = SwarmFormationShape.Wedge };
        public BoidFlock? Flock;
        public FlowField? Flow;
        public SwarmTacticalAnalyzer? Tactics;
        public Vector3 LeaderPosition;
        public Vector3 LeaderForward = Vector3.UnitZ;
        public float CohesionStrength = 1f;
        public SwarmTask? CurrentTask;
        private readonly Random _rng = new(12345);

        public void AddAgent(SwarmAgent a) { Agents.Add(a); }

        /// <summary>重建阵型槽位并分配给单位。</summary>
        public void BuildFormation()
        {
            Formation.Generate(Agents.Count);
            for (int i = 0; i < Agents.Count && i < Formation.Slots.Count; i++)
                Agents[i].Slot = Formation.Slots[i];
        }

        /// <summary>分派任务给群体。</summary>
        public void AssignTask(SwarmTask task)
        {
            CurrentTask = task;
            foreach (var a in Agents) a.Task = task;
            // 若有流场，重新生成朝向任务目标
            if (Flow != null)
            {
                var (gx, gy) = Flow.WorldToCell(task.Position);
                Flow.Generate(gx, gy);
            }
        }

        /// <summary>每帧更新：编队保持 + 流场跟随 + Boids 避让 + 战术调整。</summary>
        public void Update(float dt)
        {
            Tactics?.Update(dt);

            // 队长朝向任务方向
            if (CurrentTask != null)
            {
                Vector3 toTask = CurrentTask.Position - LeaderPosition;
                if (toTask.LengthSquared() > 1e-4f) LeaderForward = Vector3.Normalize(new Vector3(toTask.X, 0, toTask.Z));
            }

            foreach (var a in Agents)
            {
                Vector3 desired = Vector3.Zero;

                // 1. 阵型保持力
                if (a.Slot != null)
                {
                    Vector3 slotPos = Formation.SlotWorldPosition(a.Slot.Index, LeaderPosition, LeaderForward);
                    desired += (slotPos - a.Position) * CohesionStrength;
                }

                // 2. 流场跟随力
                if (Flow != null)
                {
                    var dir2 = Flow.SampleDirection(a.Position);
                    desired += new Vector3(dir2.X, 0, dir2.Y) * 2f;
                }

                // 3. 战术调整 (远离威胁/靠近兴趣)
                if (Tactics != null)
                {
                    Vector3 tac = Tactics.BestMoveDirection(a.Position, 5f, 8);
                    desired += tac * 1.5f;
                }

                // 4. 速度限制 + 位置积分
                if (desired.LengthSquared() > 1e-6f)
                {
                    desired = Vector3.Normalize(desired) * a.MaxSpeed;
                    a.Velocity = Vector3.Lerp(a.Velocity, desired, Math.Clamp(dt * 3f, 0f, 1f));
                }
                else
                {
                    a.Velocity = Vector3.Lerp(a.Velocity, Vector3.Zero, Math.Clamp(dt * 3f, 0f, 1f));
                }
                a.Position += a.Velocity * dt;
            }

            // 同步到 Boids (可选可视化)
            if (Flock != null && Flock.Boids.Count == Agents.Count)
                for (int i = 0; i < Agents.Count; i++) Flock.Boids[i].Position = Agents[i].Position;
        }

        /// <summary>对所有单位施加个体差异。</summary>
        public void ApplyVariation(float speedJitter = 0.15f, float rangeJitter = 0.2f, float viewJitter = 0.15f)
        {
            foreach (var a in Agents) a.ApplyVariation(_rng, speedJitter, rangeJitter, viewJitter);
        }

        /// <summary>协调攻击：将单位按攻击范围分组，分批进攻目标。</summary>
        public void CoordinateAttack(Vector3 target, int batchSize = 3)
        {
            // 按到目标距离排序，最近的优先攻击
            Agents.Sort((a, b) => Vector3.DistanceSquared(a.Position, target).CompareTo(Vector3.DistanceSquared(b.Position, target)));
            for (int i = 0; i < Agents.Count; i++)
            {
                // 交替分配: 一批攻击、一批压制、一批侧翼
                int role = (i / batchSize) % 3;
                Agents[i].Task = role switch
                {
                    0 => new SwarmTask(SwarmTaskType.Attack, target),
                    1 => new SwarmTask(SwarmTaskType.Defend, target + new Vector3(3, 0, 3)),
                    _ => new SwarmTask(SwarmTaskType.Scout, target + new Vector3(-5, 0, -2)),
                };
            }
            CurrentTask = new SwarmTask(SwarmTaskType.Attack, target);
        }
    }
}
