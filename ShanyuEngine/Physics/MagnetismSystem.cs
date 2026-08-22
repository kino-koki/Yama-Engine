using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace ShanyuEngine.Physics
{
    // ============================================================================================
    //  MagnetismSystem.cs
    //
    //  磁力系统。模拟磁场对带电物体/磁性物体的影响。
    //  物理上，磁力比重力复杂得多，有:
    //    - 静磁力: 永磁体之间的吸引/排斥
    //    - 洛伦兹力: 磁场对运动电荷的力 F = qv x B
    //    - 电磁感应: 变化的磁场产生电场(简化)
    //    - 磁化: 非磁性物体在磁场中被磁化
    //
    //  游戏里一般简化:
    //    1) 磁源(MagneticSource): 一个有磁矩的物体，产生磁场
    //    2) 受磁体(MagneticBody): 受磁场影响的物体
    //    3) 磁场查询: 某点的磁场强度和方向
    //
    //  注意: 真实磁力是1/r²衰减的，但游戏里为了好玩可以调整。
    //  TODO: 电磁感应(导体在磁场中运动产生电流)没实现，需要的话再加。
    // ============================================================================================

    /// <summary>磁极类型。</summary>
    public enum MagneticPole
    {
        North,      // N极
        South,      // S极
        Dipole,     // 偶极(同时有N和S)
    }

    /// <summary>磁源类型。</summary>
    public enum MagneticSourceType
    {
        /// <summary>点磁荷(简化)。最常用。</summary>
        PointCharge,

        /// <summary>磁偶极子。更真实但更复杂。</summary>
        Dipole,

        /// <summary>均匀磁场。整个区域磁场恒定。</summary>
        UniformField,

        /// <summary>电磁铁。电流产生的磁场。</summary>
        Electromagnet,

        /// <summary>磁场区域。盒形/球形区域内的磁场。</summary>
        FieldRegion,
    }

    /// <summary>磁源。产生磁场的物体。</summary>
    public sealed class MagneticSource
    {
        public int Id;
        public string Name = "";
        public MagneticSourceType Type;
        public Vector3 Position;
        public Quaternion Orientation = Quaternion.Identity;
        public float Strength;          // 磁场强度(A/m或自定义单位)
        public MagneticPole Pole = MagneticPole.Dipole;
        public Vector3 DipoleMoment;    // 磁偶极矩(对Dipole类型)
        public float Range = float.MaxValue; // 影响范围
        public float FalloffPower = 2f;  // 衰减幂(2=平方反比)
        public bool IsEnabled = true;
        public Vector3 FieldDirection = Vector3.UnitY; // 对UniformField
        public Vector3 RegionSize = Vector3.One;       // 对FieldRegion
        public float Current;           // 电流(对Electromagnet)
        public int CoilCount;           // 线圈匝数
        public List<MagneticPole> ActivePoles = new(); // 多极磁体

        /// <summary>获取某点的磁场强度向量。</summary>
        public Vector3 GetFieldAt(Vector3 point)
        {
            if (!IsEnabled) return Vector3.Zero;

            Vector3 toPoint = point - Position;
            float dist = toPoint.Length();
            if (dist > Range) return Vector3.Zero;
            if (dist < 0.001f) return Vector3.Zero;

            Vector3 dir = toPoint / dist;

            switch (Type)
            {
                case MagneticSourceType.PointCharge:
                    return GetPointChargeField(dir, dist);

                case MagneticSourceType.Dipole:
                    return GetDipoleField(toPoint, dist);

                case MagneticSourceType.UniformField:
                    if (IsInRegion(point))
                        return FieldDirection * Strength;
                    return Vector3.Zero;

                case MagneticSourceType.Electromagnet:
                    return GetElectromagnetField(dir, dist);

                case MagneticSourceType.FieldRegion:
                    if (IsInRegion(point))
                    {
                        // 区域内磁场，按距离衰减
                        float falloff = MathF.Pow(1f - dist / Range, FalloffPower);
                        return FieldDirection * Strength * falloff;
                    }
                    return Vector3.Zero;

                default:
                    return Vector3.Zero;
            }
        }

        private Vector3 GetPointChargeField(Vector3 dir, float dist)
        {
            // F ∝ q / r²
            float fieldMag = Strength / MathF.Pow(dist, FalloffPower);
            // N极: 向外发散(排斥N极物体)
            // S极: 向内收敛(吸引N极物体)
            return Pole == MagneticPole.South ? -dir * fieldMag : dir * fieldMag;
        }

        private Vector3 GetDipoleField(Vector3 r, float dist)
        {
            // 磁偶极子场公式:
            // B = (μ₀/4π) * (3(m·r̂)r̂ - m) / r³
            Vector3 m = Vector3.Transform(DipoleMoment, Orientation);
            Vector3 rHat = r / dist;
            float mDotR = Vector3.Dot(m, rHat);

            float factor = Strength / MathF.Pow(dist, FalloffPower + 1f);
            return (3f * mDotR * rHat - m) * factor;
        }

        private Vector3 GetElectromagnetField(Vector3 dir, float dist)
        {
            // 简化: 电磁铁 = 增强的点磁荷
            float effectiveStrength = Strength * Current * CoilCount;
            float fieldMag = effectiveStrength / MathF.Pow(dist, FalloffPower);
            return dir * fieldMag;
        }

        private bool IsInRegion(Vector3 point)
        {
            Vector3 d = point - Position;
            // 旋转到局部坐标系
            Vector3 local = Vector3.Transform(d, Quaternion.Inverse(Orientation));
            return MathF.Abs(local.X) <= RegionSize.X * 0.5f &&
                   MathF.Abs(local.Y) <= RegionSize.Y * 0.5f &&
                   MathF.Abs(local.Z) <= RegionSize.Z * 0.5f;
        }

        /// <summary>获取某点的磁场梯度(用于算磁化力)。</summary>
        public Vector3 GetFieldGradientAt(Vector3 point)
        {
            float eps = 0.01f;
            Vector3 fx = (GetFieldAt(point + new Vector3(eps, 0, 0)) -
                         GetFieldAt(point - new Vector3(eps, 0, 0))) / (2f * eps);
            Vector3 fy = (GetFieldAt(point + new Vector3(0, eps, 0)) -
                         GetFieldAt(point - new Vector3(0, eps, 0))) / (2f * eps);
            Vector3 fz = (GetFieldAt(point + new Vector3(0, 0, eps)) -
                         GetFieldAt(point - new Vector3(0, 0, eps))) / (2f * eps);
            return new Vector3(fx.X, fy.Y, fz.Z);
        }
    }

    /// <summary>受磁物体类型。</summary>
    public enum MagneticBodyType
    {
        /// <summary>铁磁性。被磁铁强烈吸引(铁、钴、镍)。</summary>
        Ferromagnetic,

        /// <summary>顺磁性。被弱吸引(铝、铂)。</summary>
        Paramagnetic,

        /// <summary>抗磁性。被弱排斥(铜、金、水)。</summary>
        Diamagnetic,

        /// <summary>永磁体。本身有磁矩。</summary>
        PermanentMagnet,

        /// <summary>带电粒子。受洛伦兹力。</summary>
        ChargedParticle,

        /// <summary>电磁体。可以开关的磁体。</summary>
        Electromagnet,
    }

    /// <summary>受磁物体。</summary>
    public struct MagneticBody
    {
        public int BodyId;
        public MagneticBodyType Type;
        public float Mass;
        public float MagneticSusceptibility;  // 磁化率(铁磁>1000, 顺磁~0.001, 抗磁~-0.001)
        public float Volume;
        public Vector3 PermanentMagneticMoment;   // 永磁体的磁矩
        public float Charge;                      // 带电粒子的电荷
        public float MagnetizationStrength;       // 磁化强度(被磁化后的磁矩)
        public Vector3 CurrentMagneticMoment;     // 当前磁矩(永磁+感应)
        public bool IsEnabled = true;
        public float ForceMultiplier = 1f;
        public Vector3 LastForce;
        public Vector3 LastTorque;
        public float LastFieldStrength;

        public MagneticBody()
        {
            BodyId = 0;
            Type = MagneticBodyType.Ferromagnetic;
            Mass = 1f;
            MagneticSusceptibility = 1000f;
            Volume = 0.001f;
            PermanentMagneticMoment = Vector3.Zero;
            Charge = 0;
            MagnetizationStrength = 0;
            CurrentMagneticMoment = Vector3.Zero;
        }
    }

    /// <summary>
    ///   磁力系统。管理所有磁源和受磁物体。
    /// </summary>
    public sealed class MagnetismSystem : IDisposable
    {
        private readonly List<MagneticSource> _sources = new();
        private readonly Dictionary<int, MagneticBody> _bodies = new();
        private bool _disposed;
        private float _time;
        private float _totalForceApplied;
        private float _totalTorqueApplied;
        private int _activeSourceCount;

        // 磁场缓存(空间网格)
        private readonly Dictionary<(int, int, int), Vector3> _fieldCache = new();
        private float _cacheCellSize = 1f;
        private bool _cacheEnabled;
        private float _cacheExpiryTime;
        private float _cacheTimer;

        public IReadOnlyList<MagneticSource> Sources => _sources;
        public int BodyCount => _bodies.Count;
        public int ActiveSourceCount => _activeSourceCount;
        public float Time => _time;
        public float TotalForceApplied => _totalForceApplied;
        public float TotalTorqueApplied => _totalTorqueApplied;

        public bool CacheEnabled
        {
            get => _cacheEnabled;
            set
            {
                _cacheEnabled = value;
                if (!value) _fieldCache.Clear();
            }
        }

        public float CacheCellSize
        {
            get => _cacheCellSize;
            set
            {
                _cacheCellSize = MathF.Max(0.1f, value);
                _fieldCache.Clear();
            }
        }

        // ========================================================================================
        //  磁源管理
        // ========================================================================================

        /// <summary>添加点磁荷磁源。</summary>
        public MagneticSource AddPointCharge(Vector3 position, float strength, MagneticPole pole, float range = 20f)
        {
            var source = new MagneticSource
            {
                Id = _sources.Count + 1,
                Name = $"PointCharge_{_sources.Count + 1}",
                Type = MagneticSourceType.PointCharge,
                Position = position,
                Strength = strength,
                Pole = pole,
                Range = range,
                FalloffPower = 2f,
            };
            _sources.Add(source);
            return source;
        }

        /// <summary>添加磁偶极子。</summary>
        public MagneticSource AddDipole(Vector3 position, Vector3 moment, float strength, float range = 20f)
        {
            var source = new MagneticSource
            {
                Id = _sources.Count + 1,
                Name = $"Dipole_{_sources.Count + 1}",
                Type = MagneticSourceType.Dipole,
                Position = position,
                DipoleMoment = moment,
                Strength = strength,
                Pole = MagneticPole.Dipole,
                Range = range,
                FalloffPower = 2f,
            };
            _sources.Add(source);
            return source;
        }

        /// <summary>添加均匀磁场。</summary>
        public MagneticSource AddUniformField(Vector3 direction, float strength, Vector3 regionCenter, Vector3 regionSize)
        {
            var source = new MagneticSource
            {
                Id = _sources.Count + 1,
                Name = $"UniformField_{_sources.Count + 1}",
                Type = MagneticSourceType.UniformField,
                Position = regionCenter,
                FieldDirection = Vector3.Normalize(direction),
                Strength = strength,
                RegionSize = regionSize,
                Range = regionSize.Length(),
            };
            _sources.Add(source);
            return source;
        }

        /// <summary>添加电磁铁。</summary>
        public MagneticSource AddElectromagnet(Vector3 position, float current, int coils, float range = 15f)
        {
            var source = new MagneticSource
            {
                Id = _sources.Count + 1,
                Name = $"Electromagnet_{_sources.Count + 1}",
                Type = MagneticSourceType.Electromagnet,
                Position = position,
                Current = current,
                CoilCount = coils,
                Strength = 1000f,
                Range = range,
            };
            _sources.Add(source);
            return source;
        }

        /// <summary>移除磁源。</summary>
        public bool RemoveSource(int sourceId)
        {
            int idx = _sources.FindIndex(s => s.Id == sourceId);
            if (idx < 0) return false;
            _sources.RemoveAt(idx);
            _fieldCache.Clear();
            return true;
        }

        /// <summary>获取磁源。</summary>
        public MagneticSource? GetSource(int sourceId)
        {
            return _sources.Find(s => s.Id == sourceId);
        }

        /// <summary>清除所有磁源。</summary>
        public void ClearSources()
        {
            _sources.Clear();
            _fieldCache.Clear();
        }

        // ========================================================================================
        //  物体管理
        // ========================================================================================

        /// <summary>注册受磁物体。</summary>
        public void RegisterBody(MagneticBody body)
        {
            _bodies[body.BodyId] = body;
        }

        /// <summary>注册铁磁性物体(简化)。</summary>
        public void RegisterFerromagnetic(int bodyId, float mass, float volume, float multiplier = 1f)
        {
            _bodies[bodyId] = new MagneticBody
            {
                BodyId = bodyId,
                Type = MagneticBodyType.Ferromagnetic,
                Mass = mass,
                Volume = volume,
                MagneticSusceptibility = 1000f,
                ForceMultiplier = multiplier,
                IsEnabled = true,
            };
        }

        /// <summary>注册永磁体。</summary>
        public void RegisterPermanentMagnet(int bodyId, float mass, Vector3 magneticMoment, float multiplier = 1f)
        {
            _bodies[bodyId] = new MagneticBody
            {
                BodyId = bodyId,
                Type = MagneticBodyType.PermanentMagnet,
                Mass = mass,
                PermanentMagneticMoment = magneticMoment,
                CurrentMagneticMoment = magneticMoment,
                ForceMultiplier = multiplier,
                IsEnabled = true,
            };
        }

        /// <summary>注册带电粒子。</summary>
        public void RegisterChargedParticle(int bodyId, float mass, float charge)
        {
            _bodies[bodyId] = new MagneticBody
            {
                BodyId = bodyId,
                Type = MagneticBodyType.ChargedParticle,
                Mass = mass,
                Charge = charge,
                ForceMultiplier = 1f,
                IsEnabled = true,
            };
        }

        /// <summary>移除物体。</summary>
        public bool UnregisterBody(int bodyId)
        {
            return _bodies.Remove(bodyId);
        }

        /// <summary>获取物体。</summary>
        public MagneticBody? GetBody(int bodyId)
        {
            return _bodies.TryGetValue(bodyId, out var b) ? b : null;
        }

        // ========================================================================================
        //  主更新
        // ========================================================================================

        /// <summary>每帧更新。</summary>
        public void Update(float dt, Func<int, (Vector3 pos, Quaternion rot, Vector3 vel)> getState,
            Action<int, Vector3, Vector3> applyForce)
        {
            if (_disposed) return;

            _time += dt;
            _totalForceApplied = 0;
            _totalTorqueApplied = 0;
            _activeSourceCount = 0;

            foreach (var s in _sources)
            {
                if (s.IsEnabled) _activeSourceCount++;
            }

            // 缓存过期
            if (_cacheEnabled)
            {
                _cacheTimer += dt;
                if (_cacheTimer > _cacheExpiryTime)
                {
                    _fieldCache.Clear();
                    _cacheTimer = 0;
                }
            }

            // 对每个物体
            foreach (var kvp in _bodies)
            {
                int bodyId = kvp.Key;
                var body = kvp.Value;
                if (!body.IsEnabled) continue;

                var state = getState(bodyId);
                Vector3 force = Vector3.Zero;
                Vector3 torque = Vector3.Zero;

                // 获取总磁场
                Vector3 totalField = ComputeFieldAt(state.pos);

                switch (body.Type)
                {
                    case MagneticBodyType.Ferromagnetic:
                    case MagneticBodyType.Paramagnetic:
                    case MagneticBodyType.Diamagnetic:
                        ComputeMagnetizationForce(body, state.pos, state.rot, totalField, ref force, ref torque);
                        break;

                    case MagneticBodyType.PermanentMagnet:
                        ComputePermanentMagnetForce(body, state.pos, state.rot, totalField, ref force, ref torque);
                        break;

                    case MagneticBodyType.ChargedParticle:
                        ComputeLorentzForce(body, state.vel, totalField, ref force);
                        break;

                    case MagneticBodyType.Electromagnet:
                        // 电磁体既是源也是受磁体，这里简化
                        ComputeMagnetizationForce(body, state.pos, state.rot, totalField, ref force, ref torque);
                        break;
                }

                applyForce(bodyId, force, torque);

                body.LastForce = force;
                body.LastTorque = torque;
                body.LastFieldStrength = totalField.Length();
                _totalForceApplied += force.Length();
                _totalTorqueApplied += torque.Length();
            }
        }

        // ========================================================================================
        //  力计算
        // ========================================================================================

        /// <summary>计算某点的总磁场强度。</summary>
        public Vector3 ComputeFieldAt(Vector3 point)
        {
            // 用缓存
            if (_cacheEnabled)
            {
                var key = GetCacheKey(point);
                if (_fieldCache.TryGetValue(key, out var cached))
                    return cached;
            }

            Vector3 total = Vector3.Zero;
            foreach (var s in _sources)
            {
                if (!s.IsEnabled) continue;
                total += s.GetFieldAt(point);
            }

            if (_cacheEnabled)
            {
                _fieldCache[GetCacheKey(point)] = total;
            }

            return total;
        }

        private (int, int, int) GetCacheKey(Vector3 p)
        {
            return (
                (int)MathF.Floor(p.X / _cacheCellSize),
                (int)MathF.Floor(p.Y / _cacheCellSize),
                (int)MathF.Floor(p.Z / _cacheCellSize));
        }

        /// <summary>
        ///   计算磁化力(对铁磁/顺磁/抗磁物体)。
        ///   F = (χ * V / (2 * μ₀)) * ∇|B|²
        ///   铁磁/顺磁: χ > 0, 被吸引到强场区
        ///   抗磁: χ < 0, 被排斥
        /// </summary>
        private void ComputeMagnetizationForce(MagneticBody body, Vector3 pos, Quaternion rot,
            Vector3 field, ref Vector3 force, ref Vector3 torque)
        {
            if (field.LengthSquared() < 1e-8f) return;

            // 算磁场梯度
            Vector3 gradient = Vector3.Zero;
            foreach (var s in _sources)
            {
                if (!s.IsEnabled) continue;
                gradient += s.GetFieldGradientAt(pos);
            }

            // 磁化力
            const float mu0 = 1.25663706e-6f; // 真空磁导率
            float fieldMagSq = field.LengthSquared();
            Vector3 forceDir = gradient * fieldMagSq; // 简化
            float sign = body.MagneticSusceptibility > 0 ? 1f : -1f;
            force += sign * forceDir * body.Volume * MathF.Abs(body.MagneticSusceptibility) *
                     body.ForceMultiplier / (2f * mu0);

            // 感应磁矩(简化)
            body.CurrentMagneticMoment = field * body.MagneticSusceptibility * body.Volume * 0.01f;

            // 扭矩: 让磁矩对齐磁场
            if (body.CurrentMagneticMoment.LengthSquared() > 1e-8f)
            {
                torque += Vector3.Cross(body.CurrentMagneticMoment, field) * body.ForceMultiplier;
            }
        }

        /// <summary>
        ///   计算永磁体受力。
        ///   F = ∇(m·B)
        ///   T = m × B
        /// </summary>
        private void ComputePermanentMagnetForce(MagneticBody body, Vector3 pos, Quaternion rot,
            Vector3 field, ref Vector3 force, ref Vector3 torque)
        {
            Vector3 m = Vector3.Transform(body.PermanentMagneticMoment, rot);
            body.CurrentMagneticMoment = m;

            // 算m·B的梯度
            float eps = 0.01f;
            Vector3 gradX = (ComputeFieldAt(pos + new Vector3(eps, 0, 0)) -
                            ComputeFieldAt(pos - new Vector3(eps, 0, 0))) / (2f * eps);
            Vector3 gradY = (ComputeFieldAt(pos + new Vector3(0, eps, 0)) -
                            ComputeFieldAt(pos - new Vector3(0, eps, 0))) / (2f * eps);
            Vector3 gradZ = (ComputeFieldAt(pos + new Vector3(0, 0, eps)) -
                            ComputeFieldAt(pos - new Vector3(0, 0, eps))) / (2f * eps);

            // F_i = ∂(m·B)/∂x_i = m · (∂B/∂x_i)
            float fx = Vector3.Dot(m, gradX);
            float fy = Vector3.Dot(m, gradY);
            float fz = Vector3.Dot(m, gradZ);
            force += new Vector3(fx, fy, fz) * body.ForceMultiplier;

            // 扭矩
            torque += Vector3.Cross(m, field) * body.ForceMultiplier;
        }

        /// <summary>
        ///   计算洛伦兹力。
        ///   F = q * v × B
        /// </summary>
        private void ComputeLorentzForce(MagneticBody body, Vector3 velocity, Vector3 field,
            ref Vector3 force)
        {
            if (field.LengthSquared() < 1e-8f) return;
            if (velocity.LengthSquared() < 1e-8f) return;

            force += body.Charge * Vector3.Cross(velocity, field) * body.ForceMultiplier;
        }

        // ========================================================================================
        //  查询API
        // ========================================================================================

        /// <summary>获取某点的磁场强度。</summary>
        public float GetFieldStrengthAt(Vector3 point)
        {
            return ComputeFieldAt(point).Length();
        }

        /// <summary>获取某点的磁场方向(归一化)。</summary>
        public Vector3 GetFieldDirectionAt(Vector3 point)
        {
            Vector3 f = ComputeFieldAt(point);
            return f.LengthSquared() > 1e-8f ? Vector3.Normalize(f) : Vector3.Zero;
        }

        /// <summary>获取两磁源之间的作用力(简化)。</summary>
        public Vector3 GetForceBetweenSources(int sourceA, int sourceB)
        {
            var a = GetSource(sourceA);
            var b = GetSource(sourceB);
            if (a == null || b == null) return Vector3.Zero;

            Vector3 toB = b.Position - a.Position;
            float dist = toB.Length();
            if (dist < 0.001f) return Vector3.Zero;

            // 简化: 同极排斥，异极吸引
            float sign = (a.Pole == b.Pole) ? -1f : 1f;
            float force = a.Strength * b.Strength / (dist * dist) * sign;
            return Vector3.Normalize(toB) * force;
        }

        /// <summary>设置电磁铁电流(开关磁力)。</summary>
        public void SetElectromagnetCurrent(int sourceId, float current)
        {
            var s = GetSource(sourceId);
            if (s != null && s.Type == MagneticSourceType.Electromagnet)
            {
                s.Current = current;
                _fieldCache.Clear();
            }
        }

        // ========================================================================================
        //  预设
        // ========================================================================================

        /// <summary>创建条形磁铁(N极一个点，S极一个点)。</summary>
        public (MagneticSource north, MagneticSource south) CreateBarMagnet(Vector3 center, Vector3 axis,
            float length, float strength)
        {
            Vector3 dir = Vector3.Normalize(axis) * length * 0.5f;
            var north = AddPointCharge(center + dir, strength, MagneticPole.North, length * 5f);
            north.Name = "BarMagnet_North";
            var south = AddPointCharge(center - dir, strength, MagneticPole.South, length * 5f);
            south.Name = "BarMagnet_South";
            return (north, south);
        }

        /// <summary>创建U形磁铁。</summary>
        public List<MagneticSource> CreateHorseshoeMagnet(Vector3 center, float size, float strength)
        {
            var sources = new List<MagneticSource>();
            float half = size * 0.5f;

            // N极
            var n = AddPointCharge(center + new Vector3(0, 0, half), strength, MagneticPole.North, size * 3f);
            n.Name = "Horseshoe_North";
            sources.Add(n);

            // S极
            var s = AddPointCharge(center + new Vector3(0, 0, -half), strength, MagneticPole.South, size * 3f);
            s.Name = "Horseshoe_South";
            sources.Add(s);

            return sources;
        }

        /// <summary>创建螺线管(简化为多个电磁铁)。</summary>
        public List<MagneticSource> CreateSolenoid(Vector3 center, Vector3 axis, float length,
            float radius, int segmentCount, float current, int coilsPerSegment)
        {
            var sources = new List<MagneticSource>();
            Vector3 dir = Vector3.Normalize(axis);
            float segLen = length / segmentCount;

            for (int i = 0; i < segmentCount; i++)
            {
                float t = (i + 0.5f) * segLen - length * 0.5f;
                Vector3 pos = center + dir * t;
                var em = AddElectromagnet(pos, current, coilsPerSegment, radius * 4f);
                em.Name = $"Solenoid_Segment_{i}";
                sources.Add(em);
            }

            return sources;
        }

        // ========================================================================================
        //  统计
        // ========================================================================================

        public void GetStats(out int sourceCount, out int bodyCount, out int activeSources,
            out float totalForce, out float totalTorque)
        {
            sourceCount = _sources.Count;
            bodyCount = _bodies.Count;
            activeSources = _activeSourceCount;
            totalForce = _totalForceApplied;
            totalTorque = _totalTorqueApplied;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _sources.Clear();
            _bodies.Clear();
            _fieldCache.Clear();
        }
    }

    // ============================================================================================
    //  磁场可视化
    // ============================================================================================

    /// <summary>磁力线。</summary>
    public struct MagneticFieldLine
    {
        public Vector3[] Points;
        public float Strength;
    }

    /// <summary>磁力线生成器。</summary>
    public sealed class MagneticFieldLineGenerator
    {
        private readonly MagnetismSystem _system;

        public MagneticFieldLineGenerator(MagnetismSystem system)
        {
            _system = system;
        }

        /// <summary>从某个起点追踪一条磁力线。</summary>
        public MagneticFieldLine TraceFieldLine(Vector3 start, int maxSteps, float stepSize)
        {
            var points = new List<Vector3>(maxSteps) { start };
            Vector3 current = start;
            float strength = _system.GetFieldStrengthAt(start);

            for (int i = 0; i < maxSteps; i++)
            {
                Vector3 field = _system.GetFieldDirectionAt(current);
                if (field.LengthSquared() < 1e-8f) break;

                current += field * stepSize;
                points.Add(current);

                // 超出范围或太靠近磁源就停
                if (current.LengthSquared() > 10000f) break;
            }

            return new MagneticFieldLine
            {
                Points = points.ToArray(),
                Strength = strength,
            };
        }

        /// <summary>生成多条磁力线。</summary>
        public List<MagneticFieldLine> GenerateFieldLines(Vector3 center, float radius, int lineCount,
            int maxSteps = 200, float stepSize = 0.2f)
        {
            var lines = new List<MagneticFieldLine>(lineCount);
            for (int i = 0; i < lineCount; i++)
            {
                // 在球面上均匀采样起点
                float theta = MathF.Acos(1f - 2f * (i + 0.5f) / lineCount);
                float phi = MathF.PI * (1f + MathF.Sqrt(5f)) * i;
                Vector3 dir = new(
                    MathF.Sin(theta) * MathF.Cos(phi),
                    MathF.Sin(theta) * MathF.Sin(phi),
                    MathF.Cos(theta));
                Vector3 start = center + dir * radius;

                lines.Add(TraceFieldLine(start, maxSteps, stepSize));
            }
            return lines;
        }
    }

    // ============================================================================================
    //  电磁感应(简化)
    // ============================================================================================

    /// <summary>电磁感应数据。</summary>
    public struct ElectromagneticInduction
    {
        public int BodyId;
        public float InducedEMF;        // 感应电动势
        public Vector3 InducedCurrent;  // 感应电流方向
        public float Power;             // 功率
    }

    /// <summary>电磁感应计算器。</summary>
    public sealed class ElectromagneticInductionCalculator
    {
        private readonly MagnetismSystem _system;
        private readonly Dictionary<int, ElectromagneticInduction> _inductions = new();

        public ElectromagneticInductionCalculator(MagnetismSystem system)
        {
            _system = system;
        }

        /// <summary>更新一个导体的感应电动势。</summary>
        public void Update(int bodyId, Vector3 pos, Vector3 vel, Quaternion rot, Vector3 angVel,
            float coilArea, int coilCount)
        {
            Vector3 field = _system.ComputeFieldAt(pos);
            // EMF = -N * dΦ/dt, Φ = B·A
            // 简化: 用速度近似dΦ/dt
            Vector3 areaNormal = Vector3.Transform(Vector3.UnitY, rot);
            float flux = Vector3.Dot(field, areaNormal) * coilArea;

            // 磁通量变化率(运动引起)
            Vector3 dField = _system.ComputeFieldAt(pos + vel * 0.01f) - field;
            float dFlux = Vector3.Dot(dField, areaNormal) * coilArea * 100f;
            // 旋转引起
            float rotFlux = Vector3.Dot(field, Vector3.Cross(angVel, areaNormal)) * coilArea;

            float emf = -coilCount * (dFlux + rotFlux);
            Vector3 currentDir = Vector3.Cross(areaNormal, field);
            float power = emf * emf / 10f; // 假设电阻10Ω

            _inductions[bodyId] = new ElectromagneticInduction
            {
                BodyId = bodyId,
                InducedEMF = emf,
                InducedCurrent = currentDir,
                Power = power,
            };
        }

        public ElectromagneticInduction? GetInduction(int bodyId)
        {
            return _inductions.TryGetValue(bodyId, out var i) ? i : null;
        }
    }

    // ============================================================================================
    //  磁性材质
    // ============================================================================================

    /// <summary>磁性材质预设。</summary>
    public static class MagneticMaterials
    {
        public static MagneticBody Iron => new()
        {
            Type = MagneticBodyType.Ferromagnetic,
            MagneticSusceptibility = 5000f,
            Volume = 0.001f,
        };

        public static MagneticBody Cobalt => new()
        {
            Type = MagneticBodyType.Ferromagnetic,
            MagneticSusceptibility = 7000f,
            Volume = 0.001f,
        };

        public static MagneticBody Nickel => new()
        {
            Type = MagneticBodyType.Ferromagnetic,
            MagneticSusceptibility = 3000f,
            Volume = 0.001f,
        };

        public static MagneticBody Aluminum => new()
        {
            Type = MagneticBodyType.Paramagnetic,
            MagneticSusceptibility = 2.2e-5f,
            Volume = 0.001f,
        };

        public static MagneticBody Copper => new()
        {
            Type = MagneticBodyType.Diamagnetic,
            MagneticSusceptibility = -1.0e-5f,
            Volume = 0.001f,
        };

        public static MagneticBody Gold => new()
        {
            Type = MagneticBodyType.Diamagnetic,
            MagneticSusceptibility = -3.4e-5f,
            Volume = 0.001f,
        };

        public static MagneticBody Water => new()
        {
            Type = MagneticBodyType.Diamagnetic,
            MagneticSusceptibility = -9.0e-6f,
            Volume = 0.001f,
        };

        public static MagneticBody Superconductor => new()
        {
            Type = MagneticBodyType.Diamagnetic,
            MagneticSusceptibility = -1f, // 完全抗磁
            Volume = 0.001f,
        };
    }
}
