using System;
using System.Collections.Generic;
using System.Numerics;
using YamaEngine.ECS;
using YamaEngine.Modeling;
using YamaEngine.Particles;
using YamaEngine.Audio;

namespace YamaEngine.Physics
{
    // ============================================================================================
    //  DestructibleSystem.cs  —  Yama Engine 可破坏体 / 破碎系统
    //
    //  本文件实现yama引擎的运行时可破坏体（Destructible）与程序化网格破碎（Fracture）能力。
    //  它将物理系统（PhysicsScene）、实体组件系统（EntityComponentSystem）、建模系统
    //  （EditableMesh）、粒子系统（ParticleSystemCore）与音频系统（AudioSystem）有机整合，
    //  实现“累积伤害 -> 触发破碎 -> 生成碎片网格 -> 创建碎片刚体 -> 爆炸飞溅 -> 播放粒子
    //  与音效 -> 碎片生命周期（活跃 -> 休眠 -> 合并）”的完整闭环。
    //
    //  注意：
    //    * DestructibleComponent / DestructibleMaterial / DestructibleChunk / DestructibleAsset
    //      已在 ClothDestructionSystem.cs 中定义，本文件不重复定义，仅在需要时复用。
    //    * 本系统与同模块的 DestructionSystem 互补：DestructionSystem 基于预生成资产碎块层级
    //      （DestructibleAsset.Chunks）做冲击断裂；而本系统在运行时对 EditableMesh 做程序化
    //      破碎，并完整管理碎片刚体的生命周期。
    //
    //  对接的 API（均已与现有源文件核对）：
    //    * YamaEngine.Physics.PhysicsScene
    //        - AddRigidBody(entityId, position, rotation, mass, isKinematic, isStatic)
    //        - AddBoxCollider(entityId, halfExtents, center, isTrigger)
    //        - ApplyImpulse(entityId, impulse) / GetLinearVelocity(entityId) / GetPosition(entityId)
    //        - RemoveRigidBody(entityId) / 事件 OnCollisionEnter : Action<int,int>
    //    * YamaEngine.ECS.EntityComponentSystem
    //        - CreateEntity / DestroyEntity / AddComponent<T> / GetComponent<T> /
    //          SetComponent<T> / HasComponent<T> / QueryWithComponent<T> / HasEntity
    //        - TransformComponent { Position, Rotation, Scale }
    //    * YamaEngine.Modeling.EditableMesh
    //        - Vertices : List<Vector3> / Faces : List<MeshFace> / RecalculateNormals()
    //        - MeshFace(int A, int B, int C)  readonly record struct
    //    * YamaEngine.Particles.ParticleSystemCore
    //        - SetPosition / Emit(count, position?) / Play / Update(dt, cameraPos)
    //    * YamaEngine.Audio.AudioSystem
    //        - GetClip(name) / RegisterClip(name, path, duration, type) / PlayOneShot(...)
    // ============================================================================================

    /// <summary>
    /// 破碎方法。决定 <see cref="FractureProcessor"/> 如何切分网格。
    /// </summary>
    public enum FractureMethod
    {
        /// <summary>Voronoi 剖分：随机种子点，三角形按最近种子归簇。</summary>
        Voronoi,
        /// <summary>径向破碎：以冲击点为中心的同心壳层种子点。</summary>
        Radial,
        /// <summary>切片破碎：用随机平面递归二分网格（产生真实切面）。</summary>
        Slicing,
        /// <summary>均匀破碎：规则网格种子点，碎块大小相近。</summary>
        Uniform,
    }

    /// <summary>
    /// 破碎请求。描述一次网格破碎的全部输入参数。
    /// </summary>
    public sealed class FractureRequest
    {
        /// <summary>待破碎的网格（局部空间顶点）。</summary>
        public EditableMesh? Mesh;

        /// <summary>破碎方法。</summary>
        public FractureMethod Method = FractureMethod.Voronoi;

        /// <summary>冲击点 / 破碎原点（网格局部空间）。</summary>
        public Vector3 Point;

        /// <summary>冲击力大小（影响碎块数量与飞溅强度）。</summary>
        public float Force;

        /// <summary>期望碎块数量（实际数量可能略少）。</summary>
        public int ChunkCount = 8;

        /// <summary>随机种子（0 = 非确定性）。</summary>
        public int Seed;
    }

    /// <summary>
    /// 破碎结果。包含生成的碎块网格与各碎块在原始网格局部空间中的中心位置。
    /// </summary>
    public sealed class FractureResult
    {
        /// <summary>碎块网格列表（每个为独立 EditableMesh）。</summary>
        public List<EditableMesh> ChunkMeshes = new();

        /// <summary>各碎块中心（局部空间，与 ChunkMeshes 一一对应）。</summary>
        public List<Vector3> ChunkPositions = new();

        /// <summary>是否成功。</summary>
        public bool Success;

        /// <summary>原始网格顶点数。</summary>
        public int OriginalVertexCount;

        /// <summary>原始网格面数。</summary>
        public int OriginalFaceCount;

        /// <summary>实际生成的碎块数。</summary>
        public int ChunkCount => ChunkMeshes.Count;
    }

    /// <summary>
    /// 破碎处理器。对 <see cref="EditableMesh"/> 执行程序化破碎的静态工具集。
    /// 支持 Voronoi / Radial / Uniform（最近种子归簇）与 Slicing（平面递归二分）。
    /// </summary>
    public static class FractureProcessor
    {
        /// <summary>
        /// 执行一次破碎。
        /// </summary>
        public static FractureResult Fracture(FractureRequest? request)
        {
            var result = new FractureResult();
            if (request?.Mesh == null || request.Mesh.Vertices.Count == 0 || request.Mesh.Faces.Count == 0)
            {
                result.Success = false;
                return result;
            }

            var mesh = request.Mesh;
            result.OriginalVertexCount = mesh.Vertices.Count;
            result.OriginalFaceCount = mesh.Faces.Count;

            Random rng = request.Seed == 0 ? new Random() : new Random(request.Seed);
            int chunkCount = Math.Max(1, request.ChunkCount);

            switch (request.Method)
            {
                case FractureMethod.Voronoi:
                    return FractureByNearestSeed(mesh, request.Point, chunkCount, rng, SeedMode.Voronoi, result);
                case FractureMethod.Radial:
                    return FractureByNearestSeed(mesh, request.Point, chunkCount, rng, SeedMode.Radial, result);
                case FractureMethod.Uniform:
                    return FractureByNearestSeed(mesh, request.Point, chunkCount, rng, SeedMode.Uniform, result);
                case FractureMethod.Slicing:
                    return FractureBySlicing(mesh, chunkCount, rng, result);
                default:
                    result.Success = false;
                    return result;
            }
        }

        // ----------------------------------------------------------------------------------------
        //  最近种子归簇：Voronoi / Radial / Uniform 共用路径
        // ----------------------------------------------------------------------------------------

        private enum SeedMode { Voronoi, Radial, Uniform }

        private static FractureResult FractureByNearestSeed(EditableMesh mesh, Vector3 point,
            int chunkCount, Random rng, SeedMode mode, FractureResult result)
        {
            var (center, halfExtents) = ComputeBounds(mesh);
            var seeds = GenerateSeeds(mode, point, center, halfExtents, chunkCount, rng);
            if (seeds.Count == 0)
            {
                result.Success = false;
                return result;
            }

            // 将每个三角形分配到最近的种子点
            var buckets = new List<List<MeshFace>>(seeds.Count);
            for (int i = 0; i < seeds.Count; i++) buckets.Add(new List<MeshFace>());

            for (int fi = 0; fi < mesh.Faces.Count; fi++)
            {
                var face = mesh.Faces[fi];
                if (!face.IsValidFor(mesh.Vertices.Count)) continue;
                Vector3 centroid = (mesh.Vertices[face.A] + mesh.Vertices[face.B] + mesh.Vertices[face.C]) / 3f;

                int best = 0;
                float bestDist = float.MaxValue;
                for (int i = 0; i < seeds.Count; i++)
                {
                    float d = Vector3.DistanceSquared(seeds[i], centroid);
                    if (d < bestDist) { bestDist = d; best = i; }
                }
                buckets[best].Add(face);
            }

            // 由各桶构建独立子网格
            for (int i = 0; i < buckets.Count; i++)
            {
                if (buckets[i].Count == 0) continue;
                var (subMesh, subPos) = BuildSubMesh(mesh, buckets[i], "_chunk" + i);
                result.ChunkMeshes.Add(subMesh);
                result.ChunkPositions.Add(subPos);
            }

            result.Success = result.ChunkMeshes.Count > 0;
            return result;
        }

        /// <summary>生成种子点。</summary>
        private static List<Vector3> GenerateSeeds(SeedMode mode, Vector3 point, Vector3 center,
            Vector3 halfExtents, int count, Random rng)
        {
            var seeds = new List<Vector3>(count);
            switch (mode)
            {
                case SeedMode.Voronoi:
                    for (int i = 0; i < count; i++)
                        seeds.Add(new Vector3(
                            center.X + (float)(rng.NextDouble() * 2 - 1) * halfExtents.X,
                            center.Y + (float)(rng.NextDouble() * 2 - 1) * halfExtents.Y,
                            center.Z + (float)(rng.NextDouble() * 2 - 1) * halfExtents.Z));
                    break;

                case SeedMode.Uniform:
                    int gridDim = Math.Max(2, (int)Math.Ceiling(Math.Pow(count, 1.0 / 3.0)));
                    int placed = 0;
                    for (int x = 0; x < gridDim && placed < count; x++)
                        for (int y = 0; y < gridDim && placed < count; y++)
                            for (int z = 0; z < gridDim && placed < count; z++)
                            {
                                float fx = gridDim == 1 ? 0f : (x / (float)(gridDim - 1) * 2f - 1f);
                                float fy = gridDim == 1 ? 0f : (y / (float)(gridDim - 1) * 2f - 1f);
                                float fz = gridDim == 1 ? 0f : (z / (float)(gridDim - 1) * 2f - 1f);
                                seeds.Add(center + new Vector3(fx * halfExtents.X, fy * halfExtents.Y, fz * halfExtents.Z));
                                placed++;
                            }
                    break;

                case SeedMode.Radial:
                    // 以冲击点为中心的同心壳层
                    seeds.Add(point);
                    int remaining = count - 1;
                    int shell = 1;
                    float baseR = Math.Min(halfExtents.X, Math.Min(halfExtents.Y, halfExtents.Z)) * 0.45f;
                    while (remaining > 0 && shell <= 8)
                    {
                        int onShell = Math.Min(remaining, shell * 6);
                        float radius = shell * baseR;
                        for (int i = 0; i < onShell; i++)
                        {
                            float theta = (float)(rng.NextDouble() * Math.PI * 2.0);
                            float phi = (float)(rng.NextDouble() * Math.PI - Math.PI * 0.5);
                            Vector3 dir = new(
                                MathF.Cos(phi) * MathF.Cos(theta),
                                MathF.Sin(phi),
                                MathF.Cos(phi) * MathF.Sin(theta));
                            seeds.Add(point + dir * radius);
                        }
                        remaining -= onShell;
                        shell++;
                    }
                    break;
            }
            return seeds;
        }

        // ----------------------------------------------------------------------------------------
        //  切片破碎：递归平面二分
        // ----------------------------------------------------------------------------------------

        private static FractureResult FractureBySlicing(EditableMesh mesh, int chunkCount, Random rng, FractureResult result)
        {
            var pieces = new List<EditableMesh> { mesh };

            while (pieces.Count < chunkCount)
            {
                // 选出三角形数最多且可再分的碎块
                int maxIdx = -1;
                int maxFaces = 3; // 至少 4 个面才有意义再切
                for (int i = 0; i < pieces.Count; i++)
                {
                    if (pieces[i].Faces.Count > maxFaces) { maxFaces = pieces[i].Faces.Count; maxIdx = i; }
                }
                if (maxIdx < 0) break;

                var target = pieces[maxIdx];
                pieces.RemoveAt(maxIdx);

                var (tc, _) = ComputeBounds(target);
                Vector3 normal = RandomDirection(rng);
                float offset = Vector3.Dot(normal, tc);

                var (front, back) = SliceByPlane(target, normal, offset);
                if (front.Faces.Count > 0 && back.Faces.Count > 0)
                {
                    pieces.Add(front);
                    pieces.Add(back);
                }
                else
                {
                    // 该方向无法切分，放回并终止（避免死循环）
                    pieces.Add(target);
                    break;
                }
            }

            foreach (var p in pieces)
            {
                var (pc, _) = ComputeBounds(p);
                result.ChunkMeshes.Add(p);
                result.ChunkPositions.Add(pc);
            }
            result.Success = result.ChunkMeshes.Count > 0;
            return result;
        }

        /// <summary>
        /// 用平面（法线 normal，偏移 offset，满足 dot(normal,p) = offset）将网格切成前/后两半。
        /// 前半 = dot(normal,v)-offset &gt;= 0 的部分，后半 = &lt;= 0 的部分。
        /// 采用 Sutherland–Hodgman 多边形裁剪，对三角形产生 1~2 个三角形。
        /// </summary>
        private static (EditableMesh front, EditableMesh back) SliceByPlane(
            EditableMesh src, Vector3 normal, float offset)
        {
            var front = new EditableMesh { Name = src.Name + "_f" };
            var back = new EditableMesh { Name = src.Name + "_b" };

            for (int fi = 0; fi < src.Faces.Count; fi++)
            {
                var f = src.Faces[fi];
                if (!f.IsValidFor(src.Vertices.Count)) continue;

                Vector3 va = src.Vertices[f.A], vb = src.Vertices[f.B], vc = src.Vertices[f.C];
                float da = Vector3.Dot(normal, va) - offset;
                float db = Vector3.Dot(normal, vb) - offset;
                float dc = Vector3.Dot(normal, vc) - offset;

                bool fa = da >= 0f, fb = db >= 0f, fc = dc >= 0f;

                if (fa && fb && fc) { AddTriangle(front, va, vb, vc); }
                else if (!fa && !fb && !fc) { AddTriangle(back, va, vb, vc); }
                else
                {
                    var poly = new List<Vector3>(3) { va, vb, vc };
                    var dist = new List<float>(3) { da, db, dc };

                    var frontPoly = ClipPolygonAgainstPlane(poly, dist, keepFront: true);
                    var backPoly = ClipPolygonAgainstPlane(poly, dist, keepFront: false);

                    AddPolygon(front, frontPoly);
                    AddPolygon(back, backPoly);
                }
            }

            front.RecalculateNormals();
            back.RecalculateNormals();
            return (front, back);
        }

        /// <summary>Sutherland–Hodgman：用平面裁剪多边形，返回保留侧顶点环。</summary>
        private static List<Vector3> ClipPolygonAgainstPlane(List<Vector3> poly, List<float> dist, bool keepFront)
        {
            var output = new List<Vector3>();
            int n = poly.Count;
            for (int i = 0; i < n; i++)
            {
                int j = (i + 1) % n;
                Vector3 cur = poly[i];
                Vector3 nxt = poly[j];
                float dCur = dist[i];
                float dNxt = dist[j];

                bool curIn = keepFront ? dCur >= 0f : dCur <= 0f;
                bool nxtIn = keepFront ? dNxt >= 0f : dNxt <= 0f;

                if (curIn) output.Add(cur);
                if (curIn != nxtIn)
                {
                    float denom = dCur - dNxt;
                    float t = Math.Abs(denom) < 1e-8f ? 0f : dCur / denom;
                    output.Add(cur + (nxt - cur) * t);
                }
            }
            return output;
        }

        /// <summary>扇形三角化多边形并写入网格。</summary>
        private static void AddPolygon(EditableMesh mesh, List<Vector3> poly)
        {
            if (poly.Count < 3) return;
            for (int i = 2; i < poly.Count; i++)
                AddTriangle(mesh, poly[0], poly[i - 1], poly[i]);
        }

        /// <summary>向网格追加一个三角形（非索引化，简单复制顶点）。</summary>
        private static void AddTriangle(EditableMesh mesh, Vector3 a, Vector3 b, Vector3 c)
        {
            int i0 = mesh.Vertices.Count; mesh.Vertices.Add(a);
            int i1 = mesh.Vertices.Count; mesh.Vertices.Add(b);
            int i2 = mesh.Vertices.Count; mesh.Vertices.Add(c);
            mesh.Faces.Add(new MeshFace(i0, i1, i2));
        }

        // ----------------------------------------------------------------------------------------
        //  公共工具
        // ----------------------------------------------------------------------------------------

        /// <summary>由源网格的若干三角形构建独立子网格，返回子网格与其中心。</summary>
        private static (EditableMesh sub, Vector3 center) BuildSubMesh(EditableMesh src, List<MeshFace> faces, string suffix)
        {
            var sub = new EditableMesh { Name = src.Name + suffix };
            var remap = new Dictionary<int, int>();
            Vector3 acc = Vector3.Zero;
            int count = 0;

            for (int i = 0; i < faces.Count; i++)
            {
                var f = faces[i];
                if (!f.IsValidFor(src.Vertices.Count)) continue;

                AddRemappedVertex(src, sub, remap, f.A, ref acc, ref count);
                AddRemappedVertex(src, sub, remap, f.B, ref acc, ref count);
                AddRemappedVertex(src, sub, remap, f.C, ref acc, ref count);

                sub.Faces.Add(new MeshFace(remap[f.A], remap[f.B], remap[f.C]));
            }

            sub.RecalculateNormals();
            Vector3 center = count > 0 ? acc / count : Vector3.Zero;
            return (sub, center);
        }

        private static void AddRemappedVertex(EditableMesh src, EditableMesh sub,
            Dictionary<int, int> remap, int srcIdx, ref Vector3 acc, ref int count)
        {
            if (remap.ContainsKey(srcIdx)) return;
            remap[srcIdx] = sub.Vertices.Count;
            Vector3 v = src.Vertices[srcIdx];
            sub.Vertices.Add(v);
            acc += v;
            count++;
        }

        /// <summary>计算网格 AABB（中心与半尺寸）。</summary>
        private static (Vector3 center, Vector3 halfExtents) ComputeBounds(EditableMesh mesh)
        {
            if (mesh.Vertices.Count == 0)
                return (Vector3.Zero, new Vector3(0.5f, 0.5f, 0.5f));

            Vector3 min = new(float.MaxValue), max = new(float.MinValue);
            for (int i = 0; i < mesh.Vertices.Count; i++)
            {
                min = Vector3.Min(min, mesh.Vertices[i]);
                max = Vector3.Max(max, mesh.Vertices[i]);
            }
            Vector3 center = (min + max) * 0.5f;
            Vector3 half = (max - min) * 0.5f;
            if (half.X < 1e-4f) half.X = 0.5f;
            if (half.Y < 1e-4f) half.Y = 0.5f;
            if (half.Z < 1e-4f) half.Z = 0.5f;
            return (center, half);
        }

        /// <summary>计算网格 AABB（最小/最大点）。</summary>
        public static (Vector3 min, Vector3 max) MeshBounds(EditableMesh mesh)
        {
            if (mesh.Vertices.Count == 0) return (Vector3.Zero, Vector3.Zero);
            Vector3 min = new(float.MaxValue), max = new(float.MinValue);
            for (int i = 0; i < mesh.Vertices.Count; i++)
            {
                min = Vector3.Min(min, mesh.Vertices[i]);
                max = Vector3.Max(max, mesh.Vertices[i]);
            }
            return (min, max);
        }

        /// <summary>单位球内拒绝采样得到随机单位方向。</summary>
        private static Vector3 RandomDirection(Random rng)
        {
            Vector3 v;
            float lenSq;
            do
            {
                v = new Vector3(
                    (float)(rng.NextDouble() * 2 - 1),
                    (float)(rng.NextDouble() * 2 - 1),
                    (float)(rng.NextDouble() * 2 - 1));
                lenSq = v.LengthSquared();
            }
            while (lenSq > 1f || lenSq < 1e-6f);
            return Vector3.Normalize(v);
        }
    }

    /// <summary>
    /// 可破坏系统。实现 ECS 的 <see cref="ISystem"/> 接口，统一驱动运行时可破坏实体。
    /// 职责：累积伤害、触发程序化破碎、生成碎片刚体、播放粒子与音效、管理碎片生命周期
    /// （活跃 -> 休眠 -> 合并/移除）。
    /// </summary>
    public sealed class DestructibleSystem : ISystem
    {
        /// <summary>系统名称。</summary>
        public string Name => "DestructibleSystem";

        // ---- 外部系统引用 ----

        /// <summary>物理场景（用于创建/移除碎片刚体、施加冲量、查询速度）。</summary>
        public PhysicsScene? Scene;

        /// <summary>粒子系统（破碎碎屑特效）。由本系统每帧推进。</summary>
        public ParticleSystemCore? Particles;

        /// <summary>音频系统（破碎音效）。由本系统每帧推进。</summary>
        public AudioSystem? Audio;

        /// <summary>渲染摄像机位置（粒子排序/距离用）。</summary>
        public Vector3 CameraPosition = new(0, 0, -10f);

        /// <summary>随机数生成器。</summary>
        public Random RNG = new(1337);

        // ---- 生命周期调参 ----

        /// <summary>低于此线速度（m/s）视为低速，开始累计休眠计时。</summary>
        public float SleepSpeedThreshold = 0.25f;

        /// <summary>低速持续达到该时长（秒）后进入休眠。</summary>
        public float SleepTimeRequired = 1.5f;

        /// <summary>休眠碎片存活时长（秒），到期后合并或移除。</summary>
        public float SleepingLifetime = 10f;

        /// <summary>同属主的休眠碎片间距小于此值时可合并。</summary>
        public float MergeDistance = 0.4f;

        /// <summary>单次破碎每个属主生成的最大碎片数。</summary>
        public int MaxChunksPerOwner = 64;

        /// <summary>碎片飞溅冲量缩放。</summary>
        public float ImpulseScale = 1.0f;

        /// <summary>碎片刚体最小质量（kg）。</summary>
        public float MinMassForChunk = 0.05f;

        /// <summary>破碎音效在音频系统中的注册名。</summary>
        public string FractureSoundName = "Fracture";

        // ---- 事件 ----

        /// <summary>破碎发生时触发（属主实体Id, 冲击点, 破碎结果）。</summary>
        public event Action<int, Vector3, FractureResult>? OnFractured;

        /// <summary>碎片被合并时触发（属主实体Id, 碎片实体Id）。</summary>
        public event Action<int, int>? OnChunkMerged;

        // ---- 碎片状态机 ----

        /// <summary>碎片生命周期状态。</summary>
        public enum ChunkState
        {
            /// <summary>活跃：受物理模拟驱动。</summary>
            Active,
            /// <summary>休眠：长时间低速，待合并或移除。</summary>
            Sleeping,
            /// <summary>已合并：并入合并体。</summary>
            Merged,
            /// <summary>已移除：刚体与实体均已销毁。</summary>
            Removed,
        }

        /// <summary>碎片运行时状态。</summary>
        private sealed class ChunkRuntime
        {
            public int EntityId;
            public int OwnerId;
            public Vector3 HalfExtents;
            public float Mass;
            public ChunkState State = ChunkState.Active;
            public float LowSpeedTimer;
            public float SleepingTimer;
            public float Age;
        }

        // ---- 属主源数据（运行时注册） ----

        private readonly Dictionary<int, EditableMesh> _sourceMeshes = new();
        private readonly Dictionary<int, DestructibleMaterial> _materials = new();
        private readonly Dictionary<int, FractureMethod> _methods = new();
        private readonly Dictionary<int, float> _damage = new();

        // ---- 碎片运行时追踪（键 = 碎片实体Id） ----

        private readonly Dictionary<int, ChunkRuntime> _chunks = new();

        /// <summary>当前活跃+休眠的碎片总数。</summary>
        public int ActiveChunkCount
        {
            get
            {
                int c = 0;
                foreach (var ch in _chunks.Values)
                    if (ch.State == ChunkState.Active || ch.State == ChunkState.Sleeping) c++;
                return c;
            }
        }

        // ----------------------------------------------------------------------------------------
        //  注册 / 注销
        // ----------------------------------------------------------------------------------------

        /// <summary>
        /// 将实体注册为可破坏体，绑定源网格、材质与默认破碎方法。
        /// 若实体已存在 <see cref="DestructibleComponent"/>，则保持其资产不变（仅复用）。
        /// </summary>
        public void Register(int entityId, EditableMesh mesh,
            DestructibleMaterial? material = null, FractureMethod method = FractureMethod.Voronoi)
        {
            if (mesh == null) return;
            _sourceMeshes[entityId] = mesh;
            _materials[entityId] = material ?? new DestructibleMaterial();
            _methods[entityId] = method;
            _damage[entityId] = 0f;
        }

        /// <summary>设置某实体的破碎方法。</summary>
        public void SetFractureMethod(int entityId, FractureMethod method)
        {
            if (_methods.ContainsKey(entityId)) _methods[entityId] = method;
        }

        /// <summary>注销属主（不销毁已生成的碎片实体）。</summary>
        public void Unregister(int entityId)
        {
            _sourceMeshes.Remove(entityId);
            _materials.Remove(entityId);
            _methods.Remove(entityId);
            _damage.Remove(entityId);
        }

        /// <summary>清空所有注册与碎片追踪（不销毁 ECS 实体或物理刚体）。</summary>
        public void Clear()
        {
            _sourceMeshes.Clear();
            _materials.Clear();
            _methods.Clear();
            _damage.Clear();
            _chunks.Clear();
        }

        // ----------------------------------------------------------------------------------------
        //  伤害累积与触发
        // ----------------------------------------------------------------------------------------

        /// <summary>
        /// 对可破坏实体施加伤害（累积冲击）。当累积值达到材质硬度阈值时自动触发破碎。
        /// </summary>
        /// <param name="ecs">ECS。</param>
        /// <param name="entityId">目标实体。</param>
        /// <param name="worldPoint">冲击点（世界空间）。</param>
        /// <param name="impulse">冲击冲量大小。</param>
        public void ApplyDamage(EntityComponentSystem ecs, int entityId, Vector3 worldPoint, float impulse)
        {
            if (!_sourceMeshes.ContainsKey(entityId)) return;
            if (impulse <= 0f) return;

            float cur = _damage.TryGetValue(entityId, out var d) ? d : 0f;
            cur += impulse;
            _damage[entityId] = cur;

            var mat = _materials[entityId];
            if (cur >= mat.Hardness)
            {
                FractureEntity(ecs, entityId, worldPoint, cur);
                _damage[entityId] = 0f;
            }
        }

        /// <summary>
        /// 强制对实体执行一次破碎（忽略伤害累积）。
        /// </summary>
        public void FractureEntity(EntityComponentSystem ecs, int entityId, Vector3 worldPoint, float impulse)
        {
            if (!_sourceMeshes.TryGetValue(entityId, out var mesh)) return;
            if (!_materials.TryGetValue(entityId, out var mat)) return;
            var method = _methods.TryGetValue(entityId, out var m) ? m : FractureMethod.Voronoi;

            // 取属主世界变换
            Vector3 pos = Vector3.Zero;
            Quaternion rot = Quaternion.Identity;
            Vector3 scl = Vector3.One;
            if (ecs.HasComponent<TransformComponent>(entityId))
            {
                var tf = ecs.GetComponent<TransformComponent>(entityId);
                pos = tf.Position; rot = tf.Rotation; scl = tf.Scale;
            }

            Vector3 localPoint = WorldToLocal(worldPoint, pos, rot, scl);

            int chunkCount = ComputeChunkCount(mat, impulse);
            var req = new FractureRequest
            {
                Mesh = mesh,
                Method = method,
                Point = localPoint,
                Force = impulse,
                ChunkCount = chunkCount,
                Seed = unchecked((int)(impulse * 1000f)) ^ entityId ^ RNG.Next(),
            };
            var result = FractureProcessor.Fracture(req);
            if (!result.Success || result.ChunkMeshes.Count == 0)
            {
                OnFractured?.Invoke(entityId, worldPoint, result);
                return;
            }

            // 隐藏 / 停用属主：若存在 DestructibleComponent 则禁用；移除其物理刚体
            if (ecs.HasComponent<DestructibleComponent>(entityId))
            {
                var dc = ecs.GetComponent<DestructibleComponent>(entityId);
                dc.Enabled = false;
                ecs.SetComponent(entityId, dc);
            }
            Scene?.RemoveRigidBody(entityId);

            // 生成碎片刚体
            int spawned = 0;
            for (int i = 0; i < result.ChunkMeshes.Count && spawned < MaxChunksPerOwner; i++)
            {
                var chunkMesh = result.ChunkMeshes[i];
                Vector3 localCenter = result.ChunkPositions[i];

                var (cmin, cmax) = FractureProcessor.MeshBounds(chunkMesh);
                Vector3 he = (cmax - cmin) * 0.5f * scl;
                if (he.X < 1e-3f) he.X = 0.05f;
                if (he.Y < 1e-3f) he.Y = 0.05f;
                if (he.Z < 1e-3f) he.Z = 0.05f;

                float volume = he.X * he.Y * he.Z * 8f;
                float mass = Math.Max(MinMassForChunk, volume * mat.Density * 0.001f);

                Vector3 worldCenter = LocalToWorld(localCenter, pos, rot, scl);
                int chunkEntity = ecs.CreateEntity($"Chunk_{entityId}_{i}", worldCenter);

                if (Scene != null)
                {
                    Scene.AddRigidBody(chunkEntity, worldCenter, rot, mass, false, false);
                    Scene.AddBoxCollider(chunkEntity, he, null, false);

                    // 飞溅冲量：从冲击点向外，带向上偏置
                    Vector3 dir = worldCenter - worldPoint;
                    if (dir.LengthSquared() < 1e-6f) dir = RandomUp(RNG);
                    dir += new Vector3(0f, 0.4f, 0f);
                    if (dir.LengthSquared() < 1e-6f) dir = Vector3.UnitY;
                    dir = Vector3.Normalize(dir);
                    float mag = impulse * ImpulseScale * (0.4f + (float)RNG.NextDouble() * 0.6f);
                    Scene.ApplyImpulse(chunkEntity, dir * mag);
                }

                _chunks[chunkEntity] = new ChunkRuntime
                {
                    EntityId = chunkEntity,
                    OwnerId = entityId,
                    HalfExtents = he,
                    Mass = mass,
                };
                spawned++;
            }

            // 粒子与音效
            SpawnEffects(worldPoint, impulse, mat);

            // 更新 DestructibleComponent 的碎屑计数（若存在）
            if (ecs.HasComponent<DestructibleComponent>(entityId))
            {
                var dc = ecs.GetComponent<DestructibleComponent>(entityId);
                dc.DebrisSpawned += mat.DebrisParticleCount;
                ecs.SetComponent(entityId, dc);
            }

            OnFractured?.Invoke(entityId, worldPoint, result);
        }

        /// <summary>播放破碎粒子与音效。</summary>
        private void SpawnEffects(Vector3 worldPoint, float impulse, DestructibleMaterial mat)
        {
            if (Particles != null)
            {
                Particles.SetPosition(worldPoint);
                int burst = Math.Max(4, mat.DebrisParticleCount);
                Particles.Emit(burst, worldPoint);
                if (!Particles.IsPlaying) Particles.Play();
            }

            if (Audio != null)
            {
                var clip = Audio.GetClip(FractureSoundName);
                if (clip == null)
                    clip = Audio.RegisterClip(FractureSoundName, "", 0.8f, AudioType.SFX);
                float vol = Math.Clamp(impulse / 50f, 0.1f, 1f);
                Audio.PlayOneShot(clip, worldPoint, vol, 1f);
            }
        }

        /// <summary>根据材质脆性与冲击力估算碎块数量。</summary>
        private static int ComputeChunkCount(DestructibleMaterial mat, float impulse)
        {
            int baseCount = 6;
            int extra = (int)Math.Round(mat.Brittleness * impulse * 0.1f);
            return Math.Clamp(baseCount + extra, 2, 32);
        }

        private static Vector3 RandomUp(Random rng)
        {
            float x = (float)(rng.NextDouble() * 2 - 1);
            float z = (float)(rng.NextDouble() * 2 - 1);
            return Vector3.Normalize(new Vector3(x, 1f, z));
        }

        // ----------------------------------------------------------------------------------------
        //  每帧更新：碎片生命周期（活跃 -> 休眠 -> 合并/移除）
        // ----------------------------------------------------------------------------------------

        /// <summary>
        /// 推进粒子与音频，并更新所有碎片的休眠/合并生命周期。
        /// </summary>
        public void Update(float dt, EntityComponentSystem ecs)
        {
            // 推进特效系统
            Particles?.Update(dt, CameraPosition);
            Audio?.Update(dt);

            if (_chunks.Count == 0) return;

            // 快照键集合（迭代中可能移除条目）
            var keys = new List<int>(_chunks.Keys);
            for (int i = 0; i < keys.Count; i++)
            {
                int id = keys[i];
                if (!_chunks.TryGetValue(id, out var chunk)) continue; // 可能已被合并/移除
                if (chunk.State == ChunkState.Removed || chunk.State == ChunkState.Merged) continue;

                chunk.Age += dt;

                if (chunk.State == ChunkState.Active)
                {
                    Vector3 vel = Scene != null ? (Vector3)Scene.GetLinearVelocity(id) : Vector3.Zero;
                    if (vel.Length() < SleepSpeedThreshold)
                    {
                        chunk.LowSpeedTimer += dt;
                        if (chunk.LowSpeedTimer >= SleepTimeRequired)
                        {
                            chunk.State = ChunkState.Sleeping;
                            chunk.SleepingTimer = 0f;
                        }
                    }
                    else
                    {
                        chunk.LowSpeedTimer = 0f;
                    }
                }
                else if (chunk.State == ChunkState.Sleeping)
                {
                    chunk.SleepingTimer += dt;
                    if (chunk.SleepingTimer >= SleepingLifetime)
                    {
                        MergeOrRemove(ecs, chunk);
                    }
                }
            }
        }

        /// <summary>
        /// 合并或移除休眠碎片：若同属主存在邻近休眠碎片，则将它们合并为一个静态刚体；
        /// 否则单独移除该碎片。
        /// </summary>
        private void MergeOrRemove(EntityComponentSystem ecs, ChunkRuntime chunk)
        {
            Vector3 myPos = Scene != null ? (Vector3)Scene.GetPosition(chunk.EntityId) : Vector3.Zero;

            var group = new List<ChunkRuntime>();
            foreach (var other in _chunks.Values)
            {
                if (other.State != ChunkState.Sleeping) continue;
                if (other.OwnerId != chunk.OwnerId) continue;
                Vector3 op = Scene != null ? (Vector3)Scene.GetPosition(other.EntityId) : Vector3.Zero;
                if (Vector3.Distance(myPos, op) <= MergeDistance) group.Add(other);
            }

            if (group.Count > 1 && Scene != null && ecs != null)
            {
                // 合并为一个静态刚体
                Vector3 acc = Vector3.Zero, accHe = Vector3.Zero;
                float totalMass = 0f;
                foreach (var g in group)
                {
                    acc += (Vector3)Scene.GetPosition(g.EntityId);
                    accHe += g.HalfExtents;
                    totalMass += g.Mass;
                }
                Vector3 centroid = acc / group.Count;
                Vector3 mergedHe = (accHe / group.Count) * 1.2f;

                int mergedEntity = ecs.CreateEntity($"Merged_{chunk.OwnerId}", centroid);
                Scene.AddRigidBody(mergedEntity, centroid, Quaternion.Identity, totalMass, false, true);
                Scene.AddBoxCollider(mergedEntity, mergedHe, null, false);

                foreach (var g in group)
                {
                    Scene.RemoveRigidBody(g.EntityId);
                    ecs.DestroyEntity(g.EntityId);
                    g.State = ChunkState.Merged;
                    _chunks.Remove(g.EntityId);
                    OnChunkMerged?.Invoke(g.OwnerId, g.EntityId);
                }
            }
            else
            {
                // 单独移除
                Scene?.RemoveRigidBody(chunk.EntityId);
                ecs?.DestroyEntity(chunk.EntityId);
                chunk.State = ChunkState.Removed;
                _chunks.Remove(chunk.EntityId);
            }
        }

        // ----------------------------------------------------------------------------------------
        //  坐标变换工具（TRS：translate * rotate * scale）
        // ----------------------------------------------------------------------------------------

        private static Vector3 LocalToWorld(Vector3 local, Vector3 pos, Quaternion rot, Vector3 scale)
            => pos + Vector3.Transform(local * scale, rot);

        private static Vector3 WorldToLocal(Vector3 world, Vector3 pos, Quaternion rot, Vector3 scale)
        {
            Vector3 invScale = new(
                scale.X != 0f ? 1f / scale.X : 1f,
                scale.Y != 0f ? 1f / scale.Y : 1f,
                scale.Z != 0f ? 1f / scale.Z : 1f);
            Vector3 rotated = Vector3.Transform(world - pos, Quaternion.Inverse(rot));
            return rotated * invScale;
        }
    }
}
