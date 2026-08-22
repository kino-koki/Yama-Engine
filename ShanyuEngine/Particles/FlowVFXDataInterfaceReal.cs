// =============================================================================
//  Shanyu Engine - FlowVFXDataInterfaceReal.cs
//  山雨引擎 - 真实 FlowVFX 数据接口
//
//  本文件实现粒子脚本可引用的数据接口集，所有方法均包含真实算法：
//    * FlowVFXDataInterfaceReal      - 基类
//    * DIDataInterfaceCurve          - Hermite 三次曲线评估/采样/梯度
//    * DIDataInterfaceGradient       - 多停止点颜色渐变（线性/平滑插值）
//    * DIDataInterfaceRamp           - float 斜坡映射
//    * DIDataInterfaceNoise          - Perlin/Simplex/Worley/Curl/fBm 噪声
//    * DIDataInterfaceVectorField    - 3D 纹理三线性插值/世界空间映射
//    * DIDataInterfaceTriangleLookup - 三角网格表面属性查找
//    * DIDataInterfaceGrid2D         - 粒子→网格密度 / 网格→粒子反馈
//    * DIDataInterfaceSparseGrid     - 体素化稀疏网格 / 哈希查找
//    * DIDataInterfaceNeighborGrid   - 空间哈希邻居查询
// =============================================================================
using System;
using System.Collections.Generic;
using System.Numerics;

namespace ShanyuEngine.Particles
{
    // ──────────────────────────────────────────────────────────────────────
    // 第一部分：基类
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>数据接口类型。</summary>
    public enum DIDataInterfaceType
    {
        Curve, Gradient, Ramp, Noise, VectorField,
        TriangleLookup, Grid2D, SparseGrid, NeighborGrid
    }

    /// <summary>
    /// 数据接口基类。数据接口是粒子脚本可在运行时采样外部数据的抽象，
    /// 对应 UE FlowVFX 的 DataInterface（DI）。
    /// </summary>
    public abstract class FlowVFXDataInterfaceReal
    {
        public string Name = "";
        public DIDataInterfaceType Type;
        public abstract void Reset();
    }

    // ──────────────────────────────────────────────────────────────────────
    // 第二部分：曲线接口（Hermite 三次样条）
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>曲线关键帧（时间/值/入切线/出切线）。</summary>
    public struct DICurveKey
    {
        public float Time;
        public float Value;
        public float InTangent;
        public float OutTangent;

        public DICurveKey(float time, float value, float inT = 0f, float outT = 0f)
        { Time = time; Value = value; InTangent = inT; OutTangent = outT; }
    }

    /// <summary>
    /// 曲线接口：基于 Hermite 三次样条评估。给定归一化时间 t∈[0,1] 返回值。
    /// H(t) = (2t³-3t²+1)p0 + (t³-2t²+t)m0 + (-2t³+3t²)p1 + (t³-t²)m1
    /// </summary>
    public sealed class DIDataInterfaceCurve : FlowVFXDataInterfaceReal
    {
        public List<DICurveKey> Keys = new();

        public DIDataInterfaceCurve() { Type = DIDataInterfaceType.Curve; }

        public override void Reset() => Keys.Clear();

        /// <summary>添加关键帧。</summary>
        public void AddKey(float time, float value, float inT = 0f, float outT = 0f)
            => Keys.Add(new DICurveKey(time, value, inT, outT));

        /// <summary>Hermite 三次样条求值。</summary>
        public float Sample(float t)
        {
            if (Keys.Count == 0) return 0f;
            if (Keys.Count == 1) return Keys[0].Value;
            // 按时间排序
            SortKeys();
            t = Math.Clamp(t, Keys[0].Time, Keys[^1].Time);
            // 找到 t 所在区间
            int i = 0;
            for (i = 0; i < Keys.Count - 1; i++)
                if (t <= Keys[i + 1].Time) break;

            DICurveKey k0 = Keys[i];
            DICurveKey k1 = Keys[Math.Min(i + 1, Keys.Count - 1)];
            float dt = k1.Time - k0.Time;
            if (dt < 1e-6f) return k0.Value;
            float u = (t - k0.Time) / dt;
            float u2 = u * u;
            float u3 = u2 * u;

            float h00 = 2f * u3 - 3f * u2 + 1f;
            float h10 = u3 - 2f * u2 + u;
            float h01 = -2f * u3 + 3f * u2;
            float h11 = u3 - u2;

            // 切线缩放为区间长度（Hermite 标准约定）
            return h00 * k0.Value + h10 * k0.OutTangent * dt
                 + h01 * k1.Value + h11 * k1.InTangent * dt;
        }

        /// <summary>梯度（导数）查找：有限差分。</summary>
        public float SampleGradient(float t, float eps = 0.001f)
            => (Sample(t + eps) - Sample(t - eps)) / (2f * eps);

        /// <summary>等距采样 N 个点。</summary>
        public float[] BakeSamples(int count)
        {
            float[] result = new float[count];
            for (int i = 0; i < count; i++)
                result[i] = Sample((float)i / Math.Max(1, count - 1));
            return result;
        }

        private bool _sorted;
        private void SortKeys()
        {
            if (_sorted) return;
            Keys.Sort((a, b) => a.Time.CompareTo(b.Time));
            _sorted = true;
        }
    }

    // ──────────────────────────────────────────────────────────────────────
    // 第三部分：渐变接口（多色停止点）
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>渐变停止点。</summary>
    public struct DIGradientStop
    {
        public float Time;   // [0,1]
        public Vector4 Color;
        public DIGradientStop(float t, Vector4 c) { Time = t; Color = c; }
    }

    /// <summary>渐变插值模式。</summary>
    public enum GradientInterpMode { Linear, Smoothstep, Constant }

    /// <summary>
    /// 渐变接口：多色停止点颜色渐变。支持线性/平滑/常量插值。
    /// </summary>
    public sealed class DIDataInterfaceGradient : FlowVFXDataInterfaceReal
    {
        public List<DIGradientStop> Stops = new();
        public GradientInterpMode Mode = GradientInterpMode.Linear;

        public DIDataInterfaceGradient() { Type = DIDataInterfaceType.Gradient; }

        public override void Reset() => Stops.Clear();

        public void AddStop(float time, Vector4 color) => Stops.Add(new DIGradientStop(time, color));

        /// <summary>采样渐变。</summary>
        public Vector4 Sample(float t)
        {
            if (Stops.Count == 0) return Vector4.One;
            if (Stops.Count == 1) return Stops[0].Color;
            SortStops();
            t = Math.Clamp(t, Stops[0].Time, Stops[^1].Time);
            // 找到区间
            int i = 0;
            for (i = 0; i < Stops.Count - 1; i++)
                if (t <= Stops[i + 1].Time) break;

            DIGradientStop s0 = Stops[i];
            DIGradientStop s1 = Stops[Math.Min(i + 1, Stops.Count - 1)];
            float dt = s1.Time - s0.Time;
            if (dt < 1e-6f) return s0.Color;
            float u = (t - s0.Time) / dt;

            return Mode switch
            {
                GradientInterpMode.Linear => Vector4.Lerp(s0.Color, s1.Color, u),
                GradientInterpMode.Smoothstep =>
                    Vector4.Lerp(s0.Color, s1.Color, u * u * (3f - 2f * u)),
                GradientInterpMode.Constant => s0.Color,
                _ => Vector4.Lerp(s0.Color, s1.Color, u)
            };
        }

        private bool _sorted;
        private void SortStops()
        {
            if (_sorted) return;
            Stops.Sort((a, b) => a.Time.CompareTo(b.Time));
            _sorted = true;
        }
    }

    // ──────────────────────────────────────────────────────────────────────
    // 第四部分：斜坡接口（float→float 映射）
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 斜坡接口：将输入值 [0,1] 通过分段线性映射为输出值。
    /// </summary>
    public sealed class DIDataInterfaceRamp : FlowVFXDataInterfaceReal
    {
        public List<(float In, float Out)> Points = new();

        public DIDataInterfaceRamp() { Type = DIDataInterfaceType.Ramp; }

        public override void Reset() => Points.Clear();

        public void AddPoint(float input, float output) => Points.Add((input, output));

        /// <summary>分段线性采样。</summary>
        public float Sample(float x)
        {
            if (Points.Count == 0) return 0f;
            if (Points.Count == 1) return Points[0].Output;
            SortPoints();
            if (x <= Points[0].In) return Points[0].Output;
            if (x >= Points[^1].In) return Points[^1].Output;
            for (int i = 0; i < Points.Count - 1; i++)
            {
                if (x >= Points[i].In && x <= Points[i + 1].In)
                {
                    float dx = Points[i + 1].In - Points[i].In;
                    if (dx < 1e-6f) return Points[i].Output;
                    float u = (x - Points[i].In) / dx;
                    return Points[i].Output + (Points[i + 1].Output - Points[i].Output) * u;
                }
            }
            return Points[^1].Output;
        }

        private bool _sorted;
        private void SortPoints()
        {
            if (_sorted) return;
            Points.Sort((a, b) => a.In.CompareTo(b.In));
            _sorted = true;
        }
    }

    // ──────────────────────────────────────────────────────────────────────
    // 第五部分：噪声接口（Perlin/Simplex/Worley/Curl/fBm）
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>噪声类型。</summary>
    public enum NoiseType { Perlin, Simplex, Worley, Curl }

    /// <summary>
    /// 噪声接口：集成 Perlin / Simplex / Worley / Curl 四类 3D 噪声，
    /// 并支持 fBm（分形布朗运动，多倍频叠加）。
    /// </summary>
    public sealed class DIDataInterfaceNoise : FlowVFXDataInterfaceReal
    {
        public NoiseType NoiseType = NoiseType.Perlin;
        public int Octaves = 3;
        public float Frequency = 1f;
        public float Amplitude = 1f;
        public float Lacunarity = 2f;
        public float Gain = 0.5f;

        private readonly RealPerlinNoise3D _perlin;
        private readonly SimplexNoise3D _simplex;
        private readonly WorleyNoise3D _worley;

        public DIDataInterfaceNoise() : this(2024) { }

        public DIDataInterfaceNoise(int seed)
        {
            Type = DIDataInterfaceType.Noise;
            _perlin = new RealPerlinNoise3D(seed);
            _simplex = new SimplexNoise3D(seed);
            _worley = new WorleyNoise3D(seed);
        }

        public override void Reset() { /* 噪声表为预生成，无需重置 */ }

        /// <summary>采样单倍频噪声。</summary>
        public float Sample(Vector3 p)
        {
            Vector3 sp = p * Frequency;
            return NoiseType switch
            {
                NoiseType.Perlin => _perlin.Noise(sp, 1) * Amplitude,
                NoiseType.Simplex => _simplex.Noise(sp) * Amplitude,
                NoiseType.Worley => _worley.Noise(sp) * Amplitude,
                NoiseType.Curl => _perlin.Noise(sp, 1) * Amplitude, // Curl 在 SampleCurl 中返回向量
                _ => 0f
            };
        }

        /// <summary>采样 fBm（分形布朗运动）。</summary>
        public float SampleFbm(Vector3 p)
        {
            float amp = Amplitude, freq = Frequency, sum = 0f, norm = 0f;
            for (int o = 0; o < Octaves; o++)
            {
                Vector3 sp = p * freq;
                float v = NoiseType switch
                {
                    NoiseType.Perlin => _perlin.Noise(sp, 1),
                    NoiseType.Simplex => _simplex.Noise(sp),
                    NoiseType.Worley => _worley.Noise(sp),
                    _ => _perlin.Noise(sp, 1)
                };
                sum += amp * v;
                norm += amp;
                amp *= Gain;
                freq *= Lacunarity;
            }
            return norm > 0f ? sum / norm : 0f;
        }

        /// <summary>采样 Curl 噪声（向量场，无散度）。</summary>
        public Vector3 SampleCurl(Vector3 p, float eps = 0.01f)
        {
            Vector3 sp = p * Frequency;
            // 使用三通道独立 Perlin 作为势场，求旋度
            float nx_y = _perlin.Noise(sp + new Vector3(0, eps, 0), 1) - _perlin.Noise(sp - new Vector3(0, eps, 0), 1);
            float nx_z = _perlin.Noise(sp + new Vector3(0, 0, eps), 1) - _perlin.Noise(sp - new Vector3(0, 0, eps), 1);
            float ny_x = _perlin.Noise(sp + new Vector3(eps, 0, 0), 2) - _perlin.Noise(sp - new Vector3(eps, 0, 0), 2);
            float ny_z = _perlin.Noise(sp + new Vector3(0, 0, eps), 2) - _perlin.Noise(sp - new Vector3(0, 0, eps), 2);
            float nz_x = _perlin.Noise(sp + new Vector3(eps, 0, 0), 3) - _perlin.Noise(sp - new Vector3(eps, 0, 0), 3);
            float nz_y = _perlin.Noise(sp + new Vector3(0, eps, 0), 3) - _perlin.Noise(sp - new Vector3(0, eps, 0), 3);
            float inv = 0.5f / eps;
            return new Vector3(
                (nz_y - ny_z) * inv,
                (nx_z - nz_x) * inv,
                (ny_x - nx_y) * inv) * Amplitude;
        }
    }

    /// <summary>
    /// 3D Simplex 噪声（Stefan Gustavson 实现）。比 Perlin 更快且无方向伪影。
    /// </summary>
    public sealed class SimplexNoise3D
    {
        private readonly int[] _perm = new int[512];
        private readonly int[] _permMod12 = new int[512];
        private static readonly Vector3[] _grad3 =
        {
            new(1,1,0), new(-1,1,0), new(1,-1,0), new(-1,-1,0),
            new(1,0,1), new(-1,0,1), new(1,0,-1), new(-1,0,-1),
            new(0,1,1), new(0,-1,1), new(0,1,-1), new(0,-1,-1)
        };
        private const float F3 = 1f / 3f;
        private const float G3 = 1f / 6f;

        public SimplexNoise3D(int seed)
        {
            var rng = new RealRng((uint)seed);
            int[] p = new int[256];
            for (int i = 0; i < 256; i++) p[i] = i;
            for (int i = 255; i > 0; i--)
            {
                int j = rng.Range(0, i + 1);
                (p[i], p[j]) = (p[j], p[i]);
            }
            for (int i = 0; i < 512; i++)
            {
                _perm[i] = p[i & 255];
                _permMod12[i] = _perm[i] % 12;
            }
        }

        /// <summary>采样 Simplex 噪声，返回 [-1,1]。</summary>
        public float Noise(Vector3 pos)
        {
            float skew = (pos.X + pos.Y + pos.Z) * F3;
            int i = FastFloor(pos.X + skew);
            int j = FastFloor(pos.Y + skew);
            int k = FastFloor(pos.Z + skew);
            float unskew = (i + j + k) * G3;
            float x0 = pos.X - (i - unskew);
            float y0 = pos.Y - (j - unskew);
            float z0 = pos.Z - (k - unskew);

            int i1, j1, k1, i2, j2, k2;
            if (x0 >= y0)
            {
                if (y0 >= z0) { i1 = 1; j1 = 0; k1 = 0; i2 = 1; j2 = 1; k2 = 0; }
                else if (x0 >= z0) { i1 = 1; j1 = 0; k1 = 0; i2 = 1; j2 = 0; k2 = 1; }
                else { i1 = 0; j1 = 0; k1 = 1; i2 = 1; j2 = 0; k2 = 1; }
            }
            else
            {
                if (y0 < z0) { i1 = 0; j1 = 0; k1 = 1; i2 = 0; j2 = 1; k2 = 1; }
                else if (x0 < z0) { i1 = 0; j1 = 1; k1 = 0; i2 = 0; j2 = 1; k2 = 1; }
                else { i1 = 0; j1 = 1; k1 = 0; i2 = 1; j2 = 1; k2 = 0; }
            }

            float x1 = x0 - i1 + G3, y1 = y0 - j1 + G3, z1 = z0 - k1 + G3;
            float x2 = x0 - i2 + 2f * G3, y2 = y0 - j2 + 2f * G3, z2 = z0 - k2 + 2f * G3;
            float x3 = x0 - 1f + 3f * G3, y3 = y0 - 1f + 3f * G3, z3 = z0 - 1f + 3f * G3;

            int ii = i & 255, jj = j & 255, kk = k & 255;
            int gi0 = _permMod12[ii + _perm[jj + _perm[kk]]];
            int gi1 = _permMod12[ii + i1 + _perm[jj + j1 + _perm[kk + k1]]];
            int gi2 = _permMod12[ii + i2 + _perm[jj + j2 + _perm[kk + k2]]];
            int gi3 = _permMod12[ii + 1 + _perm[jj + 1 + _perm[kk + 1]]];

            float n0 = 0f, n1 = 0f, n2 = 0f, n3 = 0f;
            float t0 = 0.6f - x0 * x0 - y0 * y0 - z0 * z0;
            if (t0 >= 0f) { t0 *= t0; n0 = t0 * t0 * Dot(_grad3[gi0], x0, y0, z0); }
            float t1 = 0.6f - x1 * x1 - y1 * y1 - z1 * z1;
            if (t1 >= 0f) { t1 *= t1; n1 = t1 * t1 * Dot(_grad3[gi1], x1, y1, z1); }
            float t2 = 0.6f - x2 * x2 - y2 * y2 - z2 * z2;
            if (t2 >= 0f) { t2 *= t2; n2 = t2 * t2 * Dot(_grad3[gi2], x2, y2, z2); }
            float t3 = 0.6f - x3 * x3 - y3 * y3 - z3 * z3;
            if (t3 >= 0f) { t3 *= t3; n3 = t3 * t3 * Dot(_grad3[gi3], x3, y3, z3); }

            return 32f * (n0 + n1 + n2 + n3); // 归一化到 [-1,1]
        }

        private static float Dot(Vector3 g, float x, float y, float z) => g.X * x + g.Y * y + g.Z * z;
        private static int FastFloor(float x) => x >= 0f ? (int)x : (int)x - 1;
    }

    /// <summary>
    /// 3D Worley（细胞）噪声：返回到最近特征点的距离。
    /// </summary>
    public sealed class WorleyNoise3D
    {
        private readonly Vector3[] _featurePoints;
        private const int GridSize = 8;

        public WorleyNoise3D(int seed)
        {
            var rng = new RealRng((uint)seed);
            // 在 [0, GridSize]^3 网格的每个格子里放一个特征点
            _featurePoints = new Vector3[GridSize * GridSize * GridSize];
            for (int x = 0; x < GridSize; x++)
                for (int y = 0; y < GridSize; y++)
                    for (int z = 0; z < GridSize; z++)
                    {
                        int idx = (x * GridSize + y) * GridSize + z;
                        _featurePoints[idx] = new Vector3(
                            x + rng.NextFloat(),
                            y + rng.NextFloat(),
                            z + rng.NextFloat());
                    }
        }

        /// <summary>采样 Worley 噪声（F1），返回 [0,1]。</summary>
        public float Noise(Vector3 p)
        {
            // 将坐标环绕到网格内（周期性）
            float period = GridSize;
            float px = ((p.X % period) + period) % period;
            float py = ((p.Y % period) + period) % period;
            float pz = ((p.Z % period) + period) % period;

            int cx = (int)px, cy = (int)py, cz = (int)pz;
            float minDist = float.MaxValue;
            // 搜索 3x3x3 邻域
            for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                    for (int dz = -1; dz <= 1; dz++)
                    {
                        int gx = Wrap(cx + dx, GridSize);
                        int gy = Wrap(cy + dy, GridSize);
                        int gz = Wrap(cz + dz, GridSize);
                        int idx = (gx * GridSize + gy) * GridSize + gz;
                        Vector3 fp = _featurePoints[idx] + new Vector3(dx, dy, dz);
                        float d = Vector3.DistanceSquared(new(px, py, pz), fp);
                        if (d < minDist) minDist = d;
                    }
            return Math.Clamp(MathF.Sqrt(minDist), 0f, 1f);
        }

        private static int Wrap(int v, int n) => ((v % n) + n) % n;
    }

    // ──────────────────────────────────────────────────────────────────────
    // 第六部分：向量场接口（3D 纹理三线性插值）
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 向量场接口：3D 纹理采样，三线性插值，世界空间映射。
    /// </summary>
    public sealed class DIDataInterfaceVectorField : FlowVFXDataInterfaceReal
    {
        public Vector3[] Data;        // 三维纹理数据
        public int Width, Height, Depth;
        public Vector3 BoundsMin = new(-5, -5, -5);
        public Vector3 BoundsMax = new(5, 5, 5);
        public bool WorldSpace = true;

        public DIDataInterfaceVectorField() { Type = DIDataInterfaceType.VectorField; }

        public void Initialize(int w, int h, int d)
        {
            Width = w; Height = h; Depth = d;
            Data = new Vector3[w * h * d];
        }

        public override void Reset()
        {
            if (Data != null) Array.Clear(Data, 0, Data.Length);
        }

        /// <summary>设置体素值。</summary>
        public void SetVoxel(int x, int y, int z, Vector3 v)
        {
            if (Data == null) return;
            x = Math.Clamp(x, 0, Width - 1);
            y = Math.Clamp(y, 0, Height - 1);
            z = Math.Clamp(z, 0, Depth - 1);
            Data[(x * Height + y) * Depth + z] = v;
        }

        /// <summary>世界坐标采样（三线性插值）。</summary>
        public Vector3 SampleWorld(Vector3 worldPos)
        {
            if (Data == null || Width == 0) return Vector3.Zero;
            Vector3 uvw;
            if (WorldSpace)
            {
                uvw = new Vector3(
                    (worldPos.X - BoundsMin.X) / Math.Max(1e-6f, BoundsMax.X - BoundsMin.X),
                    (worldPos.Y - BoundsMin.Y) / Math.Max(1e-6f, BoundsMax.Y - BoundsMin.Y),
                    (worldPos.Z - BoundsMin.Z) / Math.Max(1e-6f, BoundsMax.Z - BoundsMin.Z));
            }
            else
            {
                uvw = worldPos;
            }
            // 映射到体素坐标 [0, W-1]
            float fx = Math.Clamp(uvw.X, 0f, 1f) * (Width - 1);
            float fy = Math.Clamp(uvw.Y, 0f, 1f) * (Height - 1);
            float fz = Math.Clamp(uvw.Z, 0f, 1f) * (Depth - 1);
            return Trilinear(fx, fy, fz);
        }

        /// <summary>三线性插值。</summary>
        private Vector3 Trilinear(float fx, float fy, float fz)
        {
            int x0 = (int)MathF.Floor(fx), y0 = (int)MathF.Floor(fy), z0 = (int)MathF.Floor(fz);
            int x1 = Math.Min(x0 + 1, Width - 1);
            int y1 = Math.Min(y0 + 1, Height - 1);
            int z1 = Math.Min(z0 + 1, Depth - 1);
            x0 = Math.Clamp(x0, 0, Width - 1);
            y0 = Math.Clamp(y0, 0, Height - 1);
            z0 = Math.Clamp(z0, 0, Depth - 1);
            float tx = fx - x0, ty = fy - y0, tz = fz - z0;

            Vector3 c000 = Data[(x0 * Height + y0) * Depth + z0];
            Vector3 c100 = Data[(x1 * Height + y0) * Depth + z0];
            Vector3 c010 = Data[(x0 * Height + y1) * Depth + z0];
            Vector3 c110 = Data[(x1 * Height + y1) * Depth + z0];
            Vector3 c001 = Data[(x0 * Height + y0) * Depth + z1];
            Vector3 c101 = Data[(x1 * Height + y0) * Depth + z1];
            Vector3 c011 = Data[(x0 * Height + y1) * Depth + z1];
            Vector3 c111 = Data[(x1 * Height + y1) * Depth + z1];

            Vector3 c00 = Vector3.Lerp(c000, c100, tx);
            Vector3 c10 = Vector3.Lerp(c010, c110, tx);
            Vector3 c01 = Vector3.Lerp(c001, c101, tx);
            Vector3 c11 = Vector3.Lerp(c011, c111, tx);
            Vector3 c0 = Vector3.Lerp(c00, c10, ty);
            Vector3 c1 = Vector3.Lerp(c01, c11, ty);
            return Vector3.Lerp(c0, c1, tz);
        }
    }

    // ──────────────────────────────────────────────────────────────────────
    // 第七部分：三角网格查找接口
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 三角网格查找接口：在网格表面采样属性（位置/法线/UV）。
    /// 通过最近点投影到最近三角形。
    /// </summary>
    public sealed class DIDataInterfaceTriangleLookup : FlowVFXDataInterfaceReal
    {
        public Vector3[] Vertices = Array.Empty<Vector3>();
        public Vector3[] Normals = Array.Empty<Vector3>();
        public Vector2[] UVs = Array.Empty<Vector2>();
        public int[] Indices = Array.Empty<int>();

        public DIDataInterfaceTriangleLookup() { Type = DIDataInterfaceType.TriangleLookup; }

        public override void Reset() { /* 数据由外部填充 */ }

        /// <summary>查找离 query 最近的三角形索引（暴力最近）。</summary>
        public int FindClosestTriangle(Vector3 query)
        {
            int best = -1;
            float bestDist = float.MaxValue;
            for (int i = 0; i < Indices.Length / 3; i++)
            {
                Vector3 a = Vertices[Indices[i * 3]];
                Vector3 b = Vertices[Indices[i * 3 + 1]];
                Vector3 c = Vertices[Indices[i * 3 + 2]];
                Vector3 cp = ClosestPointOnTriangle(query, a, b, c);
                float d = Vector3.DistanceSquared(query, cp);
                if (d < bestDist) { bestDist = d; best = i; }
            }
            return best;
        }

        /// <summary>采样最近三角形的属性。</summary>
        public Vector3 SampleNormal(Vector3 query)
        {
            int tri = FindClosestTriangle(query);
            if (tri < 0) return Vector3.UnitZ;
            Vector3 n0 = Normals[Indices[tri * 3]];
            Vector3 n1 = Normals[Indices[tri * 3 + 1]];
            Vector3 n2 = Normals[Indices[tri * 3 + 2]];
            return Vector3.Normalize(n0 + n1 + n2);
        }

        /// <summary>点 P 到三角形 ABC 的最近点（基于 Ericson Real-Time Collision Detection）。</summary>
        public static Vector3 ClosestPointOnTriangle(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
        {
            Vector3 ab = b - a, ac = c - a, ap = p - a;
            float d1 = Vector3.Dot(ab, ap), d2 = Vector3.Dot(ac, ap);
            if (d1 <= 0f && d2 <= 0f) return a;

            Vector3 bp = p - b;
            float d3 = Vector3.Dot(ab, bp), d4 = Vector3.Dot(ac, bp);
            if (d3 >= 0f && d4 <= d3) return b;

            float vc = d1 * d4 - d3 * d2;
            if (vc <= 0f && d1 >= 0f && d3 <= 0f)
            {
                float v = d1 / (d1 - d3);
                return a + ab * v;
            }

            Vector3 cp = p - c;
            float d5 = Vector3.Dot(ab, cp), d6 = Vector3.Dot(ac, cp);
            if (d6 >= 0f && d5 <= d6) return c;

            float vb = d5 * d2 - d1 * d6;
            if (vb <= 0f && d2 >= 0f && d6 <= 0f)
            {
                float w = d2 / (d2 - d6);
                return a + ac * w;
            }

            float va = d3 * d6 - d5 * d4;
            if (va <= 0f && (d4 - d3) >= 0f && (d5 - d6) >= 0f)
            {
                float w = (d4 - d3) / ((d4 - d3) + (d5 - d6));
                return b + (c - b) * w;
            }

            float denom = 1f / (va + vb + vc);
            float vn = vb * denom, wn = vc * denom;
            return a + ab * vn + ac * wn;
        }
    }

    // ──────────────────────────────────────────────────────────────────────
    // 第八部分：2D 网格接口（粒子→网格密度）
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 2D 网格接口：将粒子栅格化为密度场，支持网格→粒子的反馈力。
    /// </summary>
    public sealed class DIDataInterfaceGrid2D : FlowVFXDataInterfaceReal
    {
        public int GridW = 64, GridH = 64;
        public Vector2 BoundsMin = new(-10, -10);
        public Vector2 BoundsMax = new(10, 10);
        public float[] Density;
        public float[] VelocityX;
        public float[] VelocityY;

        public DIDataInterfaceGrid2D() { Type = DIDataInterfaceType.Grid2D; Initialize(); }

        public void Initialize()
        {
            Density = new float[GridW * GridH];
            VelocityX = new float[GridW * GridH];
            VelocityY = new float[GridW * GridH];
        }

        public override void Reset()
        {
            if (Density != null) Array.Clear(Density, 0, Density.Length);
            if (VelocityX != null) Array.Clear(VelocityX, 0, VelocityX.Length);
            if (VelocityY != null) Array.Clear(VelocityY, 0, VelocityY.Length);
        }

        /// <summary>世界坐标→网格索引。</summary>
        public (int gx, int gy) WorldToGrid(Vector2 world)
        {
            float u = (world.X - BoundsMin.X) / Math.Max(1e-6f, BoundsMax.X - BoundsMin.X);
            float v = (world.Y - BoundsMin.Y) / Math.Max(1e-6f, BoundsMax.Y - BoundsMin.Y);
            int gx = Math.Clamp((int)(u * GridW), 0, GridW - 1);
            int gy = Math.Clamp((int)(v * GridH), 0, GridH - 1);
            return (gx, gy);
        }

        /// <summary>从粒子列表累积密度场（双线性散布）。</summary>
        public void RasterizeFromParticles(FlowVFXParticleDataReal particles)
        {
            Reset();
            for (int i = 0; i < particles.ActiveCount; i++)
            {
                int idx = particles.ActiveIndices[i];
                Vector3 p = particles.Position[idx];
                Vector2 world = new(p.X, p.Y);
                float u = (world.X - BoundsMin.X) / Math.Max(1e-6f, BoundsMax.X - BoundsMin.X);
                float v = (world.Y - BoundsMin.Y) / Math.Max(1e-6f, BoundsMax.Y - BoundsMin.Y);
                float fx = Math.Clamp(u * GridW, 0f, GridW - 1.001f);
                float fy = Math.Clamp(v * GridH, 0f, GridH - 1.001f);
                int x0 = (int)fx, y0 = (int)fy;
                int x1 = x0 + 1, y1 = y0 + 1;
                float tx = fx - x0, ty = fy - y0;
                // 双线性散布
                AddDensity(x0, y0, (1 - tx) * (1 - ty));
                AddDensity(x1, y0, tx * (1 - ty));
                AddDensity(x0, y1, (1 - tx) * ty);
                AddDensity(x1, y1, tx * ty);
                // 速度累积
                Vector3 vel = particles.Velocity[idx];
                AddVelocity(x0, y0, vel.X * (1 - tx) * (1 - ty), vel.Y * (1 - tx) * (1 - ty));
                AddVelocity(x1, y0, vel.X * tx * (1 - ty), vel.Y * tx * (1 - ty));
                AddVelocity(x0, y1, vel.X * (1 - tx) * ty, vel.Y * (1 - tx) * ty);
                AddVelocity(x1, y1, vel.X * tx * ty, vel.Y * tx * ty);
            }
        }

        private void AddDensity(int x, int y, float w)
        {
            if (x < 0 || x >= GridW || y < 0 || y >= GridH) return;
            Density[y * GridW + x] += w;
        }

        private void AddVelocity(int x, int y, float vx, float vy)
        {
            if (x < 0 || x >= GridW || y < 0 || y >= GridH) return;
            VelocityX[y * GridW + x] += vx;
            VelocityY[y * GridW + x] += vy;
        }

        /// <summary>网格→粒子反馈：返回网格速度（双线性采样）。</summary>
        public Vector2 SampleGridVelocity(Vector2 world)
        {
            float u = (world.X - BoundsMin.X) / Math.Max(1e-6f, BoundsMax.X - BoundsMin.X);
            float v = (world.Y - BoundsMin.Y) / Math.Max(1e-6f, BoundsMax.Y - BoundsMin.Y);
            float fx = Math.Clamp(u * GridW, 0f, GridW - 1.001f);
            float fy = Math.Clamp(v * GridH, 0f, GridH - 1.001f);
            int x0 = (int)fx, y0 = (int)fy;
            int x1 = x0 + 1, y1 = y0 + 1;
            float tx = fx - x0, ty = fy - y0;
            float vx = Bilinear(VelocityX, x0, y0, x1, y1, tx, ty);
            float vy = Bilinear(VelocityY, x0, y0, x1, y1, tx, ty);
            return new Vector2(vx, vy);
        }

        private float Bilinear(float[] arr, int x0, int y0, int x1, int y1, float tx, float ty)
        {
            float c00 = arr[y0 * GridW + x0];
            float c10 = arr[y0 * GridW + x1];
            float c01 = arr[y1 * GridW + x0];
            float c11 = arr[y1 * GridW + x1];
            return (c00 * (1 - tx) + c10 * tx) * (1 - ty) + (c01 * (1 - tx) + c11 * tx) * ty;
        }
    }

    // ──────────────────────────────────────────────────────────────────────
    // 第九部分：稀疏网格接口（体素化 + 哈希查找）
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 稀疏网格接口：只存储被占据的体素，使用字典哈希查找，节省内存。
    /// 键 = 体素坐标三元组打包的 long。
    /// </summary>
    public sealed class DIDataInterfaceSparseGrid : FlowVFXDataInterfaceReal
    {
        public float VoxelSize = 0.5f;
        public Dictionary<long, VoxelData> Voxels = new();

        public DIDataInterfaceSparseGrid() { Type = DIDataInterfaceType.SparseGrid; }

        public override void Reset() => Voxels.Clear();

        /// <summary>世界坐标→体素坐标。</summary>
        public (int x, int y, int z) WorldToVoxel(Vector3 world)
        {
            float inv = 1f / Math.Max(1e-6f, VoxelSize);
            return (
                (int)MathF.Floor(world.X * inv),
                (int)MathF.Floor(world.Y * inv),
                (int)MathF.Floor(world.Z * inv));
        }

        /// <summary>打包体素坐标为 long 键。</summary>
        public static long PackVoxel(int x, int y, int z)
        {
            // 将 [-32768,32767] 范围映射到 uint16 再打包
            uint ux = (uint)(x + 32768);
            uint uy = (uint)(y + 32768);
            uint uz = (uint)(z + 32768);
            return ((long)(ux & 0xFFFF) << 32) | ((long)(uy & 0xFFFF) << 16) | (uz & 0xFFFF);
        }

        /// <summary>向体素写入数据。</summary>
        public void SetVoxel(Vector3 world, Vector3 value)
        {
            var (x, y, z) = WorldToVoxel(world);
            long key = PackVoxel(x, y, z);
            if (Voxels.TryGetValue(key, out VoxelData vd))
            {
                vd.Value += value;
                vd.Count++;
                Voxels[key] = vd;
            }
            else
            {
                Voxels[key] = new VoxelData { Value = value, Count = 1 };
            }
        }

        /// <summary>哈希查找体素。</summary>
        public VoxelData? GetVoxel(int x, int y, int z)
        {
            long key = PackVoxel(x, y, z);
            return Voxels.TryGetValue(key, out VoxelData vd) ? vd : null;
        }

        /// <summary>采样世界坐标处的体素（无插值）。</summary>
        public Vector3 SampleWorld(Vector3 world)
        {
            var (x, y, z) = WorldToVoxel(world);
            var vd = GetVoxel(x, y, z);
            return vd != null ? vd.Value.Value : Vector3.Zero;
        }

        /// <summary>从粒子列表栅格化。</summary>
        public void RasterizeFromParticles(FlowVFXParticleDataReal particles)
        {
            Reset();
            for (int i = 0; i < particles.ActiveCount; i++)
            {
                int idx = particles.ActiveIndices[i];
                SetVoxel(particles.Position[idx], particles.Velocity[idx]);
            }
        }
    }

    /// <summary>体素数据。</summary>
    public struct VoxelData
    {
        public Vector3 Value;  // 累积值
        public int Count;      // 占据粒子数
        public Vector3 Average => Count > 0 ? Value / Count : Vector3.Zero;
    }

    // ──────────────────────────────────────────────────────────────────────
    // 第十部分：邻居网格接口（空间哈希 + 邻居查询）
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 邻居网格接口：使用空间哈希加速粒子间邻居查询。
    /// 每个哈希桶存储该体素内的粒子索引列表。
    /// </summary>
    public sealed class DIDataInterfaceNeighborGrid : FlowVFXDataInterfaceReal
    {
        public float CellSize = 0.5f;
        public float QueryRadius = 1f;
        private Dictionary<long, List<int>> _cells = new();
        private List<int> _empty = new();

        public DIDataInterfaceNeighborGrid() { Type = DIDataInterfaceType.NeighborGrid; }

        public override void Reset() => _cells.Clear();

        /// <summary>从粒子列表构建空间哈希。</summary>
        public void Build(FlowVFXParticleDataReal particles)
        {
            _cells.Clear();
            float inv = 1f / Math.Max(1e-6f, CellSize);
            for (int i = 0; i < particles.ActiveCount; i++)
            {
                int idx = particles.ActiveIndices[i];
                Vector3 p = particles.Position[idx];
                long key = PackCell((int)MathF.Floor(p.X * inv), (int)MathF.Floor(p.Y * inv), (int)MathF.Floor(p.Z * inv));
                if (!_cells.TryGetValue(key, out List<int> list))
                {
                    list = new List<int>(8);
                    _cells[key] = list;
                }
                list.Add(idx);
            }
        }

        private static long PackCell(int x, int y, int z) =>
            ((long)(x + 32768) << 32) | ((long)(y + 32768) << 16) | (z + 32768);

        /// <summary>查询半径内的邻居粒子索引。</summary>
        public List<int> QueryNeighbors(FlowVFXParticleDataReal particles, Vector3 center)
        {
            float radiusSq = QueryRadius * QueryRadius;
            float inv = 1f / Math.Max(1e-6f, CellSize);
            int cx = (int)MathF.Floor(center.X * inv);
            int cy = (int)MathF.Floor(center.Y * inv);
            int cz = (int)MathF.Floor(center.Z * inv);
            int span = (int)MathF.Ceiling(QueryRadius * inv);

            var result = new List<int>(16);
            for (int dx = -span; dx <= span; dx++)
                for (int dy = -span; dy <= span; dy++)
                    for (int dz = -span; dz <= span; dz++)
                    {
                        long key = PackCell(cx + dx, cy + dy, cz + dz);
                        if (!_cells.TryGetValue(key, out List<int> list)) continue;
                        for (int i = 0; i < list.Count; i++)
                        {
                            int idx = list[i];
                            if (Vector3.DistanceSquared(particles.Position[idx], center) <= radiusSq)
                                result.Add(idx);
                        }
                    }
            return result;
        }

        /// <summary>查询某粒子周围最近 K 个邻居（按距离排序）。</summary>
        public List<int> QueryKNearest(FlowVFXParticleDataReal particles, int particleIndex, int k)
        {
            if (particleIndex < 0 || particleIndex >= particles.Capacity) return _empty;
            Vector3 center = particles.Position[particleIndex];
            var neighbors = QueryNeighbors(particles, center);
            // 按距离排序取前 K
            neighbors.Sort((a, b) =>
                Vector3.DistanceSquared(particles.Position[a], center)
                    .CompareTo(Vector3.DistanceSquared(particles.Position[b], center)));
            if (neighbors.Count > k) neighbors.RemoveRange(k, neighbors.Count - k);
            return neighbors;
        }
    }
}
