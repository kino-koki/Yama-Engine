// ============================================================================================
//  CharacterControllerReal.cs
//
//  山雨引擎 (ShanyuEngine) 真实角色控制器模块 —— 纯手写实现，无外部物理库依赖。
//
//  内容：
//    - CharacterControllerReal：胶囊形状角色控制器
//      - SweepPhase1：沿运动方向扫掠检测
//      - StepPhase：台阶检测（射线检测步骤高度内是否有平台）
//      - SlopePhase：斜坡检测（法线Y分量判定）
//      - SweepPhase2：剩余距离扫掠
//    - IsGrounded：地面检测（球体投射/多下射线）
//    - Jump：跳跃
//    - 碰撞响应：推挤/滑动/吸附
//    - 平台跟随：移动平台上的角色随平台移动
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
    /// <summary>角色控制器状态。</summary>
    public enum CharacterControllerState
    {
        /// <summary>在地面上。</summary>
        Grounded,
        /// <summary>在空中。</summary>
        InAir,
        /// <summary>在陡峭斜面上（即将滑动）。</summary>
        OnSteepSlope,
        /// <summary>在台阶上。</summary>
        OnStep,
    }

    /// <summary>角色控制器配置。</summary>
    public struct CharacterControllerConfig
    {
        /// <summary>胶囊体半径。</summary>
        public float Radius;

        /// <summary>胶囊体半高度（总高度 = 2 * (Radius + HalfHeight)）。</summary>
        public float HalfHeight;

        /// <summary>步骤高度（自动上台阶的最大高度）。</summary>
        public float StepHeight;

        /// <summary>最大可行走斜坡角度（弧度）。</summary>
        public float MaxSlopeAngle;

        /// <summary>Skin宽度（避免卡入几何体）。</summary>
        public float SkinWidth;

        /// <summary>最小移动距离（低于此距离不移动）。</summary>
        public float MinMoveDistance;

        /// <summary>重力。</summary>
        public PhysicsVector3 Gravity;

        /// <summary>最大下落速度。</summary>
        public float MaxFallSpeed;

        /// <summary>推挤质量（推动动态物体的等效质量）。</summary>
        public float PushMass;

        /// <summary>斜坡最大cos值（cos(MaxSlopeAngle)）。</summary>
        public float MaxSlopeCos;

        public static readonly CharacterControllerConfig Default = new CharacterControllerConfig
        {
            Radius = 0.4f,
            HalfHeight = 0.7f,
            StepHeight = 0.4f,
            MaxSlopeAngle = 45.0f * (MathF.PI / 180.0f),
            SkinWidth = 0.02f,
            MinMoveDistance = 0.001f,
            Gravity = new PhysicsVector3(0, -9.81f, 0),
            MaxFallSpeed = -20.0f,
            PushMass = 10.0f,
            MaxSlopeCos = (float)Math.Cos(45.0f * Math.PI / 180.0),
        };
    }

    /// <summary>角色控制器移动结果。</summary>
    public struct CharacterMoveResult
    {
        /// <summary>实际移动向量。</summary>
        public PhysicsVector3 ActualMotion;

        /// <summary>剩余未移动向量。</summary>
        public PhysicsVector3 RemainingMotion;

        /// <summary>接触地面的法线。</summary>
        public PhysicsVector3 GroundNormal;

        /// <summary>是否在地面上。</summary>
        public bool IsGrounded;

        /// <summary>碰撞到的物体列表。</summary>
        public List<int> HitBodies;

        public CharacterMoveResult()
        {
            ActualMotion = PhysicsVector3.Zero;
            RemainingMotion = PhysicsVector3.Zero;
            GroundNormal = PhysicsVector3.UnitY;
            IsGrounded = false;
            HitBodies = new List<int>();
        }
    }

    /// <summary>
    /// 角色控制器 —— 基于胶囊形状的运动学碰撞控制器。
    /// 通过扫掠检测实现精确的移动、台阶攀登、斜坡处理和地面检测。
    /// </summary>
    public class CharacterControllerReal
    {
        #region 字段

        private CharacterControllerConfig _config;
        private PhysicsWorld _world;

        // 状态
        private PhysicsVector3 _position;
        private PhysicsVector3 _velocity;
        private PhysicsVector3 _groundNormal;
        private CharacterControllerState _state;
        private bool _isGrounded;
        private float _groundDistance;

        // 平台跟随
        private PhysicsVector3 _platformVelocity;
        private PhysicsVector3 _lastPlatformPosition;
        private bool _onPlatform;
        private int _platformBodyIndex;

        // 碰撞缓存
        private List<RaycastHitData> _sweepHits = new List<RaycastHitData>();
        private List<RaycastHitData> _groundHits = new List<RaycastHitData>();
        private const int MAX_GROUND_RAYS = 12;

        #endregion

        #region 构造

        public CharacterControllerReal(PhysicsWorld world, CharacterControllerConfig config = default)
        {
            _world = world ?? throw new ArgumentNullException(nameof(world));
            if (config.Radius <= 0) config = CharacterControllerConfig.Default;
            _config = config;
            _config.MaxSlopeCos = (float)Math.Cos(_config.MaxSlopeAngle);
            _position = PhysicsVector3.Zero;
            _velocity = PhysicsVector3.Zero;
            _groundNormal = PhysicsVector3.UnitY;
            _state = CharacterControllerState.InAir;
            _isGrounded = false;
            _groundDistance = float.MaxValue;
            _platformVelocity = PhysicsVector3.Zero;
            _lastPlatformPosition = PhysicsVector3.Zero;
            _onPlatform = false;
            _platformBodyIndex = -1;
        }

        #endregion

        #region 公共属性

        public PhysicsVector3 Position { get => _position; set => _position = value; }
        public PhysicsVector3 Velocity { get => _velocity; set => _velocity = value; }
        public PhysicsVector3 GroundNormal => _groundNormal;
        public CharacterControllerState State => _state;
        public bool IsGrounded => _isGrounded;
        public CharacterControllerConfig Config => _config;

        #endregion

        #region 核心：移动

        /// <summary>执行移动（包含完整的扫掠+台阶+斜坡处理）。</summary>
        public CharacterMoveResult Move(PhysicsVector3 motion)
        {
            var result = new CharacterMoveResult();
            result.GroundNormal = _groundNormal;

            if (motion.LengthSquared() < _config.MinMoveDistance * _config.MinMoveDistance)
            {
                result.IsGrounded = _isGrounded;
                return result;
            }

            PhysicsVector3 remainingMotion = motion;

            // 平台跟随：加上平台速度位移
            if (_onPlatform && _platformBodyIndex >= 0)
            {
                remainingMotion = remainingMotion + _platformVelocity;
            }

            // ===== SweepPhase1：沿运动方向扫掠 =====
            float moveDistance = remainingMotion.Length();
            PhysicsVector3 moveDir = remainingMotion.Normalized();
            float skinWidth = _config.SkinWidth;
            float effectiveRadius = _config.Radius - skinWidth;
            if (effectiveRadius < 0.01f) effectiveRadius = 0.01f;

            // 构造扫掠胶囊AABB
            PhysicsVector3 capsuleTop = _position + PhysicsVector3.UnitY * _config.HalfHeight;
            PhysicsVector3 capsuleBottom = _position - PhysicsVector3.UnitY * _config.HalfHeight;

            // 扫掠检测（沿运动方向）
            if (SweepCapsule(capsuleTop, capsuleBottom, effectiveRadius, moveDir, moveDistance,
                out float sweepHitDist, out PhysicsVector3 sweepHitNormal, out int sweepHitBody))
            {
                float allowedDist = Math.Max(sweepHitDist - skinWidth, 0);

                // 斜坡检测
                if (moveDir.Y >= -0.01f && sweepHitNormal.Y > 0 && sweepHitNormal.Y < _config.MaxSlopeCos)
                {
                    // 太陡的斜坡 → 滑动
                    remainingMotion = ProjectOntoPlane(remainingMotion, sweepHitNormal);
                    result.GroundNormal = sweepHitNormal;
                    result.HitBodies.Add(sweepHitBody);
                }
                else
                {
                    // 正常碰撞：移动到接触点并滑动
                    _position = _position + moveDir * allowedDist;

                    // 滑动：移除法线方向分量
                    remainingMotion = remainingMotion - sweepHitNormal * remainingMotion.Dot(sweepHitNormal);
                    result.GroundNormal = sweepHitNormal;
                    result.HitBodies.Add(sweepHitBody);

                    // 推挤动态物体
                    PushDynamicBody(sweepHitBody, -sweepHitNormal * moveDir.Dot(sweepHitNormal));
                }
            }

            // ===== StepPhase：台阶检测 =====
            if (!_isGrounded || (moveDir.Y >= -0.01f && _isGrounded))
            {
                if (TryStepUp(remainingMotion, effectiveRadius, out PhysicsVector3 steppedMotion, out int stepHitBody))
                {
                    remainingMotion = steppedMotion;
                    result.HitBodies.Add(stepHitBody);
                    _state = CharacterControllerState.OnStep;
                }
            }

            // ===== SlopePhase：斜坡检测 =====
            if (CheckSlope(effectiveRadius, out PhysicsVector3 slopeNormal, out float slopePenetration))
            {
                if (slopeNormal.Y < _config.MaxSlopeCos && slopeNormal.Y > 0.01f)
                {
                    // 在陡坡上：重力沿坡滑动
                    PhysicsVector3 gravitySlide = PhysicsVector3.Zero;
                    PhysicsVector3 gravity = _config.Gravity;
                    gravitySlide = gravity - slopeNormal * gravity.Dot(slopeNormal);
                    remainingMotion = remainingMotion + gravitySlide;
                    result.GroundNormal = slopeNormal;
                    _state = CharacterControllerState.OnSteepSlope;
                }
                else if (slopeNormal.Y >= _config.MaxSlopeCos)
                {
                    result.GroundNormal = slopeNormal;
                    if (slopePenetration > 0)
                    {
                        _position = _position + slopeNormal * (slopePenetration + skinWidth);
                    }
                }
            }

            // ===== SweepPhase2：剩余距离扫掠 =====
            if (remainingMotion.LengthSquared() > _config.MinMoveDistance * _config.MinMoveDistance)
            {
                PhysicsVector3 remainingDir = remainingMotion.Normalized();
                float remainingDist = remainingMotion.Length();

                if (SweepCapsule(
                    _position + PhysicsVector3.UnitY * _config.HalfHeight,
                    _position - PhysicsVector3.UnitY * _config.HalfHeight,
                    effectiveRadius, remainingDir, remainingDist,
                    out float hitDist2, out PhysicsVector3 hitNormal2, out int hitBody2))
                {
                    float allowedDist2 = Math.Max(hitDist2 - skinWidth, 0);
                    _position = _position + remainingDir * allowedDist2;

                    // 二次滑动
                    PhysicsVector3 remainingAfterSlide = remainingMotion - remainingDir * allowedDist2;
                    remainingAfterSlide = remainingAfterSlide - hitNormal2 * remainingAfterSlide.Dot(hitNormal2);

                    if (remainingAfterSlide.LengthSquared() > _config.MinMoveDistance * _config.MinMoveDistance)
                    {
                        PhysicsVector3 dir3 = remainingAfterSlide.Normalized();
                        float dist3 = remainingAfterSlide.Length();

                        if (SweepCapsule(
                            _position + PhysicsVector3.UnitY * _config.HalfHeight,
                            _position - PhysicsVector3.UnitY * _config.HalfHeight,
                            effectiveRadius, dir3, dist3,
                            out float hitDist3, out _, out _))
                        {
                            float allowedDist3 = Math.Max(hitDist3 - skinWidth, 0);
                            _position = _position + dir3 * allowedDist3;
                        }
                        else
                        {
                            _position = _position + remainingAfterSlide;
                        }
                    }
                    result.HitBodies.Add(hitBody2);
                }
                else
                {
                    _position = _position + remainingMotion;
                }
            }

            // ===== 地面检测更新 =====
            UpdateGroundStatus(effectiveRadius);

            result.ActualMotion = _position - (result.ActualMotion = _position - (motion * 0));
            result.ActualMotion = motion - remainingMotion;
            result.RemainingMotion = PhysicsVector3.Zero;
            result.IsGrounded = _isGrounded;

            // ===== 重力 =====
            if (!_isGrounded)
            {
                _velocity = _velocity + _config.Gravity;
                if (_velocity.Y < _config.MaxFallSpeed)
                    _velocity = new PhysicsVector3(_velocity.X, _config.MaxFallSpeed, _velocity.Z);
                _position = _position + _velocity * _world.FixedTimestep;
                _state = CharacterControllerState.InAir;
            }
            else
            {
                _velocity = new PhysicsVector3(_velocity.X, 0, _velocity.Z);
                _state = CharacterControllerState.Grounded;
            }

            return result;
        }

        #endregion

        #region 地面检测

        /// <summary>地面检测（球体投射 + 多方向下射线）。</summary>
        public bool IsGrounded()
        {
            float effectiveRadius = _config.Radius - _config.SkinWidth;
            return CheckGround(effectiveRadius);
        }

        /// <summary>检查是否在地面上。</summary>
        private bool CheckGround(float effectiveRadius)
        {
            _groundHits.Clear();
            float groundProbe = _config.SkinWidth + 0.05f;

            // 向下球体投射
            PhysicsVector3 probeOrigin = _position - PhysicsVector3.UnitY * (_config.HalfHeight - effectiveRadius * 0.5f);
            PhysicsVector3 probeDir = -PhysicsVector3.UnitY;
            float probeDist = groundProbe + _config.Radius;

            if (_world.Raycast(probeOrigin, probeDir, probeDist, out var hit))
            {
                _groundHits.Add(hit);
            }

            // 多方向下射线（环形分布）
            for (int i = 0; i < MAX_GROUND_RAYS; i++)
            {
                float angle = (float)(2.0 * Math.PI * i / MAX_GROUND_RAYS);
                PhysicsVector3 offset = new PhysicsVector3(
                    (float)Math.Cos(angle) * effectiveRadius * 0.8f, 0,
                    (float)Math.Sin(angle) * effectiveRadius * 0.8f);
                PhysicsVector3 rayOrigin = _position + offset - PhysicsVector3.UnitY * (_config.HalfHeight * 0.5f);

                if (_world.Raycast(rayOrigin, probeDir, probeDist, out var ringHit))
                {
                    _groundHits.Add(ringHit);
                }
            }

            // 找最近的地面接触
            float closestDist = float.MaxValue;
            PhysicsVector3 bestNormal = PhysicsVector3.UnitY;

            foreach (var gh in _groundHits)
            {
                if (gh.Distance < closestDist && gh.Normal.Y > 0.01f)
                {
                    closestDist = gh.Distance;
                    bestNormal = gh.Normal;
                }
            }

            if (closestDist < groundProbe + effectiveRadius)
            {
                _groundNormal = bestNormal;
                _groundDistance = closestDist;
                _isGrounded = true;

                // 吸附到地面
                if (closestDist > _config.SkinWidth && bestNormal.Y > _config.MaxSlopeCos)
                {
                    _position = _position + (-PhysicsVector3.UnitY) * (closestDist - _config.SkinWidth);
                }

                return true;
            }

            _isGrounded = false;
            _groundDistance = float.MaxValue;
            return false;
        }

        /// <summary>更新地面状态。</summary>
        private void UpdateGroundStatus(float effectiveRadius)
        {
            CheckGround(effectiveRadius);
        }

        #endregion

        #region 台阶检测

        /// <summary>尝试上台阶。</summary>
        private bool TryStepUp(PhysicsVector3 motion, float effectiveRadius,
            out PhysicsVector3 steppedMotion, out int hitBody)
        {
            steppedMotion = motion;
            hitBody = -1;

            if (_config.StepHeight <= 0) return false;

            // 检测前方是否有障碍
            PhysicsVector3 moveDir = motion.Normalized();
            float moveDist = motion.Length();

            PhysicsVector3 top = _position + PhysicsVector3.UnitY * _config.HalfHeight;
            PhysicsVector3 bottom = _position - PhysicsVector3.UnitY * _config.HalfHeight;

            if (!SweepCapsule(top, bottom, effectiveRadius, moveDir, moveDist,
                out float hitDist, out PhysicsVector3 hitNormal, out hitBody))
                return false;

            // 没有碰撞 → 无需台阶
            if (hitDist >= moveDist) return false;

            // 碰撞了 → 尝试上台阶
            float allowedDist = Math.Max(hitDist - _config.SkinWidth, 0);

            // 上移
            PhysicsVector3 stepUpMotion = PhysicsVector3.UnitY * _config.StepHeight;

            // 检测上移路径是否畅通
            PhysicsVector3 stepTopStart = _position + PhysicsVector3.UnitY * _config.HalfHeight;
            PhysicsVector3 stepBottomStart = _position - PhysicsVector3.UnitY * _config.HalfHeight;

            if (SweepCapsule(stepTopStart, stepBottomStart, effectiveRadius,
                PhysicsVector3.UnitY, _config.StepHeight,
                out float stepHitDist, out _, out _))
            {
                // 上方有障碍，不能上台阶
                if (stepHitDist < _config.StepHeight) return false;
            }

            // 执行上台阶
            _position = _position + stepUpMotion;

            // 前方水平推进（到步骤高度位置）
            PhysicsVector3 remainingDir = moveDir;
            float remainingDist = moveDist - allowedDist;

            PhysicsVector3 newTop = _position + PhysicsVector3.UnitY * _config.HalfHeight;
            PhysicsVector3 newBottom = _position - PhysicsVector3.UnitY * _config.HalfHeight;

            if (!SweepCapsule(newTop, newBottom, effectiveRadius, remainingDir, remainingDist,
                out float forwardHitDist, out _, out _))
            {
                // 前方畅通 → 移动到目标位置
                _position = _position + remainingDir * remainingDist;
                steppedMotion = PhysicsVector3.Zero;
            }
            else
            {
                // 前方仍有障碍 → 只移动到允许距离
                float forwardAllowed = Math.Max(forwardHitDist - _config.SkinWidth, 0);
                _position = _position + remainingDir * forwardAllowed;
                steppedMotion = remainingDir * (remainingDist - forwardAllowed);
            }

            // 下落回地面
            PhysicsVector3 dropTop = _position + PhysicsVector3.UnitY * _config.HalfHeight;
            PhysicsVector3 dropBottom = _position - PhysicsVector3.UnitY * _config.HalfHeight;

            if (_world.Raycast(_position, -PhysicsVector3.UnitY, _config.StepHeight + _config.SkinWidth,
                out var dropHit))
            {
                float dropDist = dropHit.Distance;
                if (dropDist > _config.SkinWidth)
                {
                    _position = _position + (-PhysicsVector3.UnitY) * (dropDist - _config.SkinWidth);
                }
            }

            return true;
        }

        #endregion

        #region 斜坡检测

        /// <summary>检查当前站立面的斜坡。</summary>
        private bool CheckSlope(float effectiveRadius,
            out PhysicsVector3 slopeNormal, out float penetration)
        {
            slopeNormal = _groundNormal;
            penetration = 0;

            if (!_isGrounded) return false;

            PhysicsVector3 probeOrigin = _position;
            PhysicsVector3 probeDir = -_groundNormal;
            float probeDist = _config.SkinWidth + 0.1f;

            if (_world.Raycast(probeOrigin, probeDir, probeDist, out var hit))
            {
                slopeNormal = hit.Normal;
                penetration = probeDist - hit.Distance;
                return true;
            }

            return false;
        }

        #endregion

        #region 跳跃

        /// <summary>执行跳跃。</summary>
        public void Jump(PhysicsVector3 jumpVelocity)
        {
            if (!_isGrounded) return;

            _velocity = jumpVelocity;
            _isGrounded = false;
            _state = CharacterControllerState.InAir;

            // 吸附偏移
            _position = _position + PhysicsVector3.UnitY * _config.SkinWidth;
        }

        /// <summary>按力跳跃。</summary>
        public void JumpWithForce(float force)
        {
            if (!_isGrounded) return;
            PhysicsVector3 jumpVel = PhysicsVector3.UnitY * (float)Math.Sqrt(2.0 * force);
            Jump(jumpVel);
        }

        #endregion

        #region 胶囊扫掠

        /// <summary>胶囊体扫掠检测。</summary>
        private bool SweepCapsule(
            PhysicsVector3 top, PhysicsVector3 bottom,
            float radius, PhysicsVector3 direction, float maxDistance,
            out float hitDistance, out PhysicsVector3 hitNormal, out int hitBody)
        {
            hitDistance = float.MaxValue;
            hitNormal = PhysicsVector3.UnitY;
            hitBody = -1;

            if (direction.LengthSquared() < 1e-8f || maxDistance <= 0) return false;

            PhysicsVector3 dir = direction.Normalized();
            float closestT = maxDistance;

            // 使用多个球体射线近似胶囊扫掠
            int numSegments = 3;
            PhysicsVector3 segDir = (top - bottom);
            float segLen = segDir.Length();
            PhysicsVector3 segNorm = segLen > 1e-6f ? segDir / segLen : PhysicsVector3.UnitY;

            bool anyHit = false;

            for (int i = 0; i <= numSegments; i++)
            {
                float t = (float)i / numSegments;
                PhysicsVector3 sphereCenter = bottom + segDir * t;

                if (_world.Raycast(sphereCenter, dir, maxDistance, out var hit))
                {
                    float adjustedT = hit.Distance - radius;
                    if (adjustedT < 0) adjustedT = 0;

                    if (adjustedT < closestT)
                    {
                        closestT = adjustedT;
                        hitNormal = hit.Normal;
                        hitBody = GetBodyIndexFromHit(hit);
                        anyHit = true;
                    }
                }
            }

            // 额外检测：对每个动态体进行精确胶囊vs形状测试
            if (_world.ContactManifolds.Count > 0)
            {
                for (int mi = 0; mi < _world.ContactManifolds.Count; mi++)
                {
                    var manifold = _world.ContactManifolds[mi];
                    foreach (var cp in manifold.Points)
                    {
                        PhysicsVector3 toContact = cp.Position - _position;
                        float projDist = toContact.Dot(dir);
                        if (projDist > 0 && projDist < closestT)
                        {
                            // 确认接触点在胶囊范围内
                            PhysicsVector3 localContact = cp.Position - bottom;
                            float alongSeg = localContact.Dot(segNorm);
                            float perpDist = (localContact - segNorm * alongSeg).Length();

                            if (alongSeg >= 0 && alongSeg <= segLen && perpDist <= radius + _config.SkinWidth)
                            {
                                closestT = projDist;
                                hitNormal = cp.Normal;
                                hitBody = manifold.BodyIndexB;
                                anyHit = true;
                            }
                        }
                    }
                }
            }

            hitDistance = closestT;
            return anyHit;
        }

        /// <summary>从射线命中获取刚体索引。</summary>
        private int GetBodyIndexFromHit(RaycastHitData hit)
        {
            if (hit.HitObject is RigidBody rb)
            {
                for (int i = 0; i < _world.BodyCount; i++)
                {
                    if (_world.Bodies[i] == rb) return i;
                }
            }
            return -1;
        }

        #endregion

        #region 碰撞响应

        /// <summary>推挤动态物体。</summary>
        private void PushDynamicBody(int bodyIndex, PhysicsVector3 pushImpulse)
        {
            if (bodyIndex < 0 || bodyIndex >= _world.BodyCount) return;
            var body = _world.GetBody(bodyIndex);
            if (body == null || body.IsStatic || body.IsKinematic) return;

            PhysicsVector3 pushForce = pushImpulse * _config.PushMass / _world.FixedTimestep;
            body.ApplyCentralImpulse(pushImpulse * _config.PushMass);
        }

        /// <summary>将向量投影到平面上。</summary>
        private static PhysicsVector3 ProjectOntoPlane(PhysicsVector3 v, PhysicsVector3 planeNormal)
        {
            return v - planeNormal * v.Dot(planeNormal);
        }

        #endregion

        #region 平台跟随

        /// <summary>更新平台跟随。</summary>
        public void UpdatePlatformFollow(float dt)
        {
            if (!_isGrounded || _groundHits.Count == 0)
            {
                _onPlatform = false;
                _platformBodyIndex = -1;
                _platformVelocity = PhysicsVector3.Zero;
                return;
            }

            // 找到当前站立的平台
            foreach (var gh in _groundHits)
            {
                if (gh.HitObject is RigidBody platformBody)
                {
                    int platIdx = -1;
                    for (int i = 0; i < _world.BodyCount; i++)
                    {
                        if (_world.Bodies[i] == platformBody) { platIdx = i; break; }
                    }
                    if (platIdx < 0) continue;

                    if (platIdx != _platformBodyIndex)
                    {
                        _platformBodyIndex = platIdx;
                        _lastPlatformPosition = platformBody.Position;
                        _onPlatform = true;
                    }
                    else
                    {
                        // 计算平台速度
                        _platformVelocity = (platformBody.Position - _lastPlatformPosition) / Math.Max(dt, 1e-6f);
                        _lastPlatformPosition = platformBody.Position;
                    }

                    return;
                }
            }

            _onPlatform = false;
            _platformBodyIndex = -1;
            _platformVelocity = PhysicsVector3.Zero;
        }

        #endregion

        #region 工具

        /// <summary>获取胶囊体世界空间AABB。</summary>
        public AABBShape GetCapsuleAABB()
        {
            float r = _config.Radius;
            PhysicsVector3 top = _position + PhysicsVector3.UnitY * _config.HalfHeight;
            PhysicsVector3 bottom = _position - PhysicsVector3.UnitY * _config.HalfHeight;
            PhysicsVector3 min = PhysicsVector3.Min(top, bottom) - new PhysicsVector3(r);
            PhysicsVector3 max = PhysicsVector3.Max(top, bottom) + new PhysicsVector3(r);
            return new AABBShape(min, max);
        }

        /// <summary>重置控制器状态。</summary>
        public void Reset(PhysicsVector3 position)
        {
            _position = position;
            _velocity = PhysicsVector3.Zero;
            _groundNormal = PhysicsVector3.UnitY;
            _state = CharacterControllerState.InAir;
            _isGrounded = false;
            _groundDistance = float.MaxValue;
            _platformVelocity = PhysicsVector3.Zero;
            _onPlatform = false;
            _platformBodyIndex = -1;
        }

        #endregion
    }
}
