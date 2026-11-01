using System;
using System.Collections.Generic;
using System.Numerics;

namespace YamaEngine.VFX
{
    // ════════════════════════════════════════════════════════════════════
    // yama引擎 - 粒子渲染系统
    // --------------------------------------------------------------------
    // 设计理念：
    //   - ParticleRenderer     : 粒子渲染器（排序/批量渲染/GPU实例化）
    //   - ParticleRenderMode   : 渲染模式（Sprite/Mesh/Ribbon/Line/Trail/Volumetric）
    //   - 排序模式             : ViewSort/DistanceSort/ProjectDepthSort/None
    //   - ParticleBlendMode    : 混合模式（Alpha/PreMultiplied/Additive/Multiply等）
    //   - ParticleLighting     : 光照模式（Unlit/Lit/Subsurface/Deferred）
    //   - GPUParticleSystem    : GPU粒子（Compute Shader模拟/间接渲染/百万粒子）
    //   - TextureAtlas        : 纹理图集（多个精灵打包到一张纹理）
    //   - 粒子LOD             : 距离->简化/减少粒子数/简化着色器
    // ════════════════════════════════════════════════════════════════════

    // ──────────────────────────────────────────────────────────────────
    // 枚举定义
    // ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// 粒子渲染模式。
    /// </summary>
    public enum ParticleRenderMode
    {
        /// <summary>精灵渲染（Billboard 四边形）。</summary>
        Sprite,
        /// <summary>网格粒子渲染。</summary>
        Mesh,
        /// <summary>Ribbon（彩带）渲染。</summary>
        Ribbon,
        /// <summary>线段渲染。</summary>
        Line,
        /// <summary>拖尾渲染。</summary>
        Trail,
        /// <summary>体积渲染（体素化/光线步进）。</summary>
        Volumetric
    }

    /// <summary>
    /// 粒子排序模式。
    /// </summary>
    public enum ParticleSortMode
    {
        /// <summary>按视角方向深度排序（从后到前）。</summary>
        ViewSort,
        /// <summary>按距摄像机距离排序。</summary>
        DistanceSort,
        /// <summary>按投影深度排序。</summary>
        ProjectDepthSort,
        /// <summary>不排序。</summary>
        None
    }

    /// <summary>
    /// 粒子混合模式。
    /// </summary>
    public enum ParticleBlendMode
    {
        /// <summary>标准 Alpha 混合。</summary>
        Alpha,
        /// <summary>预乘 Alpha 混合。</summary>
        Premultiplied,
        /// <summary>加法混合（叠加发光效果）。</summary>
        Additive,
        /// <summary>乘法混合。</summary>
        Multiply,
        /// <summary>减法混合。</summary>
        Subtractive,
        /// <summary>叠加混合。</summary>
        Overlay,
        /// <summary>不透明渲染。</summary>
        Opaque
    }

    /// <summary>
    /// 粒子光照模式。
    /// </summary>
    public enum ParticleLighting
    {
        /// <summary>无光照（自发光）。</summary>
        Unlit,
        /// <summary>受场景光照影响。</summary>
        Lit,
        /// <summary>次表面散射。</summary>
        Subsurface,
        /// <summary>延迟渲染通道写入。</summary>
        Deferred
    }

    // ──────────────────────────────────────────────────────────────────
    // 纹理图集
    // ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// 纹理图集：将多个粒子精灵打包到一张纹理中。
    /// </summary>
    public sealed class TextureAtlas
    {
        /// <summary>图集名称。</summary>
        public string Name = "";
        /// <summary>图集宽度（像素）。</summary>
        public int Width = 512;
        /// <summary>图集高度（像素）。</summary>
        public int Height = 512;
        /// <summary>列数。</summary>
        public int Columns = 4;
        /// <summary>行数。</summary>
        public int Rows = 4;
        /// <summary>子纹理数量。</summary>
        public int SubTextureCount => Columns * Rows;
        /// <summary>纹理资源句柄。</summary>
        public object? Texture;
        /// <summary>各子纹理的UV信息。</summary>
        public List<TextureAtlasEntry> Entries = new();
        /// <summary>是否使用纹理数组代替图集。</summary>
        public bool UseTextureArray = false;

        /// <summary>
        /// 根据索引获取子纹理 UV。
        /// </summary>
        public void GetSubTextureUV(int index, out Vector2 uvOffset, out Vector2 uvScale)
        {
            int col = index % Columns;
            int row = index / Columns;
            uvOffset = new Vector2(col / (float)Columns, row / (float)Rows);
            uvScale = new Vector2(1f / Columns, 1f / Rows);
        }
    }

    /// <summary>
    /// 图集子纹理条目。
    /// </summary>
    public sealed class TextureAtlasEntry
    {
        /// <summary>索引。</summary>
        public int Index;
        /// <summary>名称。</summary>
        public string Name = "";
        /// <summary>UV 偏移。</summary>
        public Vector2 UVOffset;
        /// <summary>UV 缩放。</summary>
        public Vector2 UVScale;
        /// <summary>子纹理宽度（像素）。</summary>
        public int Width;
        /// <summary>子纹理高度（像素）。</summary>
        public int Height;
    }

    // ──────────────────────────────────────────────────────────────────
    // 粒子LOD
    // ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// 粒子LOD配置。
    /// </summary>
    public sealed class ParticleLOD
    {
        /// <summary>LOD 等级。</summary>
        public int Level;
        /// <summary>距离阈值。</summary>
        public float DistanceThreshold;
        /// <summary>粒子数倍率（0-1）。</summary>
        public float ParticleMultiplier = 1f;
        /// <summary>是否启用简化着色器。</summary>
        public bool UseSimplifiedShader = false;
        /// <summary>排序模式降级（高LOD不排序）。</summary>
        public ParticleSortMode? OverrideSortMode;
        /// <summary>是否禁用拖尾/Ribbon。</summary>
        public bool DisableTrails = false;
        /// <summary>是否禁用光照。</summary>
        public bool DisableLighting = false;
    }

    // ──────────────────────────────────────────────────────────────────
    // GPU 粒子系统
    // ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// GPU 粒子系统：使用 Compute Shader 在 GPU 上模拟百万级粒子。
    /// </summary>
    public sealed class GPUParticleSystem
    {
        /// <summary>系统名称。</summary>
        public string Name = "GPUParticles";
        /// <summary>是否启用。</summary>
        public bool Enabled = true;
        /// <summary>最大粒子数（通常百万级）。</summary>
        public int MaxParticles = 1_000_000;
        /// <summary>Compute Shader 资源句柄。</summary>
        public object? ComputeShader;
        /// <summary>渲染着色器句柄。</summary>
        public object? RenderShader;
        /// <summary>粒子缓冲区句柄（GPU 端 StructuredBuffer）。</summary>
        public object? ParticleBuffer;
        /// <summary>间接参数缓冲区（DrawIndirect）。</summary>
        public object? IndirectArgsBuffer;
        /// <summary>发射参数（传递给 Compute Shader）。</summary>
        public GPUEmitParameters EmitParams = new();
        /// <summary>模拟参数。</summary>
        public GPUSimParameters SimParams = new();
        /// <summary>是否使用间接渲染。</summary>
        public bool UseIndirectRendering = true;
        /// <summary>当前活跃粒子数（GPU 回读）。</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public int ActiveParticleCount;
        /// <summary>是否需要回读粒子计数。</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public bool NeedsCountReadback;

        /// <summary>
        /// 初始化 GPU 粒子系统资源。
        /// </summary>
        public void Initialize() { /* 需要RHI支持：创建Buffer/ComputePSO */ }

        /// <summary>
        /// 发射粒子（更新发射参数并 Dispatch Compute Shader）。
        /// </summary>
        public void Emit(int count) { ActiveParticleCount = Math.Min(ActiveParticleCount + count, MaxParticles); }

        /// <summary>
        /// 模拟一帧（Dispatch Compute Shader）。
        /// </summary>
        public void Simulate(float deltaTime) { /* 需要RHI：Dispatch Compute */ }

        /// <summary>
        /// 渲染（使用 DrawInstancedIndirect）。
        /// </summary>
        public void Render() { /* 需要RHI：DrawInstancedIndirect */ }

        /// <summary>
        /// 释放 GPU 资源。
        /// </summary>
        public void Dispose() { /* 释放Buffer */ }
    }

    /// <summary>
    /// GPU 粒子发射参数。
    /// </summary>
    public struct GPUEmitParameters
    {
        public Vector3 Position;
        public float EmitRate;
        public Vector3 Direction;
        public float Speed;
        public float SpeedVariance;
        public float Size;
        public float Lifetime;
        public float LifetimeVariance;
        public int EmitCount;
        public int Padding;
    }

    /// <summary>
    /// GPU 粒子模拟参数。
    /// </summary>
    public struct GPUSimParameters
    {
        public Vector3 Gravity;
        public float DeltaTime;
        public Vector3 Wind;
        public float Drag;
        public float MaxSpeed;
        public int Padding1;
        public int Padding2;
        public int Padding3;
    }

    // ──────────────────────────────────────────────────────────────────
    // 粒子渲染器
    // ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// 粒子渲染器：负责粒子排序、批量渲染、GPU 实例化。
    /// </summary>
    public sealed class ParticleRenderer
    {
        /// <summary>渲染器名称。</summary>
        public string Name = "ParticleRenderer";
        /// <summary>渲染模式。</summary>
        public ParticleRenderMode RenderMode = ParticleRenderMode.Sprite;
        /// <summary>排序模式。</summary>
        public ParticleSortMode SortMode = ParticleSortMode.ViewSort;
        /// <summary>混合模式。</summary>
        public ParticleBlendMode BlendMode = ParticleBlendMode.Alpha;
        /// <summary>光照模式。</summary>
        public ParticleLighting LightingMode = ParticleLighting.Unlit;
        /// <summary>是否启用深度写入。</summary>
        public bool DepthWrite = false;
        /// <summary>是否启用深度测试。</summary>
        public bool DepthTest = true;
        /// <summary>是否启用GPU实例化。</summary>
        public bool GPUInstancing = true;
        /// <summary>是否使用动态排序。</summary>
        public bool DynamicSorting = true;
        /// <summary>材质句柄。</summary>
        public object? Material;
        /// <summary>纹理图集。</summary>
        public TextureAtlas? Atlas;
        /// <summary>最大可见粒子数。</summary>
        public int MaxVisibleParticles = 10000;
        /// <summary>排序缓冲区。</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        private int[] _sortIndices = Array.Empty<int>();
        /// <summary>排序距离缓冲区。</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        private float[] _sortDistances = Array.Empty<float>();
        /// <summary>LOD 配置列表。</summary>
        public List<ParticleLOD> LODSettings = new();

        /// <summary>统计信息。</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public int LastDrawCallCount;
        [System.Text.Json.Serialization.JsonIgnore]
        public int LastRenderedParticleCount;

        /// <summary>
        /// 对粒子数组进行排序。
        /// </summary>
        public void SortParticles(Particle[] particles, int count, Vector3 cameraPosition, Vector3 cameraForward)
        {
            if (SortMode == ParticleSortMode.None || count == 0)
                return;

            EnsureSortBuffers(count);

            // 计算距离
            for (int i = 0; i < count; i++)
            {
                _sortIndices[i] = i;
                if (SortMode == ParticleSortMode.DistanceSort)
                {
                    _sortDistances[i] = Vector3.DistanceSquared(particles[i].Position, cameraPosition);
                }
                else if (SortMode == ParticleSortMode.ViewSort)
                {
                    Vector3 toParticle = particles[i].Position - cameraPosition;
                    _sortDistances[i] = Vector3.Dot(toParticle, cameraForward);
                }
                else // ProjectDepthSort
                {
                    _sortDistances[i] = Vector3.DistanceSquared(particles[i].Position, cameraPosition);
                }
            }

            // 使用快速排序（从远到近排序，远的先绘制）
            Array.Sort(_sortIndices, 0, count, new ParticleSortComparer(_sortDistances));

            // 应用排序结果到粒子数组
            var sorted = new Particle[count];
            for (int i = 0; i < count; i++)
            {
                sorted[i] = particles[_sortIndices[i]];
            }
            Array.Copy(sorted, particles, count);
        }

        private void EnsureSortBuffers(int count)
        {
            if (_sortIndices.Length < count)
            {
                _sortIndices = new int[count];
                _sortDistances = new float[count];
            }
        }

        /// <summary>
        /// 准备渲染数据（构建实例数据缓冲）。
        /// </summary>
        public void PrepareRenderData(Particle[] particles, int count, Matrix4x4 viewProjection, float sizeMultiplier)
        {
            if (count == 0) return;

            // 计算摄像机到粒子距离（用于排序和LOD）
            Matrix4x4.Decompose(viewProjection, out _, out Quaternion viewRot, out Vector3 viewPos);

            // 排序
            Vector3 cameraForward = -Vector3.Transform(-Vector3.UnitZ, viewRot);
            if (DynamicSorting)
            {
                SortParticles(particles, count, viewPos, cameraForward);
            }

            // 准备实例数据
            // 实际实现需要构建 GPU InstanceBuffer（位置/大小/颜色/UV偏移等）
        }

        /// <summary>
        /// 执行渲染提交。
        /// </summary>
        public void Render()
        {
            // 需要RHI支持：设置PSO、绑定材质、DrawInstanced / DrawInstancedIndirect
            LastDrawCallCount = 1;
        }

        /// <summary>
        /// 批量渲染多个粒子系统（合并相同材质的DrawCall）。
        /// </summary>
        public void BatchRender(List<(Particle[] Particles, int Count)> batches, Vector3 cameraPosition)
        {
            int totalParticles = 0;
            foreach (var batch in batches)
            {
                totalParticles += batch.Count;
            }
            LastRenderedParticleCount = totalParticles;
            LastDrawCallCount = 1; // 批量渲染只产生一个DrawCall
        }

        /// <summary>
        /// 根据距离选择LOD等级。
        /// </summary>
        public int SelectLOD(float cameraDistance)
        {
            for (int i = LODSettings.Count - 1; i >= 0; i--)
            {
                if (cameraDistance >= LODSettings[i].DistanceThreshold)
                {
                    return LODSettings[i].Level;
                }
            }
            return 0;
        }
    }

    /// <summary>
    /// 粒子排序比较器。
    /// </summary>
    internal sealed class ParticleSortComparer : IComparer<int>
    {
        private readonly float[] _distances;

        public ParticleSortComparer(float[] distances)
        {
            _distances = distances;
        }

        public int Compare(int a, int b)
        {
            // 从远到近排序（远的先绘制）
            return _distances[b].CompareTo(_distances[a]);
        }
    }
}
