using System;
using System.Collections.Generic;
using System.Numerics;
using YamaEngine.Skinning;

namespace YamaEngine.Animation.BlendSpaces
{
    // ============================================================================================
    //  BlendSpace3DSystem.cs
    //
    //  BlendSpace3D —— 三维混合空间（对应 UE5.8 的 UBlendSpace 三轴采样）。
    //
    //  能力（深度级）：
    //    - 样本：剪辑 + 三维坐标 (X/Y/Z)，最多三轴混合（速度 / 方向 / 倾斜等）。
    //    - 四面体化：真正的 3D Delaunay（Bowyer–Watson 推广，外接球判定），
    //                每个四面体的 4 个顶点均为真实样本，覆盖样本点凸包。
    //    - 采样：查询点落在某四面体内 → 四面体重心坐标插值（最多 4 个样本）；
    //            落在凸包外 → 最近 4 个样本反距离加权。
    //    - 姿势混合：复用 BlendSpaceBase.SampleClip / BlendPoses。
    //
    //  注：置于 YamaEngine.Animation.BlendSpaces 子命名空间，与既有 BlendSpace1D/2D 并列。
    // ============================================================================================

    /// <summary>3D 混合空间样本：剪辑 + 三维坐标。</summary>
    public sealed class BlendSpaceSample3D
    {
        public AnimationClip Clip { get; set; }
        public Vector3 Position { get; set; }
        public float RuntimeWeight { get; set; }

        public BlendSpaceSample3D(AnimationClip clip, Vector3 position)
        {
            Clip = clip;
            Position = position;
        }
    }

    /// <summary>四面体：4 个样本顶点索引（均为真实样本点）。</summary>
    public readonly struct BlendSpaceTetrahedron
    {
        public readonly int A, B, C, D;
        public BlendSpaceTetrahedron(int a, int b, int c, int d) { A = a; B = b; C = c; D = d; }
    }

    /// <summary>
    /// 三维混合空间。对应 UE 的 UBlendSpace：最多三个轴（X/Y/Z）的离散样本点，
    /// 样本点构成凸包并被 Delaunay 四面体化，查询点按四面体重心坐标插值。
    /// </summary>
    public sealed class BlendSpace3D : BlendSpaceBase
    {
        /// <summary>样本列表。</summary>
        public List<BlendSpaceSample3D> Samples { get; } = new();

        public float BlendParameterX { get; set; }
        public float BlendParameterY { get; set; }
        public float BlendParameterZ { get; set; }

        /// <summary>当前查询坐标（三维）。</summary>
        public Vector3 BlendPosition
        {
            get => new(BlendParameterX, BlendParameterY, BlendParameterZ);
            set { BlendParameterX = value.X; BlendParameterY = value.Y; BlendParameterZ = value.Z; }
        }

        private List<BlendSpaceTetrahedron> _tets = new();
        private bool _dirty = true;

        public BlendSpace3D(Skeleton skeleton) : base(skeleton) { }

        /// <summary>添加样本。</summary>
        public void AddSample(AnimationClip clip, Vector3 position)
        {
            if (clip == null) return;
            Samples.Add(new BlendSpaceSample3D(clip, position));
            _dirty = true;
        }

        /// <summary>添加样本（坐标分量形式）。</summary>
        public void AddSample(AnimationClip clip, float x, float y, float z)
            => AddSample(clip, new Vector3(x, y, z));

        /// <summary>标记脏（样本变更后需重建四面体）。</summary>
        public void MarkDirty() => _dirty = true;

        /// <summary>重建四面体与长度。</summary>
        public void Rebuild()
        {
            float max = 0f;
            foreach (var s in Samples) max = MathF.Max(max, s.Clip?.LengthSeconds ?? 0f);
            LengthSeconds = MathF.Max(0.0001f, max);
            _tets = DelaunayTetrahedralize();
            _dirty = false;
        }

        /// <summary>获取四面体集合（必要时重建）。</summary>
        public List<BlendSpaceTetrahedron> GetTetrahedra()
        {
            if (_dirty) Rebuild();
            return _tets;
        }

        /// <summary>四面体化（暴露给编辑器使用）。</summary>
        public List<BlendSpaceTetrahedron> Tetrahedralize()
        {
            if (_dirty) Rebuild();
            return new List<BlendSpaceTetrahedron>(_tets);
        }

        /// <summary>
        /// 计算查询点 pos 处样本权重：
        /// 落在四面体内 → 四面体重心坐标（最多 4 样本）；
        /// 落在凸包外 → 最近 4 个样本反距离加权。
        /// </summary>
        public List<BlendSampleWeight> GetSampleWeights(Vector3 pos)
        {
            var result = new List<BlendSampleWeight>(4);
            if (_dirty) Rebuild();
            int n = Samples.Count;
            if (n == 0) return result;
            if (n == 1) { result.Add(new BlendSampleWeight(0, 1f)); return result; }
            if (n == 2)
            {
                AddSegmentWeights(result, pos);
                return result;
            }
            if (n == 3)
            {
                AddTriangleWeights(result, pos);
                return result;
            }

            // n >= 4：四面体搜索
            var pts = new Vector3[n];
            for (int i = 0; i < n; i++) pts[i] = Samples[i].Position;
            foreach (var tet in _tets)
            {
                var bc = Barycentric(pos, pts[tet.A], pts[tet.B], pts[tet.C], pts[tet.D]);
                if (bc != null &&
                    bc[0] >= -1e-4f && bc[1] >= -1e-4f && bc[2] >= -1e-4f && bc[3] >= -1e-4f)
                {
                    // 归一化（数值误差）
                    float s = bc[0] + bc[1] + bc[2] + bc[3];
                    if (s <= 1e-8f) break;
                    AddWeightIfPositive(result, tet.A, bc[0] / s);
                    AddWeightIfPositive(result, tet.B, bc[1] / s);
                    AddWeightIfPositive(result, tet.C, bc[2] / s);
                    AddWeightIfPositive(result, tet.D, bc[3] / s);
                    return result;
                }
            }

            // 凸包外 / 无四面体：最近 4 个样本反距离加权
            return NearestWeights(pos, 4);
        }

        /// <summary>按当前 BlendPosition 与内部时间采样输出姿势。</summary>
        public AnimPoseOutput Sample()
        {
            return Sample(BlendPosition, CurrentTime);
        }

        /// <summary>按指定坐标与时间采样输出姿势。</summary>
        public AnimPoseOutput Sample(Vector3 pos, float time)
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

        /// <summary>添加权重（过滤近零，避免返回无效参与混合的样本）。</summary>
        private static void AddWeightIfPositive(List<BlendSampleWeight> result, int index, float w)
        {
            if (w > 1e-5f) result.Add(new BlendSampleWeight(index, MathF.Min(w, 1f)));
        }

        // ── 2 样本线段插值 ──────────────────────────────────────────────
        private void AddSegmentWeights(List<BlendSampleWeight> result, Vector3 pos)
        {
            var a = Samples[0].Position;
            var b = Samples[1].Position;
            var ab = b - a;
            float len2 = ab.LengthSquared();
            float t = len2 > 1e-10f
                ? Math.Clamp(Vector3.Dot(pos - a, ab) / len2, 0f, 1f)
                : 0f;
            if (t < 1e-4f) result.Add(new BlendSampleWeight(0, 1f));
            else if (t > 1f - 1e-4f) result.Add(new BlendSampleWeight(1, 1f));
            else
            {
                result.Add(new BlendSampleWeight(0, 1f - t));
                result.Add(new BlendSampleWeight(1, t));
            }
        }

        // ── 3 样本三角形插值（投影到三角形平面）─────────────────────
        private void AddTriangleWeights(List<BlendSampleWeight> result, Vector3 pos)
        {
            var a = Samples[0].Position;
            var b = Samples[1].Position;
            var c = Samples[2].Position;
            var n = Vector3.Cross(b - a, c - a);
            float nl = n.Length();
            if (nl < 1e-8f) { result.AddRange(NearestWeights(pos, 3)); return; }
            n /= nl;
            // 把 pos 投影到平面
            var proj = pos - n * Vector3.Dot(pos - a, n);
            float area = nl;
            float w0 = Vector3.Dot(Vector3.Cross(b - proj, c - proj), n) / area;
            float w1 = Vector3.Dot(Vector3.Cross(c - proj, a - proj), n) / area;
            float w2 = Vector3.Dot(Vector3.Cross(a - proj, b - proj), n) / area;
            // 归一化
            float s = w0 + w1 + w2;
            if (s <= 1e-8f) { result.AddRange(NearestWeights(pos, 3)); return; }
            w0 /= s; w1 /= s; w2 /= s;
            if (w0 >= -1e-4f && w1 >= -1e-4f && w2 >= -1e-4f)
            {
                AddWeightIfPositive(result, 0, w0);
                AddWeightIfPositive(result, 1, w1);
                AddWeightIfPositive(result, 2, w2);
            }
            else result.AddRange(NearestWeights(pos, 3));
        }

        // ── 最近 k 个样本反距离加权 ────────────────────────────────────
        private List<BlendSampleWeight> NearestWeights(Vector3 pos, int k)
        {
            int n = Samples.Count;
            var idxs = new List<int>(n);
            var dists = new float[n];
            for (int i = 0; i < n; i++)
            {
                dists[i] = Vector3.Distance(pos, Samples[i].Position);
                idxs.Add(i);
            }
            idxs.Sort((x, y) => dists[x].CompareTo(dists[y]));
            int m = Math.Min(k, n);
            var ws = new float[m];
            float wsum = 0f;
            for (int i = 0; i < m; i++)
            {
                float d = dists[idxs[i]];
                ws[i] = d > 1e-6f ? 1f / (d * d) : 1e6f;
                wsum += ws[i];
            }
            var result = new List<BlendSampleWeight>(m);
            for (int i = 0; i < m; i++) result.Add(new BlendSampleWeight(idxs[i], ws[i] / wsum));
            return result;
        }

        // ── 3D Delaunay 四面体化（Bowyer–Watson 推广）──────────────────
        private List<BlendSpaceTetrahedron> DelaunayTetrahedralize()
        {
            var result = new List<BlendSpaceTetrahedron>();
            int n = Samples.Count;
            if (n < 4) return result;

            var pts = new Vector3[n + 4];
            for (int i = 0; i < n; i++) pts[i] = Samples[i].Position;

            // 包围盒
            float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue;
            float maxX = float.MinValue, maxY = float.MinValue, maxZ = float.MinValue;
            for (int i = 0; i < n; i++)
            {
                var p = pts[i];
                if (p.X < minX) minX = p.X; if (p.Y < minY) minY = p.Y; if (p.Z < minZ) minZ = p.Z;
                if (p.X > maxX) maxX = p.X; if (p.Y > maxY) maxY = p.Y; if (p.Z > maxZ) maxZ = p.Z;
            }
            float dx = maxX - minX, dy = maxY - minY, dz = maxZ - minZ;
            float dmax = MathF.Max(dx, MathF.Max(dy, dz));
            if (dmax < 1e-4f) dmax = 1f;
            var center = new Vector3((minX + maxX) * 0.5f, (minY + maxY) * 0.5f, (minZ + maxZ) * 0.5f);

            // 超四面体（足够大，包住所有点）
            pts[n] = center + new Vector3(0f, 0f, 6f * dmax);
            pts[n + 1] = center + new Vector3(5.6f * dmax, 0f, -2f * dmax);
            pts[n + 2] = center + new Vector3(-2.8f * dmax, 4.8f * dmax, -2f * dmax);
            pts[n + 3] = center + new Vector3(-2.8f * dmax, -4.8f * dmax, -2f * dmax);

            var tets = new List<(int, int, int, int)>();
            tets.Add((n, n + 1, n + 2, n + 3));

            for (int i = 0; i < n; i++)
            {
                var bad = new List<(int, int, int, int)>();
                foreach (var tet in tets)
                {
                    if (InSphere(pts[tet.Item1], pts[tet.Item2], pts[tet.Item3], pts[tet.Item4], pts[i]))
                        bad.Add(tet);
                }
                if (bad.Count == 0) continue;

                // 边界三角形（只出现奇数次的四面体面）
                var faceCount = new Dictionary<(int, int, int), int>();
                foreach (var tet in bad)
                {
                    AddTetFace(faceCount, tet.Item1, tet.Item2, tet.Item3);
                    AddTetFace(faceCount, tet.Item1, tet.Item2, tet.Item4);
                    AddTetFace(faceCount, tet.Item1, tet.Item3, tet.Item4);
                    AddTetFace(faceCount, tet.Item2, tet.Item3, tet.Item4);
                }
                tets.RemoveAll(t => bad.Contains(t));
                foreach (var kv in faceCount)
                {
                    if (kv.Value != 1) continue;
                    var (a, b, c) = kv.Key;
                    tets.Add((a, b, c, i));
                }
            }

            // 移除含超四面体顶点的四面体
            foreach (var tet in tets)
            {
                if (tet.Item1 >= n || tet.Item2 >= n || tet.Item3 >= n || tet.Item4 >= n) continue;
                result.Add(new BlendSpaceTetrahedron(tet.Item1, tet.Item2, tet.Item3, tet.Item4));
            }
            return result;
        }

        private static void AddTetFace(Dictionary<(int, int, int), int> faces, int a, int b, int c)
        {
            // 规范化顶点顺序（排序去重）
            int x = a, y = b, z = c;
            if (x > y) (x, y) = (y, x);
            if (y > z) (y, z) = (z, y);
            if (x > y) (x, y) = (y, x);
            var key = (x, y, z);
            faces.TryGetValue(key, out int cnt);
            faces[key] = cnt + 1;
        }

        /// <summary>点是否在外接球内（数值法：求外接球心与半径比较）。</summary>
        private static bool InSphere(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 p)
        {
            // 外接球心 o 满足 |o-a|^2 = |o-b|^2 = |o-c|^2 = |o-d|^2
            // 线性方程：2*(b-a)·o = |b|^2 - |a|^2 等
            if (!TrySolveSphere(a, b, c, d, out var o)) return false;
            float r2 = Vector3.DistanceSquared(o, a);
            return Vector3.DistanceSquared(o, p) < r2 - 1e-7f;
        }

        private static bool TrySolveSphere(Vector3 a, Vector3 b, Vector3 c, Vector3 d, out Vector3 o)
        {
            o = Vector3.Zero;
            // 3x3：行 = (b-a), (c-a), (d-a)
            var e1 = b - a;
            var e2 = c - a;
            var e3 = d - a;
            float det = Vector3.Dot(e1, Vector3.Cross(e2, e3));
            if (MathF.Abs(det) < 1e-10f) return false;

            var rhs = new Vector3(
                (b.LengthSquared() - a.LengthSquared()) * 0.5f,
                (c.LengthSquared() - a.LengthSquared()) * 0.5f,
                (d.LengthSquared() - a.LengthSquared()) * 0.5f);

            // 克拉默法则
            var r0 = new Vector3(rhs.X, rhs.Y, rhs.Z);
            var o1 = new Vector3(rhs.X, rhs.Y, rhs.Z);
            // 实际手动克拉默：
            // o.x = det([rhs, e2, e3]) / det
            // o.y = det([e1, rhs, e3]) / det
            // o.z = det([e1, e2, rhs]) / det
            float detX = Vector3.Dot(r0, Vector3.Cross(e2, e3));
            float detY = Vector3.Dot(e1, Vector3.Cross(r0, e3));
            float detZ = Vector3.Dot(e1, Vector3.Cross(e2, r0));
            o = new Vector3(detX / det, detY / det, detZ / det);
            return true;
        }

        /// <summary>重心坐标（相对四面体 a,b,c,d；退化返回 null）。</summary>
        private static float[]? Barycentric(Vector3 q, Vector3 a, Vector3 b, Vector3 c, Vector3 d)
        {
            var e1 = b - a;
            var e2 = c - a;
            var e3 = d - a;
            float det = Vector3.Dot(e1, Vector3.Cross(e2, e3));
            if (MathF.Abs(det) < 1e-10f) return null;
            var v = q - a;
            float w1 = Vector3.Dot(v, Vector3.Cross(e2, e3)) / det;
            float w2 = Vector3.Dot(e1, Vector3.Cross(v, e3)) / det;
            float w3 = Vector3.Dot(e1, Vector3.Cross(e2, v)) / det;
            return new float[] { 1f - w1 - w2 - w3, w1, w2, w3 };
        }
    }
}
