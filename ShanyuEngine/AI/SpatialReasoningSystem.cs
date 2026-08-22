// ============================================================================
//  ShanyuEngine.AI.SpatialReasoningSystem.cs
//  山雨引擎 -- 空间推理系统
//  对齐 FEAR AI / Halo AI / Killing Floor 2 tactical AI
// ----------------------------------------------------------------------------
//  本文件实现"空间推理"——让 AI 理解周围空间的战术意义：
//    1. SpatialQuery          - 空间查询接口(点/区域/范围)
//    2. CoverPoint            - 掩体点
//    3. CoverSystem           - 掩体系统(评估掩体质量)
//    4. VantagePoint          - 哨点/狙击点
//    5. ChokePoint            - 瓶颈点(走廊/门口)
//    6. FlankingRoute         - 侧翼路径
//    7. LineOfSightField      - 视野场(从某点能看到哪里)
//    8. ThreatField           - 威胁场(危险区域热力图)
//    9. SpatialReasoningSystem- 主类
// ----------------------------------------------------------------------------
//  注意：空间推理的结果应该缓存，不要每帧重算。
// ============================================================================
using System;
using System.Collections.Generic;
using System.Numerics;

namespace ShanyuEngine.AI
{
    // ========================================================================
    //  一、空间查询接口
    // ========================================================================

    /// <summary>
    /// 空间查询接口。由场景/导航系统提供。
    /// </summary>
    public interface ISpatialQuery
    {
        /// <summary>检查位置是否可行走。</summary>
        bool IsWalkable(Vector3 pos);
        /// <summary>射线遮挡检测(返回 true 表示被挡)。</summary>
        bool IsBlocked(Vector3 from, Vector3 to);
        /// <summary>查询范围内的点。</summary>
        List<Vector3> QueryWalkablePoints(Vector3 center, float radius);
        /// <summary>获取地面高度。</summary>
        float GetGroundHeight(Vector3 pos);
        /// <summary>获取最近的可行走点。</summary>
        Vector3 FindNearestWalkable(Vector3 pos, float maxDist = 10f);
    }

    // ========================================================================
    //  二、掩体点
    // ========================================================================

    /// <summary>掩体类型。</summary>
    public enum CoverType
    {
        None,
        Low,      // 矮掩体(可跨过)
        High,     // 高掩体(完全挡住)
        Partial,  // 部分掩体(侧面)
        Destructible // 可破坏掩体
    }

    /// <summary>掩体姿态。</summary>
    public enum CoverStance
    {
        Standing,
        Crouching,
        Prone
    }

    /// <summary>
    /// 掩体点。一个提供遮挡的位置 + 朝向。
    /// </summary>
    public struct CoverPoint
    {
        public Vector3 Position;
        public Vector3 Normal;       // 掩体法线(指向受保护的方向)
        public CoverType Type;
        public CoverStance Stance;
        public float Quality;        // 0~1, 越大越好
        public float Height;         // 掩体高度
        /// <summary>该掩体能挡住的方向(相对法线的角度范围)。</summary>
        public float CoverageAngle;  // 度
        public int Id;
        public bool IsOccupied;
        public int OccupiedBy;
        public float LastUsedTime;

        /// <summary>检查该掩体是否能挡住某方向。</summary>
        public bool ProvidesCoverFrom(Vector3 threatDir)
        {
            float dot = Vector3.Dot(Vector3.Normalize(Normal), -Vector3.Normalize(threatDir));
            float cosCoverage = MathF.Cos(CoverageAngle * 0.5f * MathF.PI / 180f);
            return dot >= cosCoverage;
        }

        /// <summary>获取掩体站立位置(往后挪一点)。</summary>
        public Vector3 GetStandPosition(float offset = 0.3f) => Position - Normal * offset;
    }

    // ========================================================================
    //  三、掩体系统
    // ========================================================================

    /// <summary>
    /// 掩体系统。生成 + 评估 + 分配掩体点。
    /// </summary>
    public sealed class CoverSystem
    {
        private readonly List<CoverPoint> _covers = new();
        private readonly ISpatialQuery _spatial;
        private readonly Dictionary<long, List<int>> _spatialHash = new();
        private float _hashCellSize = 4f;

        public CoverSystem(ISpatialQuery spatial) { _spatial = spatial; }

        /// <summary>添加掩体点。</summary>
        public int AddCover(Vector3 pos, Vector3 normal, CoverType type, float height = 1.5f)
        {
            var cp = new CoverPoint
            {
                Id = _covers.Count,
                Position = pos,
                Normal = normal,
                Type = type,
                Height = height,
                CoverageAngle = 120f,
                Stance = type == CoverType.Low ? CoverStance.Crouching : CoverStance.Standing
            };
            _covers.Add(cp);
            AddToHash(cp);
            return cp.Id;
        }

        private void AddToHash(CoverPoint cp)
        {
            long key = HashKey(cp.Position);
            if (!_spatialHash.TryGetValue(key, out var list))
                _spatialHash[key] = list = new List<int>();
            list.Add(cp.Id);
        }

        private long HashKey(Vector3 pos)
        {
            int x = (int)MathF.Floor(pos.X / _hashCellSize);
            int z = (int)MathF.Floor(pos.Z / _hashCellSize);
            return ((long)x << 32) | (uint)z;
        }

        /// <summary>评估掩体质量(对某威胁方向)。</summary>
        public void EvaluateCover(CoverPoint cover, Vector3 threatPos)
        {
            var toThreat = threatPos - cover.Position;
            float dist = toThreat.Length();
            if (dist < 0.01f) { cover.Quality = 0; return; }
            var threatDir = toThreat / dist;
            // 1. 掩体方向匹配
            bool providesCover = cover.ProvidesCoverFrom(threatDir);
            float dirScore = providesCover ? 1f : 0f;
            // 2. 距离威胁适中(太近危险，太远不好打)
            float distScore = 1f - MathF.Abs(dist - 10f) / 15f;
            distScore = Math.Clamp(distScore, 0f, 1f);
            // 3. 高度
            float heightScore = cover.Type == CoverType.High ? 1f : cover.Type == CoverType.Partial ? 0.6f : 0.3f;
            // 4. 实际遮挡测试(射线)
            float losScore = _spatial.IsBlocked(cover.Position, threatPos) ? 1f : 0f;
            cover.Quality = (dirScore * 0.4f + distScore * 0.2f + heightScore * 0.2f + losScore * 0.2f);
        }

        /// <summary>找最佳的掩体(对某威胁)。</summary>
        public CoverPoint? FindBestCover(Vector3 agentPos, Vector3 threatPos, float maxRadius = 20f)
        {
            CoverPoint? best = null;
            float bestQuality = 0f;
            foreach (var cp in QueryNearby(agentPos, maxRadius))
            {
                if (cp.IsOccupied) continue;
                var c = cp;
                EvaluateCover(c, threatPos);
                // 考虑到 agent 的距离(走过去要时间)
                float distToAgent = Vector3.Distance(agentPos, c.Position);
                float reachPenalty = distToAgent / maxRadius;
                c.Quality *= (1f - reachPenalty * 0.3f);
                if (c.Quality > bestQuality)
                {
                    bestQuality = c.Quality;
                    best = c;
                }
            }
            return best;
        }

        /// <summary>查询附近的掩体点。</summary>
        public IEnumerable<CoverPoint> QueryNearby(Vector3 pos, float radius)
        {
            int minX = (int)MathF.Floor((pos.X - radius) / _hashCellSize);
            int maxX = (int)MathF.Floor((pos.X + radius) / _hashCellSize);
            int minZ = (int)MathF.Floor((pos.Z - radius) / _hashCellSize);
            int maxZ = (int)MathF.Floor((pos.Z + radius) / _hashCellSize);
            float rSq = radius * radius;
            for (int x = minX; x <= maxX; x++)
            {
                for (int z = minZ; z <= maxZ; z++)
                {
                    long key = ((long)x << 32) | (uint)z;
                    if (!_spatialHash.TryGetValue(key, out var list)) continue;
                    foreach (var id in list)
                    {
                        if (Vector3.DistanceSquared(pos, _covers[id].Position) <= rSq)
                            yield return _covers[id];
                    }
                }
            }
        }

        /// <summary>占用掩体。</summary>
        public bool Occupy(int coverId, int agentId)
        {
            if (coverId < 0 || coverId >= _covers.Count) return false;
            if (_covers[coverId].IsOccupied) return false;
            var c = _covers[coverId];
            c.IsOccupied = true;
            c.OccupiedBy = agentId;
            _covers[coverId] = c;
            return true;
        }

        /// <summary>释放掩体。</summary>
        public void Release(int coverId, int agentId)
        {
            if (coverId < 0 || coverId >= _covers.Count) return;
            if (_covers[coverId].OccupiedBy != agentId) return;
            var c = _covers[coverId];
            c.IsOccupied = false;
            c.OccupiedBy = 0;
            _covers[coverId] = c;
        }

        /// <summary>自动从场景几何体生成掩体点。</summary>
        public void GenerateFromGeometry(List<Vector3> wallPoints, List<(Vector3 p1, Vector3 p2)> walls)
        {
            // 遍历墙边，在每个墙边两侧生成候选掩体点
            foreach (var (p1, p2) in walls)
            {
                var edge = p2 - p1;
                float edgeLen = edge.Length();
                if (edgeLen < 1f) continue;
                var dir = edge / edgeLen;
                var normal = new Vector3(-dir.Z, 0, dir.X);
                // 沿墙边每隔 1.5m 采样一个点
                int samples = (int)(edgeLen / 1.5f);
                for (int i = 0; i <= samples; i++)
                {
                    var t = (float)i / MathF.Max(1, samples);
                    var point = Vector3.Lerp(p1, p2, t);
                    // 两侧都尝试
                    for (int side = -1; side <= 1; side += 2)
                    {
                        var candidate = point + normal * side * 0.5f;
                        if (!_spatial.IsWalkable(candidate)) continue;
                        AddCover(candidate, normal * side, CoverType.High, 1.8f);
                    }
                }
            }
        }
    }

    // ========================================================================
    //  四、哨点/狙击点
    // ========================================================================

    /// <summary>
    /// 哨点。视野开阔的位置，适合放哨/狙击。
    /// </summary>
    public struct VantagePoint
    {
        public Vector3 Position;
        public float VisibilityScore;   // 0~1, 视野开阔度
        public float CoverageScore;      // 0~1, 能观察的面积比例
        public float HeightAdvantage;    // 相对周围的高度优势
        public List<Vector3> VisibleAreas; // 能看到的区域采样点
        public int Id;

        /// <summary>综合质量分。</summary>
        public float Quality => (VisibilityScore + CoverageScore + HeightAdvantage) / 3f;
    }

    /// <summary>哨点系统。</summary>
    public sealed class VantageSystem
    {
        private readonly List<VantagePoint> _vantages = new();
        private readonly ISpatialQuery _spatial;

        public VantageSystem(ISpatialQuery spatial) { _spatial = spatial; }

        /// <summary>采样评估一个候选哨点。</summary>
        public VantagePoint Evaluate(Vector3 pos, int sampleCount = 16, float sampleRadius = 15f)
        {
            var vp = new VantagePoint { Position = pos, Id = _vantages.Count, VisibleAreas = new() };
            int visibleCount = 0;
            float totalDist = 0;
            for (int i = 0; i < sampleCount; i++)
            {
                float angle = i * (2 * MathF.PI / sampleCount);
                var dir = new Vector3(MathF.Cos(angle), 0, MathF.Sin(angle));
                var target = pos + dir * sampleRadius;
                if (!_spatial.IsBlocked(pos, target))
                {
                    visibleCount++;
                    totalDist += sampleRadius;
                    vp.VisibleAreas.Add(target);
                }
                else
                {
                    // 找到遮挡点距离
                    // 简化：直接加一半距离
                    totalDist += sampleRadius * 0.3f;
                }
            }
            vp.VisibilityScore = (float)visibleCount / sampleCount;
            vp.CoverageScore = (float)visibleCount / sampleCount;
            // 高度优势
            float groundH = _spatial.GetGroundHeight(pos);
            vp.HeightAdvantage = Math.Clamp((pos.Y - groundH) / 5f, 0f, 1f);
            _vantages.Add(vp);
            return vp;
        }

        /// <summary>找最佳哨点。</summary>
        public VantagePoint? FindBestVantage(Vector3 near, float searchRadius = 30f)
        {
            VantagePoint? best = null;
            float bestQ = 0;
            foreach (var vp in _vantages)
            {
                float dist = Vector3.Distance(near, vp.Position);
                if (dist > searchRadius) continue;
                float q = vp.Quality * (1f - dist / searchRadius * 0.3f);
                if (q > bestQ) { bestQ = q; best = vp; }
            }
            return best;
        }
    }

    // ========================================================================
    //  五、瓶颈点
    // ========================================================================

    /// <summary>
    /// 瓶颈点。走廊/门口/桥等狭窄通道，适合伏击/防守。
    /// </summary>
    public struct ChokePoint
    {
        public Vector3 Position;
        public Vector3 Normal;       // 通过方向
        public float Width;          // 通道宽度
        public float Length;         // 通道长度
        public int Id;
        public float StrategicValue; // 战略价值 0~1
    }

    /// <summary>瓶颈点系统。</summary>
    public sealed class ChokePointSystem
    {
        private readonly List<ChokePoint> _chokes = new();

        public int AddChoke(Vector3 pos, Vector3 normal, float width, float length)
        {
            var cp = new ChokePoint
            {
                Id = _chokes.Count,
                Position = pos,
                Normal = normal,
                Width = width,
                Length = length,
                StrategicValue = Math.Clamp(1f - width / 5f, 0f, 1f)
            };
            _chokes.Add(cp);
            return cp.Id;
        }

        /// <summary>找两点之间路径上的所有瓶颈点。</summary>
        public List<ChokePoint> FindChokesOnPath(List<Vector3> path)
        {
            var result = new List<ChokePoint>();
            foreach (var cp in _chokes)
            {
                foreach (var p in path)
                {
                    if (Vector3.Distance(p, cp.Position) < cp.Width * 2)
                    {
                        result.Add(cp);
                        break;
                    }
                }
            }
            return result;
        }
    }

    // ========================================================================
    //  六、视野场
    // ========================================================================

    /// <summary>
    /// 视野场。从某点出发能看到的区域，用极坐标采样。
    /// 用于评估位置战术价值。
    /// </summary>
    public sealed class LineOfSightField
    {
        public Vector3 Origin;
        public float MaxRadius;
        public int AngularResolution = 36; // 每 10 度一个采样
        public float[] Distances;          // 每个角度的最大可见距离
        private readonly ISpatialQuery _spatial;

        public LineOfSightField(Vector3 origin, float maxRadius, ISpatialQuery spatial)
        {
            Origin = origin;
            MaxRadius = maxRadius;
            _spatial = spatial;
            Distances = new float[AngularResolution];
        }

        /// <summary>计算视野场。</summary>
        public void Compute()
        {
            for (int i = 0; i < AngularResolution; i++)
            {
                float angle = i * (2 * MathF.PI / AngularResolution);
                var dir = new Vector3(MathF.Cos(angle), 0, MathF.Sin(angle));
                Distances[i] = RaycastDistance(Origin, dir, MaxRadius);
            }
        }

        private float RaycastDistance(Vector3 origin, Vector3 dir, float maxDist)
        {
            // 简化：二分法找遮挡点
            float lo = 0, hi = maxDist;
            for (int iter = 0; iter < 10; iter++)
            {
                float mid = (lo + hi) * 0.5f;
                var p = origin + dir * mid;
                if (_spatial.IsBlocked(origin, p)) hi = mid;
                else lo = mid;
            }
            return lo;
        }

        /// <summary>检查某点是否在视野内。</summary>
        public bool CanSee(Vector3 target)
        {
            var to = target - Origin;
            to.Y = 0;
            float dist = to.Length();
            if (dist > MaxRadius) return false;
            float angle = MathF.Atan2(to.Z, to.X);
            if (angle < 0) angle += 2 * MathF.PI;
            int idx = (int)(angle / (2 * MathF.PI) * AngularResolution) % AngularResolution;
            return dist <= Distances[idx];
        }

        /// <summary>视野覆盖率(0~1)。</summary>
        public float CoverageRatio()
        {
            float sum = 0;
            for (int i = 0; i < AngularResolution; i++)
                sum += Distances[i];
            return sum / (AngularResolution * MaxRadius);
        }
    }

    // ========================================================================
    //  七、威胁场
    // ========================================================================

    /// <summary>
    /// 威胁场。记录每个区域被敌方火力覆盖的程度。
    /// 高威胁区域 AI 应避免，低威胁区域安全。
    /// </summary>
    public sealed class ThreatField
    {
        private readonly float[,] _grid;
        public int Width, Height;
        public Vector3 Origin;
        public float CellSize;
        private readonly ISpatialQuery _spatial;

        public ThreatField(int w, int h, float cellSize, Vector3 origin, ISpatialQuery spatial)
        {
            Width = w; Height = h; CellSize = cellSize;
            Origin = origin;
            _spatial = spatial;
            _grid = new float[w, h];
        }

        /// <summary>添加一个威胁源(敌人位置 + 火力范围)。</summary>
        public void AddThreat(Vector3 threatPos, float range, float intensity = 1f)
        {
            int cx = (int)((threatPos.X - Origin.X) / CellSize);
            int cz = (int)((threatPos.Z - Origin.Z) / CellSize);
            int r = (int)(range / CellSize);
            for (int x = Math.Max(0, cx - r); x < Math.Min(Width, cx + r + 1); x++)
            {
                for (int z = Math.Max(0, cz - r); z < Math.Min(Height, cz + r + 1); z++)
                {
                    var cellPos = Origin + new Vector3((x + 0.5f) * CellSize, 0, (z + 0.5f) * CellSize);
                    float dist = Vector3.Distance(threatPos, cellPos);
                    if (dist > range) continue;
                    // 检查视线
                    if (_spatial.IsBlocked(threatPos, cellPos)) continue;
                    float falloff = 1f - dist / range;
                    _grid[x, z] += intensity * falloff;
                }
            }
        }

        /// <summary>获取某位置的威胁值。</summary>
        public float GetThreat(Vector3 pos)
        {
            int x = (int)((pos.X - Origin.X) / CellSize);
            int z = (int)((pos.Z - Origin.Z) / CellSize);
            if (x < 0 || x >= Width || z < 0 || z >= Height) return 0;
            return _grid[x, z];
        }

        /// <summary>衰减全场(随时间降低威胁)。</summary>
        public void Decay(float rate)
        {
            for (int x = 0; x < Width; x++)
                for (int z = 0; z < Height; z++)
                    _grid[x, z] *= (1f - rate);
        }

        /// <summary>清空。</summary>
        public void Clear()
        {
            Array.Clear(_grid, 0, _grid.Length);
        }

        /// <summary>找最低威胁的可行走点。</summary>
        public Vector3 FindSafestPoint(Vector3 near, float radius)
        {
            int cx = (int)((near.X - Origin.X) / CellSize);
            int cz = (int)((near.Z - Origin.Z) / CellSize);
            int r = (int)(radius / CellSize);
            Vector3 best = near;
            float minThreat = float.MaxValue;
            for (int x = Math.Max(0, cx - r); x < Math.Min(Width, cx + r + 1); x++)
            {
                for (int z = Math.Max(0, cz - r); z < Math.Min(Height, cz + r + 1); z++)
                {
                    if (_grid[x, z] < minThreat)
                    {
                        minThreat = _grid[x, z];
                        best = Origin + new Vector3((x + 0.5f) * CellSize, 0, (z + 0.5f) * CellSize);
                    }
                }
            }
            return best;
        }
    }

    // ========================================================================
    //  八、侧翼路径
    // ========================================================================

    /// <summary>
    /// 侧翼路径。绕到敌人侧面/背后的路径。
    /// </summary>
    public struct FlankingRoute
    {
        public List<Vector3> Waypoints;
        public Vector3 Target;
        public FlankingSide Side;
        public float Length;
        public float ExposureTime; // 暴露在敌人视野下的时间
        public float StealthScore; // 隐蔽程度 0~1
        public int Id;
    }

    public enum FlankingSide { Left, Right, Both }

    /// <summary>侧翼路径生成器。</summary>
    public sealed class FlankingSystem
    {
        private readonly ISpatialQuery _spatial;
        private readonly ThreatField? _threatField;

        public FlankingSystem(ISpatialQuery spatial, ThreatField? threatField = null)
        {
            _spatial = spatial;
            _threatField = threatField;
        }

        /// <summary>生成侧翼路径。</summary>
        public FlankingRoute? GenerateFlank(Vector3 agentPos, Vector3 targetPos, float flankDist = 10f)
        {
            var toTarget = targetPos - agentPos;
            toTarget.Y = 0;
            float dist = toTarget.Length();
            if (dist < 1f) return null;
            var forward = toTarget / dist;
            var right = new Vector3(-forward.Z, 0, forward.X);

            // 尝试左右两侧
            FlankingRoute? bestRoute = null;
            float bestScore = 0;
            for (int side = -1; side <= 1; side += 2)
            {
                var flankPos = targetPos - forward * (dist * 0.5f) + right * side * flankDist;
                if (!_spatial.IsWalkable(flankPos)) continue;
                // 构建路径：agent -> flankPos -> targetPos 后方
                var behindTarget = targetPos - forward * 3f;
                var route = new List<Vector3> { agentPos, flankPos, behindTarget };
                // 评估
                float exposure = 0;
                float stealth = 0;
                foreach (var wp in route)
                {
                    if (_threatField != null)
                    {
                        float t = _threatField.GetThreat(wp);
                        exposure += t;
                        stealth += (1f - t);
                    }
                }
                float score = stealth - exposure * 0.5f;
                if (score > bestScore)
                {
                    bestScore = score;
                    bestRoute = new FlankingRoute
                    {
                        Waypoints = route,
                        Target = targetPos,
                        Side = side == -1 ? FlankingSide.Left : FlankingSide.Right,
                        Length = ComputeLength(route),
                        ExposureTime = exposure,
                        StealthScore = stealth / route.Count
                    };
                }
            }
            return bestRoute;
        }

        private static float ComputeLength(List<Vector3> path)
        {
            float len = 0;
            for (int i = 1; i < path.Count; i++)
                len += Vector3.Distance(path[i - 1], path[i]);
            return len;
        }
    }

    // ========================================================================
    //  九、空间推理系统主类
    // ========================================================================

    /// <summary>
    /// 空间推理系统。集成所有空间分析能力。
    /// </summary>
    public sealed class SpatialReasoningSystem
    {
        private readonly ISpatialQuery _spatial;
        public CoverSystem CoverSystem;
        public VantageSystem VantageSystem;
        public ChokePointSystem ChokeSystem;
        public FlankingSystem FlankSystem;
        public ThreatField? ThreatField;

        /// <summary>缓存 LOS 检查结果(短期)。</summary>
        private readonly Dictionary<(int, int), (bool blocked, float time)> _losCache = new();
        public float LosCacheDuration = 0.5f;
        private float _time;

        public SpatialReasoningSystem(ISpatialQuery spatial, int threatFieldW = 50, int threatFieldH = 50,
            float cellSize = 2f, Vector3 origin = default)
        {
            _spatial = spatial;
            CoverSystem = new CoverSystem(spatial);
            VantageSystem = new VantageSystem(spatial);
            ChokeSystem = new ChokePointSystem();
            FlankSystem = new FlankingSystem(spatial);
            if (threatFieldW > 0 && threatFieldH > 0)
                ThreatField = new ThreatField(threatFieldW, threatFieldH, cellSize, origin, spatial);
        }

        public void Update(float dt)
        {
            _time += dt;
            ThreatField?.Decay(0.05f * dt);
            // 清理过期缓存
            var keys = new List<(int, int)>(_losCache.Keys);
            foreach (var k in keys)
                if (_time - _losCache[k].time > LosCacheDuration)
                    _losCache.Remove(k);
        }

        /// <summary>带缓存的视线检查。</summary>
        public bool IsBlocked(Vector3 from, Vector3 to, int fromId = -1, int toId = -1)
        {
            if (fromId >= 0 && toId >= 0)
            {
                var key = (Math.Min(fromId, toId), Math.Max(fromId, toId));
                if (_losCache.TryGetValue(key, out var entry) && _time - entry.time < LosCacheDuration)
                    return entry.blocked;
                bool blocked = _spatial.IsBlocked(from, to);
                _losCache[key] = (blocked, _time);
                return blocked;
            }
            return _spatial.IsBlocked(from, to);
        }

        /// <summary>战术分析：找当前最优战术位置。</summary>
        public TacticalPosition FindBestTacticalPosition(Vector3 agentPos, Vector3 threatPos, float searchRadius = 20f)
        {
            var result = new TacticalPosition();
            // 1. 找最近的掩体
            var cover = CoverSystem.FindBestCover(agentPos, threatPos, searchRadius);
            if (cover.HasValue)
            {
                result.Position = cover.Value.GetStandPosition();
                result.HasCover = true;
                result.CoverQuality = cover.Value.Quality;
                result.CoverId = cover.Value.Id;
            }
            else
            {
                // 没掩体，找威胁最低的位置
                if (ThreatField != null)
                    result.Position = ThreatField.FindSafestPoint(agentPos, searchRadius);
                else
                    result.Position = agentPos;
            }
            return result;
        }

        /// <summary>评估某位置的战术价值。</summary>
        public float EvaluatePosition(Vector3 pos, Vector3 threatPos)
        {
            float score = 0;
            // 距离威胁适中
            float dist = Vector3.Distance(pos, threatPos);
            float distScore = 1f - MathF.Abs(dist - 10f) / 20f;
            score += Math.Clamp(distScore, 0, 1) * 0.3f;
            // 威胁低
            if (ThreatField != null)
                score += (1f - Math.Clamp(ThreatField.GetThreat(pos), 0, 1)) * 0.4f;
            // 附近有掩体
            var covers = CoverSystem.QueryNearby(pos, 3f);
            float bestCover = 0;
            foreach (var c in covers) bestCover = MathF.Max(bestCover, c.Quality);
            score += bestCover * 0.3f;
            return score;
        }
    }

    /// <summary>战术位置。</summary>
    public struct TacticalPosition
    {
        public Vector3 Position;
        public bool HasCover;
        public float CoverQuality;
        public int CoverId;
        public float ThreatLevel;
        public float VisibilityScore;
    }
}
