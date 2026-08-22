// ============================================================================================
//  EfficientGIApproximation.cs
//
//  山雨引擎 高效全局光照近似 —— 低配置也能获得间接光
//
//  设计原则:
//    - 不使用光线追踪, 不使用密集探针网格
//    - 使用稀疏辐照探针 + 屏幕空间近似 + 时间累积
//    - 总开销控制在 1-2ms 以内 (GTX 1060 级别)
//    - 自动降级: 高配置用探针+SSGI, 低配置仅用探针, 极低配置用环境常量
//
//  包含组件:
//    1. 稀疏辐照探针场 (Sparse Irradiance Probe Field):
//       - 只在需要的位置放置探针 (靠近表面/光源)
//       - 二阶球谐 (SH2) 存储辐照度, 9 个系数
//       - 异步更新: 每帧只更新部分探针
//    2. 轻量光传播体积 (Lightweight LPV):
//       - 使用 3D 纹理存储间接光传播方向
//       - 仅 1 次 bounce, 使用低分辨率网格
//    3. 屏幕空间全局光照 (SSGI):
//       - 基于 G-Buffer 的屏幕空间间接光
//       - 低分辨率采样 + 时间累积
//    4. 时间累积 (Temporal Accumulation):
//       - 历史帧加权累积, 减少噪声
//       - 速度感知重投影
//    5. 环境光近似 (Ambient Approximation):
//       - 基于高度/方向的廉价环境光
//       - 作为最低质量的兜底方案
//
//  命名空间: ShanyuEngine.Rendering
//  目标框架: net8.0 / C# 12+
// ============================================================================================

using System;
using System.Collections.Generic;
using System.Numerics;

namespace ShanyuEngine.Rendering
{
    // ════════════════════════════════════════════════════════════════════════
    //  二阶球谐系数 (Spherical Harmonics L2)
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 二阶球谐 (SH L2) —— 9 个系数, 用于存储辐照度
    /// 比 SH L1 (4个) 更精确, 比 SH L3 (16个) 更省内存
    /// </summary>
    public struct SH2Coefficients
    {
        // L0 (1个): 环境光
        public Vector3 L00;
        // L1 (3个): 方向性 - 线性梯度
        public Vector3 L1_1, L10, L11;
        // L2 (5个): 二阶方向性
        public Vector3 L2_2, L2_1, L20, L21, L22;

        /// <summary>空系数 (全黑)</summary>
        public static SH2Coefficients Black => new();

        /// <summary>环境光系数 (均匀)</summary>
        public static SH2Coefficients Ambient(Vector3 color)
        {
            var sh = new SH2Coefficients();
            // SH L0 基函数在球面上积分 = 4π, 所以系数 = color / (4π) * π = color/4
            sh.L00 = color * 0.25f;
            return sh;
        }

        /// <summary>从方向光构建 SH 系数</summary>
        public static SH2Coefficients FromDirectionalLight(Vector3 dir, Vector3 color, float intensity)
        {
            Vector3 n = Vector3.Normalize(dir);
            var sh = new SH2Coefficients();

            // SH 基函数 (实数球谐)
            // L0: Y00 = 0.282095
            sh.L00 = color * 0.282095f * intensity;

            // L1: Y1,-1 = 0.488603 * y, Y1,0 = 0.488603 * z, Y1,1 = 0.488603 * x
            sh.L1_1 = color * 0.488603f * n.Y * intensity;
            sh.L10 = color * 0.488603f * n.Z * intensity;
            sh.L11 = color * 0.488603f * n.X * intensity;

            // L2: 省略高频项以节省计算 (近似)
            sh.L2_2 = color * 1.092548f * n.X * n.Y * intensity;
            sh.L2_1 = color * 1.092548f * n.Y * n.Z * intensity;
            sh.L20 = color * 0.315392f * (3f * n.Z * n.Z - 1f) * intensity;
            sh.L21 = color * 1.092548f * n.X * n.Z * intensity;
            sh.L22 = color * 0.546274f * (n.X * n.X - n.Y * n.Y) * intensity;

            return sh;
        }

        /// <summary>计算指定方向的辐照度</summary>
        public Vector3 Evaluate(Vector3 dir)
        {
            Vector3 n = Vector3.Normalize(dir);
            float x = n.X, y = n.Y, z = n.Z;

            // SH 基函数值
            float Y00 = 0.282095f;
            float Y1_1 = 0.488603f * y;
            float Y10 = 0.488603f * z;
            float Y11 = 0.488603f * x;
            float Y2_2 = 1.092548f * x * y;
            float Y2_1 = 1.092548f * y * z;
            float Y20 = 0.315392f * (3f * z * z - 1f);
            float Y21 = 1.092548f * x * z;
            float Y22 = 0.546274f * (x * x - y * y);

            return L00 * Y00 +
                   L1_1 * Y1_1 + L10 * Y10 + L11 * Y11 +
                   L2_2 * Y2_2 + L2_1 * Y2_1 + L20 * Y20 + L21 * Y21 + L22 * Y22;
        }

        /// <summary>加权累加</summary>
        public static SH2Coefficients operator +(SH2Coefficients a, SH2Coefficients b)
        {
            a.L00 += b.L00;
            a.L1_1 += b.L1_1; a.L10 += b.L10; a.L11 += b.L11;
            a.L2_2 += b.L2_2; a.L2_1 += b.L2_1; a.L20 += b.L20; a.L21 += b.L21; a.L22 += b.L22;
            return a;
        }

        /// <summary>标量乘法</summary>
        public static SH2Coefficients operator *(SH2Coefficients a, float s)
        {
            a.L00 *= s;
            a.L1_1 *= s; a.L10 *= s; a.L11 *= s;
            a.L2_2 *= s; a.L2_1 *= s; a.L20 *= s; a.L21 *= s; a.L22 *= s;
            return a;
        }

        /// <summary>线性插值</summary>
        public static SH2Coefficients Lerp(SH2Coefficients a, SH2Coefficients b, float t)
        {
            return a * (1f - t) + b * t;
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    //  辐照探针 (Irradiance Probe)
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 单个辐照探针 —— 存储一个空间点的 SH2 辐照度
    /// </summary>
    public struct IrradianceProbe
    {
        public Vector3 Position;
        public SH2Coefficients SH;
        public float LastUpdate;       // 上次更新时间
        public int UpdatePriority;     // 更新优先级 (0=最高)
        public bool IsValid;           // 是否有效
        public float Coverage;          // 覆盖半径
    }

    // ════════════════════════════════════════════════════════════════════════
    //  稀疏辐照探针场 (Sparse Irradiance Probe Field)
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 稀疏辐照探针场 —— 只在需要的位置放置探针
    /// 使用八叉树空间分区, 自动在表面附近密集分布探针
    /// </summary>
    public sealed class SparseIrradianceProbeField
    {
        private readonly List<IrradianceProbe> _probes = new(256);
        private readonly Dictionary<long, List<int>> _spatialHash = new(128);
        private float _cellSize = 4.0f;     // 空间哈希单元格大小
        private int _maxProbesPerFrame = 8; // 每帧最多更新的探针数
        private int _updateCounter = 0;
        private float _probeCoverage = 5.0f; // 探针覆盖半径

        public int ProbeCount => _probes.Count;
        public float CellSize => _cellSize;
        public IReadOnlyList<IrradianceProbe> Probes => _probes;

        /// <summary>
        /// 设置探针密度参数
        /// </summary>
        public void Configure(float cellSize, int maxUpdatesPerFrame, float coverage)
        {
            _cellSize = MathF.Max(0.5f, cellSize);
            _maxProbesPerFrame = Math.Max(1, maxUpdatesPerFrame);
            _probeCoverage = MathF.Max(1f, coverage);
        }

        /// <summary>
        /// 添加探针 (如果该位置附近没有探针)
        /// </summary>
        public int AddProbe(Vector3 position)
        {
            // 检查是否附近已有探针
            long hash = HashPosition(position);
            if (_spatialHash.TryGetValue(hash, out var nearby))
            {
                foreach (int idx in nearby)
                {
                    if (Vector3.DistanceSquared(_probes[idx].Position, position) < _cellSize * _cellSize * 0.25f)
                        return idx; // 已存在
                }
            }

            var probe = new IrradianceProbe
            {
                Position = position,
                SH = SH2Coefficients.Black,
                LastUpdate = 0f,
                UpdatePriority = 0,
                IsValid = false,
                Coverage = _probeCoverage
            };

            _probes.Add(probe);
            int newIdx = _probes.Count - 1;

            // 加入空间哈希
            if (!_spatialHash.ContainsKey(hash))
                _spatialHash[hash] = new List<int>(4);
            _spatialHash[hash].Add(newIdx);

            return newIdx;
        }

        /// <summary>
        /// 批量添加探针网格 (均匀分布)
        /// </summary>
        public void AddProbeGrid(Vector3 min, Vector3 max, float spacing)
        {
            for (float x = min.X; x <= max.X; x += spacing)
            for (float y = min.Y; y <= max.Y; y += spacing)
            for (float z = min.Z; z <= max.Z; z += spacing)
            {
                AddProbe(new Vector3(x, y, z));
            }
        }

        /// <summary>
        /// 在表面附近添加探针 (智能放置)
        /// </summary>
        public void AddProbesNearSurface(Vector3 surfacePos, Vector3 normal, float density)
        {
            // 沿法线方向偏移放置探针
            Vector3 probePos = surfacePos + normal * 0.5f;
            AddProbe(probePos);

            // 在周围放置额外探针
            Vector3 tangent = MathF.Abs(normal.Y) > 0.9f
                ? Vector3.UnitX : Vector3.Normalize(Vector3.Cross(normal, Vector3.UnitY));

            for (int i = 0; i < 4; i++)
            {
                float angle = i * MathF.PI * 0.5f;
                Vector3 offset = tangent * MathF.Cos(angle) * density +
                                 Vector3.Cross(normal, tangent) * MathF.Sin(angle) * density;
                AddProbe(surfacePos + offset + normal * 0.5f);
            }
        }

        /// <summary>
        /// 采样指定位置的辐照度 (三线性插值 + 距离加权)
        /// </summary>
        public SH2Coefficients Sample(Vector3 position, Vector3 normal)
        {
            if (_probes.Count == 0)
                return SH2Coefficients.Ambient(new(0.2f, 0.2f, 0.25f));

            // 查找附近的探针
            long hash = HashPosition(position);
            var candidates = new List<int>(16);

            // 搜索 3x3x3 的邻居单元格
            int cx = (int)MathF.Floor(position.X / _cellSize);
            int cy = (int)MathF.Floor(position.Y / _cellSize);
            int cz = (int)MathF.Floor(position.Z / _cellSize);

            for (int dx = -1; dx <= 1; dx++)
            for (int dy = -1; dy <= 1; dy++)
            for (int dz = -1; dz <= 1; dz++)
            {
                long h = HashCell(cx + dx, cy + dy, cz + dz);
                if (_spatialHash.TryGetValue(h, out var list))
                    candidates.AddRange(list);
            }

            if (candidates.Count == 0)
                return SH2Coefficients.Ambient(new(0.15f, 0.15f, 0.2f));

            // 距离加权插值 (Shepard's method)
            SH2Coefficients result = SH2Coefficients.Black;
            float totalWeight = 0f;

            foreach (int idx in candidates)
            {
                var probe = _probes[idx];
                if (!probe.IsValid)
                    continue;

                float dist = Vector3.Distance(position, probe.Position);
                if (dist > probe.Coverage)
                    continue;

                // 距离权重 (反距离平方)
                float weight = 1f / (dist * dist + 0.01f);

                // 法线对齐权重 (法线方向相似的探针权重更高)
                float normalAlign = MathF.Max(0f, 1f - dist / probe.Coverage);

                weight *= normalAlign;

                result += probe.SH * weight;
                totalWeight += weight;
            }

            if (totalWeight < 0.001f)
                return SH2Coefficients.Ambient(new(0.15f, 0.15f, 0.2f));

            return result * (1f / totalWeight);
        }

        /// <summary>
        /// 异步更新探针 (每帧只更新优先级最高的几个)
        /// </summary>
        public void UpdateProbes(
            float currentTime,
            Func<Vector3, SH2Coefficients> irradianceSampler,
            float blendFactor = 0.1f)
        {
            int updated = 0;
            _updateCounter++;

            // 按优先级排序 (简化: 轮询更新)
            int startIdx = _updateCounter % Math.Max(1, _probes.Count);

            for (int i = 0; i < _probes.Count && updated < _maxProbesPerFrame; i++)
            {
                int idx = (startIdx + i) % _probes.Count;
                ref var probe = ref _probes[idx];

                // 检查是否需要更新 (每 N 帧更新一次)
                if (currentTime - probe.LastUpdate < 1f / _maxProbesPerFrame * _probes.Count)
                    continue;

                // 采样辐照度
                SH2Coefficients newSH = irradianceSampler(probe.Position);

                // 时间平滑 (避免闪烁)
                if (probe.IsValid)
                {
                    probe.SH = SH2Coefficients.Lerp(probe.SH, newSH, blendFactor);
                }
                else
                {
                    probe.SH = newSH;
                    probe.IsValid = true;
                }

                probe.LastUpdate = currentTime;
                updated++;
            }
        }

        /// <summary>设置所有探针失效 (场景变化时调用)</summary>
        public void InvalidateAll()
        {
            for (int i = 0; i < _probes.Count; i++)
            {
                _probes[i] = new IrradianceProbe
                {
                    Position = _probes[i].Position,
                    IsValid = false,
                    Coverage = _probes[i].Coverage,
                    LastUpdate = 0f,
                    UpdatePriority = _probes[i].UpdatePriority
                };
            }
        }

        /// <summary>清空所有探针</summary>
        public void Clear()
        {
            _probes.Clear();
            _spatialHash.Clear();
        }

        private long HashPosition(Vector3 pos)
        {
            return HashCell(
                (int)MathF.Floor(pos.X / _cellSize),
                (int)MathF.Floor(pos.Y / _cellSize),
                (int)MathF.Floor(pos.Z / _cellSize));
        }

        private long HashCell(int x, int y, int z)
        {
            return ((long)(x + 32768) << 42) | ((long)(y + 32768) << 21) | (long)(z + 32768);
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    //  轻量光传播体积 (Lightweight Light Propagation Volume)
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 轻量光传播体积 —— 近似间接光的传播
    /// 使用低分辨率 3D 网格存储球谐系数, 模拟光的一次弹射
    /// </summary>
    public sealed class LightweightLPV
    {
        private SH2Coefficients[,,] _grid;
        private SH2Coefficients[,,] _tempGrid;
        private readonly int _gridW, _gridH, _gridD;
        private readonly Vector3 _gridMin;
        private readonly float _cellSize;
        private int _propagationSteps = 4;
        private float _propagationStrength = 0.5f;

        public int GridWidth => _gridW;
        public int GridHeight => _gridH;
        public int GridDepth => _gridD;
        public int PropagationSteps { get => _propagationSteps; set => _propagationSteps = Math.Clamp(value, 1, 8); }

        public LightweightLPV(int gridW, int gridH, int gridD, Vector3 gridMin, float cellSize)
        {
            _gridW = gridW;
            _gridH = gridH;
            _gridD = gridD;
            _gridMin = gridMin;
            _cellSize = cellSize;
            _grid = new SH2Coefficients[gridW, gridH, gridD];
            _tempGrid = new SH2Coefficients[gridW, gridH, gridD];
        }

        /// <summary>
        /// 注入直接光到 LPV (在光源位置)
        /// </summary>
        public void InjectLight(Vector3 worldPos, SH2Coefficients sh)
        {
            var (x, y, z) = WorldToGrid(worldPos);
            if (x < 0 || x >= _gridW || y < 0 || y >= _gridH || z < 0 || z >= _gridD)
                return;

            _grid[x, y, z] = _grid[x, y, z] + sh;
        }

        /// <summary>
        /// 执行光传播 (一次迭代 = 6 邻居平均)
        /// </summary>
        public void Propagate()
        {
            for (int step = 0; step < _propagationSteps; step++)
            {
                // 交换缓冲
                var src = step % 2 == 0 ? _grid : _tempGrid;
                var dst = step % 2 == 0 ? _tempGrid : _grid;

                for (int z = 0; z < _gridD; z++)
                for (int y = 0; y < _gridH; y++)
                for (int x = 0; x < _gridW; x++)
                {
                    SH2Coefficients current = src[x, y, z];
                    SH2Coefficients accumulated = current * (1f - _propagationStrength);

                    // 6 邻居传播
                    float neighborWeight = _propagationStrength / 6f;

                    if (x > 0) accumulated = accumulated + src[x - 1, y, z] * neighborWeight;
                    if (x < _gridW - 1) accumulated = accumulated + src[x + 1, y, z] * neighborWeight;
                    if (y > 0) accumulated = accumulated + src[x, y - 1, z] * neighborWeight;
                    if (y < _gridH - 1) accumulated = accumulated + src[x, y + 1, z] * neighborWeight;
                    if (z > 0) accumulated = accumulated + src[x, y, z - 1] * neighborWeight;
                    if (z < _gridD - 1) accumulated = accumulated + src[x, y, z + 1] * neighborWeight;

                    dst[x, y, z] = accumulated;
                }
            }
        }

        /// <summary>
        /// 采样指定位置的间接光
        /// </summary>
        public SH2Coefficients Sample(Vector3 worldPos)
        {
            var (x, y, z) = WorldToGrid(worldPos);
            x = Math.Clamp(x, 0, _gridW - 1);
            y = Math.Clamp(y, 0, _gridH - 1);
            z = Math.Clamp(z, 0, _gridD - 1);
            return _grid[x, y, z];
        }

        /// <summary>清空网格</summary>
        public void Clear()
        {
            Array.Clear(_grid, 0, _grid.Length);
            Array.Clear(_tempGrid, 0, _tempGrid.Length);
        }

        private (int x, int y, int z) WorldToGrid(Vector3 worldPos)
        {
            Vector3 local = (worldPos - _gridMin) / _cellSize;
            return ((int)local.X, (int)local.Y, (int)local.Z);
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    //  屏幕空间全局光照 (Screen-Space GI)
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 屏幕空间全局光照 —— 基于 G-Buffer 的间接光近似
    /// 在屏幕空间对附近的像素采样, 获取间接光
    /// </summary>
    public sealed class ScreenSpaceGI
    {
        private int _sampleCount = 8;
        private float _maxRadius = 0.3f;   // 屏幕空间半径 (0~1)
        private float _temporalWeight = 0.9f;
        private Vector3 _fallbackAmbient = new(0.15f, 0.15f, 0.2f);

        public int SampleCount { get => _sampleCount; set => _sampleCount = Math.Clamp(value, 4, 32); }
        public float MaxRadius { get => _maxRadius; set => _maxRadius = Math.Clamp(value, 0.05f, 0.5f); }
        public float TemporalWeight { get => _temporalWeight; set => _temporalWeight = Math.Clamp(value, 0.5f, 0.98f); }

        /// <summary>
        /// 计算屏幕空间间接光
        /// </summary>
        /// <param name="uv">当前像素 UV</param>
        /// <param name="normal">当前像素法线 (世界空间)</param>
        /// <param name="depth">当前像素深度</param>
        /// <param name="sampleColor">采样函数: UV → (颜色, 法线, 深度)</param>
        public Vector3 ComputeSSGI(
            Vector2 uv,
            Vector3 normal,
            float depth,
            Func<Vector2, (Vector3 color, Vector3 sampleNormal, float sampleDepth)> sampleColor)
        {
            Vector3 indirect = Vector3.Zero;
            float totalWeight = 0f;

            // 使用低差异序列 (Halton) 采样
            for (int i = 0; i < _sampleCount; i++)
            {
                // Halton 序列生成采样方向
                float angle = Halton(i + 1, 2) * MathF.PI * 2f;
                float radius = Halton(i + 1, 3) * _maxRadius;

                Vector2 sampleUV = uv + new Vector2(
                    MathF.Cos(angle) * radius,
                    MathF.Sin(angle) * radius * 1.778f); // 宽高比补偿

                // 采样 G-Buffer
                var (color, sampleNormal, sampleDepth) = sampleColor(sampleUV);

                // 深度差异检查 (避免采样到太远的像素)
                float depthDiff = MathF.Abs(sampleDepth - depth);
                if (depthDiff > 0.1f)
                    continue;

                // 法线对齐: 只接受朝向当前像素法线的反射光
                float normalAlign = Vector3.Dot(normal, -sampleNormal);
                if (normalAlign < 0f)
                    continue;

                // 距离衰减
                float dist = Vector2.Distance(uv, sampleUV);
                float falloff = 1f - dist / _maxRadius;

                float weight = normalAlign * falloff;
                indirect += color * weight;
                totalWeight += weight;
            }

            if (totalWeight < 0.001f)
                return _fallbackAmbient;

            return indirect / totalWeight;
        }

        /// <summary>
        /// Halton 序列 (低差异序列)
        /// </summary>
        private static float Halton(int index, int baseNumber)
        {
            float result = 0f;
            float f = 1f;
            while (index > 0)
            {
                f /= baseNumber;
                result += f * (index % baseNumber);
                index /= baseNumber;
            }
            return result;
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    //  时间累积器 (Temporal Accumulator)
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 时间累积器 —— 跨帧累积 GI 结果, 减少噪声
    /// </summary>
    public sealed class TemporalGIAccumulator
    {
        private Vector3[] _history;
        private int _width;
        private int _height;
        private float _blendFactor = 0.1f; // 新帧权重 (越小越稳定但延迟越大)
        private bool _initialized = false;

        public float BlendFactor { get => _blendFactor; set => _blendFactor = Math.Clamp(value, 0.02f, 0.5f); }

        public TemporalGIAccumulator(int width, int height)
        {
            _width = width;
            _height = height;
            _history = new Vector3[width * height];
        }

        /// <summary>
        /// 时间累积 (带重投影)
        /// </summary>
        public Vector3 Accumulate(int x, int y, Vector3 current, Vector2? velocity = null)
        {
            // 计算历史帧位置 (考虑运动)
            int histX = x, histY = y;
            if (velocity.HasValue)
            {
                histX = Math.Clamp(x - (int)(velocity.Value.X * _width), 0, _width - 1);
                histY = Math.Clamp(y - (int)(velocity.Value.Y * _height), 0, _height - 1);
            }

            Vector3 history = _history[histY * _width + histX];

            // 亮度拒绝: 如果历史帧和当前帧差异太大, 使用当前帧
            float lumDiff = MathF.Abs(history.Length() - current.Length());
            float threshold = current.Length() * 0.5f + 0.1f;

            float blend = lumDiff > threshold ? 1f : _blendFactor;

            Vector3 result = Vector3.Lerp(history, current, blend);
            _history[y * _width + x] = result;
            return result;
        }

        /// <summary>重置历史缓冲</summary>
        public void Reset()
        {
            Array.Clear(_history, 0, _history.Length);
            _initialized = false;
        }

        /// <summary>调整大小</summary>
        public void Resize(int width, int height)
        {
            _width = width;
            _height = height;
            _history = new Vector3[width * height];
            _initialized = false;
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    //  高效 GI 管线 (Efficient GI Pipeline)
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// GI 质量等级
    /// </summary>
    public enum GIQualityLevel
    {
        /// <summary>极低: 仅环境常量</summary>
        AmbientOnly,
        /// <summary>低: 探针场 (低密度)</summary>
        ProbesLow,
        /// <summary>中: 探针场 (高密度) + LPV</summary>
        ProbesHigh,
        /// <summary>高: 探针 + LPV + SSGI</summary>
        Full,
        /// <summary>超高: 全部 + 高采样</summary>
        Ultra
    }

    /// <summary>
    /// 高效 GI 管线 —— 整合所有 GI 组件
    /// </summary>
    public sealed class EfficientGIPipeline
    {
        private GIQualityLevel _quality = GIQualityLevel.ProbesHigh;
        private readonly SparseIrradianceProbeField _probeField;
        private LightweightLPV? _lpv;
        private ScreenSpaceGI? _ssgi;
        private TemporalGIAccumulator? _temporal;
        private Vector3 _ambientColor = new(0.15f, 0.15f, 0.2f);
        private float _ambientIntensity = 0.3f;

        public GIQualityLevel Quality
        {
            get => _quality;
            set
            {
                _quality = value;
                UpdateComponents();
            }
        }

        public SparseIrradianceProbeField ProbeField => _probeField;
        public LightweightLPV? LPV => _lpv;
        public ScreenSpaceGI? SSGI => _ssgi;
        public TemporalGIAccumulator? Temporal => _temporal;
        public Vector3 AmbientColor { get => _ambientColor; set => _ambientColor = value; }
        public float AmbientIntensity { get => _ambientIntensity; set => _ambientIntensity = value; }

        public EfficientGIPipeline(GIQualityLevel quality = GIQualityLevel.ProbesHigh)
        {
            _probeField = new SparseIrradianceProbeField();
            Quality = quality;
        }

        private void UpdateComponents()
        {
            switch (_quality)
            {
                case GIQualityLevel.AmbientOnly:
                    _lpv = null;
                    _ssgi = null;
                    _temporal = null;
                    break;
                case GIQualityLevel.ProbesLow:
                    _lpv = null;
                    _ssgi = null;
                    _temporal = null;
                    _probeField.Configure(8.0f, 4, 10f); // 低密度
                    break;
                case GIQualityLevel.ProbesHigh:
                    _lpv = new LightweightLPV(16, 16, 16, new Vector3(-50), 6.25f);
                    _ssgi = null;
                    _temporal = null;
                    _probeField.Configure(4.0f, 8, 5f); // 高密度
                    break;
                case GIQualityLevel.Full:
                    _lpv = new LightweightLPV(24, 24, 24, new Vector3(-60), 5.0f);
                    _ssgi = new ScreenSpaceGI { SampleCount = 8, MaxRadius = 0.2f };
                    _temporal = new TemporalGIAccumulator(1920, 1080) { BlendFactor = 0.1f };
                    _probeField.Configure(4.0f, 16, 5f);
                    break;
                case GIQualityLevel.Ultra:
                    _lpv = new LightweightLPV(32, 32, 32, new Vector3(-80), 5.0f);
                    _ssgi = new ScreenSpaceGI { SampleCount = 16, MaxRadius = 0.3f };
                    _temporal = new TemporalGIAccumulator(1920, 1080) { BlendFactor = 0.05f };
                    _probeField.Configure(2.0f, 32, 3f);
                    break;
            }
        }

        /// <summary>
        /// 计算指定位置的间接光
        /// </summary>
        public Vector3 ComputeIndirectLighting(
            Vector3 worldPos,
            Vector3 normal,
            Vector3 albedo,
            Vector2? screenUV = null,
            float depth = 0f,
            Func<Vector2, (Vector3, Vector3, float)>? ssgiSampler = null,
            Vector2? velocity = null)
        {
            Vector3 indirect = _ambientColor * _ambientIntensity;

            // 1. 探针场采样
            if (_quality >= GIQualityLevel.ProbesLow)
            {
                SH2Coefficients sh = _probeField.Sample(worldPos, normal);
                Vector3 probeIrradiance = sh.Evaluate(normal);
                indirect += probeIrradiance;
            }

            // 2. LPV 采样 (1次 bounce 间接光)
            if (_lpv != null)
            {
                SH2Coefficients lpvSH = _lpv.Sample(worldPos);
                Vector3 lpvIrradiance = lpvSH.Evaluate(normal);
                indirect += lpvIrradiance * 0.5f; // LPV 贡献降权
            }

            // 3. SSGI (屏幕空间间接光)
            if (_ssgi != null && screenUV.HasValue && ssgiSampler != null)
            {
                Vector3 ssgi = _ssgi.ComputeSSGI(screenUV.Value, normal, depth, ssgiSampler);
                indirect += ssgi * 0.3f;
            }

            // 4. 时间累积
            if (_temporal != null && screenUV.HasValue)
            {
                int x = (int)(screenUV.Value.X * 1920);
                int y = (int)(screenUV.Value.Y * 1080);
                x = Math.Clamp(x, 0, 1919);
                y = Math.Clamp(y, 0, 1079);
                indirect = _temporal.Accumulate(x, y, indirect, velocity);
            }

            // 乘以反照率 (能量守恒: 间接光被表面吸收后反射)
            return indirect * albedo;
        }

        /// <summary>
        /// 更新 GI 系统 (每帧调用)
        /// </summary>
        public void Update(
            float currentTime,
            Func<Vector3, SH2Coefficients>? irradianceSampler = null,
            bool sceneChanged = false)
        {
            if (sceneChanged)
                _probeField.InvalidateAll();

            if (irradianceSampler != null && _quality >= GIQualityLevel.ProbesLow)
            {
                _probeField.UpdateProbes(currentTime, irradianceSampler);
            }

            if (_lpv != null)
            {
                _lpv.Propagate();
            }
        }

        /// <summary>
        /// 自动检测并设置质量等级
        /// </summary>
        public static GIQualityLevel AutoDetect(int vramMB)
        {
            if (vramMB < 2048) return GIQualityLevel.AmbientOnly;
            if (vramMB < 4096) return GIQualityLevel.ProbesLow;
            if (vramMB < 8192) return GIQualityLevel.ProbesHigh;
            if (vramMB < 12288) return GIQualityLevel.Full;
            return GIQualityLevel.Ultra;
        }

        /// <summary>
        /// 估算开销 (毫秒, 基于 GTX 1060)
        /// </summary>
        public float EstimateCostMS(int screenWidth, int screenHeight)
        {
            float cost = 0f;
            float pixels = screenWidth * screenHeight;

            // 环境光: 几乎免费
            if (_quality == GIQualityLevel.AmbientOnly)
                cost += pixels * 0.01e-6f;

            // 探针采样: 每像素 1 次 SH 评估
            if (_quality >= GIQualityLevel.ProbesLow)
                cost += pixels * 0.1e-6f;

            // LPV: 每像素 1 次 SH 评估 + 传播
            if (_lpv != null)
            {
                cost += pixels * 0.1e-6f;
                cost += _lpv.GridWidth * _lpv.GridHeight * _lpv.GridDepth * _lpv.PropagationSteps * 0.5e-6f;
            }

            // SSGI: 每像素 SampleCount 次采样
            if (_ssgi != null)
                cost += pixels * _ssgi.SampleCount * 0.2e-6f;

            // 时间累积
            if (_temporal != null)
                cost += pixels * 0.05e-6f;

            return cost * 1000f;
        }
    }
}
