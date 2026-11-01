using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace YamaEngine.Physics
{
    // ============================================================================================
    //  BuoyancySystem.cs
    //
    //  浮力系统。处理物体在水(或其他流体)里的浮力、阻力、波浪影响。
    //  原理就是阿基米德定律: 浮力 = 流体密度 * 重力加速度 * 排水体积
    //
    //  关键挑战:
    //    1) 算物体的排水体积 - 形状浸没在水面以下部分的体积
    //    2) 算浮力作用点 - 浸没部分的形心(浮心)
    //    3) 阻力 - 水对物体的阻力(线性+二次)
    //    4) 波浪 - 水面不是平的，有波动
    //    5) 流 - 水会流动，带着物体走
    //
    //  这玩意儿调起来挺烦的，特别是船那种东西，参数稍微变一下就翻船。
    //  TODO: 当前波浪是简化的sin叠加，正经实现要用FFT wave
    // ============================================================================================

    /// <summary>
    ///   流体类型。不同流体的密度和阻力差别很大。
    /// </summary>
    public enum FluidType
    {
        Water,          // 淡水 1000 kg/m³
        SeaWater,       // 海水 1025 kg/m³
        Oil,            // 油 ~900 kg/m³
        Lava,           // 岩浆 ~3100 kg/m³ (游戏里可以调)
        Mud,            // 泥浆 ~1700 kg/m³
        Acid,           // 酸液 ~1200 kg/m³
        Mercury,        // 水银 13546 kg/m³
        Custom,         // 自定义
    }

    /// <summary>
    ///   流体属性。
    /// </summary>
    public struct FluidProperties
    {
        public FluidType Type;
        public float Density;           // kg/m³
        public float LinearDrag;        // 线性阻力系数
        public float QuadraticDrag;     // 二次阻力系数
        public float AngularDrag;       // 角阻力系数
        public float Viscosity;         // 粘度(影响阻力)
        public float SurfaceTension;    // 表面张力(简化)
        public float FlowVelocity;      // 流速(简化:单方向)
        public Vector3 FlowDirection;   // 流向
        public float WaveHeight;        // 波浪高度
        public float WaveFrequency;     // 波浪频率
        public float WaveSpeed;         // 波浪传播速度
        public bool HasWaves;
        public float Depth;             // 流体深度(无限=负值)
        public float DamagePerSecond;   // 伤害(岩浆/酸液)

        public static FluidProperties Water => new()
        {
            Type = FluidType.Water,
            Density = 1000f,
            LinearDrag = 0.5f,
            QuadraticDrag = 0.8f,
            AngularDrag = 0.5f,
            Viscosity = 0.001f,
            SurfaceTension = 0.0728f,
            FlowVelocity = 0,
            FlowDirection = Vector3.UnitX,
            WaveHeight = 0.3f,
            WaveFrequency = 0.5f,
            WaveSpeed = 1.5f,
            HasWaves = true,
            Depth = -1f,
            DamagePerSecond = 0,
        };

        public static FluidProperties SeaWater => new()
        {
            Type = FluidType.SeaWater,
            Density = 1025f,
            LinearDrag = 0.6f,
            QuadraticDrag = 1.0f,
            AngularDrag = 0.6f,
            Viscosity = 0.0012f,
            SurfaceTension = 0.075f,
            FlowVelocity = 0.5f,
            FlowDirection = Vector3.UnitX,
            WaveHeight = 0.8f,
            WaveFrequency = 0.3f,
            WaveSpeed = 2f,
            HasWaves = true,
            Depth = -1f,
            DamagePerSecond = 0,
        };

        public static FluidProperties Oil => new()
        {
            Type = FluidType.Oil,
            Density = 900f,
            LinearDrag = 1.5f,
            QuadraticDrag = 1.5f,
            AngularDrag = 1.2f,
            Viscosity = 0.1f,
            SurfaceTension = 0.03f,
            FlowVelocity = 0,
            FlowDirection = Vector3.UnitX,
            WaveHeight = 0.1f,
            WaveFrequency = 0.3f,
            WaveSpeed = 1f,
            HasWaves = true,
            Depth = -1f,
            DamagePerSecond = 0,
        };

        public static FluidProperties Lava => new()
        {
            Type = FluidType.Lava,
            Density = 3100f,
            LinearDrag = 5f,
            QuadraticDrag = 4f,
            AngularDrag = 4f,
            Viscosity = 1f,
            SurfaceTension = 0.4f,
            FlowVelocity = 0.2f,
            FlowDirection = Vector3.UnitX,
            WaveHeight = 0.2f,
            WaveFrequency = 0.2f,
            WaveSpeed = 0.5f,
            HasWaves = true,
            Depth = -1f,
            DamagePerSecond = 50f,
        };

        public static FluidProperties Mud => new()
        {
            Type = FluidType.Mud,
            Density = 1700f,
            LinearDrag = 8f,
            QuadraticDrag = 6f,
            AngularDrag = 6f,
            Viscosity = 0.5f,
            SurfaceTension = 0.01f,
            FlowVelocity = 0.05f,
            FlowDirection = Vector3.UnitX,
            WaveHeight = 0.05f,
            WaveFrequency = 0.1f,
            WaveSpeed = 0.3f,
            HasWaves = false,
            Depth = -1f,
            DamagePerSecond = 0,
        };

        public static FluidProperties Acid => new()
        {
            Type = FluidType.Acid,
            Density = 1200f,
            LinearDrag = 0.8f,
            QuadraticDrag = 1.0f,
            AngularDrag = 0.8f,
            Viscosity = 0.0015f,
            SurfaceTension = 0.05f,
            FlowVelocity = 0,
            FlowDirection = Vector3.UnitX,
            WaveHeight = 0.2f,
            WaveFrequency = 0.4f,
            WaveSpeed = 1f,
            HasWaves = true,
            Depth = -1f,
            DamagePerSecond = 25f,
        };

        public static FluidProperties Mercury => new()
        {
            Type = FluidType.Mercury,
            Density = 13546f,
            LinearDrag = 0.3f,
            QuadraticDrag = 0.5f,
            AngularDrag = 0.3f,
            Viscosity = 0.0015f,
            SurfaceTension = 0.5f,
            FlowVelocity = 0,
            FlowDirection = Vector3.UnitX,
            WaveHeight = 0.05f,
            WaveFrequency = 0.6f,
            WaveSpeed = 0.8f,
            HasWaves = true,
            Depth = -1f,
            DamagePerSecond = 0,
        };

        public static FluidProperties Get(FluidType type) => type switch
        {
            FluidType.Water => Water,
            FluidType.SeaWater => SeaWater,
            FluidType.Oil => Oil,
            FluidType.Lava => Lava,
            FluidType.Mud => Mud,
            FluidType.Acid => Acid,
            FluidType.Mercury => Mercury,
            _ => Water,
        };
    }

    /// <summary>
    ///   水体定义。一个水体是一个有边界的流体区域。
    /// </summary>
    public sealed class WaterBody
    {
        public int Id;
        public string Name = "";
        public FluidProperties Fluid;
        public Vector3 Center;          // 水体中心
        public Vector3 HalfExtents;     // 包围盒半尺寸(长方体水体)
        public float BaseWaterLevel;    // 基准水面高度(Y)
        public bool IsInfinite;         // 无限大水体(海洋)
        public float CurrentTime;       // 内部时间(波浪相位)
        public Vector4 Color = new(0.1f, 0.3f, 0.7f, 0.7f);
        public Vector4 DeepColor = new(0.01f, 0.05f, 0.2f, 0.9f);
        public bool IsEnabled = true;
        public float WaveAmplitudeMultiplier = 1f;
        public List<BuoyancyForceField>? LocalCurrents;  // 局部水流

        public WaterBody()
        {
            Fluid = FluidProperties.Water;
        }

        /// <summary>检查点是否在水体XZ范围内。</summary>
        public bool IsInHorizontalRange(Vector3 point)
        {
            if (IsInfinite) return true;
            Vector3 d = point - Center;
            return MathF.Abs(d.X) <= HalfExtents.X && MathF.Abs(d.Z) <= HalfExtents.Z;
        }

        /// <summary>检查点是否在水体内(包括深度)。</summary>
        public bool Contains(Vector3 point)
        {
            if (!IsInHorizontalRange(point)) return false;
            if (Fluid.Depth > 0)
            {
                return point.Y <= GetSurfaceHeight(point) && point.Y >= Center.Y - Fluid.Depth;
            }
            return point.Y <= GetSurfaceHeight(point);
        }

        /// <summary>获取某点的水面高度(考虑波浪)。</summary>
        public float GetSurfaceHeight(Vector3 point)
        {
            if (!Fluid.HasWaves) return BaseWaterLevel;

            float h = BaseWaterLevel;
            // 用多个sin波叠加模拟波浪
            // 这种Gerstner wave的简化版
            float t = CurrentTime * Fluid.WaveSpeed;
            float x = point.X, z = point.Z;

            // 主波
            h += MathF.Sin(x * Fluid.WaveFrequency + t) * Fluid.WaveHeight * WaveAmplitudeMultiplier;
            // 次波(垂直方向)
            h += MathF.Sin(z * Fluid.WaveFrequency * 0.7f + t * 1.3f) * Fluid.WaveHeight * 0.6f * WaveAmplitudeMultiplier;
            // 细节波
            h += MathF.Sin((x + z) * Fluid.WaveFrequency * 2.5f + t * 2f) * Fluid.WaveHeight * 0.2f * WaveAmplitudeMultiplier;

            return h;
        }

        /// <summary>获取某点的水面法线(数值微分)。</summary>
        public Vector3 GetSurfaceNormal(Vector3 point)
        {
            if (!Fluid.HasWaves) return Vector3.UnitY;
            float eps = 0.1f;
            float hL = GetSurfaceHeight(point - new Vector3(eps, 0, 0));
            float hR = GetSurfaceHeight(point + new Vector3(eps, 0, 0));
            float hD = GetSurfaceHeight(point - new Vector3(0, 0, eps));
            float hU = GetSurfaceHeight(point + new Vector3(0, 0, eps));
            Vector3 normal = new Vector3(hL - hR, 2f * eps, hD - hU);
            return Vector3.Normalize(normal);
        }

        /// <summary>获取某点的流速。</summary>
        public Vector3 GetFlowVelocity(Vector3 point)
        {
            Vector3 flow = Fluid.FlowDirection * Fluid.FlowVelocity;

            // 局部水流
            if (LocalCurrents != null)
            {
                foreach (var current in LocalCurrents)
                {
                    float distSq = Vector3.DistanceSquared(point, current.Position);
                    if (distSq < current.Radius * current.Radius)
                    {
                        float falloff = 1f - MathF.Sqrt(distSq) / current.Radius;
                        flow += current.Direction * current.Strength * falloff;
                    }
                }
            }

            return flow;
        }

        /// <summary>获取某点的流体深度(从水面到水底)。</summary>
        public float GetDepthAt(Vector3 point)
        {
            if (!IsInHorizontalRange(point)) return 0;
            float surface = GetSurfaceHeight(point);
            if (Fluid.Depth > 0)
            {
                return Math.Max(0, Fluid.Depth - (surface - point.Y));
            }
            return float.MaxValue; // 无限深度
        }

        /// <summary>获取某点的浸没系数(0=水面, 1=完全浸没)。</summary>
        public float GetSubmersion(Vector3 point)
        {
            if (!IsInHorizontalRange(point)) return 0;
            float surface = GetSurfaceHeight(point);
            if (point.Y >= surface) return 0;
            if (Fluid.Depth <= 0) return 1;
            float bottom = Center.Y - Fluid.Depth;
            if (point.Y <= bottom) return 1;
            return (surface - point.Y) / (surface - bottom);
        }

        public void Update(float dt)
        {
            CurrentTime += dt;
        }
    }

    /// <summary>局部水流(涡流、瀑布)。</summary>
    public struct BuoyancyForceField
    {
        public Vector3 Position;
        public float Radius;
        public Vector3 Direction;
        public float Strength;
        public BuoyancyForceFieldType Type;
    }

    /// <summary>力场类型。</summary>
    public enum BuoyancyForceFieldType
    {
        Current,        // 水流
        Whirlpool,      // 漩涡
        Geyser,         // 喷泉(向上)
        Vortex,         // 涡流
    }

    /// <summary>
    ///   浮力物体。需要施加浮力的物体。
    /// </summary>
    public struct BuoyantBody
    {
        public int BodyId;
        public float Mass;
        public float Volume;            // 物体总体积
        public Vector3 CenterOfMass;    // 质心(物体局部系)
        public Vector3 CenterOfBuoyancy;// 浮心(物体局部系，浸没部分的形心)
        public float SubmersionDepth;   // 当前浸没深度(0~1)
        public bool IsFloating;
        public float BuoyancyForce;
        public Vector3 LastForceApplied;
        public Vector3 LastTorqueApplied;
        public BuoyancyShapeType ShapeType;
        public float SphereRadius;
        public Vector3 BoxHalfExtents;
        public float CapsuleRadius;
        public float CapsuleHeight;
        public float CustomBuoyancyFactor;  // 自定义浮力倍率(船>1, 重物<1)
        public float WaterDragMultiplier;   // 水阻力倍率
        public bool CanFloat;
        public float FloatLevel;        // 期望浮在水面下的比例(0~1)

        public float EffectiveDensity => Mass / MathF.Max(Volume, 1e-6f);
    }

    /// <summary>浮力形状类型。</summary>
    public enum BuoyancyShapeType
    {
        Sphere,
        Box,
        Capsule,
        Cylinder,
        Mesh,           // 复杂网格(需要采样)
        Compound,
    }

    /// <summary>
    ///   浮力采样点。对复杂形状，用多个采样点近似。
    /// </summary>
    public struct BuoyancySamplePoint
    {
        public Vector3 LocalPosition;   // 物体局部系位置
        public float Volume;            // 该采样点代表的体积
        public float Submersion;        // 当前浸没系数
    }

    /// <summary>
    ///   浮力系统。管理所有水体和浮力物体。
    /// </summary>
    public sealed class BuoyancySystem : IDisposable
    {
        private readonly List<WaterBody> _waterBodies = new();
        private readonly Dictionary<int, BuoyantBody> _bodies = new();
        private readonly Dictionary<int, List<BuoyancySamplePoint>> _samplePoints = new();
        private readonly Dictionary<int, int> _bodyWaterMap = new(); // bodyId -> waterId
        private Vector3 _gravity = new(0, -9.81f, 0);
        private bool _disposed;
        private float _totalForceApplied;
        private float _totalTorqueApplied;
        private int _activeBodyCount;

        public IReadOnlyList<WaterBody> WaterBodies => _waterBodies;
        public int BodyCount => _bodies.Count;
        public int ActiveBodyCount => _activeBodyCount;
        public Vector3 Gravity
        {
            get => _gravity;
            set => _gravity = value;
        }

        // ========================================================================================
        //  水体管理
        // ========================================================================================

        /// <summary>添加水体。</summary>
        public WaterBody AddWaterBody(FluidType fluidType, Vector3 center, Vector3 halfExtents, float baseLevel)
        {
            var body = new WaterBody
            {
                Id = _waterBodies.Count + 1,
                Name = $"Water_{_waterBodies.Count + 1}",
                Fluid = FluidProperties.Get(fluidType),
                Center = center,
                HalfExtents = halfExtents,
                BaseWaterLevel = baseLevel,
            };
            _waterBodies.Add(body);
            return body;
        }

        /// <summary>添加无限水体(海洋)。</summary>
        public WaterBody AddOcean(FluidType fluidType, float baseLevel)
        {
            var body = new WaterBody
            {
                Id = _waterBodies.Count + 1,
                Name = $"Ocean_{_waterBodies.Count + 1}",
                Fluid = FluidProperties.Get(fluidType),
                Center = new Vector3(0, baseLevel, 0),
                HalfExtents = new Vector3(float.MaxValue, 0, float.MaxValue),
                BaseWaterLevel = baseLevel,
                IsInfinite = true,
            };
            _waterBodies.Add(body);
            return body;
        }

        /// <summary>移除水体。</summary>
        public bool RemoveWaterBody(int waterId)
        {
            int idx = _waterBodies.FindIndex(w => w.Id == waterId);
            if (idx < 0) return false;
            _waterBodies.RemoveAt(idx);
            // 清理body-water映射
            var keysToRemove = new List<int>();
            foreach (var kvp in _bodyWaterMap)
            {
                if (kvp.Value == waterId)
                    keysToRemove.Add(kvp.Key);
            }
            foreach (var k in keysToRemove)
                _bodyWaterMap.Remove(k);
            return true;
        }

        /// <summary>获取某点所在的水体。</summary>
        public WaterBody? GetWaterBodyAt(Vector3 point)
        {
            // 返回第一个包含该点的水体
            foreach (var w in _waterBodies)
            {
                if (!w.IsEnabled) continue;
                if (w.Contains(point)) return w;
            }
            return null;
        }

        /// <summary>获取某点的水面高度(取最高)。</summary>
        public float GetSurfaceHeightAt(Vector3 point)
        {
            float max = float.MinValue;
            bool found = false;
            foreach (var w in _waterBodies)
            {
                if (!w.IsEnabled) continue;
                if (w.IsInHorizontalRange(point))
                {
                    float h = w.GetSurfaceHeight(point);
                    if (h > max) max = h;
                    found = true;
                }
            }
            return found ? max : float.MinValue;
        }

        /// <summary>检查点是否在水下。</summary>
        public bool IsUnderwater(Vector3 point)
        {
            foreach (var w in _waterBodies)
            {
                if (!w.IsEnabled) continue;
                if (w.Contains(point)) return true;
            }
            return false;
        }

        /// <summary>获取点的浸没深度。</summary>
        public float GetSubmersionAt(Vector3 point)
        {
            float max = 0;
            foreach (var w in _waterBodies)
            {
                if (!w.IsEnabled) continue;
                float s = w.GetSubmersion(point);
                if (s > max) max = s;
            }
            return max;
        }

        // ========================================================================================
        //  物体管理
        // ========================================================================================

        /// <summary>注册一个浮力物体。</summary>
        public void RegisterBody(BuoyantBody body)
        {
            _bodies[body.BodyId] = body;
            if (!_samplePoints.ContainsKey(body.BodyId))
                _samplePoints[body.BodyId] = new List<BuoyancySamplePoint>();
        }

        /// <summary>注册一个简化形状的物体。</summary>
        public void RegisterBody(int bodyId, float mass, float volume, BuoyancyShapeType shapeType,
            float radius = 0, Vector3 boxHalfExtents = default, float capsuleHeight = 0,
            float buoyancyFactor = 1f)
        {
            var body = new BuoyantBody
            {
                BodyId = bodyId,
                Mass = mass,
                Volume = volume,
                ShapeType = shapeType,
                SphereRadius = radius,
                BoxHalfExtents = boxHalfExtents,
                CapsuleRadius = radius,
                CapsuleHeight = capsuleHeight,
                CustomBuoyancyFactor = buoyancyFactor,
                WaterDragMultiplier = 1f,
                CanFloat = true,
                FloatLevel = 0.5f,
            };
            RegisterBody(body);
        }

        /// <summary>为复杂形状生成采样点。</summary>
        public void GenerateSamplePoints(int bodyId, int sampleCount)
        {
            if (!_bodies.TryGetValue(bodyId, out var body)) return;
            var points = new List<BuoyancySamplePoint>(sampleCount);
            float volumePerSample = body.Volume / sampleCount;

            switch (body.ShapeType)
            {
                case BuoyancyShapeType.Sphere:
                    for (int i = 0; i < sampleCount; i++)
                    {
                        // 球内均匀采样
                        Vector3 dir = new(
                            (Random.Shared.NextSingle() * 2 - 1),
                            (Random.Shared.NextSingle() * 2 - 1),
                            (Random.Shared.NextSingle() * 2 - 1));
                        if (dir.LengthSquared() > 1) dir = Vector3.Normalize(dir);
                        float r = body.SphereRadius * MathF.Pow(Random.Shared.NextSingle(), 1f / 3f);
                        points.Add(new BuoyancySamplePoint
                        {
                            LocalPosition = dir * r,
                            Volume = volumePerSample,
                        });
                    }
                    break;

                case BuoyancyShapeType.Box:
                    for (int i = 0; i < sampleCount; i++)
                    {
                        Vector3 pos = new(
                            (Random.Shared.NextSingle() * 2 - 1) * body.BoxHalfExtents.X,
                            (Random.Shared.NextSingle() * 2 - 1) * body.BoxHalfExtents.Y,
                            (Random.Shared.NextSingle() * 2 - 1) * body.BoxHalfExtents.Z);
                        points.Add(new BuoyancySamplePoint
                        {
                            LocalPosition = pos,
                            Volume = volumePerSample,
                        });
                    }
                    break;

                case BuoyancyShapeType.Capsule:
                    for (int i = 0; i < sampleCount; i++)
                    {
                        // 简化:在胶囊内随机
                        float t = Random.Shared.NextSingle();
                        Vector3 axisPos = new(0, (t - 0.5f) * body.CapsuleHeight, 0);
                        Vector3 dir = new(
                            (Random.Shared.NextSingle() * 2 - 1),
                            (Random.Shared.NextSingle() * 2 - 1),
                            (Random.Shared.NextSingle() * 2 - 1));
                        if (dir.LengthSquared() > 1) dir = Vector3.Normalize(dir);
                        Vector3 pos = axisPos + dir * body.CapsuleRadius * MathF.Pow(Random.Shared.NextSingle(), 1f / 3f);
                        points.Add(new BuoyancySamplePoint
                        {
                            LocalPosition = pos,
                            Volume = volumePerSample,
                        });
                    }
                    break;

                default:
                    // 用中心点
                    points.Add(new BuoyancySamplePoint
                    {
                        LocalPosition = Vector3.Zero,
                        Volume = body.Volume,
                    });
                    break;
            }

            _samplePoints[bodyId] = points;
        }

        /// <summary>移除物体。</summary>
        public bool UnregisterBody(int bodyId)
        {
            bool removed = _bodies.Remove(bodyId);
            _samplePoints.Remove(bodyId);
            _bodyWaterMap.Remove(bodyId);
            return removed;
        }

        /// <summary>获取物体。</summary>
        public BuoyantBody? GetBody(int bodyId)
        {
            return _bodies.TryGetValue(bodyId, out var b) ? b : null;
        }

        /// <summary>更新物体状态(由外部物理引擎调用)。</summary>
        public void UpdateBodyState(int bodyId, Vector3 position, Quaternion orientation,
            Vector3 linearVel, Vector3 angularVel)
        {
            if (!_bodies.TryGetValue(bodyId, out var body)) return;
            // 这里的position和orientation是外部物理引擎的，我们只读取不写
            // 实际的力施加是通过ApplyForce callback
        }

        // ========================================================================================
        //  主更新
        // ========================================================================================

        /// <summary>
        ///   每帧更新。需要传入每个物体的位置/朝向/速度，返回施加的力/扭矩。
        /// </summary>
        public void Update(float dt, Func<int, (Vector3 pos, Quaternion rot, Vector3 linVel, Vector3 angVel)> getState,
            Action<int, Vector3, Vector3> applyForce)
        {
            if (_disposed) return;

            _totalForceApplied = 0;
            _totalTorqueApplied = 0;
            _activeBodyCount = 0;

            // 更新水体时间
            foreach (var w in _waterBodies)
                w.Update(dt);

            // 对每个物体施加浮力
            foreach (var kvp in _bodies)
            {
                int bodyId = kvp.Key;
                var body = kvp.Value;
                if (!body.CanFloat) continue;

                var state = getState(bodyId);
                Vector3 force = Vector3.Zero;
                Vector3 torque = Vector3.Zero;

                ComputeBuoyancy(body, state.pos, state.rot, ref force, ref torque);
                ComputeDrag(body, state.pos, state.rot, state.linVel, state.angVel, ref force, ref torque);
                ComputeFlow(body, state.pos, state.rot, state.linVel, ref force, ref torque);

                applyForce(bodyId, force, torque);

                body.LastForceApplied = force;
                body.LastTorqueApplied = torque;
                _totalForceApplied += force.Length();
                _totalTorqueApplied += torque.Length();

                if (body.SubmersionDepth > 0.01f)
                    _activeBodyCount++;
            }
        }

        // ========================================================================================
        //  浮力计算
        // ========================================================================================

        private void ComputeBuoyancy(BuoyantBody body, Vector3 pos, Quaternion rot,
            ref Vector3 force, ref Vector3 torque)
        {
            // 找到物体所在的水体
            WaterBody? water = null;
            if (_bodyWaterMap.TryGetValue(body.BodyId, out int cachedWaterId))
            {
                water = _waterBodies.Find(w => w.Id == cachedWaterId);
                if (water != null && !water.Contains(pos))
                    water = null;
            }
            if (water == null)
            {
                water = GetWaterBodyAt(pos);
                if (water != null)
                    _bodyWaterMap[body.BodyId] = water.Id;
                else
                {
                    body.SubmersionDepth = 0;
                    body.IsFloating = false;
                    return;
                }
            }

            var fluid = water.Fluid;
            float gravityMag = _gravity.Length();

            // 简单形状: 直接算浸没体积
            if (_samplePoints.TryGetValue(body.BodyId, out var samples) && samples.Count > 0)
            {
                // 采样点法
                Vector3 buoyancyForce = Vector3.Zero;
                Vector3 buoyancyCenter = Vector3.Zero;
                float totalSubmergedVolume = 0;

                foreach (var sp in samples)
                {
                    Vector3 worldPos = pos + Vector3.Transform(sp.LocalPosition, rot);
                    float submersion = water.GetSubmersion(worldPos);
                    if (submersion <= 0) continue;

                    float submergedVol = sp.Volume * submersion;
                    totalSubmergedVolume += submergedVol;
                    buoyancyCenter += worldPos * submergedVol;

                    // 浮力 = 流体密度 * g * 排水体积
                    float f = fluid.Density * gravityMag * submergedVol * body.CustomBuoyancyFactor;
                    // 浮力方向: 和重力相反
                    buoyancyForce -= _gravity / gravityMag * f;
                }

                if (totalSubmergedVolume > 0)
                {
                    buoyancyCenter /= totalSubmergedVolume;
                    force += buoyancyForce;
                    // 扭矩 = r x F (r = buoyancyCenter - centerOfMass)
                    Vector3 r = buoyancyCenter - (pos + Vector3.Transform(body.CenterOfMass, rot));
                    torque += Vector3.Cross(r, buoyancyForce);

                    body.SubmersionDepth = totalSubmergedVolume / body.Volume;
                    body.CenterOfBuoyancy = Vector3.Transform(buoyancyCenter - pos, Quaternion.Inverse(rot));
                    body.BuoyancyForce = buoyancyForce.Length();
                    body.IsFloating = MathF.Abs(body.BuoyancyForce - body.Mass * gravityMag) < body.Mass * gravityMag * 0.1f;
                }
                else
                {
                    body.SubmersionDepth = 0;
                    body.IsFloating = false;
                }
            }
            else
            {
                // 简化: 用物体中心点的浸没度
                float submersion = water.GetSubmersion(pos);
                if (submersion <= 0)
                {
                    body.SubmersionDepth = 0;
                    body.IsFloating = false;
                    return;
                }

                float submergedVol = body.Volume * submersion;
                float f = fluid.Density * gravityMag * submergedVol * body.CustomBuoyancyFactor;
                Vector3 buoyancyForce = -_gravity / gravityMag * f;
                force += buoyancyForce;

                // 浮心简化为物体中心
                body.SubmersionDepth = submersion;
                body.BuoyancyForce = buoyancyForce.Length();
                body.IsFloating = MathF.Abs(body.BuoyancyForce - body.Mass * gravityMag) < body.Mass * gravityMag * 0.1f;
            }
        }

        // ========================================================================================
        //  阻力计算
        // ========================================================================================

        private void ComputeDrag(BuoyantBody body, Vector3 pos, Quaternion rot,
            Vector3 linVel, Vector3 angVel, ref Vector3 force, ref Vector3 torque)
        {
            if (body.SubmersionDepth <= 0) return;

            WaterBody? water = GetWaterBodyAt(pos);
            if (water == null) return;
            var fluid = water.Fluid;

            float submersion = body.SubmersionDepth;

            // 线性阻力: F = -c1 * v
            Vector3 linDragForce = -linVel * fluid.LinearDrag * submersion * body.WaterDragMultiplier;
            // 二次阻力: F = -c2 * |v| * v
            float speed = linVel.Length();
            if (speed > 0.01f)
            {
                linDragForce -= Vector3.Normalize(linVel) * speed * speed *
                                fluid.QuadraticDrag * submersion * body.WaterDragMultiplier;
            }
            force += linDragForce;

            // 角阻力: T = -c * w
            Vector3 angDragTorque = -angVel * fluid.AngularDrag * submersion * body.WaterDragMultiplier;
            torque += angDragTorque;

            // 粘性阻力(简化): 对旋转的影响
            if (fluid.Viscosity > 0)
            {
                torque -= angVel * fluid.Viscosity * submersion * 100f * body.WaterDragMultiplier;
            }
        }

        // ========================================================================================
        //  水流影响
        // ========================================================================================

        private void ComputeFlow(BuoyantBody body, Vector3 pos, Quaternion rot,
            Vector3 linVel, ref Vector3 force, ref Vector3 torque)
        {
            if (body.SubmersionDepth <= 0) return;

            WaterBody? water = GetWaterBodyAt(pos);
            if (water == null) return;

            Vector3 flowVel = water.GetFlowVelocity(pos);
            if (flowVel == Vector3.Zero) return;

            // 流对物体的推力: F = (v_flow - v_body) * drag * submersion
            Vector3 relVel = flowVel - linVel;
            force += relVel * water.Fluid.LinearDrag * body.SubmersionDepth * body.WaterDragMultiplier * 0.5f;

            // 局部水流(漩涡等)
            if (water.LocalCurrents != null)
            {
                foreach (var current in water.LocalCurrents)
                {
                    float distSq = Vector3.DistanceSquared(pos, current.Position);
                    if (distSq > current.Radius * current.Radius) continue;
                    float falloff = 1f - MathF.Sqrt(distSq) / current.Radius;

                    switch (current.Type)
                    {
                        case BuoyancyForceFieldType.Current:
                            force += current.Direction * current.Strength * falloff * body.SubmersionDepth;
                            break;
                        case BuoyancyForceFieldType.Whirlpool:
                            // 漩涡: 向中心拉 + 切向旋转
                            Vector3 toCenter = current.Position - pos;
                            toCenter.Y = 0;
                            float d = toCenter.Length();
                            if (d > 0.1f)
                            {
                                toCenter /= d;
                                force += toCenter * current.Strength * falloff * body.SubmersionDepth;
                                // 切向力(垂直于半径)
                                Vector3 tangent = new(-toCenter.Z, 0, toCenter.X);
                                force += tangent * current.Strength * 0.5f * falloff * body.SubmersionDepth;
                            }
                            break;
                        case BuoyancyForceFieldType.Geyser:
                            // 喷泉: 向上推
                            force += Vector3.UnitY * current.Strength * falloff * body.SubmersionDepth;
                            break;
                        case BuoyancyForceFieldType.Vortex:
                            // 涡流: 三维螺旋
                            Vector3 toVortex = current.Position - pos;
                            if (toVortex.LengthSquared() > 0.01f)
                            {
                                Vector3 axis = Vector3.Normalize(current.Direction);
                                Vector3 radial = toVortex - axis * Vector3.Dot(toVortex, axis);
                                if (radial.LengthSquared() > 0.01f)
                                {
                                    radial = Vector3.Normalize(radial);
                                    Vector3 tangent2 = Vector3.Cross(axis, radial);
                                    force += tangent2 * current.Strength * falloff * body.SubmersionDepth;
                                    force += radial * current.Strength * 0.3f * falloff * body.SubmersionDepth;
                                }
                            }
                            break;
                    }
                }
            }
        }

        // ========================================================================================
        //  查询API
        // ========================================================================================

        /// <summary>获取某点的流体属性。</summary>
        public FluidProperties? GetFluidAt(Vector3 point)
        {
            var w = GetWaterBodyAt(point);
            return w?.Fluid;
        }

        /// <summary>获取某点的流速。</summary>
        public Vector3 GetFlowAt(Vector3 point)
        {
            var w = GetWaterBodyAt(point);
            return w?.GetFlowVelocity(point) ?? Vector3.Zero;
        }

        /// <summary>获取某点的水面法线。</summary>
        public Vector3 GetSurfaceNormalAt(Vector3 point)
        {
            foreach (var w in _waterBodies)
            {
                if (!w.IsEnabled) continue;
                if (w.IsInHorizontalRange(point))
                    return w.GetSurfaceNormal(point);
            }
            return Vector3.UnitY;
        }

        /// <summary>射线投射到水面。</summary>
        public bool RaycastSurface(Vector3 origin, Vector3 direction, float maxDistance, out Vector3 hitPoint)
        {
            hitPoint = origin;
            Vector3 normalizedDir = direction.LengthSquared() > 1e-6f ? Vector3.Normalize(direction) : direction;

            // 步进采样
            float step = 0.1f;
            for (float t = 0; t < maxDistance; t += step)
            {
                Vector3 p = origin + normalizedDir * t;
                foreach (var w in _waterBodies)
                {
                    if (!w.IsEnabled) continue;
                    if (w.IsInHorizontalRange(p))
                    {
                        float surface = w.GetSurfaceHeight(p);
                        // 检查射线是否穿过水面
                        Vector3 prevP = origin + normalizedDir * MathF.Max(0, t - step);
                        float prevSurface = w.GetSurfaceHeight(prevP);
                        bool wasAbove = prevP.Y > prevSurface;
                        bool isBelow = p.Y < surface;
                        if (wasAbove && isBelow)
                        {
                            // 插值
                            float ratio = (prevSurface - prevP.Y) / (p.Y - prevP.Y);
                            hitPoint = Vector3.Lerp(prevP, p, ratio);
                            return true;
                        }
                    }
                }
            }
            return false;
        }

        // ========================================================================================
        //  统计/调试
        // ========================================================================================

        public float TotalForceApplied => _totalForceApplied;
        public float TotalTorqueApplied => _totalTorqueApplied;

        public void GetStats(out int waterBodyCount, out int bodyCount, out int activeBodies,
            out float totalForce, out float totalTorque)
        {
            waterBodyCount = _waterBodies.Count;
            bodyCount = _bodies.Count;
            activeBodies = _activeBodyCount;
            totalForce = _totalForceApplied;
            totalTorque = _totalTorqueApplied;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _waterBodies.Clear();
            _bodies.Clear();
            _samplePoints.Clear();
            _bodyWaterMap.Clear();
        }
    }

    // ============================================================================================
    //  船舶物理
    //  船是一种特殊的浮力物体，需要考虑稳定性、侧倾等。
    // ============================================================================================

    /// <summary>船舶配置。</summary>
    public sealed class ShipConfig
    {
        public float Length;
        public float Width;
        public float Height;
        public float Mass;
        public float Draft;             // 吃水深度
        public float MetacenterHeight;  // 稳心高度(越大越稳)
        public float HullDragX = 0.3f;  // 船体X方向阻力
        public float HullDragZ = 0.3f;  // 船体Z方向阻力
        public float HullDragY = 0.8f;  // 船体Y方向阻力(上下)
        public float AngularDrag = 0.5f;
        public float RudderArea = 2f;
        public float RudderMaxAngle = 0.5f; // 弧度
        public float PropellerThrust = 5000f;
        public float PropellerRpm;
        public float MaxPropellerRpm = 3000f;
        public float TurnRate;
        public float Speed;
        public Vector3 CenterOfMass;    // 局部坐标系
        public float WaterlineOffset;   // 水线相对船底的高度
    }

    /// <summary>船舶物理。</summary>
    public sealed class ShipPhysics
    {
        private readonly ShipConfig _config;
        private readonly BuoyancySystem _buoyancy;
        private int _bodyId;
        private float _rudderAngle;
        private float _throttle;        // -1~1
        private Vector3 _position;
        private Quaternion _orientation = Quaternion.Identity;
        private Vector3 _linearVelocity;
        private Vector3 _angularVelocity;
        private float _propellerRpm;

        public ShipConfig Config => _config;
        public float RudderAngle
        {
            get => _rudderAngle;
            set => _rudderAngle = Math.Clamp(value, -_config.RudderMaxAngle, _config.RudderMaxAngle);
        }
        public float Throttle
        {
            get => _throttle;
            set => _throttle = Math.Clamp(value, -1f, 1f);
        }
        public Vector3 Position => _position;
        public Quaternion Orientation => _orientation;
        public Vector3 LinearVelocity => _linearVelocity;
        public Vector3 AngularVelocity => _angularVelocity;
        public float Speed => _linearVelocity.Length();
        public float PropellerRpm => _propellerRpm;

        public ShipPhysics(ShipConfig config, BuoyancySystem buoyancy, int bodyId)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _buoyancy = buoyancy ?? throw new ArgumentNullException(nameof(buoyancy));
            _bodyId = bodyId;

            // 注册为浮力物体
            _buoyancy.RegisterBody(bodyId, config.Mass,
                config.Length * config.Width * config.Height * 0.7f, // 船体形状系数
                BuoyancyShapeType.Box,
                boxHalfExtents: new Vector3(config.Length * 0.5f, config.Height * 0.5f, config.Width * 0.5f),
                buoyancyFactor: 1f);
            _buoyancy.GenerateSamplePoints(bodyId, 64);
        }

        /// <summary>设置船的初始位置和朝向。</summary>
        public void Initialize(Vector3 position, Quaternion orientation)
        {
            _position = position;
            _orientation = orientation;
            _linearVelocity = Vector3.Zero;
            _angularVelocity = Vector3.Zero;
        }

        /// <summary>更新船舶物理。</summary>
        public void Update(float dt, Action<Vector3, Vector3, Vector3, Vector3> applyForceTorque)
        {
            // 螺旋桨转速趋向油门
            float targetRpm = _throttle * _config.MaxPropellerRpm;
            _propellerRpm += (targetRpm - _propellerRpm) * Math.Min(1f, dt * 2f);

            // 推力
            Vector3 forward = Vector3.Transform(-Vector3.UnitZ, _orientation);
            Vector3 thrust = forward * (_config.PropellerThrust * _throttle);

            // 舵力(垂直于前进方向)
            Vector3 right = Vector3.Transform(Vector3.UnitX, _orientation);
            float speed = _linearVelocity.Length();
            Vector3 rudderForce = right * (_rudderAngle * speed * _config.RudderArea * 100f);

            // 船体阻力
            Vector3 localVel = Vector3.Transform(_linearVelocity, Quaternion.Inverse(_orientation));
            Vector3 dragForce = -new Vector3(
                localVel.X * _config.HullDragX,
                localVel.Y * _config.HullDragY,
                localVel.Z * _config.HullDragZ) * speed;
            dragForce = Vector3.Transform(dragForce, _orientation);

            // 角阻力
            Vector3 angDrag = -_angularVelocity * _config.AngularDrag;

            // 稳心力矩(让船保持直立)
            Vector3 up = Vector3.Transform(Vector3.UnitY, _orientation);
            Vector3 stabilityTorque = Vector3.Cross(up, Vector3.UnitY) * _config.MetacenterHeight * 1000f;

            applyForceTorque(thrust + dragForce, rudderForce, stabilityTorque, angDrag);
        }

        /// <summary>更新船的状态(从外部物理引擎)。</summary>
        public void UpdateState(Vector3 pos, Quaternion rot, Vector3 linVel, Vector3 angVel)
        {
            _position = pos;
            _orientation = rot;
            _linearVelocity = linVel;
            _angularVelocity = angVel;
        }
    }

    // ============================================================================================
    //  水面波动(简化版FFT波浪)
    //  正经的FFT wave太复杂，这里用多个sin波叠加。
    // ============================================================================================

    /// <summary>水面波浪生成器。</summary>
    public sealed class WaveGenerator
    {
        private readonly List<Wave> _waves = new();
        private float _time;
        private float _choppiness = 1f;

        public float Choppiness
        {
            get => _choppiness;
            set => _choppiness = Math.Clamp(value, 0f, 2f);
        }

        public int WaveCount => _waves.Count;
        public float Time => _time;

        public void AddWave(Wave wave)
        {
            _waves.Add(wave);
        }

        public void ClearWaves()
        {
            _waves.Clear();
        }

        /// <summary>生成默认的海浪。</summary>
        public void GenerateDefaultOcean()
        {
            _waves.Clear();
            _waves.Add(new Wave { Amplitude = 1.0f, Wavelength = 30f, Direction = new Vector2(1, 0), Speed = 3f, Steepness = 0.5f });
            _waves.Add(new Wave { Amplitude = 0.5f, Wavelength = 15f, Direction = new Vector2(0.8f, 0.6f), Speed = 2.5f, Steepness = 0.4f });
            _waves.Add(new Wave { Amplitude = 0.3f, Wavelength = 8f, Direction = new Vector2(0.5f, 0.8f), Speed = 2f, Steepness = 0.3f });
            _waves.Add(new Wave { Amplitude = 0.15f, Wavelength = 4f, Direction = new Vector2(-0.6f, 0.8f), Speed = 1.5f, Steepness = 0.2f });
            _waves.Add(new Wave { Amplitude = 0.08f, Wavelength = 2f, Direction = new Vector2(0.3f, 0.9f), Speed = 1f, Steepness = 0.1f });
        }

        public void Update(float dt)
        {
            _time += dt;
        }

        /// <summary>获取某点的水面高度。</summary>
        public float GetHeight(Vector2 position)
        {
            float h = 0;
            foreach (var w in _waves)
            {
                float k = 2f * MathF.PI / w.Wavelength;
                Vector2 dir = Vector2.Normalize(w.Direction);
                float phase = k * Vector2.Dot(dir, position) - w.Speed * k * _time;
                h += w.Amplitude * MathF.Sin(phase);
            }
            return h * _choppiness;
        }

        /// <summary>获取某点的水面法线。</summary>
        public Vector3 GetNormal(Vector2 position)
        {
            float eps = 0.1f;
            float hL = GetHeight(position - new Vector2(eps, 0));
            float hR = GetHeight(position + new Vector2(eps, 0));
            float hD = GetHeight(position - new Vector2(0, eps));
            float hU = GetHeight(position + new Vector2(0, eps));
            return Vector3.Normalize(new Vector3(hL - hR, 2f * eps, hD - hU));
        }

        /// <summary>获取Gerstner波的位移(让水面有横向移动，更真实)。</summary>
        public Vector3 GetGerstnerDisplacement(Vector2 position)
        {
            Vector3 disp = Vector3.Zero;
            foreach (var w in _waves)
            {
                float k = 2f * MathF.PI / w.Wavelength;
                Vector2 dir = Vector2.Normalize(w.Direction);
                float phase = k * Vector2.Dot(dir, position) - w.Speed * k * _time;
                float c = MathF.Cos(phase);
                float s = MathF.Sin(phase);
                float qa = w.Steepness * w.Amplitude;
                disp.X -= dir.X * qa * c;
                disp.Z -= dir.Y * qa * c;
                disp.Y += w.Amplitude * s;
            }
            disp *= _choppiness;
            return disp;
        }
    }

    /// <summary>单个波浪。</summary>
    public struct Wave
    {
        public float Amplitude;     // 振幅
        public float Wavelength;    // 波长
        public Vector2 Direction;   // 方向
        public float Speed;         // 速度
        public float Steepness;     // 陡度(0~1)
    }

    // ============================================================================================
    //  浮力调试
    // ============================================================================================

    /// <summary>浮力调试可视化数据。</summary>
    public struct BuoyancyDebugDraw
    {
        public Vector3 BodyPosition;
        public Quaternion BodyOrientation;
        public Vector3 ForceVector;
        public Vector3 TorqueVector;
        public float Submersion;
        public Vector3[] SamplePoints;
        public float[] SampleSubmersions;
        public int BodyId;
    }

    /// <summary>浮力调试扩展。</summary>
    public static class BuoyancyDebugExtensions
    {
        public static void CollectDebugDraw(this BuoyancySystem system, List<BuoyancyDebugDraw> output)
        {
            output.Clear();
            // 简化: 实际实现要遍历所有bodies
        }
    }

    // ============================================================================================
    //  水声/特效触发
    // ============================================================================================

    /// <summary>水声事件类型。</summary>
    public enum WaterSoundEventType
    {
        Splash,         // 落水
        Exit,           // 出水
        Underwater,     // 水下
        Flowing,        // 流动声
        Wave,           // 波浪声
    }

    /// <summary>水声事件。</summary>
    public struct WaterSoundEvent
    {
        public WaterSoundEventType Type;
        public Vector3 Position;
        public float Intensity;
        public int BodyId;
    }

    /// <summary>水声事件检测器。</summary>
    public sealed class WaterSoundDetector
    {
        private readonly BuoyancySystem _buoyancy;
        private readonly Dictionary<int, float> _previousSubmersion = new();
        private readonly Queue<WaterSoundEvent> _events = new();
        private float _cooldown;

        public WaterSoundDetector(BuoyancySystem buoyancy)
        {
            _buoyancy = buoyancy;
        }

        public bool TryDequeueEvent(out WaterSoundEvent evt)
        {
            return _events.TryDequeue(out evt);
        }

        public void Update(float dt, Dictionary<int, (Vector3 pos, Quaternion rot)> bodyStates)
        {
            _cooldown -= dt;
            foreach (var kvp in bodyStates)
            {
                int bodyId = kvp.Key;
                Vector3 pos = kvp.Value.pos;
                float curSub = _buoyancy.GetSubmersionAt(pos);

                _previousSubmersion.TryGetValue(bodyId, out float prevSub);

                if (prevSub < 0.1f && curSub > 0.1f && _cooldown <= 0)
                {
                    // 落水
                    _events.Enqueue(new WaterSoundEvent
                    {
                        Type = WaterSoundEventType.Splash,
                        Position = pos,
                        Intensity = curSub,
                        BodyId = bodyId,
                    });
                    _cooldown = 0.1f;
                }
                else if (prevSub > 0.5f && curSub < 0.5f)
                {
                    // 出水
                    _events.Enqueue(new WaterSoundEvent
                    {
                        Type = WaterSoundEventType.Exit,
                        Position = pos,
                        Intensity = 1f - curSub,
                        BodyId = bodyId,
                    });
                }

                _previousSubmersion[bodyId] = curSub;
            }
        }
    }
}
