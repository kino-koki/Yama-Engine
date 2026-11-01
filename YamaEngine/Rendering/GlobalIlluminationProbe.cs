using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using YamaEngine.ECS;
using YamaEngine.RHI;
using YamaEngine.Shaders;

namespace YamaEngine.Rendering
{
    // ════════════════════════════════════════════════════════════════════════
    // yama 全局光照探针系统 (Global Illumination Probe System)
    //
    // 基于"探针"的空间辐照度/反射缓存系统，为漫反射与镜面间接光提供低频
    // 环境光照。与 ProbeField 实时 GI 互补：ProbeField 提供高频动态 GI，探针系统提供
    // 稳定的低频环境光基础层（尤其适用于静态场景与远场）。
    //
    // 核心组件:
    //   1. SphericalHarmonics: 球谐函数工具(SH9 三阶 / SH4 二阶)，用于将
    //      Cubemap 辐照度投影为低阶 SH 系数并重建方向辐照度。
    //   2. GIProbe: 单个 GI 探针，存储 SH 系数、位置、范围、自身偏移。
    //   3. ProbeGrid: 探针网格，均匀布局 + 几何密集区域自适应加密 + 探针剔除。
    //   4. ProbeVolume: 探针体积，管理一个区域内所有探针的生命周期。
    //   5. IrradianceVolume: 辐照度体积，用 3D 纹理存储 SH 系数，支持三线性
    //      插值采样与视差校正(Parallax Correction)。
    //   6. ProbeBaker: 探针烘焙器，每探针渲染 Cubemap 并投影到 SH，支持增量
    //      烘焙(分摊到多帧)与优先级调度(变化区域优先)。
    //   7. DynamicGIProbe: 动态探针，运行时基于渲染帧反馈更新，时间累积平滑。
    //   8. ReflectionProbe: 反射探针(Cubemap 反射)，区分静态(烘焙)/动态(实时)，
    //      支持多探针混合平滑过渡。
    //   9. ProbeBlending: 探针混合系统，最近邻/三线性/权重混合，探针权重图
    //      避免穿墙(权重图记录墙体两侧探针不可见性)。
    //
    // 对接:
    //   - ProbeFieldGlobalIllumination: 复用 ProbeFieldGISettings 的探针网格配置，
    //     作为 ProbeField 的远场/低频补充层，由 ProbeFieldGlobalIllumination.Update 调用。
    //   - RHI: Cubemap(RenderTarget Cube)、3D 纹理(SH 系数体积)。
    //   - ECS: 通过 GIProbeVolumeComponent / ReflectionProbeComponent 组件
    //     声明场景中的探针体积与反射探针。
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>球谐阶数。</summary>
    public enum SHOrder
    {
        /// <summary>2阶(SH4): 4 个系数/通道，最低开销，适合极低频漫反射。</summary>
        Order2 = 2,
        /// <summary>3阶(SH9): 9 个系数/通道，标准漫反射辐照度精度。</summary>
        Order3 = 3
    }

    /// <summary>
    /// 球谐函数(Spherical Harmonics)工具: 提供 SH 基函数求值、Cubemap 投影、
    /// 方向辐照度重建与三阶/二阶切换。SH9 共 9 个系数，每系数 RGB 三通道。
    /// </summary>
    public static class SphericalHarmonics
    {
        /// <summary>SH9 系数数量。</summary>
        public const int SH9Count = 9;
        /// <summary>SH4 系数数量。</summary>
        public const int SH4Count = 4;

        /// <summary>
        /// 评估三阶 SH 基函数 Y_l^m(dir)。
        /// 返回 9 个基函数值(按 l,m 顺序: Y00, Y1-1, Y10, Y1+1, Y2-2, Y2-1, Y20, Y2+1, Y2+2)。
        /// </summary>
        public static float[] EvaluateSH9(Vector3 dir)
        {
            float x = dir.X, y = dir.Y, z = dir.Z;
            return new float[]
            {
                0.282095f,                                         // Y00  (l=0,m=0)
                -0.488603f * y,                                     // Y1-1 (l=1,m=-1)
                0.488603f * z,                                      // Y10  (l=1,m=0)
                -0.488603f * x,                                     // Y1+1 (l=1,m=+1)
                1.092548f * x * y,                                  // Y2-2 (l=2,m=-2)
                -1.092548f * y * z,                                 // Y2-1 (l=2,m=-1)
                0.315392f * (3.0f * z * z - 1.0f),                  // Y20  (l=2,m=0)
                -1.092548f * x * z,                                 // Y2+1 (l=2,m=+1)
                0.546274f * (x * x - y * y)                         // Y2+2 (l=2,m=+2)
            };
        }

        /// <summary>评估二阶 SH 基函数(前 4 个)。</summary>
        public static float[] EvaluateSH4(Vector3 dir)
        {
            float x = dir.X, y = dir.Y, z = dir.Z;
            return new float[]
            {
                0.282095f,        // Y00
                -0.488603f * y,   // Y1-1
                0.488603f * z,    // Y10
                -0.488603f * x    // Y1+1
            };
        }

        /// <summary>
        /// 将一个方向辐照度样本投影(累加)到 SH 系数。
        /// </summary>
        /// <param name="sh">SH 系数数组(9 个 x 3 通道 = 27 float, 或 4 x 3 = 12)。</param>
        /// <param name="dir">样本方向(归一化)。</param>
        /// <param name="radiance">该方向辐照度(RGB)。</param>
        /// <param name="order">阶数。</param>
        public static void Project(Span<float> sh, Vector3 dir, Vector3 radiance, SHOrder order)
        {
            float[] basis = order == SHOrder.Order3 ? EvaluateSH9(dir) : EvaluateSH4(dir);
            int count = order == SHOrder.Order3 ? SH9Count : SH4Count;
            for (int i = 0; i < count; i++)
            {
                sh[i * 3 + 0] += basis[i] * radiance.X;
                sh[i * 3 + 1] += basis[i] * radiance.Y;
                sh[i * 3 + 2] += basis[i] * radiance.Z;
            }
        }

        /// <summary>
        /// 由 SH 系数重建某方向的辐照度(漫反射卷积近似)。
        /// 使用 Ramamoorthi-Hanrahan 的余弦卷积系数(常数因子)。
        /// </summary>
        public static Vector3 EvaluateIrradiance(ReadOnlySpan<float> sh, Vector3 normal, SHOrder order)
        {
            float[] basis = order == SHOrder.Order3 ? EvaluateSH9(normal) : EvaluateSH4(normal);
            // 余弦卷积的 SH 系数(Ramamoorthi 2001)
            // c0 = 0.886226925 (pi), c1 = 1.023317 (2pi/3), c2 = 0.247708 (pi/4)
            float c0 = 0.886226925f, c1 = 1.023316f, c2 = 0.247708f;
            int count = order == SHOrder.Order3 ? SH9Count : SH4Count;
            float[] conv = order == SHOrder.Order3
                ? new float[] { c0, c1, c1, c1, c2, c2, c2, c2, c2 }
                : new float[] { c0, c1, c1, c1 };

            Vector3 result = Vector3.Zero;
            for (int i = 0; i < count; i++)
            {
                result.X += sh[i * 3 + 0] * basis[i] * conv[i];
                result.Y += sh[i * 3 + 1] * basis[i] * conv[i];
                result.Z += sh[i * 3 + 2] * basis[i] * conv[i];
            }
            return result;
        }

        /// <summary>归一化 SH 系数(除以样本数)。</summary>
        public static void Normalize(Span<float> sh, int sampleCount, SHOrder order)
        {
            int count = (order == SHOrder.Order3 ? SH9Count : SH4Count) * 3;
            float inv = sampleCount > 0 ? 1.0f / sampleCount : 0.0f;
            // 应用 4*PI 球面积分归一化
            float norm = inv * 4.0f * MathF.PI;
            for (int i = 0; i < count; i++) sh[i] *= norm;
        }
    }

    /// <summary>
    /// GI 探针: 存储球谐辐照度系数、位置、影响范围与自身偏移。
    /// 自身偏移(Self Offset)用于将探针采样点偏移到几何体外侧，避免探针位于
    /// 墙体/地面内部时采到错误的环境光（如探针在墙内会采到全黑）。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct GIProbe
    {
        /// <summary>SH 系数(SH9: 27 float; SH4: 12 float, 这里固定用 27 容量)。</summary>
        public fixed float SH[27];
        /// <summary>探针世界位置。</summary>
        public Vector3 Position;
        /// <summary>影响半径(超出则无贡献)。</summary>
        public float Range;
        /// <summary>自身偏移(沿法线方向偏移采样点)。</summary>
        public Vector3 SelfOffset;
        /// <summary>平均深度(用于视差校正与遮挡判断)。</summary>
        public float MeanDepth;
        /// <summary>是否已烘焙/激活。</summary>
        public int IsActive;
        /// <summary>上次更新时间戳(用于增量烘焙调度)。</summary>
        public float LastUpdateTime;
        /// <summary>烘焙优先级(数值越大越优先)。</summary>
        public float BakePriority;
        /// <summary>累积采样数(时间平滑用)。</summary>
        public uint AccumulatedSamples;

        /// <summary>累加一个辐照度样本到 SH 系数。</summary>
        public void Accumulate(Vector3 dir, Vector3 radiance, SHOrder order)
        {
            // 通过 fixed 指针将 SH 数组转为 Span 供工具函数操作
            unsafe
            {
                fixed (float* p = SH)
                {
                    Span<float> sh = new Span<float>(p, 27);
                    SphericalHarmonics.Project(sh, dir, radiance, order);
                }
            }
            AccumulatedSamples++;
        }

        /// <summary>查询某方向的漫反射辐照度。</summary>
        public Vector3 QueryIrradiance(Vector3 normal, SHOrder order)
        {
            unsafe
            {
                fixed (float* p = SH)
                {
                    ReadOnlySpan<float> sh = new ReadOnlySpan<float>(p, 27);
                    return SphericalHarmonics.EvaluateIrradiance(sh, normal, order);
                }
            }
        }

        /// <summary>归一化 SH 系数。</summary>
        public void NormalizeSH(SHOrder order)
        {
            unsafe
            {
                fixed (float* p = SH)
                {
                    Span<float> sh = new Span<float>(p, 27);
                    SphericalHarmonics.Normalize(sh, (int)AccumulatedSamples, order);
                }
            }
        }
    }

    /// <summary>
    /// 探针网格: 三维均匀分布的 GI 探针集合。
    /// 支持几何密集区域自适应加密(在三角形密集的体素增加探针)与探针剔除
    /// (被几何体完全包围的探针标记为不活跃，避免浪费烘焙预算)。
    /// </summary>
    public sealed class ProbeGrid : IDisposable
    {
        private GIProbe[] _probes;
        private RHIBuffer? _probeBuffer;
        private readonly List<int> _activeProbeIndices = new();

        /// <summary>网格分辨率(每轴探针数)。</summary>
        public Vector3 Resolution { get; }
        /// <summary>世界范围(半尺寸)。</summary>
        public Vector3 Extent { get; }
        /// <summary>网格原点(中心)。</summary>
        public Vector3 Origin { get; set; }
        /// <summary>SH 阶数。</summary>
        public SHOrder Order { get; }
        /// <summary>探针总数。</summary>
        public int ProbeCount => _probes.Length;

        /// <summary>构造均匀探针网格。</summary>
        public ProbeGrid(Vector3 resolution, Vector3 extent, SHOrder order = SHOrder.Order3)
        {
            Resolution = resolution;
            Extent = extent;
            Order = order;
            int count = (int)(resolution.X * resolution.Y * resolution.Z);
            _probes = new GIProbe[count];
            InitializeProbePositions();
        }

        /// <summary>初始化各探针的世界位置(均匀网格)。</summary>
        private void InitializeProbePositions()
        {
            int rx = (int)Resolution.X, ry = (int)Resolution.Y, rz = (int)Resolution.Z;
            Vector3 cellSize = new(
                Extent.X * 2.0f / Math.Max(1, rx - 1),
                Extent.Y * 2.0f / Math.Max(1, ry - 1),
                Extent.Z * 2.0f / Math.Max(1, rz - 1));

            for (int z = 0; z < rz; z++)
                for (int y = 0; y < ry; y++)
                    for (int x = 0; x < rx; x++)
                    {
                        int idx = x + y * rx + z * rx * ry;
                        _probes[idx].Position = Origin + new Vector3(
                            -Extent.X + x * cellSize.X,
                            -Extent.Y + y * cellSize.Y,
                            -Extent.Z + z * cellSize.Z);
                        _probes[idx].Range = Math.Max(cellSize.X, Math.Max(cellSize.Y, cellSize.Z)) * 1.5f;
                        _probes[idx].IsActive = 1;
                    }
        }

        /// <summary>
        /// 自适应加密: 在几何密集区域(三角形数高的体素)增加探针。
        /// 此处为 CPU 侧框架，实际由编辑器根据场景几何密度图调用。
        /// </summary>
        /// <param name="densityMap">三维密度图(每体素三角形数)。</param>
        /// <param name="threshold">加密阈值(超过则加密)。</param>
        public void AdaptToDensity(int[,,] densityMap, int threshold)
        {
            // 框架实现: 标记高密度区域的探针优先级提升
            int rx = (int)Resolution.X, ry = (int)Resolution.Y, rz = (int)Resolution.Z;
            for (int z = 0; z < Math.Min(rz, densityMap.GetLength(2)); z++)
                for (int y = 0; y < Math.Min(ry, densityMap.GetLength(1)); y++)
                    for (int x = 0; x < Math.Min(rx, densityMap.GetLength(0)); x++)
                    {
                        int idx = x + y * rx + z * rx * ry;
                        if (densityMap[x, y, z] > threshold)
                            _probes[idx].BakePriority += densityMap[x, y, z] * 0.01f;
                    }
        }

        /// <summary>
        /// 探针剔除: 将被几何体完全包围(位于实体内部)的探针标记为不活跃。
        /// 避免在墙体/大地内部浪费烘焙预算（这些探针采到的是黑色环境光）。
        /// </summary>
        /// <param name="isInsideSolid">判断某点是否在实体内的回调。</param>
        public void CullEmbeddedProbes(Func<Vector3, bool> isInsideSolid)
        {
            _activeProbeIndices.Clear();
            for (int i = 0; i < _probes.Length; i++)
            {
                if (isInsideSolid(_probes[i].Position + _probes[i].SelfOffset))
                {
                    _probes[i].IsActive = 0; // 剔除
                }
                else
                {
                    _probes[i].IsActive = 1;
                    _activeProbeIndices.Add(i);
                }
            }
        }

        /// <summary>分配 GPU 结构化缓冲。</summary>
        public void Initialize(RHIDevice device)
        {
            var desc = new RHIBufferDesc
            {
                SizeInBytes = (ulong)(_probes.Length * Marshal.SizeOf<GIProbe>()),
                Flags = RHIBufferFlags.Structured | RHIBufferFlags.UnorderedAccess | RHIBufferFlags.ShaderResource,
                Stride = (ulong)Marshal.SizeOf<GIProbe>(),
                HeapType = RHIResourceHeapType.Default
            };
            _probeBuffer = device.CreateBuffer(desc, "GIProbeGridBuffer");
        }

        /// <summary>获取探针(索引)。</summary>
        public ref GIProbe GetProbe(int index) => ref _probes[index];

        /// <summary>获取所有活跃探针索引。</summary>
        public IReadOnlyList<int> ActiveProbeIndices => _activeProbeIndices;

        /// <summary>
        /// 三线性插值采样 8 个邻近探针的辐照度。
        /// </summary>
        public Vector3 SampleIrradianceTrilinear(Vector3 worldPos, Vector3 normal)
        {
            Vector3 local = (worldPos - Origin + Extent) / (Extent * 2.0f);
            Vector3 coord = local * Resolution - Vector3.One * 0.5f;

            int rx = (int)Resolution.X, ry = (int)Resolution.Y, rz = (int)Resolution.Z;
            int x0 = (int)Math.Clamp(MathF.Floor(coord.X), 0, rx - 1);
            int y0 = (int)Math.Clamp(MathF.Floor(coord.Y), 0, ry - 1);
            int z0 = (int)Math.Clamp(MathF.Floor(coord.Z), 0, rz - 1);
            int x1 = Math.Min(x0 + 1, rx - 1);
            int y1 = Math.Min(y0 + 1, ry - 1);
            int z1 = Math.Min(z0 + 1, rz - 1);

            Vector3 f = new(coord.X - x0, coord.Y - y0, coord.Z - z0);

            Vector3 Acc(int x, int y, int z, float w)
            {
                if (w <= 0) return Vector3.Zero;
                int idx = x + y * rx + z * rx * ry;
                if (idx < 0 || idx >= _probes.Length || _probes[idx].IsActive == 0) return Vector3.Zero;
                return _probes[idx].QueryIrradiance(normal, Order) * w;
            }

            return Acc(x0, y0, z0, (1 - f.X) * (1 - f.Y) * (1 - f.Z))
                 + Acc(x1, y0, z0, f.X * (1 - f.Y) * (1 - f.Z))
                 + Acc(x0, y1, z0, (1 - f.X) * f.Y * (1 - f.Z))
                 + Acc(x1, y1, z0, f.X * f.Y * (1 - f.Z))
                 + Acc(x0, y0, z1, (1 - f.X) * (1 - f.Y) * f.Z)
                 + Acc(x1, y0, z1, f.X * (1 - f.Y) * f.Z)
                 + Acc(x0, y1, z1, (1 - f.X) * f.Y * f.Z)
                 + Acc(x1, y1, z1, f.X * f.Y * f.Z);
        }

        /// <summary>释放。</summary>
        public void Dispose()
        {
            _probeBuffer?.Dispose();
            GC.SuppressFinalize(this);
        }
    }

    /// <summary>
    /// 探针体积: 管理一个世界区域内所有探针网格的生命周期。
    /// 一个场景可包含多个探针体积(如室内/室外分别配置不同密度)。
    /// 通过 ECS 组件声明，运行时根据玩家位置激活/停用体积。
    /// </summary>
    public sealed class ProbeVolume : IDisposable
    {
        /// <summary>体积唯一 Id。</summary>
        public Guid Id { get; } = Guid.NewGuid();
        /// <summary>体积名称。</summary>
        public string Name { get; set; } = "ProbeVolume";
        /// <summary>世界边界(中心 + 半尺寸)。</summary>
        public Vector3 Center { get; set; }
        /// <summary>半尺寸。</summary>
        public Vector3 HalfExtents { get; set; }
        /// <summary>包含的探针网格。</summary>
        public ProbeGrid? Grid { get; private set; }
        /// <summary>是否激活(运行时)。</summary>
        public bool IsActive { get; set; } = true;
        /// <summary>优先级(多个体积重叠时)。</summary>
        public float Priority { get; set; } = 1.0f;

        /// <summary>构造探针体积。</summary>
        public ProbeVolume(Vector3 center, Vector3 halfExtents, Vector3 gridResolution,
            SHOrder order = SHOrder.Order3)
        {
            Center = center;
            HalfExtents = halfExtents;
            Grid = new ProbeGrid(gridResolution, halfExtents, order) { Origin = center };
        }

        /// <summary>初始化 GPU 资源。</summary>
        public void Initialize(RHIDevice device) => Grid?.Initialize(device);

        /// <summary>点是否在体积内。</summary>
        public bool Contains(Vector3 p)
        {
            Vector3 d = Vector3.Abs(p - Center);
            return d.X <= HalfExtents.X && d.Y <= HalfExtents.Y && d.Z <= HalfExtents.Z;
        }

        /// <summary>释放。</summary>
        public void Dispose()
        {
            Grid?.Dispose();
            GC.SuppressFinalize(this);
        }
    }

    /// <summary>
    /// 辐照度体积: 将探针网格的 SH 系数烘焙到 3D 纹理，供着色器三线性采样。
    /// 每个体素存储 SH9 的若干系数(RGBA 打包)。支持视差校正(Parallax Correction):
    /// 用探针代表的局部环境(而非全场景)进行反射/辐照度计算，避免"光泄露"。
    /// </summary>
    public sealed class IrradianceVolume : IDisposable
    {
        private RHITexture? _shTextureR; // SH 系数 R 通道体积
        private RHITexture? _shTextureG;
        private RHITexture? _shTextureB;
        private RHITexture? _depthTexture; // 探针平均深度体积(视差校正)

        private readonly RHIDevice _device;

        /// <summary>3D 纹理分辨率(每轴体素数)。</summary>
        public Vector3i Resolution { get; }
        /// <summary>世界范围(半尺寸)。</summary>
        public Vector3 Extent { get; set; }
        /// <summary>原点。</summary>
        public Vector3 Origin { get; set; }
        /// <summary>SH 阶数。</summary>
        public SHOrder Order { get; }

        /// <summary>构造辐照度体积。</summary>
        public IrradianceVolume(RHIDevice device, Vector3i resolution, Vector3 extent, SHOrder order = SHOrder.Order3)
        {
            _device = device;
            Resolution = resolution;
            Extent = extent;
            Order = order;
        }

        /// <summary>分配 3D 纹理资源。</summary>
        public void Initialize()
        {
            // SH9 需要 9 个系数 x 3 通道。用 3 张 3D 纹理分别存 R/G/B,
            // 每张纹理 RGBA 存 4 个 SH 系数(共 9 个: 4+4+1, 第9个单独存或打包)。
            RHITextureDesc MakeSH3D(string name) => new()
            {
                Dimension = RHIResourceDimension.Texture3D,
                Format = RHIFormat.R16G16B16A16_Float,
                Width = Resolution.X, Height = Resolution.Y, DepthOrArraySize = Resolution.Z,
                MipLevels = 1, SampleCount = 1,
                Flags = RHITextureFlags.ShaderResource | RHITextureFlags.UnorderedAccess
            };
            _shTextureR = _device.CreateTexture(MakeSH3D("IrradianceSH_R"), "IrradianceSH_R");
            _shTextureG = _device.CreateTexture(MakeSH3D("IrradianceSH_G"), "IrradianceSH_G");
            _shTextureB = _device.CreateTexture(MakeSH3D("IrradianceSH_B"), "IrradianceSH_B");

            var depthDesc = MakeSH3D("IrradianceDepth");
            depthDesc.Format = RHIFormat.R16_Float;
            _depthTexture = _device.CreateTexture(depthDesc, "IrradianceDepth");
        }

        /// <summary>
        /// 三线性插值采样辐照度(GPU 着色器侧完成, 此处为 CPU 参考实现)。
        /// </summary>
        public Vector3 SampleIrradiance(Vector3 worldPos, Vector3 normal)
        {
            // CPU 侧占位: 实际由着色器采样 3D 纹理三线性插值
            return Vector3.One * 0.3f;
        }

        /// <summary>
        /// 视差校正(Parallax Correction): 给定反射方向，用探针体积的局部 AABB
        /// 替代全场景进行射线相交，使反射看起来"属于"该局部环境。
        /// </summary>
        /// <param name="reflectDir">反射方向。</param>
        /// <param name="viewPos">观察点(体积内)。</param>
        /// <returns>校正后的采样方向/距离。</returns>
        public Vector3 ParallaxCorrect(Vector3 reflectDir, Vector3 viewPos)
        {
            // 射线与 AABB 求交, 取最近交点
            Vector3 localPos = viewPos - Origin;
            Vector3 invDir = new(
                MathF.Abs(reflectDir.X) > 1e-6f ? 1.0f / reflectDir.X : 1e6f,
                MathF.Abs(reflectDir.Y) > 1e-6f ? 1.0f / reflectDir.Y : 1e6f,
                MathF.Abs(reflectDir.Z) > 1e-6f ? 1.0f / reflectDir.Z : 1e6f);

            Vector3 t0 = (-Extent - localPos) * invDir;
            Vector3 t1 = (Extent - localPos) * invDir;
            Vector3 tmin = Vector3.Min(t0, t1);
            Vector3 tmax = Vector3.Max(t0, t1);
            float tEnter = MathF.Max(MathF.Max(tmin.X, tmin.Y), tmin.Z);
            float tExit = MathF.Min(MathF.Min(tmax.X, tmax.Y), tmax.Z);

            if (tEnter > tExit || tExit < 0) return reflectDir;
            float t = tEnter > 0 ? tEnter : tExit;
            Vector3 hitPoint = localPos + reflectDir * t;
            return Vector3.Normalize(hitPoint);
        }

        /// <summary>释放。</summary>
        public void Dispose()
        {
            _shTextureR?.Dispose();
            _shTextureG?.Dispose();
            _shTextureB?.Dispose();
            _depthTexture?.Dispose();
            GC.SuppressFinalize(this);
        }
    }

    /// <summary>三维整数向量(用于 3D 纹理分辨率)。</summary>
    public struct Vector3i
    {
        public int X, Y, Z;
        public Vector3i(int x, int y, int z) { X = x; Y = y; Z = z; }
        public static implicit operator Vector3i((int, int, int) v) => new(v.Item1, v.Item2, v.Item3);
    }

    /// <summary>
    /// 探针烘焙器: 为每个探针渲染 Cubemap(6 面)，将 Cubemap 投影到 SH 系数，
    /// 支持增量烘焙(每帧烘焙少量探针，分摊到多帧)与优先级调度(变化区域优先)。
    /// </summary>
    public sealed class ProbeBaker : IDisposable
    {
        private readonly RHIDevice _device;
        private RHITexture? _probeCubemap; // 临时 Cubemap(6面)
        private RHIPipelineState? _renderPipeline;
        private RHIPipelineState? _projectPipeline;
        private RHIDescriptorHandle[] _cubeRTVs = Array.Empty<RHIDescriptorHandle>();

        /// <summary>每帧烘焙预算(探针数)。</summary>
        public int ProbesPerFrame { get; set; } = 4;
        /// <summary>Cubemap 分辨率。</summary>
        public int CubemapResolution { get; set; } = 64;
        /// <summary>烘焙队列(按优先级排序)。</summary>
        private readonly List<int> _bakeQueue = new();

        /// <summary>构造。</summary>
        public ProbeBaker(RHIDevice device)
        {
            _device = device;
        }

        /// <summary>初始化 Cubemap 与投影管线。</summary>
        public void Initialize()
        {
            // 创建 Cubemap 纹理(6 面, 作为 RenderTarget)
            var cubeDesc = new RHITextureDesc
            {
                Dimension = RHIResourceDimension.Texture2D,
                Format = RHIFormat.R16G16B16A16_Float,
                Width = CubemapResolution, Height = CubemapResolution,
                DepthOrArraySize = 6, // 6 面
                MipLevels = 1, SampleCount = 1,
                Flags = RHITextureFlags.RenderTarget | RHITextureFlags.ShaderResource | RHITextureFlags.Cube
            };
            _probeCubemap = _device.CreateTexture(cubeDesc, "ProbeBakeCubemap");

            // 为 Cubemap 的 6 面各创建一个 RTV
            _cubeRTVs = new RHIDescriptorHandle[6];
            for (int face = 0; face < 6; face++)
                _cubeRTVs[face] = _device.CreateRTV(_probeCubemap, 0, (uint)face);

            // 投影计算着色器(Cubemap -> SH 系数)
            _projectPipeline = _device.CreateComputePipelineState(new RHIComputePipelineDesc
            { Name = "ProbeSHProject", CS = ShaderManager.Instance.GetBytecode("ProbeField/GIProbe", "ProbeProjectCS", RHIShaderStage.Compute) });

            _renderPipeline = _device.CreateGraphicsPipelineState(new RHIGraphicsPipelineDesc
            { Name = "ProbeCubemapRender" });
        }

        /// <summary>
        /// 将探针加入烘焙队列。
        /// </summary>
        public void EnqueueBake(int probeIndex, float priority = 0.0f)
        {
            if (!_bakeQueue.Contains(probeIndex))
            {
                _bakeQueue.Add(probeIndex);
                // 按优先级降序排序
                _bakeQueue.Sort((a, b) => b.CompareTo(a));
            }
        }

        /// <summary>
        /// 增量烘焙: 每帧烘焙 ProbesPerFrame 个探针。
        /// 流程: 渲染 Cubemap(6面) -> 投影到 SH -> 写入探针。
        /// </summary>
        /// <param name="cmdList">命令列表。</param>
        /// <param name="grid">探针网格。</param>
        /// <param name="viewMatrices">6 面的视图矩阵。</param>
        public void BakeIncremental(RHICommandList cmdList, ProbeGrid grid, Matrix4x4[] viewMatrices)
        {
            int baked = 0;
            while (_bakeQueue.Count > 0 && baked < ProbesPerFrame)
            {
                int probeIdx = _bakeQueue[0];
                _bakeQueue.RemoveAt(0);
                if (probeIdx < 0 || probeIdx >= grid.ProbeCount) continue;

                ref GIProbe probe = ref grid.GetProbe(probeIdx);
                if (probe.IsActive == 0) continue;

                // 1. 渲染 Cubemap 6 面(以探针位置为中心)
                RenderCubemapFaces(cmdList, probe.Position, viewMatrices);

                // 2. 投影 Cubemap 到 SH 系数(计算着色器)
                ProjectCubemapToSH(cmdList, probeIdx);

                probe.LastUpdateTime = (float)Environment.TickCount * 0.001f;
                probe.AccumulatedSamples = 1;
                baked++;
            }
        }

        /// <summary>渲染 Cubemap 的 6 面。</summary>
        private void RenderCubemapFaces(RHICommandList cmdList, Vector3 probePos, Matrix4x4[] viewMatrices)
        {
            // 对 6 个面分别设置 RTV 并清屏为环境色, 后续场景渲染管线可在此基础上绘制
            Vector4 ambient = new(0.03f, 0.03f, 0.05f, 1f);
            for (int face = 0; face < 6; face++)
            {
                cmdList.OMSetRenderTargets(new[] { _cubeRTVs[face] }, null);
                cmdList.ClearRenderTargetView(_cubeRTVs[face], ambient);
                // TODO: 真实场景渲染管线以 probePos 为中心、viewMatrices[face] 为视图将场景绘制到该面
                _ = viewMatrices;
            }
        }

        /// <summary>将 Cubemap 投影到 SH 系数。</summary>
        private void ProjectCubemapToSH(RHICommandList cmdList, int probeIdx)
        {
            cmdList.SetPipelineState(_projectPipeline!);
            cmdList.TransitionTo(_probeCubemap!, RHIResourceState.NonPixelShaderResource);
            // 分派投影计算: 采样 Cubemap 所有像素, 累加 SH 系数
            int groups = (CubemapResolution * CubemapResolution * 6 + 63) / 64;
            cmdList.Dispatch(groups, 1, 1);
        }

        /// <summary>
        /// 标记变化区域: 当场景某区域发生变化(物体移动/光照改变)时，
        /// 将该区域内的探针加入高优先级烘焙队列。
        /// </summary>
        public void MarkDirtyRegion(ProbeGrid grid, Vector3 regionCenter, float regionRadius)
        {
            for (int i = 0; i < grid.ProbeCount; i++)
            {
                ref var probe = ref grid.GetProbe(i);
                if (probe.IsActive == 0) continue;
                float dist = Vector3.Distance(probe.Position, regionCenter);
                if (dist <= regionRadius)
                {
                    probe.BakePriority = MathF.Max(probe.BakePriority, 1.0f - dist / regionRadius);
                    EnqueueBake(i, probe.BakePriority);
                }
            }
        }

        /// <summary>释放。</summary>
        public void Dispose()
        {
            _probeCubemap?.Dispose();
            _renderPipeline?.Dispose();
            _projectPipeline?.Dispose();
            GC.SuppressFinalize(this);
        }
    }

    /// <summary>
    /// 动态 GI 探针: 运行时更新的探针，支持动态场景。
    /// 基于渲染帧的反馈(如 GeometryBuffer/光照缓冲)更新探针辐照度，
    /// 并通过时间累积平滑(Temporal Accumulation)减少闪烁。
    /// </summary>
    public sealed class DynamicGIProbe : IDisposable
    {
        private readonly RHIDevice _device;
        private RHIBuffer? _feedbackBuffer;
        private RHIPipelineState? _updatePipeline;

        /// <summary>时间累积 alpha(历史权重, 越大越平滑但响应越慢)。</summary>
        public float TemporalAlpha { get; set; } = 0.9f;
        /// <summary>每帧更新的探针数。</summary>
        public int ProbesPerFrame { get; set; } = 8;
        /// <summary>是否启用。</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>构造。</summary>
        public DynamicGIProbe(RHIDevice device)
        {
            _device = device;
        }

        /// <summary>初始化。</summary>
        public void Initialize()
        {
            _feedbackBuffer = _device.CreateBuffer(new RHIBufferDesc
            {
                SizeInBytes = 65536,
                Flags = RHIBufferFlags.Structured | RHIBufferFlags.UnorderedAccess | RHIBufferFlags.ShaderResource,
                Stride = 32, HeapType = RHIResourceHeapType.Default
            }, "DynamicGIProbeFeedback");

            _updatePipeline = _device.CreateComputePipelineState(new RHIComputePipelineDesc
            { Name = "DynamicGIProbeUpdate", CS = ShaderManager.Instance.GetBytecode("ProbeField/GIProbe", "DynamicProbeUpdateCS", RHIShaderStage.Compute) });
        }

        /// <summary>
        /// 每帧更新动态探针: 从渲染反馈缓冲读取探针可见性区域的新辐照度，
        /// 时间累积平滑后写入探针 SH 系数。
        /// </summary>
        public void Update(RHICommandList cmdList, ProbeGrid grid, RHITexture sceneColor, RHITexture sceneDepth, float deltaTime)
        {
            if (!Enabled || _updatePipeline == null) return;

            cmdList.SetPipelineState(_updatePipeline);
            cmdList.TransitionTo(_feedbackBuffer!, RHIResourceState.UnorderedAccess);
            cmdList.TransitionTo(sceneColor, RHIResourceState.NonPixelShaderResource);
            cmdList.TransitionTo(sceneDepth, RHIResourceState.NonPixelShaderResource);

            // 分派更新计算: 每帧更新 ProbesPerFrame 个探针
            int groups = (ProbesPerFrame + 31) / 32;
            cmdList.Dispatch(groups, 1, 1);

            cmdList.TransitionTo(_feedbackBuffer, RHIResourceState.NonPixelShaderResource);
        }

        /// <summary>释放。</summary>
        public void Dispose()
        {
            _feedbackBuffer?.Dispose();
            _updatePipeline?.Dispose();
            GC.SuppressFinalize(this);
        }
    }

    /// <summary>反射探针更新模式。</summary>
    public enum ReflectionProbeMode
    {
        /// <summary>静态: 烘焙一次，运行时不更新。</summary>
        Baked,
        /// <summary>实时: 每帧更新。</summary>
        Realtime,
        /// <summary>间隔: 按固定间隔更新。</summary>
        Interval
    }

    /// <summary>
    /// 反射探针: 存储 Cubemap 用于镜面反射。与 GI 探针(低频漫反射)区分，
    /// 反射探针保留高频方向信息(Cubemap)，适合镜面/金属材质。
    /// 支持静态(烘焙)/动态(实时/间隔)与多探针混合平滑过渡。
    /// </summary>
    public sealed class ReflectionProbe : IDisposable
    {
        /// <summary>唯一 Id。</summary>
        public Guid Id { get; } = Guid.NewGuid();
        /// <summary>名称。</summary>
        public string Name { get; set; } = "ReflectionProbe";
        /// <summary>世界位置。</summary>
        public Vector3 Position { get; set; }
        /// <summary>影响半径。</summary>
        public float Range { get; set; } = 500.0f;
        /// <summary>更新模式。</summary>
        public ReflectionProbeMode Mode { get; set; } = ReflectionProbeMode.Baked;
        /// <summary>间隔更新周期(秒, Interval 模式)。</summary>
        public float UpdateInterval { get; set; } = 2.0f;
        /// <summary>Cubemap 分辨率。</summary>
        public int Resolution { get; set; } = 128;
        /// <summary>是否启用视差校正。</summary>
        public bool EnableParallaxCorrection { get; set; } = true;
        /// <summary>视差校正 AABB(局部空间)。</summary>
        public Vector3 ParallaxHalfExtents { get; set; } = new(100, 50, 100);
        /// <summary>视差校正原点偏移。</summary>
        public Vector3 ParallaxOffset { get; set; } = Vector3.Zero;
        /// <summary>优先级(重叠时数值大的优先)。</summary>
        public float Priority { get; set; } = 1.0f;
        /// <summary>上次更新时间。</summary>
        public float LastUpdateTime { get; internal set; }
        /// <summary>当前 Cubemap 纹理。</summary>
        public RHITexture? Cubemap { get; private set; }
        /// <summary>前一帧 Cubemap(混合用)。</summary>
        public RHITexture? PreviousCubemap { get; private set; }
        /// <summary>混合权重(0=Previous, 1=Current)。</summary>
        public float BlendWeight { get; internal set; } = 1.0f;

        private readonly RHIDevice _device;

        /// <summary>构造。</summary>
        public ReflectionProbe(RHIDevice device)
        {
            _device = device;
        }

        /// <summary>分配 Cubemap 资源。</summary>
        public void Initialize()
        {
            var desc = new RHITextureDesc
            {
                Dimension = RHIResourceDimension.Texture2D,
                Format = RHIFormat.R16G16B16A16_Float,
                Width = Resolution, Height = Resolution,
                DepthOrArraySize = 6, MipLevels = 1, SampleCount = 1,
                Flags = RHITextureFlags.RenderTarget | RHITextureFlags.ShaderResource | RHITextureFlags.Cube
            };
            Cubemap = _device.CreateTexture(desc, Name + "_Cubemap");
            PreviousCubemap = _device.CreateTexture(desc, Name + "_Cubemap_Prev");
        }

        /// <summary>
        /// 更新反射探针 Cubemap。
        /// </summary>
        /// <param name="cmdList">命令列表。</param>
        /// <param name="currentTime">当前时间。</param>
        /// <param name="renderCallback">渲染场景到 Cubemap 某面的回调。</param>
        public void Update(RHICommandList cmdList, float currentTime, Action<int, RHITexture> renderCallback)
        {
            bool shouldUpdate = Mode switch
            {
                ReflectionProbeMode.Realtime => true,
                ReflectionProbeMode.Interval => (currentTime - LastUpdateTime) >= UpdateInterval,
                ReflectionProbeMode.Baked => false,
                _ => false
            };

            if (!shouldUpdate) return;

            // 交换 Cubemap(当前 -> 历史)
            (PreviousCubemap, Cubemap) = (Cubemap, PreviousCubemap);
            BlendWeight = 0.0f; // 开始混合

            // 渲染 6 面
            for (int face = 0; face < 6; face++)
            {
                renderCallback?.Invoke(face, Cubemap!);
            }

            LastUpdateTime = currentTime;
        }

        /// <summary>推进混合权重(用于平滑过渡)。</summary>
        public void AdvanceBlend(float deltaTime, float blendSpeed = 4.0f)
        {
            BlendWeight = Math.Min(1.0f, BlendWeight + deltaTime * blendSpeed);
        }

        /// <summary>释放。</summary>
        public void Dispose()
        {
            Cubemap?.Dispose();
            PreviousCubemap?.Dispose();
            GC.SuppressFinalize(this);
        }
    }

    /// <summary>探针混合模式。</summary>
    public enum ProbeBlendMode
    {
        /// <summary>最近邻: 只用最近的探针。</summary>
        Nearest,
        /// <summary>三线性: 8 个邻近探针三线性插值。</summary>
        Trilinear,
        /// <summary>权重混合: 按距离/可见性权重混合。</summary>
        Weighted
    }

    /// <summary>
    /// 探针混合系统: 管理多个探针(GI/反射)的混合，避免穿墙问题。
    /// 探针权重图(Weight Map)记录墙体两侧探针的不可见性，确保混合时不会
    /// 将墙另一侧的探针(错误的环境光)混入当前侧。
    /// </summary>
    public sealed class ProbeBlending
    {
        /// <summary>混合模式。</summary>
        public ProbeBlendMode Mode { get; set; } = ProbeBlendMode.Trilinear;
        /// <summary>权重混合的最大探针数。</summary>
        public int MaxBlendProbes { get; set; } = 4;
        /// <summary>穿墙衰减(探针权重在墙体两侧的衰减)。</summary>
        public float WallAttenuation { get; set; } = 0.1f;

        // 探针权重图: (probeA, probeB) -> 可见性(0=不可见/穿墙, 1=可见)
        private readonly Dictionary<(Guid, Guid), float> _visibilityMap = new();

        /// <summary>
        /// 设置两个探针之间的可见性(用于避免穿墙)。
        /// 当两个探针位于墙体两侧时，可见性接近 0。
        /// </summary>
        public void SetProbeVisibility(Guid probeA, Guid probeB, float visibility)
        {
            _visibilityMap[(probeA, probeB)] = visibility;
            _visibilityMap[(probeB, probeA)] = visibility;
        }

        /// <summary>获取两个探针间的可见性。</summary>
        public float GetProbeVisibility(Guid probeA, Guid probeB)
        {
            if (probeA == probeB) return 1.0f;
            return _visibilityMap.TryGetValue((probeA, probeB), out var v) ? v : 1.0f;
        }

        /// <summary>
        /// 计算位置处各探针的混合权重。
        /// </summary>
        /// <param name="probes">候选探针(位置+Id+范围)。</param>
        /// <param name="worldPos">世界位置。</param>
        /// <returns>(探针Id, 权重) 列表。</returns>
        public List<(Guid id, float weight)> ComputeBlendWeights(
            IReadOnlyList<(Guid id, Vector3 pos, float range)> probes, Vector3 worldPos)
        {
            var weights = new List<(Guid id, float weight)>();

            switch (Mode)
            {
                case ProbeBlendMode.Nearest:
                    {
                        Guid nearest = Guid.Empty;
                        float minDist = float.MaxValue;
                        foreach (var (id, pos, range) in probes)
                        {
                            float d = Vector3.Distance(pos, worldPos);
                            if (d < minDist && d <= range) { minDist = d; nearest = id; }
                        }
                        if (nearest != Guid.Empty) weights.Add((nearest, 1.0f));
                        break;
                    }
                case ProbeBlendMode.Weighted:
                    {
                        float totalWeight = 0;
                        var temp = new List<(Guid id, float weight)>();
                        foreach (var (id, pos, range) in probes)
                        {
                            float d = Vector3.Distance(pos, worldPos);
                            if (d > range) continue;
                            // 距离反比权重
                            float w = 1.0f / (1.0f + d * d * 0.001f);
                            // 穿墙衰减: 与已选探针检查可见性
                            foreach (var (sid, _) in temp)
                                w *= GetProbeVisibility(id, sid);
                            temp.Add((id, w));
                            totalWeight += w;
                            if (temp.Count >= MaxBlendProbes) break;
                        }
                        if (totalWeight > 0)
                            foreach (var (id, w) in temp)
                                weights.Add((id, w / totalWeight));
                        break;
                    }
                case ProbeBlendMode.Trilinear:
                    // 三线性混合由 ProbeGrid.SampleIrradianceTrilinear 完成
                    // 此处回退到加权
                    goto case ProbeBlendMode.Weighted;
            }

            return weights;
        }

        /// <summary>
        /// 混合多个反射探针的 Cubemap(用于平滑过渡)。
        /// 实际由着色器对多个 Cubemap 采样加权混合。
        /// </summary>
        public Vector3 BlendReflections(IReadOnlyList<(ReflectionProbe probe, float weight)> contributions,
            Vector3 reflectDir, Vector3 viewPos)
        {
            Vector3 result = Vector3.Zero;
            foreach (var (probe, weight) in contributions)
            {
                if (probe.Cubemap == null || weight <= 0) continue;
                Vector3 dir = reflectDir;
                if (probe.EnableParallaxCorrection)
                {
                    // 视差校正: 用探针局部 AABB 替代全场景
                    dir = ParallaxCorrectProbe(reflectDir, viewPos, probe);
                }
                // 采样 Cubemap(占位, 实际由着色器完成)
                Vector3 sample = SampleCubemapPlaceholder(dir);
                result += sample * weight;
            }
            return result;
        }

        /// <summary>反射探针视差校正。</summary>
        private Vector3 ParallaxCorrectProbe(Vector3 reflectDir, Vector3 viewPos, ReflectionProbe probe)
        {
            Vector3 localPos = viewPos - probe.Position - probe.ParallaxOffset;
            Vector3 invDir = new(
                MathF.Abs(reflectDir.X) > 1e-6f ? 1.0f / reflectDir.X : 1e6f,
                MathF.Abs(reflectDir.Y) > 1e-6f ? 1.0f / reflectDir.Y : 1e6f,
                MathF.Abs(reflectDir.Z) > 1e-6f ? 1.0f / reflectDir.Z : 1e6f);
            Vector3 t0 = (-probe.ParallaxHalfExtents - localPos) * invDir;
            Vector3 t1 = (probe.ParallaxHalfExtents - localPos) * invDir;
            Vector3 tmin = Vector3.Min(t0, t1);
            Vector3 tmax = Vector3.Max(t0, t1);
            float tEnter = MathF.Max(MathF.Max(tmin.X, tmin.Y), tmin.Z);
            float tExit = MathF.Min(MathF.Min(tmax.X, tmax.Y), tmax.Z);
            if (tEnter > tExit || tExit < 0) return reflectDir;
            float t = tEnter > 0 ? tEnter : tExit;
            return Vector3.Normalize(localPos + reflectDir * t);
        }

        /// <summary>Cubemap 采样占位。</summary>
        private Vector3 SampleCubemap(Vector3 dir) => new(0.3f, 0.3f, 0.4f);

        /// <summary>Cubemap 采样占位(避免与私有方法签名冲突的别名)。</summary>
        private Vector3 SampleCubemapPlaceholder(Vector3 dir) => SampleCubemap(dir);
    }

    /// <summary>
    /// GI 探针体积组件(ECS): 声明场景中一个 GI 探针体积。
    /// 挂载到实体上，由系统读取配置创建对应的 ProbeVolume。
    /// </summary>
    public struct GIProbeVolumeComponent : IComponent
    {
        /// <summary>体积中心(可由 Transform 同步)。</summary>
        public Vector3 Center;
        /// <summary>半尺寸。</summary>
        public Vector3 HalfExtents;
        /// <summary>网格分辨率(每轴探针数)。</summary>
        public Vector3 GridResolution;
        /// <summary>SH 阶数。</summary>
        public int SHOrder;
        /// <summary>是否启用。</summary>
        public bool Enabled;
        /// <summary>优先级。</summary>
        public float Priority;
    }

    /// <summary>
    /// 反射探针组件(ECS): 声明场景中一个反射探针。
    /// </summary>
    public struct ReflectionProbeComponent : IComponent
    {
        /// <summary>位置(可由 Transform 同步)。</summary>
        public Vector3 Position;
        /// <summary>影响半径。</summary>
        public float Range;
        /// <summary>更新模式。</summary>
        public ReflectionProbeMode Mode;
        /// <summary>更新间隔(秒)。</summary>
        public float UpdateInterval;
        /// <summary>Cubemap 分辨率。</summary>
        public int Resolution;
        /// <summary>启用视差校正。</summary>
        public bool EnableParallaxCorrection;
        /// <summary>视差校正半尺寸。</summary>
        public Vector3 ParallaxHalfExtents;
        /// <summary>优先级。</summary>
        public float Priority;
        /// <summary>是否启用。</summary>
        public bool Enabled;
    }

    /// <summary>
    /// 全局光照探针系统: 统筹管理所有 GI 探针体积、反射探针、烘焙与动态更新。
    /// 作为 ProbeFieldGlobalIllumination 的补充层，提供稳定的低频环境光。
    /// 由 ProbeFieldGlobalIllumination.Update 调用本系统的 Update。
    /// </summary>
    public sealed class GlobalIlluminationProbeSystem : IDisposable
    {
        private readonly RHIDevice _device;
        private readonly List<ProbeVolume> _giVolumes = new();
        private readonly List<ReflectionProbe> _reflectionProbes = new();
        private readonly ProbeBaker _baker;
        private readonly DynamicGIProbe _dynamicGI;
        private readonly ProbeBlending _blending = new();
        private IrradianceVolume? _irradianceVolume;

        /// <summary>烘焙器。</summary>
        public ProbeBaker Baker => _baker;
        /// <summary>动态 GI。</summary>
        public DynamicGIProbe DynamicGI => _dynamicGI;
        /// <summary>混合系统。</summary>
        public ProbeBlending Blending => _blending;
        /// <summary>辐照度体积。</summary>
        public IrradianceVolume? IrradianceVolume => _irradianceVolume;
        /// <summary>GI 体积数量。</summary>
        public int GIVolumeCount => _giVolumes.Count;
        /// <summary>反射探针数量。</summary>
        public int ReflectionProbeCount => _reflectionProbes.Count;

        /// <summary>构造。</summary>
        public GlobalIlluminationProbeSystem(RHIDevice device)
        {
            _device = device;
            _baker = new ProbeBaker(device);
            _dynamicGI = new DynamicGIProbe(device);
        }

        /// <summary>初始化子系统。</summary>
        public void Initialize()
        {
            _baker.Initialize();
            _dynamicGI.Initialize();
        }

        /// <summary>添加 GI 探针体积。</summary>
        public ProbeVolume AddGIVolume(Vector3 center, Vector3 halfExtents, Vector3 gridResolution,
            SHOrder order = SHOrder.Order3)
        {
            var vol = new ProbeVolume(center, halfExtents, gridResolution, order);
            vol.Initialize(_device);
            _giVolumes.Add(vol);
            return vol;
        }

        /// <summary>添加反射探针。</summary>
        public ReflectionProbe AddReflectionProbe(Vector3 position, float range,
            ReflectionProbeMode mode = ReflectionProbeMode.Baked, int resolution = 128)
        {
            var probe = new ReflectionProbe(_device)
            {
                Position = position,
                Range = range,
                Mode = mode,
                Resolution = resolution
            };
            probe.Initialize();
            _reflectionProbes.Add(probe);
            return probe;
        }

        /// <summary>
        /// 从 ECS 同步探针配置: 查询 GIProbeVolumeComponent / ReflectionProbeComponent，
        /// 创建/更新对应的探针体积与反射探针。
        /// </summary>
        public void SyncFromECS(EntityComponentSystem ecs)
        {
            // 同步 GI 探针体积
            var volEntities = ecs.QueryWithComponent<GIProbeVolumeComponent>();
            foreach (var entityId in volEntities)
            {
                var comp = ecs.GetComponent<GIProbeVolumeComponent>(entityId);
                if (!comp.Enabled) continue;
                // 若已有对应体积则跳过(简化: 按 entityId 索引)
                // 实际应有 Id 映射
            }

            // 同步反射探针
            var probeEntities = ecs.QueryWithComponent<ReflectionProbeComponent>();
            foreach (var entityId in probeEntities)
            {
                var comp = ecs.GetComponent<ReflectionProbeComponent>(entityId);
                if (!comp.Enabled) continue;
                // 同步位置
                if (ecs.HasComponent<TransformComponent>(entityId))
                {
                    var tf = ecs.GetComponent<TransformComponent>(entityId);
                    comp.Position = tf.Position;
                    ecs.SetComponent(entityId, comp);
                }
            }
        }

        /// <summary>
        /// 每帧更新: 烘焙增量探针、更新动态探针、更新反射探针混合。
        /// 由 ProbeFieldGlobalIllumination.Update 调用，复用其命令列表。
        /// </summary>
        /// <param name="cmdList">命令列表。</param>
        /// <param name="probefieldSettings">ProbeField GI 设置(读取探针网格配置)。</param>
        /// <param name="sceneColor">场景颜色纹理(动态探针反馈)。</param>
        /// <param name="sceneDepth">场景深度纹理。</param>
        /// <param name="deltaTime">帧时间。</param>
        /// <param name="currentTime">当前时间。</param>
        public void Update(RHICommandList cmdList, ProbeFieldGISettings probefieldSettings,
            RHITexture sceneColor, RHITexture sceneDepth, float deltaTime, float currentTime)
        {
            // 1. 增量烘焙 GI 探针
            foreach (var vol in _giVolumes)
            {
                if (!vol.IsActive || vol.Grid == null) continue;
                // 使用 6 面视图矩阵(占位)
                var viewMats = new Matrix4x4[6];
                _baker.BakeIncremental(cmdList, vol.Grid, viewMats);
            }

            // 2. 动态探针更新
            foreach (var vol in _giVolumes)
            {
                if (!vol.IsActive || vol.Grid == null) continue;
                _dynamicGI.Update(cmdList, vol.Grid, sceneColor, sceneDepth, deltaTime);
            }

            // 3. 反射探针更新与混合推进
            foreach (var probe in _reflectionProbes)
            {
                probe.AdvanceBlend(deltaTime);
                // 实时/间隔探针由渲染管线在适当时机调用 probe.Update
            }
        }

        /// <summary>
        /// 查询某位置的漫反射辐照度(从最近的 GI 体积三线性采样)。
        /// </summary>
        public Vector3 QueryDiffuseIrradiance(Vector3 worldPos, Vector3 normal)
        {
            foreach (var vol in _giVolumes)
            {
                if (!vol.IsActive || vol.Grid == null) continue;
                if (vol.Contains(worldPos))
                    return vol.Grid.SampleIrradianceTrilinear(worldPos, normal);
            }
            return Vector3.Zero;
        }

        /// <summary>
        /// 查询某位置的镜面反射(从最近的反射探针采样，带视差校正)。
        /// 返回最合适的反射探针与权重。
        /// </summary>
        public ReflectionProbe? QueryReflectionProbe(Vector3 worldPos)
        {
            ReflectionProbe? best = null;
            float bestDist = float.MaxValue;
            foreach (var probe in _reflectionProbes)
            {
                float d = Vector3.Distance(probe.Position, worldPos);
                if (d <= probe.Range && d < bestDist)
                {
                    bestDist = d;
                    best = probe;
                }
            }
            return best;
        }

        /// <summary>释放所有资源。</summary>
        public void Dispose()
        {
            _baker.Dispose();
            _dynamicGI.Dispose();
            _irradianceVolume?.Dispose();
            foreach (var vol in _giVolumes) vol.Dispose();
            foreach (var probe in _reflectionProbes) probe.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}
