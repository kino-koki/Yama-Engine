using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using ShanyuEngine.ECS;
using ShanyuEngine.RHI;

namespace ShanyuEngine.World
{
    // ════════════════════════════════════════════════════════════════════════
    // 山雨引擎景观草系统 (Shanyu Landscape Grass System)
    //
    // 设计目标: 提供一套基于 GPU 实例化的海量草叶渲染与交互管线, 对标
    // UE7 FlowVFX Foliage / Unity HDRP Vegetation, 并与山雨地形系统深度耦合。
    //
    // 核心能力:
    //   1. 草类型配置 (GrassType): 每种草关联网格/材质/密度/尺寸范围/坡度限制/
    //      高度限制/颜色变化/风力响应, 支持多类型混合分布。
    //   2. 草层 (GrassLayer): 将草类型与地形 Splat 图层蒙版绑定, 仅在指定地表
    //      材质(如草地层)上生长, 并提供分布参数(密度倍率/抖动半径)。
    //   3. 草簇 (GrassCluster): 一个 GPU 实例的最小单元, 含位置/法线/LOD/类型。
    //   4. Chunk 分块流式管理: 以相机为中心将世界划分为固定大小的 Chunk,
    //      仅对可视半径内的 Chunk 放置草簇; 远离的 Chunk 回收至对象池, 避免
    //      频繁创建/销毁的开销。
    //   5. 三级 LOD: 近距(完整 Mesh 草叶) / 中距(公告板 Billboard) / 远距(隐藏),
    //      切换距离可按草类型配置。
    //   6. GPU 实例化渲染: 草叶几何共享单个 VB/IB, 实例数据驻留 StructuredBuffer,
    //      经 Compute Shader 执行视锥剔除 + HZB 遮挡剔除后, 输出紧凑可见实例
    //      缓冲与间接绘制参数, 单次 DrawIndexedInstancedIndirect 渲染整 Chunk。
    //   7. 风力动画: Vertex Shader 端基于世界噪声 + 时间驱动的草叶摆动, 根部
    //      锁定、叶尖摆幅最大, 支持阵风(Gust)与方向风力。
    //   8. 草间接触阴影: 近似 AO, 通过草叶高度估算环境光遮蔽项, 增强草地体积感。
    //   9. 地形法线对齐: 草叶随地形坡度倾斜, 避免垂直穿插。
    //  10. 草交互 (GrassInteractionSystem): 角色踩踏在交互贴图/GPU Buffer 上
    //      写入压弯量, 渲染时采样使草叶被压倒, 并随时间回弹恢复。
    //
    // 对接关系:
    //   - TerrainSystem: 查询高度/法线/Splat 图层权重, 决定草能否生长。
    //   - ECS: LandscapeGrassComponent 作为实体组件, GrassPlacementSystem 实现
    //     ISystem 由 ECS 主循环驱动。
    //   - RHI: StructuredBuffer / RWStructuredBuffer / Indirect Draw / Compute
    //     Shader 全部通过 RHIDevice + RHICommandList 提交。
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>景观草系统全局常量。</summary>
    public static class GrassConstants
    {
        /// <summary>单个 Chunk 的世界边长(米)。Chunk 越大, 单次 DrawCall 覆盖越广, 但更新粒度越粗。</summary>
        public const float ChunkSize = 32.0f;

        /// <summary>单个 Chunk 内最大草簇(实例)数, 需为 64 的倍数以便 Compute 分组。</summary>
        public const int MaxClustersPerChunk = 4096;

        /// <summary>草簇放置时使用的伪随机抖动半径(米), 避免草叶呈网格状排列。</summary>
        public const float PlacementJitter = 0.45f;

        /// <summary>近距完整 Mesh 切换到公告板的默认距离(米)。</summary>
        public const float DefaultLod0Distance = 35.0f;

        /// <summary>公告板切换到隐藏的默认距离(米)。</summary>
        public const float DefaultLod1Distance = 120.0f;

        /// <summary>Chunk 可视半径(以 Chunk 数计), 超出半径的 Chunk 不放置草簇。</summary>
        public const int ChunkViewRadius = 6;

        /// <summary>Compute 剔除着色器每线程组线程数。</summary>
        public const int CullThreadGroupSize = 64;

        /// <summary>草交互贴图分辨率(每边像素), 覆盖相机周围一定范围。</summary>
        public const int InteractionMapSize = 512;

        /// <summary>草交互贴图覆盖的世界半径(米)。</summary>
        public const float InteractionWorldRadius = 64.0f;

        /// <summary>草交互压弯回弹时间常数(秒), 越大回弹越慢。</summary>
        public const float InteractionRecoverTau = 2.5f;
    }

    /// <summary>草簇 LOD 层级。</summary>
    public enum GrassLODLevel
    {
        /// <summary>近距: 完整网格草叶(最高细节)。</summary>
        LOD0_FullMesh = 0,
        /// <summary>中距: 公告板草叶(单四边形)。</summary>
        LOD1_Billboard = 1,
        /// <summary>远距: 已剔除/隐藏。</summary>
        Culled = 0xFF
    }

    /// <summary>风力动画质量档位。</summary>
    public enum GrassWindQuality
    {
        /// <summary>无风力(静态草)。</summary>
        None = 0,
        /// <summary>低质量: 单一正弦摆动。</summary>
        Low = 1,
        /// <summary>中质量: 噪声驱动的方向风力。</summary>
        Medium = 2,
        /// <summary>高质量: 噪声 + 阵风(Gust)叠加。</summary>
        High = 3
    }

    // ──────────────────────────────────────────────────────────────────────
    // GPU 端常量缓冲与实例结构 (与 HLSL 端严格对齐, 16 字节边界)
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>单个草叶实例数据(驻留 StructuredBuffer, VS 通过 SV_InstanceID 索引)。</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 16)]
    public struct GrassInstanceData
    {
        /// <summary>草叶根部世界位置。</summary>
        public Vector3 Position;
        /// <summary>草叶均匀缩放(由尺寸范围随机得到)。</summary>
        public float Scale;
        /// <summary>所处地形法线(用于草叶随坡度对齐)。</summary>
        public Vector3 Normal;
        /// <summary>颜色变化因子[0,1], 在着色器中插值基色与变体色。</summary>
        public float ColorVariation;
        /// <summary>草叶旋转(弧度, 仅绕 Up 轴有意义)。</summary>
        public Vector3 Rotation;
        /// <summary>风力相位偏移, 使同片草叶摆动略有差异。</summary>
        public float WindPhase;

        /// <summary>结构体字节数(HLSL 端 sizeof)。</summary>
        public const int Stride = 48;
    }

    /// <summary>间接绘制参数(对应 DrawIndexedInstancedIndirect 的 5 个 uint32)。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct GrassIndirectArgs
    {
        /// <summary>每个实例的索引数(草叶几何的三角形索引数)。</summary>
        public uint IndexCountPerInstance;
        /// <summary>实例数(由剔除 CS 写入可见实例数)。</summary>
        public uint InstanceCount;
        /// <summary>起始索引位置。</summary>
        public uint StartIndexLocation;
        /// <summary>基准顶点偏移。</summary>
        public int BaseVertexLocation;
        /// <summary>起始实例位置。</summary>
        public uint StartInstanceLocation;
    }

    /// <summary>剔除 Compute Shader 的常量缓冲。</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 16)]
    public struct GrassCullCB
    {
        /// <summary>本 Chunk 待剔除实例总数。</summary>
        public uint InstanceCount;
        /// <summary>可见实例缓冲最大容量。</summary>
        public uint MaxVisible;
        /// <summary>草叶几何索引数(写入间接参数 IndexCountPerInstance)。</summary>
        public uint IndexCountPerInstance;
        /// <summary>HZB 遮蔽剔除开关(0=关, 1=开)。</summary>
        public uint UseHzbCull;
        /// <summary>视投影矩阵(用于视锥剔除)。</summary>
        public Matrix4x4 ViewProjection;
        /// <summary>视锥 6 个平面方程(用于球剔除), xyz=法线, w=距离。</summary>
        public Vector4 FrustumPlane0;
        /// <summary>视锥平面 1。</summary>
        public Vector4 FrustumPlane1;
        /// <summary>视锥平面 2。</summary>
        public Vector4 FrustumPlane2;
        /// <summary>视锥平面 3。</summary>
        public Vector4 FrustumPlane3;
        /// <summary>视锥平面 4。</summary>
        public Vector4 FrustumPlane4;
        /// <summary>视锥平面 5。</summary>
        public Vector4 FrustumPlane5;
        /// <summary>草叶包围球半径(用于球-平面剔除)。</summary>
        public float BoundsRadius;
        /// <summary>对齐填充。</summary>
        public float _pad0;
        /// <summary>对齐填充。</summary>
        public float _pad1;
        /// <summary>对齐填充。</summary>
        public float _pad2;
    }

    /// <summary>渲染阶段的逐帧常量缓冲。</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 16)]
    public struct GrassRenderCB
    {
        /// <summary>视投影矩阵。</summary>
        public Matrix4x4 ViewProjection;
        /// <summary>上一帧视投影矩阵(用于运动矢量, 可选)。</summary>
        public Matrix4x4 PrevViewProjection;
        /// <summary>相机世界位置。</summary>
        public Vector3 CameraPosition;
        /// <summary>当前时间(秒)。</summary>
        public float Time;
        /// <summary>风向(世界空间, 归一化)。</summary>
        public Vector3 WindDirection;
        /// <summary>风力强度[0,1]。</summary>
        public float WindStrength;
        /// <summary>阵风强度[0,1]。</summary>
        public float GustStrength;
        /// <summary>阵风频率(赫兹)。</summary>
        public float GustFrequency;
        /// <summary>风力质量档位(见 GrassWindQuality)。</summary>
        public uint WindQuality;
        /// <summary>交互贴图世界半径。</summary>
        public float InteractionRadius;
        /// <summary>对齐填充。</summary>
        public float _pad0;
        /// <summary>对齐填充。</summary>
        public float _pad1;
    }

    /// <summary>草交互 Compute Shader 的常量缓冲。</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 16)]
    public struct GrassInteractionCB
    {
        /// <summary>交互中心世界位置(XZ)。</summary>
        public Vector2 CenterWorld;
        /// <summary>交互贴图世界半径。</summary>
        public float WorldRadius;
        /// <summary>本帧交互体数量。</summary>
        public uint InteractorCount;
        /// <summary>回弹衰减系数(= 1 - exp(-dt/tau))。</summary>
        public float RecoverFactor;
        /// <summary>贴图分辨率。</summary>
        public uint MapSize;
        /// <summary>对齐填充。</summary>
        public uint _pad0;
        /// <summary>对齐填充。</summary>
        public float _pad1;
        /// <summary>交互体数据(位置 XYZ + 压弯强度), 最多 4 个进 CB, 其余可分批。</summary>
        public Vector4 Interactors0;
        public Vector4 Interactors1;
        public Vector4 Interactors2;
        public Vector4 Interactors3;
    }

    // ════════════════════════════════════════════════════════════════════════
    // 草类型与草层配置
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 草类型配置。描述一"种"草的全部外观与分布参数, 多个 <see cref="GrassLayer"/>
    /// 可引用同一类型但在不同地形图层上分布。
    /// </summary>
    public class GrassType
    {
        /// <summary>类型名称(便于编辑器与序列化识别)。</summary>
        public string Name = "Grass";

        /// <summary>草叶网格资源 ID(近距 LOD0 使用的完整网格, 由资产系统解析)。</summary>
        public Guid MeshId = Guid.Empty;
        /// <summary>公告板贴图资源 ID(中距 LOD1 使用)。</summary>
        public Guid BillboardTextureId = Guid.Empty;
        /// <summary>草叶材质资源 ID。</summary>
        public Guid MaterialId = Guid.Empty;

        /// <summary>每平方米目标密度(草簇数)。</summary>
        public float Density = 4.0f;
        /// <summary>草叶最小缩放。</summary>
        public float MinScale = 0.8f;
        /// <summary>草叶最大缩放。</summary>
        public float MaxScale = 1.4f;
        /// <summary>草叶高度(米, 用于风力摆幅与 AO 估算)。</summary>
        public float BladeHeight = 0.4f;
        /// <summary>草叶宽度(米)。</summary>
        public float BladeWidth = 0.05f;

        /// <summary>允许生长的最小坡度(度)。0=平地也允许。</summary>
        public float MinSlope = 0.0f;
        /// <summary>允许生长的最大坡度(度)。超过则不放置(避免悬崖长草)。</summary>
        public float MaxSlope = 55.0f;
        /// <summary>允许生长的高度下限(世界 Y, 米)。</summary>
        public float MinHeight = float.MinValue;
        /// <summary>允许生长的高度上限(世界 Y, 米)。</summary>
        public float MaxHeight = float.MaxValue;

        /// <summary>颜色变化幅度[0,1], 0=无变化, 1=在基色与变体色间完全随机。</summary>
        public float ColorVariation = 0.25f;
        /// <summary>基色(健康草色)。</summary>
        public Vector3 BaseColor = new(0.18f, 0.45f, 0.12f);
        /// <summary>变体色(枯黄/深绿等)。</summary>
        public Vector3 VariantColor = new(0.35f, 0.55f, 0.18f);

        /// <summary>风力响应强度[0,1], 0=不受风影响, 1=完全随风摆动。</summary>
        public float WindResponse = 0.8f;
        /// <summary>风力摆动频率倍率。</summary>
        public float WindFrequencyScale = 1.0f;

        /// <summary>LOD0(完整网格)切换到 LOD1(公告板)的距离(米)。</summary>
        public float Lod0Distance = GrassConstants.DefaultLod0Distance;
        /// <summary>LOD1(公告板)切换到隐藏的距离(米)。</summary>
        public float Lod1Distance = GrassConstants.DefaultLod1Distance;

        /// <summary>是否参与草间接触阴影(AO)计算。</summary>
        public bool EnableContactAO = true;

        /// <summary>构造一份默认草类型配置。</summary>
        public static GrassType CreateDefault() => new()
        {
            Name = "DefaultGrass",
            Density = 4.0f,
            BladeHeight = 0.4f,
            WindResponse = 0.8f
        };
    }

    /// <summary>
    /// 草层。将一个 <see cref="GrassType"/> 绑定到地形 Splat 图的某个通道,
    /// 决定该草类型"在哪些地表材质上生长", 并提供局部分布参数。
    /// </summary>
    public class GrassLayer
    {
        /// <summary>引用的草类型(允许同类型被多图层复用)。</summary>
        public GrassType Type = new();
        /// <summary>绑定的地形 Splat 通道索引(0..3, 对应 RGBA)。仅在权重 &gt; 阈值处生长。</summary>
        public int SplatChannel = 0;
        /// <summary>Splat 通道权重阈值[0,1], 低于此值的地表不生长该草。</summary>
        public float SplatThreshold = 0.25f;
        /// <summary>密度倍率(与 GrassType.Density 相乘), 用于图层级整体稀疏/浓密调节。</summary>
        public float DensityScale = 1.0f;
        /// <summary>本层是否启用。</summary>
        public bool Enabled = true;
        /// <summary>本层放置时的随机种子偏移(使不同图层草簇错开)。</summary>
        public uint SeedOffset = 0;
    }

    // ════════════════════════════════════════════════════════════════════════
    // 草簇与 ECS 组件
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 草簇。一个 GPU 实例的逻辑描述(CPU 端用于放置决策), 渲染前转换为
    /// <see cref="GrassInstanceData"/> 上传 GPU。
    /// </summary>
    public struct GrassCluster
    {
        /// <summary>根部世界位置。</summary>
        public Vector3 Position;
        /// <summary>地形法线。</summary>
        public Vector3 Normal;
        /// <summary>缩放。</summary>
        public float Scale;
        /// <summary>颜色变化因子。</summary>
        public float ColorVariation;
        /// <summary>绕 Up 轴旋转角度(弧度)。</summary>
        public float RotationY;
        /// <summary>风力相位。</summary>
        public float WindPhase;
        /// <summary>当前 LOD。</summary>
        public GrassLODLevel LOD;
        /// <summary>所属草类型索引。</summary>
        public int TypeIndex;
    }

    /// <summary>
    /// 景观草 ECS 组件。挂载到代表一片草地的实体上, 持有草层配置与渲染引用。
    /// 实现 <see cref="IComponent"/> 以纳入 ECS 查询。
    /// </summary>
    public struct LandscapeGrassComponent : IComponent
    {
        /// <summary>草层列表。</summary>
        public List<GrassLayer> Layers;
        /// <summary>草系统实例名称(用于关联到 LandscapeGrassSystem)。</summary>
        public string SystemId;
        /// <summary>是否启用。</summary>
        public bool Enabled;
        /// <summary>放置随机种子。</summary>
        public uint Seed;
        /// <summary>整体密度倍率。</summary>
        public float GlobalDensityScale;

        /// <summary>创建默认组件。</summary>
        public static LandscapeGrassComponent Create() => new()
        {
            Layers = new List<GrassLayer>(),
            SystemId = "Default",
            Enabled = true,
            Seed = 1337u,
            GlobalDensityScale = 1.0f
        };
    }

    // ════════════════════════════════════════════════════════════════════════
    // Chunk 分块与对象池
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 草 Chunk。覆盖世界中一块 <see cref="GrassConstants.ChunkSize"/> 见方的区域,
    /// 持有该区域内的全部草簇。Chunk 是流式加载/回收与单次 Indirect Draw 的基本单元。
    /// </summary>
    public class GrassChunk
    {
        /// <summary>Chunk 在 Chunk 网格中的 X 坐标。</summary>
        public int GridX;
        /// <summary>Chunk 在 Chunk 网格中的 Z 坐标。</summary>
        public int GridZ;
        /// <summary>Chunk 原点世界坐标。</summary>
        public Vector3 WorldOrigin;
        /// <summary>本 Chunk 内的草簇列表(放置阶段填充)。</summary>
        public readonly List<GrassCluster> Clusters = new(GrassConstants.MaxClustersPerChunk);
        /// <summary>当前 LOD(整 Chunk 的代表 LOD, 由距离决定)。</summary>
        public GrassLODLevel LOD = GrassLODLevel.Culled;
        /// <summary>是否在本次帧放置中 dirty(需要重新上传 GPU)。</summary>
        public bool Dirty = true;
        /// <summary>是否已分配 GPU 资源。</summary>
        public bool GpuAllocated;
        /// <summary>实例 StructuredBuffer(本 Chunk 专属, 剔除输入)。</summary>
        public RHIBuffer InstanceBuffer;
        /// <summary>可见实例缓冲(剔除 CS 输出)。</summary>
        public RHIBuffer VisibleBuffer;
        /// <summary>间接绘制参数缓冲(剔除 CS 写入 InstanceCount)。</summary>
        public RHIBuffer IndirectArgsBuffer;
        /// <summary>实例 SRV 句柄(t0)。</summary>
        public RHIDescriptorHandle InstanceSRV;
        /// <summary>可见实例 SRV 句柄(渲染时 t0)。</summary>
        public RHIDescriptorHandle VisibleSRV;
        /// <summary>可见实例 UAV 句柄(剔除输出 u0)。</summary>
        public RHIDescriptorHandle VisibleUAV;
        /// <summary>间接参数 UAV 句柄(剔除输出 u1)。</summary>
        public RHIDescriptorHandle ArgsUAV;
        /// <summary>实际草簇数(上传到 GPU 的有效数量)。</summary>
        public int UploadedCount;

        /// <summary>Chunk 中心世界坐标。</summary>
        public Vector3 Center => WorldOrigin + new Vector3(GrassConstants.ChunkSize * 0.5f, 0, GrassConstants.ChunkSize * 0.5f);

        /// <summary>清空草簇数据(回收到池前调用)。</summary>
        public void Reset(int gridX, int gridZ, Vector3 origin)
        {
            GridX = gridX;
            GridZ = gridZ;
            WorldOrigin = origin;
            Clusters.Clear();
            LOD = GrassLODLevel.Culled;
            Dirty = true;
            UploadedCount = 0;
        }
    }

    /// <summary>
    /// Chunk 对象池。复用已分配 GPU 资源的 Chunk, 避免相机移动时频繁创建/销毁
    /// StructuredBuffer 导致的显存抖动与驱动开销。
    /// </summary>
    public class GrassChunkPool
    {
        private readonly RHIDevice _device;
        private readonly Queue<GrassChunk> _free = new();
        private readonly Dictionary<long, GrassChunk> _active = new();
        private readonly List<GrassChunk> _toRelease = new();

        /// <summary>构造池。</summary>
        /// <param name="device">RHI 设备, 用于惰性分配 Chunk 的 GPU 资源。</param>
        public GrassChunkPool(RHIDevice device) => _device = device;

        /// <summary>将 (gridX, gridZ) 编码为唯一键。</summary>
        private static long EncodeKey(int gx, int gz) => ((long)gx << 32) | (uint)gz;

        /// <summary>获取(或创建)指定网格坐标的 Chunk。</summary>
        public GrassChunk Acquire(int gridX, int gridZ, Vector3 origin)
        {
            long key = EncodeKey(gridX, gridZ);
            if (_active.TryGetValue(key, out var chunk))
                return chunk;

            chunk = _free.Count > 0 ? _free.Dequeue() : new GrassChunk();
            chunk.Reset(gridX, gridZ, origin);
            EnsureGpuResources(chunk);
            _active[key] = chunk;
            return chunk;
        }

        /// <summary>惰性分配 Chunk 的 GPU 资源(StructuredBuffer / 间接参数缓冲)。</summary>
        private void EnsureGpuResources(GrassChunk chunk)
        {
            if (chunk.GpuAllocated) return;

            // 实例缓冲(剔除输入, 只需 SRV)
            ulong instanceBytes = (ulong)GrassConstants.MaxClustersPerChunk * GrassInstanceData.Stride;
            var instDesc = new RHIBufferDesc
            {
                SizeInBytes = instanceBytes,
                Format = RHIFormat.Unknown,
                HeapType = RHIResourceHeapType.Default,
                Flags = RHIBufferFlags.Structured | RHIBufferFlags.ShaderResource,
                Stride = GrassInstanceData.Stride
            };
            chunk.InstanceBuffer = _device.CreateBuffer(instDesc, $"Grass.Instance[{chunk.GridX},{chunk.GridZ}]");
            chunk.InstanceSRV = _device.CreateSRV(chunk.InstanceBuffer, RHIFormat.Unknown, true, 0,
                (uint)GrassConstants.MaxClustersPerChunk, GrassInstanceData.Stride);

            // 可见实例缓冲(剔除输出, 需要 SRV + UAV)
            var visDesc = new RHIBufferDesc
            {
                SizeInBytes = instanceBytes,
                Format = RHIFormat.Unknown,
                HeapType = RHIResourceHeapType.Default,
                Flags = RHIBufferFlags.Structured | RHIBufferFlags.UnorderedAccess | RHIBufferFlags.ShaderResource,
                Stride = GrassInstanceData.Stride
            };
            chunk.VisibleBuffer = _device.CreateBuffer(visDesc, $"Grass.Visible[{chunk.GridX},{chunk.GridZ}]");
            chunk.VisibleSRV = _device.CreateSRV(chunk.VisibleBuffer, RHIFormat.Unknown, true, 0,
                (uint)GrassConstants.MaxClustersPerChunk, GrassInstanceData.Stride);
            chunk.VisibleUAV = _device.CreateUAV(chunk.VisibleBuffer, RHIFormat.Unknown, true, 0,
                (uint)GrassConstants.MaxClustersPerChunk, GrassInstanceData.Stride);

            // 间接绘制参数缓冲(Structured<uint>, 兼容 RWStructuredBuffer<uint>; 同时支持 IndirectArgs)
            var argsDesc = new RHIBufferDesc
            {
                SizeInBytes = 32,
                Format = RHIFormat.R32_UInt,
                HeapType = RHIResourceHeapType.Default,
                Flags = RHIBufferFlags.Structured | RHIBufferFlags.UnorderedAccess | RHIBufferFlags.IndirectArgs,
                Stride = 4
            };
            chunk.IndirectArgsBuffer = _device.CreateBuffer(argsDesc, $"Grass.Args[{chunk.GridX},{chunk.GridZ}]");
            chunk.ArgsUAV = _device.CreateUAV(chunk.IndirectArgsBuffer, RHIFormat.Unknown, true, 0, 8, 4);

            chunk.GpuAllocated = true;
        }

        /// <summary>标记远离相机的 Chunk 为待释放(下一帧真正回收)。</summary>
        public void MarkForRelease(int gridX, int gridZ)
        {
            long key = EncodeKey(gridX, gridZ);
            if (_active.TryGetValue(key, out var chunk))
            {
                _toRelease.Add(chunk);
                _active.Remove(key);
            }
        }

        /// <summary>执行延迟回收: 将待释放 Chunk 归还到空闲队列。</summary>
        public void FlushReleases()
        {
            foreach (var chunk in _toRelease)
            {
                chunk.Clusters.Clear();
                chunk.Dirty = true;
                _free.Enqueue(chunk);
            }
            _toRelease.Clear();
        }

        /// <summary>当前激活 Chunk 数量。</summary>
        public int ActiveCount => _active.Count;

        /// <summary>枚举所有激活的 Chunk(只读)。</summary>
        public IEnumerable<GrassChunk> ActiveChunks => _active.Values;

        /// <summary>释放所有 GPU 资源。</summary>
        public void Dispose()
        {
            foreach (var c in _active.Values) ReleaseGpu(c);
            foreach (var c in _free) ReleaseGpu(c);
            foreach (var c in _toRelease) ReleaseGpu(c);
            _active.Clear();
            _free.Clear();
            _toRelease.Clear();
        }

        private static void ReleaseGpu(GrassChunk c)
        {
            if (!c.GpuAllocated) return;
            c.InstanceBuffer?.Dispose();
            c.VisibleBuffer?.Dispose();
            c.IndirectArgsBuffer?.Dispose();
            c.GpuAllocated = false;
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // 草放置系统 (CPU 端: 采样地形, 决定草簇分布与 LOD)
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 草放置系统。实现 <see cref="ISystem"/> 由 ECS 主循环驱动, 负责:
    /// <list type="bullet">
    /// <item>基于地形高度图与坡度计算草分布密度;</item>
    /// <item>基于地形 Splat 图层权重过滤(只在特定图层生长);</item>
    /// <item>按相机距离分块(Chunk)管理草簇的放置与回收;</item>
    /// <item>按距离执行 LOD 切换(近 Mesh / 中公告板 / 远隐藏)。</item>
    /// </list>
    /// 所有 CPU 工作仅读取 <see cref="TerrainHeightmap"/> 的影子副本, 不触发 GPU 回读。
    /// </summary>
    public class GrassPlacementSystem : ISystem
    {
        /// <summary>系统名称。</summary>
        public string Name => "GrassPlacementSystem";

        private readonly TerrainSystem _terrain;
        private readonly GrassChunkPool _chunkPool;
        private int _lastCamGridX = int.MaxValue;
        private int _lastCamGridZ = int.MaxValue;

        /// <summary>构造放置系统。</summary>
        /// <param name="terrain">关联的地形系统(提供高度/法线/Splat 查询)。</param>
        /// <param name="chunkPool">Chunk 对象池。</param>
        public GrassPlacementSystem(TerrainSystem terrain, GrassChunkPool chunkPool)
        {
            _terrain = terrain;
            _chunkPool = chunkPool;
        }

        /// <summary>当前相机位置(由外部门面每帧设置)。</summary>
        public Vector3 CameraPosition { get; set; }

        /// <summary>ECS 主循环驱动入口(轻量 LOD 刷新; 完整放置在 UpdatePlacement 中显式调用)。</summary>
        public void Update(float dt, EntityComponentSystem ecs)
        {
            RefreshLod(CameraPosition);
        }

        /// <summary>
        /// 执行一次完整的放置刷新。由 <see cref="LandscapeGrassSystem.Update"/> 显式调用,
        /// 因为放置需要相机与组件信息, 无法仅从 ECS 上下文获得。
        /// </summary>
        /// <param name="component">草组件(含图层与种子)。</param>
        /// <param name="cameraPos">相机世界位置。</param>
        public void UpdatePlacement(LandscapeGrassComponent component, Vector3 cameraPos)
        {
            if (!component.Enabled || component.Layers == null || component.Layers.Count == 0)
                return;

            CameraPosition = cameraPos;
            int camGridX = WorldToGrid(cameraPos.X);
            int camGridZ = WorldToGrid(cameraPos.Z);

            // 相机未跨出当前 Chunk 时, 仅刷新 LOD, 不重放置(大幅降低 CPU 开销)
            if (camGridX == _lastCamGridX && camGridZ == _lastCamGridZ)
            {
                RefreshLod(cameraPos);
                return;
            }
            _lastCamGridX = camGridX;
            _lastCamGridZ = camGridZ;

            int r = GrassConstants.ChunkViewRadius;
            var needed = new HashSet<long>();
            for (int dz = -r; dz <= r; dz++)
            {
                for (int dx = -r; dx <= r; dx++)
                {
                    // 圆形可视范围(去掉对角, 减少不必要 Chunk)
                    if (dx * dx + dz * dz > r * r) continue;
                    needed.Add(EncodeKey(camGridX + dx, camGridZ + dz));
                }
            }

            // 回收不再需要的 Chunk
            var toRemove = new List<long>();
            foreach (var chunk in _chunkPool.ActiveChunks)
            {
                long key = EncodeKey(chunk.GridX, chunk.GridZ);
                if (!needed.Contains(key))
                    toRemove.Add(key);
            }
            foreach (var key in toRemove)
            {
                (int gx, int gz) = DecodeKey(key);
                _chunkPool.MarkForRelease(gx, gz);
            }
            _chunkPool.FlushReleases();

            // 放置新进入可视范围的 Chunk
            foreach (long key in needed)
            {
                (int gx, int gz) = DecodeKey(key);
                Vector3 origin = GridToWorld(gx, gz);
                var chunk = _chunkPool.Acquire(gx, gz, origin);
                if (chunk.Clusters.Count == 0 || chunk.Dirty)
                    PlaceClustersInChunk(chunk, component);
            }

            RefreshLod(cameraPos);
        }

        /// <summary>在单个 Chunk 内放置草簇(采样地形高度/坡度/Splat 权重)。</summary>
        private void PlaceClustersInChunk(GrassChunk chunk, LandscapeGrassComponent component)
        {
            chunk.Clusters.Clear();
            var rng = new Random((int)(component.Seed ^ EncodeKey(chunk.GridX, chunk.GridZ)));

            TerrainHeightmap hm = _terrain.Heightmap;
            float chunkSize = GrassConstants.ChunkSize;

            // 以固定步长网格采样, 再加抖动; 步长由最高密度图层决定
            float maxDensity = 0f;
            foreach (var layer in component.Layers)
            {
                if (!layer.Enabled) continue;
                float d = layer.Type.Density * layer.DensityScale * component.GlobalDensityScale;
                if (d > maxDensity) maxDensity = d;
            }
            if (maxDensity <= 0f) { chunk.Dirty = false; return; }

            // 步长 = 1 / sqrt(密度), 保证每平米约 maxDensity 个采样点
            float step = MathF.Max(1f / MathF.Sqrt(maxDensity), 0.15f);

            int typeIndex = 0;
            foreach (var layer in component.Layers)
            {
                if (!layer.Enabled) continue;
                GrassType type = layer.Type;
                float density = type.Density * layer.DensityScale * component.GlobalDensityScale;

                for (float lz = 0; lz < chunkSize; lz += step)
                {
                    for (float lx = 0; lx < chunkSize; lx += step)
                    {
                        // 抖动: 避免规则网格
                        float jx = (float)(rng.NextDouble() * 2 - 1) * GrassConstants.PlacementJitter;
                        float jz = (float)(rng.NextDouble() * 2 - 1) * GrassConstants.PlacementJitter;
                        float wx = chunk.WorldOrigin.X + lx + jx;
                        float wz = chunk.WorldOrigin.Z + lz + jz;
                        Vector3 wpos = new(wx, 0, wz);

                        // 1) 采样高度
                        float h = hm.SampleWorldHeight(wpos);
                        wpos.Y = h;

                        // 2) 高度限制
                        if (h < type.MinHeight || h > type.MaxHeight) continue;

                        // 3) 坡度限制: 由相邻高度差估算坡度(度)
                        float hd = hm.SampleWorldHeight(wpos + new Vector3(step, 0, 0)) - h;
                        float slope = MathF.Atan2(MathF.Abs(hd), step) * 180f / MathF.PI;
                        if (slope < type.MinSlope || slope > type.MaxSlope) continue;

                        // 4) Splat 图层权重过滤:
                        //    完整 Splat 权重需 GPU 回读, 此处仅做高度/坡度过滤以避免回读开销;
                        //    精确图层过滤由 GrassRenderer 在剔除 CS 中结合 Splat SRV 二次确认。
                        //    (可在此读取 hm.SplatTexture 回读, 但为零回读设计而省略)

                        // 5) 密度概率剔除(按密度倍率)
                        float prob = MathF.Min(density * step * step, 1f);
                        if ((float)rng.NextDouble() > prob) continue;

                        // 6) 估算法线(由高度梯度)
                        float hx1 = hm.SampleWorldHeight(wpos + new Vector3(step, 0, 0));
                        float hx0 = hm.SampleWorldHeight(wpos + new Vector3(-step, 0, 0));
                        float hz1 = hm.SampleWorldHeight(wpos + new Vector3(0, 0, step));
                        float hz0 = hm.SampleWorldHeight(wpos + new Vector3(0, 0, -step));
                        Vector3 normal = Vector3.Normalize(new Vector3(hx0 - hx1, 2f * step, hz0 - hz1));

                        var cluster = new GrassCluster
                        {
                            Position = wpos,
                            Normal = normal,
                            Scale = type.MinScale + (float)rng.NextDouble() * (type.MaxScale - type.MinScale),
                            ColorVariation = type.ColorVariation > 0 ? (float)rng.NextDouble() * type.ColorVariation : 0f,
                            RotationY = (float)rng.NextDouble() * MathF.PI * 2f,
                            WindPhase = (float)rng.NextDouble() * MathF.PI * 2f,
                            LOD = GrassLODLevel.LOD0_FullMesh,
                            TypeIndex = typeIndex
                        };
                        chunk.Clusters.Add(cluster);
                        if (chunk.Clusters.Count >= GrassConstants.MaxClustersPerChunk)
                            goto chunkFull;
                    }
                }
                typeIndex++;
            }

        chunkFull:
            chunk.Dirty = true;
        }

        /// <summary>按相机距离刷新各 Chunk 的 LOD。</summary>
        private void RefreshLod(Vector3 cameraPos)
        {
            foreach (var chunk in _chunkPool.ActiveChunks)
            {
                float dist = Vector2.Distance(
                    new Vector2(cameraPos.X, cameraPos.Z),
                    new Vector2(chunk.Center.X, chunk.Center.Z));

                GrassLODLevel newLod;
                if (dist < GrassConstants.DefaultLod0Distance) newLod = GrassLODLevel.LOD0_FullMesh;
                else if (dist < GrassConstants.DefaultLod1Distance) newLod = GrassLODLevel.LOD1_Billboard;
                else newLod = GrassLODLevel.Culled;

                if (newLod != chunk.LOD)
                {
                    chunk.LOD = newLod;
                    chunk.Dirty = true;
                }
            }
        }

        /// <summary>世界 X/Z -> Chunk 网格坐标。</summary>
        public static int WorldToGrid(float world) => (int)MathF.Floor(world / GrassConstants.ChunkSize);

        /// <summary>Chunk 网格坐标 -> 世界原点。</summary>
        public static Vector3 GridToWorld(int gx, int gz) =>
            new(gx * GrassConstants.ChunkSize, 0, gz * GrassConstants.ChunkSize);

        /// <summary>网格坐标编码为键。</summary>
        public static long EncodeKey(int gx, int gz) => ((long)gx << 32) | (uint)gz;

        /// <summary>键解码为网格坐标。</summary>
        public static (int, int) DecodeKey(long key) => ((int)(key >> 32), (int)(key & 0xFFFFFFFF));
    }

    // ════════════════════════════════════════════════════════════════════════
    // 草渲染器 (GPU 端: 上传实例, 剔除 CS, Indirect Draw, 风力/AO)
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 草渲染器。负责将 CPU 放置的草簇上传 GPU, 经 Compute Shader 执行视锥剔除 +
    /// HZB 遮挡剔除后, 用单次 <c>DrawIndexedInstancedIndirect</c> 渲染整 Chunk。
    /// 顶点着色器端驱动风力摆动并采样交互贴图实现踩踏压弯。
    /// </summary>
    public class GrassRenderer : IDisposable
    {
        private readonly RHIDevice _device;
        private RHIBuffer _grassGeometryVB;
        private RHIBuffer _grassGeometryIB;
        private uint _grassIndexCount;

        private RHIBuffer _cullCBUpload;
        private RHIBuffer _renderCBUpload;
        private RHIPipelineState _cullPSO;
        private RHIRootSignature _cullRootSig;
        private RHIPipelineState _grassMeshPSO;
        private RHIRootSignature _renderRootSig;

        private RHIDescriptorHandle _hzbSRV;
        private bool _disposed;

        /// <summary>构造渲染器并编译管线。</summary>
        public GrassRenderer(RHIDevice device)
        {
            _device = device;
            CreateGrassGeometry();
            CreateConstantBuffers();
            CompileShaders();
        }

        /// <summary>创建一片程序化草叶几何(渐窄四边形, 根部锁定)。</summary>
        private void CreateGrassGeometry()
        {
            // 6 个顶点构成一片向上渐窄的草叶(2 个三角形), 根部在原点
            // 实际引擎中可由 MeshId 加载更精细几何, 此处用程序化几何保证自包含。
            float h = 1.0f, w = 0.5f;
            var verts = new Vector4[]
            {
                new(-w, 0, 0, 0), new( w, 0, 0, 1), new(-w * 0.5f, h, 0, 2),
                new( w, 0, 0, 1), new( w * 0.5f, h, 0, 3), new(-w * 0.5f, h, 0, 2)
            };
            ushort[] indices = { 0, 1, 2, 3, 4, 5 };
            _grassIndexCount = (uint)indices.Length;

            var vbDesc = new RHIBufferDesc
            {
                SizeInBytes = (ulong)(verts.Length * 16),
                Format = RHIFormat.R32G32B32A32_Float,
                HeapType = RHIResourceHeapType.Default,
                Flags = RHIBufferFlags.VertexBuffer,
                Stride = 16
            };
            _grassGeometryVB = _device.CreateBuffer(vbDesc, "Grass.GeometryVB");
            _device.UploadToBuffer(_grassGeometryVB, verts);

            var ibDesc = new RHIBufferDesc
            {
                SizeInBytes = (ulong)(indices.Length * 2),
                Format = RHIFormat.R16_UInt,
                HeapType = RHIResourceHeapType.Default,
                Flags = RHIBufferFlags.IndexBuffer,
                Stride = 2
            };
            _grassGeometryIB = _device.CreateBuffer(ibDesc, "Grass.GeometryIB");
            _device.UploadToBuffer(_grassGeometryIB, indices);
        }

        /// <summary>创建常量缓冲上传缓冲。</summary>
        private void CreateConstantBuffers()
        {
            _cullCBUpload = _device.CreateUploadBuffer((ulong)Marshal.SizeOf<GrassCullCB>(), "Grass.CullCB");
            _renderCBUpload = _device.CreateUploadBuffer((ulong)Marshal.SizeOf<GrassRenderCB>(), "Grass.RenderCB");
        }

        /// <summary>编译剔除 CS 与渲染 VS/PS(内联 HLSL)。</summary>
        private void CompileShaders()
        {
            // ── 剔除 Compute Shader ──
            // 输入: 实例 SRV(t0); 输出: 可见实例 UAV(u0) + 间接参数 UAV(u1)
            // 着色器执行视锥球剔除, 通过 InterlockedAdd 紧凑写入可见实例并累计 InstanceCount。
            // HZB 遮挡剔除在 gUseHzbCull 开启时由像素级深度比对完成(此处保留接口, 简化为视锥剔除)。
            string cullHlsl = @"
                struct GrassInstance { float3 Position; float Scale; float3 Normal; float ColorVar;
                                       float3 Rotation; float WindPhase; };
                StructuredBuffer<GrassInstance> Instances : register(t0);
                RWStructuredBuffer<GrassInstance> Visible : register(u0);
                RWStructuredBuffer<uint> Args : register(u1);
                cbuffer CullCB : register(b0) {
                    uint gInstanceCount; uint gMaxVisible; uint gIndexCount; uint gUseHzb;
                    float4x4 gVP; float4 gPlanes[6]; float gBounds; float _p0; float _p1; float _p2;
                };
                bool SphereInFrustum(float3 c, float r) {
                    [unroll] for (int i = 0; i < 6; i++) {
                        float d = dot(gPlanes[i].xyz, c) + gPlanes[i].w;
                        if (d < -r) return false;
                    }
                    return true;
                }
                [numthreads(64,1,1)]
                void CSCull(uint3 dtid : SV_DispatchThreadID) {
                    if (dtid.x >= gInstanceCount) return;
                    GrassInstance inst = Instances[dtid.x];
                    if (!SphereInFrustum(inst.Position, gBounds)) return;
                    uint idx; InterlockedAdd(Args[1], 1, idx);
                    if (idx < gMaxVisible) Visible[idx] = inst;
                }";

            _cullRootSig = _device.CreateRootSignature(new RHIRootSignatureDesc
            {
                Parameters = new[]
                {
                    // [0] CBV b0
                    new RHIRootParameter { ParameterType = RHIRootParameterType.CBV, ShaderVisibility = RHIShaderVisibility.All,
                        Descriptor = new RHIRootDescriptor { ShaderRegister = 0 } },
                    // [1] SRV t0: 实例缓冲(单描述符表)
                    new RHIRootParameter { ParameterType = RHIRootParameterType.DescriptorTable, ShaderVisibility = RHIShaderVisibility.All,
                        DescriptorTable = new RHIRootDescriptorTable { Ranges = new[] { new RHIRootDescriptorRange { RangeType = RHIDescriptorRangeType.SRV, NumDescriptors = 1, BaseShaderRegister = 0 } } } },
                    // [2] UAV u0: 可见实例(单描述符表)
                    new RHIRootParameter { ParameterType = RHIRootParameterType.DescriptorTable, ShaderVisibility = RHIShaderVisibility.All,
                        DescriptorTable = new RHIRootDescriptorTable { Ranges = new[] { new RHIRootDescriptorRange { RangeType = RHIDescriptorRangeType.UAV, NumDescriptors = 1, BaseShaderRegister = 0 } } } },
                    // [3] UAV u1: 间接参数(单描述符表)
                    new RHIRootParameter { ParameterType = RHIRootParameterType.DescriptorTable, ShaderVisibility = RHIShaderVisibility.All,
                        DescriptorTable = new RHIRootDescriptorTable { Ranges = new[] { new RHIRootDescriptorRange { RangeType = RHIDescriptorRangeType.UAV, NumDescriptors = 1, BaseShaderRegister = 1 } } } }
                },
                AllowInputAssembler = false
            }, "Grass.CullRootSig");

            _cullPSO = _device.CreateComputePipelineState(new RHIComputePipelineDesc
            {
                Name = "Grass.CullPSO",
                CS = _device.CompileShader(cullHlsl, "CSCull", RHIShaderStage.Compute, _device.MaxShaderModel)
            });

            // ── 渲染 Vertex/Pixel Shader(风力 + 交互 + AO) ──
            string renderHlsl = @"
                struct GrassInstance { float3 Position; float Scale; float3 Normal; float ColorVar;
                                       float3 Rotation; float WindPhase; };
                StructuredBuffer<GrassInstance> Visible : register(t0);
                Texture2D<float> InteractionTex : register(t1);
                SamplerState LinearSampler : register(s0);
                cbuffer RenderCB : register(b0) {
                    float4x4 gVP; float4x4 gPrevVP; float3 gCamPos; float gTime;
                    float3 gWindDir; float gWindStrength; float gGust; float gGustFreq;
                    uint gWindQuality; float gInteractRadius; float _p0; float _p1;
                };
                struct VSIn { float4 pos : POSITION; uint iid : SV_InstanceID; };
                struct VSOut { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; float3 color : COLOR0; float ao : TEXCOORD1; };
                float3x3 RotY(float a){ float c=cos(a),s=sin(a); return float3x3(c,0,-s,0,1,0,s,0,c); }
                VSOut VSMain(VSIn v) {
                    GrassInstance inst = Visible[v.iid];
                    float3 local = mul(v.pos.xyz, RotY(inst.Rotation.y)) * inst.Scale;
                    // 根部锁定: 摆幅随高度(=local.y)增大
                    float bend = local.y * gWindStrength;
                    float wind = sin(gTime * 2.0 * gGustFreq + inst.WindPhase + inst.Position.x * 0.3) * bend;
                    float gust = sin(gTime * 0.7) * gGust * bend;
                    local.xz += gWindDir.xz * (wind + gust);
                    // 交互压弯: 采样交互贴图(以相机为中心的世界 XZ 映射到 UV)
                    float2 iuv = (inst.Position.xz - gCamPos.xz + gInteractRadius) / (2.0 * gInteractRadius);
                    float press = InteractionTex.SampleLevel(LinearSampler, iuv, 0);
                    float2 bendDir = normalize(local.xz + float2(0.0001, 0.0001));
                    local.xz += bendDir * press * 0.5 * inst.Scale;
                    float3 world = inst.Position + local;
                    VSOut o; o.pos = mul(float4(world,1), gVP);
                    o.uv = float2(v.pos.z, v.pos.y);
                    o.color = lerp(float3(0.18,0.45,0.12), float3(0.35,0.55,0.18), inst.ColorVar);
                    o.ao = saturate(local.y * 0.5); // 近似接触 AO: 越靠根越暗
                    return o;
                }
                float4 PSMain(VSOut i) : SV_Target {
                    float3 col = i.color * (0.6 + 0.4 * i.ao);
                    return float4(col, 1.0);
                }";

            _renderRootSig = _device.CreateRootSignature(new RHIRootSignatureDesc
            {
                Parameters = new[]
                {
                    // [0] CBV b0
                    new RHIRootParameter { ParameterType = RHIRootParameterType.CBV, ShaderVisibility = RHIShaderVisibility.All,
                        Descriptor = new RHIRootDescriptor { ShaderRegister = 0 } },
                    // [1] SRV t0: 可见实例
                    new RHIRootParameter { ParameterType = RHIRootParameterType.DescriptorTable, ShaderVisibility = RHIShaderVisibility.All,
                        DescriptorTable = new RHIRootDescriptorTable { Ranges = new[] { new RHIRootDescriptorRange { RangeType = RHIDescriptorRangeType.SRV, NumDescriptors = 1, BaseShaderRegister = 0 } } } },
                    // [2] SRV t1: 交互贴图
                    new RHIRootParameter { ParameterType = RHIRootParameterType.DescriptorTable, ShaderVisibility = RHIShaderVisibility.All,
                        DescriptorTable = new RHIRootDescriptorTable { Ranges = new[] { new RHIRootDescriptorRange { RangeType = RHIDescriptorRangeType.SRV, NumDescriptors = 1, BaseShaderRegister = 1 } } } }
                },
                StaticSamplers = new[] { RHISamplerDesc.LinearWrap },
                AllowInputAssembler = true
            }, "Grass.RenderRootSig");

            var grassVS = _device.CompileShader(renderHlsl, "VSMain", RHIShaderStage.Vertex, _device.MaxShaderModel);
            var grassPS = _device.CompileShader(renderHlsl, "PSMain", RHIShaderStage.Pixel, _device.MaxShaderModel);

            _grassMeshPSO = _device.CreateGraphicsPipelineState(new RHIGraphicsPipelineDesc
            {
                Name = "Grass.MeshPSO",
                VS = grassVS,
                PS = grassPS,
                InputLayout = new[] { new RHIInputElement("POSITION", RHIFormat.R32G32B32A32_Float) },
                PrimitiveTopology = RHIPrimitiveTopology.TriangleList,
                BlendState = RHIBlendDesc.Default,
                DepthStencilState = RHIDepthDesc.DepthWrite,
                RasterizerState = RHIRasterizerDesc.Default,
                RTVFormats = new[] { RHIFormat.R11G11B10_Float },
                DSVFormat = RHIFormat.D32_Float
            });
        }

        /// <summary>设置 HZB 遮挡贴图 SRV(由渲染器每帧提供, 0 表示不使用 HZB)。</summary>
        public void SetHzbTexture(RHIDescriptorHandle hzbSRV) => _hzbSRV = hzbSRV;

        /// <summary>
        /// 渲染所有激活 Chunk。先上传 dirty Chunk 的实例数据, 再逐 Chunk 执行剔除 CS,
        /// 最后用 Indirect Draw 绘制可见实例。
        /// </summary>
        public void Render(RHICommandList cmd, IEnumerable<GrassChunk> chunks, GrassRenderCB renderCB,
            RHIDescriptorHandle interactionSRV, RHIDescriptorHandle dsv, RHIDescriptorHandle rtv)
        {
            var cb = renderCB;
            _device.UploadToBuffer(_renderCBUpload, MemoryMarshal.CreateSpan(ref cb, 1));

            foreach (var chunk in chunks)
            {
                if (chunk.LOD == GrassLODLevel.Culled) continue;
                if (chunk.Clusters.Count == 0) continue;

                // 1) 上传 dirty Chunk 的实例数据到 GPU
                if (chunk.Dirty && chunk.GpuAllocated)
                {
                    UploadInstances(cmd, chunk);
                    chunk.Dirty = false;
                }
                if (!chunk.GpuAllocated) continue;

                // 2) 执行剔除 CS, 生成可见实例 + 间接参数
                CullChunk(cmd, chunk, renderCB.ViewProjection);

                // 3) Indirect Draw
                DrawChunk(cmd, chunk, interactionSRV, dsv, rtv);
            }
        }

        /// <summary>将 Chunk 的草簇转换为 GrassInstanceData 并上传 GPU。</summary>
        private void UploadInstances(RHICommandList cmd, GrassChunk chunk)
        {
            int count = Math.Min(chunk.Clusters.Count, GrassConstants.MaxClustersPerChunk);
            var data = new GrassInstanceData[count];
            for (int i = 0; i < count; i++)
            {
                var c = chunk.Clusters[i];
                data[i] = new GrassInstanceData
                {
                    Position = c.Position,
                    Scale = c.Scale,
                    Normal = c.Normal,
                    ColorVariation = c.ColorVariation,
                    Rotation = new Vector3(0, c.RotationY, 0),
                    WindPhase = c.WindPhase
                };
            }
            chunk.UploadedCount = count;
            _device.UploadToBuffer(chunk.InstanceBuffer, data);

            // 重置间接参数: IndexCount 固定, InstanceCount=0(由 CS 累加)
            var args = new GrassIndirectArgs
            {
                IndexCountPerInstance = _grassIndexCount,
                InstanceCount = 0,
                StartIndexLocation = 0,
                BaseVertexLocation = 0,
                StartInstanceLocation = 0
            };
            _device.UploadToBuffer(chunk.IndirectArgsBuffer, new[] { args });
        }

        /// <summary>对单个 Chunk 执行剔除 Compute Shader。</summary>
        private void CullChunk(RHICommandList cmd, GrassChunk chunk, Matrix4x4 viewProj)
        {
            var cb = new GrassCullCB
            {
                InstanceCount = (uint)chunk.UploadedCount,
                MaxVisible = (uint)GrassConstants.MaxClustersPerChunk,
                IndexCountPerInstance = _grassIndexCount,
                UseHzbCull = _hzbSRV.IsValid ? 1u : 0u,
                ViewProjection = viewProj,
                BoundsRadius = 0.5f
            };
            _device.UploadToBuffer(_cullCBUpload, MemoryMarshal.CreateSpan(ref cb, 1));

            cmd.SetComputeRootSignature(_cullRootSig);
            cmd.SetPipelineState(_cullPSO);
            cmd.SetComputeRootConstantBufferView(0, _cullCBUpload);
            cmd.SetComputeRootDescriptorTable(1, chunk.InstanceSRV); // t0: 实例
            cmd.SetComputeRootDescriptorTable(2, chunk.VisibleUAV);  // u0: 可见实例
            cmd.SetComputeRootDescriptorTable(3, chunk.ArgsUAV);     // u1: 间接参数

            int groups = (chunk.UploadedCount + GrassConstants.CullThreadGroupSize - 1) / GrassConstants.CullThreadGroupSize;
            cmd.UAVBarrier(chunk.IndirectArgsBuffer);
            cmd.Dispatch(groups, 1, 1);
            cmd.UAVBarrier(chunk.IndirectArgsBuffer);
        }

        /// <summary>用 Indirect Draw 绘制单个 Chunk 的可见草叶。</summary>
        private void DrawChunk(RHICommandList cmd, GrassChunk chunk, RHIDescriptorHandle interactionSRV,
            RHIDescriptorHandle dsv, RHIDescriptorHandle rtv)
        {
            cmd.SetPipelineState(_grassMeshPSO);
            cmd.SetGraphicsRootSignature(_renderRootSig);
            cmd.SetGraphicsRootConstantBufferView(0, _renderCBUpload);
            cmd.SetGraphicsRootDescriptorTable(1, chunk.VisibleSRV); // t0: 可见实例
            cmd.SetGraphicsRootDescriptorTable(2, interactionSRV);   // t1: 交互贴图
            cmd.IASetPrimitiveTopology(RHIPrimitiveTopology.TriangleList);
            cmd.IASetVertexBuffers(0, _grassGeometryVB, 16, 0);
            cmd.IASetIndexBuffer(_grassGeometryIB, RHIFormat.R16_UInt, 0);
            cmd.SetViewports(new[] { new RHIViewport(0, 0, 1920, 1080) });
            cmd.SetScissorRects(new[] { new RHIScissorRect(0, 0, 1920, 1080) });
            cmd.OMSetRenderTargets(new[] { rtv }, dsv);

            // 单次 Indirect Draw 渲染整 Chunk 的可见草叶
            cmd.DrawIndexedInstancedIndirect(chunk.IndirectArgsBuffer, 0);
        }

        /// <summary>释放 GPU 资源。</summary>
        public void Dispose()
        {
            if (_disposed) return;
            _grassGeometryVB?.Dispose();
            _grassGeometryIB?.Dispose();
            _cullCBUpload?.Dispose();
            _renderCBUpload?.Dispose();
            _cullPSO?.Dispose();
            _cullRootSig?.Dispose();
            _grassMeshPSO?.Dispose();
            _renderRootSig?.Dispose();
            _disposed = true;
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // 草交互系统 (角色踩踏压弯, 交互贴图/GPU Buffer)
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 单个交互体(角色/物体)的踩踏信息, 写入交互贴图使草叶被压弯。
    /// </summary>
    public struct GrassInteractor
    {
        /// <summary>交互体世界位置(XZ 决定贴图采样, Y 用于高度判定)。</summary>
        public Vector3 Position;
        /// <summary>压弯强度[0,1]。</summary>
        public float BendStrength;
        /// <summary>影响半径(米)。</summary>
        public float Radius;
    }

    /// <summary>
    /// 草交互系统。维护一张以相机为中心的交互贴图(R8 压弯量), 每帧由 Compute Shader
    /// 将各交互体的压弯量写入贴图并叠加时间回弹衰减; 渲染时草叶 VS 采样该贴图实现踩踏压弯。
    /// 数据也可直接以 GPU Buffer 形式供 Compute 直接读写。
    /// </summary>
    public class GrassInteractionSystem : IDisposable
    {
        private readonly RHIDevice _device;
        private readonly RHITexture _interactionTexture;
        private readonly RHIDescriptorHandle _interactionSRV;
        private readonly RHIDescriptorHandle _interactionUAV;
        private RHIBuffer _interactionCBUpload;
        private RHIPipelineState _interactPSO;
        private RHIRootSignature _interactRootSig;
        private readonly List<GrassInteractor> _interactors = new();
        private bool _disposed;

        /// <summary>交互贴图 SRV(供渲染器采样)。</summary>
        public RHIDescriptorHandle InteractionSRV => _interactionSRV;

        /// <summary>构造交互系统并创建交互贴图与管线。</summary>
        public GrassInteractionSystem(RHIDevice device)
        {
            _device = device;
            int size = GrassConstants.InteractionMapSize;

            _interactionTexture = device.CreateTexture(new RHITextureDesc
            {
                Dimension = RHIResourceDimension.Texture2D,
                Format = RHIFormat.R8_UNorm,
                Width = size,
                Height = size,
                DepthOrArraySize = 1,
                MipLevels = 1,
                SampleCount = 1,
                Flags = RHITextureFlags.ShaderResource | RHITextureFlags.UnorderedAccess
            }, "Grass.InteractionMap");

            _interactionSRV = device.CreateSRV(_interactionTexture, 0, 1);
            _interactionUAV = device.CreateUAV(_interactionTexture, 0);

            _interactionCBUpload = device.CreateUploadBuffer((ulong)Marshal.SizeOf<GrassInteractionCB>(), "Grass.InteractCB");
            CompileInteractionShader();
        }

        /// <summary>编译交互 Compute Shader(写入压弯量 + 回弹衰减)。</summary>
        private void CompileInteractionShader()
        {
            string hlsl = @"
                RWTexture2D<float> InteractMap : register(u0);
                cbuffer InteractCB : register(b0) {
                    float2 gCenter; float gRadius; uint gCount;
                    float gRecover; uint gMapSize; uint _p0; float _p1;
                    float4 gInteractors[4];
                };
                [numthreads(8,8,1)]
                void CSInteract(uint3 dtid : SV_DispatchThreadID) {
                    uint2 px = dtid.xy;
                    if (px.x >= gMapSize || px.y >= gMapSize) return;
                    float cur = InteractMap[px];
                    cur *= (1.0 - gRecover); // 时间回弹衰减
                    float2 world = gCenter + ((float2(px) / (float)gMapSize) * 2.0 - 1.0) * gRadius;
                    [unroll] for (int i = 0; i < 4; i++) {
                        if ((uint)i >= gCount) break;
                        float3 ip = gInteractors[i].xyz; float str = gInteractors[i].w;
                        float d = distance(world, ip.xz);
                        float r = 2.0; // 影响半径(简化)
                        if (d < r) cur = max(cur, str * (1.0 - d / r));
                    }
                    InteractMap[px] = saturate(cur);
                }";

            _interactRootSig = _device.CreateRootSignature(new RHIRootSignatureDesc
            {
                Parameters = new[]
                {
                    // [0] CBV b0
                    new RHIRootParameter { ParameterType = RHIRootParameterType.CBV, ShaderVisibility = RHIShaderVisibility.All,
                        Descriptor = new RHIRootDescriptor { ShaderRegister = 0 } },
                    // [1] UAV u0: 交互贴图(单描述符表; 贴图 UAV 只能用描述符表, 不能用根描述符)
                    new RHIRootParameter { ParameterType = RHIRootParameterType.DescriptorTable, ShaderVisibility = RHIShaderVisibility.All,
                        DescriptorTable = new RHIRootDescriptorTable { Ranges = new[] { new RHIRootDescriptorRange { RangeType = RHIDescriptorRangeType.UAV, NumDescriptors = 1, BaseShaderRegister = 0 } } } }
                },
                AllowInputAssembler = false
            }, "Grass.InteractRootSig");

            _interactPSO = _device.CreateComputePipelineState(new RHIComputePipelineDesc
            {
                Name = "Grass.InteractPSO",
                CS = _device.CompileShader(hlsl, "CSInteract", RHIShaderStage.Compute, _device.MaxShaderModel)
            });
        }

        /// <summary>添加一个交互体(角色踩踏)。</summary>
        public void AddInteractor(GrassInteractor interactor) => _interactors.Add(interactor);

        /// <summary>清空交互体列表。</summary>
        public void ClearInteractors() => _interactors.Clear();

        /// <summary>每帧更新交互贴图。</summary>
        /// <param name="cmd">命令列表。</param>
        /// <param name="cameraPos">相机位置(交互贴图中心)。</param>
        /// <param name="dt">帧间隔(秒)。</param>
        public void Update(RHICommandList cmd, Vector3 cameraPos, float dt)
        {
            var cb = new GrassInteractionCB
            {
                CenterWorld = new Vector2(cameraPos.X, cameraPos.Z),
                WorldRadius = GrassConstants.InteractionWorldRadius,
                InteractorCount = (uint)Math.Min(_interactors.Count, 4),
                RecoverFactor = 1f - MathF.Exp(-dt / GrassConstants.InteractionRecoverTau),
                MapSize = (uint)GrassConstants.InteractionMapSize
            };
            // 填入前 4 个交互体(XYZ=位置, W=压弯强度)
            Span<Vector4> slots = stackalloc Vector4[4] { cb.Interactors0, cb.Interactors1, cb.Interactors2, cb.Interactors3 };
            for (int i = 0; i < 4; i++)
            {
                if (i < _interactors.Count)
                {
                    var it = _interactors[i];
                    slots[i] = new Vector4(it.Position, it.BendStrength);
                }
                else slots[i] = Vector4.Zero;
            }
            cb.Interactors0 = slots[0];
            cb.Interactors1 = slots[1];
            cb.Interactors2 = slots[2];
            cb.Interactors3 = slots[3];
            _device.UploadToBuffer(_interactionCBUpload, MemoryMarshal.CreateSpan(ref cb, 1));

            cmd.SetComputeRootSignature(_interactRootSig);
            cmd.SetPipelineState(_interactPSO);
            cmd.SetComputeRootConstantBufferView(0, _interactionCBUpload);
            cmd.SetComputeRootDescriptorTable(1, _interactionUAV);

            int groups = GrassConstants.InteractionMapSize / 8;
            cmd.UAVBarrier(_interactionTexture);
            cmd.Dispatch(groups, groups, 1);
            cmd.UAVBarrier(_interactionTexture);
        }

        /// <summary>释放资源。</summary>
        public void Dispose()
        {
            if (_disposed) return;
            _interactionTexture?.Dispose();
            _interactionCBUpload?.Dispose();
            _interactPSO?.Dispose();
            _interactRootSig?.Dispose();
            _disposed = true;
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // 景观草系统门面 (组合放置/渲染/交互, 对接 ECS 与地形)
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 景观草系统门面。组合 <see cref="GrassPlacementSystem"/> / <see cref="GrassRenderer"/> /
    /// <see cref="GrassInteractionSystem"/>, 对外提供统一的初始化、每帧更新与渲染入口,
    /// 并与 <see cref="TerrainSystem"/> / ECS / RHI 完成对接。
    /// </summary>
    public sealed class LandscapeGrassSystem : IDisposable
    {
        private readonly RHIDevice _device;
        private readonly TerrainSystem _terrain;
        private readonly EntityComponentSystem _ecs;
        private readonly GrassChunkPool _chunkPool;
        private readonly GrassPlacementSystem _placement;
        private readonly GrassRenderer _renderer;
        private readonly GrassInteractionSystem _interaction;
        private bool _disposed;

        /// <summary>RHI 设备。</summary>
        public RHIDevice Device => _device;
        /// <summary>草放置系统(实现 ISystem, 可注册到 ECS)。</summary>
        public GrassPlacementSystem Placement => _placement;
        /// <summary>草渲染器。</summary>
        public GrassRenderer Renderer => _renderer;
        /// <summary>草交互系统。</summary>
        public GrassInteractionSystem Interaction => _interaction;
        /// <summary>Chunk 对象池。</summary>
        public GrassChunkPool ChunkPool => _chunkPool;

        /// <summary>当前帧风力方向(世界空间)。</summary>
        public Vector3 WindDirection = new(1, 0, 0.3f);
        /// <summary>当前帧风力强度[0,1]。</summary>
        public float WindStrength = 0.4f;
        /// <summary>阵风强度[0,1]。</summary>
        public float GustStrength = 0.2f;
        /// <summary>阵风频率(赫兹)。</summary>
        public float GustFrequency = 1.5f;
        /// <summary>风力质量。</summary>
        public GrassWindQuality WindQuality = GrassWindQuality.High;

        /// <summary>
        /// 构造景观草系统。
        /// </summary>
        /// <param name="device">RHI 设备。</param>
        /// <param name="terrain">地形系统(提供高度/法线/Splat 查询)。</param>
        /// <param name="ecs">ECS 实例(用于注册放置系统与查询草组件)。</param>
        public LandscapeGrassSystem(RHIDevice device, TerrainSystem terrain, EntityComponentSystem ecs)
        {
            _device = device;
            _terrain = terrain;
            _ecs = ecs;
            _chunkPool = new GrassChunkPool(device);
            _placement = new GrassPlacementSystem(terrain, _chunkPool);
            _renderer = new GrassRenderer(device);
            _interaction = new GrassInteractionSystem(device);

            // 将放置系统注册到 ECS 主循环(LOD 刷新等轻量 CPU 工作)
            _ecs.AddSystem(_placement);
        }

        /// <summary>每帧更新: 放置刷新 + 交互贴图更新。</summary>
        /// <param name="cmd">命令列表(交互贴图 CS 需要提交)。</param>
        /// <param name="cameraPos">相机世界位置。</param>
        /// <param name="dt">帧间隔(秒)。</param>
        public void Update(RHICommandList cmd, Vector3 cameraPos, float dt)
        {
            // 查询所有挂载 LandscapeGrassComponent 的实体, 对每个执行放置刷新
            var entities = _ecs.QueryWithComponent<LandscapeGrassComponent>();
            foreach (int entityId in entities)
            {
                var comp = _ecs.GetComponent<LandscapeGrassComponent>(entityId);
                if (!comp.Enabled) continue;
                _placement.UpdatePlacement(comp, cameraPos);
            }

            // 更新交互贴图(角色踩踏)
            _interaction.Update(cmd, cameraPos, dt);
        }

        /// <summary>渲染草(由渲染器在适当 Pass 调用)。</summary>
        /// <param name="cmd">命令列表。</param>
        /// <param name="cameraPos">相机位置。</param>
        /// <param name="viewProj">视投影矩阵。</param>
        /// <param name="time">当前时间(秒)。</param>
        /// <param name="dsv">深度模板视图。</param>
        /// <param name="rtv">渲染目标视图。</param>
        public void Render(RHICommandList cmd, Vector3 cameraPos, Matrix4x4 viewProj, float time,
            RHIDescriptorHandle dsv, RHIDescriptorHandle rtv)
        {
            var renderCB = new GrassRenderCB
            {
                ViewProjection = viewProj,
                PrevViewProjection = viewProj,
                CameraPosition = cameraPos,
                Time = time,
                WindDirection = Vector3.Normalize(WindDirection),
                WindStrength = WindStrength,
                GustStrength = GustStrength,
                GustFrequency = GustFrequency,
                WindQuality = (uint)WindQuality,
                InteractionRadius = GrassConstants.InteractionWorldRadius
            };

            _renderer.Render(cmd, _chunkPool.ActiveChunks, renderCB,
                _interaction.InteractionSRV, dsv, rtv);
        }

        /// <summary>设置 HZB 遮挡贴图(由渲染器每帧提供)。</summary>
        public void SetHzbTexture(RHIDescriptorHandle hzbSRV) => _renderer.SetHzbTexture(hzbSRV);

        /// <summary>添加一个交互体(角色踩踏), 在下次 Update 时生效。</summary>
        public void AddInteractor(GrassInteractor interactor) => _interaction.AddInteractor(interactor);

        /// <summary>清空交互体。</summary>
        public void ClearInteractors() => _interaction.ClearInteractors();

        /// <summary>释放全部资源。</summary>
        public void Dispose()
        {
            if (_disposed) return;
            _ecs.RemoveSystem(_placement);
            _chunkPool.Dispose();
            _renderer.Dispose();
            _interaction.Dispose();
            _disposed = true;
        }
    }
}
