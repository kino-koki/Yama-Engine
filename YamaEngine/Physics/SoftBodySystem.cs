using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace YamaEngine.Physics
{
    // ============================================================================================
    //  SoftBodySystem.cs
    //
    //  软体系统。用质点-弹簧模型(Mass-Spring Model)实现可变形物体。
    //  核心思想：把物体离散成一组质点，质点之间用弹簧连接，弹簧有刚度和阻尼。
    //  对每个质点用牛顿第二定律积分，整体就能模拟软体的形变。
    //
    //  弹簧类型：
    //    - 结构弹簧(Structural)：相邻质点之间，抵抗拉伸
    //    - 剪切弹簧(Shear)：对角线连接，抵抗剪切
    //    - 弯曲弹簧(Bend)：跨一个质点连接，抵抗弯曲
    //
    //  求解方法：Verlet积分(隐式欧拉也行，但Verlet对弹簧更稳)
    //
    //  注意：质量不能为0，弹簧刚度太大显式积分会爆掉，需要小步长或隐式积分
    //  TODO: 体积守恒约束还没做，软体被压扁了不会自动恢复体积
    // ============================================================================================

    /// <summary>软体质点。</summary>
    public struct SoftParticle
    {
        public Vector3 Position;
        public Vector3 OldPosition;       // Verlet用
        public Vector3 Velocity;
        public Vector3 ForceAccumulator;
        public float Mass;
        public float InvMass;
        public float Radius;              // 用于碰撞
        public bool Pinned;               // 钉住不动
        public Vector3 PinnedPosition;
    }

    /// <summary>弹簧。</summary>
    public struct SoftSpring
    {
        public int ParticleA;
        public int ParticleB;
        public float RestLength;
        public float Stiffness;           // 刚度k
        public float Damping;             // 阻尼c
        public SpringType Type;

        public SoftSpring(int a, int b, float restLength, float stiffness, float damping, SpringType type)
        {
            ParticleA = a;
            ParticleB = b;
            RestLength = restLength;
            Stiffness = stiffness;
            Damping = damping;
            Type = type;
        }
    }

    /// <summary>弹簧类型。</summary>
    public enum SpringType
    {
        Structural = 0,   // 结构
        Shear = 1,        // 剪切
        Bend = 2,         // 弯曲
        Volume = 3        // 体积(用于3D)
    }

    /// <summary>
    /// 软体系统主类。
    /// </summary>
    public sealed class SoftBodySystem
    {
        private readonly List<SoftParticle> _particles = new(256);
        private readonly List<SoftSpring> _springs = new(512);

        // 体积约束(3D软体用)
        private readonly List<VolumeConstraint> _volumeConstraints = new(64);

        private Vector3 _gravity = new(0, -9.81f, 0);
        private float _globalDamping = 0.01f;
        private float _groundY = float.MinValue;
        private int _subSteps = 4;        // 子步数，弹簧软体一般要多步才稳

        public int ParticleCount => _particles.Count;
        public int SpringCount => _springs.Count;

        /// <summary>体积约束(4个质点形成的四面体要保持体积)。</summary>
        private struct VolumeConstraint
        {
            public int P0, P1, P2, P3;
            public float RestVolume;
            public float Stiffness;
        }

        public void SetGravity(Vector3 g) => _gravity = g;
        public void SetGlobalDamping(float d) => _globalDamping = d;
        public void SetGround(float y) => _groundY = y;
        public void SetSubSteps(int n) => _subSteps = Math.Clamp(n, 1, 32);

        // =========================================================================
        //  构建
        // =========================================================================

        public int AddParticle(Vector3 position, float mass, float radius = 0.05f, bool pinned = false)
        {
            if (mass <= 0f) mass = 0.001f;
            _particles.Add(new SoftParticle
            {
                Position = position,
                OldPosition = position,
                Velocity = Vector3.Zero,
                ForceAccumulator = Vector3.Zero,
                Mass = mass,
                InvMass = pinned ? 0f : 1f / mass,
                Radius = radius,
                Pinned = pinned,
                PinnedPosition = position
            });
            return _particles.Count - 1;
        }

        public void AddSpring(int a, int b, float stiffness, float damping, SpringType type = SpringType.Structural)
        {
            if (a < 0 || b < 0 || a >= _particles.Count || b >= _particles.Count || a == b) return;
            float restLength = Vector3.Distance(_particles[a].Position, _particles[b].Position);
            _springs.Add(new SoftSpring(a, b, restLength, stiffness, damping, type));
        }

        public void AddSpringWithRestLength(int a, int b, float restLength,
            float stiffness, float damping, SpringType type = SpringType.Structural)
        {
            if (a < 0 || b < 0 || a >= _particles.Count || b >= _particles.Count || a == b) return;
            _springs.Add(new SoftSpring(a, b, restLength, stiffness, damping, type));
        }

        /// <summary>添加体积约束(四面体)。</summary>
        public void AddVolumeConstraint(int p0, int p1, int p2, int p3, float stiffness = 1f)
        {
            float vol = ComputeTetrahedronVolume(
                _particles[p0].Position, _particles[p1].Position,
                _particles[p2].Position, _particles[p3].Position);
            _volumeConstraints.Add(new VolumeConstraint
            {
                P0 = p0, P1 = p1, P2 = p2, P3 = p3,
                RestVolume = vol,
                Stiffness = stiffness
            });
        }

        // =========================================================================
        //  预设软体
        // =========================================================================

        /// <summary>创建一个立方体软体。3D网格，6面都有结构/剪切/弯曲弹簧。</summary>
        public void CreateBoxSoftBody(Vector3 center, Vector3 size, int resolution,
            float mass, float stiffness, float damping)
        {
            int startX = _particles.Count;
            Vector3 step = size / Math.Max(1, resolution - 1);
            Vector3 origin = center - size * 0.5f;

            // 创建质点网格
            for (int x = 0; x < resolution; x++)
                for (int y = 0; y < resolution; y++)
                    for (int z = 0; z < resolution; z++)
                    {
                        var pos = origin + new Vector3(x * step.X, y * step.Y, z * step.Z);
                        float perMass = mass / (resolution * resolution * resolution);
                        AddParticle(pos, perMass, step.Length() * 0.3f);
                    }

            // 添加弹簧
            for (int x = 0; x < resolution; x++)
                for (int y = 0; y < resolution; y++)
                    for (int z = 0; z < resolution; z++)
                    {
                        int idx = (x * resolution + y) * resolution + z + startX;
                        // 结构弹簧：6方向邻居
                        if (x < resolution - 1) AddSpring(idx, idx + resolution * resolution, stiffness, damping, SpringType.Structural);
                        if (y < resolution - 1) AddSpring(idx, idx + resolution, stiffness, damping, SpringType.Structural);
                        if (z < resolution - 1) AddSpring(idx, idx + 1, stiffness, damping, SpringType.Structural);

                        // 剪切弹簧：面对角线
                        if (x < resolution - 1 && y < resolution - 1)
                        {
                            AddSpring(idx, idx + resolution * resolution + resolution, stiffness * 0.5f, damping, SpringType.Shear);
                            AddSpring(idx + resolution * resolution, idx + resolution, stiffness * 0.5f, damping, SpringType.Shear);
                        }
                        if (x < resolution - 1 && z < resolution - 1)
                        {
                            AddSpring(idx, idx + resolution * resolution + 1, stiffness * 0.5f, damping, SpringType.Shear);
                            AddSpring(idx + resolution * resolution, idx + 1, stiffness * 0.5f, damping, SpringType.Shear);
                        }
                        if (y < resolution - 1 && z < resolution - 1)
                        {
                            AddSpring(idx, idx + resolution + 1, stiffness * 0.5f, damping, SpringType.Shear);
                            AddSpring(idx + resolution, idx + 1, stiffness * 0.5f, damping, SpringType.Shear);
                        }

                        // 弯曲弹簧：跨1个邻居
                        if (x < resolution - 2) AddSpring(idx, idx + 2 * resolution * resolution, stiffness * 0.2f, damping, SpringType.Bend);
                        if (y < resolution - 2) AddSpring(idx, idx + 2 * resolution, stiffness * 0.2f, damping, SpringType.Bend);
                        if (z < resolution - 2) AddSpring(idx, idx + 2, stiffness * 0.2f, damping, SpringType.Bend);
                    }

            // 添加体积约束(每个小立方体的四面体)
            for (int x = 0; x < resolution - 1; x++)
                for (int y = 0; y < resolution - 1; y++)
                    for (int z = 0; z < resolution - 1; z++)
                    {
                        int i000 = ((x * resolution + y) * resolution + z) + startX;
                        int i100 = (((x + 1) * resolution + y) * resolution + z) + startX;
                        int i010 = ((x * resolution + y + 1) * resolution + z) + startX;
                        int i001 = ((x * resolution + y) * resolution + z + 1) + startX;
                        AddVolumeConstraint(i000, i100, i010, i001, stiffness * 0.5f);
                    }
        }

        /// <summary>创建一个球形软体。</summary>
        public void CreateSphereSoftBody(Vector3 center, float radius, int resolution,
            float mass, float stiffness, float damping)
        {
            // 用Fibonacci球面分布
            int startX = _particles.Count;
            float perMass = mass / resolution;

            for (int i = 0; i < resolution; i++)
            {
                float t = (float)i / resolution;
                float phi = MathF.Acos(1f - 2f * t);
                float theta = MathF.PI * (1f + MathF.Sqrt(5f)) * i;
                var pos = center + new Vector3(
                    radius * MathF.Sin(phi) * MathF.Cos(theta),
                    radius * MathF.Cos(phi),
                    radius * MathF.Sin(phi) * MathF.Sin(theta));
                AddParticle(pos, perMass, radius * 0.1f);
            }

            // 中心质点(用于维持体积)
            int centerIdx = AddParticle(center, mass * 0.5f, radius * 0.1f);

            // 每个表面质点连接到中心
            for (int i = startX; i < _particles.Count - 1; i++)
            {
                AddSpring(i, centerIdx, stiffness, damping, SpringType.Structural);
            }

            // 表面质点之间连接最近的几个
            for (int i = startX; i < _particles.Count - 1; i++)
            {
                var pi = _particles[i].Position;
                // 找最近的4个邻居
                var neighbors = new List<(int idx, float dist)>();
                for (int j = startX; j < _particles.Count - 1; j++)
                {
                    if (i == j) continue;
                    float d = Vector3.Distance(pi, _particles[j].Position);
                    neighbors.Add((j, d));
                }
                neighbors.Sort((a, b) => a.dist.CompareTo(b.dist));
                for (int k = 0; k < Math.Min(4, neighbors.Count); k++)
                {
                    if (neighbors[k].idx > i)  // 避免重复添加
                        AddSpring(i, neighbors[k].idx, stiffness * 0.5f, damping, SpringType.Structural);
                }
            }
        }

        // =========================================================================
        //  模拟
        // =========================================================================

        public void Step(float dt)
        {
            if (_particles.Count == 0) return;
            float subDt = dt / _subSteps;
            for (int i = 0; i < _subSteps; i++)
            {
                StepOnce(subDt);
            }
        }

        private void StepOnce(float dt)
        {
            var span = CollectionsMarshal.AsSpan(_particles);

            // 1) 清力 + 重力
            for (int i = 0; i < span.Length; i++)
            {
                span[i].ForceAccumulator = _gravity * span[i].Mass;
            }

            // 2) 弹簧力
            ApplySpringForces(span);

            // 3) 体积约束力
            ApplyVolumeForces(span);

            // 4) 外部力(用户施加的)
            ApplyExternalForces(span);

            // 5) 积分(Verlet)
            IntegrateVerlet(span, dt);

            // 6) 碰撞
            HandleCollisions(span);

            // 7) 钉住的质点恢复位置
            for (int i = 0; i < span.Length; i++)
            {
                if (span[i].Pinned)
                {
                    span[i].Position = span[i].PinnedPosition;
                    span[i].OldPosition = span[i].PinnedPosition;
                    span[i].Velocity = Vector3.Zero;
                }
            }
        }

        private void ApplySpringForces(Span<SoftParticle> particles)
        {
            var springs = CollectionsMarshal.AsSpan(_springs);
            for (int i = 0; i < springs.Length; i++)
            {
                ref var s = ref springs[i];
                ref var a = ref particles[s.ParticleA];
                ref var b = ref particles[s.ParticleB];

                Vector3 delta = b.Position - a.Position;
                float dist = delta.Length();
                if (dist < 1e-6f) continue;

                Vector3 dir = delta / dist;
                float stretch = dist - s.RestLength;

                // 弹簧力 F = -k * x
                Vector3 force = s.Stiffness * stretch * dir;

                // 阻尼: 相对速度沿弹簧方向
                Vector3 relVel = b.Velocity - a.Velocity;
                float dampForce = Vector3.Dot(relVel, dir) * s.Damping;
                force += dampForce * dir;

                // 施加
                if (!a.Pinned)
                {
                    a.ForceAccumulator += force;
                }
                if (!b.Pinned)
                {
                    b.ForceAccumulator -= force;
                }
            }
        }

        private void ApplyVolumeForces(Span<SoftParticle> particles)
        {
            for (int i = 0; i < _volumeConstraints.Count; i++)
            {
                ref var vc = ref _volumeConstraints.AsSpan()[i];
                ref var p0 = ref particles[vc.P0];
                ref var p1 = ref particles[vc.P1];
                ref var p2 = ref particles[vc.P2];
                ref var p3 = ref particles[vc.P3];

                float currentVol = ComputeTetrahedronVolume(p0.Position, p1.Position, p2.Position, p3.Position);
                float diff = currentVol - vc.RestVolume;
                if (MathF.Abs(diff) < 1e-8f) continue;

                // 算梯度(每个顶点对体积的偏导)
                Vector3 grad0 = (p1.Position - p0.Position) / 6f;
                Vector3 grad1 = (p2.Position - p1.Position) / 6f;
                Vector3 grad2 = (p3.Position - p2.Position) / 6f;
                Vector3 grad3 = (p0.Position - p3.Position) / 6f;

                float scale = -vc.Stiffness * diff;
                Vector3 f0 = grad0 * scale;
                Vector3 f1 = grad1 * scale;
                Vector3 f2 = grad2 * scale;
                Vector3 f3 = grad3 * scale;

                if (!p0.Pinned) p0.ForceAccumulator += f0;
                if (!p1.Pinned) p1.ForceAccumulator += f1;
                if (!p2.Pinned) p2.ForceAccumulator += f2;
                if (!p3.Pinned) p3.ForceAccumulator += f3;
            }
        }

        private void ApplyExternalForces(Span<SoftParticle> particles)
        {
            // 用户通过ApplyForceToParticle施加的力已经在ForceAccumulator里了
            // 这里可以加风之类的外部场
            // 暂时空着
        }

        private void IntegrateVerlet(Span<SoftParticle> particles, float dt)
        {
            for (int i = 0; i < particles.Length; i++)
            {
                ref var p = ref particles[i];
                if (p.Pinned || p.InvMass <= 0f) continue;

                // Verlet: x_new = 2*x - x_old + a*dt^2
                Vector3 temp = p.Position;
                Vector3 acceleration = p.ForceAccumulator * p.InvMass;
                p.Position = 2f * p.Position - p.OldPosition + acceleration * dt * dt;

                // 全局阻尼(速度衰减)
                p.Position = temp + (p.Position - temp) * (1f - _globalDamping * dt);

                p.OldPosition = temp;

                // 更新速度(用于阻尼计算和外部读取)
                p.Velocity = (p.Position - p.OldPosition) / dt;

                p.ForceAccumulator = Vector3.Zero;
            }
        }

        private void HandleCollisions(Span<SoftParticle> particles)
        {
            // 地面碰撞
            if (_groundY > float.MinValue)
            {
                for (int i = 0; i < particles.Length; i++)
                {
                    ref var p = ref particles[i];
                    if (p.Pinned) continue;
                    if (p.Position.Y - p.Radius < _groundY)
                    {
                        p.Position.Y = _groundY + p.Radius;
                        // 摩擦
                        p.OldPosition.X = p.Position.X + (p.OldPosition.X - p.Position.X) * 0.7f;
                        p.OldPosition.Z = p.Position.Z + (p.OldPosition.Z - p.Position.Z) * 0.7f;
                    }
                }
            }

            // 质点间碰撞(简化：O(n^2)，大量质点时性能不行)
            // TODO: 用空间哈希加速
            if (particles.Length < 128)
            {
                for (int i = 0; i < particles.Length; i++)
                {
                    for (int j = i + 1; j < particles.Length; j++)
                    {
                        ref var a = ref particles[i];
                        ref var b = ref particles[j];
                        Vector3 d = b.Position - a.Position;
                        float dist = d.Length();
                        float minDist = a.Radius + b.Radius;
                        if (dist < minDist && dist > 1e-6f)
                        {
                            Vector3 dir = d / dist;
                            float overlap = minDist - dist;
                            float totalInvMass = a.InvMass + b.InvMass;
                            if (totalInvMass < 1e-8f) continue;
                            if (!a.Pinned)
                                a.Position -= dir * overlap * (a.InvMass / totalInvMass);
                            if (!b.Pinned)
                                b.Position += dir * overlap * (b.InvMass / totalInvMass);
                        }
                    }
                }
            }
        }

        // =========================================================================
        //  外部API
        // =========================================================================

        public Vector3 GetParticlePosition(int idx)
        {
            if (idx < 0 || idx >= _particles.Count) return Vector3.Zero;
            return _particles[idx].Position;
        }

        public void SetParticlePosition(int idx, Vector3 pos)
        {
            if (idx < 0 || idx >= _particles.Count) return;
            var p = _particles[idx];
            p.Position = pos;
            p.OldPosition = pos;
            _particles[idx] = p;
        }

        public void PinParticle(int idx)
        {
            if (idx < 0 || idx >= _particles.Count) return;
            var p = _particles[idx];
            p.Pinned = true;
            p.PinnedPosition = p.Position;
            p.InvMass = 0f;
            _particles[idx] = p;
        }

        public void UnpinParticle(int idx)
        {
            if (idx < 0 || idx >= _particles.Count) return;
            var p = _particles[idx];
            p.Pinned = false;
            p.InvMass = p.Mass > 0f ? 1f / p.Mass : 0f;
            _particles[idx] = p;
        }

        public void ApplyForceToParticle(int idx, Vector3 force)
        {
            if (idx < 0 || idx >= _particles.Count) return;
            var p = _particles[idx];
            p.ForceAccumulator += force;
            _particles[idx] = p;
        }

        /// <summary>施加一个冲量(瞬时改变速度)。</summary>
        public void ApplyImpulseToParticle(int idx, Vector3 impulse)
        {
            if (idx < 0 || idx >= _particles.Count) return;
            var p = _particles[idx];
            if (p.Pinned) return;
            p.Velocity += impulse * p.InvMass;
            // Verlet: 也要改OldPosition
            p.OldPosition = p.Position - p.Velocity * 0.016f;
            _particles[idx] = p;
        }

        /// <summary>获取所有质点位置(给渲染用)。</summary>
        public IEnumerable<Vector3> GetPositions()
        {
            foreach (var p in _particles)
                yield return p.Position;
        }

        /// <summary>获取所有弹簧(给渲染用，画线)。</summary>
        public IEnumerable<(Vector3 a, Vector3 b, SpringType type)> GetSprings()
        {
            foreach (var s in _springs)
            {
                yield return (_particles[s.ParticleA].Position, _particles[s.ParticleB].Position, s.Type);
            }
        }

        /// <summary>清空。</summary>
        public void Clear()
        {
            _particles.Clear();
            _springs.Clear();
            _volumeConstraints.Clear();
        }

        // =========================================================================
        //  辅助
        // =========================================================================

        private static float ComputeTetrahedronVolume(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3)
        {
            // V = |((p1-p0) x (p2-p0)) · (p3-p0)| / 6
            Vector3 a = p1 - p0;
            Vector3 b = p2 - p0;
            Vector3 c = p3 - p0;
            return MathF.Abs(Vector3.Dot(Vector3.Cross(a, b), c)) / 6f;
        }
    }

    /// <summary>
    /// 软体和外部刚体的碰撞处理。
    /// </summary>
    public sealed class SoftBodyRigidbodyCollider
    {
        private readonly SoftBodySystem _softBody;

        public SoftBodyRigidbodyCollider(SoftBodySystem softBody)
        {
            _softBody = softBody;
        }

        /// <summary>处理软体质点和球体的碰撞。</summary>
        public void CollideWithSphere(Vector3 sphereCenter, float sphereRadius, float friction = 0.3f)
        {
            for (int i = 0; i < _softBody.ParticleCount; i++)
            {
                var pos = _softBody.GetParticlePosition(i);
                Vector3 d = pos - sphereCenter;
                float dist = d.Length();
                float minDist = sphereRadius + 0.05f; // 加质点半径
                if (dist < minDist && dist > 1e-6f)
                {
                    Vector3 normal = d / dist;
                    Vector3 newPos = sphereCenter + normal * minDist;
                    _softBody.SetParticlePosition(i, newPos);
                }
            }
        }

        /// <summary>处理软体质点和无限平面的碰撞。</summary>
        public void CollideWithPlane(Vector3 planePoint, Vector3 planeNormal, float friction = 0.3f)
        {
            planeNormal = Vector3.Normalize(planeNormal);
            for (int i = 0; i < _softBody.ParticleCount; i++)
            {
                var pos = _softBody.GetParticlePosition(i);
                float dist = Vector3.Dot(pos - planePoint, planeNormal);
                if (dist < 0.05f)
                {
                    Vector3 newPos = pos - planeNormal * dist + planeNormal * 0.05f;
                    _softBody.SetParticlePosition(i, newPos);
                }
            }
        }

        /// <summary>处理软体质点和AABB的碰撞。</summary>
        public void CollideWithBox(Vector3 boxCenter, Vector3 boxHalfExtents, Quaternion boxRotation)
        {
            var invRot = Quaternion.Inverse(boxRotation);
            for (int i = 0; i < _softBody.ParticleCount; i++)
            {
                var pos = _softBody.GetParticlePosition(i);
                // 转到box局部系
                Vector3 local = Vector3.Transform(pos - boxCenter, invRot);
                // 找box上最近的点
                Vector3 closest = new(
                    Math.Clamp(local.X, -boxHalfExtents.X, boxHalfExtents.X),
                    Math.Clamp(local.Y, -boxHalfExtents.Y, boxHalfExtents.Y),
                    Math.Clamp(local.Z, -boxHalfExtents.Z, boxHalfExtents.Z));
                Vector3 delta = local - closest;
                float dist = delta.Length();
                if (dist < 0.05f)
                {
                    Vector3 normal = dist > 1e-6f ? delta / dist : new Vector3(0, 1, 0);
                    Vector3 newLocal = closest + normal * 0.05f;
                    Vector3 newWorld = Vector3.Transform(newLocal, boxRotation) + boxCenter;
                    _softBody.SetParticlePosition(i, newWorld);
                }
            }
        }
    }
}
