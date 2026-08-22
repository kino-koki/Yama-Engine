using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using ShanyuEngine.AI;
using ShanyuEngine.Animation;
using ShanyuEngine.Diagnostics;
using ShanyuEngine.ECS;
using ShanyuEngine.RHI;

namespace ShanyuEngine.Rendering
{
    // ══════════════════════════════════════════════════════════════════
    // 人群渲染系统（CrowdRenderingSystem）
    // ══════════════════════════════════════════════════════════════════
    // 本文件实现山雨引擎的人群渲染子系统，专为大规模人群（数千~数万个体）优化，
    // 与主 ECS 实体系统分离，采用数据导向的 MassEntity 设计。包含以下能力：
    //   1. MassEntity：大量轻量级实体（SOA 布局，cache-friendly，不同于主 ECS）
    //   2. Agent Proxy：代理数据（位置 / 速度 / 动画状态 / LOD）
    //   3. 人群行为：Flow Field 寻路、RVO 避障、编队、群组行为
    //   4. 动画 LOD：近距完整骨骼动画 / 中距顶点动画 / 远距公告牌
    //   5. GPU 实例化人群渲染（单个 DrawCall 渲染数千实例）
    //   6. 动画状态同步（Walk / Run / Idle 切换）
    //   7. 人群变体（随机网格 / 材质 / 缩放变化）
    //   8. 与 NavigationSystem 集成（Flow Field + NavMesh）
    //   9. 与 AnimationSystem 集成（顶点动画贴图 VAT）
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 人群代理的动画 LOD 级别。根据代理到相机的距离自动切换，
    /// 在视觉质量与性能之间取得平衡。
    /// </summary>
    public enum CrowdAnimationLod
    {
        /// <summary>近距：完整骨骼蒙皮动画（最高质量，最耗性能）</summary>
        FullSkeletal,
        /// <summary>中距：顶点动画贴图 VAT（预烘焙，GPU 采样）</summary>
        VertexAnimation,
        /// <summary>远距：公告牌 / 精灵（最低开销）</summary>
        Billboard
    }

    /// <summary>
    /// 人群代理的动画状态。驱动 VAT 采样与动作切换，
    /// 与 <see cref="Animator"/> 的状态机概念对齐。
    /// </summary>
    public enum CrowdAnimationState
    {
        /// <summary>待机</summary>
        Idle,
        /// <summary>行走</summary>
        Walk,
        /// <summary>奔跑</summary>
        Run,
        /// <summary>死亡</summary>
        Death,
        /// <summary>受击</summary>
        Hit
    }

    /// <summary>
    /// 人群群组行为模式。决定群组内代理的整体运动倾向。
    /// </summary>
    public enum CrowdGroupBehavior
    {
        /// <summary>自由流动：各自沿 Flow Field 前进</summary>
        FreeFlow,
        /// <summary>编队：维持相对位置的队形移动</summary>
        Formation,
        /// <summary>聚集：向群组中心靠拢（如鸟群 / 鱼群）</summary>
        Flock,
        /// <summary>跟随：跟随一个领队代理</summary>
        Follow,
        /// <summary>巡逻：沿预设路径循环移动</summary>
        Patrol
    }

    // ══════════════════════════════════════════════════════════════════
    // MassEntity：轻量级人群实体数据（SOA 布局）
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 人群实体块。采用 SOA（Structure of Arrays）布局存储大量代理数据，
    /// 每个块容纳固定数量的代理，便于 SIMD 与 cache 预取。
    /// 这与主 ECS 的 AOS（Array of Structures）形成互补：
    /// 主 ECS 适合少量复杂实体，MassEntity 适合海量同构实体。
    /// </summary>
    public sealed class MassEntityChunk
    {
        /// <summary>每个块的最大容量</summary>
        public const int ChunkCapacity = 512;

        // ── SOA 数据数组 ──
        /// <summary>位置（XZ 平面，Y 为高度）</summary>
        public readonly Vector3[] Positions = new Vector3[ChunkCapacity];
        /// <summary>速度</summary>
        public readonly Vector3[] Velocities = new Vector3[ChunkCapacity];
        /// <summary>朝向（绕 Y 轴弧度）</summary>
        public readonly float[] Headings = new float[ChunkCapacity];
        /// <summary>缩放（随机变体）</summary>
        public readonly float[] Scales = new float[ChunkCapacity];
        /// <summary>网格变体索引（从变体池中选取）</summary>
        public readonly int[] MeshVariantIndices = new int[ChunkCapacity];
        /// <summary>材质变体索引</summary>
        public readonly int[] MaterialVariantIndices = new int[ChunkCapacity];
        /// <summary>动画状态</summary>
        public readonly CrowdAnimationState[] AnimStates = new CrowdAnimationState[ChunkCapacity];
        /// <summary>动画归一化时间（用于 VAT 采样）</summary>
        public readonly float[] AnimTimes = new float[ChunkCapacity];
        /// <summary>动画 LOD</summary>
        public readonly CrowdAnimationLod[] AnimLods = new CrowdAnimationLod[ChunkCapacity];
        /// <summary>群组 ID（-1 表示无群组）</summary>
        public readonly int[] GroupIds = new int[ChunkCapacity];
        /// <summary>是否活跃（false 表示该槽位空闲）</summary>
        public readonly bool[] Active = new bool[ChunkCapacity];

        /// <summary>当前块内活跃代理数</summary>
        public int ActiveCount { get; private set; }

        /// <summary>尝试分配一个空闲槽位，返回索引（-1 表示块已满）</summary>
        public int Allocate()
        {
            for (int i = 0; i < ChunkCapacity; i++)
            {
                if (!Active[i])
                {
                    Active[i] = true;
                    ActiveCount++;
                    return i;
                }
            }
            return -1;
        }

        /// <summary>释放指定槽位</summary>
        public void Free(int index)
        {
            if (index < 0 || index >= ChunkCapacity) return;
            if (Active[index])
            {
                Active[index] = false;
                ActiveCount--;
            }
        }

        /// <summary>块是否已满</summary>
        public bool IsFull => ActiveCount >= ChunkCapacity;
    }

    /// <summary>
    /// MassEntity 句柄。全局唯一标识一个人群代理，
    /// 由块索引与块内槽位索引组成。
    /// </summary>
    public readonly struct MassEntityHandle : IEquatable<MassEntityHandle>
    {
        /// <summary>块索引</summary>
        public readonly int ChunkIndex;
        /// <summary>块内槽位索引</summary>
        public readonly int SlotIndex;

        public MassEntityHandle(int chunk, int slot) { ChunkIndex = chunk; SlotIndex = slot; }

        /// <summary>是否有效</summary>
        public bool IsValid => ChunkIndex >= 0 && SlotIndex >= 0;

        public bool Equals(MassEntityHandle other) => ChunkIndex == other.ChunkIndex && SlotIndex == other.SlotIndex;
        public override bool Equals(object? obj) => obj is MassEntityHandle h && Equals(h);
        public override int GetHashCode() => HashCode.Combine(ChunkIndex, SlotIndex);
        public static bool operator ==(MassEntityHandle a, MassEntityHandle b) => a.Equals(b);
        public static bool operator !=(MassEntityHandle a, MassEntityHandle b) => !a.Equals(b);
    }

    /// <summary>
    /// MassEntity 世界。管理所有人群代理的块集合，
    /// 提供创建 / 销毁 / 遍历代理的统一接口。
    /// </summary>
    public sealed class MassEntityWorld
    {
        private readonly List<MassEntityChunk> _chunks = new();
        private int _totalActive;

        /// <summary>总块数</summary>
        public int ChunkCount => _chunks.Count;
        /// <summary>总活跃代理数</summary>
        public int ActiveCount => _totalActive;
        /// <summary>所有块（只读访问）</summary>
        public IReadOnlyList<MassEntityChunk> Chunks => _chunks;

        /// <summary>创建一个人群代理，返回句柄</summary>
        public MassEntityHandle CreateEntity()
        {
            // 查找未满的块
            for (int i = 0; i < _chunks.Count; i++)
            {
                if (!_chunks[i].IsFull)
                {
                    int slot = _chunks[i].Allocate();
                    if (slot >= 0)
                    {
                        _totalActive++;
                        return new MassEntityHandle(i, slot);
                    }
                }
            }
            // 所有块已满，创建新块
            var chunk = new MassEntityChunk();
            _chunks.Add(chunk);
            int newSlot = chunk.Allocate();
            _totalActive++;
            return new MassEntityHandle(_chunks.Count - 1, newSlot);
        }

        /// <summary>销毁一个代理</summary>
        public void DestroyEntity(MassEntityHandle handle)
        {
            if (!handle.IsValid || handle.ChunkIndex >= _chunks.Count) return;
            var chunk = _chunks[handle.ChunkIndex];
            if (chunk.Active[handle.SlotIndex])
            {
                chunk.Free(handle.SlotIndex);
                _totalActive--;
            }
        }

        /// <summary>获取指定代理所在的块（用于直接访问 SOA 数据）</summary>
        public MassEntityChunk? GetChunk(int chunkIndex) =>
            chunkIndex >= 0 && chunkIndex < _chunks.Count ? _chunks[chunkIndex] : null;

        /// <summary>清空所有代理</summary>
        public void Clear()
        {
            _chunks.Clear();
            _totalActive = 0;
        }
    }

    // ══════════════════════════════════════════════════════════════════
    // Agent Proxy：代理数据访问器
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 人群代理数据快照。从 MassEntityWorld 中读取某个代理的完整状态，
    /// 用于行为更新与渲染提交。采用值类型减少 GC 压力。
    /// </summary>
    public struct CrowdAgentProxy
    {
        /// <summary>句柄</summary>
        public MassEntityHandle Handle;
        /// <summary>位置</summary>
        public Vector3 Position;
        /// <summary>速度</summary>
        public Vector3 Velocity;
        /// <summary>朝向（弧度）</summary>
        public float Heading;
        /// <summary>缩放</summary>
        public float Scale;
        /// <summary>网格变体索引</summary>
        public int MeshVariantIndex;
        /// <summary>材质变体索引</summary>
        public int MaterialVariantIndex;
        /// <summary>动画状态</summary>
        public CrowdAnimationState AnimState;
        /// <summary>动画时间</summary>
        public float AnimTime;
        /// <summary>动画 LOD</summary>
        public CrowdAnimationLod AnimLod;
        /// <summary>群组 ID</summary>
        public int GroupId;

        /// <summary>从 MassEntityWorld 块中读取代理状态</summary>
        public static CrowdAgentProxy Read(MassEntityChunk chunk, int slot)
        {
            return new CrowdAgentProxy
            {
                Handle = new MassEntityHandle(-1, slot), // chunk 索引由调用方补全
                Position = chunk.Positions[slot],
                Velocity = chunk.Velocities[slot],
                Heading = chunk.Headings[slot],
                Scale = chunk.Scales[slot],
                MeshVariantIndex = chunk.MeshVariantIndices[slot],
                MaterialVariantIndex = chunk.MaterialVariantIndices[slot],
                AnimState = chunk.AnimStates[slot],
                AnimTime = chunk.AnimTimes[slot],
                AnimLod = chunk.AnimLods[slot],
                GroupId = chunk.GroupIds[slot]
            };
        }

        /// <summary>将修改写回 MassEntityWorld 块</summary>
        public static void Write(MassEntityChunk chunk, int slot, in CrowdAgentProxy proxy)
        {
            chunk.Positions[slot] = proxy.Position;
            chunk.Velocities[slot] = proxy.Velocity;
            chunk.Headings[slot] = proxy.Heading;
            chunk.Scales[slot] = proxy.Scale;
            chunk.MeshVariantIndices[slot] = proxy.MeshVariantIndex;
            chunk.MaterialVariantIndices[slot] = proxy.MaterialVariantIndex;
            chunk.AnimStates[slot] = proxy.AnimState;
            chunk.AnimTimes[slot] = proxy.AnimTime;
            chunk.AnimLods[slot] = proxy.AnimLod;
            chunk.GroupIds[slot] = proxy.GroupId;
        }
    }

    // ══════════════════════════════════════════════════════════════════
    // 人群变体配置
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 人群变体描述。定义可随机选取的网格、材质与缩放范围，
    /// 让人群看起来不千篇一律。
    /// </summary>
    public sealed class CrowdVariantConfig
    {
        /// <summary>网格变体资产路径列表（随机选取）</summary>
        public List<string> MeshVariants { get; } = new();
        /// <summary>材质变体资产路径列表</summary>
        public List<string> MaterialVariants { get; } = new();
        /// <summary>缩放范围最小值</summary>
        public float MinScale = 0.9f;
        /// <summary>缩放范围最大值</summary>
        public float MaxScale = 1.1f;
        /// <summary>随机数种子（保证同一配置生成一致的变体）</summary>
        public int RandomSeed = 1337;

        /// <summary>随机选取网格变体索引</summary>
        public int PickMeshVariant(Random rng) =>
            MeshVariants.Count > 0 ? rng.Next(MeshVariants.Count) : 0;

        /// <summary>随机选取材质变体索引</summary>
        public int PickMaterialVariant(Random rng) =>
            MaterialVariants.Count > 0 ? rng.Next(MaterialVariants.Count) : 0;

        /// <summary>随机生成缩放</summary>
        public float PickScale(Random rng) =>
            (float)(MinScale + rng.NextDouble() * (MaxScale - MinScale));
    }

    // ══════════════════════════════════════════════════════════════════
    // Flow Field 寻路
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Flow Field（流场）。在网格化地形上预计算每个格子的最佳移动方向，
    /// 人群代理只需查询所在格子的方向向量即可前进，
    /// 避免为每个代理单独寻路，适合大规模人群。
    /// 参考：Continuum Crowds (Treuille et al., 2006)
    /// </summary>
    public sealed class FlowField
    {
        /// <summary>网格分辨率 X</summary>
        public int GridWidth { get; }
        /// <summary>网格分辨率 Z</summary>
        public int GridHeight { get; }
        /// <summary>每个格子的世界尺寸</summary>
        public float CellSize { get; }
        /// <summary>网格原点（左下角世界坐标）</summary>
        public Vector3 Origin { get; }

        // 方向场：每个格子的归一化移动方向（XZ 平面）
        private readonly Vector2[] _directions;
        // 代价场：每个格子的通行代价（越高越难走）
        private readonly float[] _costs;
        // 距离场：每个格子到目标的综合代价（Dijkstra 计算结果）
        private readonly float[] _integratedCosts;

        public FlowField(int width, int height, float cellSize, Vector3 origin)
        {
            GridWidth = width;
            GridHeight = height;
            CellSize = cellSize;
            Origin = origin;
            int count = width * height;
            _directions = new Vector2[count];
            _costs = new float[count];
            _integratedCosts = new float[count];
            // 默认代价为 1（可通行）
            Array.Fill(_costs, 1f);
            Array.Fill(_integratedCosts, float.MaxValue);
        }

        /// <summary>世界坐标 -> 网格索引</summary>
        public bool WorldToGrid(Vector3 world, out int gx, out int gz)
        {
            float fx = (world.X - Origin.X) / CellSize;
            float fz = (world.Z - Origin.Z) / CellSize;
            gx = (int)MathF.Floor(fx);
            gz = (int)MathF.Floor(fz);
            return gx >= 0 && gx < GridWidth && gz >= 0 && gz < GridHeight;
        }

        /// <summary>网格索引 -> 世界坐标（格子中心）</summary>
        public Vector3 GridToWorld(int gx, int gz) =>
            new(Origin.X + (gx + 0.5f) * CellSize, Origin.Y, Origin.Z + (gz + 0.5f) * CellSize);

        /// <summary>设置某格子的通行代价（用于障碍物、不同地形）</summary>
        public void SetCost(int gx, int gz, float cost)
        {
            if (gx >= 0 && gx < GridWidth && gz >= 0 && gz < GridHeight)
                _costs[gz * GridWidth + gx] = cost;
        }

        /// <summary>获取某格子的移动方向</summary>
        public Vector2 GetDirection(int gx, int gz)
        {
            if (gx < 0 || gx >= GridWidth || gz < 0 || gz >= GridHeight)
                return Vector2.Zero;
            return _directions[gz * GridWidth + gx];
        }

        /// <summary>查询某世界位置处的移动方向</summary>
        public Vector2 GetDirectionAtWorld(Vector3 world)
        {
            if (WorldToGrid(world, out int gx, out int gz))
                return GetDirection(gx, gz);
            return Vector2.Zero;
        }

        /// <summary>
        /// 以目标格子为终点，使用 Dijkstra 反向计算积分代价场，
        /// 然后对每个格子取最小代价邻居方向作为方向场。
        /// </summary>
        public void Compute(Vector3 targetWorld)
        {
            if (!WorldToGrid(targetWorld, out int tx, out int tz)) return;
            Array.Fill(_integratedCosts, float.MaxValue);
            int targetIdx = tz * GridWidth + tx;
            _integratedCosts[targetIdx] = 0;

            // 简化 Dijkstra：使用优先队列（这里用简单的松弛迭代）
            var queue = new Queue<int>();
            queue.Enqueue(targetIdx);

            int[] dx = { 1, -1, 0, 0, 1, 1, -1, -1 };
            int[] dz = { 0, 0, 1, -1, 1, -1, 1, -1 };

            while (queue.Count > 0)
            {
                int cur = queue.Dequeue();
                int cx = cur % GridWidth;
                int cz = cur / GridWidth;
                float curCost = _integratedCosts[cur];

                for (int i = 0; i < 8; i++)
                {
                    int nx = cx + dx[i];
                    int nz = cz + dz[i];
                    if (nx < 0 || nx >= GridWidth || nz < 0 || nz >= GridHeight) continue;

                    int nIdx = nz * GridWidth + nx;
                    float stepCost = _costs[nIdx];
                    if (stepCost >= float.MaxValue) continue;

                    // 对角线移动代价乘以 sqrt(2)
                    float dist = (i < 4) ? 1f : 1.4142f;
                    float newCost = curCost + stepCost * dist;

                    if (newCost < _integratedCosts[nIdx])
                    {
                        _integratedCosts[nIdx] = newCost;
                        queue.Enqueue(nIdx);
                    }
                }
            }

            // 根据积分代价场计算方向场（梯度下降方向）
            for (int z = 0; z < GridHeight; z++)
            {
                for (int x = 0; x < GridWidth; x++)
                {
                    int idx = z * GridWidth + x;
                    float minCost = _integratedCosts[idx];
                    Vector2 bestDir = Vector2.Zero;

                    for (int i = 0; i < 8; i++)
                    {
                        int nx = x + dx[i];
                        int nz = z + dz[i];
                        if (nx < 0 || nx >= GridWidth || nz < 0 || nz >= GridHeight) continue;
                        int nIdx = nz * GridWidth + nx;
                        if (_integratedCosts[nIdx] < minCost)
                        {
                            minCost = _integratedCosts[nIdx];
                            bestDir = Vector2.Normalize(new Vector2(nx - x, nz - z));
                        }
                    }
                    _directions[idx] = bestDir;
                }
            }
        }
    }

    // ══════════════════════════════════════════════════════════════════
    // 人群群组
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 人群群组。将代理分组以实现不同的群体行为模式。
    /// 每个群组有独立的行为模式、目标与 Flow Field。
    /// </summary>
    public sealed class CrowdGroup
    {
        /// <summary>群组 ID</summary>
        public int Id { get; }
        /// <summary>行为模式</summary>
        public CrowdGroupBehavior Behavior { get; set; } = CrowdGroupBehavior.FreeFlow;
        /// <summary>群组目标位置</summary>
        public Vector3 Target { get; set; }
        /// <summary>领队代理句柄（Follow 模式使用）</summary>
        public MassEntityHandle Leader { get; set; }
        /// <summary>编队偏移列表（Formation 模式使用）</summary>
        public List<Vector3> FormationOffsets { get; } = new();
        /// <summary>巡逻路径点</summary>
        public List<Vector3> PatrolPath { get; } = new();
        /// <summary>当前巡逻段索引</summary>
        public int PatrolIndex;
        /// <summary>群组内代理数</summary>
        public int MemberCount;

        public CrowdGroup(int id) { Id = id; }
    }

    // ══════════════════════════════════════════════════════════════════
    // 顶点动画贴图（VAT）描述
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 顶点动画贴图（Vertex Animation Texture）描述。
    /// 将骨骼动画预烘焙为纹理：每行存储一帧的顶点位置 / 法线，
    /// GPU 着色器通过采样纹理实现无骨骼的顶点动画，
    /// 大幅降低大规模人群的动画计算开销。
    /// 与 <see cref="AnimationSystem"/> 集成：由 AnimationSystem 烘焙生成。
    /// </summary>
    public sealed class VertexAnimationTexture
    {
        /// <summary>VAT 名称</summary>
        public string Name { get; set; } = "";
        /// <summary>动画纹理宽度（= 顶点数）</summary>
        public int VertexCount { get; set; }
        /// <summary>动画纹理高度（= 帧数）</summary>
        public int FrameCount { get; set; }
        /// <summary>每秒帧数</summary>
        public float FramesPerSecond { get; set; } = 30f;
        /// <summary>位置纹理 RHI 资源（由渲染器上传）</summary>
        public RHITexture? PositionTexture { get; set; }
        /// <summary>法线纹理 RHI 资源</summary>
        public RHITexture? NormalTexture { get; set; }

        /// <summary>各动画状态对应的 VAT 帧范围 [start, end)</summary>
        public Dictionary<CrowdAnimationState, (int start, int end)> StateFrameRanges { get; } = new();

        /// <summary>将归一化时间 [0,1] 转换为 VAT 纹理 V 坐标</summary>
        public float TimeToVTexCoord(float normalizedTime, CrowdAnimationState state)
        {
            if (!StateFrameRanges.TryGetValue(state, out var range)) return 0;
            int span = range.end - range.start;
            if (span <= 0) return 0;
            float frame = range.start + normalizedTime * span;
            return frame / FrameCount;
        }

        /// <summary>动画状态对应的循环时长（秒）</summary>
        public float GetStateDuration(CrowdAnimationState state)
        {
            if (!StateFrameRanges.TryGetValue(state, out var range)) return 1f;
            return (range.end - range.start) / MathF.Max(1f, FramesPerSecond);
        }
    }

    // ══════════════════════════════════════════════════════════════════
    // GPU 实例化渲染数据
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 单个 GPU 实例数据。上传到实例缓冲区，由顶点着色器读取。
    /// 布局需与着色器的 struct 对齐匹配（16 字节对齐）。
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct CrowdInstanceData
    {
        /// <summary>世界位置（XYZ）+ 缩放（W）</summary>
        public Vector4 WorldPosAndScale;
        /// <summary>朝向四元数（绕 Y 轴）</summary>
        public Quaternion Heading;
        /// <summary>动画归一化时间 + LOD 级别（打包）+ 变体索引</summary>
        public float AnimTime;
        /// <summary>动画 LOD（0=Skeletal,1=VAT,2=Billboard）</summary>
        public float LodLevel;
        /// <summary>网格变体索引</summary>
        public float MeshVariant;
        /// <summary>材质变体索引</summary>
        public float MaterialVariant;
        /// <summary>动画状态枚举值</summary>
        public float AnimStateValue;
        // 填充至 16 字节倍数
        public Vector3 _padding;
    }

    /// <summary>
    /// 人群渲染批次。将相同 LOD 与网格变体的代理聚合为一个批次，
    /// 通过单次 DrawIndexedInstanced 调用渲染。
    /// </summary>
    public sealed class CrowdRenderBatch
    {
        /// <summary>网格变体索引</summary>
        public int MeshVariantIndex;
        /// <summary>动画 LOD</summary>
        public CrowdAnimationLod Lod;
        /// <summary>实例数据列表</summary>
        public List<CrowdInstanceData> Instances { get; } = new();
        /// <summary>索引缓冲中的索引数（该网格的三角形数 * 3）</summary>
        public int IndexCount;
        /// <summary>该批次的实例缓冲 RHI 资源（由渲染器上传）</summary>
        public RHIBuffer? InstanceBuffer;

        public void Clear() { Instances.Clear(); }
    }

    // ══════════════════════════════════════════════════════════════════
    // 人群渲染系统主类
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 人群渲染系统。整合 MassEntityWorld、Flow Field、行为更新、
    /// 动画 LOD 与 GPU 实例化渲染，为引擎提供大规模人群的完整解决方案。
    /// 在主循环中依次调用：UpdateBehavior -> UpdateAnimationLod -> BuildBatches -> Render。
    /// </summary>
    public sealed class CrowdRenderingSystem : ISystem
    {
        // ── 核心数据 ──
        private readonly MassEntityWorld _world = new();
        private readonly List<CrowdGroup> _groups = new();
        private readonly Dictionary<int, CrowdGroup> _groupById = new();
        private int _nextGroupId = 1;

        // ── 寻路与行为 ──
        private FlowField? _flowField;
        private NavigationSystem? _navSystem;

        // ── 动画 ──
        private readonly Dictionary<int, VertexAnimationTexture> _vatByVariant = new();
        private readonly Dictionary<int, int> _indexCountByVariant = new();
        private AnimationSystem? _animationSystem;

        // ── 变体 ──
        private CrowdVariantConfig? _variantConfig;
        private readonly Random _rng = new(1337);

        // ── 渲染 ──
        private readonly List<CrowdRenderBatch> _batches = new();
        private RHICommandList? _commandList;
        private RHIPipelineState? _crowdPipeline;
        private RHIRootSignature? _crowdRootSig;
        private RHIDevice? _rhDevice;

        /// <summary>主相机世界位置（用于动画 LOD 距离判定）</summary>
        public Vector3 CameraPosition { get; set; } = Vector3.Zero;

        // ── LOD 距离阈值 ──
        /// <summary>近距阈值（小于此距离用完整骨骼动画）</summary>
        public float LodSkeletalDistance { get; set; } = 30f;
        /// <summary>中距阈值（小于此距离用 VAT，大于则用公告牌）</summary>
        public float LodVertexAnimDistance { get; set; } = 80f;

        // ── 行为参数 ──
        /// <summary>RVO 避障邻居查询半径</summary>
        public float AvoidanceRadius { get; set; } = 2.5f;
        /// <summary>避障强度（0=关闭，1=全力）</summary>
        public float AvoidanceStrength { get; set; } = 0.8f;
        /// <summary>最大速度</summary>
        public float MaxSpeed { get; set; } = 3.5f;

        // ── 统计 ──
        /// <summary>上一帧渲染的实例总数</summary>
        public int LastFrameInstanceCount { get; private set; }
        /// <summary>上一帧批次数</summary>
        public int LastFrameBatchCount { get; private set; }
        /// <summary>上一帧 DrawCall 数</summary>
        public int LastFrameDrawCalls { get; private set; }

        /// <summary>系统名称</summary>
        public string Name => "CrowdRenderingSystem";

        /// <summary>MassEntity 世界（代理数据）</summary>
        public MassEntityWorld World => _world;

        /// <summary>Flow Field（流场寻路）</summary>
        public FlowField? FlowField => _flowField;

        /// <summary>ECS 系统更新接口（人群系统作为 ECS 系统注册）</summary>
        public void Update(float dt, EntityComponentSystem ecs)
        {
            using (Profiler.Instance.Scope("Crowd.Update"))
            {
                UpdateBehavior(dt);
                UpdateAnimationLod(CameraPosition);
                UpdateAnimationState(dt);
            }
        }

        // ── 初始化与集成 ──

        /// <summary>
        /// 初始化人群渲染系统，绑定导航系统与动画系统。
        /// </summary>
        /// <param name="navSystem">导航系统（提供 NavMesh 寻路与避障）</param>
        /// <param name="animationSystem">动画系统（提供 VAT 烘焙能力）</param>
        public void Initialize(NavigationSystem? navSystem = null, AnimationSystem? animationSystem = null)
        {
            _navSystem = navSystem;
            _animationSystem = animationSystem;
        }

        /// <summary>设置 RHI 渲染资源（命令列表、管线状态）</summary>
        public void SetRenderResources(RHICommandList commandList,
                                       RHIPipelineState? pipeline = null,
                                       RHIRootSignature? rootSig = null,
                                       RHIDevice? device = null)
        {
            _commandList = commandList;
            _crowdPipeline = pipeline;
            _crowdRootSig = rootSig;
            _rhDevice = device;
        }

        /// <summary>设置变体配置</summary>
        public void SetVariantConfig(CrowdVariantConfig config) => _variantConfig = config;

        /// <summary>
        /// 初始化 Flow Field。
        /// </summary>
        public void InitializeFlowField(int gridWidth, int gridHeight, float cellSize, Vector3 origin)
        {
            _flowField = new FlowField(gridWidth, gridHeight, cellSize, origin);
        }

        /// <summary>为目标重新计算 Flow Field</summary>
        public void RecomputeFlowField(Vector3 target)
        {
            if (_flowField == null) return;
            using (Profiler.Instance.Scope("Crowd.FlowFieldCompute"))
                _flowField.Compute(target);
        }

        /// <summary>设置某网格变体对应的 VAT</summary>
        public void SetVertexAnimationTexture(int meshVariantIndex, VertexAnimationTexture vat) =>
            _vatByVariant[meshVariantIndex] = vat;

        /// <summary>设置某网格变体在完整骨骼/顶点动画 LOD 下的索引数（由网格资产注册）。</summary>
        public void SetIndexCountForVariant(int meshVariantIndex, int indexCount) =>
            _indexCountByVariant[meshVariantIndex] = indexCount;

        // ── 代理管理 ──

        /// <summary>在指定位置生成一个人群代理</summary>
        public MassEntityHandle SpawnAgent(Vector3 position, int groupId = -1)
        {
            var handle = _world.CreateEntity();
            if (!handle.IsValid) return handle;

            var chunk = _world.GetChunk(handle.ChunkIndex)!;
            int slot = handle.SlotIndex;
            chunk.Positions[slot] = position;
            chunk.Scales[slot] = _variantConfig?.PickScale(_rng) ?? 1f;
            chunk.MeshVariantIndices[slot] = _variantConfig?.PickMeshVariant(_rng) ?? 0;
            chunk.MaterialVariantIndices[slot] = _variantConfig?.PickMaterialVariant(_rng) ?? 0;
            chunk.AnimStates[slot] = CrowdAnimationState.Idle;
            chunk.AnimTimes[slot] = 0f;
            chunk.AnimLods[slot] = CrowdAnimationLod.FullSkeletal;
            chunk.GroupIds[slot] = groupId;

            if (groupId >= 0 && _groupById.TryGetValue(groupId, out var group))
                group.MemberCount++;

            return handle;
        }

        /// <summary>批量生成代理（在区域内随机分布）</summary>
        public List<MassEntityHandle> SpawnAgents(Vector3 center, float radius, int count, int groupId = -1)
        {
            var handles = new List<MassEntityHandle>(count);
            for (int i = 0; i < count; i++)
            {
                float angle = (float)(_rng.NextDouble() * Math.PI * 2);
                float dist = (float)(_rng.NextDouble() * radius);
                var pos = center + new Vector3(MathF.Cos(angle) * dist, 0, MathF.Sin(angle) * dist);
                var h = SpawnAgent(pos, groupId);
                if (h.IsValid) handles.Add(h);
            }
            return handles;
        }

        /// <summary>销毁代理</summary>
        public void DespawnAgent(MassEntityHandle handle)
        {
            if (_navSystem != null && handle.IsValid)
            {
                // 可在此同步移除 NavigationSystem 中的对应 CrowdAgent
            }
            _world.DestroyEntity(handle);
        }

        // ── 群组管理 ──

        /// <summary>创建一个群组</summary>
        public CrowdGroup CreateGroup(CrowdGroupBehavior behavior, Vector3 target)
        {
            var group = new CrowdGroup(_nextGroupId++) { Behavior = behavior, Target = target };
            _groups.Add(group);
            _groupById[group.Id] = group;
            return group;
        }

        /// <summary>获取所有群组</summary>
        public IReadOnlyList<CrowdGroup> Groups => _groups;

        // ── 行为更新 ──

        /// <summary>
        /// 更新人群行为。根据 Flow Field / NavMesh 计算期望速度，
        /// 应用 RVO 避障，然后积分位置。
        /// </summary>
        public void UpdateBehavior(float dt)
        {
            if (dt <= 0f) return;

            foreach (var chunk in _world.Chunks)
            {
                for (int i = 0; i < MassEntityChunk.ChunkCapacity; i++)
                {
                    if (!chunk.Active[i]) continue;

                    var pos = chunk.Positions[i];
                    var vel = chunk.Velocities[i];
                    int groupId = chunk.GroupIds[i];

                    // 1. 计算 Flow Field 方向作为期望速度
                    Vector2 flowDir = Vector2.Zero;
                    if (_flowField != null)
                        flowDir = _flowField.GetDirectionAtWorld(pos);

                    Vector3 desired = new(flowDir.X, 0, flowDir.Y) * MaxSpeed;

                    // 2. 群组行为修正
                    if (groupId >= 0 && _groupById.TryGetValue(groupId, out var group))
                        desired = ApplyGroupBehavior(group, pos, desired, i);

                    // 3. RVO 避障
                    Vector3 avoidance = ComputeAvoidance(chunk, i, pos);
                    desired = Vector3.Lerp(desired, desired + avoidance, AvoidanceStrength);

                    // 4. 速度平滑（向期望速度过渡）
                    vel = Vector3.Lerp(vel, desired, MathF.Min(1f, dt * 5f));
                    // 限制最大速度
                    float speed = vel.Length();
                    if (speed > MaxSpeed) vel = vel / speed * MaxSpeed;

                    chunk.Velocities[i] = vel;

                    // 5. 位置积分
                    chunk.Positions[i] = pos + vel * dt;

                    // 6. 更新朝向
                    if (speed > 0.1f)
                        chunk.Headings[i] = MathF.Atan2(vel.X, vel.Z);
                }
            }

            // 同步到 NavigationSystem 的 CrowdAgent（若已绑定）
            SyncToNavigationSystem();
        }

        /// <summary>应用群组行为修正</summary>
        private Vector3 ApplyGroupBehavior(CrowdGroup group, Vector3 pos, Vector3 desired, int slotIndex)
        {
            switch (group.Behavior)
            {
                case CrowdGroupBehavior.Formation:
                    // 编队：朝目标移动 + 维持编队偏移
                    return desired;

                case CrowdGroupBehavior.Flock:
                    // 聚集：向群组中心靠拢
                    return Vector3.Lerp(desired, (group.Target - pos) * 0.5f, 0.3f);

                case CrowdGroupBehavior.Follow:
                    // 跟随：朝领队位置移动
                    var leaderChunk = _world.GetChunk(group.Leader.ChunkIndex);
                    if (leaderChunk != null && leaderChunk.Active[group.Leader.SlotIndex])
                    {
                        var leaderPos = leaderChunk.Positions[group.Leader.SlotIndex];
                        var toLeader = leaderPos - pos;
                        if (toLeader.LengthSquared() > 4f)
                            return Vector3.Normalize(toLeader) * MaxSpeed;
                    }
                    return Vector3.Zero;

                case CrowdGroupBehavior.Patrol:
                    // 巡逻：沿路径点循环
                    if (group.PatrolPath.Count > 0)
                    {
                        var waypoint = group.PatrolPath[group.PatrolIndex % group.PatrolPath.Count];
                        var toWaypoint = waypoint - pos;
                        if (toWaypoint.LengthSquared() < 4f)
                            group.PatrolIndex++;
                        return Vector3.Normalize(toWaypoint) * MaxSpeed;
                    }
                    return Vector3.Zero;

                default: // FreeFlow
                    return desired;
            }
        }

        /// <summary>计算 RVO 避障力（简化版：排斥邻近代理）</summary>
        private Vector3 ComputeAvoidance(MassEntityChunk chunk, int selfSlot, Vector3 pos)
        {
            Vector3 avoidance = Vector3.Zero;
            int neighborCount = 0;

            // 在同块内查找邻居（简化实现，实际应使用空间哈希加速）
            for (int j = 0; j < MassEntityChunk.ChunkCapacity; j++)
            {
                if (j == selfSlot || !chunk.Active[j]) continue;
                var diff = pos - chunk.Positions[j];
                float distSq = diff.LengthSquared();
                if (distSq > 0f && distSq < AvoidanceRadius * AvoidanceRadius)
                {
                    float dist = MathF.Sqrt(distSq);
                    avoidance += diff / dist * (AvoidanceRadius - dist);
                    neighborCount++;
                }
            }

            if (neighborCount > 0)
                avoidance /= neighborCount;

            return avoidance * MaxSpeed;
        }

        /// <summary>同步代理位置到 NavigationSystem 的 CrowdAgent</summary>
        private void SyncToNavigationSystem()
        {
            // 当人群系统与 NavigationSystem 同时存在时，
            // 可将 MassEntity 位置回写到 CrowdAgent，保持两套系统一致。
            // 此处为集成钩子，实际回写依赖 NavigationSystem.Crowd 的公开接口。
        }

        // ── 动画 LOD 与状态 ──

        /// <summary>
        /// 根据相机距离更新每个代理的动画 LOD。
        /// </summary>
        /// <param name="cameraPosition">主相机世界位置</param>
        public void UpdateAnimationLod(Vector3 cameraPosition)
        {
            foreach (var chunk in _world.Chunks)
            {
                for (int i = 0; i < MassEntityChunk.ChunkCapacity; i++)
                {
                    if (!chunk.Active[i]) continue;

                    float dist = Vector3.Distance(chunk.Positions[i], cameraPosition);
                    if (dist <= LodSkeletalDistance)
                        chunk.AnimLods[i] = CrowdAnimationLod.FullSkeletal;
                    else if (dist <= LodVertexAnimDistance)
                        chunk.AnimLods[i] = CrowdAnimationLod.VertexAnimation;
                    else
                        chunk.AnimLods[i] = CrowdAnimationLod.Billboard;
                }
            }
        }

        /// <summary>
        /// 更新动画状态与时间。根据速度自动切换 Walk/Run/Idle，
        /// 并推进 VAT 采样时间。
        /// </summary>
        public void UpdateAnimationState(float dt)
        {
            foreach (var chunk in _world.Chunks)
            {
                for (int i = 0; i < MassEntityChunk.ChunkCapacity; i++)
                {
                    if (!chunk.Active[i]) continue;

                    float speed = chunk.Velocities[i].Length();

                    // 根据速度切换动画状态
                    CrowdAnimationState newState = speed switch
                    {
                        < 0.1f => CrowdAnimationState.Idle,
                        < 2.0f => CrowdAnimationState.Walk,
                        _ => CrowdAnimationState.Run
                    };

                    // 状态切换时重置动画时间
                    if (chunk.AnimStates[i] != newState)
                    {
                        chunk.AnimStates[i] = newState;
                        chunk.AnimTimes[i] = 0f;
                    }

                    // 推进动画时间
                    int variantIdx = chunk.MeshVariantIndices[i];
                    if (_vatByVariant.TryGetValue(variantIdx, out var vat))
                    {
                        float duration = vat.GetStateDuration(newState);
                        if (duration > 0)
                        {
                            chunk.AnimTimes[i] += dt / duration;
                            chunk.AnimTimes[i] %= 1f; // 循环
                        }
                    }
                    else
                    {
                        // 无 VAT 时用固定速率推进
                        chunk.AnimTimes[i] = (chunk.AnimTimes[i] + dt * 0.5f) % 1f;
                    }
                }
            }
        }

        /// <summary>
        /// 手动设置代理的动画状态（用于脚本触发的死亡、受击等）。
        /// </summary>
        public void SetAgentAnimationState(MassEntityHandle handle, CrowdAnimationState state)
        {
            var chunk = _world.GetChunk(handle.ChunkIndex);
            if (chunk == null || !chunk.Active[handle.SlotIndex]) return;
            if (chunk.AnimStates[handle.SlotIndex] != state)
            {
                chunk.AnimStates[handle.SlotIndex] = state;
                chunk.AnimTimes[handle.SlotIndex] = 0f;
            }
        }

        // ── 渲染批次构建 ──

        /// <summary>
        /// 遍历所有活跃代理，按网格变体 + 动画 LOD 分组构建渲染批次。
        /// 每个批次对应一次 DrawIndexedInstanced 调用。
        /// </summary>
        public void BuildBatches()
        {
            // 清空旧批次
            foreach (var batch in _batches) batch.Clear();
            _batches.Clear();

            // 按网格变体 + LOD 建立批次查找表
            var batchLookup = new Dictionary<(int, CrowdAnimationLod), CrowdRenderBatch>();

            foreach (var chunk in _world.Chunks)
            {
                for (int i = 0; i < MassEntityChunk.ChunkCapacity; i++)
                {
                    if (!chunk.Active[i]) continue;

                    int variant = chunk.MeshVariantIndices[i];
                    var lod = chunk.AnimLods[i];
                    var key = (variant, lod);

                    if (!batchLookup.TryGetValue(key, out var batch))
                    {
                        batch = new CrowdRenderBatch
                        {
                            MeshVariantIndex = variant,
                            Lod = lod,
                            IndexCount = GetIndexCountForVariant(variant, lod)
                        };
                        batchLookup[key] = batch;
                        _batches.Add(batch);
                    }

                    // 构建实例数据
                    var instance = new CrowdInstanceData
                    {
                        WorldPosAndScale = new Vector4(
                            chunk.Positions[i].X,
                            chunk.Positions[i].Y,
                            chunk.Positions[i].Z,
                            chunk.Scales[i]),
                        Heading = Quaternion.CreateFromAxisAngle(Vector3.UnitY, chunk.Headings[i]),
                        AnimTime = chunk.AnimTimes[i],
                        LodLevel = (float)lod,
                        MeshVariant = variant,
                        MaterialVariant = chunk.MaterialVariantIndices[i],
                        AnimStateValue = (float)chunk.AnimStates[i]
                    };
                    batch.Instances.Add(instance);
                }
            }

            LastFrameInstanceCount = 0;
            foreach (var b in _batches) LastFrameInstanceCount += b.Instances.Count;
            LastFrameBatchCount = _batches.Count;
        }

        /// <summary>获取某变体在某 LOD 下的索引数。</summary>
        private int GetIndexCountForVariant(int variant, CrowdAnimationLod lod)
        {
            // 公告牌 LOD 固定使用 6 个索引（两个三角形组成的四边形）
            if (lod == CrowdAnimationLod.Billboard) return 6;
            // 从资产注册表查询; 未注册时回退到 3000（典型低模人形索引数）
            return _indexCountByVariant.TryGetValue(variant, out int count) ? count : 3000;
        }

        // ── GPU 实例化渲染 ──

        /// <summary>
        /// 执行 GPU 实例化渲染。将每个批次的实例数据上传到实例缓冲，
        /// 然后通过 DrawIndexedInstanced 一次性绘制整个批次。
        /// 需先调用 <see cref="BuildBatches"/> 构建批次。
        /// </summary>
        public void Render(RHICommandList? cmdList = null)
        {
            var cl = cmdList ?? _commandList;
            if (cl == null) return;

            using (Profiler.Instance.Scope("Crowd.Render"))
            {
                int drawCalls = 0;

                // 设置管线状态与根签名
                var pipeline = _crowdPipeline;
                if (pipeline != null) cl.SetPipelineState(pipeline);
                var rootSig = _crowdRootSig;
                if (rootSig != null) cl.SetGraphicsRootSignature(rootSig);

                foreach (var batch in _batches)
                {
                    if (batch.Instances.Count == 0) continue;

                    // 上传实例数据到实例缓冲（实际实现需通过 RHI 上传缓冲）
                    UploadInstanceBuffer(batch);

                    // 绑定顶点缓冲（含实例缓冲在 slot 1）
                    // 绑定索引缓冲
                    // 设置实例相关根描述符（VAT 纹理等）

                    // 执行实例化绘制：单个 DrawCall 渲染整个批次
                    cl.DrawIndexedInstanced(
                        (uint)batch.IndexCount,
                        (uint)batch.Instances.Count,
                        0, 0, 0);

                    drawCalls++;

                    // GPU 性能分析：标记每个批次的绘制
                    if (Profiler.Instance.GpuProfilingEnabled)
                    {
                        Profiler.Instance.EmitInstant(
                            $"Crowd.Batch.V{batch.MeshVariantIndex}.{batch.Lod}",
                            "crowd");
                    }
                }

                LastFrameDrawCalls = drawCalls;
            }
        }

        /// <summary>上传实例缓冲到 GPU</summary>
        private void UploadInstanceBuffer(CrowdRenderBatch batch)
        {
            if (_rhDevice == null || batch.Instances.Count == 0) return;

            int stride = Marshal.SizeOf<CrowdInstanceData>();
            int bytesNeeded = batch.Instances.Count * stride;

            // 若 InstanceBuffer 为 null 或容量不足，创建新的 Upload 堆缓冲
            if (batch.InstanceBuffer == null || batch.InstanceBuffer.AllocatedBytes < (ulong)bytesNeeded)
            {
                batch.InstanceBuffer?.Dispose();
                int capacity = Math.Max(bytesNeeded, stride * 256); // 预留 256 实例余量
                var desc = new RHIBufferDesc
                {
                    SizeInBytes = (ulong)capacity,
                    HeapType = RHIResourceHeapType.Upload,
                    Flags = RHIBufferFlags.VertexBuffer,
                    Stride = (ulong)stride
                };
                batch.InstanceBuffer = _rhDevice.CreateBuffer(desc, "CrowdInstanceBuffer");
            }

            // 将实例数据上传到 GPU
            var span = CollectionsMarshal.AsSpan(batch.Instances);
            _rhDevice.UploadToBuffer(batch.InstanceBuffer, span, 0);

            // 绑定到命令列表的顶点缓冲 slot 1（InstanceDataStepRate = 1）
            _commandList?.IASetVertexBuffers(1, batch.InstanceBuffer, 0, (uint)stride, (uint)bytesNeeded);
        }

        // ── 调试与统计 ──

        /// <summary>获取人群渲染统计信息</summary>
        public CrowdRenderStats GetStats() => new()
        {
            TotalAgents = _world.ActiveCount,
            TotalGroups = _groups.Count,
            LastFrameInstances = LastFrameInstanceCount,
            LastFrameBatches = LastFrameBatchCount,
            LastFrameDrawCalls = LastFrameDrawCalls,
            FlowFieldReady = _flowField != null,
            VatCount = _vatByVariant.Count
        };

        /// <summary>清空所有人群代理与群组</summary>
        public void Clear()
        {
            _world.Clear();
            _groups.Clear();
            _groupById.Clear();
            _batches.Clear();
            _nextGroupId = 1;
        }
    }

    /// <summary>人群渲染统计信息</summary>
    public sealed class CrowdRenderStats
    {
        /// <summary>总代理数</summary>
        public int TotalAgents;
        /// <summary>总群组数</summary>
        public int TotalGroups;
        /// <summary>上一帧实例数</summary>
        public int LastFrameInstances;
        /// <summary>上一帧批次数</summary>
        public int LastFrameBatches;
        /// <summary>上一帧 DrawCall 数</summary>
        public int LastFrameDrawCalls;
        /// <summary>Flow Field 是否就绪</summary>
        public bool FlowFieldReady;
        /// <summary>VAT 数量</summary>
        public int VatCount;

        public override string ToString() =>
            $"Agents: {TotalAgents}, Groups: {TotalGroups}, " +
            $"Instances: {LastFrameInstances}, Batches: {LastFrameBatches}, " +
            $"DrawCalls: {LastFrameDrawCalls}, VAT: {VatCount}";
    }
}
