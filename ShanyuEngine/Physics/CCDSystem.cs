// ============================================================================================
//  CCDSystem.cs
//
//  山雨引擎 (ShanyuEngine) 连续碰撞检测 (CCD) 系统
//
//  功能：
//    防止高速运动的物体穿透薄几何体（子弹穿过墙壁等问题）。
//
//  算法：
//    保守推进/时间冲击 (TOI) 检测：
//    1. 找出所有 CCD 候选体（速度超过阈值的动态体）
//    2. 对每个 CCD 候选体，沿速度方向扫掠 AABB 检测潜在碰撞
//    3. 对每个潜在碰撞对，使用二分法计算 TOI
//    4. 在 TOI 处推进物体 → 解决碰撞 → 消耗 dt → 重复
//
//  参考：PhysX CCD 模式、Box2D 保守推进 (Conservative Advancement)
//
//  命名空间：ShanyuEngine.Physics
//  目标框架：net8.0 / C# 8.0+
//  外部依赖：仅 System.Numerics
// ============================================================================================

using System;
using System.Collections.Generic;
using System.Numerics;

namespace ShanyuEngine.Physics
{
    #region ======================== CCD 配置 ========================

    /// <summary>CCD 系统配置。</summary>
    public struct CCDConfig
    {
        public bool Enabled;
        public float VelocityThreshold;
        public int MaxCCDSteps;
        public float TOITolerance;
        public int MaxTOIIterations;
        public int MaxCCDBodiesPerFrame;

        public static readonly CCDConfig Default = new CCDConfig
        {
            Enabled = true,
            VelocityThreshold = 10.0f,
            MaxCCDSteps = 4,
            TOITolerance = 0.001f,
            MaxTOIIterations = 15,
            MaxCCDBodiesPerFrame = 64,
        };
    }

    #endregion

    #region ======================== CCD 碰撞结果 ========================

    public struct CCDHit
    {
        public int BodyIndex;
        public float TOI;
        public PhysicsVector3 Point;
        public PhysicsVector3 Normal;
        public float Penetration;
    }

    #endregion

    #region ======================== CCD 系统主类 ========================

    /// <summary>
    /// 连续碰撞检测 (CCD) 系统。
    /// 每个 PhysicsWorldReal 可持有此系统的一个实例。
    /// </summary>
    public sealed class CCDSystem
    {
        private CCDConfig _config;
        private DynamicAABBTree _broadphase;
        private List<RigidBody> _bodies;
        private Dictionary<int, int> _proxyToBody;

        private readonly List<int> _ccdCandidates = new List<int>();
        private readonly List<int> _alreadyProcessedCCD = new List<int>();

        public readonly List<CCDHit> CCDHits = new List<CCDHit>();
        public bool HasCCDThisFrame => CCDHits.Count > 0;
        public CCDConfig Config => _config;

        public CCDSystem(CCDConfig config, DynamicAABBTree broadphase,
                         List<RigidBody> bodies, Dictionary<int, int> proxyToBody)
        {
            _config = config;
            _broadphase = broadphase;
            _bodies = bodies;
            _proxyToBody = proxyToBody;
        }

        public void SetConfig(CCDConfig config) { _config = config; }

        #region ===== 主入口 =====

        public void RunCCD(float dt, List<ContactManifold> allManifolds,
                           ShapeCollisionDispatcher collisionDispatcher)
        {
            CCDHits.Clear();
            if (!_config.Enabled) return;

            FindCCDCandidates(dt);
            if (_ccdCandidates.Count == 0) return;

            if (_ccdCandidates.Count > _config.MaxCCDBodiesPerFrame)
                _ccdCandidates.Sort((a, b) =>
                    _bodies[b].LinearVelocity.LengthSquared().CompareTo(
                    _bodies[a].LinearVelocity.LengthSquared()));

            int maxCandidates = Math.Min(_ccdCandidates.Count, _config.MaxCCDBodiesPerFrame);
            _alreadyProcessedCCD.Clear();

            for (int ci = 0; ci < maxCandidates; ci++)
            {
                int bodyIdx = _ccdCandidates[ci];
                var body = _bodies[bodyIdx];
                if (body == null || body.IsStatic || body.IsSleeping) continue;
                if (body.CollisionShape == null) continue;
                if (_alreadyProcessedCCD.Contains(bodyIdx)) continue;

                float remainingDt = dt;
                int stepCount = 0;

                while (remainingDt > _config.TOITolerance && stepCount < _config.MaxCCDSteps)
                {
                    stepCount++;
                    var potentialHits = SweepCCD(body, bodyIdx, remainingDt);
                    if (potentialHits.Count == 0) break;

                    float earliestTOI = remainingDt;
                    CCDHit? bestHit = null;

                    foreach (var hit in potentialHits)
                    {
                        float toi = ComputeTOI(body, bodyIdx,
                            _bodies[hit.BodyIndex], hit.BodyIndex,
                            remainingDt, collisionDispatcher);
                        if (toi < earliestTOI) { earliestTOI = toi; bestHit = hit; }
                    }

                    if (bestHit.HasValue && earliestTOI < remainingDt - _config.TOITolerance)
                    {
                        var hit = bestHit.Value;
                        float advanceDt = Math.Max(earliestTOI, 0);
                        AdvanceBodyToTOI(body, advanceDt);

                        var ccdManifold = CreateCCDManifold(
                            bodyIdx, hit.BodyIndex, hit.Point, hit.Normal, hit.Penetration);
                        allManifolds.Add(ccdManifold);

                        CCDHits.Add(new CCDHit
                        {
                            BodyIndex = hit.BodyIndex,
                            TOI = earliestTOI / Math.Max(dt, 1e-6f),
                            Point = hit.Point,
                            Normal = hit.Normal,
                            Penetration = hit.Penetration,
                        });
                        remainingDt -= advanceDt;
                    }
                    else
                    {
                        AdvanceBodyToTOI(body, remainingDt);
                        remainingDt = 0;
                    }
                }
                _alreadyProcessedCCD.Add(bodyIdx);
            }
        }

        #endregion

        #region ===== 候选体检测 =====

        private void FindCCDCandidates(float dt)
        {
            _ccdCandidates.Clear();
            for (int i = 0; i < _bodies.Count; i++)
            {
                var body = _bodies[i];
                if (body == null || body.IsStatic || body.IsSleeping) continue;
                if (body.CollisionShape == null) continue;
                if (NeedsCCD(body, dt)) _ccdCandidates.Add(i);
            }
        }

        private bool NeedsCCD(RigidBody body, float dt)
        {
            float speed = body.LinearVelocity.Length();
            float stepDistance = speed * dt;
            var aabb = body.ComputeWorldAABB();
            float minExtent = Math.Min(
                Math.Min(aabb.Max.X - aabb.Min.X, aabb.Max.Y - aabb.Min.Y),
                aabb.Max.Z - aabb.Min.Z);
            return stepDistance > minExtent * 0.5f;
        }

        #endregion

        #region ===== 扫掠检测 =====

        private List<CCDHit> SweepCCD(RigidBody body, int bodyIdx, float dt)
        {
            var results = new List<CCDHit>();
            PhysicsVector3 displacement = body.LinearVelocity * dt;

            AABBShape startAABB = body.ComputeWorldAABB();
            PhysicsVector3 endPos = body.Position + displacement;
            var endTransform = new PhysicsTransform(endPos, body.Rotation);
            AABBShape endAABB = body.CollisionShape.ComputeAABB(endTransform);
            AABBShape sweepAABB = AABBShape.Union(startAABB, endAABB);

            _broadphase.Query(sweepAABB, (proxyId, userData) =>
            {
                if (!_proxyToBody.TryGetValue(proxyId, out int otherIdx)) return;
                if (otherIdx == bodyIdx) return;
                if (otherIdx >= _bodies.Count) return;
                var other = _bodies[otherIdx];
                if (other == null || other.CollisionShape == null) return;
                if (other.IsStatic && other.IsSleeping) return;
                results.Add(new CCDHit
                {
                    BodyIndex = otherIdx,
                    TOI = 1.0f,
                    Point = PhysicsVector3.Zero,
                    Normal = PhysicsVector3.UnitY,
                    Penetration = 0,
                });
            });
            return results;
        }

        #endregion

        #region ===== TOI 计算（保守推进） =====

        private float ComputeTOI(RigidBody bodyA, int idxA, RigidBody bodyB, int idxB,
            float dt, ShapeCollisionDispatcher dispatcher)
        {
            if (bodyA.CollisionShape == null || bodyB.CollisionShape == null) return dt;
            float t0 = 0, t1 = dt;

            for (int iter = 0; iter < _config.MaxTOIIterations; iter++)
            {
                float tMid = (t0 + t1) * 0.5f;
                PhysicsVector3 posA = bodyA.Position + bodyA.LinearVelocity * tMid;
                PhysicsVector3 posB = bodyB.Position + bodyB.LinearVelocity * tMid;

                var transformA = new PhysicsTransform(posA, bodyA.Rotation);
                var transformB = new PhysicsTransform(posB, bodyB.Rotation);

                if (dispatcher.TestCollision(
                    bodyA.CollisionShape, transformA,
                    bodyB.CollisionShape, transformB,
                    out PhysicsVector3 normal, out float penetration))
                {
                    t1 = tMid;
                }
                else
                {
                    t0 = tMid;
                }
                if (t1 - t0 < _config.TOITolerance) break;
            }
            return (t0 + t1) * 0.5f;
        }

        #endregion

        #region ===== 推进 & 流形生成 =====

        private void AdvanceBodyToTOI(RigidBody body, float dt)
        {
            if (dt <= 0) return;
            body.Position += body.LinearVelocity * dt;
            body.UpdateInertiaWorld();
        }

        private ContactManifold CreateCCDManifold(int bodyIdxA, int bodyIdxB,
            PhysicsVector3 point, PhysicsVector3 normal, float penetration)
        {
            var manifold = new ContactManifold
            {
                BodyIndexA = bodyIdxA,
                BodyIndexB = bodyIdxB,
                Normal = normal,
                Timestamp = 0,
            };
            manifold.Points.Add(new ContactPoint
            {
                Position = point,
                Normal = normal,
                PenetrationDepth = Math.Max(penetration, 0.001f),
                ContactID = bodyIdxA * 10000 + bodyIdxB + 1000000,
                ShapeIndexA = bodyIdxA,
                ShapeIndexB = bodyIdxB,
            });
            manifold.Normalize();
            return manifold;
        }

        #endregion

        #region ===== 工具 =====

        public IReadOnlyList<int> GetCCDCandidates() => _ccdCandidates;
        public void Reset()
        {
            _ccdCandidates.Clear();
            _alreadyProcessedCCD.Clear();
            CCDHits.Clear();
        }

        #endregion
    }

    #endregion
}
