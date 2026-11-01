// ============================================================================================
//  PhysicsEngineEnhanced.cs - yama引擎物理引擎增强模块
//  功能: 多体动力学(顺序冲量) / CCD / 休眠 / 材质 / 回调 / 触发器 / 射线检测 / 调试视图
//  命名空间: YamaEngine.Physics | 目标: net8.0 | 依赖: System.Numerics
// ============================================================================================
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;

namespace YamaEngine.Physics
{
    public enum EnhancedBodyType { Dynamic, Static, Kinematic }
    public enum EnhancedColliderShape { Sphere, Box, Capsule }

    /// <summary>物理材质: 摩擦/弹性/密度组合。</summary>
    public sealed class PhysicsMaterialEnhanced
    {
        public string Name { get; set; } = "Default";
        public float StaticFriction { get; set; } = 0.5f;
        public float DynamicFriction { get; set; } = 0.4f;
        public float Restitution { get; set; } = 0.3f;
        public float Density { get; set; } = 1000f;

        public static (float friction, float restitution) Combine(PhysicsMaterialEnhanced a, PhysicsMaterialEnhanced b)
            => (MathF.Sqrt(a.StaticFriction * b.StaticFriction), MathF.Min(a.Restitution, b.Restitution));

        public static PhysicsMaterialEnhanced Rubber => new() { Name = "Rubber", StaticFriction = 0.9f, DynamicFriction = 0.8f, Restitution = 0.8f, Density = 1100f };
        public static PhysicsMaterialEnhanced Ice => new() { Name = "Ice", StaticFriction = 0.05f, DynamicFriction = 0.03f, Restitution = 0.1f, Density = 917f };
        public static PhysicsMaterialEnhanced Metal => new() { Name = "Metal", StaticFriction = 0.6f, DynamicFriction = 0.5f, Restitution = 0.4f, Density = 7800f };
    }

    /// <summary>增强物理体。</summary>
    public sealed class EnhancedPhysicsBody
    {
        public int Id;
        public string Name = "";
        public EnhancedBodyType BodyType = EnhancedBodyType.Dynamic;
        public EnhancedColliderShape Shape;
        public Vector3 Position;
        public Quaternion Orientation = Quaternion.Identity;
        public Vector3 Velocity;
        public Vector3 AngularVelocity;
        public Vector3 ForceAccumulator;
        public Vector3 TorqueAccumulator;
        public float Mass = 1f;
        public float InverseMass = 1f;
        public float Radius;
        public Vector3 HalfExtents;
        public float Height;
        public PhysicsMaterialEnhanced? Material;
        public bool IsTrigger;
        public float LinearDamping = 0.01f;
        public float AngularDamping = 0.01f;
        public bool IsSleeping;
        public float SleepTimer;
        public Vector3 LastAcceleration;

        public bool IsStatic => BodyType == EnhancedBodyType.Static;
        public bool IsKinematic => BodyType == EnhancedBodyType.Kinematic;
        public EnhancedPhysicsBody(int id) { Id = id; }

        public void ApplyForce(Vector3 force) { if (!IsStatic && !IsKinematic) ForceAccumulator += force; }

        public void ApplyImpulse(Vector3 impulse, Vector3 contactPoint)
        {
            if (IsStatic || IsKinematic) return;
            Velocity += impulse * InverseMass;
            AngularVelocity += Vector3.Cross(contactPoint - Position, impulse) * InverseMass * 0.5f;
            WakeUp();
        }

        public void WakeUp() { IsSleeping = false; SleepTimer = 0f; }

        public Vector3 GetVelocityAtPoint(Vector3 point) =>
            IsStatic ? Vector3.Zero : Velocity + Vector3.Cross(AngularVelocity, point - Position);
    }

    public struct ContactConstraintEnhanced
    {
        public int BodyA, BodyB;
        public Vector3 ContactPoint, Normal;
        public float Penetration, AccumulatedNormalImpulse, AccumulatedTangentImpulse, Restitution, Friction;
    }

    public struct EnhancedCollisionEvent
    {
        public EnhancedPhysicsBody BodyA, BodyB;
        public Vector3 ContactPoint, Normal;
        public float Penetration;
    }

    public struct RayHitEnhanced
    {
        public EnhancedPhysicsBody? Body;
        public Vector3 Point, Normal;
        public float Distance, TimeOfImpact;
    }

    public struct DebugLine { public Vector3 Start, End, Color; }

    /// <summary>物理引擎增强系统: 多体动力学 + CCD + 休眠 + 回调 + 射线检测 + 调试。</summary>
    public sealed class PhysicsEngineEnhanced
    {
        private readonly List<EnhancedPhysicsBody> _bodies = new(128);
        private readonly List<ContactConstraintEnhanced> _contacts = new(256);
        private readonly Dictionary<int, EnhancedPhysicsBody> _bodyMap = new(128);
        private readonly HashSet<(int, int)> _prevPairs = new(64), _curPairs = new(64);
        private readonly HashSet<(int, int)> _prevTrig = new(32), _curTrig = new(32);
        private readonly List<DebugLine> _debugLines = new(512);

        public Vector3 Gravity { get; set; } = new(0f, -9.81f, 0f);
        public int SolverIterations { get; set; } = 10;
        public bool EnableSleep { get; set; } = true;
        public float SleepVelocityThreshold { get; set; } = 0.05f;
        public float SleepAccelerationThreshold { get; set; } = 0.5f;
        public float SleepTimeRequired { get; set; } = 1.0f;
        public bool EnableCCD { get; set; } = true;
        public float CCDSkinWidth { get; set; } = 0.01f;
        public float Baumgarte { get; set; } = 0.2f;
        public float Slop { get; set; } = 0.005f;
        public int BodyCount => _bodies.Count;

        public Action<EnhancedCollisionEvent>? OnCollisionEnter, OnCollisionStay, OnCollisionExit;
        public Action<EnhancedCollisionEvent>? OnTriggerEnter, OnTriggerExit;

        public EnhancedPhysicsBody CreateBody(EnhancedColliderShape shape, Vector3 position,
            float mass = 1f, float radius = 0.5f, Vector3 halfExtents = default, float height = 1f)
        {
            int id = _bodies.Count;
            var body = new EnhancedPhysicsBody(id)
            {
                Shape = shape, Position = position, Mass = mass,
                InverseMass = mass > 0f ? 1f / mass : 0f, Radius = radius,
                HalfExtents = halfExtents == default ? new Vector3(0.5f) : halfExtents, Height = height
            };
            _bodies.Add(body);
            _bodyMap[id] = body;
            return body;
        }

        public EnhancedPhysicsBody CreateStaticBody(EnhancedColliderShape shape, Vector3 position,
            float radius = 0.5f, Vector3 halfExtents = default, float height = 1f)
        {
            var body = CreateBody(shape, position, 0f, radius, halfExtents, height);
            body.BodyType = EnhancedBodyType.Static;
            body.InverseMass = 0f;
            return body;
        }

        /// <summary>执行物理模拟步: 力→速度积分→碰撞→CCD→冲量求解→位置积分→修正→休眠→回调。</summary>
        public void Step(float dt)
        {
            if (dt <= 0f) return;
            _contacts.Clear(); _curPairs.Clear(); _curTrig.Clear();
            ApplyForces();
            IntegrateVelocities(dt);
            DetectCollisions();
            if (EnableCCD) PerformCCD(dt);
            SolveConstraints();
            IntegratePositions(dt);
            PositionCorrection();
            if (EnableSleep) UpdateSleep(dt);
            DispatchCallbacks();
            foreach (var b in _bodies) { b.ForceAccumulator = Vector3.Zero; b.TorqueAccumulator = Vector3.Zero; }
        }

        private void ApplyForces()
        {
            foreach (var b in _bodies)
                if (!b.IsStatic && !b.IsSleeping) b.ForceAccumulator += Gravity * b.Mass;
        }

        private void IntegrateVelocities(float dt)
        {
            foreach (var b in _bodies)
            {
                if (b.IsStatic || b.IsSleeping) continue;
                b.LastAcceleration = b.ForceAccumulator * b.InverseMass;
                b.Velocity += b.LastAcceleration * dt;
                b.Velocity *= (1f - b.LinearDamping);
                b.AngularVelocity *= (1f - b.AngularDamping);
            }
        }

        private void IntegratePositions(float dt)
        {
            foreach (var b in _bodies)
            {
                if (b.IsStatic || b.IsSleeping) continue;
                b.Position += b.Velocity * dt;
                if (b.AngularVelocity.LengthSquared() > 1e-10f)
                {
                    float h = dt * 0.5f;
                    var dq = new Quaternion(b.AngularVelocity.X * h, b.AngularVelocity.Y * h, b.AngularVelocity.Z * h, 0f);
                    b.Orientation = Quaternion.Normalize(dq * b.Orientation);
                }
            }
        }

        // === 碰撞检测 ===

        private void DetectCollisions()
        {
            int n = _bodies.Count;
            for (int i = 0; i < n; i++)
                for (int j = i + 1; j < n; j++)
                {
                    var a = _bodies[i]; var b = _bodies[j];
                    if (a.IsStatic && b.IsStatic) continue;
                    if (!AABBOverlap(a, b)) continue;
                    var contact = GenerateContact(a, b);
                    if (!contact.HasValue) continue;
                    if (a.IsTrigger || b.IsTrigger) _curTrig.Add((i, j));
                    else { _contacts.Add(contact.Value); _curPairs.Add((i, j)); }
                }
        }

        private bool AABBOverlap(EnhancedPhysicsBody a, EnhancedPhysicsBody b)
        {
            (Vector3 minA, Vector3 maxA) = ComputeAABB(a);
            (Vector3 minB, Vector3 maxB) = ComputeAABB(b);
            return minA.X < maxB.X && maxA.X > minB.X && minA.Y < maxB.Y && maxA.Y > minB.Y && minA.Z < maxB.Z && maxA.Z > minB.Z;
        }

        private (Vector3 min, Vector3 max) ComputeAABB(EnhancedPhysicsBody b)
        {
            if (b.Shape == EnhancedColliderShape.Sphere) return (b.Position - Vector3.One * b.Radius, b.Position + Vector3.One * b.Radius);
            if (b.Shape == EnhancedColliderShape.Box) return (b.Position - b.HalfExtents, b.Position + b.HalfExtents);
            float r = b.Radius, hh = b.Height * 0.5f;
            return (b.Position - new Vector3(r, hh + r, r), b.Position + new Vector3(r, hh + r, r));
        }

        private ContactConstraintEnhanced? GenerateContact(EnhancedPhysicsBody a, EnhancedPhysicsBody b)
        {
            if (a.Shape == EnhancedColliderShape.Sphere && b.Shape == EnhancedColliderShape.Sphere) return GenSphereSphere(a, b);
            if (a.Shape == EnhancedColliderShape.Sphere && b.Shape == EnhancedColliderShape.Box) return GenSphereBox(a, b);
            if (a.Shape == EnhancedColliderShape.Box && b.Shape == EnhancedColliderShape.Sphere)
            {
                var c = GenSphereBox(b, a);
                if (c.HasValue)
                {
                    var cc = c.Value;
                    cc.Normal = -cc.Normal;
                    cc.BodyA = a.Id;
                    cc.BodyB = b.Id;
                    c = cc;
                }
                return c;
            }
            if (a.Shape == EnhancedColliderShape.Box && b.Shape == EnhancedColliderShape.Box) return GenBoxBox(a, b);
            return null;
        }

        private ContactConstraintEnhanced? GenSphereSphere(EnhancedPhysicsBody a, EnhancedPhysicsBody b)
        {
            Vector3 d = b.Position - a.Position;
            float dist = d.Length(), sumR = a.Radius + b.Radius;
            if (dist >= sumR) return null;
            Vector3 n = dist > 1e-8f ? d / dist : Vector3.UnitY;
            var (fr, re) = CombineMaterials(a, b);
            return new ContactConstraintEnhanced { BodyA = a.Id, BodyB = b.Id, ContactPoint = a.Position + n * a.Radius, Normal = n, Penetration = sumR - dist, Restitution = re, Friction = fr };
        }

        private ContactConstraintEnhanced? GenSphereBox(EnhancedPhysicsBody s, EnhancedPhysicsBody bx)
        {
            Vector3 local = s.Position - bx.Position;
            Vector3 clamped = Vector3.Clamp(local, -bx.HalfExtents, bx.HalfExtents);
            Vector3 d = local - clamped;
            float dist = d.Length();
            if (dist >= s.Radius) return null;
            Vector3 n = dist > 1e-8f ? d / dist : Vector3.UnitY;
            var (fr, re) = CombineMaterials(s, bx);
            return new ContactConstraintEnhanced { BodyA = s.Id, BodyB = bx.Id, ContactPoint = bx.Position + clamped, Normal = n, Penetration = s.Radius - dist, Restitution = re, Friction = fr };
        }

        private ContactConstraintEnhanced? GenBoxBox(EnhancedPhysicsBody a, EnhancedPhysicsBody b)
        {
            Vector3 d = b.Position - a.Position;
            Vector3 ov = a.HalfExtents + b.HalfExtents - Vector3.Abs(d);
            if (ov.X <= 0 || ov.Y <= 0 || ov.Z <= 0) return null;
            Vector3 n; float pen;
            if (ov.X <= ov.Y && ov.X <= ov.Z) { n = new(d.X >= 0 ? 1 : -1, 0, 0); pen = ov.X; }
            else if (ov.Y <= ov.Z) { n = new(0, d.Y >= 0 ? 1 : -1, 0); pen = ov.Y; }
            else { n = new(0, 0, d.Z >= 0 ? 1 : -1); pen = ov.Z; }
            var (fr, re) = CombineMaterials(a, b);
            return new ContactConstraintEnhanced { BodyA = a.Id, BodyB = b.Id, ContactPoint = (a.Position + b.Position) * 0.5f, Normal = n, Penetration = pen, Restitution = re, Friction = fr };
        }

        private (float friction, float restitution) CombineMaterials(EnhancedPhysicsBody a, EnhancedPhysicsBody b)
            => (a.Material != null && b.Material != null) ? PhysicsMaterialEnhanced.Combine(a.Material, b.Material) : (0.4f, 0.3f);

        // === 顺序冲量求解 (Erin Catto Box2D) ===

        private void SolveConstraints()
        {
            // Warm starting
            for (int i = 0; i < _contacts.Count; i++)
            {
                ref var c = ref CollectionsMarshal.AsSpan(_contacts)[i];
                if (c.AccumulatedNormalImpulse == 0f) continue;
                var a = _bodyMap[c.BodyA]; var b = _bodyMap[c.BodyB];
                Vector3 imp = c.Normal * c.AccumulatedNormalImpulse;
                a.Velocity -= imp * a.InverseMass; b.Velocity += imp * b.InverseMass;
            }
            // 迭代求解
            for (int iter = 0; iter < SolverIterations; iter++)
            {
                for (int i = 0; i < _contacts.Count; i++)
                {
                    ref var c = ref CollectionsMarshal.AsSpan(_contacts)[i];
                    var a = _bodyMap[c.BodyA]; var b = _bodyMap[c.BodyB];
                    if (a.IsSleeping) a.WakeUp(); if (b.IsSleeping) b.WakeUp();

                    Vector3 rv = b.GetVelocityAtPoint(c.ContactPoint) - a.GetVelocityAtPoint(c.ContactPoint);
                    float vn = Vector3.Dot(rv, c.Normal);
                    float re = vn < -0.5f ? c.Restitution : 0f;
                    float invSum = a.InverseMass + b.InverseMass;
                    if (invSum < 1e-10f) continue;

                    // 法线冲量
                    float jn = -(1f + re) * vn / invSum;
                    float oldN = c.AccumulatedNormalImpulse;
                    c.AccumulatedNormalImpulse = MathF.Max(0f, oldN + jn);
                    jn = c.AccumulatedNormalImpulse - oldN;
                    Vector3 impN = c.Normal * jn;
                    a.Velocity -= impN * a.InverseMass; b.Velocity += impN * b.InverseMass;

                    // 切线冲量 (摩擦)
                    Vector3 t = rv - c.Normal * vn;
                    float tLen = t.Length();
                    if (tLen < 1e-6f) continue;
                    t /= tLen;
                    float jt = -Vector3.Dot(rv, t) / invSum;
                    float maxF = c.AccumulatedNormalImpulse * c.Friction;
                    float oldT = c.AccumulatedTangentImpulse;
                    c.AccumulatedTangentImpulse = Math.Clamp(oldT + jt, -maxF, maxF);
                    jt = c.AccumulatedTangentImpulse - oldT;
                    Vector3 impT = t * jt;
                    a.Velocity -= impT * a.InverseMass; b.Velocity += impT * b.InverseMass;
                }
            }
        }

        private void PositionCorrection()
        {
            foreach (var c in _contacts)
            {
                var a = _bodyMap[c.BodyA]; var b = _bodyMap[c.BodyB];
                float invSum = a.InverseMass + b.InverseMass;
                if (invSum < 1e-10f) continue;
                Vector3 corr = c.Normal * (MathF.Max(c.Penetration - Slop, 0f) * Baumgarte / invSum);
                a.Position -= corr * a.InverseMass; b.Position += corr * b.InverseMass;
            }
        }

        // === 连续碰撞检测 (CCD) ===

        private void PerformCCD(float dt)
        {
            foreach (var body in _bodies)
            {
                if (body.IsStatic || body.IsSleeping || body.Velocity.Length() < 0.5f) continue;
                foreach (var other in _bodies)
                {
                    if (other.Id == body.Id || other.IsTrigger) continue;
                    float? toi = body.Shape == EnhancedColliderShape.Sphere ? SweptSphereVsBody(body, other, dt)
                               : body.Shape == EnhancedColliderShape.Box ? SweptBoxVsBody(body, other, dt) : null;
                    if (toi.HasValue && toi.Value < dt && toi.Value > 0f)
                    {
                        float f = toi.Value / dt;
                        body.Position -= body.Velocity * dt * (1f - f);
                        body.Velocity *= 0.5f;
                        body.WakeUp();
                    }
                }
            }
        }

        private float? SweptSphereVsBody(EnhancedPhysicsBody s, EnhancedPhysicsBody o, float dt)
        {
            Vector3 move = s.Velocity * dt;
            float mLen = move.Length();
            if (mLen < 1e-8f) return null;
            Vector3 dir = move / mLen;
            if (o.Shape == EnhancedColliderShape.Sphere)
            {
                float sumR = s.Radius + o.Radius + CCDSkinWidth;
                Vector3 oc = s.Position - o.Position;
                float b = Vector3.Dot(oc, dir), c = oc.LengthSquared() - sumR * sumR;
                if (c > 0f && b > 0f) return null;
                float disc = b * b - c;
                if (disc < 0f) return null;
                float t = MathF.Max(0f, -b - MathF.Sqrt(disc));
                return t / mLen * dt;
            }
            if (o.Shape == EnhancedColliderShape.Box)
            {
                Vector3 min = o.Position - o.HalfExtents - Vector3.One * (s.Radius + CCDSkinWidth);
                Vector3 max = o.Position + o.HalfExtents + Vector3.One * (s.Radius + CCDSkinWidth);
                return RayAABBTOI(s.Position, dir, min, max, mLen, dt);
            }
            return null;
        }

        private float? SweptBoxVsBody(EnhancedPhysicsBody box, EnhancedPhysicsBody other, float dt)
        {
            // 保守推进 (Conservative Advancement)
            float t = 0f;
            for (int i = 0; i < 8; i++)
            {
                Vector3 pos = box.Position + box.Velocity * t * dt;
                (Vector3 minA, Vector3 maxA) = (pos - box.HalfExtents, pos + box.HalfExtents);
                (Vector3 minB, Vector3 maxB) = ComputeAABB(other);
                if (minA.X < maxB.X && maxA.X > minB.X && minA.Y < maxB.Y && maxA.Y > minB.Y && minA.Z < maxB.Z && maxA.Z > minB.Z)
                    return t * dt;
                t += 0.125f;
                if (t >= 1f) return null;
            }
            return null;
        }

        private float? RayAABBTOI(Vector3 o, Vector3 d, Vector3 min, Vector3 max, float maxDist, float dt)
        {
            float tmin = 0f, tmax = maxDist;
            Span<float> ov = stackalloc float[] { o.X, o.Y, o.Z };
            Span<float> dv = stackalloc float[] { d.X, d.Y, d.Z };
            Span<float> mn = stackalloc float[] { min.X, min.Y, min.Z };
            Span<float> mx = stackalloc float[] { max.X, max.Y, max.Z };
            for (int i = 0; i < 3; i++)
            {
                if (MathF.Abs(dv[i]) < 1e-8f) { if (ov[i] < mn[i] || ov[i] > mx[i]) return null; }
                else
                {
                    float t1 = (mn[i] - ov[i]) / dv[i], t2 = (mx[i] - ov[i]) / dv[i];
                    if (t1 > t2) (t1, t2) = (t2, t1);
                    tmin = MathF.Max(tmin, t1); tmax = MathF.Min(tmax, t2);
                    if (tmax < tmin) return null;
                }
            }
            return tmin / maxDist * dt;
        }

        // === 休眠系统 ===

        private void UpdateSleep(float dt)
        {
            foreach (var b in _bodies)
            {
                if (b.IsStatic) continue;
                float spd = b.Velocity.Length(), angSpd = b.AngularVelocity.Length(), acc = b.LastAcceleration.Length();
                if (spd < SleepVelocityThreshold && angSpd < SleepVelocityThreshold * 0.5f && acc < SleepAccelerationThreshold)
                {
                    b.SleepTimer += dt;
                    if (b.SleepTimer >= SleepTimeRequired) { b.IsSleeping = true; b.Velocity = Vector3.Zero; b.AngularVelocity = Vector3.Zero; }
                }
                else { b.SleepTimer = 0f; b.IsSleeping = false; }
            }
        }

        // === 碰撞回调 ===

        private void DispatchCallbacks()
        {
            foreach (var p in _curPairs)
            {
                if (!_prevPairs.Contains(p)) OnCollisionEnter?.Invoke(CreateEvent(p));
                else OnCollisionStay?.Invoke(CreateEvent(p));
            }
            foreach (var p in _prevPairs)
                if (!_curPairs.Contains(p)) OnCollisionExit?.Invoke(CreateEvent(p));
            _prevPairs.Clear(); foreach (var p in _curPairs) _prevPairs.Add(p);

            foreach (var p in _curTrig)
                if (!_prevTrig.Contains(p)) OnTriggerEnter?.Invoke(CreateEvent(p));
            foreach (var p in _prevTrig)
                if (!_curTrig.Contains(p)) OnTriggerExit?.Invoke(CreateEvent(p));
            _prevTrig.Clear(); foreach (var p in _curTrig) _prevTrig.Add(p);
        }

        private EnhancedCollisionEvent CreateEvent((int, int) p)
        {
            var a = _bodyMap[p.Item1]; var b = _bodyMap[p.Item2];
            return new EnhancedCollisionEvent { BodyA = a, BodyB = b, ContactPoint = (a.Position + b.Position) * 0.5f, Normal = Vector3.Normalize(b.Position - a.Position) };
        }

        // === 射线检测 ===

        public RayHitEnhanced? Raycast(Vector3 origin, Vector3 direction, float maxDist = float.MaxValue)
        {
            RayHitEnhanced? closest = null;
            Vector3 dir = Vector3.Normalize(direction);
            foreach (var b in _bodies)
            {
                var hit = b.Shape == EnhancedColliderShape.Box ? RaycastBox(origin, dir, maxDist, b) : RaycastSphere(origin, dir, maxDist, b);
                if (hit.HasValue && (!closest.HasValue || hit.Value.Distance < closest.Value.Distance)) closest = hit;
            }
            return closest;
        }

        private RayHitEnhanced? RaycastSphere(Vector3 o, Vector3 d, float maxD, EnhancedPhysicsBody b)
        {
            Vector3 oc = o - b.Position;
            float bb = Vector3.Dot(oc, d), c = oc.LengthSquared() - b.Radius * b.Radius;
            float disc = bb * bb - c;
            if (disc < 0f) return null;
            float t = -bb - MathF.Sqrt(disc);
            if (t < 0f) t = -bb + MathF.Sqrt(disc);
            if (t < 0f || t > maxD) return null;
            Vector3 p = o + d * t;
            return new RayHitEnhanced { Body = b, Point = p, Normal = Vector3.Normalize(p - b.Position), Distance = t, TimeOfImpact = t };
        }

        private RayHitEnhanced? RaycastBox(Vector3 o, Vector3 d, float maxD, EnhancedPhysicsBody b)
        {
            Vector3 min = b.Position - b.HalfExtents, max = b.Position + b.HalfExtents;
            float tmin = 0f, tmax = maxD; Vector3 normal = Vector3.Zero;
            Span<float> ov = stackalloc float[] { o.X, o.Y, o.Z };
            Span<float> dv = stackalloc float[] { d.X, d.Y, d.Z };
            Span<float> mn = stackalloc float[] { min.X, min.Y, min.Z };
            Span<float> mx = stackalloc float[] { max.X, max.Y, max.Z };
            for (int i = 0; i < 3; i++)
            {
                if (MathF.Abs(dv[i]) < 1e-8f) { if (ov[i] < mn[i] || ov[i] > mx[i]) return null; }
                else
                {
                    float t1 = (mn[i] - ov[i]) / dv[i], t2 = (mx[i] - ov[i]) / dv[i];
                    if (t1 > t2) (t1, t2) = (t2, t1);
                    if (t1 > tmin) { tmin = t1; normal = i == 0 ? new(dv[0] < 0 ? 1 : -1, 0, 0) : i == 1 ? new(0, dv[1] < 0 ? 1 : -1, 0) : new(0, 0, dv[2] < 0 ? 1 : -1); }
                    tmax = MathF.Min(tmax, t2);
                    if (tmax < tmin) return null;
                }
            }
            if (tmin > maxD) return null;
            return new RayHitEnhanced { Body = b, Point = o + d * tmin, Normal = normal, Distance = tmin, TimeOfImpact = tmin };
        }

        public RayHitEnhanced? SphereCast(Vector3 origin, float radius, Vector3 direction, float maxDist)
        {
            RayHitEnhanced? closest = null;
            Vector3 dir = Vector3.Normalize(direction);
            foreach (var b in _bodies)
            {
                if (b.Shape != EnhancedColliderShape.Sphere) continue;
                float sumR = radius + b.Radius;
                Vector3 oc = origin - b.Position;
                float bb = Vector3.Dot(oc, dir), c = oc.LengthSquared() - sumR * sumR;
                if (c > 0f && bb > 0f) continue;
                float disc = bb * bb - c;
                if (disc < 0f) continue;
                float t = MathF.Max(0f, -bb - MathF.Sqrt(disc));
                if (t > maxDist) continue;
                if (!closest.HasValue || t < closest.Value.Distance)
                    closest = new RayHitEnhanced { Body = b, Point = origin + dir * t, Normal = Vector3.Normalize(origin + dir * t - b.Position), Distance = t, TimeOfImpact = t };
            }
            return closest;
        }

        // === 调试视图 (线框渲染) ===

        public List<DebugLine> GenerateDebugLines()
        {
            _debugLines.Clear();
            Vector3 col = new(0f, 1f, 0f);
            foreach (var b in _bodies)
            {
                if (b.Shape == EnhancedColliderShape.Sphere) DrawSphereWF(b.Position, b.Radius, col);
                else if (b.Shape == EnhancedColliderShape.Box) DrawBoxWF(b.Position, b.HalfExtents, col);
                else if (b.Shape == EnhancedColliderShape.Capsule) DrawCapsuleWF(b.Position, b.Radius, b.Height, col);
            }
            return _debugLines;
        }

        private void DrawSphereWF(Vector3 c, float r, Vector3 col)
        {
            const int seg = 16;
            for (int ax = 0; ax < 3; ax++)
                for (int i = 0; i < seg; i++)
                {
                    float a0 = i * 2f * MathF.PI / seg, a1 = (i + 1) * 2f * MathF.PI / seg;
                    Vector3 p0 = ax == 0 ? new(0, MathF.Cos(a0) * r, MathF.Sin(a0) * r) : ax == 1 ? new(MathF.Cos(a0) * r, 0, MathF.Sin(a0) * r) : new(MathF.Cos(a0) * r, MathF.Sin(a0) * r, 0);
                    Vector3 p1 = ax == 0 ? new(0, MathF.Cos(a1) * r, MathF.Sin(a1) * r) : ax == 1 ? new(MathF.Cos(a1) * r, 0, MathF.Sin(a1) * r) : new(MathF.Cos(a1) * r, MathF.Sin(a1) * r, 0);
                    _debugLines.Add(new DebugLine { Start = c + p0, End = c + p1, Color = col });
                }
        }

        private void DrawBoxWF(Vector3 c, Vector3 he, Vector3 col)
        {
            Vector3[] v = new Vector3[8];
            int idx = 0;
            for (int x = -1; x <= 1; x += 2) for (int y = -1; y <= 1; y += 2) for (int z = -1; z <= 1; z += 2) v[idx++] = c + new Vector3(x * he.X, y * he.Y, z * he.Z);
            int[,] e = { {0,1},{0,2},{0,4},{1,3},{1,5},{2,3},{2,6},{3,7},{4,5},{4,6},{5,7},{6,7} };
            for (int i = 0; i < 12; i++) _debugLines.Add(new DebugLine { Start = v[e[i,0]], End = v[e[i,1]], Color = col });
        }

        private void DrawCapsuleWF(Vector3 c, float r, float h, Vector3 col)
        {
            float hh = h * 0.5f;
            DrawSphereWF(c + Vector3.UnitY * hh, r, col);
            DrawSphereWF(c - Vector3.UnitY * hh, r, col);
            _debugLines.Add(new DebugLine { Start = c + new Vector3(r, hh, 0), End = c + new Vector3(r, -hh, 0), Color = col });
            _debugLines.Add(new DebugLine { Start = c + new Vector3(-r, hh, 0), End = c + new Vector3(-r, -hh, 0), Color = col });
            _debugLines.Add(new DebugLine { Start = c + new Vector3(0, hh, r), End = c + new Vector3(0, -hh, r), Color = col });
            _debugLines.Add(new DebugLine { Start = c + new Vector3(0, hh, -r), End = c + new Vector3(0, -hh, -r), Color = col });
        }
    }
}
