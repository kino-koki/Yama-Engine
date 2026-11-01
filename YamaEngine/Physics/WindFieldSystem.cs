using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace YamaEngine.Physics
{
    // ============================================================================================
    //  WindFieldSystem.cs
    //
    //  风场系统。用来模拟风对物理物体的影响。
    //  风3D向量场 + 时间演化 + 多个风源(暴风/旋风/局部风扇)。
    //
    //  用法:
    //    1) AddWindZone加几个风区
    //    2) RegisterBody注册受风影响的物体
    //    3) 每帧Update(dt)，会回调施加力
    //
    //  实现细节:
    //    - 全局风: 一个方向+强度，叠加噪声让它不均匀
    //    - 风区: 局部球形/盒形区域，强度可调
    //    - 旋风: 圆周运动+向上吸力
    //    - 阵风: 短时强风，会衰减
    //    - 物体受风面积: 简化用AABB投影，或者用形状的迎风面积
    //
    //  注意: 风对轻物体影响大，对重物体影响小。阻力公式 F = 0.5 * ρ * v² * Cd * A
    // ============================================================================================

    /// <summary>风区类型。</summary>
    public enum WindZoneType
    {
        /// <summary>全局方向风。整个场景都有。</summary>
        Directional,

        /// <summary>球形风区。离中心越近风越强。</summary>
        Spherical,

        /// <summary>盒形风区。</summary>
        Box,

        /// <summary>旋风。圆周运动+向上吸。</summary>
        Vortex,

        /// <summary>风扇。一个方向的锥形风。</summary>
        Fan,

        /// <summary>爆炸性气流。短时强风。</summary>
        Pulse,
    }

    /// <summary>风区形状。</summary>
    public enum WindFalloffMode
    {
        /// <summary>恒定强度。</summary>
        Constant,

        /// <summary>线性衰减。</summary>
        Linear,

        /// <summary>二次衰减(更陡)。</summary>
        Quadratic,

        /// <summary>平滑阶梯。</summary>
        Smoothstep,
    }

    /// <summary>
    ///   风区。定义一个区域内的风。
    /// </summary>
    public sealed class WindZone
    {
        public int Id;
        public string Name = "";
        public WindZoneType Type;
        public Vector3 Position;
        public Vector3 Direction = Vector3.UnitX;
        public float Strength;          // 基础强度
        public float Radius;            // 球形半径
        public Vector3 BoxSize;         // 盒形尺寸
        public float FanAngle = 0.5f;   // 锥角(弧度)
        public float FanRange = 10f;    // 锥距离
        public WindFalloffMode Falloff = WindFalloffMode.Linear;
        public float Turbulence = 0.2f; // 湍流强度(0~1)
        public float TurbulenceFrequency = 0.5f;
        public float PulseDuration;     // 脉冲持续时间
        public float PulseTimer;        // 脉冲剩余时间
        public float PulseStrength;     // 脉冲强度
        public bool IsEnabled = true;
        public float Priority;          // 优先级(多个风区叠加时用)
        public VortexConfig? VortexConfig;

        /// <summary>检查点是否在风区内。</summary>
        public bool Contains(Vector3 point)
        {
            switch (Type)
            {
                case WindZoneType.Directional:
                    return true;
                case WindZoneType.Spherical:
                case WindZoneType.Vortex:
                    return Vector3.DistanceSquared(point, Position) <= Radius * Radius;
                case WindZoneType.Box:
                    Vector3 d = point - Position;
                    return MathF.Abs(d.X) <= BoxSize.X * 0.5f &&
                           MathF.Abs(d.Y) <= BoxSize.Y * 0.5f &&
                           MathF.Abs(d.Z) <= BoxSize.Z * 0.5f;
                case WindZoneType.Fan:
                    Vector3 toPoint = point - Position;
                    float dist = toPoint.Length();
                    if (dist > FanRange) return false;
                    if (dist < 0.01f) return true;
                    Vector3 dir = toPoint / dist;
                    float dot = Vector3.Dot(dir, Vector3.Normalize(Direction));
                    float angle = MathF.Acos(Math.Clamp(dot, -1f, 1f));
                    return angle <= FanAngle;
                case WindZoneType.Pulse:
                    return Vector3.DistanceSquared(point, Position) <= Radius * Radius;
                default:
                    return false;
            }
        }

        /// <summary>获取某点的风强度系数(0~1，含衰减)。</summary>
        public float GetFalloff(Vector3 point)
        {
            if (Type == WindZoneType.Directional) return 1f;

            float distSq;
            float maxDist;

            switch (Type)
            {
                case WindZoneType.Spherical:
                case WindZoneType.Vortex:
                case WindZoneType.Pulse:
                    distSq = Vector3.DistanceSquared(point, Position);
                    maxDist = Radius;
                    break;
                case WindZoneType.Box:
                    // 简化:用中心距离和BoxSize的平均
                    distSq = Vector3.DistanceSquared(point, Position);
                    maxDist = (BoxSize.X + BoxSize.Y + BoxSize.Z) / 3f * 0.5f;
                    break;
                case WindZoneType.Fan:
                    Vector3 toPoint = point - Position;
                    distSq = toPoint.LengthSquared();
                    maxDist = FanRange;
                    break;
                default:
                    return 1f;
            }

            if (distSq >= maxDist * maxDist) return 0f;
            float t = MathF.Sqrt(distSq) / maxDist;

            return Falloff switch
            {
                WindFalloffMode.Constant => 1f,
                WindFalloffMode.Linear => 1f - t,
                WindFalloffMode.Quadratic => (1f - t) * (1f - t),
                WindFalloffMode.Smoothstep => 1f - t * t * (3f - 2f * t),
                _ => 1f - t,
            };
        }

        /// <summary>获取某点的风速向量(不含湍流)。</summary>
        public Vector3 GetWindVector(Vector3 point, float time)
        {
            float falloff = GetFalloff(point);
            if (falloff <= 0) return Vector3.Zero;

            Vector3 wind;
            switch (Type)
            {
                case WindZoneType.Directional:
                case WindZoneType.Spherical:
                case WindZoneType.Box:
                case WindZoneType.Fan:
                    wind = Vector3.Normalize(Direction) * Strength * falloff;
                    break;

                case WindZoneType.Vortex:
                    wind = ComputeVortexWind(point, falloff);
                    break;

                case WindZoneType.Pulse:
                    float pulseFactor = PulseTimer > 0 ? (PulseTimer / PulseDuration) : 0;
                    wind = Vector3.Normalize(Direction) * (Strength + PulseStrength * pulseFactor) * falloff;
                    break;

                default:
                    wind = Vector3.Zero;
                    break;
            }

            // 添加湍流
            if (Turbulence > 0)
            {
                wind += ComputeTurbulence(point, time) * Turbulence;
            }

            return wind;
        }

        private Vector3 ComputeVortexWind(Vector3 point, float falloff)
        {
            if (VortexConfig == null) return Vector3.Zero;
            var cfg = VortexConfig.Value;

            Vector3 toCenter = point - Position;
            // 投影到水平面
            Vector3 horizontal = new(toCenter.X, 0, toCenter.Z);
            float horizDist = horizontal.Length();
            if (horizDist < 0.01f)
            {
                // 中心: 向上吸
                return Vector3.UnitY * cfg.UpwardForce * falloff;
            }

            Vector3 radial = horizontal / horizDist;
            // 切向(旋转方向)
            Vector3 tangent = new(-radial.Z, 0, radial.X);
            // 旋转方向由Sign决定
            tangent *= cfg.RotationDirection;

            // 切向力(随距离衰减)
            float tangentStrength = Strength * falloff;
            // 径向(向中心吸)
            float radialStrength = cfg.InwardForce * falloff;
            // 向上吸
            float upStrength = cfg.UpwardForce * falloff;

            Vector3 wind = tangent * tangentStrength - radial * radialStrength + Vector3.UnitY * upStrength;

            // 高度衰减(越往上旋风越弱)
            float heightFactor = Math.Clamp(1f - MathF.Abs(toCenter.Y) / cfg.Height, 0f, 1f);
            wind *= heightFactor;

            return wind;
        }

        private Vector3 ComputeTurbulence(Vector3 point, float time)
        {
            // 用多个sin叠加模拟噪声
            float x = point.X * TurbulenceFrequency + time;
            float y = point.Y * TurbulenceFrequency + time * 1.3f;
            float z = point.Z * TurbulenceFrequency + time * 0.7f;

            float nx = MathF.Sin(x) * MathF.Cos(y * 1.7f) + MathF.Sin(x * 2.3f) * 0.5f;
            float ny = MathF.Sin(y) * MathF.Cos(z * 1.5f) + MathF.Sin(y * 2.7f) * 0.5f;
            float nz = MathF.Sin(z) * MathF.Cos(x * 1.3f) + MathF.Sin(z * 2.1f) * 0.5f;

            return new Vector3(nx, ny, nz) * Strength;
        }

        /// <summary>更新脉冲计时器。</summary>
        public void Update(float dt)
        {
            if (PulseTimer > 0)
            {
                PulseTimer -= dt;
                if (PulseTimer < 0) PulseTimer = 0;
            }
        }

        /// <summary>触发脉冲。</summary>
        public void TriggerPulse(float strength, float duration)
        {
            PulseStrength = strength;
            PulseDuration = duration;
            PulseTimer = duration;
        }
    }

    /// <summary>旋风配置。</summary>
    public struct VortexConfig
    {
        public float Height;            // 旋风高度
        public float RotationDirection; // 旋转方向: 1=逆时针, -1=顺时针
        public float InwardForce;       // 向心力
        public float UpwardForce;       // 向上力
    }

    /// <summary>
    ///   受风物体。需要风场影响的物体。
    /// </summary>
    public struct WindAffectedBody
    {
        public int BodyId;
        public float Mass;
        public float DragCoefficient;   // 风阻系数
        public float FrontalArea;       // 迎风面积
        public float WindMultiplier;    // 风力倍率(轻物体>1, 重物体<1)
        public bool UseAABBProjection;  // 用AABB算迎风面
        public Vector3 AABBHalfExtents;
        public WindAffectedShapeType ShapeType;
        public float SphereRadius;
        public Vector3 BoxHalfExtents;
        public float CapsuleRadius;
        public float CapsuleHeight;
        public Vector3 LastWindForce;
        public float LastWindSpeed;
        public bool IsWindSensitive;    // 是否对风敏感(轻物体)
    }

    /// <summary>受风物体形状类型。</summary>
    public enum WindAffectedShapeType
    {
        Sphere,
        Box,
        Capsule,
        Mesh,
        Point,
    }

    /// <summary>
    ///   全局风配置。
    /// </summary>
    public struct GlobalWindConfig
    {
        public Vector3 BaseWind;            // 基础风向
        public float BaseStrength;          // 基础强度
        public float Turbulence;            // 全局湍流(0~1)
        public float GustFrequency;         // 阵风频率
        public float GustStrength;          // 阵风强度
        public float GustDuration;          // 阵风持续时间
        public float HeightFalloff;         // 高度衰减(越高风越大，地面风小)
        public float MinHeight;
        public float MaxHeight;
        public float WindNoiseScale;        // 噪声尺度
        public float WindNoiseSpeed;        // 噪声变化速度

        public static GlobalWindConfig Default => new()
        {
            BaseWind = new Vector3(1, 0, 0),
            BaseStrength = 5f,
            Turbulence = 0.3f,
            GustFrequency = 0.1f,
            GustStrength = 3f,
            GustDuration = 2f,
            HeightFalloff = 0.5f,
            MinHeight = 0f,
            MaxHeight = 50f,
            WindNoiseScale = 0.1f,
            WindNoiseSpeed = 0.5f,
        };
    }

    /// <summary>
    ///   风场系统。管理所有风区和受风物体。
    /// </summary>
    public sealed class WindFieldSystem : IDisposable
    {
        private readonly List<WindZone> _windZones = new();
        private readonly Dictionary<int, WindAffectedBody> _bodies = new();
        private GlobalWindConfig _globalConfig = GlobalWindConfig.Default;
        private float _time;
        private float _gustTimer;
        private float _currentGustStrength;
        private bool _disposed;
        private int _activeZoneCount;
        private float _totalForceApplied;

        // 噪声采样(简化)
        private readonly float[] _noiseTable = new float[256];
        private int _noiseOffset;

        public IReadOnlyList<WindZone> WindZones => _windZones;
        public GlobalWindConfig GlobalConfig
        {
            get => _globalConfig;
            set => _globalConfig = value;
        }
        public int BodyCount => _bodies.Count;
        public int ActiveZoneCount => _activeZoneCount;
        public float Time => _time;
        public float TotalForceApplied => _totalForceApplied;
        public Vector3 CurrentGlobalWind { get; private set; }

        public WindFieldSystem()
        {
            // 初始化噪声表
            var rng = new Random(42);
            for (int i = 0; i < _noiseTable.Length; i++)
            {
                _noiseTable[i] = (float)(rng.NextDouble() * 2 - 1);
            }
        }

        // ========================================================================================
        //  风区管理
        // ========================================================================================

        /// <summary>添加全局方向风。</summary>
        public WindZone AddDirectionalWind(Vector3 direction, float strength, float turbulence = 0.2f)
        {
            var zone = new WindZone
            {
                Id = _windZones.Count + 1,
                Name = $"DirectionalWind_{_windZones.Count + 1}",
                Type = WindZoneType.Directional,
                Direction = Vector3.Normalize(direction),
                Strength = strength,
                Turbulence = turbulence,
                Falloff = WindFalloffMode.Constant,
            };
            _windZones.Add(zone);
            return zone;
        }

        /// <summary>添加球形风区。</summary>
        public WindZone AddSphericalWind(Vector3 center, float radius, Vector3 direction, float strength,
            WindFalloffMode falloff = WindFalloffMode.Linear, float turbulence = 0.2f)
        {
            var zone = new WindZone
            {
                Id = _windZones.Count + 1,
                Name = $"SphericalWind_{_windZones.Count + 1}",
                Type = WindZoneType.Spherical,
                Position = center,
                Radius = radius,
                Direction = direction == Vector3.Zero ? Vector3.UnitX : Vector3.Normalize(direction),
                Strength = strength,
                Falloff = falloff,
                Turbulence = turbulence,
            };
            _windZones.Add(zone);
            return zone;
        }

        /// <summary>添加盒形风区。</summary>
        public WindZone AddBoxWind(Vector3 center, Vector3 size, Vector3 direction, float strength,
            float turbulence = 0.2f)
        {
            var zone = new WindZone
            {
                Id = _windZones.Count + 1,
                Name = $"BoxWind_{_windZones.Count + 1}",
                Type = WindZoneType.Box,
                Position = center,
                BoxSize = size,
                Direction = direction == Vector3.Zero ? Vector3.UnitX : Vector3.Normalize(direction),
                Strength = strength,
                Turbulence = turbulence,
            };
            _windZones.Add(zone);
            return zone;
        }

        /// <summary>添加旋风。</summary>
        public WindZone AddVortex(Vector3 center, float radius, float strength, float height,
            float rotationDirection = 1f, float inwardForce = 0.5f, float upwardForce = 1f)
        {
            var zone = new WindZone
            {
                Id = _windZones.Count + 1,
                Name = $"Vortex_{_windZones.Count + 1}",
                Type = WindZoneType.Vortex,
                Position = center,
                Radius = radius,
                Strength = strength,
                VortexConfig = new VortexConfig
                {
                    Height = height,
                    RotationDirection = rotationDirection,
                    InwardForce = inwardForce,
                    UpwardForce = upwardForce,
                },
            };
            _windZones.Add(zone);
            return zone;
        }

        /// <summary>添加风扇。</summary>
        public WindZone AddFan(Vector3 position, Vector3 direction, float range, float angle,
            float strength, float turbulence = 0.1f)
        {
            var zone = new WindZone
            {
                Id = _windZones.Count + 1,
                Name = $"Fan_{_windZones.Count + 1}",
                Type = WindZoneType.Fan,
                Position = position,
                Direction = direction == Vector3.Zero ? Vector3.UnitX : Vector3.Normalize(direction),
                FanRange = range,
                FanAngle = angle,
                Strength = strength,
                Turbulence = turbulence,
            };
            _windZones.Add(zone);
            return zone;
        }

        /// <summary>添加脉冲风(爆炸性气流)。</summary>
        public WindZone AddPulseWind(Vector3 center, float radius, Vector3 direction,
            float strength, float pulseStrength, float duration)
        {
            var zone = new WindZone
            {
                Id = _windZones.Count + 1,
                Name = $"PulseWind_{_windZones.Count + 1}",
                Type = WindZoneType.Pulse,
                Position = center,
                Radius = radius,
                Direction = direction == Vector3.Zero ? Vector3.UnitX : Vector3.Normalize(direction),
                Strength = strength,
                PulseStrength = pulseStrength,
                PulseDuration = duration,
                PulseTimer = duration,
            };
            _windZones.Add(zone);
            return zone;
        }

        /// <summary>移除风区。</summary>
        public bool RemoveWindZone(int zoneId)
        {
            int idx = _windZones.FindIndex(z => z.Id == zoneId);
            if (idx < 0) return false;
            _windZones.RemoveAt(idx);
            return true;
        }

        /// <summary>获取风区。</summary>
        public WindZone? GetWindZone(int zoneId)
        {
            return _windZones.Find(z => z.Id == zoneId);
        }

        /// <summary>清除所有风区。</summary>
        public void ClearWindZones()
        {
            _windZones.Clear();
        }

        // ========================================================================================
        //  物体管理
        // ========================================================================================

        /// <summary>注册受风物体。</summary>
        public void RegisterBody(WindAffectedBody body)
        {
            _bodies[body.BodyId] = body;
        }

        /// <summary>简化注册。</summary>
        public void RegisterBody(int bodyId, float mass, float dragCoef, float frontalArea,
            float windMultiplier = 1f, bool isWindSensitive = false)
        {
            _bodies[bodyId] = new WindAffectedBody
            {
                BodyId = bodyId,
                Mass = mass,
                DragCoefficient = dragCoef,
                FrontalArea = frontalArea,
                WindMultiplier = windMultiplier,
                IsWindSensitive = isWindSensitive,
                ShapeType = WindAffectedShapeType.Point,
            };
        }

        /// <summary>移除物体。</summary>
        public bool UnregisterBody(int bodyId)
        {
            return _bodies.Remove(bodyId);
        }

        /// <summary>获取物体。</summary>
        public WindAffectedBody? GetBody(int bodyId)
        {
            return _bodies.TryGetValue(bodyId, out var b) ? b : null;
        }

        // ========================================================================================
        //  主更新
        // ========================================================================================

        /// <summary>
        ///   每帧更新。
        /// </summary>
        public void Update(float dt, Func<int, (Vector3 pos, Quaternion rot, Vector3 vel)> getState,
            Action<int, Vector3, Vector3> applyForce)
        {
            if (_disposed) return;

            _time += dt;
            _totalForceApplied = 0;
            _activeZoneCount = 0;

            // 更新风区
            foreach (var z in _windZones)
            {
                z.Update(dt);
                if (z.IsEnabled) _activeZoneCount++;
            }

            // 更新阵风
            UpdateGusts(dt);

            // 计算全局风(含噪声)
            UpdateGlobalWind();

            // 对每个物体施加风力
            foreach (var kvp in _bodies)
            {
                int bodyId = kvp.Key;
                var body = kvp.Value;

                var state = getState(bodyId);
                Vector3 totalWind = ComputeWindAt(state.pos, _time);

                // 物体自身速度对相对风速的影响
                Vector3 relWind = totalWind - state.vel;

                // 算力
                Vector3 force = ComputeWindForce(body, relWind, state.rot);
                Vector3 torque = ComputeWindTorque(body, force, state.rot);

                applyForce(bodyId, force, torque);

                body.LastWindForce = force;
                body.LastWindSpeed = totalWind.Length();
                _totalForceApplied += force.Length();
            }
        }

        private void UpdateGusts(float dt)
        {
            _gustTimer -= dt;
            if (_gustTimer <= 0)
            {
                // 触发新阵风
                if (_globalConfig.GustFrequency > 0 && Random.Shared.NextDouble() < _globalConfig.GustFrequency * dt * 10f)
                {
                    _currentGustStrength = _globalConfig.GustStrength * (0.5f + Random.Shared.NextSingle());
                    _gustTimer = _globalConfig.GustDuration;
                }
                else
                {
                    _currentGustStrength = 0;
                    _gustTimer = 1f / MathF.Max(_globalConfig.GustFrequency, 0.01f);
                }
            }
            else
            {
                // 阵风衰减
                _currentGustStrength *= MathF.Pow(0.5f, dt);
            }
        }

        private void UpdateGlobalWind()
        {
            Vector3 baseWind = Vector3.Normalize(_globalConfig.BaseWind) * _globalConfig.BaseStrength;

            // 加噪声
            Vector3 noise = SampleNoise3D(new Vector3(_time * _globalConfig.WindNoiseSpeed, 0, 0),
                _globalConfig.WindNoiseScale) * _globalConfig.Turbulence * _globalConfig.BaseStrength;

            // 加阵风
            Vector3 gust = Vector3.Normalize(_globalConfig.BaseWind) * _currentGustStrength;

            CurrentGlobalWind = baseWind + noise + gust;
        }

        /// <summary>计算某点的总风速(全局+所有风区)。</summary>
        public Vector3 ComputeWindAt(Vector3 point, float time)
        {
            Vector3 wind = CurrentGlobalWind;

            // 高度衰减
            if (_globalConfig.HeightFalloff > 0)
            {
                float height = point.Y;
                if (height < _globalConfig.MinHeight)
                {
                    float factor = Math.Clamp(
                        height / _globalConfig.MinHeight, 0f, 1f);
                    factor = 1f - (1f - factor) * _globalConfig.HeightFalloff;
                    wind *= factor;
                }
                else if (height > _globalConfig.MaxHeight)
                {
                    // 高空风稍微增强
                    wind *= 1f + (height - _globalConfig.MaxHeight) * 0.01f * _globalConfig.HeightFalloff;
                }
            }

            // 叠加风区
            foreach (var zone in _windZones)
            {
                if (!zone.IsEnabled) continue;
                if (!zone.Contains(point)) continue;
                wind += zone.GetWindVector(point, time);
            }

            return wind;
        }

        /// <summary>计算风对物体的力。</summary>
        private Vector3 ComputeWindForce(WindAffectedBody body, Vector3 relWind, Quaternion rot)
        {
            // F = 0.5 * ρ * v² * Cd * A * direction
            const float airDensity = 1.225f; // kg/m³

            float speed = relWind.Length();
            if (speed < 0.01f) return Vector3.Zero;

            float area = body.FrontalArea;
            if (body.UseAABBProjection)
            {
                // 用AABB投影面积(简化)
                Vector3 localWind = Vector3.Transform(relWind, Quaternion.Inverse(rot));
                Vector3 absLocal = new(MathF.Abs(localWind.X), MathF.Abs(localWind.Y), MathF.Abs(localWind.Z));
                // 取最大两个轴的面积
                float xy = body.AABBHalfExtents.X * body.AABBHalfExtents.Y * 4;
                float xz = body.AABBHalfExtents.X * body.AABBHalfExtents.Z * 4;
                float yz = body.AABBHalfExtents.Y * body.AABBHalfExtents.Z * 4;
                // 加权:风沿X主要打YZ面
                area = (absLocal.X * yz + absLocal.Y * xz + absLocal.Z * xy) /
                       (absLocal.X + absLocal.Y + absLocal.Z + 1e-6f);
            }

            float forceMag = 0.5f * airDensity * speed * speed * body.DragCoefficient * area;
            forceMag *= body.WindMultiplier;

            // 风敏感物体额外加成
            if (body.IsWindSensitive)
            {
                forceMag *= Math.Clamp(50f / MathF.Max(body.Mass, 0.1f), 1f, 10f);
            }

            return Vector3.Normalize(relWind) * forceMag;
        }

        /// <summary>计算风对物体的扭矩(简化:力作用在质心上方)。</summary>
        private Vector3 ComputeWindTorque(WindAffectedBody body, Vector3 force, Quaternion rot)
        {
            // 简化: 假设力作用点在物体上方(产生翻倒力矩)
            Vector3 applicationPoint = Vector3.Transform(new Vector3(0, body.AABBHalfExtents.Y, 0), rot);
            Vector3 r = applicationPoint; // 相对质心
            return Vector3.Cross(r, force);
        }

        // ========================================================================================
        //  噪声
        // ========================================================================================

        private float SampleNoise1D(float x)
        {
            // 简化Perlin噪声
            float scaled = x * 0.1f;
            int i0 = (int)MathF.Floor(scaled) & 255;
            int i1 = (i0 + 1) & 255;
            float t = scaled - MathF.Floor(scaled);
            float fade = t * t * (3f - 2f * t);
            return _noiseTable[i0] * (1f - fade) + _noiseTable[i1] * fade;
        }

        private Vector3 SampleNoise3D(Vector3 p, float scale)
        {
            return new Vector3(
                SampleNoise1D(p.X * scale),
                SampleNoise1D(p.Y * scale + 100),
                SampleNoise1D(p.Z * scale + 200));
        }

        // ========================================================================================
        //  查询API
        // ========================================================================================

        /// <summary>获取某点的风速。</summary>
        public Vector3 GetWindAt(Vector3 point)
        {
            return ComputeWindAt(point, _time);
        }

        /// <summary>获取某点的风速强度。</summary>
        public float GetWindSpeedAt(Vector3 point)
        {
            return ComputeWindAt(point, _time).Length();
        }

        /// <summary>获取某点的风向(归一化)。</summary>
        public Vector3 GetWindDirectionAt(Vector3 point)
        {
            Vector3 w = ComputeWindAt(point, _time);
            return w.LengthSquared() > 1e-6f ? Vector3.Normalize(w) : Vector3.Zero;
        }

        /// <summary>触发某风区的脉冲。</summary>
        public void TriggerPulse(int zoneId, float strength, float duration)
        {
            var zone = GetWindZone(zoneId);
            if (zone != null && zone.Type == WindZoneType.Pulse)
            {
                zone.TriggerPulse(strength, duration);
            }
        }

        /// <summary>设置全局风。</summary>
        public void SetGlobalWind(Vector3 direction, float strength)
        {
            _globalConfig.BaseWind = direction == Vector3.Zero ? Vector3.UnitX : Vector3.Normalize(direction);
            _globalConfig.BaseStrength = strength;
        }

        // ========================================================================================
        //  预设
        // ========================================================================================

        /// <summary>应用预设: 晴天微风。</summary>
        public void ApplyPresetCalmBreeze()
        {
            ClearWindZones();
            _globalConfig = new GlobalWindConfig
            {
                BaseWind = new Vector3(1, 0, 0.3f),
                BaseStrength = 2f,
                Turbulence = 0.2f,
                GustFrequency = 0.05f,
                GustStrength = 1f,
                GustDuration = 1.5f,
                HeightFalloff = 0.3f,
            };
        }

        /// <summary>应用预设: 强风。</summary>
        public void ApplyPresetStrongWind()
        {
            ClearWindZones();
            _globalConfig = new GlobalWindConfig
            {
                BaseWind = new Vector3(1, 0, 0),
                BaseStrength = 12f,
                Turbulence = 0.6f,
                GustFrequency = 0.3f,
                GustStrength = 8f,
                GustDuration = 3f,
                HeightFalloff = 0.4f,
            };
        }

        /// <summary>应用预设: 暴风雨。</summary>
        public void ApplyPresetStorm()
        {
            ClearWindZones();
            _globalConfig = new GlobalWindConfig
            {
                BaseWind = new Vector3(1, -0.2f, 0.5f),
                BaseStrength = 25f,
                Turbulence = 0.9f,
                GustFrequency = 0.5f,
                GustStrength = 20f,
                GustDuration = 4f,
                HeightFalloff = 0.5f,
            };
        }

        /// <summary>应用预设: 龙卷风。</summary>
        public void ApplyPresetTornado(Vector3 center, float radius = 15f)
        {
            ClearWindZones();
            AddVortex(center, radius, strength: 30f, height: 30f,
                rotationDirection: 1f, inwardForce: 2f, upwardForce: 5f);
            _globalConfig = GlobalWindConfig.Default;
            _globalConfig.BaseStrength = 5f;
            _globalConfig.Turbulence = 0.8f;
        }

        // ========================================================================================
        //  统计/调试
        // ========================================================================================

        public void GetStats(out int zoneCount, out int bodyCount, out float totalForce,
            out Vector3 currentGlobalWind, out float time)
        {
            zoneCount = _windZones.Count;
            bodyCount = _bodies.Count;
            totalForce = _totalForceApplied;
            currentGlobalWind = CurrentGlobalWind;
            time = _time;
        }

        /// <summary>收集可视化数据。</summary>
        public void CollectVisualizationData(List<WindVisualizationData> output, Vector3 sampleCenter,
            Vector3 sampleSize, int resolution)
        {
            output.Clear();
            float dx = sampleSize.X / resolution;
            float dy = sampleSize.Y / resolution;
            float dz = sampleSize.Z / resolution;

            for (int x = 0; x < resolution; x++)
            {
                for (int y = 0; y < resolution; y++)
                {
                    for (int z = 0; z < resolution; z++)
                    {
                        Vector3 p = sampleCenter - sampleSize * 0.5f +
                                    new Vector3(x * dx, y * dy, z * dz);
                        Vector3 wind = ComputeWindAt(p, _time);
                        if (wind.LengthSquared() > 0.01f)
                        {
                            output.Add(new WindVisualizationData
                            {
                                Position = p,
                                WindVector = wind,
                                Strength = wind.Length(),
                            });
                        }
                    }
                }
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _windZones.Clear();
            _bodies.Clear();
        }
    }

    /// <summary>风可视化数据。</summary>
    public struct WindVisualizationData
    {
        public Vector3 Position;
        public Vector3 WindVector;
        public float Strength;
    }

    // ============================================================================================
    //  树叶/草等植被的风力影响
    //  植被一般不受物理引擎影响，但需要受风影响摆动。
    // ============================================================================================

    /// <summary>植被风影响数据。</summary>
    public struct VegetationWindData
    {
        public Vector3 Position;
        public float Stiffness;     // 刚度(越硬越不动)
        public float PhaseOffset;   // 相位偏移(让每棵树不同步)
        public float MaxBend;       // 最大弯曲角度
        public Vector3 CurrentBend;
    }

    /// <summary>植被风系统。</summary>
    public sealed class VegetationWindSystem
    {
        private readonly WindFieldSystem _wind;
        private readonly List<VegetationWindData> _vegetation = new();
        private float _time;

        public VegetationWindSystem(WindFieldSystem wind)
        {
            _wind = wind;
        }

        public void AddVegetation(Vector3 position, float stiffness, float phaseOffset, float maxBend)
        {
            _vegetation.Add(new VegetationWindData
            {
                Position = position,
                Stiffness = stiffness,
                PhaseOffset = phaseOffset,
                MaxBend = maxBend,
                CurrentBend = Vector3.Zero,
            });
        }

        public void Clear()
        {
            _vegetation.Clear();
        }

        public void Update(float dt)
        {
            _time += dt;
            for (int i = 0; i < _vegetation.Count; i++)
            {
                var v = _vegetation[i];
                Vector3 wind = _wind.GetWindAt(v.Position);

                // 目标弯曲方向(沿风向)
                Vector3 targetBend = wind / MathF.Max(v.Stiffness, 0.1f);
                // 限制最大弯曲
                float bendMag = targetBend.Length();
                if (bendMag > v.MaxBend)
                    targetBend = targetBend / bendMag * v.MaxBend;

                // 加摆动(自然感)
                float sway = MathF.Sin(_time * 2f + v.PhaseOffset) * 0.1f;
                targetBend += new Vector3(sway, 0, sway * 0.7f);

                // 平滑过渡
                v.CurrentBend = Vector3.Lerp(v.CurrentBend, targetBend, Math.Min(1f, dt * 5f));
                _vegetation[i] = v;
            }
        }

        public IReadOnlyList<VegetationWindData> Vegetation => _vegetation;
    }

    // ============================================================================================
    //  旗帜物理
    //  旗帜是受风影响的布料简化版。
    // ============================================================================================

    /// <summary>简化旗帜物理。</summary>
    public sealed class FlagPhysics
    {
        private readonly Vector3[] _points;
        private readonly Vector3[] _velocities;
        private readonly bool[] _pinned;
        private readonly float[] _stiffness;
        private readonly int _width;
        private readonly int _height;
        private readonly WindFieldSystem _wind;
        private float _time;

        public int Width => _width;
        public int Height => _height;
        public Vector3[] Points => _points;

        public FlagPhysics(int width, int height, Vector3 startPos, Vector3 flagDir, Vector3 upDir,
            WindFieldSystem wind)
        {
            _width = width;
            _height = height;
            _wind = wind;
            int count = width * height;
            _points = new Vector3[count];
            _velocities = new Vector3[count];
            _pinned = new bool[count];
            _stiffness = new float[count];

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int idx = y * width + x;
                    _points[idx] = startPos + flagDir * x * 0.1f + upDir * y * 0.1f;
                    _velocities[idx] = Vector3.Zero;
                    _pinned[idx] = x == 0; // 左边固定
                    _stiffness[idx] = 1f;
                }
            }
        }

        public void Update(float dt)
        {
            _time += dt;
            float damping = 0.95f;
            Vector3 gravity = new(0, -2f, 0); // 旗帜重力较小

            // 应用重力和风
            for (int i = 0; i < _points.Length; i++)
            {
                if (_pinned[i]) continue;
                Vector3 wind = _wind.GetWindAt(_points[i]);
                _velocities[i] += (wind * 0.5f + gravity) * dt;
                _velocities[i] *= damping;
            }

            // 距离约束(简化的布料)
            for (int iter = 0; iter < 3; iter++)
            {
                SatisfyConstraints();
            }

            // 积分位置
            for (int i = 0; i < _points.Length; i++)
            {
                if (_pinned[i]) continue;
                _points[i] += _velocities[i] * dt;
            }
        }

        private void SatisfyConstraints()
        {
            float restDist = 0.1f;
            // 水平
            for (int y = 0; y < _height; y++)
            {
                for (int x = 0; x < _width - 1; x++)
                {
                    int i0 = y * _width + x;
                    int i1 = y * _width + x + 1;
                    SatisfyDistance(i0, i1, restDist);
                }
            }
            // 垂直
            for (int y = 0; y < _height - 1; y++)
            {
                for (int x = 0; x < _width; x++)
                {
                    int i0 = y * _width + x;
                    int i1 = (y + 1) * _width + x;
                    SatisfyDistance(i0, i1, restDist);
                }
            }
        }

        private void SatisfyDistance(int i0, int i1, float restDist)
        {
            Vector3 delta = _points[i1] - _points[i0];
            float dist = delta.Length();
            if (dist < 1e-6f) return;
            float diff = (dist - restDist) / dist;
            Vector3 correction = delta * 0.5f * diff;

            if (!_pinned[i0]) _points[i0] += correction;
            if (!_pinned[i1]) _points[i1] -= correction;
        }

        /// <summary>设置固定点。</summary>
        public void SetPinned(int x, int y, bool pinned)
        {
            if (x >= 0 && x < _width && y >= 0 && y < _height)
                _pinned[y * _width + x] = pinned;
        }
    }
}
