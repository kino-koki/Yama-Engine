// ============================================================================================
//  SoftBodyPhysicsSystem.cs - 山雨引擎柔体物理系统 (质点-弹簧模型)
//  弹簧: 结构(保持形状)/剪切(防剪切)/弯曲(防弯折), F = -k*x - c*v (胡克+阻尼)
//  积分: Semi-Implicit Euler | 自碰撞: 空间哈希 | 刚体交互: 双向碰撞响应
//  压力模型: P = nRT/V (气球/球类) | 命名空间: ShanyuEngine.Physics | net8.0
// ============================================================================================

using System;
using System.Collections.Generic;
using System.Numerics;

namespace ShanyuEngine.Physics
{
    /// <summary>柔体质点。</summary>
    public struct SoftBodyParticle
    {
        public Vector3 Position;
        public Vector3 Velocity;
        public Vector3 ForceAccumulator;
        public float Mass;
        public float InverseMass;
        public float Radius;
        public bool Pinned;

        public SoftBodyParticle(Vector3 position, float mass, float radius = 0.05f)
        {
            Position = position;
            Velocity = Vector3.Zero;
            ForceAccumulator = Vector3.Zero;
            Mass = MathF.Max(mass, 1e-6f);
            InverseMass = 1f / Mass;
            Radius = radius;
            Pinned = false;
        }
    }

    /// <summary>弹簧类型。</summary>
    public enum SoftBodySpringType
    {
        Structural,
        Shear,
        Bend
    }

    /// <summary>弹簧连接。</summary>
    public struct SoftBodySpring
    {
        public int ParticleA;
        public int ParticleB;
        public float RestLength;
        public float Stiffness;
        public float Damping;
        public SoftBodySpringType Type;

        public SoftBodySpring(int a, int b, float restLength, float stiffness, float damping, SoftBodySpringType type)
        {
            ParticleA = a;
            ParticleB = b;
            RestLength = MathF.Max(restLength, 1e-6f);
            Stiffness = stiffness;
            Damping = damping;
            Type = type;
        }
    }

    /// <summary>柔体三角形面 (用于压力模型和渲染)。</summary>
    public struct SoftBodyTriangle
    {
        public int V0, V1, V2;
    }

    /// <summary>刚体碰撞代理 (轻量接口, 避免直接依赖Real.Rigidbody)。</summary>
    public struct RigidColliderProxy
    {
        public Vector3 Position;
        public Quaternion Rotation;
        public Vector3 HalfExtents;     // 盒半尺寸 (Sphere时X=半径)
        public bool IsSphere;
        public float InverseMass;       // 0=静态
        public Vector3 Velocity;
        public Vector3 AngularVelocity;

        public static RigidColliderProxy Sphere(Vector3 center, float radius, float inverseMass = 0f)
            => new() { Position = center, Rotation = Quaternion.Identity,
                       HalfExtents = new Vector3(radius), IsSphere = true, InverseMass = inverseMass };

        public static RigidColliderProxy Box(Vector3 center, Quaternion rotation, Vector3 halfExtents, float inverseMass = 0f)
            => new() { Position = center, Rotation = rotation, HalfExtents = halfExtents, IsSphere = false, InverseMass = inverseMass };
    }

    /// <summary>
    /// 柔体物理系统 —— 管理一个柔体的质点、弹簧、面, 执行模拟循环。
    /// </summary>
    public sealed class SoftBodyPhysicsSystem
    {
        private SoftBodyParticle[] _particles;
        private readonly List<SoftBodySpring> _springs = new(512);
        private readonly List<SoftBodyTriangle> _triangles = new(256);
        private readonly List<RigidColliderProxy> _colliders = new(32);

        // 空间哈希 (自碰撞)
        private Dictionary<long, List<int>> _hashGrid = new(256);
        private float _hashCellSize = 0.2f;

        // 物理参数
        public Vector3 Gravity { get; set; } = new(0f, -9.81f, 0f);
        public float GlobalDamping { get; set; } = 0.02f;
        public float SelfCollisionStiffness { get; set; } = 0.5f;
        public float RigidCollisionStiffness { get; set; } = 0.8f;
        public float RigidCollisionFriction { get; set; } = 0.3f;

        // 压力模型参数
        public bool EnablePressure { get; set; } = false;
        public float PressureConstant { get; set; } = 1.0f;       // nRT
        public float PressureRestVolume { get; set; } = 1.0f;      // 静止体积

        public int ParticleCount => _particles.Length;
        public int SpringCount => _springs.Count;
        public int TriangleCount => _triangles.Count;

        public SoftBodyPhysicsSystem(int maxParticles = 256)
        {
            _particles = new SoftBodyParticle[maxParticles];
        }

        /// <summary>设置质点数组。</summary>
        public void SetParticles(SoftBodyParticle[] particles)
        {
            _particles = particles ?? throw new ArgumentNullException(nameof(particles));
        }

        /// <summary>获取质点数组 (可读可写)。</summary>
        public SoftBodyParticle[] Particles => _particles;

        /// <summary>添加弹簧。</summary>
        public void AddSpring(int a, int b, float stiffness, float damping, SoftBodySpringType type)
        {
            if (a < 0 || b < 0 || a >= _particles.Length || b >= _particles.Length) return;
            float restLen = Vector3.Distance(_particles[a].Position, _particles[b].Position);
            _springs.Add(new SoftBodySpring(a, b, restLen, stiffness, damping, type));
        }

        /// <summary>添加三角形面。</summary>
        public void AddTriangle(int v0, int v1, int v2)
        {
            _triangles.Add(new SoftBodyTriangle { V0 = v0, V1 = v1, V2 = v2 });
        }

        /// <summary>添加刚体碰撞代理。</summary>
        public void AddRigidCollider(RigidColliderProxy collider) => _colliders.Add(collider);

        /// <summary>清空刚体碰撞代理。</summary>
        public void ClearRigidColliders() => _colliders.Clear();

        /// <summary>钉住质点。</summary>
        public void PinParticle(int index)
        {
            if (index >= 0 && index < _particles.Length)
            {
                _particles[index].Pinned = true;
                _particles[index].InverseMass = 0f;
            }
        }

        /// <summary>
        /// 从网格网格创建柔体 (rows x cols 质点网格)。
        /// 自动生成结构/剪切/弯曲弹簧。
        /// </summary>
        public static SoftBodyPhysicsSystem CreateFromGrid(
            Vector3 origin, int rows, int cols, float spacing,
            float mass, float structuralK, float shearK, float bendK, float damping)
        {
            int count = rows * cols;
            var particles = new SoftBodyParticle[count];
            for (int r = 0; r < rows; r++)
            {
                for (int c = 0; c < cols; c++)
                {
                    int idx = r * cols + c;
                    var pos = origin + new Vector3(c * spacing, -r * spacing, 0);
                    particles[idx] = new SoftBodyParticle(pos, mass);
                }
            }

            var system = new SoftBodyPhysicsSystem(count);
            system.SetParticles(particles);

            // 结构弹簧: 右、下
            for (int r = 0; r < rows; r++)
            {
                for (int c = 0; c < cols; c++)
                {
                    int idx = r * cols + c;
                    if (c < cols - 1)
                        system.AddSpring(idx, idx + 1, structuralK, damping, SoftBodySpringType.Structural);
                    if (r < rows - 1)
                        system.AddSpring(idx, idx + cols, structuralK, damping, SoftBodySpringType.Structural);
                }
            }

            // 剪切弹簧: 对角线
            for (int r = 0; r < rows - 1; r++)
            {
                for (int c = 0; c < cols - 1; c++)
                {
                    int idx = r * cols + c;
                    system.AddSpring(idx, idx + cols + 1, shearK, damping, SoftBodySpringType.Shear);
                    system.AddSpring(idx + 1, idx + cols, shearK, damping, SoftBodySpringType.Shear);
                }
            }

            // 弯曲弹簧: 跨1个
            for (int r = 0; r < rows; r++)
            {
                for (int c = 0; c < cols; c++)
                {
                    int idx = r * cols + c;
                    if (c < cols - 2)
                        system.AddSpring(idx, idx + 2, bendK, damping, SoftBodySpringType.Bend);
                    if (r < rows - 2)
                        system.AddSpring(idx, idx + 2 * cols, bendK, damping, SoftBodySpringType.Bend);
                }
            }

            return system;
        }

        /// <summary>
        /// 执行一次物理模拟步。
        /// </summary>
        public void Step(float deltaTime)
        {
            if (deltaTime <= 0f || _particles.Length == 0) return;

            // 1. 清零力累加器, 施加重力
            for (int i = 0; i < _particles.Length; i++)
            {
                _particles[i].ForceAccumulator = Vector3.Zero;
                if (!_particles[i].Pinned)
                    _particles[i].ForceAccumulator += Gravity * _particles[i].Mass;
            }

            // 2. 计算弹簧力 F = -k*x - c*v
            ComputeSpringForces();

            // 3. 计算压力模型力 (如果启用)
            if (EnablePressure)
                ComputePressureForces();

            // 4. 自碰撞力 (空间哈希)
            ComputeSelfCollisionForces();

            // 5. 刚体碰撞力
            ComputeRigidCollisionForces();

            // 6. Semi-Implicit Euler 积分
            Integrate(deltaTime);

            // 7. 刚体碰撞位置修正 (防止穿透)
            ResolveRigidPenetration();
        }

        /// <summary>计算所有弹簧力: F = -k*(|L|-L0)*dir - c*v_rel。</summary>
        private void ComputeSpringForces()
        {
            for (int s = 0; s < _springs.Count; s++)
            {
                ref readonly SoftBodySpring spring = ref _springs.AsSpan()[s];
                ref SoftBodyParticle pa = ref _particles[spring.ParticleA];
                ref SoftBodyParticle pb = ref _particles[spring.ParticleB];

                Vector3 L = pa.Position - pb.Position;
                float len = L.Length();
                if (len < 1e-8f) continue;

                Vector3 dir = L / len;
                float stretch = len - spring.RestLength;

                // 阻尼: 沿弹簧方向的相对速度
                Vector3 vRel = pa.Velocity - pb.Velocity;
                float vAlong = Vector3.Dot(vRel, dir);

                Vector3 force = dir * (-spring.Stiffness * stretch - spring.Damping * vAlong);

                if (!pa.Pinned)
                    pa.ForceAccumulator += force;
                if (!pb.Pinned)
                    pb.ForceAccumulator -= force;
            }
        }

        /// <summary>压力模型: 基于理想气体定律 P = nRT/V, 对每个面施加向外压力。</summary>
        private void ComputePressureForces()
        {
            float volume = ComputeEnclosedVolume();
            if (volume < 1e-6f) return;

            float pressure = PressureConstant / volume;

            for (int t = 0; t < _triangles.Count; t++)
            {
                ref readonly SoftBodyTriangle tri = ref _triangles.AsSpan()[t];
                Vector3 p0 = _particles[tri.V0].Position;
                Vector3 p1 = _particles[tri.V1].Position;
                Vector3 p2 = _particles[tri.V2].Position;

                // 面法线 (向外)
                Vector3 normal = Vector3.Cross(p1 - p0, p2 - p0);
                float area = normal.Length() * 0.5f;
                if (area < 1e-10f) continue;

                normal /= area * 2f; // 归一化

                // 压力力 = P * area * normal, 均分到3个顶点
                Vector3 force = normal * (pressure * area / 3f);

                if (!_particles[tri.V0].Pinned)
                    _particles[tri.V0].ForceAccumulator += force;
                if (!_particles[tri.V1].Pinned)
                    _particles[tri.V1].ForceAccumulator += force;
                if (!_particles[tri.V2].Pinned)
                    _particles[tri.V2].ForceAccumulator += force;
            }
        }

        /// <summary>用散度定理估算封闭体积: V = (1/6) * Σ (face_centroid . face_normal * area)。</summary>
        private float ComputeEnclosedVolume()
        {
            float volume = 0f;
            for (int t = 0; t < _triangles.Count; t++)
            {
                ref readonly SoftBodyTriangle tri = ref _triangles.AsSpan()[t];
                Vector3 p0 = _particles[tri.V0].Position;
                Vector3 p1 = _particles[tri.V1].Position;
                Vector3 p2 = _particles[tri.V2].Position;

                Vector3 centroid = (p0 + p1 + p2) / 3f;
                Vector3 cross = Vector3.Cross(p1 - p0, p2 - p0);
                volume += Vector3.Dot(centroid, cross);
            }
            return MathF.Abs(volume) / 6f;
        }

        /// <summary>自碰撞: 用空间哈希检测质点对穿透, 施加排斥力。</summary>
        private void ComputeSelfCollisionForces()
        {
            _hashGrid.Clear();
            _hashCellSize = _particles[0].Radius * 2f;

            // 插入所有质点到哈希网格
            for (int i = 0; i < _particles.Length; i++)
            {
                long hash = HashPosition(_particles[i].Position);
                if (!_hashGrid.TryGetValue(hash, out var list))
                {
                    list = new List<int>(4);
                    _hashGrid[hash] = list;
                }
                list.Add(i);
            }

            // 查询邻近质点
            for (int i = 0; i < _particles.Length; i++)
            {
                ref SoftBodyParticle pi = ref _particles[i];
                if (pi.Pinned) continue;

                // 检查当前格及相邻27格
                int cx = (int)MathF.Floor(pi.Position.X / _hashCellSize);
                int cy = (int)MathF.Floor(pi.Position.Y / _hashCellSize);
                int cz = (int)MathF.Floor(pi.Position.Z / _hashCellSize);

                for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                for (int dz = -1; dz <= 1; dz++)
                {
                    long hash = HashCell(cx + dx, cy + dy, cz + dz);
                    if (!_hashGrid.TryGetValue(hash, out var list)) continue;

                    for (int li = 0; li < list.Count; li++)
                    {
                        int j = list[li];
                        if (j <= i) continue;

                        ref SoftBodyParticle pj = ref _particles[j];
                        Vector3 delta = pi.Position - pj.Position;
                        float distSq = delta.LengthSquared();
                        float minDist = pi.Radius + pj.Radius;

                        if (distSq < minDist * minDist && distSq > 1e-12f)
                        {
                            float dist = MathF.Sqrt(distSq);
                            Vector3 dir = delta / dist;
                            float penetration = minDist - dist;

                            // 排斥力
                            Vector3 force = dir * (penetration * SelfCollisionStiffness * 100f);
                            if (!pi.Pinned)
                                pi.ForceAccumulator += force;
                            if (!pj.Pinned)
                                pj.ForceAccumulator -= force;
                        }
                    }
                }
            }
        }

        /// <summary>刚体碰撞: 质点 vs 球/盒, 施加力。</summary>
        private void ComputeRigidCollisionForces()
        {
            for (int i = 0; i < _particles.Length; i++)
            {
                ref SoftBodyParticle p = ref _particles[i];
                if (p.Pinned) continue;

                for (int c = 0; c < _colliders.Count; c++)
                {
                    var collider = _colliders[c];
                    Vector3 closest;
                    Vector3 normal;
                    float penetration;

                    if (collider.IsSphere)
                    {
                        float radius = collider.HalfExtents.X;
                        Vector3 delta = p.Position - collider.Position;
                        float dist = delta.Length();
                        float sumR = radius + p.Radius;
                        if (dist >= sumR) continue;

                        normal = dist > 1e-8f ? delta / dist : Vector3.UnitY;
                        penetration = sumR - dist;
                        closest = collider.Position + normal * radius;
                    }
                    else
                    {
                        // 盒碰撞: 将质点变换到盒局部空间
                        Vector3 localPos = Vector3.Transform(p.Position - collider.Position,
                            Quaternion.Inverse(collider.Rotation));
                        Vector3 clamped = Vector3.Clamp(localPos, -collider.HalfExtents, collider.HalfExtents);

                        Vector3 localDelta = localPos - clamped;
                        float dist = localDelta.Length();

                        if (dist >= p.Radius) continue;

                        if (dist > 1e-8f)
                        {
                            Vector3 localNormal = localDelta / dist;
                            normal = Vector3.Transform(localNormal, collider.Rotation);
                            penetration = p.Radius - dist;
                            closest = Vector3.Transform(clamped, collider.Rotation) + collider.Position;
                        }
                        else
                        {
                            // 质点在盒内部, 找最近面
                            Vector3 absLocal = new(MathF.Abs(localPos.X), MathF.Abs(localPos.Y), MathF.Abs(localPos.Z));
                            Vector3 d = collider.HalfExtents - absLocal;
                            if (d.X < d.Y && d.X < d.Z)
                            {
                                normal = new Vector3(MathF.Sign(localPos.X), 0, 0);
                                penetration = d.X + p.Radius;
                            }
                            else if (d.Y < d.Z)
                            {
                                normal = new Vector3(0, MathF.Sign(localPos.Y), 0);
                                penetration = d.Y + p.Radius;
                            }
                            else
                            {
                                normal = new Vector3(0, 0, MathF.Sign(localPos.Z));
                                penetration = d.Z + p.Radius;
                            }
                            normal = Vector3.Transform(normal, collider.Rotation);
                            closest = p.Position;
                        }
                    }

                    // 法向力 (弹簧-阻尼模型)
                    Vector3 relVel = p.Velocity - collider.Velocity;
                    float vn = Vector3.Dot(relVel, normal);

                    Vector3 force = normal * (penetration * RigidCollisionStiffness * 200f);
                    // 阻尼 (碰撞时)
                    if (vn < 0f)
                        force -= normal * (vn * RigidCollisionStiffness * 10f);

                    // 摩擦力
                    Vector3 vt = relVel - normal * vn;
                    force -= vt * (RigidCollisionFriction * RigidCollisionStiffness);

                    p.ForceAccumulator += force;
                }
            }
        }

        /// <summary>Semi-Implicit Euler 积分。</summary>
        private void Integrate(float dt)
        {
            for (int i = 0; i < _particles.Length; i++)
            {
                ref SoftBodyParticle p = ref _particles[i];
                if (p.Pinned || p.InverseMass <= 0f) continue;

                // v_new = v + (F/m)*dt
                p.Velocity += p.ForceAccumulator * p.InverseMass * dt;

                // 全局阻尼 (空气阻力)
                p.Velocity *= (1f - GlobalDamping);

                // x_new = x + v_new * dt
                p.Position += p.Velocity * dt;
            }
        }

        /// <summary>刚体穿透位置修正 (直接移动质点消除穿透)。</summary>
        private void ResolveRigidPenetration()
        {
            for (int i = 0; i < _particles.Length; i++)
            {
                ref SoftBodyParticle p = ref _particles[i];
                if (p.Pinned) continue;

                for (int c = 0; c < _colliders.Count; c++)
                {
                    var collider = _colliders[c];
                    if (collider.IsSphere)
                    {
                        float radius = collider.HalfExtents.X;
                        Vector3 delta = p.Position - collider.Position;
                        float dist = delta.Length();
                        float sumR = radius + p.Radius;
                        if (dist < sumR && dist > 1e-8f)
                        {
                            p.Position = collider.Position + delta / dist * sumR;
                        }
                    }
                    else
                    {
                        Vector3 localPos = Vector3.Transform(p.Position - collider.Position,
                            Quaternion.Inverse(collider.Rotation));
                        Vector3 clamped = Vector3.Clamp(localPos, -collider.HalfExtents, collider.HalfExtents);
                        Vector3 localDelta = localPos - clamped;
                        float dist = localDelta.Length();

                        if (dist < p.Radius && dist > 1e-8f)
                        {
                            Vector3 corrected = clamped + localDelta / dist * p.Radius;
                            p.Position = Vector3.Transform(corrected, collider.Rotation) + collider.Position;
                        }
                    }
                }
            }
        }

        /// <summary>空间哈希函数。</summary>
        private long HashPosition(Vector3 pos)
        {
            int cx = (int)MathF.Floor(pos.X / _hashCellSize);
            int cy = (int)MathF.Floor(pos.Y / _hashCellSize);
            int cz = (int)MathF.Floor(pos.Z / _hashCellSize);
            return HashCell(cx, cy, cz);
        }

        private static long HashCell(int cx, int cy, int cz)
        {
            return (long)cx * 73856093L ^ (long)cy * 19349663L ^ (long)cz * 83492791L;
        }

        /// <summary>获取所有质点位置 (用于渲染)。</summary>
        public Vector3[] GetPositions()
        {
            var positions = new Vector3[_particles.Length];
            for (int i = 0; i < _particles.Length; i++)
                positions[i] = _particles[i].Position;
            return positions;
        }

        /// <summary>对质点施加外力。</summary>
        public void ApplyForce(int index, Vector3 force)
        {
            if (index >= 0 && index < _particles.Length)
                _particles[index].ForceAccumulator += force;
        }
    }
}
