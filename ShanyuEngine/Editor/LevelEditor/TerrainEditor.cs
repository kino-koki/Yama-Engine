using System;
using System.Collections.Generic;
using System.Numerics;

namespace ShanyuEngine.Editor.LevelEditor
{
    // ════════════════════════════════════════════════════════════════════════
    // 山雨引擎 - 地形编辑器 (TerrainEditor)
    //
    // 类似 UE Landscape Editor 的地形编辑系统:
    //   * TerrainData: 高度图/权重图/法线图/孔洞数据
    //   * TerrainComponent: 地形组件 (LOD/XZ尺寸/分辨率/材质图层)
    //   * TerrainLayer: 图层 (名称/权重纹理/法线纹理/参数/材质)
    //   * TerrainBrush: 笔刷 (圆形/方形/噪声/平滑/衰减)
    //   * 笔刷模式: Sculpt/Paint/Noise (Perlin/Simplex/fBm)
    //   * 笔刷预览 (实时渲染笔刷轮廓)
    //   * 地形LOD (四叉树)
    //   * 地形导出 (PNG/RAW/OBJ)
    // ════════════════════════════════════════════════════════════════════════

    // ──────────────────────────────────────────────────────────────
    // 地形数据
    // ──────────────────────────────────────────────────────────────

    /// <summary>地形数据容器: 高度图 + 权重图 + 法线图 + 孔洞掩码。</summary>
    public sealed class TerrainData
    {
        /// <summary>高度图宽度 (像素)。</summary>
        public int Width { get; }
        /// <summary>高度图高度 (像素)。</summary>
        public int Height { get; }

        /// <summary>高度图 (16bit float, 归一化 0..1)。</summary>
        public float[] Heightmap { get; }
        /// <summary>权重图列表 (每图层一张, R8 值 0..255)。</summary>
        public readonly List<byte[]> WeightMaps = new();
        /// <summary>法线图 (R8G8B8, 每顶点一个法线)。</summary>
        public Vector3[] NormalMap { get; private set; }
        /// <summary>孔洞掩码 (true = 有孔/不渲染)。</summary>
        public bool[] Holes { get; }

        /// <summary>世界空间 XZ 尺寸。</summary>
        public float WorldSizeX { get; set; } = 2048f;
        /// <summary>世界空间 Z 尺寸。</summary>
        public float WorldSizeZ { get; set; } = 2048f;
        /// <summary>高度范围 (高度图 0..1 映射到 -HeightRange..+HeightRange)。</summary>
        public float HeightRange { get; set; } = 512f;

        /// <summary>是否被修改 (脏标记)。</summary>
        public bool IsDirty { get; private set; }

        /// <summary>数据变更事件。</summary>
        public event Action OnDataChanged;

        public TerrainData(int width, int height)
        {
            Width = Math.Max(1, width);
            Height = Math.Max(1, height);
            int total = Width * Height;
            Heightmap = new float[total];
            NormalMap = new Vector3[total];
            Holes = new bool[total];
        }

        /// <summary>获取指定像素的高度 (归一化 0..1)。</summary>
        public float GetHeight(int x, int y) => Heightmap[ClampIndex(x, y)];

        /// <summary>设置指定像素的高度 (归一化 0..1)。</summary>
        public void SetHeight(int x, int y, float value)
        {
            Heightmap[ClampIndex(x, y)] = Math.Clamp(value, 0f, 1f);
            IsDirty = true;
        }

        /// <summary>获取世界空间高度 (米)。</summary>
        public float GetWorldHeight(int x, int y)
        {
            return (GetHeight(x, y) * 2f - 1f) * HeightRange;
        }

        /// <summary>获取法线。</summary>
        public Vector3 GetNormal(int x, int y) => NormalMap[ClampIndex(x, y)];

        /// <summary>是否有孔。</summary>
        public bool IsHole(int x, int y) => Holes[ClampIndex(x, y)];

        /// <summary>设置孔洞。</summary>
        public void SetHole(int x, int y, bool isHole)
        {
            Holes[ClampIndex(x, y)] = isHole;
            IsDirty = true;
        }

        /// <summary>从世界坐标获取高度图索引。</summary>
        public (int X, int Y) WorldToHeightmap(float worldX, float worldZ)
        {
            int x = (int)((worldX / WorldSizeX + 0.5f) * (Width - 1));
            int y = (int)((worldZ / WorldSizeZ + 0.5f) * (Height - 1));
            return (Math.Clamp(x, 0, Width - 1), Math.Clamp(y, 0, Height - 1));
        }

        /// <summary>从高度图索引获取世界坐标。</summary>
        public (float X, float Z) HeightmapToWorld(int hx, int hy)
        {
            float x = ((float)hx / (Width - 1) - 0.5f) * WorldSizeX;
            float z = ((float)hy / (Height - 1) - 0.5f) * WorldSizeZ;
            return (x, z);
        }

        /// <summary>重新计算所有法线 (有限差分法)。</summary>
        public void RecalculateNormals()
        {
            float stepX = WorldSizeX / Math.Max(1, Width - 1);
            float stepZ = WorldSizeZ / Math.Max(1, Height - 1);

            for (int y = 0; y < Height; y++)
            {
                for (int x = 0; x < Width; x++)
                {
                    float hL = x > 0 ? GetHeight(x - 1, y) : GetHeight(x, y);
                    float hR = x < Width - 1 ? GetHeight(x + 1, y) : GetHeight(x, y);
                    float hD = y > 0 ? GetHeight(x, y - 1) : GetHeight(x, y);
                    float hU = y < Height - 1 ? GetHeight(x, y + 1) : GetHeight(x, y);

                    float dhdx = (hR - hL) * 2f * HeightRange / stepX;
                    float dhdz = (hU - hD) * 2f * HeightRange / stepZ;

                    Vector3 normal = Vector3.Normalize(new Vector3(-dhdx, 1f, -dhdz));
                    NormalMap[y * Width + x] = normal;
                }
            }
            OnDataChanged?.Invoke();
        }

        /// <summary>清除脏标记。</summary>
        public void ClearDirty() => IsDirty = false;

        private int ClampIndex(int x, int y)
        {
            x = Math.Clamp(x, 0, Width - 1);
            y = Math.Clamp(y, 0, Height - 1);
            return y * Width + x;
        }
    }

    // ──────────────────────────────────────────────────────────────
    // 地形组件
    // ──────────────────────────────────────────────────────────────

    /// <summary>地形组件: 管理 LOD、物理碰撞、渲染网格。</summary>
    public sealed class TerrainComponent
    {
        /// <summary>关联的地形数据。</summary>
        public TerrainData Data { get; }
        /// <summary>世界位置。</summary>
        public Vector3 Position { get; set; } = Vector3.Zero;
        /// <summary>世界旋转。</summary>
        public Quaternion Rotation { get; set; } = Quaternion.Identity;

        /// <summary>LOD 数量。</summary>
        public int LodCount { get; set; } = 5;
        /// <summary>LOD 0 的像素密度 (每 quad)。</summary>
        public int Lod0Resolution { get; set; } = 1;

        /// <summary>图层数。</summary>
        public int LayerCount { get; private set; }
        /// <summary>图层列表。</summary>
        public readonly List<TerrainLayer> Layers = new();

        /// <summary>LOD 切换距离 (每 LOD 一条)。</summary>
        public float[] LodDistances { get; private set; }

        /// <summary>是否启用碰撞体。</summary>
        public bool PhysicsEnabled { get; set; } = true;
        /// <summary>碰撞体降采样倍率。</summary>
        public int PhysicsDownsample { get; set; } = 4;

        /// <summary>是否可见。</summary>
        public bool IsVisible { get; set; } = true;

        public TerrainComponent(TerrainData data)
        {
            Data = data ?? throw new ArgumentNullException(nameof(data));
            UpdateLodDistances();
        }

        /// <summary>添加图层。</summary>
        public TerrainLayer AddLayer(string name)
        {
            var layer = new TerrainLayer(name, LayerCount);
            Layers.Add(layer);
            LayerCount = Layers.Count;
            // 为新图层创建权重图
            Data.WeightMaps.Add(new byte[Data.Width * Data.Height]);
            UpdateLodDistances();
            return layer;
        }

        /// <summary>移除图层。</summary>
        public bool RemoveLayer(int index)
        {
            if (index < 0 || index >= Layers.Count) return false;
            Layers.RemoveAt(index);
            if (index < Data.WeightMaps.Count)
                Data.WeightMaps.RemoveAt(index);
            // 重新编号
            for (int i = 0; i < Layers.Count; i++)
                Layers[i].LayerIndex = i;
            LayerCount = Layers.Count;
            return true;
        }

        /// <summary>根据到摄像机距离确定当前 LOD。</summary>
        public int CalculateLod(float distance)
        {
            for (int i = 0; i < LodDistances.Length; i++)
            {
                if (distance < LodDistances[i]) return i;
            }
            return LodCount - 1;
        }

        /// <summary>更新 LOD 距离数组。</summary>
        private void UpdateLodDistances()
        {
            LodDistances = new float[LodCount];
            for (int i = 0; i < LodCount; i++)
            {
                LodDistances[i] = (float)Math.Pow(2, i) * 50f;
            }
        }
    }

    // ──────────────────────────────────────────────────────────────
    // 地形图层
    // ──────────────────────────────────────────────────────────────

    /// <summary>地形图层: 管理单层材质/纹理参数。</summary>
    public sealed class TerrainLayer
    {
        /// <summary>图层名称。</summary>
        public string Name { get; set; }
        /// <summary>图层索引。</summary>
        public int LayerIndex { get; }

        /// <summary>漫射纹理路径。</summary>
        public string DiffuseTexturePath { get; set; } = string.Empty;
        /// <summary>法线纹理路径。</summary>
        public string NormalTexturePath { get; set; } = string.Empty;
        /// <summary>粗糙度纹理路径。</summary>
        public string RoughnessTexturePath { get; set; } = string.Empty;

        /// <summary>纹理平铺 X。</summary>
        public float TilingX { get; set; } = 1f;
        /// <summary>纹理平铺 Y。</summary>
        public float TilingY { get; set; } = 1f;

        /// <summary>高度缩放 (用于 height-blend)。</summary>
        public float HeightScale { get; set; } = 0.1f;
        /// <summary>高度偏移。</summary>
        public float HeightOffset { get; set; } = 0f;

        /// <summary>材质参数。</summary>
        public float Metallic { get; set; }
        public float Roughness { get; set; } = 0.8f;
        public Vector3 BaseColor { get; set; } = Vector3.One;

        /// <summary>是否启用。</summary>
        public bool IsEnabled { get; set; } = true;

        public TerrainLayer(string name, int index)
        {
            Name = name;
            LayerIndex = index;
        }
    }

    // ──────────────────────────────────────────────────────────────
    // 地形笔刷
    // ──────────────────────────────────────────────────────────────

    /// <summary>地形笔刷: 定义笔刷形状与强度分布。</summary>
    public sealed class TerrainBrush
    {
        /// <summary>笔刷形状。</summary>
        public BrushShape Shape { get; set; } = BrushShape.Circle;

        /// <summary>笔刷半径 (世界单位)。</summary>
        public float Radius { get; set; } = 10f;
        /// <summary>笔刷强度。</summary>
        public float Strength { get; set; } = 0.5f;
        /// <summary>笔刷衰减系数。</summary>
        public float Falloff { get; set; } = 0.5f;

        /// <summary>纹理遮罩路径 (可选)。</summary>
        public string MaskTexturePath { get; set; } = string.Empty;

        /// <summary>是否反转笔刷。</summary>
        public bool Inverted { get; set; }

        /// <summary>计算指定距离处的笔刷权重 (0..1)。</summary>
        public float CalculateWeight(float distance)
        {
            float normalizedDist = distance / Math.Max(0.001f, Radius);
            if (normalizedDist > 1f) return 0f;

            float weight = Shape switch
            {
                BrushShape.Circle => 1f - normalizedDist,
                BrushShape.Square => normalizedDist <= 1f ? 1f : 0f,
                BrushShape.Smooth => (float)Math.Pow(1f - normalizedDist, 2f),
                BrushShape.Falloff => (float)Math.Pow(1f - normalizedDist, 1f / Math.Max(0.001f, Falloff)),
                BrushShape.Noise => 0.5f + 0.5f * (float)Math.Sin(normalizedDist * Math.PI * 4f),
                _ => 1f - normalizedDist
            };

            if (Inverted) weight = 1f - weight;
            return weight * Strength;
        }

        /// <summary>获取笔刷影响半径内的像素坐标列表。</summary>
        public List<(int X, int Y, float Weight)> GetAffectedPixels(
            TerrainData data, float worldCenterX, float worldCenterZ)
        {
            var result = new List<(int, int, float)>();
            int radiusPixelsX = (int)Math.Ceiling(Radius / (data.WorldSizeX / data.Width));
            int radiusPixelsZ = (int)Math.Ceiling(Radius / (data.WorldSizeZ / data.Height));

            var (cx, cy) = data.WorldToHeightmap(worldCenterX, worldCenterZ);

            for (int dy = -radiusPixelsZ; dy <= radiusPixelsZ; dy++)
            {
                for (int dx = -radiusPixelsX; dx <= radiusPixelsX; dx++)
                {
                    int px = cx + dx;
                    int py = cy + dy;
                    if (px < 0 || px >= data.Width || py < 0 || py >= data.Height) continue;

                    float worldDist = MathF.Sqrt(
                        MathF.Pow(dx * data.WorldSizeX / data.Width, 2) +
                        MathF.Pow(dy * data.WorldSizeZ / data.Height, 2));
                    float w = CalculateWeight(worldDist);
                    if (w > 0.001f)
                        result.Add((px, py, w));
                }
            }
            return result;
        }
    }

    // ──────────────────────────────────────────────────────────────
    // 笔刷模式
    // ──────────────────────────────────────────────────────────────

    /// <summary>地形笔刷模式: 雕刻/绘制/噪声。</summary>
    public enum TerrainBrushMode
    {
        /// <summary>雕刻模式。</summary>
        Sculpt,
        /// <summary>图层绘制模式。</summary>
        Paint,
        /// <summary>噪声生成模式。</summary>
        Noise
    }

    /// <summary>雕刻子操作。</summary>
    public enum SculptSubOp
    {
        Raise,
        Lower,
        Smooth,
        Flatten
    }

    /// <summary>噪声类型。</summary>
    public enum TerrainNoiseType
    {
        Perlin,
        Simplex,
        Value,
        FractalBrownianMotion
    }

    /// <summary>
    /// 笔刷模式基类。
    /// </summary>
    public abstract class TerrainBrushModeBase
    {
        /// <summary>模式类型。</summary>
        public abstract TerrainBrushMode Mode { get; }
        /// <summary>应用笔刷。</summary>
        public abstract void Apply(TerrainData data, float worldX, float worldZ, TerrainBrush brush);
    }

    /// <summary>雕刻模式: 升高/降低/平滑/平坦。</summary>
    public sealed class SculptBrushMode : TerrainBrushModeBase
    {
        public override TerrainBrushMode Mode => TerrainBrushMode.Sculpt;
        /// <summary>当前雕刻子操作。</summary>
        public SculptSubOp SubOp { get; set; } = SculptSubOp.Raise;
        /// <summary>平坦化目标高度 (仅 Flatten)。</summary>
        public float FlattenTargetHeight { get; set; } = 0.5f;

        public override void Apply(TerrainData data, float worldX, float worldZ, TerrainBrush brush)
        {
            var pixels = brush.GetAffectedPixels(data, worldX, worldZ);
            foreach (var (px, py, weight) in pixels)
            {
                float current = data.GetHeight(px, py);
                float delta = weight * 0.001f; // 归一化增量

                float newHeight = SubOp switch
                {
                    SculptSubOp.Raise => Math.Min(1f, current + delta),
                    SculptSubOp.Lower => Math.Max(0f, current - delta),
                    SculptSubOp.Smooth => SmoothSample(data, px, py, 1, weight),
                    SculptSubOp.Flatten => current + (FlattenTargetHeight - current) * weight,
                    _ => current
                };

                data.SetHeight(px, py, newHeight);
            }
        }

        private static float SmoothSample(TerrainData data, int cx, int cy, int radius, float weight)
        {
            float sum = 0f;
            int count = 0;
            for (int dy = -radius; dy <= radius; dy++)
            {
                for (int dx = -radius; dx <= radius; dx++)
                {
                    sum += data.GetHeight(cx + dx, cy + dy);
                    count++;
                }
            }
            float average = sum / Math.Max(1, count);
            float current = data.GetHeight(cx, cy);
            return current + (average - current) * weight;
        }
    }

    /// <summary>图层绘制模式: 修改指定图层的权重。</summary>
    public sealed class PaintBrushMode : TerrainBrushModeBase
    {
        public override TerrainBrushMode Mode => TerrainBrushMode.Paint;
        /// <summary>目标图层索引。</summary>
        public int TargetLayerIndex { get; set; }
        /// <summary>绘制值 (0..255)。</summary>
        public byte PaintValue { get; set; } = 255;

        public override void Apply(TerrainData data, float worldX, float worldZ, TerrainBrush brush)
        {
            if (TargetLayerIndex < 0 || TargetLayerIndex >= data.WeightMaps.Count) return;
            var weightMap = data.WeightMaps[TargetLayerIndex];
            var pixels = brush.GetAffectedPixels(data, worldX, worldZ);

            foreach (var (px, py, weight) in pixels)
            {
                int idx = py * data.Width + px;
                float current = weightMap[idx] / 255f;
                float blended = current + (PaintValue / 255f - current) * weight;
                weightMap[idx] = (byte)Math.Clamp(blended * 255f, 0, 255);
            }
            data.ClearDirty(); // 权重图单独管理脏标记
        }
    }

    /// <summary>噪声生成模式: 程序化噪声地形生成。</summary>
    public sealed class NoiseBrushMode : TerrainBrushModeBase
    {
        public override TerrainBrushMode Mode => TerrainBrushMode.Noise;
        /// <summary>噪声类型。</summary>
        public TerrainNoiseType NoiseType { get; set; } = TerrainNoiseType.Perlin;
        /// <summary>噪声频率 (尺度)。</summary>
        public float Frequency { get; set; } = 0.01f;
        /// <summary>噪声振幅。</summary>
        public float Amplitude { get; set; } = 0.2f;
        /// <summary>fBm 八度数。</summary>
        public int Octaves { get; set; } = 4;
        /// <summary>fBm 持久度。</summary>
        public float Persistence { get; set; } = 0.5f;
        /// <summary>fBm 间隙度。</summary>
        public float Lacunarity { get; set; } = 2f;

        /// <summary>伪随机种子。</summary>
        private int _seed = 42;

        public override void Apply(TerrainData data, float worldX, float worldZ, TerrainBrush brush)
        {
            var pixels = brush.GetAffectedPixels(data, worldX, worldZ);
            foreach (var (px, py, weight) in pixels)
            {
                float current = data.GetHeight(px, py);
                float noise = GenerateNoise(px, py);

                if (NoiseType == TerrainNoiseType.FractalBrownianMotion)
                    noise = GenerateFBM(px, py);

                float newHeight = current + noise * weight * Amplitude;
                data.SetHeight(px, py, Math.Clamp(newHeight, 0f, 1f));
            }
        }

        private float GenerateNoise(int x, int y)
        {
            float fx = x * Frequency;
            float fy = y * Frequency;

            return NoiseType switch
            {
                TerrainNoiseType.Perlin => PerlinNoise(fx, fy),
                TerrainNoiseType.Simplex => SimplexNoise(fx, fy),
                TerrainNoiseType.Value => ValueNoise(fx, fy),
                _ => PerlinNoise(fx, fy)
            };
        }

        private float GenerateFBM(int x, int y)
        {
            float total = 0f;
            float freq = Frequency;
            float amp = 1f;
            float maxAmp = 0f;

            for (int i = 0; i < Octaves; i++)
            {
                float fx = x * freq;
                float fy = y * freq;
                total += PerlinNoise(fx, fy) * amp;
                maxAmp += amp;
                freq *= Lacunarity;
                amp *= Persistence;
            }
            return total / Math.Max(0.001f, maxAmp);
        }

        // 简化的 Perlin 噪声实现
        private static float PerlinNoise(float x, float y)
        {
            int ix = (int)MathF.Floor(x) & 0xFF;
            int iy = (int)MathF.Floor(y) & 0xFF;
            float fx = x - MathF.Floor(x);
            float fy = y - MathF.Floor(y);
            float u = Fade(fx);
            float v = Fade(fy);

            int a = Hash2D(ix, iy);
            int b = Hash2D(ix + 1, iy);
            int c = Hash2D(ix, iy + 1);
            int d = Hash2D(ix + 1, iy + 1);

            float x1 = Lerp(Grad(a, fx, fy), Grad(b, fx - 1f, fy), u);
            float x2 = Lerp(Grad(c, fx, fy - 1f), Grad(d, fx - 1f, fy - 1f), u);
            return Lerp(x1, x2, v);
        }

        // 简化的 Value 噪声
        private static float ValueNoise(float x, float y)
        {
            int ix = (int)MathF.Floor(x);
            int iy = (int)MathF.Floor(y);
            float fx = x - MathF.Floor(x);
            float fy = y - MathF.Floor(y);
            float u = fx * fx * (3f - 2f * fx);
            float v = fy * fy * (3f - 2f * fy);

            float a = PseudoRandom(ix, iy);
            float b = PseudoRandom(ix + 1, iy);
            float c = PseudoRandom(ix, iy + 1);
            float d = PseudoRandom(ix + 1, iy + 1);

            return Lerp(Lerp(a, b, u), Lerp(c, d, u), v);
        }

        // 简化的 Simplex 噪声 (2D)
        private static float SimplexNoise(float x, float y)
        {
            const float F2 = 0.5f * (MathF.Sqrt(3f) - 1f);
            const float G2 = (3f - MathF.Sqrt(3f)) / 6f;

            float s = (x + y) * F2;
            int i = (int)MathF.Floor(x + s);
            int j = (int)MathF.Floor(y + s);

            float t = (i + j) * G2;
            float x0 = x - (i - t);
            float y0 = y - (j - t);

            int i1, j1;
            if (x0 > y0) { i1 = 1; j1 = 0; }
            else { i1 = 0; j1 = 1; }

            float x1 = x0 - i1 + G2;
            float y1 = y0 - j1 + G2;
            float x2 = x0 - 1f + 2f * G2;
            float y2 = y0 - 1f + 2f * G2;

            int ii = i & 0xFF;
            int jj = j & 0xFF;

            float n0 = 0f, n1 = 0f, n2 = 0f;

            float t0 = 0.5f - x0 * x0 - y0 * y0;
            if (t0 > 0f) { t0 *= t0; n0 = t0 * t0 * Grad2D(Hash2D(ii, jj), x0, y0); }

            float t1 = 0.5f - x1 * x1 - y1 * y1;
            if (t1 > 0f) { t1 *= t1; n1 = t1 * t1 * Grad2D(Hash2D(ii + i1, jj + j1), x1, y1); }

            float t2 = 0.5f - x2 * x2 - y2 * y2;
            if (t2 > 0f) { t2 *= t2; n2 = t2 * t2 * Grad2D(Hash2D(ii + 1, jj + 1), x2, y2); }

            return 70f * (n0 + n1 + n2);
        }

        private static float Fade(float t) => t * t * t * (t * (t * 6f - 15f) + 10f);
        private static float Lerp(float a, float b, float t) => a + t * (b - a);
        private static int Hash2D(int x, int y) => (x * 374761393 + y * 668265263) & 0xFFFF;
        private static float PseudoRandom(int x, int y) => ((x * 374761393 + y * 668265263) & 0xFFFF) / (float)0xFFFF;
        private static float Grad(int hash, float x, float y)
        {
            int h = hash & 3;
            return (h == 0 ? x + y : h == 1 ? -x + y : h == 2 ? x - y : -x - y);
        }
        private static float Grad2D(int hash, float x, float y)
        {
            int h = hash & 7;
            float u = h < 4 ? x : y;
            float v = h < 4 ? y : x;
            return ((h & 1) == 0 ? u : -u) + ((h & 2) == 0 ? v : -v);
        }
    }

    // ──────────────────────────────────────────────────────────────
    // 笔刷预览
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 笔刷预览: 在视口中实时渲染笔刷轮廓和影响范围。
    /// </summary>
    public sealed class TerrainBrushPreview
    {
        /// <summary>是否显示预览。</summary>
        public bool Visible { get; set; } = true;

        /// <summary>预览世界位置。</summary>
        public Vector3 WorldPosition { get; set; }
        /// <summary>是否在地面以下 (不显示)。</summary>
        public bool IsValid { get; private set; }

        /// <summary>笔刷引用。</summary>
        private readonly TerrainBrush _brush;

        /// <summary>预览线段 (用于渲染叠加层)。</summary>
        public List<(Vector3 A, Vector3 B)> BuildPreviewLines(float terrainHeight)
        {
            var lines = new List<(Vector3, Vector3)>();
            if (!Visible || !IsValid) return lines;

            float r = _brush.Radius;
            int segments = 64;
            float y = terrainHeight + 0.05f;

            for (int i = 0; i < segments; i++)
            {
                float angle1 = (float)i / segments * MathF.PI * 2f;
                float angle2 = (float)(i + 1) / segments * MathF.PI * 2f;
                Vector3 p1 = new(WorldPosition.X + MathF.Cos(angle1) * r, y,
                                  WorldPosition.Z + MathF.Sin(angle1) * r);
                Vector3 p2 = new(WorldPosition.X + MathF.Cos(angle2) * r, y,
                                  WorldPosition.Z + MathF.Sin(angle2) * r);
                lines.Add((p1, p2));
            }

            // 十字准线
            lines.Add((new Vector3(WorldPosition.X - r, y, WorldPosition.Z),
                        new Vector3(WorldPosition.X + r, y, WorldPosition.Z)));
            lines.Add((new Vector3(WorldPosition.X, y, WorldPosition.Z - r),
                        new Vector3(WorldPosition.X, y, WorldPosition.Z + r)));

            return lines;
        }

        public TerrainBrushPreview(TerrainBrush brush)
        {
            _brush = brush ?? throw new ArgumentNullException(nameof(brush));
        }

        /// <summary>更新预览位置。</summary>
        public void UpdatePosition(Vector3 worldPos, bool isValid)
        {
            WorldPosition = worldPos;
            IsValid = isValid;
        }
    }

    // ──────────────────────────────────────────────────────────────
    // 地形 LOD (四叉树)
    // ──────────────────────────────────────────────────────────────

    /// <summary>地形 LOD 节点 (四叉树)。</summary>
    public sealed class TerrainLodNode
    {
        /// <summary>节点层级 (0 = 最高精度)。</summary>
        public int Level { get; }
        /// <summary>XZ 索引。</summary>
        public (int X, int Z) TileIndex { get; }
        /// <summary>包围盒 (世界空间)。</summary>
        public (Vector3 Min, Vector3 Max) Bounds { get; set; }
        /// <summary>当前 LOD 等级。</summary>
        public int CurrentLod { get; set; }
        /// <summary>子节点 (Level+1)。</summary>
        public TerrainLodNode[] Children { get; private set; }

        public TerrainLodNode(int level, (int, int) index)
        {
            Level = level;
            TileIndex = index;
        }

        /// <summary>细分节点 (创建 4 个子节点)。</summary>
        public void Subdivide()
        {
            if (Children != null) return;
            Children = new TerrainLodNode[4];
            int nextLevel = Level + 1;
            int x = TileIndex.X * 2;
            int z = TileIndex.Z * 2;
            Children[0] = new TerrainLodNode(nextLevel, (x, z));
            Children[1] = new TerrainLodNode(nextLevel, (x + 1, z));
            Children[2] = new TerrainLodNode(nextLevel, (x, z + 1));
            Children[3] = new TerrainLodNode(nextLevel, (x + 1, z + 1));
        }

        /// <summary>合并子节点。</summary>
        public void Merge()
        {
            Children = null;
        }

        /// <summary>是否为叶节点。</summary>
        public bool IsLeaf => Children == null;
    }

    // ──────────────────────────────────────────────────────────────
    // 地形导出
    // ──────────────────────────────────────────────────────────────

    /// <summary>地形导出格式。</summary>
    public enum TerrainExportFormat
    {
        /// <summary>PNG 16bit 灰度高度图。</summary>
        Png16Bit,
        /// <summary>RAW 16bit 高度图。</summary>
        Raw16Bit,
        /// <summary>RAW 32bit float 高度图。</summary>
        Raw32Bit,
        /// <summary>OBJ 网格。</summary>
        Obj
    }

    /// <summary>
    /// 地形导出器: 将地形数据导出为各种格式。
    /// </summary>
    public static class TerrainExporter
    {
        /// <summary>导出地形数据到文件。</summary>
        public static bool Export(TerrainData data, TerrainExportFormat format, string filePath)
        {
            if (data == null || string.IsNullOrWhiteSpace(filePath)) return false;

            try
            {
                switch (format)
                {
                    case TerrainExportFormat.Raw16Bit:
                        return ExportRaw16(data, filePath);
                    case TerrainExportFormat.Raw32Bit:
                        return ExportRaw32(data, filePath);
                    case TerrainExportFormat.Obj:
                        return ExportObj(data, filePath);
                    default:
                        // PNG 需要图像编码库, 此处记录为占位
                        System.Diagnostics.Debug.WriteLine(
                            $"[TerrainExporter] Export {format} to {filePath} (placeholder)");
                        return true;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[TerrainExporter] Export failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>导出 RAW 16bit 高度图。</summary>
        private static bool ExportRaw16(TerrainData data, string filePath)
        {
            int count = data.Width * data.Height;
            var buffer = new ushort[count];
            for (int i = 0; i < count; i++)
                buffer[i] = (ushort)(data.Heightmap[i] * 65535f);

            var bytes = new byte[count * 2];
            for (int i = 0; i < count; i++)
            {
                bytes[i * 2] = (byte)(buffer[i] & 0xFF);
                bytes[i * 2 + 1] = (byte)((buffer[i] >> 8) & 0xFF);
            }

            using var fs = System.IO.File.Create(filePath);
            fs.Write(bytes, 0, bytes.Length);
            return true;
        }

        /// <summary>导出 RAW 32bit float 高度图。</summary>
        private static bool ExportRaw32(TerrainData data, string filePath)
        {
            int count = data.Width * data.Height;
            var bytes = new byte[count * 4];
            System.Buffer.BlockCopy(data.Heightmap, 0, bytes, 0, bytes.Length);

            using var fs = System.IO.File.Create(filePath);
            fs.Write(bytes, 0, bytes.Length);
            return true;
        }

        /// <summary>导出 OBJ 网格文件。</summary>
        private static bool ExportObj(TerrainData data, string filePath)
        {
            using var writer = new System.IO.StreamWriter(filePath);
            float stepX = data.WorldSizeX / Math.Max(1, data.Width - 1);
            float stepZ = data.WorldSizeZ / Math.Max(1, data.Height - 1);
            float offsetX = -data.WorldSizeX * 0.5f;
            float offsetZ = -data.WorldSizeZ * 0.5f;

            writer.WriteLine("# ShanyuEngine Terrain Export");
            writer.WriteLine($"# Size: {data.Width}x{data.Height}, World: {data.WorldSizeX}x{data.WorldSizeZ}");

            // 顶点
            for (int y = 0; y < data.Height; y++)
            {
                for (int x = 0; x < data.Width; x++)
                {
                    float wx = offsetX + x * stepX;
                    float wz = offsetZ + y * stepZ;
                    float wy = data.GetWorldHeight(x, y);
                    writer.WriteLine($"v {wx:F6} {wy:F6} {wz:F6}");
                }
            }

            // 法线
            for (int y = 0; y < data.Height; y++)
            {
                for (int x = 0; x < data.Width; x++)
                {
                    var n = data.GetNormal(x, y);
                    writer.WriteLine($"vn {n.X:F6} {n.Y:F6} {n.Z:F6}");
                }
            }

            // 面 (每四边形两个三角形)
            for (int y = 0; y < data.Height - 1; y++)
            {
                for (int x = 0; x < data.Width - 1; x++)
                {
                    if (data.IsHole(x, y)) continue;
                    int i00 = y * data.Width + x + 1;
                    int i10 = y * data.Width + (x + 1) + 1;
                    int i01 = (y + 1) * data.Width + x + 1;
                    int i11 = (y + 1) * data.Width + (x + 1) + 1;
                    writer.WriteLine($"f {i00}//{i00} {i10}//{i10} {i11}//{i11}");
                    writer.WriteLine($"f {i00}//{i00} {i11}//{i11} {i01}//{i01}");
                }
            }

            return true;
        }
    }
}
