using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace YamaEngine.Physics
{
    // ============================================================================================
    //  DestructionSystem.cs
    //
    //  破坏系统。处理物体的破碎、断裂、切片等效果。
    //  和单纯粒子特效不同，破坏系统要把一个刚体真实地拆成多个刚体。
    //
    //  主要功能：
    //    1. Voronoi破碎 - 把凸包按Voronoi图切成多个小凸包，模拟玻璃/混凝土碎裂
    //    2. 切片(Slicing) - 用一个平面切物体，分成两半
    //    3. 断裂(Fracture) - 根据应力分布让物体沿薄弱面断裂
    //    4. 碎片管理 - 生成的新刚体加入物理世界，带初始速度
    //
    //  注意：质量不能为0(继承自父物体)，碎片数量要限制避免性能崩溃
    //  TODO: 实时布尔运算比较慢，大量碎片时性能不行
    // ============================================================================================

    /// <summary>破坏类型。</summary>
    public enum DestructionType
    {
        Voronoi = 0,        // Voronoi破碎
        Slice = 1,          // 平面切片
        Radial = 2,         // 径向爆裂(从一点向四周裂)
        Shatter = 3         // 完全粉碎
    }

    /// <summary>碎片数据。</summary>
    public struct Fragment
    {
        public Vector3[] Vertices;     // 凸包顶点
        public int[] Indices;          // 三角形索引
        public Vector3 CenterOfMass;
        public float Mass;
        public Matrix4x4 InertiaTensor;
        public Vector3 InitialVelocity;
        public Vector3 InitialAngularVelocity;
        public Vector3 Position;
        public Quaternion Orientation;
    }

    /// <summary>
    /// 破坏系统主类。
    /// </summary>
    public sealed class DestructionSystem
    {
        private readonly List<Fragment> _fragments = new(64);
        private readonly Random _rng = new(42);

        // 破坏参数
        private int _maxFragments = 32;
        private float _impulseThreshold = 5f;     // 触发破坏的最小冲量
        private float _stressThreshold = 100f;    // 应力阈值
        private float _fragmentLifetime = 10f;    // 碎片存活时间(秒)
        private bool _enableStressAccumulation = true;

        public int FragmentCount => _fragments.Count;

        public void SetMaxFragments(int n) => _maxFragments = Math.Clamp(n, 1, 256);
        public void SetImpulseThreshold(float t) => _impulseThreshold = MathF.Max(0f, t);
        public void SetStressThreshold(float t) => _stressThreshold = MathF.Max(0f, t);
        public void SetFragmentLifetime(float t) => _fragmentLifetime = MathF.Max(0f, t);
        public void EnableStressAccumulation(bool enable) => _enableStressAccumulation = enable;

        // =========================================================================
        //  Voronoi破碎
        //  在物体内部撒一批种子点，每个种子点对应一个Voronoi胞元，
        //  胞元和物体的相交部分就是一个碎片
        // =========================================================================

        /// <summary>
        /// 用Voronoi方法把一个凸包破碎成多个碎片。
        /// </summary>
        public List<Fragment> VoronoiFracture(Vector3[] sourceVertices, int[] sourceIndices,
            Vector3 center, Vector3 halfExtents, int seedCount, float totalMass)
        {
            var fragments = new List<Fragment>(seedCount);

            // 1) 在包围盒内生成种子点
            var seeds = new List<Vector3>(seedCount);
            for (int i = 0; i < seedCount; i++)
            {
                Vector3 seed = center + new Vector3(
                    (float)(_rng.NextDouble() * 2 - 1) * halfExtents.X,
                    (float)(_rng.NextDouble() * 2 - 1) * halfExtents.Y,
                    (float)(_rng.NextDouble() * 2 - 1) * halfExtents.Z);
                seeds.Add(seed);
            }

            // 2) 对每个种子点，构建Voronoi胞元(初始为包围盒)
            for (int i = 0; i < seeds.Count; i++)
            {
                var cell = CreateBoxFromCenter(seeds[i], halfExtents * 2f);

                // 用其他种子点的中垂面切割这个胞元
                for (int j = 0; j < seeds.Count; j++)
                {
                    if (i == j) continue;
                    Vector3 mid = (seeds[i] + seeds[j]) * 0.5f;
                    Vector3 normal = Vector3.Normalize(seeds[j] - seeds[i]);
                    cell = ClipConvexHullWithPlane(cell, mid, normal);
                    if (cell.Length == 0) break;
                }

                if (cell.Length < 4) continue;

                // 3) 用源物体(凸包)切割胞元
                // 简化：直接用源物体的AABB切割
                var sourceMin = center - halfExtents;
                var sourceMax = center + halfExtents;
                cell = ClipConvexHullWithAABB(cell, sourceMin, sourceMax);

                if (cell.Length < 4) continue;

                // 4) 创建碎片
                var frag = CreateFragmentFromVertices(cell, totalMass / seedCount);
                fragments.Add(frag);

                if (fragments.Count >= _maxFragments) break;
            }

            _fragments.AddRange(fragments);
            return fragments;
        }

        /// <summary>
        /// 径向破碎：从一个点向四周爆裂。
        /// 适合子弹打玻璃那种效果。
        /// </summary>
        public List<Fragment> RadialFracture(Vector3[] sourceVertices, int[] sourceIndices,
            Vector3 impactPoint, Vector3 impactNormal, float radius, int fragmentCount, float totalMass)
        {
            var fragments = new List<Fragment>(fragmentCount);

            // 在impactPoint周围一圈生成种子点
            Vector3 refVec = MathF.Abs(impactNormal.X) < 0.9f
                ? new Vector3(1, 0, 0) : new Vector3(0, 1, 0);
            Vector3 tangent1 = Vector3.Normalize(Vector3.Cross(impactNormal, refVec));
            Vector3 tangent2 = Vector3.Cross(impactNormal, tangent1);

            var seeds = new List<Vector3>(fragmentCount);
            // 中心点
            seeds.Add(impactPoint);

            for (int i = 1; i < fragmentCount; i++)
            {
                float angle = (float)i / fragmentCount * MathF.PI * 2f;
                float r = (float)(_rng.NextDouble() * 0.7 + 0.3) * radius;
                Vector3 seed = impactPoint + tangent1 * MathF.Cos(angle) * r + tangent2 * MathF.Sin(angle) * r;
                // 沿法线偏移一点
                seed += impactNormal * (float)(_rng.NextDouble() - 0.5) * radius * 0.5f;
                seeds.Add(seed);
            }

            // 用Voronoi方法切割
            for (int i = 0; i < seeds.Count; i++)
            {
                var cell = CreateBoxFromCenter(impactPoint, new Vector3(radius * 2, radius * 2, radius * 2));

                for (int j = 0; j < seeds.Count; j++)
                {
                    if (i == j) continue;
                    Vector3 mid = (seeds[i] + seeds[j]) * 0.5f;
                    Vector3 normal = Vector3.Normalize(seeds[j] - seeds[i]);
                    cell = ClipConvexHullWithPlane(cell, mid, normal);
                    if (cell.Length == 0) break;
                }

                if (cell.Length < 4) continue;

                var frag = CreateFragmentFromVertices(cell, totalMass / fragmentCount);
                // 径向初速度
                Vector3 dir = frag.CenterOfMass - impactPoint;
                if (dir.LengthSquared() > 1e-6f)
                {
                    dir = Vector3.Normalize(dir);
                    frag.InitialVelocity = dir * 3f + impactNormal * 2f;
                    frag.InitialAngularVelocity = new Vector3(
                        (float)(_rng.NextDouble() * 2 - 1) * 5f,
                        (float)(_rng.NextDouble() * 2 - 1) * 5f,
                        (float)(_rng.NextDouble() * 2 - 1) * 5f);
                }
                fragments.Add(frag);

                if (fragments.Count >= _maxFragments) break;
            }

            _fragments.AddRange(fragments);
            return fragments;
        }

        // =========================================================================
        //  切片
        // =========================================================================

        /// <summary>
        /// 用一个平面把凸包切成两半。
        /// </summary>
        public (Fragment? positive, Fragment? negative) SliceConvexHull(
            Vector3[] vertices, int[] indices,
            Vector3 planePoint, Vector3 planeNormal,
            float totalMass)
        {
            planeNormal = Vector3.Normalize(planeNormal);

            // 把顶点按平面分到正负两侧
            var posVerts = new List<Vector3>();
            var negVerts = new List<Vector3>();
            var signs = new float[vertices.Length];

            for (int i = 0; i < vertices.Length; i++)
            {
                float d = Vector3.Dot(vertices[i] - planePoint, planeNormal);
                signs[i] = d;
                if (d >= 0) posVerts.Add(vertices[i]);
                else negVerts.Add(vertices[i]);
            }

            // 算平面和每条边的交点，加到两侧
            for (int i = 0; i < indices.Length; i += 3)
            {
                for (int j = 0; j < 3; j++)
                {
                    int a = indices[i + j];
                    int b = indices[i + (j + 1) % 3];
                    if (signs[a] * signs[b] < 0)
                    {
                        // 跨越平面，算交点
                        float t = signs[a] / (signs[a] - signs[b]);
                        Vector3 intersection = Vector3.Lerp(vertices[a], vertices[b], t);
                        posVerts.Add(intersection);
                        negVerts.Add(intersection);
                    }
                }
            }

            Fragment? pos = null, neg = null;
            if (posVerts.Count >= 4)
            {
                var posArr = posVerts.ToArray();
                pos = CreateFragmentFromVertices(posArr, totalMass * 0.5f);
            }
            if (negVerts.Count >= 4)
            {
                var negArr = negVerts.ToArray();
                neg = CreateFragmentFromVertices(negArr, totalMass * 0.5f);
            }

            if (pos.HasValue) _fragments.Add(pos.Value);
            if (neg.HasValue) _fragments.Add(neg.Value);

            return (pos, neg);
        }

        /// <summary>多次切片模拟木头/金属的断裂。</summary>
        public List<Fragment> SliceMultiple(Vector3[] vertices, int[] indices,
            Vector3[] planePoints, Vector3[] planeNormals, float totalMass)
        {
            var current = new List<(Vector3[] verts, int[] indices)> { (vertices, indices) };
            var result = new List<Fragment>();

            for (int i = 0; i < planePoints.Length; i++)
            {
                var next = new List<(Vector3[] verts, int[] indices)>();
                foreach (var piece in current)
                {
                    var (pos, neg) = SliceConvexHull(piece.verts, piece.indices,
                        planePoints[i], planeNormals[i], totalMass / (planePoints.Length + 1));
                    if (pos.HasValue)
                        next.Add((pos.Value.Vertices, pos.Value.Indices));
                    if (neg.HasValue)
                        next.Add((neg.Value.Vertices, neg.Value.Indices));
                }
                current = next;
            }

            foreach (var piece in current)
            {
                if (piece.verts.Length >= 4)
                    result.Add(CreateFragmentFromVertices(piece.verts, totalMass / current.Count));
            }

            _fragments.AddRange(result);
            return result;
        }

        // =========================================================================
        //  应力断裂
        // =========================================================================

        /// <summary>
        /// 根据碰撞冲量判断是否应该破坏。
        /// 返回true表示应该破坏。
        /// </summary>
        public bool ShouldFracture(float impulse, Vector3 impactPoint, Vector3 impactNormal)
        {
            return impulse > _impulseThreshold;
        }

        /// <summary>
        /// 应力积累系统。多次小冲击累积，达到阈值后破坏。
        /// </summary>
        private readonly Dictionary<int, float> _stressMap = new();

        public void AccumulateStress(int bodyId, float impulse)
        {
            if (!_enableStressAccumulation) return;
            if (!_stressMap.TryGetValue(bodyId, out float stress))
                stress = 0f;
            stress += impulse;
            _stressMap[bodyId] = stress;
        }

        public bool CheckStressFracture(int bodyId)
        {
            if (_stressMap.TryGetValue(bodyId, out float stress))
                return stress > _stressThreshold;
            return false;
        }

        public void ClearStress(int bodyId)
        {
            _stressMap.Remove(bodyId);
        }

        public float GetStress(int bodyId)
        {
            return _stressMap.TryGetValue(bodyId, out float s) ? s : 0f;
        }

        // =========================================================================
        //  几何辅助
        // =========================================================================

        /// <summary>用平面切割凸包，返回正侧的顶点。</summary>
        public Vector3[] ClipConvexHullWithPlane(Vector3[] vertices, Vector3 planePoint, Vector3 planeNormal)
        {
            var result = new List<Vector3>();
            var signs = new float[vertices.Length];

            for (int i = 0; i < vertices.Length; i++)
                signs[i] = Vector3.Dot(vertices[i] - planePoint, planeNormal);

            // 收集正侧顶点
            for (int i = 0; i < vertices.Length; i++)
                if (signs[i] >= 0) result.Add(vertices[i]);

            // 算每条边的交点
            // 简化：对每对顶点检查
            for (int i = 0; i < vertices.Length; i++)
            {
                for (int j = i + 1; j < vertices.Length; j++)
                {
                    if (signs[i] * signs[j] < 0)
                    {
                        float t = signs[i] / (signs[i] - signs[j]);
                        Vector3 intersection = Vector3.Lerp(vertices[i], vertices[j], t);
                        result.Add(intersection);
                    }
                }
            }

            return result.ToArray();
        }

        /// <summary>用AABB切割凸包。</summary>
        public Vector3[] ClipConvexHullWithAABB(Vector3[] vertices, Vector3 min, Vector3 max)
        {
            var result = vertices;
            // 6个面依次切
            result = ClipConvexHullWithPlane(result, min, new Vector3(1, 0, 0));     // x > min
            result = ClipConvexHullWithPlane(result, max, new Vector3(-1, 0, 0));    // x < max
            result = ClipConvexHullWithPlane(result, min, new Vector3(0, 1, 0));     // y > min
            result = ClipConvexHullWithPlane(result, max, new Vector3(0, -1, 0));    // y < max
            result = ClipConvexHullWithPlane(result, min, new Vector3(0, 0, 1));     // z > min
            result = ClipConvexHullWithPlane(result, max, new Vector3(0, 0, -1));    // z < max
            return result;
        }

        /// <summary>创建一个AABB的8个顶点。</summary>
        private static Vector3[] CreateBoxFromCenter(Vector3 center, Vector3 size)
        {
            Vector3 min = center - size * 0.5f;
            Vector3 max = center + size * 0.5f;
            return new Vector3[]
            {
                new(min.X, min.Y, min.Z),
                new(max.X, min.Y, min.Z),
                new(min.X, max.Y, min.Z),
                new(max.X, max.Y, min.Z),
                new(min.X, min.Y, max.Z),
                new(max.X, min.Y, max.Z),
                new(min.X, max.Y, max.Z),
                new(max.X, max.Y, max.Z),
            };
        }

        /// <summary>从顶点列表创建碎片。</summary>
        private Fragment CreateFragmentFromVertices(Vector3[] vertices, float mass)
        {
            // 注意：质量不能为0
            mass = MathF.Max(mass, 0.001f);

            // 算质心
            Vector3 com = Vector3.Zero;
            for (int i = 0; i < vertices.Length; i++)
                com += vertices[i];
            com /= vertices.Length;

            // 算包围盒用于惯性张量近似
            Vector3 min = vertices[0], max = vertices[0];
            for (int i = 1; i < vertices.Length; i++)
            {
                min = Vector3.Min(min, vertices[i]);
                max = Vector3.Max(max, vertices[i]);
            }
            Vector3 halfExtents = (max - min) * 0.5f;
            var inertia = RigidBodyDynamics.BoxInertia(mass, halfExtents);

            // 生成三角形索引(简化：用fan triangulation从质心)
            var indices = new List<int>();
            for (int i = 1; i < vertices.Length - 1; i++)
            {
                indices.Add(0);
                indices.Add(i);
                indices.Add(i + 1);
            }

            return new Fragment
            {
                Vertices = vertices,
                Indices = indices.ToArray(),
                CenterOfMass = com,
                Mass = mass,
                InertiaTensor = inertia,
                Position = com,
                Orientation = Quaternion.Identity,
                InitialVelocity = Vector3.Zero,
                InitialAngularVelocity = Vector3.Zero
            };
        }

        // =========================================================================
        //  碎片管理
        // =========================================================================

        /// <summary>获取所有碎片(给物理世界创建刚体用)。</summary>
        public IEnumerable<Fragment> GetFragments() => _fragments;

        /// <summary>清除所有碎片。</summary>
        public void ClearFragments() => _fragments.Clear();

        /// <summary>移除某个碎片。</summary>
        public void RemoveFragment(int idx)
        {
            if (idx < 0 || idx >= _fragments.Count) return;
            _fragments.RemoveAt(idx);
        }

        /// <summary>更新碎片存活时间，过期的移除。</summary>
        public void UpdateLifetimes(float dt)
        {
            // 简化：没真正实现lifetime，需要外部记录创建时间
            // TODO: 加创建时间字段
        }

        // =========================================================================
        //  破坏预设
        // =========================================================================

        /// <summary>玻璃破碎预设：碎成很多小片。</summary>
        public List<Fragment> BreakGlass(Vector3[] vertices, int[] indices,
            Vector3 impactPoint, float totalMass)
        {
            int count = Math.Min(_maxFragments, 20);
            return RadialFracture(vertices, indices, impactPoint, Vector3.UnitY, 0.5f, count, totalMass);
        }

        /// <summary>木头断裂预设：沿一个方向裂成几条。</summary>
        public List<Fragment> BreakWood(Vector3[] vertices, int[] indices,
            Vector3 grainDirection, float totalMass, int pieces = 3)
        {
            var result = new List<Fragment>();
            // 沿grainDirection方向切pieces-1刀
            Vector3 min = vertices[0], max = vertices[0];
            foreach (var v in vertices)
            {
                min = Vector3.Min(min, v);
                max = Vector3.Max(max, v);
            }

            Vector3 normal = Vector3.Normalize(grainDirection);
            var current = new List<(Vector3[] verts, int[] idx)> { (vertices, indices) };

            for (int i = 1; i < pieces; i++)
            {
                float t = (float)i / pieces;
                Vector3 planePoint = Vector3.Lerp(min, max, t);
                var next = new List<(Vector3[] verts, int[] idx)>();
                foreach (var piece in current)
                {
                    var (pos, neg) = SliceConvexHull(piece.verts, piece.idx, planePoint, normal, totalMass / pieces);
                    if (pos.HasValue) next.Add((pos.Value.Vertices, pos.Value.Indices));
                    if (neg.HasValue) next.Add((neg.Value.Vertices, neg.Value.Indices));
                }
                current = next;
            }

            foreach (var piece in current)
            {
                if (piece.verts.Length >= 4)
                    result.Add(CreateFragmentFromVertices(piece.verts, totalMass / current.Count));
            }

            _fragments.AddRange(result);
            return result;
        }

        /// <summary>完全粉碎预设：碎成小颗粒。</summary>
        public List<Fragment> Shatter(Vector3[] vertices, int[] indices,
            Vector3 center, float totalMass, int pieceCount = 16)
        {
            // 用Voronoi方法
            Vector3 min = vertices[0], max = vertices[0];
            foreach (var v in vertices)
            {
                min = Vector3.Min(min, v);
                max = Vector3.Max(max, v);
            }
            Vector3 halfExtents = (max - min) * 0.5f;
            return VoronoiFracture(vertices, indices, center, halfExtents,
                Math.Min(pieceCount, _maxFragments), totalMass);
        }

        // =========================================================================
        //  统计
        // =========================================================================

        public DestructionStats GetStats()
        {
            var stats = new DestructionStats
            {
                TotalFragments = _fragments.Count,
                TrackedBodies = _stressMap.Count
            };
            float totalMass = 0f;
            foreach (var f in _fragments)
                totalMass += f.Mass;
            stats.TotalMass = totalMass;
            return stats;
        }

        public struct DestructionStats
        {
            public int TotalFragments;
            public int TrackedBodies;
            public float TotalMass;
        }
    }
}
