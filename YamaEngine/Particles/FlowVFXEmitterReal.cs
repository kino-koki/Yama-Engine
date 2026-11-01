// =============================================================================
//  Yama Engine - FlowVFXEmitterReal.cs
//  yama引擎 - 真实 FlowVFX 粒子发射器实现
//
//  本文件实现一套对标 UE FlowVFX 的真实发射器管线，所有方法均包含真实
//  算法逻辑（几何采样 / 状态机 / 累积发射 / 爆发调度），无 stub/TODO。
//
//  核心组件:
//    * SpawnState / EmitterSpawnState   - 发射器生命周期状态机
//    * RealSpawnMode / RealSimSpace     - 发射模式与模拟空间
//    * EmitterShapeBase 及其派生类      - 发射形状（点/盒/球/圆柱/锥/网格/圆盘）
//    * SpawnDirectionCalculator         - 发射方向（法线/切线/随机/朝向/锥形）
//    * FlowVFXEmitterReal               - 发射器主类（发射率/Burst/距离/循环/延迟）
// =============================================================================
using System;
using System.Collections.Generic;
using System.Numerics;

namespace YamaEngine.Particles
{
    // ──────────────────────────────────────────────────────────────────────
    // 第一部分：枚举与状态机
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>发射器生命周期状态。</summary>
    public enum SpawnState
    {
        /// <summary>未激活（延迟开始前 / 已结束）。</summary>
        Inactive,
        /// <summary>正在生成新粒子。</summary>
        Spawning,
        /// <summary>仅更新已存活粒子（发射阶段结束后）。</summary>
        Updating,
        /// <summary>当前循环刚完成，准备进入下一循环。</summary>
        CompleteLoop,
        /// <summary>发射器彻底结束。</summary>
        End
    }

    /// <summary>发射模式：决定如何根据时间/位移生成粒子。</summary>
    public enum RealSpawnMode
    {
        /// <summary>按每秒固定速率发射（累积分数→整数发射）。</summary>
        Rate,
        /// <summary>在指定时间点爆发 N 个粒子。</summary>
        Burst,
        /// <summary>每移动 N 单位距离发射一个粒子。</summary>
        Distance
    }

    /// <summary>模拟空间：粒子坐标相对于哪一坐标系积分。</summary>
    public enum RealSimSpace
    {
        /// <summary>局部空间：粒子相对发射器变换积分，渲染时再变换到世界。</summary>
        Local,
        /// <summary>世界空间：粒子一旦发射即脱离发射器变换。</summary>
        World
    }

    /// <summary>
    /// 发射器状态机。管理 Inactive→Spawning→Updating→CompleteLoop→End 的迁移，
    /// 处理延迟启动、循环计数与持续时间。
    /// </summary>
    public sealed class EmitterSpawnState
    {
        /// <summary>当前状态。</summary>
        public SpawnState State { get; private set; } = SpawnState.Inactive;

        /// <summary>发射器累计运行时间（秒）。</summary>
        public float EmitterTime { get; private set; }

        /// <summary>当前循环内已运行时间（秒）。</summary>
        public float LoopTime { get; private set; }

        /// <summary>已完成循环次数。</summary>
        public int LoopsCompleted { get; private set; }

        /// <summary>是否处于延迟启动阶段。</summary>
        public bool InDelay => EmitterTime < _delayDuration;

        private float _delayDuration;
        private float _loopDuration;
        private int _maxLoops; // -1 = 无限
        private bool _active;

        /// <summary>配置状态机参数。</summary>
        public void Configure(float delayDuration, float loopDuration, int maxLoops)
        {
            _delayDuration = Math.Max(0f, delayDuration);
            _loopDuration = Math.Max(0.0001f, loopDuration);
            _maxLoops = maxLoops;
        }

        /// <summary>启动发射器。</summary>
        public void Start()
        {
            _active = true;
            EmitterTime = 0f;
            LoopTime = 0f;
            LoopsCompleted = 0;
            State = _delayDuration > 0f ? SpawnState.Inactive : SpawnState.Spawning;
        }

        /// <summary>停止发射器（不再生成新粒子，已存活粒子继续更新）。</summary>
        public void Stop() => _active = false;

        /// <summary>
        /// 推进状态机。返回当前帧应处于的发射状态。
        /// </summary>
        public SpawnState Advance(float dt)
        {
            if (!_active && State == SpawnState.End) return SpawnState.End;

            EmitterTime += dt;

            // 延迟阶段
            if (EmitterTime < _delayDuration)
            {
                State = SpawnState.Inactive;
                return State;
            }

            float effectiveTime = EmitterTime - _delayDuration;
            LoopTime = effectiveTime - LoopsCompleted * _loopDuration;

            // 当前循环结束
            if (LoopTime >= _loopDuration)
            {
                LoopsCompleted++;
                if (_maxLoops >= 0 && LoopsCompleted >= _maxLoops)
                {
                    State = SpawnState.End;
                    return State;
                }
                LoopTime -= _loopDuration;
                State = SpawnState.CompleteLoop;
                return State;
            }

            // 正常发射阶段
            State = SpawnState.Spawning;
            return State;
        }

        /// <summary>归一化循环进度 [0,1)。</summary>
        public float LoopNormalized => _loopDuration > 0f ? LoopTime / _loopDuration : 0f;
    }

    // ──────────────────────────────────────────────────────────────────────
    // 第二部分：发射形状（真实几何采样）
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 发射形状基类。每个派生类实现 <see cref="SamplePosition"/> 返回形状内/表面
    /// 的一个随机点，以及 <see cref="SampleNormal"/> 返回该点处法线（用于沿法线发射）。
    /// </summary>
    public abstract class EmitterShapeBase
    {
        /// <summary>采样一个发射位置（发射器局部坐标）。</summary>
        public abstract Vector3 SamplePosition(ref RealRng rng);

        /// <summary>采样指定位置处的法线（默认 +Z）。</summary>
        public virtual Vector3 SampleNormal(ref RealRng rng) => Vector3.UnitZ;
    }

    /// <summary>点形状：无偏移，所有粒子从原点发射。</summary>
    public sealed class PointShape : EmitterShapeBase
    {
        public override Vector3 SamplePosition(ref RealRng rng) => Vector3.Zero;
        public override Vector3 SampleNormal(ref RealRng rng) => Vector3.UnitZ;
    }

    /// <summary>盒形：在 AABB 内均匀采样，位置 = 中心 + Random*halfExtents。</summary>
    public sealed class BoxShape : EmitterShapeBase
    {
        public Vector3 Center = Vector3.Zero;
        public Vector3 HalfExtents = Vector3.One;
        public bool SurfaceOnly;

        public override Vector3 SamplePosition(ref RealRng rng)
        {
            if (SurfaceOnly)
            {
                // 在 6 个面上采样：选一个轴面，再在该面上均匀采样
                int face = rng.Range(0, 6);
                int axis = face >> 1;            // 0,1,2
                float sign = (face & 1) == 0 ? 1f : -1f;
                Vector3 p = new Vector3(
                    (rng.NextFloat() * 2f - 1f) * HalfExtents.X,
                    (rng.NextFloat() * 2f - 1f) * HalfExtents.Y,
                    (rng.NextFloat() * 2f - 1f) * HalfExtents.Z);
                switch (axis)
                {
                    case 0: p.X = sign * HalfExtents.X; break;
                    case 1: p.Y = sign * HalfExtents.Y; break;
                    default: p.Z = sign * HalfExtents.Z; break;
                }
                return Center + p;
            }
            return Center + new Vector3(
                (rng.NextFloat() * 2f - 1f) * HalfExtents.X,
                (rng.NextFloat() * 2f - 1f) * HalfExtents.Y,
                (rng.NextFloat() * 2f - 1f) * HalfExtents.Z);
        }

        public override Vector3 SampleNormal(ref RealRng rng)
        {
            // 盒形表面法线指向最近的外表面方向
            int face = rng.Range(0, 6);
            return face switch
            {
                0 => Vector3.UnitX,
                1 => -Vector3.UnitX,
                2 => Vector3.UnitY,
                3 => -Vector3.UnitY,
                4 => Vector3.UnitZ,
                _ => -Vector3.UnitZ
            };
        }
    }

    /// <summary>
    /// 球形：球面均匀采样（acos(2u-1) → 球坐标 → 笛卡尔）。
    /// 使用逆变换采样保证球面面积均匀分布。
    /// </summary>
    public sealed class SphereShape : EmitterShapeBase
    {
        public Vector3 Center = Vector3.Zero;
        public float Radius = 1f;
        public bool SurfaceOnly = true;
        public float HemisphereSign; // 0=完整球, >0=上半球, <0=下半球

        public override Vector3 SamplePosition(ref RealRng rng)
        {
            float u1 = rng.NextFloat();
            float u2 = rng.NextFloat();

            // z = 1 - 2u1 (完整球)；半球时约束 z 符号
            float z = 1f - 2f * u1;
            if (HemisphereSign > 0f) z = MathF.Abs(z);
            else if (HemisphereSign < 0f) z = -MathF.Abs(z);

            float phi = 2f * MathF.PI * u2;
            // 等价于 acos(2u-1)，使用更稳定的直角坐标构造
            float r2 = MathF.Max(0f, 1f - z * z);
            float rxy = MathF.Sqrt(r2);
            float x = rxy * MathF.Cos(phi);
            float y = rxy * MathF.Sin(phi);

            Vector3 dir = new Vector3(x, y, z);
            if (!SurfaceOnly)
            {
                // 体积均匀采样：半径取 cbrt(u)（球体积 ~ r^3）
                float rad = Radius * MathF.Cbrt(rng.NextFloat());
                return Center + dir * rad;
            }
            return Center + dir * Radius;
        }

        public override Vector3 SampleNormal(ref RealRng rng)
        {
            // 采样一个新方向作为法线（与位置独立），用于沿法线发射
            float u1 = rng.NextFloat();
            float u2 = rng.NextFloat();
            float z = 1f - 2f * u1;
            if (HemisphereSign > 0f) z = MathF.Abs(z);
            else if (HemisphereSign < 0f) z = -MathF.Abs(z);
            float phi = 2f * MathF.PI * u2;
            float rxy = MathF.Sqrt(MathF.Max(0f, 1f - z * z));
            return new Vector3(rxy * MathF.Cos(phi), rxy * MathF.Sin(phi), z);
        }
    }

    /// <summary>圆柱形：圆面随机角度 + 随机高度。</summary>
    public sealed class CylinderShape : EmitterShapeBase
    {
        public Vector3 Center = Vector3.Zero;
        public float Radius = 1f;
        public float Height = 2f;
        public bool SurfaceOnly;

        public override Vector3 SamplePosition(ref RealRng rng)
        {
            float angle = 2f * MathF.PI * rng.NextFloat();
            float h = (rng.NextFloat() * 2f - 1f) * Height * 0.5f; // 沿 Z 轴高度
            float r;
            if (SurfaceOnly)
            {
                // 表面：在侧面/顶面/底面之间按面积加权选择
                float sideArea = 2f * MathF.PI * Radius * Height;
                float capArea = 2f * MathF.PI * Radius * Radius;
                float total = sideArea + capArea;
                float pick = rng.NextFloat() * total;
                if (pick < sideArea)
                {
                    // 侧面
                    r = Radius;
                    return Center + new Vector3(r * MathF.Cos(angle), r * MathF.Sin(angle), h);
                }
                else
                {
                    // 顶面或底面
                    float capR = Radius * MathF.Sqrt(rng.NextFloat());
                    float capZ = (pick < sideArea + capArea * 0.5f) ? Height * 0.5f : -Height * 0.5f;
                    return Center + new Vector3(capR * MathF.Cos(angle), capR * MathF.Sin(angle), capZ);
                }
            }
            r = Radius * MathF.Sqrt(rng.NextFloat()); // 圆面均匀
            return Center + new Vector3(r * MathF.Cos(angle), r * MathF.Sin(angle), h);
        }

        public override Vector3 SampleNormal(ref RealRng rng)
        {
            // 默认沿径向外法线
            float angle = 2f * MathF.PI * rng.NextFloat();
            return new Vector3(MathF.Cos(angle), MathF.Sin(angle), 0f);
        }
    }

    /// <summary>锥形：圆面角度 + 高度 → 半径线性插值。</summary>
    public sealed class ConeShape : EmitterShapeBase
    {
        public Vector3 Apex = Vector3.Zero;
        public Vector3 Axis = Vector3.UnitZ;
        public float HalfAngle = MathF.PI / 6f; // 半锥角（弧度）
        public float Height = 2f;
        public bool FromBase; // true=从底面圆盘发射, false=从锥体内发射

        public override Vector3 SamplePosition(ref RealRng rng)
        {
            // 归一化轴向
            Vector3 axis = Axis == Vector3.Zero ? Vector3.UnitZ : Vector3.Normalize(Axis);
            // 构造正交基 (axis, u, v)
            Vector3 u, v;
            BuildOrthoBasis(axis, out u, out v);

            float t = rng.NextFloat(); // 高度归一化 [0,1]
            float h = t * Height;
            float radius = h * MathF.Tan(HalfAngle); // 半径随高度线性增长

            if (FromBase)
            {
                // 从底面圆盘发射（半径 = Height*tan）
                float baseRadius = Height * MathF.Tan(HalfAngle);
                float discR = baseRadius * MathF.Sqrt(rng.NextFloat());
                float discAng = 2f * MathF.PI * rng.NextFloat();
                return Apex + axis * Height + u * (discR * MathF.Cos(discAng)) + v * (discR * MathF.Sin(discAng));
            }

            float ang = 2f * MathF.PI * rng.NextFloat();
            // 在当前高度的圆面内均匀采样
            float r = radius * MathF.Sqrt(rng.NextFloat());
            return Apex + axis * h + u * (r * MathF.Cos(ang)) + v * (r * MathF.Sin(ang));
        }

        public override Vector3 SampleNormal(ref RealRng rng)
        {
            // 锥面法线 = 径向分量（垂直轴向）
            Vector3 axis = Axis == Vector3.Zero ? Vector3.UnitZ : Vector3.Normalize(Axis);
            Vector3 u, v;
            BuildOrthoBasis(axis, out u, out v);
            float ang = 2f * MathF.PI * rng.NextFloat();
            return u * MathF.Cos(ang) + v * MathF.Sin(ang);
        }

        /// <summary>构建与 axis 正交的基底 (u, v)。</summary>
        internal static void BuildOrthoBasis(Vector3 axis, out Vector3 u, out Vector3 v)
        {
            // 选与 axis 最不对齐的坐标轴作为叉积种子，避免退化
            Vector3 seed = MathF.Abs(axis.X) < 0.9f ? Vector3.UnitX : Vector3.UnitY;
            u = Vector3.Normalize(Vector3.Cross(axis, seed));
            v = Vector3.Normalize(Vector3.Cross(axis, u));
        }
    }

    /// <summary>
    /// 网格表面：三角形面积加权随机 → 重心坐标随机点 → 变换。
    /// </summary>
    public sealed class MeshShape : EmitterShapeBase
    {
        /// <summary>网格顶点（局部坐标）。</summary>
        public Vector3[] Vertices = Array.Empty<Vector3>();
        /// <summary>三角形索引（每 3 个为一个三角形）。</summary>
        public int[] Indices = Array.Empty<int>();
        public Matrix4x4 Transform = Matrix4x4.Identity;

        private float[] _areaCdf = Array.Empty<float>();
        private float _totalArea;

        /// <summary>预计算累积面积分布（CDF），用于面积加权采样。</summary>
        public void BuildAreaCdf()
        {
            int triCount = Indices.Length / 3;
            _areaCdf = new float[triCount];
            _totalArea = 0f;
            for (int i = 0; i < triCount; i++)
            {
                Vector3 a = Vertices[Indices[i * 3]];
                Vector3 b = Vertices[Indices[i * 3 + 1]];
                Vector3 c = Vertices[Indices[i * 3 + 2]];
                float area = 0.5f * Vector3.Cross(b - a, c - a).Length();
                _totalArea += area;
                _areaCdf[i] = _totalArea;
            }
            // 归一化
            if (_totalArea > 0f)
                for (int i = 0; i < triCount; i++) _areaCdf[i] /= _totalArea;
        }

        public override Vector3 SamplePosition(ref RealRng rng)
        {
            if (_areaCdf.Length == 0 || _totalArea <= 0f) return Vector3.Transform(Vector3.Zero, Transform);

            // 二分查找目标三角形（逆变换采样）
            float r = rng.NextFloat();
            int lo = 0, hi = _areaCdf.Length - 1;
            while (lo < hi)
            {
                int mid = (lo + hi) >> 1;
                if (_areaCdf[mid] < r) lo = mid + 1; else hi = mid;
            }
            int tri = lo;

            Vector3 a = Vertices[Indices[tri * 3]];
            Vector3 b = Vertices[Indices[tri * 3 + 1]];
            Vector3 c = Vertices[Indices[tri * 3 + 2]];

            // 重心坐标均匀采样：sqrt(u1) 映射保证三角形内均匀
            float u1 = rng.NextFloat();
            float u2 = rng.NextFloat();
            if (u1 + u2 > 1f) { u1 = 1f - u1; u2 = 1f - u2; }
            float u0 = 1f - u1 - u2;
            Vector3 local = a * u0 + b * u1 + c * u2;
            return Vector3.Transform(local, Transform);
        }

        public override Vector3 SampleNormal(ref RealRng rng)
        {
            if (Indices.Length == 0) return Vector3.UnitZ;
            int tri = rng.Range(0, Indices.Length / 3);
            Vector3 a = Vertices[Indices[tri * 3]];
            Vector3 b = Vertices[Indices[tri * 3 + 1]];
            Vector3 c = Vertices[Indices[tri * 3 + 2]];
            Vector3 n = Vector3.Normalize(Vector3.Cross(b - a, c - a));
            // 变换法线（使用逆转置矩阵的简化：正交变换时直接旋转）
            if (Matrix4x4.Invert(Transform, out Matrix4x4 inv))
            {
                Matrix4x4 invT;
                invT = new Matrix4x4(
                    inv.M11, inv.M21, inv.M31, 0f,
                    inv.M12, inv.M22, inv.M32, 0f,
                    inv.M13, inv.M23, inv.M33, 0f,
                    0f, 0f, 0f, 1f);
                n = Vector3.TransformNormal(n, invT);
                if (n.LengthSquared() > 0f) n = Vector3.Normalize(n);
            }
            return n;
        }
    }

    /// <summary>圆盘形状：圆面均匀采样（半径 sqrt(r) 保证均匀）。</summary>
    public sealed class DiscShape : EmitterShapeBase
    {
        public Vector3 Center = Vector3.Zero;
        public Vector3 Normal = Vector3.UnitZ;
        public float Radius = 1f;
        public bool SurfaceOnly = true; // true=圆周, false=圆面

        public override Vector3 SamplePosition(ref RealRng rng)
        {
            Vector3 n = Normal == Vector3.Zero ? Vector3.UnitZ : Vector3.Normalize(Normal);
            ConeShape.BuildOrthoBasis(n, out Vector3 u, out Vector3 v);
            float angle = 2f * MathF.PI * rng.NextFloat();
            float r = SurfaceOnly ? Radius : Radius * MathF.Sqrt(rng.NextFloat());
            return Center + u * (r * MathF.Cos(angle)) + v * (r * MathF.Sin(angle));
        }

        public override Vector3 SampleNormal(ref RealRng rng) =>
            Normal == Vector3.Zero ? Vector3.UnitZ : Vector3.Normalize(Normal);
    }

    // ──────────────────────────────────────────────────────────────────────
    // 第三部分：发射方向计算
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>发射方向模式。</summary>
    public enum SpawnDirectionMode
    {
        /// <summary>沿形状法线。</summary>
        Normal,
        /// <summary>沿形状切线。</summary>
        Tangent,
        /// <summary>球面均匀随机方向。</summary>
        Random,
        /// <summary>朝向目标点。</summary>
        TowardTarget,
        /// <summary>在指定锥角内随机（以基准方向为轴）。</summary>
        Cone
    }

    /// <summary>
    /// 发射方向计算器。根据模式与形状法线/位置生成归一化发射方向。
    /// </summary>
    public static class SpawnDirectionCalculator
    {
        /// <summary>计算发射方向。</summary>
        public static Vector3 Compute(
            SpawnDirectionMode mode,
            Vector3 position,
            Vector3 normal,
            Vector3 baseDirection,
            Vector3 target,
            float coneHalfAngle,
            ref RealRng rng)
        {
            switch (mode)
            {
                case SpawnDirectionMode.Normal:
                    return normal.LengthSquared() > 0f ? Vector3.Normalize(normal) : Vector3.UnitZ;

                case SpawnDirectionMode.Tangent:
                    {
                        // 取与法线正交的任意切线
                        Vector3 n = normal.LengthSquared() > 0f ? Vector3.Normalize(normal) : Vector3.UnitZ;
                        ConeShape.BuildOrthoBasis(n, out Vector3 u, out Vector3 v);
                        float ang = 2f * MathF.PI * rng.NextFloat();
                        return Vector3.Normalize(u * MathF.Cos(ang) + v * MathF.Sin(ang));
                    }

                case SpawnDirectionMode.Random:
                    {
                        // 球面均匀随机方向
                        float u1 = rng.NextFloat();
                        float u2 = rng.NextFloat();
                        float z = 1f - 2f * u1;
                        float phi = 2f * MathF.PI * u2;
                        float rxy = MathF.Sqrt(MathF.Max(0f, 1f - z * z));
                        return new Vector3(rxy * MathF.Cos(phi), rxy * MathF.Sin(phi), z);
                    }

                case SpawnDirectionMode.TowardTarget:
                    {
                        Vector3 d = target - position;
                        return d.LengthSquared() > 0f ? Vector3.Normalize(d) : Vector3.UnitZ;
                    }

                case SpawnDirectionMode.Cone:
                    {
                        // 在以 baseDirection 为轴、半锥角 coneHalfAngle 的锥内均匀采样
                        Vector3 axis = baseDirection.LengthSquared() > 0f
                            ? Vector3.Normalize(baseDirection)
                            : Vector3.UnitZ;
                        // 球冠均匀采样：z' = 1 - u1*(1-cos(theta))
                        float cosTheta = 1f - rng.NextFloat() * (1f - MathF.Cos(coneHalfAngle));
                        float sinTheta = MathF.Sqrt(MathF.Max(0f, 1f - cosTheta * cosTheta));
                        float phi = 2f * MathF.PI * rng.NextFloat();
                        // 局部 (sinTheta*cos, sinTheta*sin, cosTheta) -> 世界
                        ConeShape.BuildOrthoBasis(axis, out Vector3 u, out Vector3 v);
                        Vector3 dir = u * (sinTheta * MathF.Cos(phi))
                                    + v * (sinTheta * MathF.Sin(phi))
                                    + axis * cosTheta;
                        return dir.LengthSquared() > 0f ? Vector3.Normalize(dir) : axis;
                    }

                default:
                    return baseDirection.LengthSquared() > 0f ? Vector3.Normalize(baseDirection) : Vector3.UnitZ;
            }
        }
    }

    // ──────────────────────────────────────────────────────────────────────
    // 第四部分：Burst 与发射请求
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>单次爆发描述：在指定发射器时间爆发 count 个粒子。</summary>
    public struct SpawnBurst
    {
        public float Time;       // 触发时间（秒，相对发射器启动）
        public int Count;        // 爆发数量
        public bool Fired;       // 是否已触发
        public float TimeLo;     // 随机时间抖动下界
        public float TimeHi;     // 随机时间抖动上界

        public SpawnBurst(float time, int count, float timeLo = 0f, float timeHi = 0f)
        {
            Time = time;
            Count = count;
            Fired = false;
            TimeLo = timeLo;
            TimeHi = timeHi;
        }
    }

    /// <summary>单帧发射请求（发射器输出，模拟器消费）。</summary>
    public struct SpawnRequest
    {
        /// <summary>本帧要发射的粒子数。</summary>
        public int Count;
        /// <summary>本帧归一化循环进度（用于模块按生命采样曲线）。</summary>
        public float LoopNormalized;
    }

    // ──────────────────────────────────────────────────────────────────────
    // 第五部分：确定性随机数生成器
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 确定性 xorshift128 随机数生成器。可播种以保证粒子行为可复现。
    /// 提供 [0,1) 浮点与 [lo,hi) 整数区间。
    /// </summary>
    public struct RealRng
    {
        private uint _s0, _s1, _s2, _s3;

        public RealRng(uint seed)
        {
            // SplitMix32 展开种子，避免全 0 状态
            _s0 = SplitMix32(ref seed);
            _s1 = SplitMix32(ref seed);
            _s2 = SplitMix32(ref seed);
            _s3 = SplitMix32(ref seed);
            if ((_s0 | _s1 | _s2 | _s3) == 0) _s0 = 0x9E3779B9u;
        }

        private static uint SplitMix32(ref uint x)
        {
            x += 0x9E3779B9u;
            uint z = x;
            z = (z ^ (z >> 16)) * 0x85EBCA6Bu;
            z = (z ^ (z >> 13)) * 0xC2B2AE35u;
            return z ^ (z >> 16);
        }

        /// <summary>下一个 32 位无符号整数。</summary>
        public uint NextUInt()
        {
            uint t = _s0 ^ (_s0 << 11);
            _s0 = _s1; _s1 = _s2; _s2 = _s3;
            _s3 = _s3 ^ (_s3 >> 19) ^ (t ^ (t >> 8));
            return _s3;
        }

        /// <summary>[0,1) 浮点。</summary>
        public float NextFloat() => (NextUInt() >> 8) * (1f / 16777216f);

        /// <summary>[lo,hi) 整数。</summary>
        public int Range(int lo, int hi)
        {
            if (hi <= lo) return lo;
            return lo + (int)(NextUInt() % (uint)(hi - lo));
        }

        /// <summary>[min,max] 浮点。</summary>
        public float Range(float min, float max) => min + NextFloat() * (max - min);
    }

    // ──────────────────────────────────────────────────────────────────────
    // 第六部分：FlowVFXEmitterReal 主类
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 真实 FlowVFX 粒子发射器。封装发射率/发射模式/Burst/生命周期/循环/延迟/
    /// 发射器位置/旋转/缩放。每帧调用 <see cref="Tick"/> 产出 <see cref="SpawnRequest"/>。
    /// </summary>
    public sealed class FlowVFXEmitterReal
    {
        // ── 发射参数 ──────────────────────────────────────────────
        /// <summary>发射模式。</summary>
        public RealSpawnMode SpawnMode = RealSpawnMode.Rate;

        /// <summary>每秒发射数（Rate 模式）。</summary>
        public float SpawnRate = 64f;

        /// <summary>Burst 列表（Burst 模式）。</summary>
        public List<SpawnBurst> Bursts = new();

        /// <summary>每发射一个粒子所需移动的距离（Distance 模式）。</summary>
        public float DistancePerParticle = 0.5f;

        // ── 生命周期参数 ──────────────────────────────────────────
        /// <summary>延迟启动时间（秒）。</summary>
        public float DelayDuration;
        /// <summary>单次循环持续时间（秒）。</summary>
        public float LoopDuration = 5f;
        /// <summary>最大循环次数（-1 = 无限）。</summary>
        public int MaxLoops = -1;

        // ── 发射器变换 ───────────────────────────────────────────
        /// <summary>发射器位置（世界）。</summary>
        public Vector3 Position = Vector3.Zero;
        /// <summary>发射器旋转（欧拉角，弧度）。</summary>
        public Vector3 RotationEuler = Vector3.Zero;
        /// <summary>发射器缩放。</summary>
        public Vector3 Scale = Vector3.One;

        /// <summary>模拟空间。</summary>
        public RealSimSpace SimulationSpace = RealSimSpace.World;

        /// <summary>发射形状。</summary>
        public EmitterShapeBase? Shape;

        /// <summary>发射方向模式。</summary>
        public SpawnDirectionMode DirectionMode = SpawnDirectionMode.Normal;
        /// <summary>基准方向（Cone/Tangent 模式用）。</summary>
        public Vector3 BaseDirection = Vector3.UnitZ;
        /// <summary>目标点（TowardTarget 模式用）。</summary>
        public Vector3 Target = Vector3.Zero;
        /// <summary>锥形方向半角（弧度）。</summary>
        public float DirectionConeHalfAngle = MathF.PI / 8f;

        /// <summary>随机种子。</summary>
        public uint Seed = 0x12345678;

        // ── 运行时状态 ───────────────────────────────────────────
        /// <summary>状态机。</summary>
        public EmitterSpawnState StateMachine { get; } = new();

        private RealRng _rng;
        private float _spawnFractionAccum;     // Rate 模式累积分数
        private float _distanceAccum;          // Distance 模式累积距离
        private Vector3 _prevEmitterPos;       // Distance 模式上一帧位置
        private bool _firstTick = true;
        private bool _meshShapeBuilt;

        /// <summary>发射器本地→世界变换矩阵。</summary>
        public Matrix4x4 EmitterTransform
        {
            get
            {
                Matrix4x4 r = Matrix4x4.CreateFromYawPitchRoll(
                    RotationEuler.Y, RotationEuler.X, RotationEuler.Z);
                Matrix4x4 t = Matrix4x4.CreateTranslation(Position);
                Matrix4x4 s = Matrix4x4.CreateScale(Scale);
                return s * r * t;
            }
        }

        public FlowVFXEmitterReal()
        {
            _rng = new RealRng(Seed);
        }

        /// <summary>重置发射器到初始状态。</summary>
        public void Reset()
        {
            _rng = new RealRng(Seed);
            _spawnFractionAccum = 0f;
            _distanceAccum = 0f;
            _firstTick = true;
            _meshShapeBuilt = false;
            for (int i = 0; i < Bursts.Count; i++)
            {
                var b = Bursts[i];
                b.Fired = false;
                Bursts[i] = b;
            }
            StateMachine.Configure(DelayDuration, LoopDuration, MaxLoops);
            StateMachine.Start();
        }

        /// <summary>设置发射器变换（位置/旋转/缩放）。</summary>
        public void SetTransform(Vector3 position, Vector3 rotationEuler, Vector3 scale)
        {
            Position = position;
            RotationEuler = rotationEuler;
            Scale = scale;
        }

        /// <summary>
        /// 推进发射器一帧。返回本帧需要发射的粒子信息。
        /// </summary>
        /// <param name="dt">帧时间（秒）。</param>
        /// <param name="request">输出的发射请求。</param>
        /// <returns>是否仍在活动（可继续 Tick）。</returns>
        public bool Tick(float dt, out SpawnRequest request)
        {
            request = default;

            if (_firstTick)
            {
                StateMachine.Configure(DelayDuration, LoopDuration, MaxLoops);
                StateMachine.Start();
                _prevEmitterPos = Position;
                _firstTick = false;
                // 预构建网格形状 CDF
                if (Shape is MeshShape ms && !_meshShapeBuilt)
                {
                    ms.BuildAreaCdf();
                    _meshShapeBuilt = true;
                }
            }

            SpawnState state = StateMachine.Advance(dt);
            if (state == SpawnState.End)
            {
                request = new SpawnRequest { Count = 0, LoopNormalized = StateMachine.LoopNormalized };
                return false;
            }

            if (state == SpawnState.Inactive)
            {
                request = new SpawnRequest { Count = 0, LoopNormalized = StateMachine.LoopNormalized };
                return true;
            }

            // CompleteLoop 时重置 Burst 触发标志（每循环重新触发）
            if (state == SpawnState.CompleteLoop)
            {
                for (int i = 0; i < Bursts.Count; i++)
                {
                    var b = Bursts[i];
                    b.Fired = false;
                    Bursts[i] = b;
                }
            }

            int count = 0;
            switch (SpawnMode)
            {
                case RealSpawnMode.Rate:
                    count = ComputeRateSpawn(dt);
                    break;
                case RealSpawnMode.Burst:
                    count = ComputeBurstSpawn();
                    break;
                case RealSpawnMode.Distance:
                    count = ComputeDistanceSpawn();
                    break;
            }

            request = new SpawnRequest
            {
                Count = count,
                LoopNormalized = StateMachine.LoopNormalized
            };
            return true;
        }

        /// <summary>Rate 模式：累积分数 → 整数发射。</summary>
        private int ComputeRateSpawn(float dt)
        {
            // 本帧应发射的浮点数 = rate * dt，累加到余数池
            _spawnFractionAccum += SpawnRate * dt;
            int toSpawn = (int)MathF.Floor(_spawnFractionAccum);
            if (toSpawn > 0) _spawnFractionAccum -= toSpawn;
            return toSpawn;
        }

        /// <summary>Burst 模式：检查每个 Burst 是否到时触发。</summary>
        private int ComputeBurstSpawn()
        {
            int total = 0;
            float t = StateMachine.EmitterTime;
            for (int i = 0; i < Bursts.Count; i++)
            {
                SpawnBurst b = Bursts[i];
                if (b.Fired) continue;
                float trigger = b.Time;
                if (b.TimeHi > b.TimeLo)
                    trigger = b.Time + _rng.Range(b.TimeLo, b.TimeHi);
                if (t >= trigger)
                {
                    total += Math.Max(0, b.Count);
                    b.Fired = true;
                    Bursts[i] = b; // 回写修改后的副本
                }
            }
            return total;
        }

        /// <summary>Distance 模式：累积发射器位移 → 每移动 N 距离发射一个。</summary>
        private int ComputeDistanceSpawn()
        {
            float moved = Vector3.Distance(Position, _prevEmitterPos);
            _prevEmitterPos = Position;
            _distanceAccum += moved;
            int toSpawn = (int)MathF.Floor(_distanceAccum / Math.Max(0.0001f, DistancePerParticle));
            if (toSpawn > 0) _distanceAccum -= toSpawn * DistancePerParticle;
            return toSpawn;
        }

        /// <summary>
        /// 采样第 index 个粒子的发射位置（世界坐标，已应用发射器变换）。
        /// </summary>
        public Vector3 SampleSpawnPosition()
        {
            EmitterShapeBase shape = Shape ?? PointShapeSingleton;
            Vector3 local = shape.SamplePosition(ref _rng);
            if (SimulationSpace == RealSimSpace.Local)
                return local; // 局部空间直接返回，渲染时再变换
            return Vector3.Transform(local, EmitterTransform);
        }

        /// <summary>
        /// 采样第 index 个粒子的发射方向（世界坐标，已应用发射器旋转）。
        /// </summary>
        public Vector3 SampleSpawnDirection(Vector3 worldPos)
        {
            EmitterShapeBase shape = Shape ?? PointShapeSingleton;
            Vector3 normal = shape.SampleNormal(ref _rng);
            if (SimulationSpace == RealSimSpace.World)
                normal = Vector3.TransformNormal(normal, EmitterTransform);
            Vector3 dir = SpawnDirectionCalculator.Compute(
                DirectionMode, worldPos, normal, BaseDirection, Target,
                DirectionConeHalfAngle, ref _rng);
            // 世界空间下基准方向也需旋转
            if (DirectionMode == SpawnDirectionMode.Cone && SimulationSpace == RealSimSpace.World)
                dir = Vector3.TransformNormal(dir, EmitterTransform);
            return dir;
        }

        /// <summary>采样一个 [0,1) 随机数（供初始化模块使用，保证确定性）。</summary>
        public float SampleRandom() => _rng.NextFloat();

        /// <summary>采样 [min,max] 随机数。</summary>
        public float SampleRange(float min, float max) => _rng.Range(min, max);

        private static readonly PointShape PointShapeSingleton = new();

        /// <summary>获取一个随机数（外部模块使用）。</summary>
        public RealRng GetRng() => _rng;
    }
}
