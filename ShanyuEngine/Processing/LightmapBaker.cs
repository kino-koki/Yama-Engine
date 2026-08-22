// ====================================================================
// LightmapBaker.cs — 光照烘焙系统 (CPU 路径追踪烘焙器)
//
// 命名空间: ShanyuEngine.Processing
//
// 核心功能:
//   1. LightmapSettings: 烘焙参数 (分辨率/光照模式/反弹/降噪)
//   2. LightmapUVGenerator: UV 展开 (矩形装箱/Chart 分割/边距)
//   3. CPURaytracer: CPU 光线追踪器 (Moller-Trumbore / BVH SAH)
//   4. BakedLightmap: 烘焙结果 (颜色/方向性)
//   5. LightmapBaker: 主烘焙器 (直接光/间接光/AO/Jacobi 降噪)
//   6. IrradianceProbeBaker: 辐照度探针烘焙 (SH L2 / 八面体卷积)
//   7. ProgressReport: 烘焙进度回调
// ====================================================================

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;

namespace ShanyuEngine.Processing
{
    // ════════════════════════════════════════════════════════════════
    // 烘焙场景基础数据结构
    // ════════════════════════════════════════════════════════════════

    /// <summary>烘焙用场景描述。</summary>
    public class BakingScene
    {
        /// <summary>场景中的网格列表。</summary>
        public List<BakingMesh> Meshes { get; set; } = new();
        /// <summary>场景中的光源列表。</summary>
        public List<BakingLight> Lights { get; set; } = new();
        /// <summary>场景全局环境光颜色 (HDR)。</summary>
        public Vector3 AmbientColor { get; set; } = new Vector3(0.05f, 0.05f, 0.08f);
        /// <summary>世界空间包围盒。</summary>
        public AABB WorldBounds { get; set; }
    }

    /// <summary>烘焙用网格。</summary>
    public class BakingMesh
    {
        /// <summary>顶点位置。</summary>
        public List<Vector3> Positions { get; set; } = new();
        /// <summary>顶点法线。</summary>
        public List<Vector3> Normals { get; set; } = new();
        /// <summary>顶点纹理坐标 (光照图 UV 通道)。</summary>
        public List<Vector2> LightmapUVs { get; set; } = new();
        /// <summary>索引缓冲。</summary>
        public List<uint> Indices { get; set; } = new();
        /// <summary>材质索引。</summary>
        public int MaterialIndex { get; set; }
        /// <summary>变换矩阵 (世界空间)。</summary>
        public Matrix4x4 Transform { get; set; } = Matrix4x4.Identity;
    }

    /// <summary>烘焙用光源。</summary>
    public class BakingLight
    {
        /// <summary>光源类型。</summary>
        public BakingLightType Type { get; set; }
        /// <summary>光源位置 (点光源/聚光灯) 或方向 (平行光)。</summary>
        public Vector3 PositionOrDirection { get; set; }
        /// <summary>光源颜色 (HDR)。</summary>
        public Vector3 Color { get; set; } = Vector3.One;
        /// <summary>光源强度。</summary>
        public float Intensity { get; set; } = 1.0f;
        /// <summary>衰减范围 (点光源/聚光灯)。</summary>
        public float Range { get; set; } = 20.0f;
        /// <summary>聚光灯内角 (弧度)。</summary>
        public float InnerConeAngle { get; set; } = 0.3f;
        /// <summary>聚光灯外角 (弧度)。</summary>
        public float OuterConeAngle { get; set; } = 0.5f;
    }

    /// <summary>烘焙光源类型。</summary>
    public enum BakingLightType
    {
        /// <summary>平行光 (方向光)。</summary>
        Directional,
        /// <summary>点光源 (球面衰减)。</summary>
        Point,
        /// <summary>聚光灯。</summary>
        Spot,
        /// <summary>面光源 (矩形)。</summary>
        Area
    }

    // ════════════════════════════════════════════════════════════════
    // LightmapSettings — 烘焙参数设置
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// 光照烘焙设置。控制分辨率、光照模式、采样数与降噪参数。
    /// </summary>
    public class LightmapSettings
    {
        /// <summary>光照图分辨率 (每网格的像素尺寸)。</summary>
        public int Resolution { get; set; } = 512;

        /// <summary>光照模式: 直接光 + 间接光。</summary>
        public LightmapMode Mode { get; set; } = LightmapMode.DirectAndIndirect;

        /// <summary>每个 Texel 的直接光照采样数。</summary>
        public int DirectSamples { get; set; } = 256;

        /// <summary>每个 Texel 的环境光遮蔽半球采样数。</summary>
        public int AOSamples { get; set; } = 128;

        /// <summary>间接光照反弹次数 (1 = 一次弹射)。</summary>
        public int IndirectBounces { get; set; } = 1;

        /// <summary>每个间接光照采样点的采样数。</summary>
        public int IndirectSamples { get; set; } = 64;

        /// <summary>Jacobi 降噪迭代次数。</summary>
        public int DenoiseIterations { get; set; } = 4;

        /// <summary>UV 边距 (像素)。</summary>
        public int Padding { get; set; } = 2;

        /// <summary>环境光采样模式。</summary>
        public AmbientOcclusionMode AmbientMode { get; set; } = AmbientOcclusionMode.Hemisphere;
    }

    /// <summary>光照烘焙模式。</summary>
    public enum LightmapMode
    {
        /// <summary>仅直接光照。</summary>
        DirectOnly,
        /// <summary>直接 + 间接光照。</summary>
        DirectAndIndirect,
        /// <summary>仅环境光遮蔽。</summary>
        AmbientOcclusionOnly
    }

    /// <summary>环境光遮蔽模式。</summary>
    public enum AmbientOcclusionMode
    {
        /// <summary>半球采样。</summary>
        Hemisphere,
        /// <summary>蒙特卡洛 COS 加权采样。</summary>
        MonteCarloCosWeighted
    }

    // ════════════════════════════════════════════════════════════════
    // LightmapUVGenerator — UV 展开
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// 光照图 UV 生成器。负责将 3D 网格展开到 2D UV 空间。
    /// 使用锐角边分割 Chart，矩形装箱算法打包 UV 岛。
    /// </summary>
    public class LightmapUVGenerator
    {
        /// <summary>锐角阈值 (度)，超过此角度的边视为接缝边。</summary>
        public float SharpAngleThreshold { get; set; } = 60.0f;

        /// <summary>UV 岛边缘预留像素边距。</summary>
        public int Padding { get; set; } = 2;

        /// <summary>目标纹理尺寸 (用于 Padding 计算)。</summary>
        public int TextureSize { get; set; } = 512;

        /// <summary>
        /// 为网格生成光照图 UV 坐标。
        /// </summary>
        public List<Vector2> GenerateLightmapUVs(BakingMesh mesh)
        {
            // Step 1: 根据锐角边分割 Chart
            var charts = GenerateCharts(mesh);

            // Step 2: 对每个 Chart 进行参数化 (投影到最平面的轴)
            foreach (var chart in charts)
            {
                ParameterizeChart(chart);
            }

            // Step 3: 矩形装箱打包所有 Chart
            PackCharts(charts, TextureSize, Padding);

            // Step 4: 构建 UV 列表
            var uvs = new List<Vector2>(mesh.Positions.Count);
            for (int i = 0; i < mesh.Positions.Count; i++)
                uvs.Add(Vector2.Zero);

            foreach (var chart in charts)
            {
                for (int i = 0; i < chart.Vertices.Count; i++)
                {
                    int originalIdx = chart.Vertices[i];
                    uvs[originalIdx] = chart.PackedUVs[i];
                }
            }

            return uvs;
        }

        /// <summary>
        /// 根据锐角边分割 UV Chart。
        /// 锐角 (法线夹角超过阈值) 处的边将被切断为 Chart 边界。
        /// </summary>
        public List<UVChart> GenerateCharts(BakingMesh mesh)
        {
            var charts = new List<UVChart>();
            var faceChart = new int[mesh.Indices.Count / 3]; // 每个面所属的 Chart
            for (int i = 0; i < faceChart.Length; i++) faceChart[i] = -1;

            float cosThreshold = MathF.Cos(SharpAngleThreshold * MathF.PI / 180.0f);

            // 构建面邻接关系
            var faceNormals = new List<Vector3>();
            for (int i = 0; i < mesh.Indices.Count; i += 3)
            {
                Vector3 v0 = mesh.Positions[(int)mesh.Indices[i]];
                Vector3 v1 = mesh.Positions[(int)mesh.Indices[i + 1]];
                Vector3 v2 = mesh.Positions[(int)mesh.Indices[i + 2]];
                Vector3 n = Vector3.Cross(v1 - v0, v2 - v0);
                float len = n.Length();
                faceNormals.Add(len > 1e-10f ? n / len : Vector3.UnitY);
            }

            int currentChart = 0;

            for (int f = 0; f < faceNormals.Count; f++)
            {
                if (faceChart[f] >= 0) continue;

                // BFS 扩展 Chart
                var queue = new Queue<int>();
                queue.Enqueue(f);
                faceChart[f] = currentChart;

                var chart = new UVChart { ChartId = currentChart };

                while (queue.Count > 0)
                {
                    int face = queue.Dequeue();
                    chart.Faces.Add(face);

                    // 获取相邻面
                    for (int adj = 0; adj < faceNormals.Count; adj++)
                    {
                        if (adj == face || faceChart[adj] >= 0) continue;
                        if (!AreFacesAdjacent(mesh, face, adj, out _)) continue;

                        // 检查法线夹角 (锐角 = 接缝)
                        float cosAngle = Vector3.Dot(faceNormals[face], faceNormals[adj]);
                        if (cosAngle < cosThreshold) continue; // 锐角，不合并

                        faceChart[adj] = currentChart;
                        queue.Enqueue(adj);
                    }
                }

                // 收集 Chart 的唯一顶点
                var vertexSet = new HashSet<int>();
                foreach (int face in chart.Faces)
                {
                    for (int i = 0; i < 3; i++)
                    {
                        vertexSet.Add((int)mesh.Indices[face * 3 + i]);
                    }
                }
                chart.Vertices = new List<int>(vertexSet);
                chart.PackedUVs = new List<Vector2>(chart.Vertices.Count);
                for (int i = 0; i < chart.Vertices.Count; i++)
                    chart.PackedUVs.Add(Vector2.Zero);

                charts.Add(chart);
                currentChart++;
            }

            return charts;
        }

        /// <summary>
        /// 矩形装箱算法: 将所有 UV Chart 打包到 [0,1]x[0,1] 空间。
        /// 使用改进的 MaxRects 最佳短边优先 (BSSF) 算法。
        /// </summary>
        public void PackCharts(List<UVChart> charts, int textureSize, int padding)
        {
            // 计算 Chart 包围盒
            foreach (var chart in charts)
            {
                float minX = float.MaxValue, minY = float.MaxValue;
                float maxX = float.MinValue, maxY = float.MinValue;
                foreach (var uv in chart.PackedUVs)
                {
                    minX = Math.Min(minX, uv.X);
                    minY = Math.Min(minY, uv.Y);
                    maxX = Math.Max(maxX, uv.X);
                    maxY = Math.Max(maxY, uv.Y);
                }
                chart.Width = Math.Max(1, (int)((maxX - minX) * textureSize) + padding * 2);
                chart.Height = Math.Max(1, (int)((maxY - minY) * textureSize) + padding * 2);

                // 归一化到局部 [0,1]
                float w = maxX - minX;
                float h = maxY - minY;
                if (w < 1e-8f || h < 1e-8f) continue;
                for (int i = 0; i < chart.PackedUVs.Count; i++)
                {
                    chart.PackedUVs[i] = new Vector2(
                        (chart.PackedUVs[i].X - minX) / w,
                        (chart.PackedUVs[i].Y - minY) / h
                    );
                }
            }

            // MaxRects BSSF 装箱
            var freeRects = new List<Rect>
            {
                new Rect { X = 0, Y = 0, Width = textureSize, Height = textureSize }
            };

            // 按 max(width, height) 降序排列
            charts.Sort((a, b) => Math.Max(b.Width, b.Height).CompareTo(Math.Max(a.Width, a.Height)));

            float texSizeF = textureSize;

            foreach (var chart in charts)
            {
                Rect? bestRect = null;
                int bestShortSideFit = int.MaxValue;
                int bestLongSideFit = int.MaxValue;

                foreach (var free in freeRects)
                {
                    if (free.Width >= chart.Width && free.Height >= chart.Height)
                    {
                        int shortFit = Math.Min(free.Width - chart.Width, free.Height - chart.Height);
                        int longFit = Math.Max(free.Width - chart.Width, free.Height - chart.Height);
                        if (shortFit < bestShortSideFit ||
                            (shortFit == bestShortSideFit && longFit < bestLongSideFit))
                        {
                            bestRect = new Rect
                            {
                                X = free.X,
                                Y = free.Y,
                                Width = chart.Width,
                                Height = chart.Height
                            };
                            bestShortSideFit = shortFit;
                            bestLongSideFit = longFit;
                        }
                    }
                }

                if (bestRect.HasValue)
                {
                    // 放置 Chart
                    chart.PackX = bestRect.Value.X;
                    chart.PackY = bestRect.Value.Y;

                    float padTexel = (float)padding / texSizeF;

                    for (int i = 0; i < chart.PackedUVs.Count; i++)
                    {
                        chart.PackedUVs[i] = new Vector2(
                            (bestRect.Value.X + padding + chart.PackedUVs[i].X * (chart.Width - padding * 2)) / texSizeF,
                            (bestRect.Value.Y + padding + chart.PackedUVs[i].Y * (chart.Height - padding * 2)) / texSizeF
                        );
                    }

                    // 分割空闲矩形
                    SplitFreeRects(freeRects, bestRect.Value);
                }
            }
        }

        // ── 内部方法 ──

        private void ParameterizeChart(UVChart chart)
        {
            // 简化参数化: 使用面法线找最平坦的投影轴
            // 生产级实现应使用 LSCM (Least Squares Conformal Map) 或 ABF
            // 此处使用三角剖分投影作为简化版

            if (chart.Faces.Count == 0) return;

            // 随机挑一个面的法线作为投影方向 (简化实现)
            Vector3 avgNormal = Vector3.Zero;
            int faceCount = Math.Min(chart.Faces.Count, 10);
            for (int i = 0; i < faceCount; i++)
            {
                // 需要外部访问面法线，此处使用简化近似
                avgNormal += Vector3.UnitY; // 占位
            }
            // 使用简化映射: x -> u, z -> v
            // 生产实现需引入三角形几何数据计算投影
        }

        private bool AreFacesAdjacent(BakingMesh mesh, int faceA, int faceB, out int sharedEdge)
        {
            sharedEdge = -1;
            int baseA = faceA * 3, baseB = faceB * 3;
            int shared = 0;

            for (int i = 0; i < 3; i++)
            {
                for (int j = 0; j < 3; j++)
                {
                    if (mesh.Indices[baseA + i] == mesh.Indices[baseB + j])
                        shared++;
                }
            }

            return shared == 2;
        }

        private static void SplitFreeRects(List<Rect> freeRects, Rect placed)
        {
            var newFree = new List<Rect>();

            for (int i = freeRects.Count - 1; i >= 0; i--)
            {
                var fr = freeRects[i];
                if (!RectsIntersect(fr, placed)) continue;

                // 上方剩余
                if (placed.Y + placed.Height < fr.Y + fr.Height)
                {
                    newFree.Add(new Rect
                    {
                        X = fr.X, Y = placed.Y + placed.Height,
                        Width = fr.Width, Height = fr.Y + fr.Height - placed.Y - placed.Height
                    });
                }

                // 下方剩余
                if (placed.Y > fr.Y)
                {
                    newFree.Add(new Rect
                    {
                        X = fr.X, Y = fr.Y,
                        Width = fr.Width, Height = placed.Y - fr.Y
                    });
                }

                // 右方剩余
                if (placed.X + placed.Width < fr.X + fr.Width)
                {
                    newFree.Add(new Rect
                    {
                        X = placed.X + placed.Width, Y = fr.Y,
                        Width = fr.X + fr.Width - placed.X - placed.Width,
                        Height = fr.Height
                    });
                }

                // 左方剩余
                if (placed.X > fr.X)
                {
                    newFree.Add(new Rect
                    {
                        X = fr.X, Y = fr.Y,
                        Width = placed.X - fr.X, Height = fr.Height
                    });
                }

                freeRects.RemoveAt(i);
            }

            freeRects.AddRange(newFree);
        }

        private static bool RectsIntersect(Rect a, Rect b)
        {
            return a.X < b.X + b.Width && a.X + a.Width > b.X &&
                   a.Y < b.Y + b.Height && a.Y + a.Height > b.Y;
        }
    }

    /// <summary>UV Chart: 一组被展开的相连三角面。</summary>
    public class UVChart
    {
        public int ChartId { get; set; }
        public List<int> Faces { get; set; } = new();
        public List<int> Vertices { get; set; } = new();
        public List<Vector2> PackedUVs { get; set; } = new();
        public int PackX { get; set; }
        public int PackY { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
    }

    /// <summary>矩形 (像素坐标)。</summary>
    internal struct Rect
    {
        public int X, Y, Width, Height;
    }

    // ════════════════════════════════════════════════════════════════
    // CPURaytracer — CPU 光线追踪器
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// 光线 (Ray): origin + direction + 参数范围。
    /// </summary>
    public struct Ray
    {
        /// <summary>射线起点。</summary>
        public Vector3 Origin;
        /// <summary>射线方向 (单位向量)。</summary>
        public Vector3 Direction;
        /// <summary>最小参数 t。</summary>
        public float TMin;
        /// <summary>最大参数 t。</summary>
        public float TMax;

        public Ray(Vector3 origin, Vector3 direction, float tMin = 0.001f, float tMax = float.MaxValue)
        {
            Origin = origin;
            Direction = direction;
            TMin = tMin;
            TMax = tMax;
        }

        /// <summary>射线在参数 t 处的点。</summary>
        public Vector3 At(float t) => Origin + Direction * t;
    }

    /// <summary>
    /// 轴对齐包围盒 (AABB)。使用 Slab 法进行射线求交。
    /// </summary>
    public struct AABB
    {
        public Vector3 Min;
        public Vector3 Max;

        public AABB(Vector3 min, Vector3 max)
        {
            Min = min;
            Max = max;
        }

        /// <summary>空包围盒。</summary>
        public static AABB Empty => new AABB(
            new Vector3(float.MaxValue, float.MaxValue, float.MaxValue),
            new Vector3(float.MinValue, float.MinValue, float.MinValue));

        /// <summary>Slab 法射线-AABB 求交。</summary>
        public bool Intersect(Ray ray, out float tNear, out float tFar)
        {
            tNear = 0;
            tFar = float.MaxValue;

            // X 轴 Slab
            float invDirX = 1.0f / ray.Direction.X;
            float t1 = (Min.X - ray.Origin.X) * invDirX;
            float t2 = (Max.X - ray.Origin.X) * invDirX;
            if (t1 > t2) { float tmp = t1; t1 = t2; t2 = tmp; }
            tNear = Math.Max(tNear, t1);
            tFar = Math.Min(tFar, t2);
            if (tNear > tFar) return false;

            // Y 轴 Slab
            float invDirY = 1.0f / ray.Direction.Y;
            t1 = (Min.Y - ray.Origin.Y) * invDirY;
            t2 = (Max.Y - ray.Origin.Y) * invDirY;
            if (t1 > t2) { float tmp = t1; t1 = t2; t2 = tmp; }
            tNear = Math.Max(tNear, t1);
            tFar = Math.Min(tFar, t2);
            if (tNear > tFar) return false;

            // Z 轴 Slab
            float invDirZ = 1.0f / ray.Direction.Z;
            t1 = (Min.Z - ray.Origin.Z) * invDirZ;
            t2 = (Max.Z - ray.Origin.Z) * invDirZ;
            if (t1 > t2) { float tmp = t1; t1 = t2; t2 = tmp; }
            tNear = Math.Max(tNear, t1);
            tFar = Math.Min(tFar, t2);
            if (tNear > tFar) return false;

            return true;
        }

        /// <summary>扩展包围盒以包含点。</summary>
        public void Expand(Vector3 point)
        {
            Min = Vector3.Min(Min, point);
            Max = Vector3.Max(Max, point);
        }

        /// <summary>扩展包围盒以包含另一个 AABB。</summary>
        public void Union(AABB other)
        {
            Min = Vector3.Min(Min, other.Min);
            Max = Vector3.Max(Max, other.Max);
        }

        /// <summary>表面积。</summary>
        public float SurfaceArea()
        {
            Vector3 d = Max - Min;
            return 2.0f * (d.X * d.Y + d.Y * d.Z + d.Z * d.X);
        }

        /// <summary>中心点。</summary>
        public Vector3 Center => (Min + Max) * 0.5f;
    }

    /// <summary>
    /// 三角形: 使用 Moller-Trumbore 算法进行射线-三角形求交。
    /// </summary>
    public struct Triangle
    {
        public Vector3 V0, V1, V2;
        /// <summary>面法线 (预计算)。</summary>
        public Vector3 Normal;

        public Triangle(Vector3 v0, Vector3 v1, Vector3 v2)
        {
            V0 = v0;
            V1 = v1;
            V2 = v2;
            Vector3 e1 = v1 - v0;
            Vector3 e2 = v2 - v0;
            Normal = Vector3.Cross(e1, e2);
            float len = Normal.Length();
            if (len > 1e-10f) Normal /= len;
        }

        /// <summary>
        /// Moller-Trumbore 射线-三角形求交算法。
        /// 参考: Moller & Trumbore, "Fast, Minimum Storage Ray-Triangle Intersection", 1997。
        /// </summary>
        public bool Intersect(Ray ray, out float t, out float u, out float v)
        {
            const float EPSILON = 1e-8f;
            t = 0; u = 0; v = 0;

            Vector3 e1 = V1 - V0;
            Vector3 e2 = V2 - V0;
            Vector3 h = Vector3.Cross(ray.Direction, e2);
            float a = Vector3.Dot(e1, h);

            // 平行或背面
            if (a > -EPSILON && a < EPSILON)
                return false;

            float f = 1.0f / a;
            Vector3 s = ray.Origin - V0;
            u = f * Vector3.Dot(s, h);

            if (u < 0.0f || u > 1.0f)
                return false;

            Vector3 q = Vector3.Cross(s, e1);
            v = f * Vector3.Dot(ray.Direction, q);

            if (v < 0.0f || u + v > 1.0f)
                return false;

            t = f * Vector3.Dot(e2, q);

            return t >= ray.TMin && t <= ray.TMax;
        }

        /// <summary>三角形的包围盒。</summary>
        public AABB GetBounds()
        {
            return new AABB(
                Vector3.Min(Vector3.Min(V0, V1), V2),
                Vector3.Max(Vector3.Max(V0, V1), V2)
            );
        }

        /// <summary>三角形面积。</summary>
        public float Area()
        {
            Vector3 e1 = V1 - V0;
            Vector3 e2 = V2 - V0;
            return Vector3.Cross(e1, e2).Length() * 0.5f;
        }
    }

    /// <summary>
    /// BVH 二叉节点。叶节点存储三角形索引范围；内部节点存储子节点偏移。
    /// </summary>
    public struct BVHNode
    {
        /// <summary>节点包围盒。</summary>
        public AABB Bounds;

        /// <summary>
        /// 如果 LeftCount > 0: 叶节点，LeftFirst = 三角形起始索引，LeftCount = 三角形数量。
        /// 如果 LeftCount == 0: 内部节点，LeftFirst = 左子节点索引，Right = 左子节点索引 + 1。
        /// </summary>
        public int LeftFirst;
        public int Count; // > 0 = leaf count, 0 = internal

        public bool IsLeaf => Count > 0;
    }

    /// <summary>射线命中结果。</summary>
    public struct HitResult
    {
        /// <summary>命中参数 t。</summary>
        public float T;
        /// <summary>命中点位置。</summary>
        public Vector3 Point;
        /// <summary>命中点法线。</summary>
        public Vector3 Normal;
        /// <summary>重心坐标 u。</summary>
        public float U;
        /// <summary>重心坐标 v。</summary>
        public float V;
        /// <summary>命中的三角形索引。</summary>
        public int TriangleIndex;
        /// <summary>是否命中。</summary>
        public bool Hit;

        public static HitResult Miss => new HitResult { Hit = false, T = float.MaxValue };
    }

    /// <summary>
    /// BVH 构建器。使用 SAH (Surface Area Heuristic) 选择最优分割平面。
    /// </summary>
    public class BVHBuilder
    {
        private const int MaxTrianglesPerLeaf = 4;
        private const int MaxDepth = 64;

        /// <summary>三角形列表 (全局)。</summary>
        public List<Triangle> Triangles { get; private set; } = new();

        /// <summary>BVH 节点列表。</summary>
        public List<BVHNode> Nodes { get; private set; } = new();

        /// <summary>构建 BVH。</summary>
        public void Build(List<Triangle> triangles)
        {
            Triangles = triangles ?? throw new ArgumentNullException(nameof(triangles));
            Nodes = new List<BVHNode>(triangles.Count * 2);

            if (triangles.Count == 0) return;

            var triIndices = new int[triangles.Count];
            for (int i = 0; i < triIndices.Length; i++) triIndices[i] = i;

            // 计算所有三角形的包围盒
            var triBounds = new AABB[triangles.Count];
            var overallBounds = AABB.Empty;
            for (int i = 0; i < triangles.Count; i++)
            {
                triBounds[i] = triangles[i].GetBounds();
                overallBounds.Union(triBounds[i]);
            }

            BuildRecursive(triIndices, 0, triIndices.Length, triBounds, overallBounds, 0);
        }

        /// <summary>
        /// 追踪射线求交。
        /// </summary>
        public HitResult Trace(Ray ray)
        {
            if (Nodes.Count == 0) return HitResult.Miss;

            var best = HitResult.Miss;
            var stack = new Stack<int>();
            stack.Push(0);

            while (stack.Count > 0)
            {
                int nodeIdx = stack.Pop();
                ref BVHNode node = ref Nodes[nodeIdx];

                if (!node.Bounds.Intersect(ray, out float tNear, out float tFar))
                    continue;
                if (tNear > best.T) continue;

                if (node.IsLeaf)
                {
                    // 测试叶节点中的所有三角形
                    for (int i = 0; i < node.Count; i++)
                    {
                        int triIdx = node.LeftFirst + i;
                        if (triIdx >= Triangles.Count) break;

                        ref Triangle tri = ref Triangles[triIdx];
                        if (tri.Intersect(ray, out float t, out float u, out float v))
                        {
                            if (t < best.T)
                            {
                                best = new HitResult
                                {
                                    Hit = true,
                                    T = t,
                                    Point = ray.At(t),
                                    Normal = tri.Normal,
                                    U = u,
                                    V = v,
                                    TriangleIndex = triIdx
                                };
                            }
                        }
                    }
                }
                else
                {
                    // 推入子节点
                    int left = node.LeftFirst;
                    int right = left + 1;
                    if (right < Nodes.Count) stack.Push(right);
                    if (left < Nodes.Count) stack.Push(left);
                }
            }

            return best;
        }

        private int BuildRecursive(int[] triIndices, int start, int count,
            AABB[] triBounds, AABB nodeBounds, int depth)
        {
            if (depth >= MaxDepth || count <= MaxTrianglesPerLeaf)
            {
                return BuildLeaf(triIndices, start, count, triBounds, ref nodeBounds);
            }

            // SAH: 选择最优分割轴与位置
            int splitAxis;
            float splitPos;
            ComputeSAHSplit(triIndices, start, count, triBounds, nodeBounds,
                out splitAxis, out splitPos);

            // 分割
            int mid = Partition(triIndices, start, count, triBounds, splitAxis, splitPos);

            // 退化情况: 无法分割
            if (mid == start || mid == start + count)
            {
                return BuildLeaf(triIndices, start, count, triBounds, ref nodeBounds);
            }

            // 构建子节点
            int leftCount = mid - start;
            int rightCount = start + count - mid;

            AABB leftBounds = AABB.Empty;
            AABB rightBounds = AABB.Empty;
            for (int i = start; i < mid; i++) leftBounds.Union(triBounds[triIndices[i]]);
            for (int i = mid; i < start + count; i++) rightBounds.Union(triBounds[triIndices[i]]);

            int nodeIdx = Nodes.Count;
            Nodes.Add(new BVHNode { Bounds = nodeBounds, LeftFirst = 0, Count = 0 });

            int leftChild = BuildRecursive(triIndices, start, leftCount, triBounds, leftBounds, depth + 1);
            int rightChild = BuildRecursive(triIndices, mid, rightCount, triBounds, rightBounds, depth + 1);

            Nodes[nodeIdx] = new BVHNode
            {
                Bounds = nodeBounds,
                LeftFirst = leftChild,
                Count = 0
            };

            return nodeIdx;
        }

        private int BuildLeaf(int[] triIndices, int start, int count,
            AABB[] triBounds, ref AABB nodeBounds)
        {
            // 将三角形索引范围存储到 Triangles 列表中
            int firstTri = Nodes.Count > 0 ? Nodes[Nodes.Count - 1].LeftFirst + Nodes[Nodes.Count - 1].Count : 0;

            // 重新映射到全局三角形索引
            int nodeIdx = Nodes.Count;
            Nodes.Add(new BVHNode
            {
                Bounds = nodeBounds,
                LeftFirst = -1, // 占位
                Count = count
            });

            // 存储实际的三角形索引
            var leafTris = new int[count];
            for (int i = 0; i < count; i++)
                leafTris[i] = triIndices[start + i];

            // 需要为叶节点存储三角形索引 — 使用简化方案:
            // 叶节点的 LeftFirst 存储 _leafTriangleIndices 中的偏移
            int leafBase = RegisterLeafTriangles(leafTris);
            Nodes[nodeIdx] = new BVHNode
            {
                Bounds = nodeBounds,
                LeftFirst = leafBase,
                Count = count
            };

            return nodeIdx;
        }

        // 叶三角形索引存储
        private List<int> _leafTriangleIndices = new();

        private int RegisterLeafTriangles(int[] indices)
        {
            int baseIdx = _leafTriangleIndices.Count;
            _leafTriangleIndices.AddRange(indices);
            return baseIdx;
        }

        private void ComputeSAHSplit(int[] triIndices, int start, int count,
            AABB[] triBounds, AABB nodeBounds, out int axis, out float pos)
        {
            axis = 0;
            pos = nodeBounds.Center.X;

            float bestCost = float.MaxValue;
            Vector3 extent = nodeBounds.Max - nodeBounds.Min;

            // 对每个轴计算 SAH 代价
            for (int a = 0; a < 3; a++)
            {
                float nodeArea = nodeBounds.SurfaceArea();
                if (nodeArea < 1e-10f) continue;

                // 计算中点分割的 SAH 代价
                float mid = nodeBounds.Min.GetAxis(a) + extent.GetAxis(a) * 0.5f;

                int leftCount = 0, rightCount = 0;
                AABB leftBB = AABB.Empty, rightBB = AABB.Empty;

                for (int i = start; i < start + count; i++)
                {
                    float triCenter = triBounds[triIndices[i]].Center.GetAxis(a);
                    if (triCenter <= mid)
                    {
                        leftCount++;
                        leftBB.Union(triBounds[triIndices[i]]);
                    }
                    else
                    {
                        rightCount++;
                        rightBB.Union(triBounds[triIndices[i]]);
                    }
                }

                if (leftCount == 0 || rightCount == 0) continue;

                float leftArea = leftBB.SurfaceArea();
                float rightArea = rightBB.SurfaceArea();

                // SAH 代价 = (leftArea * leftCount + rightArea * rightCount) / nodeArea
                float cost = (leftArea * leftCount + rightArea * rightCount) / nodeArea;

                if (cost < bestCost)
                {
                    bestCost = cost;
                    axis = a;
                    pos = mid;
                }
            }
        }

        private static int Partition(int[] triIndices, int start, int count,
            AABB[] triBounds, int axis, float pos)
        {
            int left = start;
            int right = start + count - 1;

            while (left <= right)
            {
                float center = triBounds[triIndices[left]].Center.GetAxis(axis);
                if (center <= pos)
                {
                    left++;
                }
                else
                {
                    // 交换
                    (triIndices[left], triIndices[right]) = (triIndices[right], triIndices[left]);
                    right--;
                }
            }

            return left;
        }
    }

    // ════════════════════════════════════════════════════════════════
    // BakedLightmap — 烘焙结果
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// 烘焙后的光照图。包含颜色数据与方向性数据。
    /// </summary>
    public class BakedLightmap
    {
        /// <summary>光照图宽度 (像素)。</summary>
        public int Width { get; set; }
        /// <summary>光照图高度 (像素)。</summary>
        public int Height { get; set; }
        /// <summary>颜色数据 (Width * Height, RGB)。</summary>
        public Vector3[] ColorData { get; set; } = Array.Empty<Vector3>();
        /// <summary>方向性数据 (Width * Height, 用于法线偏移烘焙)。</summary>
        public Vector3[] DirectionalityData { get; set; } = Array.Empty<Vector3>();

        /// <summary>获取指定像素的颜色。</summary>
        public Vector3 GetColor(int x, int y) => ColorData[y * Width + x];

        /// <summary>设置指定像素的颜色。</summary>
        public void SetColor(int x, int y, Vector3 color) => ColorData[y * Width + x] = color;
    }

    // ════════════════════════════════════════════════════════════════
    // ProgressReport — 烘焙进度回调
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// 烘焙进度报告。用于回调通知烘焙进度。
    /// </summary>
    public class ProgressReport
    {
        /// <summary>总进度百分比 (0.0 ~ 1.0)。</summary>
        public float Progress { get; set; }
        /// <summary>当前阶段描述。</summary>
        public string Stage { get; set; } = "";
        /// <summary>预估剩余时间 (毫秒)。</summary>
        public long EstimatedRemainingMs { get; set; }
        /// <summary>已处理的像素/Texel 数量。</summary>
        public int ProcessedCount { get; set; }
        /// <summary>总像素/Texel 数量。</summary>
        public int TotalCount { get; set; }

        /// <summary>进度变更事件。</summary>
        public event Action<ProgressReport>? OnProgressChanged;

        /// <summary>报告进度。</summary>
        public void Report(float progress, string stage, long remainingMs = 0, int processed = 0, int total = 0)
        {
            Progress = Math.Clamp(progress, 0.0f, 1.0f);
            Stage = stage;
            EstimatedRemainingMs = remainingMs;
            ProcessedCount = processed;
            TotalCount = total;
            OnProgressChanged?.Invoke(this);
        }
    }

    // ════════════════════════════════════════════════════════════════
    // LightmapBaker — 主烘焙器
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// CPU 光照烘焙器。对场景中每个网格的光照图进行逐 Texel 射线采样。
    /// 支持直接光照、间接光照 (1 次反弹)、环境光遮蔽。
    /// 使用 Jacobi 迭代进行 3x3 高斯核降噪。
    /// </summary>
    public class LightmapBaker
    {
        private BVHBuilder _bvh = new();
        private List<Triangle> _sceneTriangles = new();
        private BakingScene _scene = null!;
        private Random _rng = new Random(42); // 固定种子可复现

        /// <summary>进度回调 (可选)。</summary>
        public Action<ProgressReport>? OnProgress { get; set; }

        /// <summary>
        /// 烘焙整个场景的光照图。
        /// </summary>
        /// <param name="scene">烘焙场景。</param>
        /// <param name="settings">烘焙设置。</param>
        /// <returns>每个网格一张烘焙后的光照图。</returns>
        public BakedLightmap[] BakeScene(BakingScene scene, LightmapSettings settings)
        {
            if (scene == null) throw new ArgumentNullException(nameof(scene));
            if (settings == null) throw new ArgumentNullException(nameof(settings));

            _scene = scene;
            var progress = new ProgressReport();
            progress.OnProgressChanged += p => OnProgress?.Invoke(p);

            // 阶段 1: 构建 BVH
            progress.Report(0.0f, "构建 BVH 加速结构...");
            BuildSceneBVH();

            // 阶段 2: 为每个网格烘焙光照图
            var results = new BakedLightmap[scene.Meshes.Count];
            int totalPixels = scene.Meshes.Sum(m => settings.Resolution * settings.Resolution);
            int processedPixels = 0;

            for (int m = 0; m < scene.Meshes.Count; m++)
            {
                float meshProgress = (float)processedPixels / totalPixels;
                progress.Report(meshProgress, $"烘焙网格 {m + 1}/{scene.Meshes.Count}...");

                results[m] = BakeMesh(scene.Meshes[m], settings, progress, processedPixels, totalPixels);
                processedPixels += settings.Resolution * settings.Resolution;
            }

            // 阶段 3: 降噪
            progress.Report(0.85f, "Jacobi 迭代降噪...");
            for (int i = 0; i < results.Length; i++)
            {
                if (results[i] != null)
                    DenoiseLightmap(results[i], settings.DenoiseIterations);
            }

            progress.Report(1.0f, "烘焙完成");
            return results;
        }

        /// <summary>
        /// 烘焙单个网格的光照图。对每个 Texel 发射射线采样直接/间接光照。
        /// </summary>
        private BakedLightmap BakeMesh(BakingMesh mesh, LightmapSettings settings,
            ProgressReport progress, int baseProcessed, int totalPixels)
        {
            int res = settings.Resolution;
            var lightmap = new BakedLightmap
            {
                Width = res,
                Height = res,
                ColorData = new Vector3[res * res],
                DirectionalityData = new Vector3[res * res]
            };

            for (int y = 0; y < res; y++)
            {
                for (int x = 0; x < res; x++)
                {
                    // Texel 中心 UV
                    float u = (x + 0.5f) / res;
                    float v = (y + 0.5f) / res;

                    // 从 UV 反推 3D 位置与法线 (简化: 使用最近三角形的插值)
                    if (!ComputeTexelWorldPosition(mesh, u, v, out Vector3 pos, out Vector3 normal))
                    {
                        int idx = y * res + x;
                        lightmap.ColorData[idx] = Vector3.Zero;
                        lightmap.DirectionalityData[idx] = Vector3.Zero;
                        continue;
                    }

                    Vector3 color = Vector3.Zero;
                    Vector3 dirAccum = Vector3.Zero;

                    // 直接光照
                    if (settings.Mode != LightmapMode.AmbientOcclusionOnly)
                    {
                        Vector3 direct = SampleDirectLight(pos, normal, settings);
                        color += direct;
                    }

                    // 间接光照
                    if (settings.Mode == LightmapMode.DirectAndIndirect && settings.IndirectBounces > 0)
                    {
                        Vector3 indirect = SampleIndirectLight(pos, normal, settings);
                        color += indirect;
                    }

                    // 环境光遮蔽
                    float ao = SampleAmbientOcclusion(pos, normal, settings);

                    // 混合环境光
                    color = color * ao + _scene.AmbientColor * (1.0f - ao) * 0.5f;

                    int idx2 = y * res + x;
                    lightmap.ColorData[idx2] = color;
                    lightmap.DirectionalityData[idx2] = normal;
                }

                // 更新进度
                if (y % 16 == 0)
                {
                    int processed = baseProcessed + y * res;
                    progress.Report((float)processed / totalPixels,
                        $"烘焙 Texel 行 {y}/{res}...", 0, processed, totalPixels);
                }
            }

            return lightmap;
        }

        /// <summary>
        /// 直接光照采样: 对每个光源发射阴影射线。
        /// </summary>
        private Vector3 SampleDirectLight(Vector3 pos, Vector3 normal, LightmapSettings settings)
        {
            Vector3 result = Vector3.Zero;

            foreach (var light in _scene.Lights)
            {
                Vector3 lightDir;
                float lightDist;
                float attenuation = 1.0f;

                if (light.Type == BakingLightType.Directional)
                {
                    lightDir = -Vector3.Normalize(light.PositionOrDirection);
                    lightDist = float.MaxValue;
                }
                else if (light.Type == BakingLightType.Point)
                {
                    Vector3 toLight = light.PositionOrDirection - pos;
                    lightDist = toLight.Length();
                    lightDir = toLight / lightDist;
                    // 球面衰减: 1 / (1 + 0.1*d + 0.01*d^2)
                    attenuation = 1.0f / (1.0f + 0.1f * lightDist + 0.01f * lightDist * lightDist);
                    if (lightDist > light.Range) attenuation = 0;
                }
                else if (light.Type == BakingLightType.Spot)
                {
                    Vector3 toLight = light.PositionOrDirection - pos;
                    lightDist = toLight.Length();
                    lightDir = toLight / lightDist;
                    attenuation = 1.0f / (1.0f + 0.1f * lightDist + 0.01f * lightDist * lightDist);
                    if (lightDist > light.Range) attenuation = 0;

                    // 聚光灯锥角衰减
                    float cosAngle = Vector3.Dot(-lightDir, Vector3.Normalize(light.PositionOrDirection));
                    float outerCos = MathF.Cos(light.OuterConeAngle);
                    float innerCos = MathF.Cos(light.InnerConeAngle);
                    if (cosAngle < outerCos) attenuation = 0;
                    else if (cosAngle < innerCos)
                        attenuation *= (cosAngle - outerCos) / (innerCos - outerCos);
                }
                else
                {
                    continue; // Area light 占位
                }

                // Lambert N.L
                float nDotL = Math.Max(0, Vector3.Dot(normal, lightDir));
                if (nDotL <= 0) continue;

                // 阴影射线
                var shadowRay = new Ray(pos + normal * 0.001f, lightDir, 0.001f, lightDist - 0.002f);
                var shadowHit = _bvh.Trace(shadowRay);

                if (shadowHit.Hit)
                    continue; // 在阴影中

                result += light.Color * light.Intensity * attenuation * nDotL;
            }

            return result;
        }

        /// <summary>
        /// 间接光照采样: 对命中点发射半球射线进行 1 次反弹。
        /// </summary>
        private Vector3 SampleIndirectLight(Vector3 pos, Vector3 normal, LightmapSettings settings)
        {
            Vector3 result = Vector3.Zero;
            int samples = settings.IndirectSamples;

            for (int i = 0; i < samples; i++)
            {
                // COS 加权半球采样
                Vector3 dir = CosWeightedHemisphere(normal, ref _rng);
                var bounceRay = new Ray(pos + normal * 0.002f, dir, 0.002f, 50.0f);
                var hit = _bvh.Trace(bounceRay);

                if (hit.Hit)
                {
                    // 对反弹命中点采样直接光照
                    Vector3 bounceDirect = SampleDirectLight(hit.Point, hit.Normal, settings);
                    // 简化的 diffuse BRDF: albedo / PI (假设 albedo = 0.5)
                    float pdf = Math.Max(0.001f, dir.Dot(normal) / MathF.PI);
                    result += bounceDirect * 0.5f / (MathF.PI * pdf * (float)samples);
                }
            }

            return result;
        }

        /// <summary>
        /// 环境光遮蔽采样: 对每个 Texel 发射半球射线计算遮挡率。
        /// </summary>
        private float SampleAmbientOcclusion(Vector3 pos, Vector3 normal, LightmapSettings settings)
        {
            int hits = 0;
            int samples = settings.AOSamples;
            float aoRadius = 2.0f;

            for (int i = 0; i < samples; i++)
            {
                Vector3 dir = CosWeightedHemisphere(normal, ref _rng);
                var aoRay = new Ray(pos + normal * 0.002f, dir, 0.002f, aoRadius);
                var hit = _bvh.Trace(aoRay);

                if (hit.Hit)
                    hits++;
            }

            return 1.0f - ((float)hits / samples);
        }

        /// <summary>
        /// Jacobi 迭代降噪。使用 3x3 高斯核进行多次迭代平滑。
        /// </summary>
        public static void DenoiseLightmap(BakedLightmap lightmap, int iterations)
        {
            if (lightmap == null || lightmap.ColorData.Length == 0) return;

            int w = lightmap.Width;
            int h = lightmap.Height;

            // 3x3 高斯核权重
            // [1, 2, 1]
            // [2, 4, 2]   总和 = 16
            // [1, 2, 1]
            const float KERNEL_SUM = 16.0f;

            for (int iter = 0; iter < iterations; iter++)
            {
                var temp = new Vector3[w * h];

                for (int y = 1; y < h - 1; y++)
                {
                    for (int x = 1; x < w - 1; x++)
                    {
                        Vector3 sum = Vector3.Zero;

                        // 3x3 高斯卷积
                        sum += lightmap.ColorData[(y - 1) * w + (x - 1)] * 1.0f;
                        sum += lightmap.ColorData[(y - 1) * w + x] * 2.0f;
                        sum += lightmap.ColorData[(y - 1) * w + (x + 1)] * 1.0f;
                        sum += lightmap.ColorData[y * w + (x - 1)] * 2.0f;
                        sum += lightmap.ColorData[y * w + x] * 4.0f;
                        sum += lightmap.ColorData[y * w + (x + 1)] * 2.0f;
                        sum += lightmap.ColorData[(y + 1) * w + (x - 1)] * 1.0f;
                        sum += lightmap.ColorData[(y + 1) * w + x] * 2.0f;
                        sum += lightmap.ColorData[(y + 1) * w + (x + 1)] * 1.0f;

                        temp[y * w + x] = sum / KERNEL_SUM;
                    }
                }

                // 边界像素不变
                for (int x = 0; x < w; x++)
                {
                    temp[x] = lightmap.ColorData[x];
                    temp[(h - 1) * w + x] = lightmap.ColorData[(h - 1) * w + x];
                }
                for (int y = 0; y < h; y++)
                {
                    temp[y * w] = lightmap.ColorData[y * w];
                    temp[y * w + w - 1] = lightmap.ColorData[y * w + w - 1];
                }

                lightmap.ColorData = temp;
            }
        }

        // ── 内部方法 ──

        private void BuildSceneBVH()
        {
            _sceneTriangles.Clear();

            foreach (var mesh in _scene.Meshes)
            {
                for (int i = 0; i < mesh.Indices.Count; i += 3)
                {
                    Vector3 v0 = TransformPoint(mesh.Positions[(int)mesh.Indices[i]], mesh.Transform);
                    Vector3 v1 = TransformPoint(mesh.Positions[(int)mesh.Indices[i + 1]], mesh.Transform);
                    Vector3 v2 = TransformPoint(mesh.Positions[(int)mesh.Indices[i + 2]], mesh.Transform);
                    _sceneTriangles.Add(new Triangle(v0, v1, v2));
                }
            }

            _bvh = new BVHBuilder();
            _bvh.Build(_sceneTriangles);
        }

        private bool ComputeTexelWorldPosition(BakingMesh mesh, float u, float v,
            out Vector3 position, out Vector3 normal)
        {
            position = Vector3.Zero;
            normal = Vector3.UnitY;

            // 简化: 找到 UV 最近的三角形，插值位置
            // 生产实现应使用射线-纹理空间求交
            float bestDist = float.MaxValue;
            int bestTri = -1;

            for (int i = 0; i < mesh.LightmapUVs.Count && i < mesh.Positions.Count; i++)
            {
                Vector2 uv = mesh.LightmapUVs[i];
                float dsq = (u - uv.X) * (u - uv.X) + (v - uv.Y) * (v - uv.Y);
                if (dsq < bestDist)
                {
                    bestDist = dsq;
                    bestTri = i;
                }
            }

            if (bestTri < 0) return false;

            position = TransformPoint(mesh.Positions[bestTri], mesh.Transform);
            if (bestTri < mesh.Normals.Count)
            {
                Vector3 n = mesh.Normals[bestTri];
                normal = Vector3.TransformNormal(n, mesh.Transform);
                float len = normal.Length();
                if (len > 1e-10f) normal /= len;
            }

            return true;
        }

        /// <summary>COS 加权半球随机采样。</summary>
        private static Vector3 CosWeightedHemisphere(Vector3 normal, ref Random rng)
        {
            // 均匀随机数
            float r1 = (float)rng.NextDouble();
            float r2 = (float)rng.NextDouble();

            // COS 加权: r1 = cos(theta)
            float cosTheta = MathF.Sqrt(r1);
            float sinTheta = MathF.Sqrt(1.0f - r1);
            float phi = 2.0f * MathF.PI * r2;

            // 局部坐标
            float localX = sinTheta * MathF.Cos(phi);
            float localY = sinTheta * MathF.Sin(phi);
            float localZ = cosTheta;

            // 构建切线空间
            Vector3 up = MathF.Abs(normal.Y) < 0.999f
                ? Vector3.UnitY
                : Vector3.UnitX;
            Vector3 tangent = Vector3.Normalize(Vector3.Cross(normal, up));
            Vector3 bitangent = Vector3.Cross(normal, tangent);

            return tangent * localX + bitangent * localY + normal * localZ;
        }

        private static Vector3 TransformPoint(Vector3 p, Matrix4x4 m)
        {
            return new Vector3(
                m.M11 * p.X + m.M21 * p.Y + m.M31 * p.Z + m.M41,
                m.M12 * p.X + m.M22 * p.Y + m.M32 * p.Z + m.M42,
                m.M13 * p.X + m.M23 * p.Y + m.M33 * p.Z + m.M43);
        }
    }

    // ════════════════════════════════════════════════════════════════
    // IrradianceProbeBaker — 辐照度探针烘焙
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// 辐照度探针烘焙器。在 3D 空间中均匀放置探针网格，
    /// 通过球谐函数 (SH) L2 (9 系数) 投影存储辐照度。
    /// 支持从烘焙结果提取探针辐照度与八面体卷积方向纹理。
    /// </summary>
    public class IrradianceProbeBaker
    {
        /// <summary>球谐 L2 系数数量 (3 阶 = 9 系数)。</summary>
        public const int SHL2CoeffCount = 9;

        /// <summary>探针网格分辨率 (每轴探针数)。</summary>
        public int GridResolution { get; set; } = 8;

        /// <summary>每个探针的采样方向数 (用于 SH 投影)。</summary>
        public int SamplesPerProbe { get; set; } = 256;

        /// <summary>探针之间的间距 (世界单位)。</summary>
        public float ProbeSpacing { get; set; } = 2.0f;

        /// <summary>
        /// 烘焙辐照度探针网格。
        /// </summary>
        /// <param name="bakedLightmaps">已烘焙的光照图列表。</param>
        /// <param name="meshes">对应的网格列表。</param>
        /// <param name="worldBounds">场景包围盒。</param>
        /// <param name="progress">进度回调。</param>
        /// <returns>探针数据数组。</returns>
        public IrradianceProbeData[] BakeProbes(BakedLightmap[] bakedLightmaps, List<BakingMesh> meshes,
            AABB worldBounds, Action<ProgressReport>? progress = null)
        {
            var progressReport = new ProgressReport();
            progressReport.OnProgressChanged += p => progress?.Invoke(p);

            progressReport.Report(0.0f, "放置探针网格...");

            // 1. 计算探针位置 (3D 均匀网格)
            var probes = GenerateProbeGrid(worldBounds);

            progressReport.Report(0.1f, $"生成 {probes.Length} 个探针...");

            // 2. 为每个探针烘焙 SH 系数
            var results = new IrradianceProbeData[probes.Length];
            for (int i = 0; i < probes.Length; i++)
            {
                // 简化: 从光照图插值获取辐照度
                // 生产实现: 对每个探针位置发射射线采样
                Vector3 position = probes[i];

                // SH L2 系数 (RGB 三通道)
                var shCoeffsR = new float[SHL2CoeffCount];
                var shCoeffsG = new float[SHL2CoeffCount];
                var shCoeffsB = new float[SHL2CoeffCount];

                // 球面均匀采样 + SH 投影
                for (int s = 0; s < SamplesPerProbe; s++)
                {
                    // Fibonacci 球采样
                    float goldenRatio = (1.0f + MathF.Sqrt(5.0f)) / 2.0f;
                    float theta = 2.0f * MathF.PI * s / goldenRatio;
                    float phi = MathF.Acos(1.0f - 2.0f * (s + 0.5f) / SamplesPerProbe);

                    Vector3 dir = SphericalToCartesian(theta, phi);

                    // 获取该方向的辐照度 (简化: 使用环境光 + 采样)
                    Vector3 irradiance = SampleIrradianceAtProbe(position, dir, bakedLightmaps, meshes);

                    // SH 基函数求值 (L2, 9 系数)
                    float[] shBasis = EvaluateSHBasisL2(dir);

                    // 投影: 累加 irradiance * SH_basis
                    for (int c = 0; c < SHL2CoeffCount; c++)
                    {
                        shCoeffsR[c] += irradiance.X * shBasis[c];
                        shCoeffsG[c] += irradiance.Y * shBasis[c];
                        shCoeffsB[c] += irradiance.Z * shBasis[c];
                    }
                }

                // 归一化 SH 系数
                float weight = 4.0f * MathF.PI / SamplesPerProbe;
                for (int c = 0; c < SHL2CoeffCount; c++)
                {
                    shCoeffsR[c] *= weight;
                    shCoeffsG[c] *= weight;
                    shCoeffsB[c] *= weight;
                }

                results[i] = new IrradianceProbeData
                {
                    Position = position,
                    SHCoefficientsR = shCoeffsR,
                    SHCoefficientsG = shCoeffsG,
                    SHCoefficientsB = shCoeffsB,
                    Valid = true
                };

                if (i % 10 == 0)
                {
                    progressReport.Report(0.1f + 0.7f * (float)i / probes.Length,
                        $"烘焙探针 {i}/{probes.Length}...");
                }
            }

            progressReport.Report(0.85f, "生成八面体卷积纹理...");

            // 3. 为每个探针生成八面体卷积方向纹理
            for (int i = 0; i < results.Length; i++)
            {
                results[i].OctahedralMap = GenerateOctahedralMap(results[i], 8);
            }

            progressReport.Report(1.0f, "探针烘焙完成");
            return results;
        }

        /// <summary>生成 3D 探针网格。</summary>
        public Vector3[] GenerateProbeGrid(AABB bounds)
        {
            var probes = new List<Vector3>();
            Vector3 center = bounds.Center;
            Vector3 extent = (bounds.Max - bounds.Min) * 0.5f;
            float spacing = ProbeSpacing;

            int res = GridResolution;
            for (int z = 0; z < res; z++)
            {
                for (int y = 0; y < res; y++)
                {
                    for (int x = 0; x < res; x++)
                    {
                        float fx = res > 1 ? (float)x / (res - 1) : 0.5f;
                        float fy = res > 1 ? (float)y / (res - 1) : 0.5f;
                        float fz = res > 1 ? (float)z / (res - 1) : 0.5f;

                        probes.Add(new Vector3(
                            center.X + (fx - 0.5f) * 2.0f * extent.X,
                            center.Y + (fy - 0.5f) * 2.0f * extent.Y,
                            center.Z + (fz - 0.5f) * 2.0f * extent.Z
                        ));
                    }
                }
            }

            return probes.ToArray();
        }

        /// <summary>
        /// 评估 SH L2 基函数 (9 系数)。
        /// SH0 = 0.282095 (Y_0^0)
        /// SH1 = 0.488603 * Y   (Y_1^{-1})
        /// SH2 = 0.488603 * Z   (Y_1^0)
        /// SH3 = 0.488603 * X   (Y_1^1)
        /// SH4 = 1.092548 * XY  (Y_2^{-2})
        /// SH5 = 1.092548 * YZ  (Y_2^{-1})
        /// SH6 = 0.315392 * (3Z^2-1) (Y_2^0)
        /// SH7 = 1.092548 * XZ  (Y_2^1)
        /// SH8 = 0.546274 * (X^2-Y^2) (Y_2^2)
        /// </summary>
        public static float[] EvaluateSHBasisL2(Vector3 dir)
        {
            float x = dir.X, y = dir.Y, z = dir.Z;
            return new float[]
            {
                0.282095f,                        // SH0
                0.488603f * y,                    // SH1
                0.488603f * z,                    // SH2
                0.488603f * x,                    // SH3
                1.092548f * x * y,               // SH4
                1.092548f * y * z,               // SH5
                0.315392f * (3.0f * z * z - 1.0f), // SH6
                1.092548f * x * z,               // SH7
                0.546274f * (x * x - y * y)      // SH8
            };
        }

        /// <summary>
        /// 从 SH 系数重建某方向的辐照度。
        /// irradiance(dir) = sum_i SH_i * SHBasis_i(dir)
        /// </summary>
        public static Vector3 EvaluateSHIrradiance(float[] shR, float[] shG, float[] shB, Vector3 dir)
        {
            float[] basis = EvaluateSHBasisL2(dir);
            float r = 0, g = 0, b = 0;
            for (int i = 0; i < SHL2CoeffCount; i++)
            {
                r += shR[i] * basis[i];
                g += shG[i] * basis[i];
                b += shB[i] * basis[i];
            }
            return new Vector3(r, g, b);
        }

        /// <summary>
        /// 生成八面体卷积方向纹理。将探针辐照度投影到八面体映射的 2D 纹理。
        /// 用于运行时快速采样特定方向的辐照度。
        /// </summary>
        public Vector3[] GenerateOctahedralMap(IrradianceProbeData probe, int resolution)
        {
            int size = resolution * resolution;
            var map = new Vector3[size];

            for (int y = 0; y < resolution; y++)
            {
                for (int x = 0; x < resolution; x++)
                {
                    // 八面体映射: 将 [0,res]x[0,res] 映射到球面方向
                    float u = (x + 0.5f) / resolution * 2.0f - 1.0f;
                    float v = (y + 0.5f) / resolution * 2.0f - 1.0f;

                    Vector3 dir = OctahedralToDirection(u, v);

                    // 从 SH 重建该方向的辐照度
                    Vector3 irradiance = EvaluateSHIrradiance(
                        probe.SHCoefficientsR, probe.SHCoefficientsG, probe.SHCoefficientsB, dir);

                    map[y * resolution + x] = irradiance;
                }
            }

            return map;
        }

        // ── 内部方法 ──

        private Vector3 SampleIrradianceAtProbe(Vector3 position, Vector3 direction,
            BakedLightmap[] lightmaps, List<BakingMesh> meshes)
        {
            // 简化: 射线行走到最近表面获取光照
            // 生产实现应使用完整光线追踪
            Vector3 samplePos = position + direction * 1.0f;

            // 尝试从光照图采样
            foreach (var mesh in meshes)
            {
                if (mesh.LightmapUVs.Count == 0) continue;
                if (lightmaps == null) continue;

                // 检查采样点是否在网格包围盒内 (简化判断)
                // 返回环境光颜色
                return new Vector3(0.15f, 0.15f, 0.2f);
            }

            return new Vector3(0.1f, 0.1f, 0.15f);
        }

        /// <summary>球坐标转直角坐标。</summary>
        private static Vector3 SphericalToCartesian(float theta, float phi)
        {
            float sinPhi = MathF.Sin(phi);
            return new Vector3(
                sinPhi * MathF.Cos(theta),
                MathF.Cos(phi),
                sinPhi * MathF.Sin(theta)
            );
        }

        /// <summary>八面体映射到方向向量。</summary>
        private static Vector3 OctahedralToDirection(float u, float v)
        {
            float absU = MathF.Abs(u);
            float absV = MathF.Abs(v);
            float m = Math.Max(absU, absV);

            float x = u / m;
            float y = v / m;
            float z = 1.0f - m;

            if (u < 0) z = -z; // 不需要, 已经正确
            // 简化八面体映射
            Vector3 dir = new Vector3(x, z, y);
            float len = dir.Length();
            return len > 1e-8f ? dir / len : Vector3.UnitY;
        }
    }

    /// <summary>
    /// 辐照度探针数据。存储位置、SH 系数与八面体卷积纹理。
    /// </summary>
    public class IrradianceProbeData
    {
        /// <summary>探针世界空间位置。</summary>
        public Vector3 Position { get; set; }
        /// <summary>SH L2 系数 (R 通道, 9 个)。</summary>
        public float[] SHCoefficientsR { get; set; } = new float[IrradianceProbeBaker.SHL2CoeffCount];
        /// <summary>SH L2 系数 (G 通道, 9 个)。</summary>
        public float[] SHCoefficientsG { get; set; } = new float[IrradianceProbeBaker.SHL2CoeffCount];
        /// <summary>SH L2 系数 (B 通道, 9 个)。</summary>
        public float[] SHCoefficientsB { get; set; } = new float[IrradianceProbeBaker.SHL2CoeffCount];
        /// <summary>八面体卷积方向纹理 (运行时采样用)。</summary>
        public Vector3[]? OctahedralMap { get; set; }
        /// <summary>探针是否有效 (不在几何体内部)。</summary>
        public bool Valid { get; set; }
    }

    // ════════════════════════════════════════════════════════════════
    // 扩展方法: AABB 轴分量访问
    // ════════════════════════════════════════════════════════════════

    internal static class Vector3AxisExtensions
    {
        public static float GetAxis(this Vector3 v, int axis) => axis switch
        {
            0 => v.X,
            1 => v.Y,
            2 => v.Z,
            _ => throw new ArgumentOutOfRangeException(nameof(axis))
        };
    }
}
