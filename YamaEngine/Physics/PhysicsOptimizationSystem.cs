using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Linq;

namespace YamaEngine.Physics
{
    // ============================================================================================
    //  PhysicsOptimizationSystem.cs
    //
    //  物理优化系统。包含连续碰撞检测(CCD)、时间步优化、LOD(细节层次)管理、
    //  空间哈希缓存、预测-校正、刚体稳定性优化等。
    //
    //  CCD(Continuous Collision Detection)是核心功能:
    //  - 防止快速移动的物体穿透(隧道效应)
    //  - 用Sweep-and-Prune或者球面扫掠(Sphere Sweep)检测
    //  - 找到第一个碰撞时刻(ToI, Time of Impact)
    //
    //  用法:
    //    var ccd = new ContinuousCollisionDetector();
    //    ccd.EnableCcd(body);
    //    float toi = ccd.ComputeTimeOfImpact(bodyA, bodyB, dt);
    //    if (toi < dt) {
    //        // 在toi时刻会发生碰撞，需要提前处理
    //    }
    //
    //  注意: CCD开销大，只给快速移动的物体开。子弹、高速载具这种。
    //  TODO: 支持非球形的CCD(目前只有球-球、球-静态)。
    // ============================================================================================

    /// <summary>CCD方法类型。</summary>
    public enum CcdMethod
    {
        /// <summary>禁用CCD。</summary>
        Disabled,
        /// <summary>球面扫掠(保守)。把物体当成球来处理。</summary>
        SphereSweep,
        /// <summary>线性扫掠。只考虑平移，不考虑旋转。</summary>
        LinearSweep,
        /// <summary>轴对齐扫掠。把物体简化成AABB扫掠。</summary>
        AabbSweep,
        /// <summary>子步进。把大时间步分成多个小步。</summary>
        Substepping,
        /// <summary>时间-of-Impact。精确求碰撞时刻。</summary>
        TimeOfImpact,
        /// <summary>混合方法。根据速度自适应选择。</summary>
        Adaptive,
    }

    /// <summary>CCD配置。</summary>
    public class CcdConfig
    {
        /// <summary>全局CCD方法。</summary>
        public CcdMethod Method = CcdMethod.SphereSweep;

        /// <summary>启用CCD的速度阈值。速度低于此值的body不做CCD。</summary>
        public float VelocityThreshold = 5f;

        /// <summary>CCD的精度(0-1)。越高越精确但越慢。</summary>
        public float Precision = 0.9f;

        /// <summary>最大CCD迭代次数。</summary>
        public int MaxIterations = 16;

        /// <summary>最大子步数量。</summary>
        public int MaxSubsteps = 8;

        /// <summary>最小子步时间。</summary>
        public float MinSubstepTime = 1f / 240f;

        /// <summary>穿透容差。</summary>
        public float PenetrationTolerance = 0.01f;

        /// <summary>球面扫掠的保守系数(>1表示更保守)。</summary>
        public float ConservativeFactor = 1.1f;

        /// <summary>是否在碰撞前减速(而不是瞬间停止)。</summary>
        public bool DecelerateBeforeImpact = false;

        /// <summary>减速系数。</summary>
        public float DecelerationFactor = 0.5f;

        /// <summary>是否记录CCD的碰撞事件。</summary>
        public bool RecordEvents = true;

        /// <summary>最大事件记录数量。</summary>
        public int MaxRecordedEvents = 1000;

        public static CcdConfig Default => new();
        public static CcdConfig HighPrecision => new()
        {
            Method = CcdMethod.TimeOfImpact,
            Precision = 0.99f,
            MaxIterations = 32,
            MaxSubsteps = 16,
        };
        public static CcdConfig Fast => new()
        {
            Method = CcdMethod.SphereSweep,
            Precision = 0.7f,
            MaxIterations = 4,
            MaxSubsteps = 2,
        };
    }

    /// <summary>CCD查询结果。</summary>
    public struct CcdResult
    {
        /// <summary>是否检测到碰撞。</summary>
        public bool HasCollision;

        /// <summary>碰撞时刻(Time of Impact)，相对于本帧开始的时间。</summary>
        public float TimeOfImpact;

        /// <summary>碰撞点(世界坐标)。</summary>
        public Vector3 ContactPoint;

        /// <summary>碰撞法线。</summary>
        public Vector3 ContactNormal;

        /// <summary>穿透深度。</summary>
        public float PenetrationDepth;

        /// <summary>使用的检测方法。</summary>
        public CcdMethod Method;

        /// <summary>实际迭代次数。</summary>
        public int Iterations;

        /// <summary>检测耗时(微秒)。</summary>
        public long TimeMicroseconds;

        /// <summary>参与碰撞的两个body id。</summary>
        public int BodyIdA;
        public int BodyIdB;

        public static CcdResult NoCollision => new()
        {
            HasCollision = false,
            TimeOfImpact = float.MaxValue,
        };
    }

    /// <summary>CCD事件(记录一次CCD检测到的碰撞)。</summary>
    public struct CcdEvent
    {
        public int BodyIdA;
        public int BodyIdB;
        public float TimeOfImpact;
        public Vector3 ContactPoint;
        public Vector3 ContactNormal;
        public float SpeedAtImpact;
        public float FrameTime;
        public double Timestamp;
        public CcdMethod Method;
    }

    // ============================================================================================
    //  连续碰撞检测器
    // ============================================================================================

    /// <summary>
    /// 连续碰撞检测器。防止快速移动的物体穿透。
    /// </summary>
    public class ContinuousCollisionDetector
    {
        private CcdConfig _config = new();
        private readonly List<CcdEvent> _events = new();
        private readonly HashSet<int> _ccdEnabledBodies = new();
        private long _totalChecks;
        private long _totalCollisions;
        private double _totalTimeMs;
        private double _maxTimeMs;

        public CcdConfig Config
        {
            get => _config;
            set => _config = value ?? new CcdConfig();
        }

        public IReadOnlyList<CcdEvent> Events => _events;
        public long TotalChecks => _totalChecks;
        public long TotalCollisions => _totalCollisions;
        public double AvgTimeMs => _totalChecks > 0 ? _totalTimeMs / _totalChecks : 0;
        public double MaxTimeMs => _maxTimeMs;

        // ========================================================================================
        //  Body管理
        // ========================================================================================

        /// <summary>给body启用CCD。</summary>
        public void EnableCcd(int bodyId)
        {
            _ccdEnabledBodies.Add(bodyId);
        }

        /// <summary>给body禁用CCD。</summary>
        public void DisableCcd(int bodyId)
        {
            _ccdEnabledBodies.Remove(bodyId);
        }

        /// <summary>检查body是否启用了CCD。</summary>
        public bool IsCcdEnabled(int bodyId)
        {
            return _ccdEnabledBodies.Contains(bodyId);
        }

        /// <summary>清除所有CCD启用。</summary>
        public void ClearCcdEnabled()
        {
            _ccdEnabledBodies.Clear();
        }

        // ========================================================================================
        //  检测接口
        // ========================================================================================

        /// <summary>计算两个球体的Time of Impact。</summary>
        public CcdResult ComputeSphereSphereToi(
            Vector3 posA0, Vector3 posA1, float radiusA,
            Vector3 posB0, Vector3 posB1, float radiusB,
            int bodyIdA = -1, int bodyIdB = -1)
        {
            var sw = Stopwatch.StartNew();
            _totalChecks++;

            var result = new CcdResult
            {
                Method = CcdMethod.SphereSweep,
                BodyIdA = bodyIdA,
                BodyIdB = bodyIdB,
            };

            // 球A从posA0移动到posA1，球B从posB0移动到posB1
            // 相对运动: B相对于A的运动
            Vector3 dA = posA1 - posA0;
            Vector3 dB = posB1 - posB0;
            Vector3 relMove = dB - dA;
            float relMoveLen = relMove.Length();

            if (relMoveLen < 1e-8f)
            {
                // 相对静止，检查初始重叠
                float dist = Vector3.Distance(posA0, posB0);
                float sumR0 = radiusA + radiusB;
                if (dist <= sumR0)
                {
                    result.HasCollision = true;
                    result.TimeOfImpact = 0;
                    result.PenetrationDepth = sumR0 - dist;
                    result.ContactNormal = dist > 1e-6f
                        ? Vector3.Normalize(posA0 - posB0) : Vector3.UnitY;
                    result.ContactPoint = (posA0 + posB0) * 0.5f;
                }
                else
                {
                    result.HasCollision = false;
                    result.TimeOfImpact = float.MaxValue;
                }
                sw.Stop();
                RecordTime(sw.Elapsed.TotalMilliseconds);
                result.TimeMicroseconds = sw.ElapsedTicks * 1000000 / Stopwatch.Frequency;
                return result;
            }

            // 求解二次方程: |P + t*relMove|² = (rA+rB)²
            // 其中 P = posB0 - posA0
            Vector3 P = posB0 - posA0;
            float a = Vector3.Dot(relMove, relMove);
            float b = 2f * Vector3.Dot(P, relMove);
            float sumR = radiusA + radiusB;
            float c = Vector3.Dot(P, P) - sumR * sumR;

            float discriminant = b * b - 4 * a * c;

            if (discriminant < 0)
            {
                // 没有碰撞
                result.HasCollision = false;
                result.TimeOfImpact = float.MaxValue;
            }
            else
            {
                float sqrtD = MathF.Sqrt(discriminant);
                float t1 = (-b - sqrtD) / (2 * a);
                float t2 = (-b + sqrtD) / (2 * a);

                // 取最小的有效t(在[0,1]范围内)
                float t = -1;
                if (t1 >= 0 && t1 <= 1) t = t1;
                else if (t2 >= 0 && t2 <= 1) t = t2;

                if (t >= 0 && t <= 1)
                {
                    result.HasCollision = true;
                    result.TimeOfImpact = t;
                    Vector3 contactA = posA0 + dA * t;
                    Vector3 contactB = posB0 + dB * t;
                    result.ContactPoint = (contactA + contactB) * 0.5f;
                    result.ContactNormal = Vector3.Normalize(contactA - contactB);
                    result.PenetrationDepth = 0;
                    _totalCollisions++;
                }
                else if (c < 0)
                {
                    // 初始就重叠
                    result.HasCollision = true;
                    result.TimeOfImpact = 0;
                    result.PenetrationDepth = sumR - MathF.Sqrt(c);
                    result.ContactPoint = (posA0 + posB0) * 0.5f;
                    result.ContactNormal = Vector3.Normalize(posA0 - posB0);
                    _totalCollisions++;
                }
                else
                {
                    result.HasCollision = false;
                    result.TimeOfImpact = float.MaxValue;
                }
            }

            sw.Stop();
            RecordTime(sw.Elapsed.TotalMilliseconds);
            result.TimeMicroseconds = sw.ElapsedTicks * 1000000 / Stopwatch.Frequency;
            result.Iterations = 1;
            return result;
        }

        /// <summary>计算球体与静态平面的Time of Impact。</summary>
        public CcdResult ComputeSpherePlaneToi(
            Vector3 pos0, Vector3 pos1, float radius,
            Vector3 planePoint, Vector3 planeNormal,
            int bodyIdA = -1, int bodyIdB = -1)
        {
            var sw = Stopwatch.StartNew();
            _totalChecks++;

            var result = new CcdResult
            {
                Method = CcdMethod.LinearSweep,
                BodyIdA = bodyIdA,
                BodyIdB = bodyIdB,
            };

            Vector3 move = pos1 - pos0;
            float moveLen = move.Length();

            // 平面方程: dot(N, X - P0) = 0
            // 球心到平面的距离: dot(N, center - P0)
            // 球碰到平面时: dot(N, center - P0) = radius
            // center = pos0 + t * move
            // dot(N, pos0 + t*move - P0) = radius
            // dot(N, pos0 - P0) + t * dot(N, move) = radius
            // t = (radius - dot(N, pos0 - P0)) / dot(N, move)

            float dist0 = Vector3.Dot(planeNormal, pos0 - planePoint);
            float denom = Vector3.Dot(planeNormal, move);

            if (MathF.Abs(denom) < 1e-8f)
            {
                // 移动方向和平面平行，检查是否已经在平面内
                if (MathF.Abs(dist0) <= radius)
                {
                    result.HasCollision = true;
                    result.TimeOfImpact = 0;
                    result.PenetrationDepth = radius - MathF.Abs(dist0);
                    result.ContactNormal = dist0 > 0 ? -planeNormal : planeNormal;
                    result.ContactPoint = pos0 - result.ContactNormal * dist0;
                    _totalCollisions++;
                }
                else
                {
                    result.HasCollision = false;
                    result.TimeOfImpact = float.MaxValue;
                }
            }
            else
            {
                // t = (radius - dist0) / denom  (如果球在平面上方)
                // 或 t = (-radius - dist0) / denom (如果球在平面下方)
                float targetDist = dist0 > 0 ? radius : -radius;
                float t = (targetDist - dist0) / denom;

                if (t >= 0 && t <= 1)
                {
                    result.HasCollision = true;
                    result.TimeOfImpact = t;
                    result.ContactPoint = pos0 + move * t - planeNormal * radius;
                    result.ContactNormal = dist0 > 0 ? -planeNormal : planeNormal;
                    _totalCollisions++;
                }
                else if (MathF.Abs(dist0) <= radius)
                {
                    // 初始穿透
                    result.HasCollision = true;
                    result.TimeOfImpact = 0;
                    result.PenetrationDepth = radius - MathF.Abs(dist0);
                    result.ContactNormal = dist0 > 0 ? -planeNormal : planeNormal;
                    _totalCollisions++;
                }
                else
                {
                    result.HasCollision = false;
                    result.TimeOfImpact = float.MaxValue;
                }
            }

            sw.Stop();
            RecordTime(sw.Elapsed.TotalMilliseconds);
            result.TimeMicroseconds = sw.ElapsedTicks * 1000000 / Stopwatch.Frequency;
            result.Iterations = 1;
            return result;
        }

        /// <summary>计算球体扫掠与AABB的碰撞。</summary>
        public CcdResult ComputeSphereAabbToi(
            Vector3 pos0, Vector3 pos1, float radius,
            Vector3 aabbMin, Vector3 aabbMax,
            int bodyIdA = -1, int bodyIdB = -1)
        {
            var sw = Stopwatch.StartNew();
            _totalChecks++;

            var result = new CcdResult
            {
                Method = CcdMethod.SphereSweep,
                BodyIdA = bodyIdA,
                BodyIdB = bodyIdB,
            };

            // 把AABB往外扩radius，变成"膨胀AABB"
            // 然后做射线-膨胀AABB相交测试
            Vector3 expandedMin = aabbMin - new Vector3(radius);
            Vector3 expandedMax = aabbMax + new Vector3(radius);

            Vector3 move = pos1 - pos0;
            Vector3 invMove = new(
                MathF.Abs(move.X) > 1e-8f ? 1f / move.X : float.MaxValue,
                MathF.Abs(move.Y) > 1e-8f ? 1f / move.Y : float.MaxValue,
                MathF.Abs(move.Z) > 1e-8f ? 1f / move.Z : float.MaxValue);

            // Slab法
            float t1 = (expandedMin.X - pos0.X) * invMove.X;
            float t2 = (expandedMax.X - pos0.X) * invMove.X;
            float tmin = MathF.Min(t1, t2);
            float tmax = MathF.Max(t1, t2);

            t1 = (expandedMin.Y - pos0.Y) * invMove.Y;
            t2 = (expandedMax.Y - pos0.Y) * invMove.Y;
            tmin = MathF.Max(tmin, MathF.Min(t1, t2));
            tmax = MathF.Min(tmax, MathF.Max(t1, t2));

            t1 = (expandedMin.Z - pos0.Z) * invMove.Z;
            t2 = (expandedMax.Z - pos0.Z) * invMove.Z;
            tmin = MathF.Max(tmin, MathF.Min(t1, t2));
            tmax = MathF.Min(tmax, MathF.Max(t1, t2));

            if (tmax >= MathF.Max(0, tmin) && tmin <= 1f && tmax >= 0)
            {
                float t = MathF.Max(0, tmin);
                result.HasCollision = true;
                result.TimeOfImpact = t;
                Vector3 contactCenter = pos0 + move * t;

                // 找最近的AABB面作为法线
                Vector3 center = (aabbMin + aabbMax) * 0.5f;
                Vector3 half = (aabbMax - aabbMin) * 0.5f;
                Vector3 d = contactCenter - center;
                Vector3 absD = new(MathF.Abs(d.X), MathF.Abs(d.Y), MathF.Abs(d.Z));

                // 除以half extents，找最大分量
                Vector3 ratio = new(
                    half.X > 1e-6f ? d.X / half.X : 0,
                    half.Y > 1e-6f ? d.Y / half.Y : 0,
                    half.Z > 1e-6f ? d.Z / half.Z : 0);

                Vector3 absRatio = new(MathF.Abs(ratio.X), MathF.Abs(ratio.Y), MathF.Abs(ratio.Z));

                if (absRatio.X >= absRatio.Y && absRatio.X >= absRatio.Z)
                    result.ContactNormal = new Vector3(MathF.Sign(ratio.X), 0, 0);
                else if (absRatio.Y >= absRatio.Z)
                    result.ContactNormal = new Vector3(0, MathF.Sign(ratio.Y), 0);
                else
                    result.ContactNormal = new Vector3(0, 0, MathF.Sign(ratio.Z));

                result.ContactPoint = contactCenter - result.ContactNormal * radius;
                _totalCollisions++;
            }
            else
            {
                // 检查初始穿透
                Vector3 closest = Vector3.Clamp(pos0, aabbMin, aabbMax);
                float dist = Vector3.Distance(pos0, closest);
                if (dist <= radius)
                {
                    result.HasCollision = true;
                    result.TimeOfImpact = 0;
                    result.PenetrationDepth = radius - dist;
                    result.ContactNormal = dist > 1e-6f
                        ? Vector3.Normalize(pos0 - closest) : Vector3.UnitY;
                    result.ContactPoint = closest;
                    _totalCollisions++;
                }
                else
                {
                    result.HasCollision = false;
                    result.TimeOfImpact = float.MaxValue;
                }
            }

            sw.Stop();
            RecordTime(sw.Elapsed.TotalMilliseconds);
            result.TimeMicroseconds = sw.ElapsedTicks * 1000000 / Stopwatch.Frequency;
            result.Iterations = 1;
            return result;
        }

        /// <summary>计算两个AABB的扫掠碰撞。</summary>
        public CcdResult ComputeAabbAabbToi(
            Vector3 minA0, Vector3 maxA0, Vector3 minA1, Vector3 maxA1,
            Vector3 minB0, Vector3 maxB0, Vector3 minB1, Vector3 maxB1,
            int bodyIdA = -1, int bodyIdB = -1)
        {
            var sw = Stopwatch.StartNew();
            _totalChecks++;

            var result = new CcdResult
            {
                Method = CcdMethod.AabbSweep,
                BodyIdA = bodyIdA,
                BodyIdB = bodyIdB,
            };

            // 简化: 用中心点的移动来近似
            Vector3 centerA0 = (minA0 + maxA0) * 0.5f;
            Vector3 centerA1 = (minA1 + maxA1) * 0.5f;
            Vector3 centerB0 = (minB0 + maxB0) * 0.5f;
            Vector3 centerB1 = (minB1 + maxB1) * 0.5f;

            Vector3 halfA = (maxA0 - minA0) * 0.5f;
            Vector3 halfB = (maxB0 - minB0) * 0.5f;
            Vector3 sumHalf = halfA + halfB;

            Vector3 moveA = centerA1 - centerA0;
            Vector3 moveB = centerB1 - centerB0;
            Vector3 relMove = moveB - moveA;

            // 对每个轴做slab测试
            float tmin = 0, tmax = 1;
            int normalAxis = 0;
            float normalSign = 1;

            for (int axis = 0; axis < 3; axis++)
            {
                float relMoveAxis = axis == 0 ? relMove.X : axis == 1 ? relMove.Y : relMove.Z;
                float distAxis = axis == 0 ? centerB0.X - centerA0.X
                                 : axis == 1 ? centerB0.Y - centerA0.Y
                                 : centerB0.Z - centerA0.Z;
                float sumHalfAxis = axis == 0 ? sumHalf.X : axis == 1 ? sumHalf.Y : sumHalf.Z;

                if (MathF.Abs(relMoveAxis) < 1e-8f)
                {
                    // 该轴上相对静止，检查是否已经重叠
                    if (MathF.Abs(distAxis) > sumHalfAxis)
                    {
                        // 该轴分离，无碰撞
                        result.HasCollision = false;
                        result.TimeOfImpact = float.MaxValue;
                        goto done;
                    }
                }
                else
                {
                    float t1 = (sumHalfAxis - distAxis) / relMoveAxis;
                    float t2 = (-sumHalfAxis - distAxis) / relMoveAxis;
                    float tEnter = MathF.Min(t1, t2);
                    float tExit = MathF.Max(t1, t2);

                    if (tEnter > tmin)
                    {
                        tmin = tEnter;
                        normalAxis = axis;
                        normalSign = relMoveAxis > 0 ? -1 : 1;
                    }
                    if (tExit < tmax) tmax = tExit;

                    if (tmin > tmax)
                    {
                        // 分离，无碰撞
                        result.HasCollision = false;
                        result.TimeOfImpact = float.MaxValue;
                        goto done;
                    }
                }
            }

            if (tmin <= tmax && tmin <= 1f && tmax >= 0f)
            {
                float t = MathF.Max(0, tmin);
                result.HasCollision = true;
                result.TimeOfImpact = t;
                result.ContactNormal = normalAxis == 0 ? new Vector3(normalSign, 0, 0)
                                   : normalAxis == 1 ? new Vector3(0, normalSign, 0)
                                   : new Vector3(0, 0, normalSign);

                Vector3 contactCenterA = centerA0 + moveA * t;
                Vector3 contactCenterB = centerB0 + moveB * t;
                result.ContactPoint = (contactCenterA + contactCenterB) * 0.5f;
                _totalCollisions++;
            }
            else
            {
                result.HasCollision = false;
                result.TimeOfImpact = float.MaxValue;
            }

            done:
            sw.Stop();
            RecordTime(sw.Elapsed.TotalMilliseconds);
            result.TimeMicroseconds = sw.ElapsedTicks * 1000000 / Stopwatch.Frequency;
            result.Iterations = 1;
            return result;
        }

        /// <summary>通用的ToI计算(用二分法/牛顿法)。</summary>
        public CcdResult ComputeGenericToi(
            Func<float, bool> overlapTest, // 给定t∈[0,1]，返回是否重叠
            int maxIterations = 16,
            float precision = 0.001f)
        {
            var sw = Stopwatch.StartNew();
            _totalChecks++;

            var result = new CcdResult
            {
                Method = CcdMethod.TimeOfImpact,
            };

            // 检查t=0是否重叠
            if (overlapTest(0))
            {
                result.HasCollision = true;
                result.TimeOfImpact = 0;
                _totalCollisions++;
                sw.Stop();
                RecordTime(sw.Elapsed.TotalMilliseconds);
                result.TimeMicroseconds = sw.ElapsedTicks * 1000000 / Stopwatch.Frequency;
                result.Iterations = 0;
                return result;
            }

            // 检查t=1是否重叠
            if (!overlapTest(1))
            {
                result.HasCollision = false;
                result.TimeOfImpact = float.MaxValue;
                sw.Stop();
                RecordTime(sw.Elapsed.TotalMilliseconds);
                result.TimeMicroseconds = sw.ElapsedTicks * 1000000 / Stopwatch.Frequency;
                result.Iterations = 0;
                return result;
            }

            // 二分法找第一个碰撞时刻
            float lo = 0, hi = 1;
            int iter;
            for (iter = 0; iter < maxIterations; iter++)
            {
                float mid = (lo + hi) * 0.5f;
                if (overlapTest(mid))
                {
                    hi = mid;
                }
                else
                {
                    lo = mid;
                }
                if (hi - lo < precision) break;
            }

            result.HasCollision = true;
            result.TimeOfImpact = hi;
            result.Iterations = iter + 1;
            _totalCollisions++;

            sw.Stop();
            RecordTime(sw.Elapsed.TotalMilliseconds);
            result.TimeMicroseconds = sw.ElapsedTicks * 1000000 / Stopwatch.Frequency;
            return result;
        }

        // ========================================================================================
        //  子步进
        // ========================================================================================

        /// <summary>计算需要的子步数量。</summary>
        public int ComputeSubstepCount(float dt, float maxVelocity, float minBodySize)
        {
            if (maxVelocity <= 0 || minBodySize <= 0) return 1;

            // 每步移动的距离不应超过最小body尺寸
            float maxStepDistance = minBodySize * 0.5f;
            float maxStepTime = maxStepDistance / maxVelocity;

            int substeps = (int)MathF.Ceiling(dt / maxStepTime);
            return Math.Clamp(substeps, 1, _config.MaxSubsteps);
        }

        /// <summary>子步进更新。把一帧拆成多个子步执行。</summary>
        public void SubstepUpdate(float dt, Action<float> stepCallback)
        {
            if (stepCallback == null || dt <= 0) return;

            int substeps = Math.Clamp((int)MathF.Ceiling(dt / _config.MinSubstepTime),
                1, _config.MaxSubsteps);

            float subDt = dt / substeps;
            for (int i = 0; i < substeps; i++)
            {
                stepCallback(subDt);
            }
        }

        /// <summary>自适应子步进。根据当前最大速度决定子步数量。</summary>
        public void AdaptiveSubstep(float dt, float maxSpeed, float minBodyRadius,
            Action<float> stepCallback)
        {
            if (stepCallback == null || dt <= 0) return;

            int substeps = ComputeSubstepCount(dt, maxSpeed, minBodyRadius);
            float subDt = dt / substeps;

            for (int i = 0; i < substeps; i++)
            {
                stepCallback(subDt);
            }
        }

        // ========================================================================================
        //  事件记录
        // ========================================================================================

        /// <summary>记录CCD事件。</summary>
        public void RecordEvent(CcdEvent evt)
        {
            if (!_config.RecordEvents) return;
            _events.Add(evt);
            if (_events.Count > _config.MaxRecordedEvents)
            {
                _events.RemoveAt(0);
            }
        }

        /// <summary>清空事件。</summary>
        public void ClearEvents()
        {
            _events.Clear();
        }

        /// <summary>重置统计。</summary>
        public void ResetStats()
        {
            _totalChecks = 0;
            _totalCollisions = 0;
            _totalTimeMs = 0;
            _maxTimeMs = 0;
        }

        private void RecordTime(double ms)
        {
            _totalTimeMs += ms;
            if (ms > _maxTimeMs) _maxTimeMs = ms;
        }

        // ========================================================================================
        //  报告
        // ========================================================================================

        /// <summary>生成统计报告。</summary>
        public string GenerateReport()
        {
            return $"=== CCD Stats ===\n" +
                   $"Checks: {_totalChecks}\n" +
                   $"Collisions: {_totalCollisions}\n" +
                   $"Collision rate: {(_totalChecks > 0 ? (double)_totalCollisions / _totalChecks * 100 : 0):F1}%\n" +
                   $"Avg time: {AvgTimeMs:F4} ms\n" +
                   $"Max time: {_maxTimeMs:F4} ms\n" +
                   $"Events recorded: {_events.Count}\n" +
                   $"CCD enabled bodies: {_ccdEnabledBodies.Count}";
        }
    }

    // ============================================================================================
    //  扫掠结果
    // ============================================================================================

    /// <summary>形状扫掠结果。</summary>
    public struct SweepResult
    {
        public bool HasHit;
        public float HitFraction;    // 0-1
        public Vector3 HitPoint;
        public Vector3 HitNormal;
        public float HitDistance;
        public int HitBodyId;
    }

    /// <summary>扫掠检测器。对各种形状做扫掠测试。</summary>
    public static class SweepTester
    {
        /// <summary>球体扫掠。</summary>
        public static SweepResult SphereSweep(
            Vector3 start, Vector3 end, float radius,
            Func<Vector3, Vector3, float, (bool hit, Vector3 point, Vector3 normal, int bodyId)> testCallback)
        {
            var result = new SweepResult { HitFraction = 1f };

            if (testCallback == null) return result;

            Vector3 dir = end - start;
            float maxDist = dir.Length();
            if (maxDist < 1e-6f) return result;
            dir /= maxDist;

            var hit = testCallback(start, dir, maxDist + radius);
            if (hit.hit)
            {
                float distToHit = Vector3.Distance(start, hit.point);
                float fraction = (distToHit - radius) / maxDist;
                if (fraction < 0) fraction = 0;
                if (fraction < result.HitFraction)
                {
                    result.HasHit = true;
                    result.HitFraction = fraction;
                    result.HitPoint = hit.point;
                    result.HitNormal = hit.normal;
                    result.HitDistance = fraction * maxDist;
                    result.HitBodyId = hit.bodyId;
                }
            }

            return result;
        }

        /// <summary>AABB扫掠。</summary>
        public static SweepResult AabbSweep(
            Vector3 min, Vector3 max,
            Vector3 startCenter, Vector3 endCenter,
            Vector3 targetMin, Vector3 targetMax)
        {
            var result = new SweepResult { HitFraction = 1f };

            Vector3 halfExtents = (max - min) * 0.5f;
            Vector3 move = endCenter - startCenter;

            // 膨胀目标AABB
            Vector3 expandedMin = targetMin - halfExtents;
            Vector3 expandedMax = targetMax + halfExtents;

            // 射线-AABB测试
            Vector3 invDir = new(
                MathF.Abs(move.X) > 1e-8f ? 1f / move.X : float.MaxValue,
                MathF.Abs(move.Y) > 1e-8f ? 1f / move.Y : float.MaxValue,
                MathF.Abs(move.Z) > 1e-8f ? 1f / move.Z : float.MaxValue);

            float t1 = (expandedMin.X - startCenter.X) * invDir.X;
            float t2 = (expandedMax.X - startCenter.X) * invDir.X;
            float tmin = MathF.Min(t1, t2);
            float tmax = MathF.Max(t1, t2);

            t1 = (expandedMin.Y - startCenter.Y) * invDir.Y;
            t2 = (expandedMax.Y - startCenter.Y) * invDir.Y;
            tmin = MathF.Max(tmin, MathF.Min(t1, t2));
            tmax = MathF.Min(tmax, MathF.Max(t1, t2));

            t1 = (expandedMin.Z - startCenter.Z) * invDir.Z;
            t2 = (expandedMax.Z - startCenter.Z) * invDir.Z;
            tmin = MathF.Max(tmin, MathF.Min(t1, t2));
            tmax = MathF.Min(tmax, MathF.Max(t1, t2));

            if (tmax >= tmin && tmin <= 1f && tmax >= 0)
            {
                float t = MathF.Max(0, tmin);
                result.HasHit = true;
                result.HitFraction = t;
                result.HitPoint = startCenter + move * t;
                result.HitDistance = t * move.Length();

                // 计算法线
                Vector3 hitCenter = startCenter + move * t;
                Vector3 center = (targetMin + targetMax) * 0.5f;
                Vector3 d = hitCenter - center;
                Vector3 half = (targetMax - targetMin) * 0.5f;
                Vector3 ratio = new(
                    half.X > 1e-6f ? d.X / half.X : 0,
                    half.Y > 1e-6f ? d.Y / half.Y : 0,
                    half.Z > 1e-6f ? d.Z / half.Z : 0);
                Vector3 absRatio = new(MathF.Abs(ratio.X), MathF.Abs(ratio.Y), MathF.Abs(ratio.Z));

                if (absRatio.X >= absRatio.Y && absRatio.X >= absRatio.Z)
                    result.HitNormal = new Vector3(MathF.Sign(ratio.X), 0, 0);
                else if (absRatio.Y >= absRatio.Z)
                    result.HitNormal = new Vector3(0, MathF.Sign(ratio.Y), 0);
                else
                    result.HitNormal = new Vector3(0, 0, MathF.Sign(ratio.Z));
            }

            return result;
        }
    }

    // ============================================================================================
    //  物理优化器 (LOD, 空间哈希, 时间步优化等)
    // ============================================================================================

    /// <summary>物理LOD(细节层次)级别。</summary>
    public enum PhysicsLOD
    {
        /// <summary>高质量: 全功能，CCD开启。</summary>
        High = 0,
        /// <summary>中质量: 关闭CCD，减少迭代。</summary>
        Medium = 1,
        /// <summary>低质量: 简化碰撞，减少子步。</summary>
        Low = 2,
        /// <summary>极低: 只做基础碰撞，关闭大部分功能。</summary>
        VeryLow = 3,
    }

    /// <summary>LOD配置。</summary>
    public class LODConfig
    {
        public PhysicsLOD DefaultLOD = PhysicsLOD.High;

        /// <summary>根据距离决定LOD。</summary>
        public bool DistanceBasedLOD = true;

        /// <summary>LOD切换距离。</summary>
        public float MediumDistance = 30f;
        public float LowDistance = 60f;
        public float VeryLowDistance = 100f;

        /// <summary>根据屏幕大小决定LOD。</summary>
        public bool ScreenSizeBasedLOD = false;

        /// <summary>各LOD级别的求解器迭代次数。</summary>
        public int HighIterations = 10;
        public int MediumIterations = 6;
        public int LowIterations = 3;
        public int VeryLowIterations = 1;

        /// <summary>各LOD级别的子步数量。</summary>
        public int HighSubsteps = 4;
        public int MediumSubsteps = 2;
        public int LowSubsteps = 1;
        public int VeryLowSubsteps = 1;

        /// <summary>各LOD是否启用CCD。</summary>
        public bool HighCcd = true;
        public bool MediumCcd = false;
        public bool LowCcd = false;
        public bool VeryLowCcd = false;

        /// <summary>获取指定LOD的迭代次数。</summary>
        public int GetIterations(PhysicsLOD lod) => lod switch
        {
            PhysicsLOD.High => HighIterations,
            PhysicsLOD.Medium => MediumIterations,
            PhysicsLOD.Low => LowIterations,
            PhysicsLOD.VeryLow => VeryLowIterations,
            _ => HighIterations,
        };

        /// <summary>获取指定LOD的子步数量。</summary>
        public int GetSubsteps(PhysicsLOD lod) => lod switch
        {
            PhysicsLOD.High => HighSubsteps,
            PhysicsLOD.Medium => MediumSubsteps,
            PhysicsLOD.Low => LowSubsteps,
            PhysicsLOD.VeryLow => VeryLowSubsteps,
            _ => HighSubsteps,
        };

        /// <summary>获取指定LOD是否启用CCD。</summary>
        public bool GetCcdEnabled(PhysicsLOD lod) => lod switch
        {
            PhysicsLOD.High => HighCcd,
            PhysicsLOD.Medium => MediumCcd,
            PhysicsLOD.Low => LowCcd,
            PhysicsLOD.VeryLow => VeryLowCcd,
            _ => HighCcd,
        };

        /// <summary>根据距离获取LOD。</summary>
        public PhysicsLOD GetLODByDistance(float distance)
        {
            if (distance < MediumDistance) return PhysicsLOD.High;
            if (distance < LowDistance) return PhysicsLOD.Medium;
            if (distance < VeryLowDistance) return PhysicsLOD.Low;
            return PhysicsLOD.VeryLow;
        }
    }

    /// <summary>Body的LOD信息。</summary>
    public struct BodyLODInfo
    {
        public int BodyId;
        public PhysicsLOD CurrentLOD;
        public float DistanceToCamera;
        public float ScreenSize;
        public bool IsImportant;  // 玩家、重要物体永远High
        public int LastUpdateFrame;
    }

    /// <summary>
    /// 物理优化系统。管理LOD、时间步优化、空间哈希等。
    /// </summary>
    public class PhysicsOptimizationSystem
    {
        private readonly LODConfig _lodConfig = new();
        private readonly Dictionary<int, BodyLODInfo> _bodyLODs = new();
        private Vector3 _cameraPosition;
        private float _cameraFov = 60f;
        private int _frameNumber;
        private long _totalOptimizationTimeMs;
        private int _bodiesOptimized;
        private int _bodiesSimplified;

        public LODConfig LODConfig => _lodConfig;
        public Vector3 CameraPosition { get => _cameraPosition; set => _cameraPosition = value; }
        public float CameraFov { get => _cameraFov; set => _cameraFov = value; }
        public int FrameNumber => _frameNumber;
        public long TotalOptimizationTimeMs => _totalOptimizationTimeMs;
        public int BodiesOptimized => _bodiesOptimized;
        public int BodiesSimplified => _bodiesSimplified;

        // ========================================================================================
        //  LOD管理
        // ========================================================================================

        /// <summary>注册body到LOD系统。</summary>
        public void RegisterBody(int bodyId, bool isImportant = false)
        {
            _bodyLODs[bodyId] = new BodyLODInfo
            {
                BodyId = bodyId,
                CurrentLOD = _lodConfig.DefaultLOD,
                IsImportant = isImportant,
            };
        }

        /// <summary>注销body。</summary>
        public void UnregisterBody(int bodyId)
        {
            _bodyLODs.Remove(bodyId);
        }

        /// <summary>设置body为重要(永远High LOD)。</summary>
        public void SetBodyImportant(int bodyId, bool important)
        {
            if (_bodyLODs.TryGetValue(bodyId, out var info))
            {
                info.IsImportant = important;
                _bodyLODs[bodyId] = info;
            }
        }

        /// <summary>更新所有body的LOD。</summary>
        public void UpdateLODs(Dictionary<int, Vector3> bodyPositions)
        {
            var sw = Stopwatch.StartNew();
            _frameNumber++;

            foreach (var kvp in _bodyLODs)
            {
                int bodyId = kvp.Key;
                var info = kvp.Value;

                if (info.IsImportant)
                {
                    info.CurrentLOD = PhysicsLOD.High;
                    info.LastUpdateFrame = _frameNumber;
                    _bodyLODs[bodyId] = info;
                    continue;
                }

                if (_lodConfig.DistanceBasedLOD && bodyPositions.TryGetValue(bodyId, out var pos))
                {
                    float dist = Vector3.Distance(pos, _cameraPosition);
                    info.DistanceToCamera = dist;
                    info.CurrentLOD = _lodConfig.GetLODByDistance(dist);
                }

                info.LastUpdateFrame = _frameNumber;
                _bodyLODs[bodyId] = info;
            }

            sw.Stop();
            _totalOptimizationTimeMs += sw.ElapsedMilliseconds;
            _bodiesOptimized = _bodyLODs.Count;
        }

        /// <summary>获取body的LOD。</summary>
        public PhysicsLOD GetBodyLOD(int bodyId)
        {
            if (_bodyLODs.TryGetValue(bodyId, out var info))
                return info.CurrentLOD;
            return _lodConfig.DefaultLOD;
        }

        /// <summary>获取body的迭代次数。</summary>
        public int GetBodyIterations(int bodyId)
        {
            return _lodConfig.GetIterations(GetBodyLOD(bodyId));
        }

        /// <summary>获取body的子步数量。</summary>
        public int GetBodySubsteps(int bodyId)
        {
            return _lodConfig.GetSubsteps(GetBodyLOD(bodyId));
        }

        /// <summary>获取body是否启用CCD。</summary>
        public bool GetBodyCcdEnabled(int bodyId)
        {
            return _lodConfig.GetCcdEnabled(GetBodyLOD(bodyId));
        }

        /// <summary>获取各LOD级别的body数量。</summary>
        public (int high, int medium, int low, int veryLow) GetLODCounts()
        {
            int high = 0, medium = 0, low = 0, veryLow = 0;
            foreach (var info in _bodyLODs.Values)
            {
                switch (info.CurrentLOD)
                {
                    case PhysicsLOD.High: high++; break;
                    case PhysicsLOD.Medium: medium++; break;
                    case PhysicsLOD.Low: low++; break;
                    case PhysicsLOD.VeryLow: veryLow++; break;
                }
            }
            return (high, medium, low, veryLow);
        }

        // ========================================================================================
        //  时间步优化
        // ========================================================================================

        /// <summary>计算最优时间步。</summary>
        public float ComputeOptimalTimestep(float targetFps, float maxVelocity,
            float minBodySize, float baseDt)
        {
            // 根据最大速度和最小body尺寸计算
            float maxStepTime = minBodySize / MathF.Max(maxVelocity, 0.1f) * 0.5f;
            float targetDt = 1f / targetFps;

            // 如果目标dt太大，需要子步进
            if (targetDt > maxStepTime)
            {
                // 用更小的时间步
                return maxStepTime;
            }

            return MathF.Min(baseDt, targetDt);
        }

        /// <summary>计算需要的子步数量。</summary>
        public int ComputeOptimalSubsteps(float dt, float maxVelocity, float minBodySize)
        {
            if (maxVelocity <= 0 || minBodySize <= 0) return 1;
            float maxStepDist = minBodySize * 0.5f;
            float maxStepTime = maxStepDist / maxVelocity;
            int substeps = (int)MathF.Ceiling(dt / maxStepTime);
            return Math.Clamp(substeps, 1, 16);
        }

        // ========================================================================================
        //  空间哈希缓存
        // ========================================================================================

        /// <summary>空间哈希。用于快速邻居查询。</summary>
        public class SpatialHash
        {
            private readonly Dictionary<long, List<int>> _cells = new();
            private readonly float _cellSize;
            private readonly Dictionary<int, long> _bodyCells = new();

            public float CellSize => _cellSize;
            public int CellCount => _cells.Count;

            public SpatialHash(float cellSize = 4f)
            {
                _cellSize = MathF.Max(0.1f, cellSize);
            }

            /// <summary>计算cell key。</summary>
            private long ComputeCellKey(Vector3 pos)
            {
                int x = (int)MathF.Floor(pos.X / _cellSize);
                int y = (int)MathF.Floor(pos.Y / _cellSize);
                int z = (int)MathF.Floor(pos.Z / _cellSize);
                // 把3D坐标编码成一个long
                return ((long)(x + 32768) << 42) | ((long)(y + 32768) << 21) | (long)(z + 32768);
            }

            /// <summary>添加body。</summary>
            public void Insert(int bodyId, Vector3 position)
            {
                long key = ComputeCellKey(position);
                if (!_cells.TryGetValue(key, out var list))
                {
                    list = new List<int>();
                    _cells[key] = list;
                }
                list.Add(bodyId);
                _bodyCells[bodyId] = key;
            }

            /// <summary>更新body位置。</summary>
            public void Update(int bodyId, Vector3 newPosition)
            {
                if (_bodyCells.TryGetValue(bodyId, out long oldKey))
                {
                    if (_cells.TryGetValue(oldKey, out var list))
                    {
                        list.Remove(bodyId);
                        if (list.Count == 0) _cells.Remove(oldKey);
                    }
                }
                Insert(bodyId, newPosition);
            }

            /// <summary>移除body。</summary>
            public void Remove(int bodyId)
            {
                if (_bodyCells.TryGetValue(bodyId, out long key))
                {
                    if (_cells.TryGetValue(key, out var list))
                    {
                        list.Remove(bodyId);
                        if (list.Count == 0) _cells.Remove(key);
                    }
                    _bodyCells.Remove(bodyId);
                }
            }

            /// <summary>查询某个位置附近的body。</summary>
            public List<int> QueryNearby(Vector3 position, int radius = 1)
            {
                var result = new List<int>();
                int cx = (int)MathF.Floor(position.X / _cellSize);
                int cy = (int)MathF.Floor(position.Y / _cellSize);
                int cz = (int)MathF.Floor(position.Z / _cellSize);

                for (int dx = -radius; dx <= radius; dx++)
                {
                    for (int dy = -radius; dy <= radius; dy++)
                    {
                        for (int dz = -radius; dz <= radius; dz++)
                        {
                            long key = ((long)(cx + dx + 32768) << 42) |
                                       ((long)(cy + dy + 32768) << 21) |
                                       (long)(cz + dz + 32768);
                            if (_cells.TryGetValue(key, out var list))
                            {
                                result.AddRange(list);
                            }
                        }
                    }
                }
                return result;
            }

            /// <summary>查询AABB范围内的body。</summary>
            public List<int> QueryAABB(Vector3 min, Vector3 max)
            {
                var result = new List<int>();
                int minX = (int)MathF.Floor(min.X / _cellSize);
                int maxX = (int)MathF.Floor(max.X / _cellSize);
                int minY = (int)MathF.Floor(min.Y / _cellSize);
                int maxY = (int)MathF.Floor(max.Y / _cellSize);
                int minZ = (int)MathF.Floor(min.Z / _cellSize);
                int maxZ = (int)MathF.Floor(max.Z / _cellSize);

                for (int x = minX; x <= maxX; x++)
                {
                    for (int y = minY; y <= maxY; y++)
                    {
                        for (int z = minZ; z <= maxZ; z++)
                        {
                            long key = ((long)(x + 32768) << 42) |
                                       ((long)(y + 32768) << 21) |
                                       (long)(z + 32768);
                            if (_cells.TryGetValue(key, out var list))
                            {
                                result.AddRange(list);
                            }
                        }
                    }
                }
                return result;
            }

            /// <summary>清空。</summary>
            public void Clear()
            {
                _cells.Clear();
                _bodyCells.Clear();
            }

            /// <summary>获取所有cell的信息(调试用)。</summary>
            public List<(Vector3 center, int bodyCount)> GetCellInfo()
            {
                var result = new List<(Vector3, int)>();
                foreach (var kvp in _cells)
                {
                    long key = kvp.Key;
                    int z = (int)(key & 0x1FFFFF) - 32768;
                    int y = (int)((key >> 21) & 0x1FFFFF) - 32768;
                    int x = (int)((key >> 42) & 0x1FFFFF) - 32768;
                    Vector3 center = new(
                        x * _cellSize + _cellSize * 0.5f,
                        y * _cellSize + _cellSize * 0.5f,
                        z * _cellSize + _cellSize * 0.5f);
                    result.Add((center, kvp.Value.Count));
                }
                return result;
            }
        }

        /// <summary>空间哈希实例。</summary>
        public SpatialHash Hash { get; }

        /// <summary>创建优化系统。</summary>
        public PhysicsOptimizationSystem(float hashCellSize = 4f)
        {
            Hash = new SpatialHash(hashCellSize);
        }

        // ========================================================================================
        //  刚体稳定性优化
        // ========================================================================================

        /// <summary>稳定化参数。</summary>
        public struct StabilityParams
        {
            /// <summary>位置修正系数。</summary>
            public float PositionCorrectionFactor;
            /// <summary>位置修正slop。</summary>
            public float PositionSlop;
            /// <summary>速度修正系数。</summary>
            public float VelocityCorrectionFactor;
            /// <summary>速度slop。</summary>
            public float VelocitySlop;
            /// <summary>最大位置修正。</summary>
            public float MaxPositionCorrection;
            /// <summary>最大速度修正。</summary>
            public float MaxVelocityCorrection;
            /// <summary>是否启用伪速度(Pseudo Velocity)。</summary>
            public bool EnablePseudoVelocity;
            /// <summary>是否启用预热(warm starting)。</summary>
            public bool EnableWarmStarting;
            /// <summary>预热系数。</summary>
            public float WarmStartingFactor;

            public static StabilityParams Default => new()
            {
                PositionCorrectionFactor = 0.2f,
                PositionSlop = 0.005f,
                VelocityCorrectionFactor = 0.8f,
                VelocitySlop = 0.5f,
                MaxPositionCorrection = 0.2f,
                MaxVelocityCorrection = 10f,
                EnablePseudoVelocity = true,
                EnableWarmStarting = true,
                WarmStartingFactor = 0.8f,
            };
        }

        /// <summary>对刚体做稳定性优化。</summary>
        public void StabilizeBody(
            ref Vector3 velocity, ref Vector3 angularVelocity,
            float invMass, float dt, in StabilityParams param)
        {
            if (invMass <= 0) return;

            // 速度阻尼(防止抖动)
            float velMag = velocity.Length();
            if (velMag < param.VelocitySlop)
            {
                // 低速度时加大阻尼
                float dampFactor = 1f - param.VelocityCorrectionFactor * dt;
                velocity *= dampFactor;
            }

            float angVelMag = angularVelocity.Length();
            if (angVelMag < param.VelocitySlop)
            {
                float dampFactor = 1f - param.VelocityCorrectionFactor * dt;
                angularVelocity *= dampFactor;
            }

            // 限制最大速度(防止数值爆炸)
            if (velMag > param.MaxVelocityCorrection)
            {
                velocity = velocity / velMag * param.MaxVelocityCorrection;
            }
            if (angVelMag > param.MaxVelocityCorrection)
            {
                angularVelocity = angularVelocity / angVelMag * param.MaxVelocityCorrection;
            }
        }

        /// <summary>位置修正(Baumgarte)。</summary>
        public Vector3 ComputePositionCorrection(
            float penetration, Vector3 normal, float invMassA, float invMassB,
            in StabilityParams param)
        {
            float invMassSum = invMassA + invMassB;
            if (invMassSum <= 0) return Vector3.Zero;

            float correction = MathF.Max(penetration - param.PositionSlop, 0) /
                              invMassSum * param.PositionCorrectionFactor;
            correction = MathF.Min(correction, param.MaxPositionCorrection);

            return normal * correction;
        }

        // ========================================================================================
        //  预测-校正
        // ========================================================================================

        /// <summary>预测-校正积分器。</summary>
        public struct PredictCorrectState
        {
            public Vector3 PredictedPosition;
            public Quaternion PredictedOrientation;
            public Vector3 PredictedVelocity;
            public Vector3 PredictedAngularVelocity;
            public Vector3 CorrectedPosition;
            public Quaternion CorrectedOrientation;
            public Vector3 CorrectedVelocity;
            public Vector3 CorrectedAngularVelocity;
        }

        /// <summary>预测步: 用当前速度预测下一步位置。</summary>
        public void PredictStep(
            Vector3 position, Quaternion orientation,
            Vector3 velocity, Vector3 angularVelocity,
            Vector3 force, Vector3 torque,
            float invMass, Matrix4x4 invInertia,
            Vector3 gravity, float dt,
            out PredictCorrectState state)
        {
            state = new PredictCorrectState();

            // 预测速度
            state.PredictedVelocity = velocity + (force * invMass + gravity) * dt;
            state.PredictedAngularVelocity = angularVelocity +
                Vector3.Transform(torque, invInertia) * dt;

            // 预测位置
            state.PredictedPosition = position + state.PredictedVelocity * dt;

            // 预测朝向
            float halfDt = dt * 0.5f;
            Vector3 av = state.PredictedAngularVelocity;
            Quaternion dq = new(av.X * halfDt, av.Y * halfDt, av.Z * halfDt, 0f);
            Quaternion q = orientation;
            state.PredictedOrientation = Quaternion.Normalize(new Quaternion(
                dq.X * q.W + dq.W * q.X + dq.Y * q.Z - dq.Z * q.Y,
                dq.Y * q.W + dq.W * q.Y + dq.Z * q.X - dq.X * q.Z,
                dq.Z * q.W + dq.W * q.Z + dq.X * q.Y - dq.Y * q.X,
                dq.W * q.W - dq.X * q.X - dq.Y * q.Y - dq.Z * q.Z));
        }

        /// <summary>校正步: 根据碰撞结果修正预测的位置和速度。</summary>
        public void CorrectStep(
            ref PredictCorrectState state,
            Vector3 positionCorrection, Vector3 velocityCorrection)
        {
            state.CorrectedPosition = state.PredictedPosition + positionCorrection;
            state.CorrectedVelocity = state.PredictedVelocity + velocityCorrection;
            state.CorrectedOrientation = state.PredictedOrientation;
            state.CorrectedAngularVelocity = state.PredictedAngularVelocity;
        }

        // ========================================================================================
        //  碰撞对优化
        // ========================================================================================

        /// <summary>碰撞对(用于管理持久碰撞信息)。</summary>
        public class CollisionPair
        {
            public int BodyA;
            public int BodyB;
            public Vector3 LastContactPoint;
            public Vector3 LastContactNormal;
            public float LastNormalImpulse;
            public float LastTangentImpulse;
            public int FramesSinceLastContact;
            public bool IsActive;

            public long PairKey => ((long)BodyA << 32) | (uint)BodyB;
        }

        private readonly Dictionary<long, CollisionPair> _persistentPairs = new();

        /// <summary>更新持久碰撞对。</summary>
        public void UpdateCollisionPairs(List<(int a, int b, Vector3 point, Vector3 normal)> currentContacts)
        {
            // 标记所有pair为inactive
            foreach (var pair in _persistentPairs.Values)
            {
                pair.IsActive = false;
            }

            // 更新当前接触
            foreach (var contact in currentContacts)
            {
                int a = Math.Min(contact.a, contact.b);
                int b = Math.Max(contact.a, contact.b);
                long key = ((long)a << 32) | (uint)b;

                if (!_persistentPairs.TryGetValue(key, out var pair))
                {
                    pair = new CollisionPair { BodyA = a, BodyB = b };
                    _persistentPairs[key] = pair;
                }

                pair.LastContactPoint = contact.point;
                pair.LastContactNormal = contact.normal;
                pair.FramesSinceLastContact = 0;
                pair.IsActive = true;
            }

            // 清理长时间未接触的pair
            var toRemove = new List<long>();
            foreach (var kvp in _persistentPairs)
            {
                if (!kvp.Value.IsActive)
                {
                    kvp.Value.FramesSinceLastContact++;
                    if (kvp.Value.FramesSinceLastContact > 10)
                    {
                        toRemove.Add(kvp.Key);
                    }
                }
            }

            foreach (var key in toRemove)
            {
                _persistentPairs.Remove(key);
            }
        }

        /// <summary>获取持久碰撞对的数量。</summary>
        public int PersistentPairCount => _persistentPairs.Count;

        /// <summary>获取持久碰撞对(用于warm starting)。</summary>
        public CollisionPair? GetPersistentPair(int bodyA, int bodyB)
        {
            int a = Math.Min(bodyA, bodyB);
            int b = Math.Max(bodyA, bodyB);
            long key = ((long)a << 32) | (uint)b;
            return _persistentPairs.TryGetValue(key, out var pair) ? pair : null;
        }

        // ========================================================================================
        //  性能调优
        // ========================================================================================

        /// <summary>性能调优建议。</summary>
        public struct TuningAdvice
        {
            public bool ReduceIterations;
            public bool IncreaseIterations;
            public bool EnableSleeping;
            public bool DisableCcdForSlowBodies;
            public bool UseSimplerShapes;
            public bool ReduceSubsteps;
            public bool IncreaseBroadphaseEfficiency;
            public string Description;

            public static TuningAdvice None => new() { Description = "当前性能良好，无需调优。" };
        }

        /// <summary>根据性能数据给出调优建议。</summary>
        public TuningAdvice GetTuningAdvice(
            double stepTimeMs, double targetTimeMs,
            int bodyCount, int activeBodyCount,
            int contactCount, int constraintCount,
            int iterations, int substeps,
            float avgSpeed, float maxSpeed)
        {
            var advice = new TuningAdvice();

            if (stepTimeMs > targetTimeMs * 1.5f)
            {
                // 性能不达标
                advice.Description = $"步进时间 {stepTimeMs:F1}ms 超过目标 {targetTimeMs:F1}ms。";

                if (iterations > 4)
                {
                    advice.ReduceIterations = true;
                    advice.Description += " 建议减少求解器迭代次数。";
                }
                if (substeps > 1)
                {
                    advice.ReduceSubsteps = true;
                    advice.Description += " 建议减少子步数量。";
                }
                if (activeBodyCount > bodyCount * 0.5f)
                {
                    advice.EnableSleeping = true;
                    advice.Description += " 建议启用休眠机制。";
                }
                if (maxSpeed < 5f && iterations > 2)
                {
                    advice.DisableCcdForSlowBodies = true;
                    advice.Description += " 最大速度较低，可以禁用CCD。";
                }
                if (contactCount > bodyCount * 4)
                {
                    advice.UseSimplerShapes = true;
                    advice.Description += " 接触点过多，建议使用简化碰撞形状。";
                }
                if (activeBodyCount > 500)
                {
                    advice.IncreaseBroadphaseEfficiency = true;
                    advice.Description += " 活跃body较多，建议优化宽相位。";
                }
            }
            else if (stepTimeMs < targetTimeMs * 0.3f)
            {
                // 性能很好，可以提升质量
                advice.IncreaseIterations = true;
                advice.Description = $"步进时间 {stepTimeMs:F1}ms 远低于目标，可以增加迭代次数提升质量。";
            }
            else
            {
                advice = TuningAdvice.None;
            }

            return advice;
        }

        // ========================================================================================
        //  统计
        // ========================================================================================

        /// <summary>重置统计。</summary>
        public void ResetStats()
        {
            _totalOptimizationTimeMs = 0;
            _bodiesOptimized = 0;
            _bodiesSimplified = 0;
        }

        /// <summary>生成报告。</summary>
        public string GenerateReport()
        {
            var (high, medium, low, veryLow) = GetLODCounts();
            return $"=== PhysicsOptimization Report ===\n" +
                   $"Bodies: {_bodyLODs.Count} (High:{high}, Med:{medium}, Low:{low}, VLow:{veryLow})\n" +
                   $"Hash cells: {Hash.CellCount}\n" +
                   $"Persistent pairs: {PersistentPairCount}\n" +
                   $"Optimization time: {_totalOptimizationTimeMs}ms\n" +
                   $"Frame: {_frameNumber}";
        }
    }

    // ============================================================================================
    //  休眠管理器
    // ============================================================================================

    /// <summary>休眠管理器。管理body的休眠和唤醒。</summary>
    public class SleepManager
    {
        private readonly Dictionary<int, float> _sleepTimers = new();
        private readonly HashSet<int> _sleepingBodies = new();
        private readonly HashSet<int> _noSleepBodies = new();

        public float LinearSleepThreshold = 0.05f;     // m/s
        public float AngularSleepThreshold = 0.1f;      // rad/s
        public float SleepTimeThreshold = 1f;           // 秒
        public float WakeUpRadius = 2f;                  // 唤醒半径

        /// <summary>能量休眠阈值。</summary>
        public float EnergySleepThreshold = 0.005f;

        /// <summary>是否启用休眠。</summary>
        public bool SleepEnabled = true;

        /// <summary>初始化（读取休眠配置）。</summary>
        public SleepManager(SleepSettings settings)
        {
            LinearSleepThreshold = settings.LinearSleepThreshold;
            AngularSleepThreshold = settings.AngularSleepThreshold;
            EnergySleepThreshold = settings.EnergySleepThreshold;
            SleepTimeThreshold = settings.SleepDelay;
            SleepEnabled = settings.SleepEnabled;
        }

        public int SleepingCount => _sleepingBodies.Count;
        public int TrackedCount => _sleepTimers.Count;

        /// <summary>注册body。</summary>
        public void Register(int bodyId)
        {
            _sleepTimers[bodyId] = 0;
        }

        /// <summary>注销body。</summary>
        public void Unregister(int bodyId)
        {
            _sleepTimers.Remove(bodyId);
            _sleepingBodies.Remove(bodyId);
            _noSleepBodies.Remove(bodyId);
        }

        /// <summary>设置body不允许休眠。</summary>
        public void SetNeverSleep(int bodyId, bool neverSleep)
        {
            if (neverSleep)
            {
                _noSleepBodies.Add(bodyId);
                _sleepingBodies.Remove(bodyId);
                _sleepTimers[bodyId] = 0;
            }
            else
            {
                _noSleepBodies.Remove(bodyId);
            }
        }

        /// <summary>更新休眠状态。</summary>
        public void Update(int bodyId, Vector3 velocity, Vector3 angularVelocity, float dt)
        {
            if (_noSleepBodies.Contains(bodyId))
            {
                _sleepTimers[bodyId] = 0;
                _sleepingBodies.Remove(bodyId);
                return;
            }

            float linSq = velocity.LengthSquared();
            float angSq = angularVelocity.LengthSquared();
            float linThreshSq = LinearSleepThreshold * LinearSleepThreshold;
            float angThreshSq = AngularSleepThreshold * AngularSleepThreshold;

            if (linSq < linThreshSq && angSq < angThreshSq)
            {
                _sleepTimers[bodyId] += dt;
                if (_sleepTimers[bodyId] >= SleepTimeThreshold)
                {
                    _sleepingBodies.Add(bodyId);
                }
            }
            else
            {
                _sleepTimers[bodyId] = 0;
                _sleepingBodies.Remove(bodyId);
            }
        }

        /// <summary>检查body是否在休眠。</summary>
        public bool IsSleeping(int bodyId)
        {
            return _sleepingBodies.Contains(bodyId);
        }

        /// <summary>唤醒body。</summary>
        public void WakeUp(int bodyId)
        {
            _sleepingBodies.Remove(bodyId);
            _sleepTimers[bodyId] = 0;
        }

        /// <summary>唤醒指定范围内的所有body。</summary>
        public void WakeUpInRange(Dictionary<int, Vector3> positions, Vector3 center, float radius)
        {
            float radiusSq = radius * radius;
            foreach (var kvp in positions)
            {
                if (Vector3.DistanceSquared(kvp.Value, center) < radiusSq)
                {
                    WakeUp(kvp.Key);
                }
            }
        }

        /// <summary>唤醒所有body。</summary>
        public void WakeUpAll()
        {
            _sleepingBodies.Clear();
            foreach (var key in _sleepTimers.Keys.ToList())
            {
                _sleepTimers[key] = 0;
            }
        }

        /// <summary>唤醒所有刚体（同步置为清醒状态）。</summary>
        public void WakeUpAll(List<RigidBody> bodies)
        {
            if (bodies != null)
            {
                foreach (var b in bodies)
                {
                    if (b != null)
                    {
                        b.State = BodyState.Awake;
                        WakeUp(b.BodyIndex);
                    }
                }
            }
        }

        /// <summary>碰撞发生时唤醒两个刚体。</summary>
        public void WakeOnCollision(RigidBody bodyA, RigidBody bodyB)
        {
            if (bodyA != null)
            {
                WakeUp(bodyA.BodyIndex);
                bodyA.State = BodyState.Awake;
            }
            if (bodyB != null)
            {
                WakeUp(bodyB.BodyIndex);
                bodyB.State = BodyState.Awake;
            }
        }

        /// <summary>根据速度更新一批刚体的休眠状态。</summary>
        public void UpdateSleep(List<RigidBody> bodies, float dt)
        {
            if (bodies == null || !SleepEnabled) return;
            float linThreshSq = LinearSleepThreshold * LinearSleepThreshold;
            float angThreshSq = AngularSleepThreshold * AngularSleepThreshold;

            for (int i = 0; i < bodies.Count; i++)
            {
                var body = bodies[i];
                if (body == null) continue;
                if (body.IsStatic || body.IsKinematic) continue;

                int id = body.BodyIndex;
                if (_noSleepBodies.Contains(id))
                {
                    _sleepTimers[id] = 0;
                    _sleepingBodies.Remove(id);
                    body.State = BodyState.Awake;
                    continue;
                }

                float linSq = body.LinearVelocity.LengthSquared();
                float angSq = body.AngularVelocity.LengthSquared();

                if (linSq < linThreshSq && angSq < angThreshSq)
                {
                    _sleepTimers[id] = _sleepTimers.GetValueOrDefault(id) + dt;
                    if (_sleepTimers[id] >= SleepTimeThreshold)
                    {
                        _sleepingBodies.Add(id);
                        body.State = BodyState.Sleeping;
                    }
                }
                else
                {
                    _sleepTimers[id] = 0;
                    _sleepingBodies.Remove(id);
                    body.State = BodyState.Awake;
                }
            }
        }

        /// <summary>重置休眠管理器。</summary>
        public void Reset()
        {
            _sleepTimers.Clear();
            _sleepingBodies.Clear();
            _noSleepBodies.Clear();
        }

        /// <summary>获取所有休眠body的id。</summary>
        public IEnumerable<int> GetSleepingBodies()
        {
            return _sleepingBodies;
        }

        /// <summary>清空所有数据。</summary>
        public void Clear()
        {
            _sleepTimers.Clear();
            _sleepingBodies.Clear();
            _noSleepBodies.Clear();
        }
    }

    // ============================================================================================
    //  碰撞形状简化器
    // ============================================================================================

    /// <summary>碰撞形状简化器。把复杂形状简化为简单形状以提升性能。</summary>
    public class ShapeSimplifier
    {
        /// <summary>简化策略。</summary>
        public enum SimplifyStrategy
        {
            /// <summary>用包围球。</summary>
            BoundingSphere,
            /// <summary>用AABB。</summary>
            BoundingBox,
            /// <summary>用凸包。</summary>
            ConvexHull,
            /// <summary>体素化。</summary>
            Voxelization,
            /// <summary>多形状合并。</summary>
            Merge,
        }

        /// <summary>把凸包简化为包围球。</summary>
        public static (Vector3 center, float radius) SimplifyToSphere(Vector3[] vertices)
        {
            if (vertices == null || vertices.Length == 0)
                return (Vector3.Zero, 0);

            // 计算质心
            Vector3 center = Vector3.Zero;
            foreach (var v in vertices) center += v;
            center /= vertices.Length;

            // 找最远点
            float maxDistSq = 0;
            foreach (var v in vertices)
            {
                float d = Vector3.DistanceSquared(v, center);
                if (d > maxDistSq) maxDistSq = d;
            }

            return (center, MathF.Sqrt(maxDistSq));
        }

        /// <summary>把凸包简化为AABB。</summary>
        public static (Vector3 min, Vector3 max) SimplifyToBox(Vector3[] vertices)
        {
            if (vertices == null || vertices.Length == 0)
                return (Vector3.Zero, Vector3.Zero);

            Vector3 min = vertices[0];
            Vector3 max = vertices[0];
            foreach (var v in vertices)
            {
                min = Vector3.Min(min, v);
                max = Vector3.Max(max, v);
            }
            return (min, max);
        }

        /// <summary>把凸包简化为胶囊(近似)。</summary>
        public static (Vector3 center, Vector3 axis, float radius, float halfHeight) SimplifyToCapsule(
            Vector3[] vertices)
        {
            if (vertices == null || vertices.Length == 0)
                return (Vector3.Zero, Vector3.UnitY, 0, 0);

            // 找最长轴
            var (min, max) = SimplifyToBox(vertices);
            Vector3 size = max - min;

            Vector3 axis;
            if (size.Y >= size.X && size.Y >= size.Z)
                axis = Vector3.UnitY;
            else if (size.X >= size.Z)
                axis = Vector3.UnitX;
            else
                axis = Vector3.UnitZ;

            Vector3 center = (min + max) * 0.5f;

            // 计算半径(垂直于轴的最大距离)
            float radius = 0;
            foreach (var v in vertices)
            {
                Vector3 d = v - center;
                Vector3 perp = d - axis * Vector3.Dot(d, axis);
                float dist = perp.Length();
                if (dist > radius) radius = dist;
            }

            // 半高
            float halfHeight = Vector3.Dot(size, axis) * 0.5f - radius;
            halfHeight = MathF.Max(halfHeight, 0.01f);

            return (center, axis, radius, halfHeight);
        }

        /// <summary>凸包顶点数量简化(降采样)。</summary>
        public static Vector3[] SimplifyConvexHull(Vector3[] vertices, int targetCount)
        {
            if (vertices == null || vertices.Length <= targetCount)
                return vertices ?? Array.Empty<Vector3>();

            // 简化: 随机采样
            // TODO: 应该用更智能的方法(比如保留极值点)
            var rng = new Random(42);
            var result = new Vector3[targetCount];
            var indices = new HashSet<int>();

            // 先加入AABB的极值点
            for (int i = 0; i < vertices.Length && indices.Count < targetCount; i++)
            {
                bool isExtreme = false;
                for (int axis = 0; axis < 3; axis++)
                {
                    float val = axis == 0 ? vertices[i].X : axis == 1 ? vertices[i].Y : vertices[i].Z;
                    float minVal = float.MaxValue, maxVal = float.MinValue;
                    foreach (var v in vertices)
                    {
                        float vv = axis == 0 ? v.X : axis == 1 ? v.Y : v.Z;
                        if (vv < minVal) minVal = vv;
                        if (vv > maxVal) maxVal = vv;
                    }
                    if (val == minVal || val == maxVal)
                    {
                        isExtreme = true;
                        break;
                    }
                }
                if (isExtreme) indices.Add(i);
            }

            // 随机填充剩余
            while (indices.Count < targetCount)
            {
                indices.Add(rng.Next(vertices.Length));
            }

            int idx = 0;
            foreach (var i in indices)
            {
                result[idx++] = vertices[i];
            }

            return result;
        }
    }
}
