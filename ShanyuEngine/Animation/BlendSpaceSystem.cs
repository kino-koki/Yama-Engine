using System;
using System.Collections.Generic;
using System.Numerics;
using ShanyuEngine.Skinning;

namespace ShanyuEngine.Animation.BlendSpaces
{
    // ============================================================================================
    //  BlendSpaceSystem.cs
    //
    //  混合空间系统（Blend Space System）。
    //
    //  提供基于参数坐标的动画混合：
    //    - BlendSpace1D ：单轴混合（如速度轴），在相邻阈值样本间线性插值。
    //    - BlendSpace2D ：双轴混合（如速度 + 方向），基于 Delaunay 三角剖分与重心坐标插值。
    //    - 采样算法     ：查询点落在三角形内时用重心坐标；落在凸包外时取最近 3 个样本反距离加权。
    //    - 网格编辑器数据：BlendSpaceGrid / BlendSpaceGridPoint，供编辑器预览每个网格点的混合权重。
    //
    //  复用既有 AnimPoseOutput / AnimationClip / Skeleton / SkeletonPose 数据类型。
    //  注：置于子命名空间 ShanyuEngine.Animation.BlendSpaces，与既有 AnimBlendSpaceSystem.cs
    //  中的 BlendSpace 等类型隔离，保证可编译。
    // ============================================================================================

    /// <summary>单个样本的混合权重结果。</summary>
    public readonly struct BlendSampleWeight
    {
        public readonly int Index;
        public readonly float Weight;
        public BlendSampleWeight(int index, float weight) { Index = index; Weight = weight; }
    }

    // ──────────────────────────────────────────────────────────────
    //  样本定义
    // ──────────────────────────────────────────────────────────────

    /// <summary>1D 混合空间样本：动画剪辑 + 单轴阈值。</summary>
    public sealed class BlendSpaceSample1D
    {
        public AnimationClip Clip { get; set; } = null!;
        public float X { get; set; }
        /// <summary>本帧求得的混合权重（运行期临时值）。</summary>
        public float RuntimeWeight { get; set; }
        public BlendSpaceSample1D() { }
        public BlendSpaceSample1D(AnimationClip clip, float x) { Clip = clip; X = x; }
    }

    /// <summary>2D 混合空间样本：动画剪辑 + 二维坐标。</summary>
    public sealed class BlendSpaceSample2D
    {
        public AnimationClip Clip { get; set; } = null!;
        public Vector2 Position { get; set; }
        public float RuntimeWeight { get; set; }
        public BlendSpaceSample2D() { }
        public BlendSpaceSample2D(AnimationClip clip, Vector2 position) { Clip = clip; Position = position; }
        public BlendSpaceSample2D(AnimationClip clip, float x, float y) { Clip = clip; Position = new Vector2(x, y); }
    }

    // ──────────────────────────────────────────────────────────────
    //  混合空间基类
    // ──────────────────────────────────────────────────────────────

    /// <summary>混合空间基类：管理骨架、内部播放时间与姿势混合辅助。</summary>
    public abstract class BlendSpaceBase
    {
        /// <summary>目标骨架。</summary>
        public Skeleton Skeleton { get; }

        /// <summary>是否循环。</summary>
        public bool Loop { get; set; } = true;

        /// <summary>播放速率。</summary>
        public float PlayRate { get; set; } = 1f;

        /// <summary>当前播放时间（秒）。</summary>
        public float CurrentTime { get; set; }

        /// <summary>混合空间长度（取所有样本动画的最大长度）。</summary>
        public float LengthSeconds { get; protected set; } = 1f;

        protected BlendSpaceBase(Skeleton skeleton)
        {
            Skeleton = skeleton ?? throw new ArgumentNullException(nameof(skeleton));
        }

        /// <summary>推进内部播放时间。</summary>
        public virtual void Tick(float deltaTime)
        {
            if (LengthSeconds <= 0f) { CurrentTime = 0f; return; }
            CurrentTime += deltaTime * PlayRate;
            if (Loop)
            {
                CurrentTime %= LengthSeconds;
                if (CurrentTime < 0f) CurrentTime += LengthSeconds;
            }
            else if (CurrentTime > LengthSeconds) CurrentTime = LengthSeconds;
        }

        /// <summary>采样一个剪辑到 AnimPoseOutput（按指定时间，自动循环/钳制）。</summary>
        protected AnimPoseOutput SampleClip(AnimationClip clip, float time, int boneCount)
        {
            var output = AnimPoseOutput.Create(boneCount);
            if (clip == null) return output;
            float len = clip.LengthSeconds;
            float t = len > 0f ? time : 0f;
            if (Loop && len > 0f) { t %= len; if (t < 0f) t += len; }
            else if (t > len) t = len;
            var pose = clip.Sample(Skeleton, t);
            output.CopyFromSkeletonPose(Skeleton, pose);
            return output;
        }

        /// <summary>按权重混合多个姿势到 dst。</summary>
        protected static void BlendPoses(AnimPoseOutput dst, AnimPoseOutput[] poses, float[] weights, int count)
        {
            int n = dst.BoneCount;
            float wsum = 0f;
            for (int k = 0; k < count; k++) wsum += weights[k];
            if (wsum <= 1e-8f)
            {
                if (count > 0) StateCopy(dst, poses[0]);
                return;
            }

            var trans = new Vector3[n];
            var scale = new Vector3[n];
            for (int i = 0; i < n; i++) { trans[i] = Vector3.Zero; scale[i] = Vector3.Zero; }

            for (int k = 0; k < count; k++)
            {
                float wn = weights[k] / wsum;
                if (wn <= 0f) continue;
                var p = poses[k];
                for (int i = 0; i < n; i++)
                {
                    trans[i] += p.Translations[i] * wn;
                    scale[i] += p.Scales[i] * wn;
                }
            }

            // 旋转：按归一化权重顺序 slerp（逐骨骼）
            for (int i = 0; i < n; i++)
            {
                Quaternion a = poses[0].Rotations[i];
                float aw = weights[0];
                for (int k = 1; k < count; k++)
                {
                    float w = weights[k];
                    aw += w;
                    a = Quaternion.Slerp(a, poses[k].Rotations[i], aw > 0f ? w / aw : 0f);
                }
                dst.Translations[i] = trans[i];
                dst.Scales[i] = scale[i];
                dst.Rotations[i] = Quaternion.Normalize(a);
            }
        }

        private static void StateCopy(AnimPoseOutput dst, AnimPoseOutput src)
        {
            int n = Math.Min(dst.BoneCount, src.BoneCount);
            for (int i = 0; i < n; i++)
            {
                dst.Translations[i] = src.Translations[i];
                dst.Rotations[i] = src.Rotations[i];
                dst.Scales[i] = src.Scales[i];
            }
        }
    }

    // ──────────────────────────────────────────────────────────────
    //  BlendSpace1D —— 单轴混合（速度轴）
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 一维混合空间。样本按单轴阈值（X）升序排列，查询时找到相邻的两个样本线性插值。
    /// 典型用途：按速度混合 Idle/Walk/Run。
    /// </summary>
    public sealed class BlendSpace1D : BlendSpaceBase
    {
        /// <summary>样本列表（添加后自动按 X 排序）。</summary>
        public List<BlendSpaceSample1D> Samples { get; } = new();

        /// <summary>当前混合参数（X 轴值）。</summary>
        public float BlendParameter { get; set; }

        private bool _sorted = true;

        public BlendSpace1D(Skeleton skeleton) : base(skeleton) { }

        /// <summary>添加样本。</summary>
        public void AddSample(AnimationClip clip, float x)
        {
            if (clip == null) return;
            Samples.Add(new BlendSpaceSample1D(clip, x));
            _sorted = false;
        }

        /// <summary>确保样本按 X 排序并重算长度。</summary>
        public void Rebuild()
        {
            Samples.Sort((a, b) => a.X.CompareTo(b.X));
            _sorted = true;
            float max = 0f;
            foreach (var s in Samples) max = MathF.Max(max, s.Clip?.LengthSeconds ?? 0f);
            LengthSeconds = MathF.Max(0.0001f, max);
        }

        /// <summary>
        /// 计算查询值 x 处的样本权重（最多 2 个相邻样本）。
        /// </summary>
        public List<BlendSampleWeight> GetSampleWeights(float x)
        {
            var result = new List<BlendSampleWeight>(2);
            if (!_sorted) Rebuild();
            if (Samples.Count == 0) return result;
            if (Samples.Count == 1)
            {
                result.Add(new BlendSampleWeight(0, 1f));
                return result;
            }
            if (x <= Samples[0].X)
            {
                result.Add(new BlendSampleWeight(0, 1f));
                return result;
            }
            if (x >= Samples[^1].X)
            {
                result.Add(new BlendSampleWeight(Samples.Count - 1, 1f));
                return result;
            }
            for (int i = 0; i < Samples.Count - 1; i++)
            {
                if (x >= Samples[i].X && x <= Samples[i + 1].X)
                {
                    float span = Samples[i + 1].X - Samples[i].X;
                    float alpha = span > 1e-8f ? (x - Samples[i].X) / span : 0f;
                    result.Add(new BlendSampleWeight(i, 1f - alpha));
                    result.Add(new BlendSampleWeight(i + 1, alpha));
                    return result;
                }
            }
            result.Add(new BlendSampleWeight(Samples.Count - 1, 1f));
            return result;
        }

        /// <summary>按当前 BlendParameter 与内部时间采样输出姿势。</summary>
        public AnimPoseOutput Sample()
        {
            return Sample(BlendParameter, CurrentTime);
        }

        /// <summary>按指定参数与时间采样输出姿势。</summary>
        public AnimPoseOutput Sample(float x, float time)
        {
            var output = AnimPoseOutput.Create(Skeleton.Bones.Count);
            if (!_sorted) Rebuild();
            if (Samples.Count == 0) return output;

            var weights = GetSampleWeights(x);
            var poses = new AnimPoseOutput[weights.Count];
            var ws = new float[weights.Count];
            for (int k = 0; k < weights.Count; k++)
            {
                poses[k] = SampleClip(Samples[weights[k].Index].Clip, time, Skeleton.Bones.Count);
                ws[k] = weights[k].Weight;
                Samples[weights[k].Index].RuntimeWeight = weights[k].Weight;
            }
            if (weights.Count == 1)
            {
                for (int i = 0; i < output.BoneCount; i++)
                {
                    output.Translations[i] = poses[0].Translations[i];
                    output.Rotations[i] = poses[0].Rotations[i];
                    output.Scales[i] = poses[0].Scales[i];
                }
                return output;
            }
            BlendPoses(output, poses, ws, weights.Count);
            return output;
        }
    }

    // ──────────────────────────────────────────────────────────────
    //  几何工具：方向判定 / 点在圆内判定 / 重心坐标
    // ──────────────────────────────────────────────────────────────

    internal static class BlendSpaceGeometry
    {
        /// <summary>二维方向判定：&gt;0 为 CCW，&lt;0 为 CW，0 共线。</summary>
        public static float Orient(Vector2 a, Vector2 b, Vector2 c)
            => (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);

        /// <summary>点 d 是否在三角形 (a,b,c) 的外接圆内（与方向无关）。</summary>
        public static bool InCircle(Vector2 a, Vector2 b, Vector2 c, Vector2 d)
        {
            float ax = a.X - d.X, ay = a.Y - d.Y;
            float bx = b.X - d.X, by = b.Y - d.Y;
            float cx = c.X - d.X, cy = c.Y - d.Y;
            float ap = ax * ax + ay * ay;
            float bp = bx * bx + by * by;
            float cp = cx * cx + cy * cy;
            float det = ax * (by * cp - bp * cy)
                      - ay * (bx * cp - bp * cx)
                      + ap * (bx * cy - by * cx);
            float o = Orient(a, b, c);
            if (o > 0f) return det > 0f;
            if (o < 0f) return det < 0f;
            return false; // 退化三角形
        }

        /// <summary>计算点 p 相对三角形 (a,b,c) 的重心坐标 (u,v,w)，u 对应 a。</summary>
        public static void Barycentric(Vector2 a, Vector2 b, Vector2 c, Vector2 p, out float u, out float v, out float w)
        {
            var v0 = b - a;
            var v1 = c - a;
            var v2 = p - a;
            float d00 = Vector2.Dot(v0, v0);
            float d01 = Vector2.Dot(v0, v1);
            float d11 = Vector2.Dot(v1, v1);
            float d20 = Vector2.Dot(v2, v0);
            float d21 = Vector2.Dot(v2, v1);
            float denom = d00 * d11 - d01 * d01;
            if (MathF.Abs(denom) < 1e-12f)
            {
                u = v = w = 0f;
                return;
            }
            v = (d11 * d20 - d01 * d21) / denom;
            w = (d00 * d21 - d01 * d20) / denom;
            u = 1f - v - w;
        }
    }

    // ──────────────────────────────────────────────────────────────
    //  三角形
    // ──────────────────────────────────────────────────────────────

    /// <summary>混合空间三角形：三个样本索引。</summary>
    public readonly struct BlendSpaceTriangle
    {
        public readonly int A;
        public readonly int B;
        public readonly int C;
        public BlendSpaceTriangle(int a, int b, int c) { A = a; B = b; C = c; }
    }

    // ──────────────────────────────────────────────────────────────
    //  BlendSpace2D —— 双轴混合（速度 + 方向）
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 二维混合空间。样本散布在二维平面，使用 Delaunay 三角剖分构建三角网，
    /// 查询点落在三角形内时用重心坐标插值；落在凸包外时取最近 3 个样本反距离加权。
    /// </summary>
    public sealed class BlendSpace2D : BlendSpaceBase
    {
        /// <summary>样本列表。</summary>
        public List<BlendSpaceSample2D> Samples { get; } = new();

        /// <summary>当前混合参数（X 轴）。</summary>
        public float BlendParameterX { get; set; }

        /// <summary>当前混合参数（Y 轴）。</summary>
        public float BlendParameterY { get; set; }

        /// <summary>当前查询坐标。</summary>
        public Vector2 BlendPosition
        {
            get => new(BlendParameterX, BlendParameterY);
            set { BlendParameterX = value.X; BlendParameterY = value.Y; }
        }

        private List<BlendSpaceTriangle> _triangles = new();
        private bool _dirty = true;

        public BlendSpace2D(Skeleton skeleton) : base(skeleton) { }

        /// <summary>添加样本。</summary>
        public void AddSample(AnimationClip clip, Vector2 position)
        {
            if (clip == null) return;
            Samples.Add(new BlendSpaceSample2D(clip, position));
            _dirty = true;
        }

        /// <summary>添加样本（坐标分量形式）。</summary>
        public void AddSample(AnimationClip clip, float x, float y)
            => AddSample(clip, new Vector2(x, y));

        /// <summary>标记脏（样本变更后需重建三角网）。</summary>
        public void MarkDirty() => _dirty = true;

        /// <summary>重建三角网与长度。</summary>
        public void Rebuild()
        {
            float max = 0f;
            foreach (var s in Samples) max = MathF.Max(max, s.Clip?.LengthSeconds ?? 0f);
            LengthSeconds = MathF.Max(0.0001f, max);
            _triangles = DelaunayTriangulate();
            _dirty = false;
        }

        /// <summary>获取三角网（必要时重建）。</summary>
        public List<BlendSpaceTriangle> GetTriangles()
        {
            if (_dirty) Rebuild();
            return _triangles;
        }

        /// <summary>三角剖分（暴露给编辑器使用）。</summary>
        public List<BlendSpaceTriangle> Triangulate()
        {
            if (_dirty) Rebuild();
            return new List<BlendSpaceTriangle>(_triangles);
        }

        /// <summary>
        /// 计算查询点 pos 处的样本权重：
        /// 若落在某三角形内，返回 3 个顶点的重心坐标权重；
        /// 否则返回最近 3 个样本的反距离权重。
        /// </summary>
        public List<BlendSampleWeight> GetSampleWeights(Vector2 pos)
        {
            var result = new List<BlendSampleWeight>(3);
            if (_dirty) Rebuild();
            if (Samples.Count == 0) return result;
            if (Samples.Count == 1) { result.Add(new BlendSampleWeight(0, 1f)); return result; }

            // 1) 三角形内 -> 重心坐标
            if (_triangles.Count > 0)
            {
                for (int t = 0; t < _triangles.Count; t++)
                {
                    var tri = _triangles[t];
                    var a = Samples[tri.A].Position;
                    var b = Samples[tri.B].Position;
                    var c = Samples[tri.C].Position;
                    BlendSpaceGeometry.Barycentric(a, b, c, pos, out float u, out float v, out float w);
                    const float eps = -1e-5f;
                    if (u >= eps && v >= eps && w >= eps)
                    {
                        // 钳制负值并归一化
                        u = MathF.Max(0f, u);
                        v = MathF.Max(0f, v);
                        w = MathF.Max(0f, w);
                        float sum = u + v + w;
                        if (sum > 1e-8f)
                        {
                            result.Add(new BlendSampleWeight(tri.A, u / sum));
                            result.Add(new BlendSampleWeight(tri.B, v / sum));
                            result.Add(new BlendSampleWeight(tri.C, w / sum));
                            return result;
                        }
                    }
                }
            }

            // 2) 凸包外 -> 最近 3 个样本反距离加权
            return NearestThreeWeights(pos);
        }

        /// <summary>取最近 3 个样本，按反距离加权。</summary>
        public List<BlendSampleWeight> NearestThreeWeights(Vector2 pos)
        {
            var result = new List<BlendSampleWeight>(3);
            int n = Samples.Count;
            if (n == 0) return result;
            if (n == 1) { result.Add(new BlendSampleWeight(0, 1f)); return result; }

            int k = Math.Min(3, n);
            var dists = new (int idx, float d)[n];
            for (int i = 0; i < n; i++)
            {
                var d = Vector2.DistanceSquared(pos, Samples[i].Position);
                dists[i] = (i, d);
            }
            Array.Sort(dists, (x, y) => x.d.CompareTo(y.d));

            float wsum = 0f;
            var ws = new float[k];
            for (int i = 0; i < k; i++)
            {
                float dist = MathF.Sqrt(dists[i].d);
                ws[i] = dist < 1e-5f ? 1e6f : 1f / dist;
                wsum += ws[i];
            }
            if (wsum <= 1e-8f)
            {
                for (int i = 0; i < k; i++) result.Add(new BlendSampleWeight(dists[i].idx, 1f / k));
                return result;
            }
            for (int i = 0; i < k; i++)
                result.Add(new BlendSampleWeight(dists[i].idx, ws[i] / wsum));
            return result;
        }

        /// <summary>按当前 BlendPosition 与内部时间采样输出姿势。</summary>
        public AnimPoseOutput Sample()
        {
            return Sample(BlendPosition, CurrentTime);
        }

        /// <summary>按指定坐标与时间采样输出姿势。</summary>
        public AnimPoseOutput Sample(Vector2 pos, float time)
        {
            var output = AnimPoseOutput.Create(Skeleton.Bones.Count);
            if (Samples.Count == 0) return output;

            var weights = GetSampleWeights(pos);
            var poses = new AnimPoseOutput[weights.Count];
            var ws = new float[weights.Count];
            for (int k = 0; k < weights.Count; k++)
            {
                poses[k] = SampleClip(Samples[weights[k].Index].Clip, time, Skeleton.Bones.Count);
                ws[k] = weights[k].Weight;
            }
            // 清零所有运行期权重后再写入
            foreach (var s in Samples) s.RuntimeWeight = 0f;
            for (int k = 0; k < weights.Count; k++)
                Samples[weights[k].Index].RuntimeWeight = weights[k].Weight;

            if (weights.Count == 1)
            {
                for (int i = 0; i < output.BoneCount; i++)
                {
                    output.Translations[i] = poses[0].Translations[i];
                    output.Rotations[i] = poses[0].Rotations[i];
                    output.Scales[i] = poses[0].Scales[i];
                }
                return output;
            }
            BlendPoses(output, poses, ws, weights.Count);
            return output;
        }

        // ── Delaunay 三角剖分（Bowyer-Watson 算法）───────────────
        private List<BlendSpaceTriangle> DelaunayTriangulate()
        {
            var tris = new List<BlendSpaceTriangle>();
            int n = Samples.Count;
            if (n < 3) return tris;

            // 收集点坐标（含超级三角形 3 个顶点：索引 n, n+1, n+2）
            var pts = new Vector2[n + 3];
            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            for (int i = 0; i < n; i++)
            {
                pts[i] = Samples[i].Position;
                var p = pts[i];
                if (p.X < minX) minX = p.X;
                if (p.Y < minY) minY = p.Y;
                if (p.X > maxX) maxX = p.X;
                if (p.Y > maxY) maxY = p.Y;
            }
            float dx = maxX - minX;
            float dy = maxY - minY;
            float dmax = MathF.Max(dx, dy);
            if (dmax < 1e-4f) dmax = 1f;
            float midX = (minX + maxX) * 0.5f;
            float midY = (minY + maxY) * 0.5f;
            // 超级三角形足够大以包住所有点
            pts[n] = new Vector2(midX - 20f * dmax, midY - dmax);
            pts[n + 1] = new Vector2(midX, midY + 20f * dmax);
            pts[n + 2] = new Vector2(midX + 20f * dmax, midY - dmax);

            // 三角形列表（用顶点索引）
            var triangles = new List<(int, int, int)>();
            triangles.Add((n, n + 1, n + 2));

            for (int i = 0; i < n; i++)
            {
                var bad = new List<(int, int, int)>();
                foreach (var tri in triangles)
                {
                    if (BlendSpaceGeometry.InCircle(pts[tri.Item1], pts[tri.Item2], pts[tri.Item3], pts[i]))
                        bad.Add(tri);
                }

                // 收集 bad 三角形的边界（只出现一次的边）
                var edgeCounts = new Dictionary<(int, int), int>();
                foreach (var tri in bad)
                {
                    AddEdge(edgeCounts, tri.Item1, tri.Item2);
                    AddEdge(edgeCounts, tri.Item2, tri.Item3);
                    AddEdge(edgeCounts, tri.Item3, tri.Item1);
                }
                var polygon = new List<(int, int)>();
                foreach (var kv in edgeCounts)
                    if (kv.Value == 1) polygon.Add(kv.Key);

                // 移除 bad 三角形
                triangles.RemoveAll(t => bad.Contains(t));

                // 用 polygon 边与点 i 构造新三角形
                foreach (var e in polygon)
                    triangles.Add((e.Item1, e.Item2, i));
            }

            // 移除包含超级三角形顶点的三角形
            foreach (var tri in triangles)
            {
                if (tri.Item1 >= n || tri.Item2 >= n || tri.Item3 >= n) continue;
                tris.Add(new BlendSpaceTriangle(tri.Item1, tri.Item2, tri.Item3));
            }
            return tris;
        }

        private static void AddEdge(Dictionary<(int, int), int> edges, int a, int b)
        {
            var key = a < b ? (a, b) : (b, a);
            edges.TryGetValue(key, out int c);
            edges[key] = c + 1;
        }
    }

    // ──────────────────────────────────────────────────────────────
    //  网格编辑器数据结构
    // ──────────────────────────────────────────────────────────────

    /// <summary>网格点：编辑器预览用的混合权重。</summary>
    public sealed class BlendSpaceGridPoint
    {
        public int IX { get; set; }
        public int IY { get; set; }
        public Vector2 Center { get; set; }
        /// <summary>该网格点处各样本的混合权重。</summary>
        public List<BlendSampleWeight> Weights { get; set; } = new();
    }

    /// <summary>
    /// 网格单元：由 4 个相邻网格点构成的四边形，供编辑器高亮与拾取。
    /// </summary>
    public sealed class BlendSpaceGridCell
    {
        public int IX { get; set; }
        public int IY { get; set; }
        /// <summary>四个角点的索引（Points 数组中的索引）。</summary>
        public (int p00, int p10, int p01, int p11) Corners { get; set; }
    }

    /// <summary>
    /// 混合空间网格：在轴范围内按行列采样，缓存每个网格点的混合权重。
    /// 供编辑器渲染权重热力图与采样预览。
    /// </summary>
    public sealed class BlendSpaceGrid
    {
        /// <summary>X 轴最小值。</summary>
        public float MinX { get; set; } = 0f;
        /// <summary>X 轴最大值。</summary>
        public float MaxX { get; set; } = 1f;
        /// <summary>Y 轴最小值。</summary>
        public float MinY { get; set; } = -1f;
        /// <summary>Y 轴最大值。</summary>
        public float MaxY { get; set; } = 1f;
        /// <summary>X 方向分段数。</summary>
        public int DivisionsX { get; set; } = 8;
        /// <summary>Y 方向分段数。</summary>
        public int DivisionsY { get; set; } = 8;

        /// <summary>网格点（[DivisionsY+1, DivisionsX+1]）。</summary>
        public BlendSpaceGridPoint[,] Points { get; private set; } = new BlendSpaceGridPoint[0, 0];

        /// <summary>网格单元。</summary>
        public List<BlendSpaceGridCell> Cells { get; private set; } = new();

        /// <summary>根据 2D 混合空间构建网格。</summary>
        public void Build(BlendSpace2D blendSpace)
        {
            if (blendSpace == null) return;
            DivisionsX = Math.Max(1, DivisionsX);
            DivisionsY = Math.Max(1, DivisionsY);
            Points = new BlendSpaceGridPoint[DivisionsY + 1, DivisionsX + 1];
            Cells.Clear();

            float stepX = (MaxX - MinX) / DivisionsX;
            float stepY = (MaxY - MinY) / DivisionsY;

            for (int j = 0; j <= DivisionsY; j++)
            {
                for (int i = 0; i <= DivisionsX; i++)
                {
                    float x = MinX + i * stepX;
                    float y = MinY + j * stepY;
                    var pt = new BlendSpaceGridPoint
                    {
                        IX = i,
                        IY = j,
                        Center = new Vector2(x, y),
                        Weights = blendSpace.GetSampleWeights(new Vector2(x, y))
                    };
                    Points[j, i] = pt;
                }
            }

            for (int j = 0; j < DivisionsY; j++)
            {
                for (int i = 0; i < DivisionsX; i++)
                {
                    Cells.Add(new BlendSpaceGridCell
                    {
                        IX = i,
                        IY = j,
                        Corners = (j * (DivisionsX + 1) + i,
                                   j * (DivisionsX + 1) + i + 1,
                                   (j + 1) * (DivisionsX + 1) + i,
                                   (j + 1) * (DivisionsX + 1) + i + 1)
                    });
                }
            }
        }

        /// <summary>获取指定行列的网格点。</summary>
        public BlendSpaceGridPoint? GetPoint(int ix, int iy)
        {
            if (iy < 0 || iy > DivisionsY || ix < 0 || ix > DivisionsX) return null;
            return Points[iy, ix];
        }
    }

    /// <summary>
    /// 混合空间编辑器数据：轴配置、显示开关、三角网与网格缓存。
    /// </summary>
    public sealed class BlendSpaceEditorData
    {
        public string AxisNameX { get; set; } = "Speed";
        public string AxisNameY { get; set; } = "Direction";
        public float MinX { get; set; } = 0f;
        public float MaxX { get; set; } = 600f;
        public float MinY { get; set; } = -180f;
        public float MaxY { get; set; } = 180f;
        public int GridDivisionsX { get; set; } = 8;
        public int GridDivisionsY { get; set; } = 8;
        public bool ShowGrid { get; set; } = true;
        public bool ShowTriangles { get; set; } = true;
        public bool ShowSampleLabels { get; set; } = true;

        /// <summary>三角网缓存。</summary>
        public List<BlendSpaceTriangle> Triangles { get; private set; } = new();

        /// <summary>网格缓存。</summary>
        public BlendSpaceGrid Grid { get; } = new();

        /// <summary>重建编辑器数据（样本变更后调用）。</summary>
        public void Rebuild(BlendSpace2D blendSpace)
        {
            if (blendSpace == null) return;
            blendSpace.MarkDirty();
            Triangles = blendSpace.Triangulate();
            Grid.MinX = MinX;
            Grid.MaxX = MaxX;
            Grid.MinY = MinY;
            Grid.MaxY = MaxY;
            Grid.DivisionsX = GridDivisionsX;
            Grid.DivisionsY = GridDivisionsY;
            Grid.Build(blendSpace);
        }
    }
}
