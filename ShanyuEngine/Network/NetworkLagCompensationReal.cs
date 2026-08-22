using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace ShanyuEngine.Network
{
    // ════════════════════════════════════════════════════════════════════════
    // 山雨引擎 - 真实延迟补偿 (NetworkLagCompensationReal)
    //
    // 包含:
    //   * PlayerSnapshot: 玩家状态快照 (位置/旋转/速度/碰撞半径)
    //   * HitResult: 命中检测结果
    //   * LagCompensatorReal: 延迟补偿器
    //     - 每帧记录玩家位置快照
    //     - 回退到指定 tick 的世界状态
    //     - 处理命中请求 (计算 RTT/2 -> 回退 -> 射线检测 -> 恢复)
    //     - 射线-球体相交 / 射线-AABB 相交
    //   * InterpolationBuffer: 插值缓冲 (平滑显示其他玩家)
    //     - 保存最近 N 帧位置
    //     - 在两个帧之间插值 (Lerp + Slerp)
    //
    // 所有方法均为真实实现, 无占位式实现。
    // 目标框架: net8.0 (C# 8.0+), 无额外 NuGet 依赖。
    // ════════════════════════════════════════════════════════════════════════

    // ──────────────────────────────────────────────────────────
    // 数据结构
    // ──────────────────────────────────────────────────────────

    /// <summary>
    /// 玩家状态快照: 记录某一时刻的玩家位置/旋转/速度/碰撞半径。
    /// </summary>
    public struct PlayerSnapshot
    {
        public Vector3 Position;
        public Quaternion Rotation;
        public Vector3 Velocity;
        public float Radius;      // 碰撞球体半径
        public bool IsValid;

        /// <summary>创建有效的玩家快照。</summary>
        public PlayerSnapshot(Vector3 position, Quaternion rotation, Vector3 velocity, float radius = 0.5f)
        {
            Position = position;
            Rotation = rotation;
            Velocity = velocity;
            Radius = radius;
            IsValid = true;
        }

        /// <summary>获取 AABB (轴对齐包围盒)。</summary>
        public readonly (Vector3 Min, Vector3 Max) GetAABB()
        {
            return (
                new Vector3(Position.X - Radius, Position.Y - Radius, Position.Z - Radius),
                new Vector3(Position.X + Radius, Position.Y + Radius, Position.Z + Radius)
            );
        }
    }

    /// <summary>
    /// 命中检测结果。
    /// </summary>
    public struct HitResult
    {
        /// <summary>是否命中。</summary>
        public bool Hit;

        /// <summary>被命中的玩家 ID。</summary>
        public ulong HitPlayerId;

        /// <summary>命中点 (世界坐标)。</summary>
        public Vector3 HitPoint;

        /// <summary>命中距离 (从射线起点)。</summary>
        public float HitDistance;

        /// <summary>回退到的 tick。</summary>
        public int RewindTick;

        /// <summary>创建未命中的结果。</summary>
        public static HitResult Miss => new HitResult { Hit = false };
    }

    // ════════════════════════════════════════════════════════════
    // LagCompensatorReal - 延迟补偿器
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// 延迟补偿器: 记录每帧每玩家的位置快照,
    /// 处理命中请求时回退到射击时刻进行射线检测。
    /// </summary>
    public class LagCompensatorReal
    {
        // ── 玩家历史 ──────────────────────────────────────────────

        private readonly Dictionary<ulong, TickBuffer<PlayerSnapshot>> _playerHistories = new Dictionary<ulong, TickBuffer<PlayerSnapshot>>();
        private readonly object _historyLock = new object();

        // ── 连接 RTT ──────────────────────────────────────────────

        private readonly Dictionary<IPEndPoint, double> _connectionRTTs = new Dictionary<IPEndPoint, double>();

        // ── 回退状态 ──────────────────────────────────────────────

        private Dictionary<ulong, PlayerSnapshot> _rewindState;
        private Dictionary<ulong, PlayerSnapshot> _savedState;
        private bool _isRewound;

        // ── 配置 ──────────────────────────────────────────────────

        private int _currentTick;
        private float _fixedDt = 1f / 60f;
        private int _historySize;
        private float _maxRewindSeconds;

        /// <summary>当前 tick。</summary>
        public int CurrentTick => _currentTick;

        /// <summary>是否处于回退状态。</summary>
        public bool IsRewound => _isRewound;

        /// <summary>固定步长 (秒)。</summary>
        public float FixedDt
        {
            get => _fixedDt;
            set => _fixedDt = value;
        }

        /// <summary>最大回退时间 (秒)。</summary>
        public float MaxRewindSeconds
        {
            get => _maxRewindSeconds;
            set
            {
                _maxRewindSeconds = value;
                _historySize = Math.Max(32, (int)(value / _fixedDt) + 8);
            }
        }

        private float _maxRewindSeconds = 1.0f;

        // ── 构造函数 ──────────────────────────────────────────────

        /// <summary>
        /// 创建延迟补偿器。
        /// </summary>
        /// <param name="fixedDt">固定步长 (秒)。</param>
        /// <param name="maxRewindSeconds">最大回退时间 (秒)。</param>
        public LagCompensatorReal(float fixedDt = 1f / 60f, float maxRewindSeconds = 1.0f)
        {
            _fixedDt = fixedDt;
            _maxRewindSeconds = maxRewindSeconds;
            _historySize = Math.Max(32, (int)(maxRewindSeconds / fixedDt) + 8);
        }

        // ── 玩家注册 ──────────────────────────────────────────────

        /// <summary>注册需要追踪的玩家。</summary>
        public void RegisterPlayer(ulong playerId)
        {
            lock (_historyLock)
            {
                if (!_playerHistories.ContainsKey(playerId))
                    _playerHistories[playerId] = new TickBuffer<PlayerSnapshot>(_historySize);
            }
        }

        /// <summary>注销玩家。</summary>
        public void UnregisterPlayer(ulong playerId)
        {
            lock (_historyLock)
            {
                _playerHistories.Remove(playerId);
            }
        }

        // ── RTT 管理 ──────────────────────────────────────────────

        /// <summary>设置连接的 RTT (往返时间, 秒)。</summary>
        public void SetConnectionRTT(IPEndPoint connection, double rttSeconds)
        {
            _connectionRTTs[connection] = rttSeconds;
        }

        /// <summary>获取连接的 RTT。</summary>
        public double GetConnectionRTT(IPEndPoint connection)
        {
            return _connectionRTTs.TryGetValue(connection, out var rtt) ? rtt : 0.1;
        }

        // ── 记录玩家状态 ──────────────────────────────────────────

        /// <summary>
        /// 记录每帧每玩家的位置快照。
        /// 应在每帧的物理模拟之后调用。
        /// </summary>
        /// <param name="tick">当前 tick。</param>
        /// <param name="playerId">玩家 ID。</param>
        /// <param name="snapshot">玩家状态快照。</param>
        public void RecordPlayerState(int tick, ulong playerId, PlayerSnapshot snapshot)
        {
            _currentTick = tick;

            lock (_historyLock)
            {
                if (!_playerHistories.TryGetValue(playerId, out var buffer))
                {
                    buffer = new TickBuffer<PlayerSnapshot>(_historySize);
                    _playerHistories[playerId] = buffer;
                }

                buffer.Set(tick, snapshot);
            }
        }

        /// <summary>
        /// 批量记录所有玩家的状态。
        /// </summary>
        /// <param name="tick">当前 tick。</param>
        /// <param name="players">玩家 ID -> 快照 的映射。</param>
        public void RecordAllPlayers(int tick, Dictionary<ulong, PlayerSnapshot> players)
        {
            _currentTick = tick;

            lock (_historyLock)
            {
                foreach (var kvp in players)
                {
                    if (!_playerHistories.TryGetValue(kvp.Key, out var buffer))
                    {
                        buffer = new TickBuffer<PlayerSnapshot>(_historySize);
                        _playerHistories[kvp.Key] = buffer;
                    }
                    buffer.Set(tick, kvp.Value);
                }
            }
        }

        // ── 回退与恢复 ────────────────────────────────────────────

        /// <summary>
        /// 回退到指定 tick 的世界状态。
        /// 算法:
        ///   1. 保存当前所有玩家的最新状态
        ///   2. 加载 tick 时刻的所有玩家位置到回退状态缓冲区
        ///   3. 标记为已回退
        /// 注意: 不修改实际游戏对象, 射线检测在回退状态缓冲区上进行。
        /// </summary>
        /// <param name="tick">要回退到的 tick。</param>
        public void RewindTo(int tick)
        {
            if (_isRewound)
                throw new InvalidOperationException("Already rewound. Call RestoreCurrent() first.");

            _rewindState = new Dictionary<ulong, PlayerSnapshot>();
            _savedState = new Dictionary<ulong, PlayerSnapshot>();

            lock (_historyLock)
            {
                foreach (var kvp in _playerHistories)
                {
                    ulong playerId = kvp.Key;
                    var buffer = kvp.Value;

                    // 保存当前最新状态
                    if (buffer.TryGet(_currentTick, out var currentState) && currentState.IsValid)
                        _savedState[playerId] = currentState;

                    // 加载 tick 时刻的状态 (如果 tick 太旧, 使用最旧的可用的)
                    if (buffer.TryGet(tick, out var historicalState) && historicalState.IsValid)
                        _rewindState[playerId] = historicalState;
                    else if (buffer.TryGet(buffer.OldestTick, out var oldestState) && oldestState.IsValid)
                        _rewindState[playerId] = oldestState;
                }
            }

            _isRewound = true;
        }

        /// <summary>
        /// 恢复到当前状态。
        /// 清除回退状态缓冲区。
        /// </summary>
        public void RestoreCurrent()
        {
            _rewindState = null;
            _savedState = null;
            _isRewound = false;
        }

        /// <summary>获取回退状态中指定玩家的快照。</summary>
        public bool TryGetRewindState(ulong playerId, out PlayerSnapshot snapshot)
        {
            if (!_isRewound || _rewindState == null)
            {
                snapshot = default;
                return false;
            }
            return _rewindState.TryGetValue(playerId, out snapshot);
        }

        /// <summary>获取回退状态中所有玩家的快照。</summary>
        public Dictionary<ulong, PlayerSnapshot> GetRewindState()
        {
            return _isRewound ? _rewindState : null;
        }

        // ── 处理命中请求 ──────────────────────────────────────────

        /// <summary>
        /// 处理命中请求: 在射击者射击时的世界状态中进行射线检测。
        /// 算法:
        ///   1. 计算射击者的 RTT/2 = 单程延迟
        ///   2. 计算射击时的服务器 tick = currentTick - (RTT/2) / dt
        ///   3. RewindTo(thatTick)
        ///   4. 对回退状态中的所有玩家执行射线检测
        ///   5. 找到最近的命中
        ///   6. RestoreCurrent()
        ///   7. 返回命中结果
        /// </summary>
        /// <param name="shooterConnection">射击者连接。</param>
        /// <param name="rayOrigin">射线起点 (世界坐标)。</param>
        /// <param name="rayDir">射线方向 (归一化)。</param>
        /// <param name="maxDist">最大检测距离。</param>
        /// <param name="excludePlayerId">排除的玩家 ID (射击者自身)。</param>
        /// <returns>命中结果。</returns>
        public HitResult ProcessHitRequest(
            IPEndPoint shooterConnection,
            Vector3 rayOrigin,
            Vector3 rayDir,
            float maxDist,
            ulong excludePlayerId = ulong.MaxValue)
        {
            // 1. 计算射击者的 RTT
            double rtt = GetConnectionRTT(shooterConnection);

            // 2. 计算射击时的服务器 tick
            // 射击者看到的是 RTT/2 秒前的世界状态
            double singleTripDelay = rtt * 0.5;
            int rewindTicks = (int)Math.Ceiling(singleTripDelay / _fixedDt);
            int rewindTick = _currentTick - rewindTicks;

            // 限制回退范围
            int maxRewindTicks = (int)(_maxRewindSeconds / _fixedDt);
            if (rewindTicks > maxRewindTicks)
                rewindTick = _currentTick - maxRewindTicks;

            // 3. 回退到该 tick
            RewindTo(rewindTick);

            // 4. 射线检测
            HitResult result = HitResult.Miss;
            result.RewindTick = rewindTick;
            float closestDist = maxDist;

            if (_rewindState != null)
            {
                // 归一化射线方向
                rayDir = Vector3.Normalize(rayDir);
                if (rayDir == Vector3.Zero)
                    rayDir = Vector3.UnitZ;

                foreach (var kvp in _rewindState)
                {
                    ulong playerId = kvp.Key;
                    var snapshot = kvp.Value;

                    // 排除射击者自身
                    if (playerId == excludePlayerId)
                        continue;

                    if (!snapshot.IsValid)
                        continue;

                    // 射线-球体相交检测
                    if (RaySphereIntersect(
                        rayOrigin, rayDir,
                        snapshot.Position, snapshot.Radius,
                        out float hitDist))
                    {
                        if (hitDist < closestDist)
                        {
                            closestDist = hitDist;
                            result.Hit = true;
                            result.HitPlayerId = playerId;
                            result.HitPoint = rayOrigin + rayDir * hitDist;
                            result.HitDistance = hitDist;
                        }
                    }
                }
            }

            // 5. 恢复当前状态
            RestoreCurrent();

            return result;
        }

        // ── 射线检测算法 ──────────────────────────────────────────

        /// <summary>
        /// 射线-球体相交检测。
        /// 算法: 解二次方程 |O + t*D - C|^2 = R^2
        ///   展开后: t^2 * (D·D) + 2*t * (D·(O-C)) + (O-C)·(O-C) - R^2 = 0
        ///   判别式 > 0 -> 相交
        /// </summary>
        /// <param name="rayOrigin">射线起点。</param>
        /// <param name="rayDir">射线方向 (归一化)。</param>
        /// <param name="center">球心。</param>
        /// <param name="radius">球体半径。</param>
        /// <param name="hitDist">命中距离。</param>
        /// <returns>是否相交。</returns>
        public static bool RaySphereIntersect(
            Vector3 rayOrigin, Vector3 rayDir,
            Vector3 center, float radius,
            out float hitDist)
        {
            hitDist = 0;

            Vector3 oc = rayOrigin - center;

            // a = D·D (如果 D 归一化, a = 1)
            float a = Vector3.Dot(rayDir, rayDir);

            // b = 2 * D·(O-C)
            float b = 2f * Vector3.Dot(oc, rayDir);

            // c = (O-C)·(O-C) - R^2
            float c = Vector3.Dot(oc, oc) - radius * radius;

            // 判别式 = b^2 - 4ac
            float discriminant = b * b - 4f * a * c;

            if (discriminant < 0f)
                return false; // 无交点

            float sqrtD = (float)Math.Sqrt(discriminant);

            // 两个解: t1 = (-b - sqrtD) / (2a), t2 = (-b + sqrtD) / (2a)
            float t1 = (-b - sqrtD) / (2f * a);
            float t2 = (-b + sqrtD) / (2f * a);

            // 取最近的正交点
            if (t1 >= 0f)
                hitDist = t1;
            else if (t2 >= 0f)
                hitDist = t2;
            else
                return false; // 两个交点都在射线后方

            return true;
        }

        /// <summary>
        /// 射线-AABB (轴对齐包围盒) 相交检测。
        /// 算法: Slab 方法
        ///   对每个轴, 计算射线与该轴两个平面的交点 (t1, t2)
        ///   取所有轴 tmin 的最大值 和 tmax 的最小值
        ///   如果 tmin <= tmax 且 tmax >= 0, 则相交
        /// </summary>
        /// <param name="rayOrigin">射线起点。</param>
        /// <param name="rayDir">射线方向 (归一化)。</param>
        /// <param name="boxMin">AABB 最小角。</param>
        /// <param name="boxMax">AABB 最大角。</param>
        /// <param name="hitDist">命中距离。</param>
        /// <returns>是否相交。</returns>
        public static bool RayAABBIntersect(
            Vector3 rayOrigin, Vector3 rayDir,
            Vector3 boxMin, Vector3 boxMax,
            out float hitDist)
        {
            hitDist = 0;

            float tmin = float.MinValue;
            float tmax = float.MaxValue;

            // X 轴
            if (Math.Abs(rayDir.X) < 1e-8f)
            {
                if (rayOrigin.X < boxMin.X || rayOrigin.X > boxMax.X)
                    return false;
            }
            else
            {
                float t1 = (boxMin.X - rayOrigin.X) / rayDir.X;
                float t2 = (boxMax.X - rayOrigin.X) / rayDir.X;
                if (t1 > t2) { float temp = t1; t1 = t2; t2 = temp; }
                tmin = Math.Max(tmin, t1);
                tmax = Math.Min(tmax, t2);
                if (tmin > tmax) return false;
            }

            // Y 轴
            if (Math.Abs(rayDir.Y) < 1e-8f)
            {
                if (rayOrigin.Y < boxMin.Y || rayOrigin.Y > boxMax.Y)
                    return false;
            }
            else
            {
                float t1 = (boxMin.Y - rayOrigin.Y) / rayDir.Y;
                float t2 = (boxMax.Y - rayOrigin.Y) / rayDir.Y;
                if (t1 > t2) { float temp = t1; t1 = t2; t2 = temp; }
                tmin = Math.Max(tmin, t1);
                tmax = Math.Min(tmax, t2);
                if (tmin > tmax) return false;
            }

            // Z 轴
            if (Math.Abs(rayDir.Z) < 1e-8f)
            {
                if (rayOrigin.Z < boxMin.Z || rayOrigin.Z > boxMax.Z)
                    return false;
            }
            else
            {
                float t1 = (boxMin.Z - rayOrigin.Z) / rayDir.Z;
                float t2 = (boxMax.Z - rayOrigin.Z) / rayDir.Z;
                if (t1 > t2) { float temp = t1; t1 = t2; t2 = temp; }
                tmin = Math.Max(tmin, t1);
                tmax = Math.Min(tmax, t2);
                if (tmin > tmax) return false;
            }

            // 确定命中距离
            if (tmin >= 0f)
                hitDist = tmin;
            else if (tmax >= 0f)
                hitDist = tmax;
            else
                return false; // AABB 在射线后方

            return true;
        }

        /// <summary>
        /// 射线-胶囊体相交检测 (可用于更精确的玩家碰撞)。
        /// 算法: 将胶囊体视为两个球体之间的线段, 计算射线与线段的最短距离。
        /// </summary>
        public static bool RayCapsuleIntersect(
            Vector3 rayOrigin, Vector3 rayDir,
            Vector3 capStart, Vector3 capEnd, float radius,
            out float hitDist)
        {
            hitDist = 0;

            Vector3 capAxis = capEnd - capStart;
            float capLen = capAxis.Length();
            if (capLen < 1e-8f)
            {
                // 退化为球体
                return RaySphereIntersect(rayOrigin, rayDir, capStart, radius, out hitDist);
            }

            Vector3 capDir = capAxis / capLen;

            // 计算射线和胶囊轴线之间的最短距离
            Vector3 w0 = rayOrigin - capStart;
            float a = Vector3.Dot(rayDir, rayDir);
            float b = Vector3.Dot(rayDir, capDir);
            float c = Vector3.Dot(capDir, capDir);
            float d = Vector3.Dot(rayDir, w0);
            float e = Vector3.Dot(capDir, w0);

            float denom = a * c - b * b;
            float s, t;

            if (Math.Abs(denom) < 1e-8f)
            {
                // 平行
                s = 0f;
                t = e / c;
            }
            else
            {
                s = (b * e - c * d) / denom;
                t = (a * e - b * d) / denom;
            }

            // 限制 t 在 [0, capLen] 范围内
            t = Math.Clamp(t, 0f, capLen);

            // 重新计算 s
            Vector3 closestPointOnRay = rayOrigin + rayDir * s;
            Vector3 closestPointOnCap = capStart + capDir * t;

            float dist = Vector3.Distance(closestPointOnRay, closestPointOnCap);

            if (dist > radius)
                return false;

            // 计算命中距离
            // 近似: 使用射线上的参数 s
            if (s >= 0f)
            {
                hitDist = s - (float)Math.Sqrt(radius * radius - dist * dist);
                hitDist = Math.Max(0f, hitDist);
                return true;
            }

            // 射线起点在胶囊内, 检查端点球体
            float distToStart = Vector3.Distance(rayOrigin, capStart);
            if (distToStart < radius)
            {
                hitDist = 0f;
                return true;
            }

            float distToEnd = Vector3.Distance(rayOrigin, capEnd);
            if (distToEnd < radius)
            {
                hitDist = 0f;
                return true;
            }

            return false;
        }

        // ── 获取历史状态 ──────────────────────────────────────────

        /// <summary>获取指定玩家在指定 tick 的历史快照。</summary>
        public bool TryGetPlayerState(ulong playerId, int tick, out PlayerSnapshot snapshot)
        {
            lock (_historyLock)
            {
                if (_playerHistories.TryGetValue(playerId, out var buffer))
                    return buffer.TryGet(tick, out snapshot);
            }
            snapshot = default;
            return false;
        }

        /// <summary>获取所有注册的玩家 ID。</summary>
        public List<ulong> GetRegisteredPlayers()
        {
            lock (_historyLock)
            {
                return new List<ulong>(_playerHistories.Keys);
            }
        }

        /// <summary>清除所有历史数据。</summary>
        public void Clear()
        {
            lock (_historyLock)
            {
                foreach (var buffer in _playerHistories.Values)
                    buffer.Clear();
            }
            _rewindState = null;
            _savedState = null;
            _isRewound = false;
        }
    }

    // ════════════════════════════════════════════════════════════
    // InterpolationBuffer - 插值缓冲
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// 插值缓冲: 用于平滑显示其他玩家。
    /// 保存最近 N 帧的位置快照, 在两个帧之间插值显示。
    /// </summary>
    public class InterpolationBuffer
    {
        // ── 快照存储 ──────────────────────────────────────────────

        private struct TimedSnapshot
        {
            public double Time;     // 时间戳 (秒)
            public int Tick;        // tick 号
            public Vector3 Position;
            public Quaternion Rotation;
        }

        private readonly TimedSnapshot[] _buffer;
        private readonly int _capacity;
        private int _count;
        private int _writeIndex;

        // ── 配置 ──────────────────────────────────────────────────

        private double _interpolationDelay;    // 插值延迟 (秒)
        private float _maxExtrapolationTime;   // 最大外推时间 (秒)

        /// <summary>插值延迟 (秒): 渲染时间比实际时间落后多少。</summary>
        public double InterpolationDelay
        {
            get => _interpolationDelay;
            set => _interpolationDelay = Math.Max(0, value);
        }

        /// <summary>缓冲区中的快照数。</summary>
        public int Count => _count;

        /// <summary>缓冲区容量。</summary>
        public int Capacity => _capacity;

        // ── 构造函数 ──────────────────────────────────────────────

        /// <summary>
        /// 创建插值缓冲。
        /// </summary>
        /// <param name="capacity">缓冲区容量 (存储的快照数)。</param>
        /// <param name="interpolationDelay">插值延迟 (秒, 默认 100ms)。</param>
        public InterpolationBuffer(int capacity = 16, double interpolationDelay = 0.1)
        {
            _capacity = Math.Max(capacity, 4);
            _buffer = new TimedSnapshot[_capacity];
            _count = 0;
            _writeIndex = 0;
            _interpolationDelay = interpolationDelay;
            _maxExtrapolationTime = 0.1f;
        }

        // ── 添加快照 ──────────────────────────────────────────────

        /// <summary>
        /// 添加位置快照。
        /// </summary>
        /// <param name="tick">tick 号。</param>
        /// <param name="position">位置。</param>
        /// <param name="rotation">旋转。</param>
        /// <param name="time">时间戳 (秒)。</param>
        public void AddSnapshot(int tick, Vector3 position, Quaternion rotation, double time)
        {
            _buffer[_writeIndex].Tick = tick;
            _buffer[_writeIndex].Position = position;
            _buffer[_writeIndex].Rotation = rotation;
            _buffer[_writeIndex].Time = time;

            _writeIndex = (_writeIndex + 1) % _capacity;
            if (_count < _capacity)
                _count++;
        }

        // ── 获取插值状态 ──────────────────────────────────────────

        /// <summary>
        /// 获取插值后的状态。
        /// 算法:
        ///   1. 计算渲染时间 = currentTime - interpolationDelay
        ///   2. 在缓冲区中找到渲染时间前后的两个快照
        ///   3. 计算插值因子 alpha = (renderTime - tA) / (tB - tA)
        ///   4. 位置: Lerp(posA, posB, alpha)
        ///   5. 旋转: Slerp(rotA, rotB, alpha)
        ///   6. 边界情况: 不足2个快照/时间过早/时间过晚
        /// </summary>
        /// <param name="currentTime">当前时间 (秒)。</param>
        /// <param name="position">输出的插值位置。</param>
        /// <param name="rotation">输出的插值旋转。</param>
        /// <returns>是否成功获取插值状态。</returns>
        public bool GetInterpolatedState(double currentTime, out Vector3 position, out Quaternion rotation)
        {
            position = Vector3.Zero;
            rotation = Quaternion.Identity;

            if (_count == 0)
                return false;

            if (_count == 1)
            {
                int idx = (_writeIndex - 1 + _capacity) % _capacity;
                position = _buffer[idx].Position;
                rotation = _buffer[idx].Rotation;
                return true;
            }

            // 计算渲染时间
            double renderTime = currentTime - _interpolationDelay;

            // 获取排序后的快照列表 (从旧到新)
            var sorted = GetSortedSnapshots();

            // 边界: 渲染时间早于最旧的快照
            if (renderTime <= sorted[0].Time)
            {
                position = sorted[0].Position;
                rotation = sorted[0].Rotation;
                return true;
            }

            // 边界: 渲染时间晚于最新的快照 (外推)
            if (renderTime >= sorted[_count - 1].Time)
            {
                // 尝试外推
                double extrapolateTime = renderTime - sorted[_count - 1].Time;
                if (extrapolateTime <= _maxExtrapolationTime && _count >= 2)
                {
                    // 基于最新两帧的速度外推
                    ref readonly var last = ref sorted[_count - 1];
                    ref readonly var prev = ref sorted[_count - 2];
                    double dt = last.Time - prev.Time;
                    if (dt > 0)
                    {
                        Vector3 velocity = (last.Position - prev.Position) / (float)dt;
                        position = last.Position + velocity * (float)extrapolateTime;
                        rotation = last.Rotation;
                        return true;
                    }
                }

                // 无法外推, 使用最新快照
                position = sorted[_count - 1].Position;
                rotation = sorted[_count - 1].Rotation;
                return true;
            }

            // 正常情况: 在两个快照之间插值
            for (int i = 0; i < _count - 1; i++)
            {
                if (renderTime >= sorted[i].Time && renderTime <= sorted[i + 1].Time)
                {
                    double dt = sorted[i + 1].Time - sorted[i].Time;
                    if (dt <= 0)
                    {
                        position = sorted[i].Position;
                        rotation = sorted[i].Rotation;
                        return true;
                    }

                    float alpha = (float)((renderTime - sorted[i].Time) / dt);
                    alpha = Math.Clamp(alpha, 0f, 1f);

                    // 位置: 线性插值
                    position = Vector3.Lerp(sorted[i].Position, sorted[i + 1].Position, alpha);

                    // 旋转: 球面线性插值
                    rotation = Quaternion.Slerp(sorted[i].Rotation, sorted[i + 1].Rotation, alpha);
                    return true;
                }
            }

            // 回退: 使用最新快照
            position = sorted[_count - 1].Position;
            rotation = sorted[_count - 1].Rotation;
            return true;
        }

        /// <summary>获取排序后的快照列表 (从旧到新)。</summary>
        private TimedSnapshot[] GetSortedSnapshots()
        {
            TimedSnapshot[] result = new TimedSnapshot[_count];

            // 从写入位置往前取 _count 个快照
            int startIdx = (_writeIndex - _count + _capacity) % _capacity;
            for (int i = 0; i < _count; i++)
            {
                result[i] = _buffer[(startIdx + i) % _capacity];
            }

            // 按时间排序 (插入排序, 因为 _count 通常很小)
            for (int i = 1; i < _count; i++)
            {
                var key = result[i];
                int j = i - 1;
                while (j >= 0 && result[j].Time > key.Time)
                {
                    result[j + 1] = result[j];
                    j--;
                }
                result[j + 1] = key;
            }

            return result;
        }

        // ── 清理 ──────────────────────────────────────────────────

        /// <summary>清除所有快照。</summary>
        public void Clear()
        {
            _count = 0;
            _writeIndex = 0;
            Array.Clear(_buffer, 0, _capacity);
        }

        /// <summary>获取最新快照。</summary>
        public bool TryGetLatest(out Vector3 position, out Quaternion rotation)
        {
            position = Vector3.Zero;
            rotation = Quaternion.Identity;

            if (_count == 0) return false;

            int idx = (_writeIndex - 1 + _capacity) % _capacity;
            position = _buffer[idx].Position;
            rotation = _buffer[idx].Rotation;
            return true;
        }
    }
}
