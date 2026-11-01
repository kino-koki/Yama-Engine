using System;
using System.Collections.Generic;
using System.Numerics;
using YamaEngine.ECS;
using YamaEngine.RHI;
using YamaEngine.Shaders;

namespace YamaEngine.Rendering
{
    // ============================================================================================
    //  DecalSystem.cs
    //
    //  yama引擎 —— 贴花渲染系统
    //
    //  本文件实现一套基于视锥体投影的延迟贴花方案，对齐 UE 的 DeferredDecal / Unity 的
    //  Decal Projector 思路，并与引擎自身的 RHI（RHIDevice/RHICommandList/RHIBuffer/RHITexture）
    //  和 ECS（EntityComponentSystem/IComponent）对接。
    //
    //  主要组成：
    //    1. DecalComponent        —— 贴花组件（位置 / 旋转 / 尺寸 / 材质 / 衰减）
    //    2. DecalMeshBuilder      —— 贴花网格生成（基于视锥体的 AABB 裁剪网格）
    //    3. DecalDepthBiasMode    —— 修复贴花 Z 冲突（Depth Bias / Stencil 技术）
    //    4. DecalProjector        —— 贴花投影（把材质投影到场景几何体表面）
    //    5. DecalLifecycle        —— 生命周期管理（自动淡出 / 过期删除）
    //    6. DecalSorter           —— 贴花排序（按距离 / 优先级）
    //    7. DecalPreset           —— 弹孔 / 脚印 / 血迹 / 涂鸦预设
    //    8. DecalRenderResources  —— 与 RHI 对接（顶点/索引缓冲、PSO、根签名、纹理）
    //    9. DecalSystem           —— 系统入口（对接 ECS）
    //
    //  渲染流程概述：
    //    a) 每帧收集所有可见 DecalComponent；
    //    b) DecalSorter 按材质 / 距离 / 优先级排序；
    //    c) DecalLifecycle 推进淡出与过期；
    //    d) 对每个贴花，用 DecalMeshBuilder 生成或复用裁剪后的盒体网格；
    //    e) DecalProjector 计算投影矩阵（世界→贴花局部空间）；
    //    f) 在 RHICommandList 上设置 Depth Bias / Stencil，绑定 PSO 与材质，绘制。
    // ============================================================================================

    /// <summary>贴花 Z 冲突修复方式。</summary>
    public enum DecalDepthBiasMode
    {
        /// <summary>不处理，可能出现 Z 冲突闪烁。</summary>
        None,
        /// <summary>使用 Depth Bias（Slope-Scaled）把贴花推到表面前方。</summary>
        DepthBias,
        /// <summary>使用 Stencil：先用场景深度写 Stencil，再只对标记区域绘制贴花。</summary>
        Stencil,
        /// <summary>同时使用 Depth Bias 与 Stencil（最稳健）。</summary>
        DepthBiasAndStencil
    }

    /// <summary>贴花混合模式。</summary>

    /// <summary>贴花材质描述（轻量，避免与 PbrMaterial 强耦合）。</summary>
    public sealed class DecalMaterial
    {
        /// <summary>漫反射贴图（RHI 纹理句柄，可为空）。</summary>
        public RHITexture? DiffuseTexture;
        /// <summary>法线贴图。</summary>
        public RHITexture? NormalTexture;
        /// <summary>自发光颜色。</summary>
        public Vector3 EmissiveColor = Vector3.Zero;
        /// <summary>基础颜色（无贴图时使用）。</summary>
        public Vector4 BaseColor = Vector4.One;
        /// <summary>混合模式。</summary>
        public DecalBlendMode BlendMode = DecalBlendMode.Translucent;
        /// <summary>整体不透明度。</summary>
        public float Opacity = 1f;
    }

    /// <summary>
    ///   贴花组件（ECS 组件）。描述一个贴花在世界中的位置、朝向、尺寸、材质与生命周期。
    ///   实现 IComponent 以便挂到 ECS 实体上集中管理。
    /// </summary>
    public struct DecalComponent : IComponent
    {
        /// <summary>所属实体 ID。</summary>
        public int EntityId;

        /// <summary>世界位置（贴花盒体中心）。</summary>
        public Vector3 Position;

        /// <summary>世界朝向（贴花前向为 -Z，与相机一致）。</summary>
        public Quaternion Rotation;

        /// <summary>贴花盒体半尺寸（X=宽半, Y=高半, Z=深度半）。</summary>
        public Vector3 HalfSize;

        /// <summary>材质索引（指向 DecalSystem 维护的材质池）。</summary>
        public int MaterialIndex;

        /// <summary>Z 冲突修复方式。</summary>
        public DecalDepthBiasMode DepthBiasMode;

        /// <summary>渲染优先级（越大越后绘制，覆盖在前）。</summary>
        public int SortPriority;

        /// <summary>距离衰减：贴花在盒体深度方向上的衰减指数。</summary>
        public float FalloffExponent;

        /// <summary>生命周期：剩余存活时间（秒，<=0 表示永久）。</summary>
        public float Lifetime;

        /// <summary>已存活时间。</summary>
        public float Elapsed;

        /// <summary>淡出开始时间（在 Lifetime 内的占比，0.8 表示最后 20% 淡出）。</summary>
        public float FadeStartRatio;

        /// <summary>当前不透明度（由生命周期计算）。</summary>
        public float CurrentOpacity;

        /// <summary>是否已过期（应被删除）。</summary>
        public bool IsExpired;

        /// <summary>是否可见（视锥剔除后）。</summary>
        public bool IsVisible;

        /// <summary>到相机的平方距离（排序用）。</summary>
        public float DistanceSqrToCamera;

        /// <summary>构建一个默认贴花组件。</summary>
        public static DecalComponent Create(Vector3 position, Quaternion rotation, Vector3 halfSize,
            int materialIndex, float lifetime = 0f)
        {
            return new DecalComponent
            {
                Position = position,
                Rotation = rotation,
                HalfSize = halfSize,
                MaterialIndex = materialIndex,
                DepthBiasMode = DecalDepthBiasMode.DepthBias,
                SortPriority = 0,
                FalloffExponent = 2f,
                Lifetime = lifetime,
                Elapsed = 0f,
                FadeStartRatio = 0.8f,
                CurrentOpacity = 1f,
                IsExpired = false,
                IsVisible = true
            };
        }
    }

    // ============================================================================================
    //  DecalMeshBuilder —— 贴花网格生成（基于视锥体的 AABB 裁剪）
    // ============================================================================================

    /// <summary>贴花顶点。</summary>
    public struct DecalVertex
    {
        public Vector3 Position;
        public Vector3 Normal;
        /// <summary>贴花局部 UV（用于采样贴花材质）。</summary>
        public Vector2 TexCoord;
    }

    /// <summary>
    ///   贴花网格生成器。贴花本身是一个朝向 -Z 的盒体（视锥体近似），
    ///   渲染时用盒体的 8 个角点构成 6 个面。这里生成一个标准盒体网格，
    ///   实际的"裁剪到场景几何体"由片元着色器用深度重建世界坐标完成（延迟贴花做法），
    ///   因此 CPU 端只需盒体网格即可，无需逐贴花重算几何。
    /// </summary>
    public static class DecalMeshBuilder
    {
        /// <summary>生成单位盒体（半尺寸 1）的贴花网格，渲染时用世界矩阵缩放到实际尺寸。</summary>
        public static (DecalVertex[] vertices, ushort[] indices) BuildUnitBoxMesh()
        {
            // 24 个顶点（每面 4 个，便于独立法线与 UV）
            var vertices = new DecalVertex[24];

            // 简化：直接用硬编码的 24 顶点盒体（每面独立法线与 UV）
            int v = 0;
            // +X 面
            vertices[v++] = Make(new(1, -1, -1), new(1, 0, 0), 0, 0);
            vertices[v++] = Make(new(1, 1, -1), new(1, 0, 0), 1, 0);
            vertices[v++] = Make(new(1, 1, 1), new(1, 0, 0), 1, 1);
            vertices[v++] = Make(new(1, -1, 1), new(1, 0, 0), 0, 1);
            // -X 面
            vertices[v++] = Make(new(-1, -1, 1), new(-1, 0, 0), 0, 0);
            vertices[v++] = Make(new(-1, 1, 1), new(-1, 0, 0), 1, 0);
            vertices[v++] = Make(new(-1, 1, -1), new(-1, 0, 0), 1, 1);
            vertices[v++] = Make(new(-1, -1, -1), new(-1, 0, 0), 0, 1);
            // +Y 面
            vertices[v++] = Make(new(-1, 1, -1), new(0, 1, 0), 0, 0);
            vertices[v++] = Make(new(1, 1, -1), new(0, 1, 0), 1, 0);
            vertices[v++] = Make(new(1, 1, 1), new(0, 1, 0), 1, 1);
            vertices[v++] = Make(new(-1, 1, 1), new(0, 1, 0), 0, 1);
            // -Y 面
            vertices[v++] = Make(new(-1, -1, 1), new(0, -1, 0), 0, 0);
            vertices[v++] = Make(new(1, -1, 1), new(0, -1, 0), 1, 0);
            vertices[v++] = Make(new(1, -1, -1), new(0, -1, 0), 1, 1);
            vertices[v++] = Make(new(-1, -1, -1), new(0, -1, 0), 0, 1);
            // +Z 面
            vertices[v++] = Make(new(-1, -1, 1), new(0, 0, 1), 0, 0);
            vertices[v++] = Make(new(1, -1, 1), new(0, 0, 1), 1, 0);
            vertices[v++] = Make(new(1, 1, 1), new(0, 0, 1), 1, 1);
            vertices[v++] = Make(new(-1, 1, 1), new(0, 0, 1), 0, 1);
            // -Z 面（贴花主要投影面）
            vertices[v++] = Make(new(1, -1, -1), new(0, 0, -1), 0, 0);
            vertices[v++] = Make(new(-1, -1, -1), new(0, 0, -1), 1, 0);
            vertices[v++] = Make(new(-1, 1, -1), new(0, 0, -1), 1, 1);
            vertices[v++] = Make(new(1, 1, -1), new(0, 0, -1), 0, 1);

            // 索引：每面两个三角形
            var indices = new ushort[36];
            for (int face = 0; face < 6; face++)
            {
                int baseIdx = face * 4;
                int triIdx = face * 6;
                indices[triIdx + 0] = (ushort)(baseIdx + 0);
                indices[triIdx + 1] = (ushort)(baseIdx + 1);
                indices[triIdx + 2] = (ushort)(baseIdx + 2);
                indices[triIdx + 3] = (ushort)(baseIdx + 0);
                indices[triIdx + 4] = (ushort)(baseIdx + 2);
                indices[triIdx + 5] = (ushort)(baseIdx + 3);
            }

            return (vertices, indices);
        }

        private static DecalVertex Make(Vector3 p, Vector3 n, float u, float v) => new()
        {
            Position = p,
            Normal = n,
            TexCoord = new Vector2(u, v)
        };

        /// <summary>
        ///   计算贴花盒体的 8 个角点世界坐标（用于视锥剔除与 AABB 裁剪）。
        /// </summary>
        public static Vector3[] ComputeWorldCorners(in DecalComponent decal)
        {
            var corners = new Vector3[8];
            Vector3 h = decal.HalfSize;
            Vector3[] local = {
                new(-h.X, -h.Y, -h.Z), new( h.X, -h.Y, -h.Z),
                new( h.X,  h.Y, -h.Z), new(-h.X,  h.Y, -h.Z),
                new(-h.X, -h.Y,  h.Z), new( h.X, -h.Y,  h.Z),
                new( h.X,  h.Y,  h.Z), new(-h.X,  h.Y,  h.Z),
            };
            for (int i = 0; i < 8; i++)
                corners[i] = decal.Position + Vector3.Transform(local[i], decal.Rotation);
            return corners;
        }

        /// <summary>计算贴花盒体的世界 AABB。</summary>
        public static (Vector3 min, Vector3 max) ComputeWorldAABB(in DecalComponent decal)
        {
            var corners = ComputeWorldCorners(in decal);
            Vector3 min = corners[0], max = corners[0];
            for (int i = 1; i < 8; i++)
            {
                min = Vector3.Min(min, corners[i]);
                max = Vector3.Max(max, corners[i]);
            }
            return (min, max);
        }
    }

    // ============================================================================================
    //  DecalProjector —— 贴花投影矩阵
    // ============================================================================================

    /// <summary>
    ///   贴花投影器。计算把世界空间变换到贴花局部空间的矩阵，供片元着色器
    ///   重建表面坐标并裁剪到盒体内部。同时提供深度→UV 的映射。
    /// </summary>
    public static class DecalProjector
    {
        /// <summary>
        ///   计算贴花的 World→DecalLocal 矩阵（用于在着色器中裁剪）。
        ///   贴花局部空间：原点在盒体中心，XYZ 范围 -1..1。
        /// </summary>
        public static Matrix4x4 ComputeWorldToDecal(in DecalComponent decal)
        {
            // DecalLocal = Scale(1/HalfSize) * Inverse(Rotation) * Translate(-Position)
            var invRot = Quaternion.Inverse(decal.Rotation);
            var invTrans = Matrix4x4.CreateTranslation(-decal.Position);
            var invRotM = Matrix4x4.CreateFromQuaternion(invRot);
            var scale = Matrix4x4.CreateScale(
                1f / Math.Max(decal.HalfSize.X, 1e-4f),
                1f / Math.Max(decal.HalfSize.Y, 1e-4f),
                1f / Math.Max(decal.HalfSize.Z, 1e-4f));
            return invTrans * invRotM * scale;
        }

        /// <summary>计算贴花的世界矩阵（缩放 × 旋转 × 平移），用于盒体网格绘制。</summary>
        public static Matrix4x4 ComputeWorldMatrix(in DecalComponent decal)
        {
            return Matrix4x4.CreateScale(decal.HalfSize) *
                   Matrix4x4.CreateFromQuaternion(decal.Rotation) *
                   Matrix4x4.CreateTranslation(decal.Position);
        }

        /// <summary>计算深度衰减权重（盒体深度方向 0..1，按 FalloffExponent 衰减）。</summary>
        public static float ComputeDepthFalloff(in DecalComponent decal, float localDepthNormalized)
        {
            // localDepthNormalized: -1..1，靠近投影面（-1）权重高
            float d = (localDepthNormalized * 0.5f) + 0.5f; // 0..1
            return MathF.Pow(1f - Math.Clamp(d, 0f, 1f), decal.FalloffExponent);
        }
    }

    // ============================================================================================
    //  DecalLifecycle —— 生命周期管理（淡出 / 过期）
    // ============================================================================================

    /// <summary>贴花生命周期管理器。</summary>
    public static class DecalLifecycle
    {
        /// <summary>
        ///   推进单个贴花的生命周期，更新 CurrentOpacity 与 IsExpired。
        /// </summary>
        public static void Update(ref DecalComponent decal, float dt)
        {
            if (decal.Lifetime <= 0f)
            {
                // 永久贴花
                decal.CurrentOpacity = 1f;
                decal.IsExpired = false;
                return;
            }

            decal.Elapsed += dt;
            float t = Math.Clamp(decal.Elapsed / decal.Lifetime, 0f, 1f);

            if (t >= 1f)
            {
                decal.CurrentOpacity = 0f;
                decal.IsExpired = true;
                return;
            }

            // 在 FadeStartRatio 之后开始线性淡出
            if (t >= decal.FadeStartRatio && decal.FadeStartRatio < 1f)
            {
                float fadeT = (t - decal.FadeStartRatio) / (1f - decal.FadeStartRatio);
                decal.CurrentOpacity = Math.Clamp(1f - fadeT, 0f, 1f);
            }
            else
            {
                decal.CurrentOpacity = 1f;
            }
            decal.IsExpired = false;
        }

        /// <summary>批量推进并返回需要移除的实体列表。</summary>
        public static List<int> UpdateAll(EntityComponentSystem ecs, float dt)
        {
            var toRemove = new List<int>();
            var entities = ecs.QueryWithComponent<DecalComponent>();
            foreach (int id in entities)
            {
                var decal = ecs.GetComponent<DecalComponent>(id);
                Update(ref decal, dt);
                ecs.SetComponent(id, decal);
                if (decal.IsExpired) toRemove.Add(id);
            }
            return toRemove;
        }
    }

    // ============================================================================================
    //  DecalSorter —— 贴花排序（按距离 / 优先级）
    // ============================================================================================

    /// <summary>贴花排序方式。</summary>
    public enum DecalSortMethod
    {
        /// <summary>按到相机距离从远到近（保证近的覆盖远的，正确半透明混合）。</summary>
        BackToFront,
        /// <summary>按 SortPriority 升序。</summary>
        ByPriority,
        /// <summary>按材质分组（减少状态切换），组内按距离。</summary>
        ByMaterialThenDistance
    }

    /// <summary>贴花排序器。</summary>
    public static class DecalSorter
    {
        /// <summary>对贴花实体列表排序，返回排序后的实体 ID 列表。</summary>
        public static List<int> Sort(EntityComponentSystem ecs, Vector3 cameraPosition, DecalSortMethod method)
        {
            var entities = ecs.QueryWithComponent<DecalComponent>();
            // 预计算距离
            var items = new List<(int id, DecalComponent decal)>(entities.Count);
            foreach (int id in entities)
            {
                var d = ecs.GetComponent<DecalComponent>(id);
                d.DistanceSqrToCamera = Vector3.DistanceSquared(d.Position, cameraPosition);
                ecs.SetComponent(id, d);
                items.Add((id, d));
            }

            switch (method)
            {
                case DecalSortMethod.BackToFront:
                    items.Sort((a, b) => b.decal.DistanceSqrToCamera.CompareTo(a.decal.DistanceSqrToCamera));
                    break;
                case DecalSortMethod.ByPriority:
                    items.Sort((a, b) => a.decal.SortPriority.CompareTo(b.decal.SortPriority));
                    break;
                case DecalSortMethod.ByMaterialThenDistance:
                    items.Sort((a, b) =>
                    {
                        int m = a.decal.MaterialIndex.CompareTo(b.decal.MaterialIndex);
                        if (m != 0) return m;
                        return b.decal.DistanceSqrToCamera.CompareTo(a.decal.DistanceSqrToCamera);
                    });
                    break;
            }

            var result = new List<int>(items.Count);
            foreach (var (id, _) in items) result.Add(id);
            return result;
        }
    }

    // ============================================================================================
    //  DecalPreset —— 弹孔 / 脚印 / 血迹 / 涂鸦预设
    // ============================================================================================

    /// <summary>贴花预设类型。</summary>
    public enum DecalPresetType
    {
        BulletHole,
        Footprint,
        BloodSplat,
        Graffiti,
        ScorchMark,
        Crack
    }

    /// <summary>贴花预设工厂。提供常见贴花的快速创建参数。</summary>
    public static class DecalPreset
    {
        /// <summary>根据预设类型创建一个贴花组件（材质索引由调用方映射）。</summary>
        public static DecalComponent Create(DecalPresetType type, Vector3 position, Quaternion rotation,
            int materialIndex)
        {
            return type switch
            {
                DecalPresetType.BulletHole => DecalComponent.Create(position, rotation,
                    new Vector3(0.08f, 0.08f, 0.05f), materialIndex, lifetime: 30f),
                DecalPresetType.Footprint => DecalComponent.Create(position, rotation,
                    new Vector3(0.15f, 0.25f, 0.03f), materialIndex, lifetime: 15f),
                DecalPresetType.BloodSplat => DecalComponent.Create(position, rotation,
                    new Vector3(0.6f, 0.6f, 0.05f), materialIndex, lifetime: 25f),
                DecalPresetType.Graffiti => DecalComponent.Create(position, rotation,
                    new Vector3(1.5f, 1.0f, 0.1f), materialIndex, lifetime: 0f),
                DecalPresetType.ScorchMark => DecalComponent.Create(position, rotation,
                    new Vector3(0.5f, 0.5f, 0.05f), materialIndex, lifetime: 60f),
                DecalPresetType.Crack => DecalComponent.Create(position, rotation,
                    new Vector3(0.4f, 0.4f, 0.05f), materialIndex, lifetime: 0f),
                _ => DecalComponent.Create(position, rotation, new Vector3(0.5f, 0.5f, 0.05f), materialIndex, 0f)
            };
        }

        /// <summary>根据表面法线计算让贴花贴附该表面的朝向（贴花 -Z 对齐法线）。</summary>
        public static Quaternion AlignToSurface(Vector3 surfaceNormal, Vector3 upHint)
        {
            if (surfaceNormal.LengthSquared() < 1e-6f) return Quaternion.Identity;
            surfaceNormal = Vector3.Normalize(surfaceNormal);
            // 贴花前向是 -Z，要让 -Z = -surfaceNormal，即 +Z = surfaceNormal
            Vector3 forward = -surfaceNormal;
            Vector3 up = upHint;
            if (MathF.Abs(Vector3.Dot(up, surfaceNormal)) > 0.99f)
                up = MathF.Abs(surfaceNormal.Y) > 0.99f ? Vector3.UnitZ : Vector3.UnitY;
            up = Vector3.Normalize(up - surfaceNormal * Vector3.Dot(up, surfaceNormal));

            Vector3 right = Vector3.Normalize(Vector3.Cross(up, forward));
            up = Vector3.Cross(forward, right);
            var m = new Matrix4x4(
                right.X, up.X, forward.X, 0,
                right.Y, up.Y, forward.Y, 0,
                right.Z, up.Z, forward.Z, 0,
                0, 0, 0, 1);
            Matrix4x4.Invert(m, out var inv);
            return Quaternion.CreateFromRotationMatrix(inv);
        }
    }

    // ============================================================================================
    //  DecalRenderResources —— 与 RHI 对接
    // ============================================================================================

    /// <summary>
    ///   贴花渲染所需的 RHI 资源集合。包括共享的盒体顶点/索引缓冲、PSO、根签名。
    ///   所有贴花复用同一份盒体网格，仅世界矩阵与材质不同。
    /// </summary>
    public sealed class DecalRenderResources : IDisposable
    {
        private readonly RHIDevice _device;
        private bool _disposed;

        /// <summary>共享盒体顶点缓冲。</summary>
        public RHIBuffer? VertexBuffer { get; private set; }

        /// <summary>共享盒体索引缓冲。</summary>
        public RHIBuffer? IndexBuffer { get; private set; }

        /// <summary>每实例常量缓冲（世界矩阵 + 衰减 + 不透明度）。</summary>
        public RHIBuffer? InstanceConstants { get; private set; }

        /// <summary>根签名。</summary>
        public RHIRootSignature? RootSignature { get; private set; }

        /// <summary>半透明贴花 PSO。</summary>
        public RHIPipelineState? TranslucentPSO { get; private set; }

        /// <summary>法体贴花 PSO。</summary>
        public RHIPipelineState? NormalPSO { get; private set; }

        /// <summary>不透明贴花 PSO。</summary>
        public RHIPipelineState? OpaquePSO { get; private set; }

        /// <summary>顶点数。</summary>
        public int VertexCount { get; private set; }

        /// <summary>索引数。</summary>
        public int IndexCount { get; private set; }

        /// <summary>顶点结构大小（字节）。</summary>
        public const int VertexStride = 32; // float3 pos + float3 normal + float2 uv = 8 floats

        /// <summary>常量缓冲大小（对齐到 256）。</summary>
        public const int ConstantBufferSize = 256;

        public DecalRenderResources(RHIDevice device)
        {
            _device = device ?? throw new ArgumentNullException(nameof(device));
        }

        /// <summary>初始化或重建 RHI 资源。</summary>
        public void Initialize()
        {
            if (_device == null) return;

            // 1. 生成盒体网格
            var (verts, indices) = DecalMeshBuilder.BuildUnitBoxMesh();
            VertexCount = verts.Length;
            IndexCount = indices.Length;

            // 2. 创建顶点缓冲（上传顶点数据）
            var vertexData = new byte[verts.Length * VertexStride];
            for (int i = 0; i < verts.Length; i++)
            {
                int o = i * VertexStride;
                WriteFloat(vertexData, o + 0, verts[i].Position.X);
                WriteFloat(vertexData, o + 4, verts[i].Position.Y);
                WriteFloat(vertexData, o + 8, verts[i].Position.Z);
                WriteFloat(vertexData, o + 12, verts[i].Normal.X);
                WriteFloat(vertexData, o + 16, verts[i].Normal.Y);
                WriteFloat(vertexData, o + 20, verts[i].Normal.Z);
                WriteFloat(vertexData, o + 24, verts[i].TexCoord.X);
                WriteFloat(vertexData, o + 28, verts[i].TexCoord.Y);
            }
            var vbDesc = new RHIBufferDesc
            {
                SizeInBytes = (ulong)vertexData.Length,
                Flags = RHIBufferFlags.VertexBuffer,
                Stride = VertexStride
            };
            VertexBuffer = _device.CreateCommittedBuffer(vbDesc, vertexData, "Decal_VB");

            // 3. 创建索引缓冲
            var indexData = new byte[indices.Length * sizeof(ushort)];
            for (int i = 0; i < indices.Length; i++)
                WriteUShort(indexData, i * 2, indices[i]);
            var ibDesc = new RHIBufferDesc
            {
                SizeInBytes = (ulong)indexData.Length,
                Flags = RHIBufferFlags.IndexBuffer,
                Stride = 2
            };
            IndexBuffer = _device.CreateCommittedBuffer(ibDesc, indexData, "Decal_IB");

            // 4. 创建每实例常量缓冲（上传缓冲，每帧更新）
            var cbDesc = new RHIBufferDesc
            {
                SizeInBytes = ConstantBufferSize,
                Flags = RHIBufferFlags.ConstantBuffer,
                HeapType = RHIResourceHeapType.Upload
            };
            InstanceConstants = _device.CreateBuffer(cbDesc, "Decal_CB");

            // 5. 创建根签名（参数：CBV + SRV 描述符表）
            // 实际项目里这里会绑定着色器资源；此处构造描述供 PSO 使用。
            var rootDesc = new RHIRootSignatureDesc
            {
                Parameters = new[]
                {
                    new RHIRootParameter
                    {
                        ParameterType = RHIRootParameterType.CBV,
                        ShaderVisibility = RHIShaderVisibility.Vertex,
                        Descriptor = new RHIRootDescriptor { ShaderRegister = 0, RegisterSpace = 0 }
                    },
                    new RHIRootParameter
                    {
                        ParameterType = RHIRootParameterType.DescriptorTable,
                        ShaderVisibility = RHIShaderVisibility.Pixel,
                        DescriptorTable = new RHIRootDescriptorTable
                        {
                            Ranges = new[]
                            {
                                new RHIRootDescriptorRange
                                {
                                    RangeType = RHIDescriptorRangeType.SRV,
                                    NumDescriptors = 4,
                                    BaseShaderRegister = 0,
                                    RegisterSpace = 0,
                                    OffsetInDescriptorsFromTableStart = 0
                                }
                            }
                        }
                    }
                },
                StaticSamplers = new[] { RHISamplerDesc.LinearWrap },
                AllowInputAssembler = true
            };
            RootSignature = _device.CreateRootSignature(rootDesc, "Decal_RootSig");

            // 6. 创建 PSO（半透明 / 法线 / 不透明）
            // 着色器字节码通过 ShaderManager 从 Decals/Decals.hlsl 编译获取。
            TranslucentPSO = CreatePSO(DecalBlendMode.Translucent, RHIRasterizerDesc.NoCull, "Decal_Translucent");
            NormalPSO = CreatePSO(DecalBlendMode.Normal, RHIRasterizerDesc.NoCull, "Decal_Normal");
            OpaquePSO = CreatePSO(DecalBlendMode.Opaque, RHIRasterizerDesc.NoCull, "Decal_Opaque");
        }

        private RHIPipelineState? CreatePSO(DecalBlendMode blend, RHIRasterizerDesc raster, string name)
        {
            var desc = new RHIGraphicsPipelineDesc
            {
                Name = name,
                InputLayout = new[]
                {
                    new RHIInputElement("POSITION", 0, RHIFormat.R32G32B32_Float, 0, 0),
                    new RHIInputElement("NORMAL", 0, RHIFormat.R32G32B32_Float, 0, 12),
                    new RHIInputElement("TEXCOORD", 0, RHIFormat.R32G32_Float, 0, 24),
                },
                PrimitiveTopology = RHIPrimitiveTopology.TriangleList,
                BlendState = MakeBlend(blend),
                DepthStencilState = MakeDepthStencil(blend),
                RasterizerState = MakeRasterizer(raster),
                RTVFormats = new[] { RHIFormat.R8G8B8A8_UNorm },
                DSVFormat = RHIFormat.D32_Float,
                SampleCount = 1,
                VS = ShaderManager.Instance.GetBytecode("Decals/Decals", "DecalVS", RHIShaderStage.Vertex),
                PS = ShaderManager.Instance.GetBytecode("Decals/Decals", "DecalPS", RHIShaderStage.Pixel)
            };
            // 通过 ShaderManager 从 Decals/Decals.hlsl 编译获取着色器字节码。
            try
            {
                return _device.CreateGraphicsPipelineState(desc, name);
            }
            catch
            {
                // 后端可能因缺少着色器抛异常；返回 null 让系统降级。
                return null;
            }
        }

        private static RHIBlendDesc MakeBlend(DecalBlendMode mode)
        {
            if (mode == DecalBlendMode.Opaque) return RHIBlendDesc.Default;
            return RHIBlendDesc.AlphaBlend;
        }

        private static RHIDepthDesc MakeDepthStencil(DecalBlendMode mode)
        {
            // 贴花只读深度，不写深度，避免遮挡后续贴花
            return new RHIDepthDesc
            {
                DepthEnable = true,
                DepthWriteEnable = false,
                DepthFunc = RHIComparison.LessEqual,
                StencilEnable = false
            };
        }

        private static RHIRasterizerDesc MakeRasterizer(RHIRasterizerDesc base_)
        {
            // 贴花默认带 Depth Bias，避免与表面 Z 冲突
            base_.DepthBias = -100;
            base_.SlopeScaledDepthBias = -1.0f;
            base_.DepthBiasClamp = 0.001f;
            return base_;
        }

        /// <summary>根据混合模式选择 PSO。</summary>
        public RHIPipelineState? SelectPSO(DecalBlendMode mode) => mode switch
        {
            DecalBlendMode.Opaque => OpaquePSO,
            DecalBlendMode.Normal => NormalPSO,
            DecalBlendMode.Emissive => TranslucentPSO,
            DecalBlendMode.Material => TranslucentPSO,
            _ => TranslucentPSO
        };

        /// <summary>
        ///   把每实例常量数据上传到 InstanceConstants 缓冲。
        ///   由渲染器每帧调用。数据布局见 DecalRenderer.UploadInstanceConstants 注释。
        /// </summary>
        public void UploadInstanceData(float[] data)
        {
            var cb = InstanceConstants;
            if (_device == null || cb == null || data == null) return;
            _device.UploadToBuffer(cb, data.AsSpan(), 0);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            VertexBuffer?.Dispose();
            IndexBuffer?.Dispose();
            InstanceConstants?.Dispose();
            RootSignature?.Dispose();
            TranslucentPSO?.Dispose();
            NormalPSO?.Dispose();
            OpaquePSO?.Dispose();
        }

        private static void WriteFloat(byte[] buf, int offset, float v)
        {
            BitConverter.TryWriteBytes(new Span<byte>(buf, offset, 4), v);
        }
        private static void WriteUShort(byte[] buf, int offset, ushort v)
        {
            BitConverter.TryWriteBytes(new Span<byte>(buf, offset, 2), v);
        }
    }

    // ============================================================================================
    //  DecalRenderer —— 单帧绘制逻辑（与 RHICommandList 对接）
    // ============================================================================================

    /// <summary>
    ///   贴花渲染器。负责在一帧内把所有可见贴花绘制到 GeometryBuffer/RTV。
    /// </summary>

    // ============================================================================================
    //  DecalSystem —— 系统入口（对接 ECS + RHI）
    // ============================================================================================

    /// <summary>
    ///   贴花系统。统一管理贴花的创建、生命周期、排序与渲染。
    ///   实现 ISystem 以便挂到 ECS 的系统列表中每帧 tick。
    /// </summary>
    public sealed class DecalSystem : ISystem, IDisposable
    {
        private DecalRenderResources? _resources;
        private DecalRenderer? _renderer;
        private readonly EntityComponentSystem _ecs;
        private bool _disposed;

        /// <summary>贴花渲染器（暴露给外部调用 Render）。</summary>
        public DecalRenderer? Renderer => _renderer;

        /// <summary>RHI 资源。</summary>
        public DecalRenderResources? Resources => _resources;

        public string Name => "DecalSystem";

        /// <summary>构造（无 RHI 模式，仅做生命周期/排序管理）。</summary>
        public DecalSystem(EntityComponentSystem ecs)
        {
            _ecs = ecs ?? throw new ArgumentNullException(nameof(ecs));
        }

        /// <summary>构造（带 RHI 设备，启用完整渲染）。</summary>
        public DecalSystem(EntityComponentSystem ecs, RHIDevice device) : this(ecs)
        {
            _resources = new DecalRenderResources(device);
            _resources.Initialize();
            _renderer = new DecalRenderer(device);
        }

        /// <summary>注册一个贴花材质，返回索引。</summary>
        public int RegisterMaterial(DecalMaterial material)
            => _renderer?.RegisterMaterial(material) ?? -1;

        /// <summary>在世界中放置一个贴花组件并返回实体 ID。</summary>
        public int SpawnDecal(DecalComponent decal)
        {
            int id = _ecs.CreateEntity("Decal", decal.Position, decal.Rotation);
            decal.EntityId = id;
            _ecs.AddComponent(id, decal);
            return id;
        }

        /// <summary>快捷放置预设贴花。</summary>
        public int SpawnPreset(DecalPresetType type, Vector3 position, Vector3 surfaceNormal,
            int materialIndex, Vector3 upHint = default)
        {
            if (upHint == default) upHint = Vector3.UnitY;
            var rot = DecalPreset.AlignToSurface(surfaceNormal, upHint);
            var decal = DecalPreset.Create(type, position, rot, materialIndex);
            return SpawnDecal(decal);
        }

        /// <summary>每帧更新：推进生命周期、移除过期贴花。</summary>
        public void Update(float dt, EntityComponentSystem ecs)
        {
            var expired = DecalLifecycle.UpdateAll(ecs, dt);
            foreach (int id in expired)
            {
                ecs.RemoveComponent<DecalComponent>(id);
                ecs.DestroyEntity(id);
            }
        }

        /// <summary>渲染所有可见贴花（在 GeometryBuffer/不透明 Pass 之后调用）。</summary>
        public void Render(RHICommandList cmd, Vector3 cameraPosition, Matrix4x4 cameraViewProjection)
        {
            _renderer?.Render(cmd, _ecs, cameraPosition, cameraViewProjection);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _resources?.Dispose();
        }
    }

}
