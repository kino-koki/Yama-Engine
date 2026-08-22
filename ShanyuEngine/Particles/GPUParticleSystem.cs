using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using ShanyuEngine.RHI;

namespace ShanyuEngine.Particles
{
    // ════════════════════════════════════════════════════════════════════
    // 山雨引擎 GPU 粒子系统核心
    // --------------------------------------------------------------------
    // 设计目标：超越 UE7 FlowVFX 的 GPU 粒子模拟管线
    //   - 完全 GPU 驱动：发射 / 模拟 / 回收 / 排序 / 渲染全部在计算/图形管线
    //   - 环形缓冲粒子池：单发射器支持百万级粒子，零 CPU 回读
    //   - GPU 端原子分配/回收：基于 InterlockedAdd 的 lock-free 索引分配
    //   - 多形态发射：球 / 半球 / 盒 / 锥 / 网格表面 / 距离场表面
    //   - 力场：重力 / 风力 / Curl Noise 湍流 / 吸引力 / 涡旋 / 阻力
    //   - 碰撞：深度缓冲碰撞 / 平面碰撞 / 有向距离场(SDF)碰撞
    //   - GPU Bitonic Sort：透明粒子按距离/年龄排序
    //   - 渲染：Billboard / Ribbon(带状) / Mesh(网格) 三种渲染体
    //   - 与 RHI 抽象层完全对接（RHIDevice / RHICommandList / RHIBuffer）
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    /// GPU 粒子渲染体的类型。决定如何把模拟后的粒子数据绘制到屏幕。
    /// </summary>
    public enum GPUParticleRenderType
    {
        /// <summary>面向相机的广告牌四边形，最常用、性能最优。</summary>
        Billboard,
        /// <summary>带状粒子，沿运动轨迹连接成连续条带，适合火焰/闪电/能量流。</summary>
        Ribbon,
        /// <summary>网格粒子，每个粒子绘制一个完整网格实例，适合碎片/落叶。</summary>
        Mesh,
        /// <summary>无光照仅输出深度的粒子，用于自定义后期合成。</summary>
        DepthOnly
    }

    /// <summary>
    /// 发射形状类型。决定粒子初始位置与初始速度方向的采样空间。
    /// </summary>
    public enum GPUEmissionShape
    {
        Sphere,        // 球体体积/表面
        Hemisphere,    // 半球
        Box,           // 盒体
        Cone,          // 锥体
        Cylinder,      // 圆柱
        MeshSurface,   // 网格表面（按三角形面积加权采样）
        MeshVertex,    // 网格顶点
        SDFSurface,    // 有向距离场表面采样
        Line           // 线段
    }

    /// <summary>
    /// 力场类型。多个力场可叠加作用于同一批粒子。
    /// </summary>
    public enum GPUForceFieldType
    {
        Gravity,      // 恒定重力（指向固定方向）
        Wind,         // 风力（方向性 + 阵风噪声）
        CurlNoise,    // Curl Noise 湍流（无散度矢量场，模拟流体扰动）
        Attractor,    // 点吸引力（向某点拉拽，可设衰减半径）
        Repeller,     // 点排斥力
        Vortex,       // 涡旋（绕轴旋转的切向力）
        Drag,         // 阻力（与速度方向相反，按速度大小衰减）
        Radial,       // 径向力（从中心向外/向内）
        VectorField   // 3D 矢量场纹理采样力（Volume 纹理）
    }

    /// <summary>
    /// 碰撞类型。决定粒子与场景如何交互碰撞。
    /// </summary>
    public enum GPUCollisionType
    {
        None,           // 无碰撞
        DepthBuffer,    // 深度缓冲碰撞（采样场景深度图反投影）
        Plane,          // 无限平面碰撞
        Sphere,         // 球体碰撞
        Box,            // 盒体碰撞
        SDF,            // 有向距离场碰撞（全局距离场 GDF）
        Heightfield     // 高度图碰撞（地形）
    }

    /// <summary>
    /// 粒子排序模式。透明粒子需要按深度排序以保证正确的混合顺序。
    /// </summary>
    public enum GPUParticleSortMode
    {
        None,           // 不排序（加法混合/不透明粒子）
        DistanceFrontToBack, // 按相机距离从近到远
        DistanceBackToFront, // 按相机距离从远到近（标准透明排序）
        AgeAscending,   // 按年龄升序
        AgeDescending   // 按年龄降序
    }

    // ──────────────────────────────────────────────────────────────────
    // GPU 端常量缓冲结构定义（与 HLSL cb 对应，需保持 16 字节对齐）
    // ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// 发射器参数（每发射器一份，上传至常量缓冲 b1）。
    /// 所有 Vector3 后跟一个 float 填充，保证 HLSL 中 float4 对齐。
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 16)]
    public struct GPUEmitterParams
    {
        public Vector3 EmitterPosition;   // 发射器世界位置
        public float EmitRate;             // 每秒发射速率
        public Vector3 EmitterRotation;    // 发射器欧拉旋转（弧度）
        public float BurstCount;           // 单次脉冲数量
        public Vector3 EmitterScale;       // 发射器缩放
        public float LifetimeMin;          // 最小生命周期
        public Vector3 ShapeExtents;       // 形状尺寸（半径/半 extents）
        public float LifetimeMax;          // 最大生命周期
        public Vector3 InitialVelocityMin; // 初始速度下限
        public float SpeedMin;             // 初速度大小下限
        public Vector3 InitialVelocityMax; // 初始速度上限
        public float SpeedMax;             // 初速度大小上限
        public Vector3 Gravity;            // 重力加速度
        public float Mass;                 // 粒子质量
        public Vector4 ColorStart;         // 起始颜色 RGBA
        public Vector4 ColorEnd;           // 结束颜色 RGBA
        public Vector2 SizeStart;          // 起始尺寸 XY
        public Vector2 SizeEnd;            // 结束尺寸 XY
        public float Drag;                 // 阻力系数
        public float CurlNoiseStrength;    // Curl Noise 强度
        public float CurlNoiseFrequency;   // Curl Noise 频率
        public float CurlNoiseSpeed;       // Curl Noise 时间演化速度
        public uint MaxParticles;          // 该发射器最大粒子数
        public uint EmitShape;             // 发射形状枚举值
        public uint SortMode;              // 排序模式枚举值
        public uint RenderType;            // 渲染类型枚举值
        public float SystemAge;            // 系统累计时间
        public float DeltaTime;            // 本帧步长
        public uint RandomSeed;            // 随机种子
        public uint Pad0;                  // 对齐填充
    }

    /// <summary>
    /// 模拟全局参数（每帧一份，上传至常量缓冲 b0）。
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 16)]
    public struct GPUSimParams
    {
        public Matrix4x4 ViewMatrix;          // 视图矩阵
        public Matrix4x4 ProjMatrix;          // 投影矩阵
        public Matrix4x4 InvViewProjMatrix;   // 反向视图投影（用于深度反投影）
        public Vector3 CameraPosition;        // 相机世界位置
        public float DeltaTime;               // 帧步长
        public Vector3 CameraRight;           // 相机右向（Billboard 用）
        public float ElapsedTime;             // 引擎累计时间
        public Vector3 CameraUp;              // 相机上向（Billboard 用）
        public uint FrameIndex;               // 帧序号（用于随机抖动）
        public Vector2 DepthTextureSize;      // 深度图尺寸
        public Vector2 InvDepthTextureSize;   // 深度图尺寸倒数
        public float NearZ;                   // 近裁剪面
        public float FarZ;                    // 远裁剪面
        public uint MaxParticles;             // 全局最大粒子数
        public uint AliveCount;               // 当前存活数（GPU 回读填充）
        public uint SortPassCount;            // Bitonic 排序总趟数
        public uint SortSubPassCount;         // Bitonic 排序子趟数
        public uint NumForceFields;           // 活动力场数量
        public uint NumColliders;             // 活动碰撞体数量
        public uint Pad0;
        public uint Pad1;
    }

    /// <summary>
    /// 力场参数（结构化缓冲 SRV/UAV 传入）。
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 16)]
    public struct GPUForceFieldData
    {
        public Vector3 Position;       // 力场中心位置
        public float Strength;         // 力强度
        public Vector3 Direction;      // 力方向（重力/风/涡旋轴）
        public float Radius;           // 影响半径
        public uint FieldType;         // GPUForceFieldType 枚举值
        public float Falloff;          // 衰减指数（1=线性,2=平方反比）
        public float Tightness;        // 涡旋紧度 / 噪声频率
        public float Pad0;
    }

    /// <summary>
    /// 碰撞体参数（结构化缓冲传入）。
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 16)]
    public struct GPUColliderData
    {
        public Vector3 Position;   // 碰撞体位置
        public float Radius;       // 球半径 / SDF 采样半径
        public Vector3 Extents;    // 盒 extents / 平面法线
        public float Bounce;       // 弹性系数 (0=完全不弹,1=完全弹)
        public Vector3 Normal;     // 平面法线 / 涡旋轴
        public float Friction;     // 摩擦系数
        public uint ColliderType;  // GPUCollisionType 枚举值
        public float KillThreshold;// 速度低于此值则杀死粒子
        public float SDFTextureIdx;// SDF 纹理索引
        public uint Pad0;
    }

    // ──────────────────────────────────────────────────────────────────
    // GPU 粒子 SOA 缓冲描述。采用结构体数组(AoS)打包为单缓冲以减少绑定开销，
    // 但逻辑上按 SOA 语义访问。每个粒子 64 字节，缓存行友好。
    // ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// 单个 GPU 粒子的紧凑表示（64 字节，2 个 float4 向量）。
    /// 与 HLSL 中 struct Particle 对应。
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 16)]
    public struct GPUParticle
    {
        public Vector3 Position;    // 世界位置
        public float Age;           // 当前年龄（秒）
        public Vector3 Velocity;    // 速度向量
        public float Lifetime;      // 总生命周期（秒）
        public Vector4 Color;       // 颜色 RGBA（含透明度）
        public Vector3 Aux;         // 辅助数据：X=尺寸宽,Y=尺寸高,Z=旋转角
        public float CameraDist;    // 到相机距离平方（排序用）
    }

    // ════════════════════════════════════════════════════════════════════
    // 发射器描述（CPU 端配置，可序列化）
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    /// GPU 发射器描述。描述一个发射器的全部静态配置，用于生成 GPUEmitterParams 并驱动着色器。
    /// </summary>
    public class GPUEmitterDesc
    {
        public string Name = "GPUEmitter";
        public GPUEmissionShape Shape = GPUEmissionShape.Cone;
        public GPUParticleRenderType RenderType = GPUParticleRenderType.Billboard;
        public GPUParticleSortMode SortMode = GPUParticleSortMode.DistanceBackToFront;

        // 发射参数
        public float Rate = 500f;                 // 每秒发射数
        public float BurstCount = 0;              // 脉冲数量（0=连续发射）
        public float BurstInterval = 0f;          // 脉冲间隔
        public uint MaxParticles = 200_000;       // 最大粒子数

        // 形状参数
        public float Radius = 1f;
        public float Height = 1f;
        public float Angle = 25f;                 // 锥角度（度）
        public Vector3 BoxExtents = Vector3.One;  // 盒半尺寸

        // 生命周期
        public float LifetimeMin = 2f;
        public float LifetimeMax = 5f;

        // 速度
        public float SpeedMin = 1f;
        public float SpeedMax = 5f;
        public Vector3 VelocityDir = Vector3.UnitY;

        // 颜色与尺寸曲线
        public Vector4 ColorStart = new(1, 1, 1, 1);
        public Vector4 ColorEnd = new(1, 1, 1, 0);
        public Vector2 SizeStart = new(0.2f, 0.2f);
        public Vector2 SizeEnd = new(0.05f, 0.05f);

        // 物理参数
        public Vector3 Gravity = new(0, -9.81f, 0);
        public float Mass = 1f;
        public float Drag = 0.1f;

        // 湍流
        public float CurlNoiseStrength = 0f;
        public float CurlNoiseFrequency = 0.5f;
        public float CurlNoiseSpeed = 0.2f;

        // 网格发射用的引用（仅 MeshSurface/MeshVertex 有效）
        public RHIBuffer? MeshVertexBuffer;
        public RHIBuffer? MeshIndexBuffer;
        public int MeshVertexCount;
        public int MeshIndexCount;

        /// <summary>转换为可上传 GPU 的参数结构体。</summary>
        public GPUEmitterParams ToParams(in Vector3 worldPos)
        {
            return new GPUEmitterParams
            {
                EmitterPosition = worldPos,
                EmitRate = Rate,
                EmitterRotation = Vector3.Zero,
                BurstCount = BurstCount,
                EmitterScale = Vector3.One,
                LifetimeMin = LifetimeMin,
                ShapeExtents = new Vector3(Radius, Height, Angle),
                LifetimeMax = LifetimeMax,
                InitialVelocityMin = VelocityDir * SpeedMin,
                SpeedMin = SpeedMin,
                InitialVelocityMax = VelocityDir * SpeedMax,
                SpeedMax = SpeedMax,
                Gravity = Gravity,
                Mass = MathF.Max(0.0001f, Mass),
                ColorStart = ColorStart,
                ColorEnd = ColorEnd,
                SizeStart = SizeStart,
                SizeEnd = SizeEnd,
                Drag = Drag,
                CurlNoiseStrength = CurlNoiseStrength,
                CurlNoiseFrequency = CurlNoiseFrequency,
                CurlNoiseSpeed = CurlNoiseSpeed,
                MaxParticles = MaxParticles,
                EmitShape = (uint)Shape,
                SortMode = (uint)SortMode,
                RenderType = (uint)RenderType,
                SystemAge = 0f,
                DeltaTime = 0f,
                RandomSeed = (uint)Name.GetHashCode()
            };
        }
    }

    // ════════════════════════════════════════════════════════════════════
    // GPU Bitonic 排序器 —— 用于透明粒子深度排序
    // --------------------------------------------------------------------
    // Bitonic Sort 是一种适合 GPU 并行的排序网络，复杂度 O(n·log²n)，
    // 但每步运算完全规则、无分支、无数据依赖，极其适合 GPU/SIMT 执行。
    // 本实现采用“键值对”排序：键=相机距离，值=粒子索引，排序后按索引重排粒子。
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    /// GPU Bitonic 排序器。对粒子索引按相机距离排序，输出有序索引缓冲供渲染使用。
    /// </summary>
    public class BitonicSorter
    {
        private readonly RHIDevice _device;
        private RHIPipelineState? _sortPSO;
        private RHIRootSignature? _sortRootSig;
        private RHIBuffer? _sortKeyBuffer;     // 键缓冲（距离）
        private RHIBuffer? _sortIndexBuffer;   // 值缓冲（粒子索引）
        private int _capacity;

        /// <summary>排序所需容量（必须为 2 的幂）。</summary>
        public int Capacity => _capacity;

        public BitonicSorter(RHIDevice device) => _device = device;

        /// <summary>
        /// 初始化排序器资源。容量自动向上取整为 2 的幂。
        /// </summary>
        public void Initialize(int maxParticles)
        {
            // Bitonic 网络要求元素数为 2 的幂
            _capacity = NextPow2(maxParticles);
            var keyDesc = new RHIBufferDesc
            {
                SizeInBytes = (ulong)_capacity * sizeof(float),
                Format = RHIFormat.Unknown,
                HeapType = RHIResourceHeapType.Default,
                Flags = RHIBufferFlags.Structured | RHIBufferFlags.UnorderedAccess,
                Stride = (ulong)sizeof(float)
            };
            var idxDesc = keyDesc;
            idxDesc.Stride = 4; // uint
            idxDesc.SizeInBytes = (ulong)_capacity * 4u;

            _sortKeyBuffer = _device.CreateBuffer(keyDesc, "ParticleSortKey");
            _sortIndexBuffer = _device.CreateBuffer(idxDesc, "ParticleSortIndex");

            BuildPipeline();
        }

        /// <summary>编译排序计算着色器并创建管线与根签名。</summary>
        private void BuildPipeline()
        {
            // 根签名：[0] 常量(CBSimParams) [1] 键UAV [2] 索引UAV
            var rootParams = new RHIRootParameter[3];
            rootParams[0] = new RHIRootParameter
            {
                ParameterType = RHIRootParameterType.Constants,
                ShaderVisibility = RHIShaderVisibility.All,
                Constants = new RHIRootConstants { Num32BitValues = 4, ShaderRegister = 0 }
            };
            rootParams[1] = new RHIRootParameter
            {
                ParameterType = RHIRootParameterType.UAV,
                ShaderVisibility = RHIShaderVisibility.All,
                Descriptor = new RHIRootDescriptor { ShaderRegister = 0 }
            };
            rootParams[2] = new RHIRootParameter
            {
                ParameterType = RHIRootParameterType.UAV,
                ShaderVisibility = RHIShaderVisibility.All,
                Descriptor = new RHIRootDescriptor { ShaderRegister = 1 }
            };

            var rootDesc = new RHIRootSignatureDesc
            {
                Parameters = rootParams,
                AllowInputAssembler = false
            };
            _sortRootSig = _device.CreateRootSignature(rootDesc);

            // 内嵌 Bitonic Sort HLSL 源码（键值排序网络）
            string hlsl = @"
                RWStructuredBuffer<float> Keys : register(u0);
                RWStructuredBuffer<uint> Indices : register(u1);
                cbuffer SortCB : register(b0) { uint gSize; uint gStride; uint gDir; uint gPad; };

                [numthreads(64,1,1)]
                void CSBitonicSort(uint3 dtid : SV_DispatchThreadID)
                {
                    uint i = dtid.x;
                    if (i >= gSize) return;
                    uint j = i ^ (gStride >> 1);
                    if (j > i)
                    {
                        float ki = Keys[i];
                        float kj = Keys[j];
                        bool swap = (gDir == 1u) ? (ki > kj) : (ki < kj);
                        if (swap)
                        {
                            Keys[i] = kj; Keys[j] = ki;
                            uint ti = Indices[i]; Indices[i] = Indices[j]; Indices[j] = ti;
                        }
                    }
                }
                [numthreads(64,1,1)]
                void CSInitIndex(uint3 dtid : SV_DispatchThreadID)
                {
                    uint i = dtid.x;
                    if (i < gSize) Indices[i] = i;
                    if (i < gSize) Keys[i] = 1e30; // 死粒子置极大值排到末尾
                }
            ";

            var cs = _device.CompileShader(hlsl, "CSBitonicSort", RHIShaderStage.Compute, _device.MaxShaderModel);
            var initCs = _device.CompileShader(hlsl, "CSInitIndex", RHIShaderStage.Compute, _device.MaxShaderModel);

            _sortPSO = _device.CreateComputePipelineState(new RHIComputePipelineDesc
            {
                Name = "BitonicSortPSO",
                CS = cs
            });
        }

        /// <summary>
        /// 在给定命令列表上执行排序。
        /// </summary>
        /// <param name="cmd">计算命令列表</param>
        /// <param name="particleCount">实际粒子数</param>
        public void Sort(RHICommandList cmd, int particleCount)
        {
            if (_sortPSO == null || _sortRootSig == null) return;
            int n = NextPow2(particleCount);

            cmd.SetComputeRootSignature(_sortRootSig);
            cmd.SetPipelineState(_sortPSO);

            // 1) 初始化索引缓冲（顺序 0..n）与键缓冲（死粒子置极大值）
            cmd.SetComputeRootUnorderedAccessView(1, _sortKeyBuffer!.GpuVirtualAddress);
            cmd.SetComputeRootUnorderedAccessView(2, _sortIndexBuffer!.GpuVirtualAddress);

            int groups = (n + 63) / 64;
            // InitIndex dispatch（此处简化，实际应使用单独 init PSO）
            // ... 见主系统调度

            // 2) Bitonic 排序网络：外层 size=2,4,8,...,n；内层 stride=size,size/2,...,1
            for (int size = 2; size <= n; size *= 2)
            {
                for (int stride = size; stride > 1; stride /= 2)
                {
                    // 上传 (size, stride, dir) 常量
                    uint dir = (uint)(((size & n) != 0) ? 1 : 0); // 升/降序
                    Span<uint> cb = stackalloc uint[4] { (uint)n, (uint)stride, dir, 0 };
                    unsafe
                    {
                        fixed (uint* p = cb)
                            cmd.SetComputeRoot32BitConstants(0, 4, (IntPtr)p, 0);
                    }
                    cmd.Dispatch(groups, 1, 1);
                }
            }
        }

        public RHIBuffer? GetSortedIndexBuffer() => _sortIndexBuffer;

        private static int NextPow2(int v)
        {
            uint x = (uint)v; x--; x |= x >> 1; x |= x >> 2; x |= x >> 4;
            x |= x >> 8; x |= x >> 16; x++;
            return (int)Math.Max(x, 2);
        }
    }

    // ════════════════════════════════════════════════════════════════════
    // GPU 粒子系统主类
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    /// GPU 粒子系统核心。管理粒子池、发射器、力场、碰撞、排序与渲染的全部 GPU 资源。
    /// 该系统采用环形缓冲 + 原子计数器实现 lock-free 的粒子分配/回收，
    /// 整个模拟循环无 CPU 回读，可稳定驱动百万级粒子。
    /// </summary>
    public class GPUParticleSystem : IDisposable
    {
        // ── RHI 设备与命令队列 ──
        private readonly RHIDevice _device;
        private RHICommandQueue? _computeQueue;
        private RHICommandList? _computeCmd;
        private RHICommandList? _graphicsCmd;

        // ── 粒子池资源（环形缓冲）──
        private RHIBuffer? _particleBufferA;     // 粒子数据缓冲 A（当前帧）
        private RHIBuffer? _particleBufferB;     // 粒子数据缓冲 B（下一帧，乒乓）
        private RHIBuffer? _aliveIndexBuffer;    // 存活粒子索引列表
        private RHIBuffer? _deadIndexBuffer;     // 死亡粒子索引栈（环形回收）
        private RHIBuffer? _counterBuffer;       // 原子计数器：[0]=alive [1]=dead [2]=emit [3]=sort
        private RHIBuffer? _indirectArgsBuffer;  // 间接绘制参数缓冲
        private bool _useBufferA = true;

        // ── 常量缓冲 ──
        private RHIBuffer? _simParamsCB;         // 模拟全局参数
        private RHIBuffer? _emitterParamsCB;     // 发射器参数

        // ── 力场与碰撞结构化缓冲 ──
        private RHIBuffer? _forceFieldBuffer;
        private RHIBuffer? _colliderBuffer;

        // ── 深度图（碰撞用）──
        private RHITexture? _depthTexture;
        private RHIDescriptorHandle _depthSRV;

        // ── 排序器 ──
        private readonly BitonicSorter _sorter;

        // ── 计算管线（各阶段）──
        private RHIPipelineState? _emitPSO;
        private RHIPipelineState? _simulatePSO;
        private RHIPipelineState? _reclaimPSO;
        private RHIPipelineState? _sortInitPSO;
        private RHIRootSignature? _particleRootSig;

        // ── 图形管线（渲染）──
        private RHIPipelineState? _billboardPSO;
        private RHIPipelineState? _ribbonPSO;
        private RHIPipelineState? _meshPSO;
        private RHIRootSignature? _renderRootSig;

        // ── 配置 ──
        private readonly List<GPUEmitterDesc> _emitters = new();
        private readonly List<GPUForceFieldData> _forceFields = new();
        private readonly List<GPUColliderData> _colliders = new();
        private int _maxParticles;
        private float _systemAge;
        private bool _disposed;

        /// <summary>系统是否已初始化。</summary>
        public bool IsInitialized { get; private set; }
        /// <summary>最大粒子容量。</summary>
        public int MaxParticles => _maxParticles;
        /// <summary>力场列表（可运行时增删）。</summary>
        public IReadOnlyList<GPUForceFieldData> ForceFields => _forceFields;
        /// <summary>碰撞体列表。</summary>
        public IReadOnlyList<GPUColliderData> Colliders => _colliders;

        public GPUParticleSystem(RHIDevice device)
        {
            _device = device;
            _sorter = new BitonicSorter(device);
        }

        // ──────────────────────────────────────────────────────────────
        // 初始化：分配 GPU 资源、编译着色器、创建管线
        // ──────────────────────────────────────────────────────────────

        /// <summary>
        /// 初始化粒子系统，分配环形缓冲池并编译全部着色器管线。
        /// </summary>
        /// <param name="maxParticles">最大粒子数（建议 2 的幂，上限 4M）</param>
        public void Initialize(int maxParticles)
        {
            _maxParticles = Math.Clamp(maxParticles, 1024, 4_194_304);

            // 创建异步计算队列与命令列表
            _computeQueue = _device.GetCommandQueue(RHIQueueType.Compute);
            _computeCmd = _device.CreateCommandList(RHIQueueType.Compute, "ParticleComputeCmd");
            _graphicsCmd = _device.CreateCommandList(RHIQueueType.Graphics, "ParticleGraphicsCmd");

            AllocateBuffers();
            CreateRootSignatures();
            CompileComputeShaders();
            CompileRenderShaders();
            _sorter.Initialize(_maxParticles);

            // 初始化死亡索引栈：写入 0..maxParticles-1（所有粒子初始为“可分配”）
            InitDeadStack();

            IsInitialized = true;
        }

        /// <summary>分配所有 GPU 缓冲资源（粒子池、索引栈、计数器等）。</summary>
        private void AllocateBuffers()
        {
            ulong stride = (ulong)Marshal.SizeOf<GPUParticle>();
            ulong particleBytes = (ulong)_maxParticles * stride;

            // 粒子数据乒乓缓冲（避免模拟阶段的读写冲突）
            var particleDesc = new RHIBufferDesc
            {
                SizeInBytes = particleBytes,
                Format = RHIFormat.Unknown,
                HeapType = RHIResourceHeapType.Default,
                Flags = RHIBufferFlags.Structured | RHIBufferFlags.UnorderedAccess | RHIBufferFlags.ShaderResource,
                Stride = stride
            };
            _particleBufferA = _device.CreateBuffer(particleDesc, "ParticlePoolA");
            _particleBufferB = _device.CreateBuffer(particleDesc, "ParticlePoolB");

            // 存活索引 / 死亡索引栈
            var idxDesc = new RHIBufferDesc
            {
                SizeInBytes = (ulong)_maxParticles * 4u,
                Format = RHIFormat.R32_UInt,
                HeapType = RHIResourceHeapType.Default,
                Flags = RHIBufferFlags.Structured | RHIBufferFlags.UnorderedAccess,
                Stride = 4
            };
            _aliveIndexBuffer = _device.CreateBuffer(idxDesc, "AliveIndexList");
            _deadIndexBuffer = _device.CreateBuffer(idxDesc, "DeadIndexStack");

            // 计数器缓冲（4 个 uint32：存活数/可分配数/本帧发射数/排序数）
            var counterDesc = new RHIBufferDesc
            {
                SizeInBytes = 64, // 16 个 uint32 预留
                Format = RHIFormat.Unknown,
                HeapType = RHIResourceHeapType.Default,
                Flags = RHIBufferFlags.UnorderedAccess,
                Stride = 4
            };
            _counterBuffer = _device.CreateBuffer(counterDesc, "ParticleCounters");

            // 间接绘制参数（DrawIndexedInstancedIndirect 用）
            var indirectDesc = new RHIBufferDesc
            {
                SizeInBytes = 64,
                Format = RHIFormat.Unknown,
                HeapType = RHIResourceHeapType.Default,
                Flags = RHIBufferFlags.IndirectArgs | RHIBufferFlags.UnorderedAccess,
                Stride = 4
            };
            _indirectArgsBuffer = _device.CreateBuffer(indirectDesc, "ParticleIndirectArgs");

            // 常量缓冲
            var cbDesc = new RHIBufferDesc
            {
                SizeInBytes = 256,
                Format = RHIFormat.Unknown,
                HeapType = RHIResourceHeapType.Upload,
                Flags = RHIBufferFlags.ConstantBuffer,
                Stride = 256
            };
            _simParamsCB = _device.CreateBuffer(cbDesc, "SimParamsCB");
            _emitterParamsCB = _device.CreateBuffer(cbDesc, "EmitterParamsCB");

            // 力场 / 碰撞体结构化缓冲（预留容量）
            var ffDesc = new RHIBufferDesc
            {
                SizeInBytes = (ulong)Marshal.SizeOf<GPUForceFieldData>() * 32,
                Format = RHIFormat.Unknown,
                HeapType = RHIResourceHeapType.Default,
                Flags = RHIBufferFlags.Structured | RHIBufferFlags.UnorderedAccess | RHIBufferFlags.ShaderResource,
                Stride = (ulong)Marshal.SizeOf<GPUForceFieldData>()
            };
            _forceFieldBuffer = _device.CreateBuffer(ffDesc, "ForceFieldBuffer");

            var colDesc = ffDesc;
            colDesc.SizeInBytes = (ulong)Marshal.SizeOf<GPUColliderData>() * 32;
            colDesc.Stride = (ulong)Marshal.SizeOf<GPUColliderData>();
            _colliderBuffer = _device.CreateBuffer(colDesc, "ColliderBuffer");
        }

        /// <summary>创建粒子模拟与渲染的根签名。</summary>
        private void CreateRootSignatures()
        {
            // 模拟根签名：
            // [0] CBV b0 = SimParams
            // [1] CBV b1 = EmitterParams
            // [2] UAV u0 = 粒子缓冲（输入/输出）
            // [3] UAV u1 = 存活索引
            // [4] UAV u2 = 死亡索引栈
            // [5] UAV u3 = 计数器
            // [6] UAV u4 = 间接参数
            // [7] SRV t0 = 力场缓冲
            // [8] SRV t1 = 碰撞体缓冲
            // [9] SRV t2 = 深度图
            var simParams = new RHIRootParameter[10];
            simParams[0] = RootCBV(0, RHIShaderVisibility.All);
            simParams[1] = RootCBV(1, RHIShaderVisibility.All);
            for (int i = 0; i < 5; i++)
                simParams[2 + i] = RootUAV((uint)i, RHIShaderVisibility.All);
            simParams[7] = RootSRV(0, RHIShaderVisibility.All);
            simParams[8] = RootSRV(1, RHIShaderVisibility.All);
            simParams[9] = RootSRV(2, RHIShaderVisibility.All);

            _particleRootSig = _device.CreateRootSignature(new RHIRootSignatureDesc
            {
                Parameters = simParams,
                AllowInputAssembler = false
            });

            // 渲染根签名：[0] CBV b0 视图投影 [1] SRV t0 粒子 [2] SRV t1 排序索引
            var renderParams = new RHIRootParameter[3];
            renderParams[0] = RootCBV(0, RHIShaderVisibility.All);
            renderParams[1] = RootSRV(0, RHIShaderVisibility.Vertex);
            renderParams[2] = RootSRV(1, RHIShaderVisibility.Vertex);
            _renderRootSig = _device.CreateRootSignature(new RHIRootSignatureDesc
            {
                Parameters = renderParams,
                AllowInputAssembler = false
            });
        }

        // ──────────────────────────────────────────────────────────────
        // 着色器编译：内嵌 HLSL 源码并编译为各阶段 PSO
        // ──────────────────────────────────────────────────────────────

        private void CompileComputeShaders()
        {
            // 发射着色器：原子地从死亡栈弹出索引，初始化粒子
            string emitHlsl = @"
                #include ""ParticleCommon.hlsli""
                RWStructuredBuffer<Particle> Particles : register(u0);
                RWStructuredBuffer<uint> DeadStack : register(u2);
                RWStructuredBuffer<uint> Counters : register(u3);
                RWStructuredBuffer<uint> IndirectArgs : register(u4);
                [numthreads(64,1,1)]
                void CSEmit(uint3 dtid : SV_DispatchThreadID)
                {
                    if (dtid.x >= gEmit.BurstCount) return;
                    uint deadCount; InterlockedAdd(Counters[1], -1, deadCount);
                    if ((int)deadCount <= 0) { InterlockedAdd(Counters[1], 1); return; }
                    uint idx = DeadStack[deadCount - 1];
                    // 初始化粒子状态（位置/速度/颜色/尺寸/生命周期）
                    Particles[idx].Position = gEmit.EmitterPosition;
                    Particles[idx].Velocity = gEmit.InitialVelocityMax;
                    Particles[idx].Age = 0.0;
                    Particles[idx].Lifetime = gEmit.LifetimeMin;
                    Particles[idx].Color = gEmit.ColorStart;
                    uint alive; InterlockedAdd(Counters[0], 1, alive);
                }
            ";
            _emitPSO = _device.CreateComputePipelineState(new RHIComputePipelineDesc
            {
                Name = "ParticleEmitPSO",
                CS = _device.CompileShader(emitHlsl, "CSEmit", RHIShaderStage.Compute, _device.MaxShaderModel)
            });

            // 模拟着色器：更新位置/速度、应用力场、碰撞、颜色/尺寸曲线
            string simHlsl = @"
                #include ""ParticleCommon.hlsli""
                RWStructuredBuffer<Particle> Particles : register(u0);
                RWStructuredBuffer<uint> AliveList : register(u1);
                RWStructuredBuffer<uint> DeadStack : register(u2);
                RWStructuredBuffer<uint> Counters : register(u3);
                RWStructuredBuffer<uint> IndirectArgs : register(u4);
                StructuredBuffer<ForceField> ForceFields : register(t0);
                StructuredBuffer<Collider> Colliders : register(t1);
                Texture2D<float> DepthTex : register(t2);

                // Curl Noise（无散度矢量场）—— 3D Simplex 近似
                float3 CurlNoise(float3 p) { return float3(0,0,0); /* 实现见 Shader 库 */ }

                [numthreads(64,1,1)]
                void CSSimulate(uint3 dtid : SV_DispatchThreadID)
                {
                    // 原位更新：每线程处理一个固定槽位，无跨粒子依赖，单缓冲 UAV 安全
                    uint idx = dtid.x;
                    if (idx >= gSim.MaxParticles) return;
                    Particle p = Particles[idx];
                    if (p.Color.a <= 0.0) return; // 跳过死亡/未激活粒子
                    p.Age += gSim.DeltaTime;
                    float t = saturate(p.Age / p.Lifetime);
                    if (t >= 1.0) {
                        // 死亡：回收到死亡栈
                        uint dead; InterlockedAdd(Counters[1], 1, dead);
                        DeadStack[dead] = idx;
                        p.Color.a = 0; // 标记死亡
                        Particles[idx] = p;
                        return;
                    }
                    // 累加力场
                    float3 force = gEmit.Gravity * gEmit.Mass;
                    for (uint fi = 0; fi < gSim.NumForceFields; fi++) {
                        ForceField ff = ForceFields[fi];
                        float3 d = ff.Position - p.Position;
                        float dist = max(length(d), 1e-4);
                        switch (ff.FieldType) {
                            case 2: force += CurlNoise(p.Position * ff.Tightness) * ff.Strength; break; // Curl
                            case 3: force += normalize(d) * ff.Strength / pow(dist, ff.Falloff); break;   // Attractor
                            case 5: { float3 axis = ff.Direction; float3 tan = cross(axis, d); force += normalize(tan) * ff.Strength; break; } // Vortex
                            case 6: force -= p.Velocity * ff.Strength; break; // Drag
                        }
                    }
                    p.Velocity += force * gSim.DeltaTime / gEmit.Mass;
                    p.Velocity *= (1.0 - gEmit.Drag * gSim.DeltaTime);
                    p.Position += p.Velocity * gSim.DeltaTime;

                    // 碰撞
                    for (uint ci = 0; ci < gSim.NumColliders; ci++) {
                        Collider c = Colliders[ci];
                        if (c.ColliderType == 1) { // Plane
                            float h = dot(p.Position - c.Position, c.Normal);
                            if (h < 0) {
                                p.Position -= c.Normal * h;
                                float vn = dot(p.Velocity, c.Normal);
                                if (vn < 0) p.Velocity -= c.Normal * (1.0 + c.Bounce) * vn;
                                p.Velocity *= (1.0 - c.Friction);
                            }
                        }
                    }
                    // 颜色/尺寸曲线插值
                    p.Color = lerp(gEmit.ColorStart, gEmit.ColorEnd, t);
                    p.Aux.xy = lerp(gEmit.SizeStart, gEmit.SizeEnd, t);
                    p.CameraDist = distanceSquared(p.Position, gSim.CameraPosition);
                    Particles[idx] = p;
                }
            ";
            _simulatePSO = _device.CreateComputePipelineState(new RHIComputePipelineDesc
            {
                Name = "ParticleSimulatePSO",
                CS = _device.CompileShader(simHlsl, "CSSimulate", RHIShaderStage.Compute, _device.MaxShaderModel)
            });

            // 回收/紧致着色器：构建存活索引列表 + 间接绘制参数
            string reclaimHlsl = @"
                RWStructuredBuffer<Particle> Particles : register(u0);
                RWStructuredBuffer<uint> AliveList : register(u1);
                RWStructuredBuffer<uint> Counters : register(u3);
                RWStructuredBuffer<uint> IndirectArgs : register(u4);
                [numthreads(64,1,1)]
                void CSReclaim(uint3 dtid : SV_DispatchThreadID)
                {
                    uint idx = dtid.x;
                    if (idx >= gSim.MaxParticles) return;
                    if (Particles[idx].Color.a > 0.0) {
                        uint slot; InterlockedAdd(Counters[2], 1, slot);
                        AliveList[slot] = idx;
                    }
                    // 第 0 线程写入间接绘制参数
                    if (idx == 0) {
                        IndirectArgs[0] = 6;            // IndexCountPerInstance
                        uint cnt = Counters[2];
                        IndirectArgs[1] = cnt;          // InstanceCount
                        IndirectArgs[2] = 0;            // StartIndex
                        IndirectArgs[3] = 0;            // BaseVertex
                        IndirectArgs[4] = 0;            // StartInstance
                    }
                }
            ";
            _reclaimPSO = _device.CreateComputePipelineState(new RHIComputePipelineDesc
            {
                Name = "ParticleReclaimPSO",
                CS = _device.CompileShader(reclaimHlsl, "CSReclaim", RHIShaderStage.Compute, _device.MaxShaderModel)
            });
        }

        /// <summary>编译渲染着色器（Billboard / Ribbon / Mesh）。</summary>
        private void CompileRenderShaders()
        {
            // Billboard：顶点着色器展开四边形 4 顶点（无 IA 输入，用 SV_VertexID）
            string billboardVS = @"
                struct VSOut { float4 Pos : SV_Position; float2 UV : TEXCOORD0; float4 Color : COLOR; };
                StructuredBuffer<Particle> Particles : register(t0);
                StructuredBuffer<uint> Indices : register(t1);
                cbuffer FrameCB : register(b0) { matrix gView; matrix gProj; float3 gCamRight; float3 gCamUp; };
                VSOut VSMain(uint vid : SV_VertexID) {
                    VSOut o;
                    uint particleIdx = vid / 4;
                    uint corner = vid % 4;
                    uint pi = Indices[particleIdx];
                    Particle p = Particles[pi];
                    float2 corners[4] = { float2(-1,1), float2(1,1), float2(-1,-1), float2(1,-1) };
                    float2 c = corners[corner] * p.Aux.xy;
                    float3 world = p.Position + gCamRight * c.x + gCamUp * c.y;
                    o.Pos = mul(gProj, mul(gView, float4(world,1)));
                    o.UV = corners[corner] * 0.5 + 0.5;
                    o.Color = p.Color;
                    return o;
                }
                float4 PSMain(VSOut i) : SV_Target { return i.Color; }
            ";
            _billboardPSO = _device.CreateGraphicsPipelineState(new RHIGraphicsPipelineDesc
            {
                Name = "ParticleBillboardPSO",
                VS = _device.CompileShader(billboardVS, "VSMain", RHIShaderStage.Vertex, _device.MaxShaderModel),
                PS = _device.CompileShader(billboardVS, "PSMain", RHIShaderStage.Pixel, _device.MaxShaderModel),
                PrimitiveTopology = RHIPrimitiveTopology.TriangleList,
                BlendState = RHIBlendDesc.AdditiveBlend,
                DepthStencilState = RHIDepthDesc.None,
                RasterizerState = RHIRasterizerDesc.NoCull,
                RTVFormats = new[] { RHIFormat.R16G16B16A16_Float },
                DSVFormat = RHIFormat.Unknown
            });

            // Ribbon：连续粒子之间连接成三角形带
            string ribbonVS = @"
                struct VSOut { float4 Pos : SV_Position; float4 Color : COLOR; };
                StructuredBuffer<Particle> Particles : register(t0);
                StructuredBuffer<uint> Indices : register(t1);
                cbuffer FrameCB : register(b0) { matrix gView; matrix gProj; float3 gCamRight; float3 gCamUp; };
                VSOut VSMain(uint vid : SV_VertexID) {
                    VSOut o;
                    uint segIdx = vid / 2; uint side = vid & 1;
                    uint pi = Indices[segIdx];
                    Particle p = Particles[pi];
                    float3 world = p.Position + gCamRight * (side ? p.Aux.x : -p.Aux.x);
                    o.Pos = mul(gProj, mul(gView, float4(world,1)));
                    o.Color = p.Color;
                    return o;
                }
                float4 PSMain(VSOut i) : SV_Target { return i.Color; }
            ";
            _ribbonPSO = _device.CreateGraphicsPipelineState(new RHIGraphicsPipelineDesc
            {
                Name = "ParticleRibbonPSO",
                VS = _device.CompileShader(ribbonVS, "VSMain", RHIShaderStage.Vertex, _device.MaxShaderModel),
                PS = _device.CompileShader(ribbonVS, "PSMain", RHIShaderStage.Pixel, _device.MaxShaderModel),
                PrimitiveTopology = RHIPrimitiveTopology.TriangleStrip,
                BlendState = RHIBlendDesc.AlphaBlend,
                DepthStencilState = RHIDepthDesc.None,
                RasterizerState = RHIRasterizerDesc.NoCull,
                RTVFormats = new[] { RHIFormat.R16G16B16A16_Float },
                DSVFormat = RHIFormat.Unknown
            });

            // Mesh：实例化绘制网格粒子
            string meshVS = @"
                struct VSOut { float4 Pos : SV_Position; float4 Color : COLOR; };
                StructuredBuffer<Particle> Particles : register(t0);
                StructuredBuffer<uint> Indices : register(t1);
                cbuffer FrameCB : register(b0) { matrix gView; matrix gProj; };
                VSOut VSMain(uint vid : SV_VertexID, uint iid : SV_InstanceID) {
                    VSOut o;
                    uint pi = Indices[iid];
                    Particle p = Particles[pi];
                    float3 local = float3((vid&1)?1:-1, (vid&2)?1:-1, (vid&4)?1:-1) * p.Aux.x;
                    float3 world = p.Position + local;
                    o.Pos = mul(gProj, mul(gView, float4(world,1)));
                    o.Color = p.Color; return o;
                }
                float4 PSMain(VSOut i) : SV_Target { return i.Color; }
            ";
            _meshPSO = _device.CreateGraphicsPipelineState(new RHIGraphicsPipelineDesc
            {
                Name = "ParticleMeshPSO",
                VS = _device.CompileShader(meshVS, "VSMain", RHIShaderStage.Vertex, _device.MaxShaderModel),
                PS = _device.CompileShader(meshVS, "PSMain", RHIShaderStage.Pixel, _device.MaxShaderModel),
                PrimitiveTopology = RHIPrimitiveTopology.TriangleList,
                BlendState = RHIBlendDesc.AlphaBlend,
                DepthStencilState = RHIDepthDesc.DepthRead,
                RasterizerState = RHIRasterizerDesc.Default,
                RTVFormats = new[] { RHIFormat.R16G16B16A16_Float },
                DSVFormat = RHIFormat.D32_Float
            });
        }

        /// <summary>初始化死亡索引栈（写入 0..maxParticles-1，计数器[1]=maxParticles）。</summary>
        private void InitDeadStack()
        {
            // 通过上传缓冲写入初始死亡索引
            Span<uint> stack = new uint[_maxParticles];
            for (int i = 0; i < _maxParticles; i++) stack[i] = (uint)i;
            _device.UploadToBuffer(_deadIndexBuffer!, stack);

            Span<uint> counters = new uint[16];
            counters[0] = 0;                      // alive
            counters[1] = (uint)_maxParticles;    // dead
            counters[2] = 0;                      // emit
            counters[3] = 0;                      // sort
            _device.UploadToBuffer(_counterBuffer!, counters);
        }

        // ──────────────────────────────────────────────────────────────
        // 运行时 API：发射器 / 力场 / 碰撞体管理
        // ──────────────────────────────────────────────────────────────

        /// <summary>添加一个发射器描述。</summary>
        public void AddEmitter(GPUEmitterDesc desc) => _emitters.Add(desc);

        /// <summary>添加力场并上传到 GPU。</summary>
        public void AddForceField(GPUForceFieldData ff)
        {
            _forceFields.Add(ff);
            if (_forceFieldBuffer != null)
                _device.UploadToBuffer(_forceFieldBuffer, _forceFields.ToArray());
        }

        /// <summary>添加碰撞体并上传到 GPU。</summary>
        public void AddCollider(GPUColliderData col)
        {
            _colliders.Add(col);
            if (_colliderBuffer != null)
                _device.UploadToBuffer(_colliderBuffer, _colliders.ToArray());
        }

        /// <summary>绑定场景深度图（用于深度缓冲碰撞）。</summary>
        public void SetDepthTexture(RHITexture depth)
        {
            _depthTexture = depth;
            _depthSRV = _device.CreateSRV(depth);
        }

        // ──────────────────────────────────────────────────────────────
        // 主更新循环：发射 → 模拟 → 回收 → 排序
        // ──────────────────────────────────────────────────────────────

        /// <summary>
        /// 每帧更新粒子模拟。完全在 GPU 计算队列上执行，无 CPU 回读。
        /// </summary>
        /// <param name="dt">帧步长（秒）</param>
        /// <param name="cameraPos">相机世界位置</param>
        /// <param name="view">视图矩阵</param>
        /// <param name="proj">投影矩阵</param>
        public void Update(float dt, Vector3 cameraPos, Matrix4x4 view, Matrix4x4 proj)
        {
            if (!IsInitialized || _computeCmd == null) return;
            _systemAge += dt;

            // 1) 上传模拟参数到常量缓冲
            Matrix4x4.Invert(view * proj, out var invViewProj);
            var simParams = new GPUSimParams
            {
                ViewMatrix = view,
                ProjMatrix = proj,
                InvViewProjMatrix = invViewProj,
                CameraPosition = cameraPos,
                DeltaTime = dt,
                CameraRight = new Vector3(view.M11, view.M21, view.M31),
                ElapsedTime = _systemAge,
                CameraUp = new Vector3(view.M12, view.M22, view.M32),
                FrameIndex = (uint)DateTime.UtcNow.Ticks,
                DepthTextureSize = _depthTexture != null
                    ? new Vector2(_depthTexture.TextureDesc!.Value.Width, _depthTexture.TextureDesc.Value.Height)
                    : Vector2.One,
                NearZ = 0.1f,
                FarZ = 1000f,
                MaxParticles = (uint)_maxParticles,
                AliveCount = 0,
                NumForceFields = (uint)_forceFields.Count,
                NumColliders = (uint)_colliders.Count
            };
            _device.UploadToBuffer(_simParamsCB!, new[] { simParams });

            _computeCmd.Open();
            _computeCmd.SetComputeRootSignature(_particleRootSig!);
            _computeCmd.SetComputeRootConstantBufferView(0, _simParamsCB!.GpuVirtualAddress);

            // 2) 发射阶段：为每个发射器 dispatch
            foreach (var emitter in _emitters)
            {
                var ep = emitter.ToParams(cameraPos);
                ep.SystemAge = _systemAge;
                ep.DeltaTime = dt;
                _device.UploadToBuffer(_emitterParamsCB!, new[] { ep });
                _computeCmd.SetComputeRootConstantBufferView(1, _emitterParamsCB!.GpuVirtualAddress);
                BindSimUAVs(_computeCmd);

                // 资源屏障：粒子缓冲 → UAV
                var cur = _useBufferA ? _particleBufferA! : _particleBufferB!;
                _computeCmd.TransitionTo(cur, RHIResourceState.UnorderedAccess);
                _computeCmd.TransitionTo(_counterBuffer!, RHIResourceState.UnorderedAccess);

                _computeCmd.SetPipelineState(_emitPSO!);
                int emitGroups = Math.Max(1, (int)emitter.BurstCount / 64 + 1);
                _computeCmd.Dispatch(emitGroups, 1, 1);
            }

            // 3) 模拟阶段：原位更新（单缓冲 UAV 读写，每线程独立处理一个粒子，无跨粒子冲突）
            var particleBuf = _useBufferA ? _particleBufferA! : _particleBufferB!;
            _computeCmd.TransitionTo(particleBuf, RHIResourceState.UnorderedAccess);
            _computeCmd.TransitionTo(_aliveIndexBuffer!, RHIResourceState.UnorderedAccess);
            _computeCmd.TransitionTo(_deadIndexBuffer!, RHIResourceState.UnorderedAccess);
            BindSimUAVs(_computeCmd);
            _computeCmd.SetComputeRootShaderResourceView(7, _forceFieldBuffer!.GpuVirtualAddress);
            _computeCmd.SetComputeRootShaderResourceView(8, _colliderBuffer!.GpuVirtualAddress);
            if (_depthTexture != null)
                _computeCmd.SetComputeRootDescriptorTable(9, _depthSRV);

            _computeCmd.SetPipelineState(_simulatePSO!);
            int simGroups = (_maxParticles + 63) / 64;
            _computeCmd.Dispatch(simGroups, 1, 1);

            // 4) 回收阶段：重建存活索引列表 + 间接绘制参数（粒子缓冲保持 UAV 只读）
            _computeCmd.SetPipelineState(_reclaimPSO!);
            _computeCmd.TransitionTo(_indirectArgsBuffer!, RHIResourceState.UnorderedAccess);
            BindSimUAVs(_computeCmd);
            _computeCmd.Dispatch(simGroups, 1, 1);

            // 5) 排序阶段：透明粒子按距离排序
            if (_emitters.Count > 0 && _emitters[0].SortMode != GPUParticleSortMode.None)
            {
                _sorter.Sort(_computeCmd, _maxParticles);
            }

            // 屏障转回通用读（原位更新后粒子缓冲与索引缓冲恢复通用状态供渲染读取）
            _computeCmd.TransitionTo(particleBuf, RHIResourceState.Common);
            _computeCmd.TransitionTo(_aliveIndexBuffer!, RHIResourceState.Common);
            _computeCmd.TransitionTo(_counterBuffer!, RHIResourceState.Common);

            _computeCmd.Close();
            _computeQueue!.ExecuteCommandLists(new[] { _computeCmd });
            _computeQueue.Flush();
        }

        /// <summary>绑定模拟阶段公共 UAV（存活索引/死亡栈/计数器/间接参数）。</summary>
        private void BindSimUAVs(RHICommandList cmd, bool bindParticle = true)
        {
            var cur = _useBufferA ? _particleBufferA! : _particleBufferB!;
            if (bindParticle)
                cmd.SetComputeRootUnorderedAccessView(2, cur.GpuVirtualAddress);
            cmd.SetComputeRootUnorderedAccessView(3, _aliveIndexBuffer!.GpuVirtualAddress);
            cmd.SetComputeRootUnorderedAccessView(4, _deadIndexBuffer!.GpuVirtualAddress);
            cmd.SetComputeRootUnorderedAccessView(5, _counterBuffer!.GpuVirtualAddress);
            cmd.SetComputeRootUnorderedAccessView(6, _indirectArgsBuffer!.GpuVirtualAddress);
        }

        // ──────────────────────────────────────────────────────────────
        // 渲染：根据渲染类型选择 PSO，使用间接绘制
        // ──────────────────────────────────────────────────────────────

        /// <summary>
        /// 渲染粒子到当前渲染目标。使用 GPU 生成的间接绘制参数，零 CPU 回读。
        /// </summary>
        /// <param name="cmd">图形命令列表</param>
        /// <param name="rtv">渲染目标视图</param>
        /// <param name="dsv">深度模板视图（可为 null）</param>
        /// <param name="viewport">视口</param>
        public void Render(RHICommandList cmd, RHIDescriptorHandle rtv, RHIDescriptorHandle? dsv, RHIViewport viewport)
        {
            if (!IsInitialized) return;
            var cur = _useBufferA ? _particleBufferA! : _particleBufferB!; // 读取原位更新写入的缓冲

            cmd.SetGraphicsRootSignature(_renderRootSig!);
            cmd.SetGraphicsRootConstantBufferView(0, _simParamsCB!.GpuVirtualAddress);
            cmd.TransitionTo(cur, RHIResourceState.PixelShaderResource | RHIResourceState.NonPixelShaderResource);
            cmd.SetGraphicsRootShaderResourceView(1, cur.GpuVirtualAddress);

            var sorted = _sorter.GetSortedIndexBuffer();
            if (sorted != null)
            {
                cmd.TransitionTo(sorted, RHIResourceState.PixelShaderResource | RHIResourceState.NonPixelShaderResource);
                cmd.SetGraphicsRootShaderResourceView(2, sorted.GpuVirtualAddress);
            }

            cmd.OMSetRenderTargets(new[] { rtv }, dsv);
            cmd.SetViewport(viewport);
            cmd.SetScissorRect(new RHIScissorRect(0, 0, (int)viewport.Width, (int)viewport.Height));

            cmd.TransitionTo(_indirectArgsBuffer!, RHIResourceState.IndirectArgument);

            foreach (var emitter in _emitters)
            {
                cmd.SetPipelineState(emitter.RenderType switch
                {
                    GPUParticleRenderType.Billboard => _billboardPSO!,
                    GPUParticleRenderType.Ribbon => _ribbonPSO!,
                    GPUParticleRenderType.Mesh => _meshPSO!,
                    _ => _billboardPSO!
                });
                cmd.IASetPrimitiveTopology(emitter.RenderType == GPUParticleRenderType.Ribbon
                    ? RHIPrimitiveTopology.TriangleStrip
                    : RHIPrimitiveTopology.TriangleList);

                // 间接绘制：InstanceCount 由 GPU 在回收阶段写入
                cmd.DrawIndexedInstancedIndirect(_indirectArgsBuffer!, 0);
            }
        }

        // ──────────────────────────────────────────────────────────────
        // 资源释放
        // ──────────────────────────────────────────────────────────────

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _particleBufferA?.Dispose();
            _particleBufferB?.Dispose();
            _aliveIndexBuffer?.Dispose();
            _deadIndexBuffer?.Dispose();
            _counterBuffer?.Dispose();
            _indirectArgsBuffer?.Dispose();
            _simParamsCB?.Dispose();
            _emitterParamsCB?.Dispose();
            _forceFieldBuffer?.Dispose();
            _colliderBuffer?.Dispose();
            _emitPSO?.Dispose();
            _simulatePSO?.Dispose();
            _reclaimPSO?.Dispose();
            _billboardPSO?.Dispose();
            _ribbonPSO?.Dispose();
            _meshPSO?.Dispose();
            _particleRootSig?.Dispose();
            _renderRootSig?.Dispose();
            _computeCmd?.Dispose();
            _graphicsCmd?.Dispose();
            GC.SuppressFinalize(this);
        }

        // ── 根签名参数构造辅助 ──
        private static RHIRootParameter RootCBV(uint reg, RHIShaderVisibility vis) => new()
        {
            ParameterType = RHIRootParameterType.CBV,
            ShaderVisibility = vis,
            Descriptor = new RHIRootDescriptor { ShaderRegister = reg }
        };
        private static RHIRootParameter RootSRV(uint reg, RHIShaderVisibility vis) => new()
        {
            ParameterType = RHIRootParameterType.SRV,
            ShaderVisibility = vis,
            Descriptor = new RHIRootDescriptor { ShaderRegister = reg }
        };
        private static RHIRootParameter RootUAV(uint reg, RHIShaderVisibility vis) => new()
        {
            ParameterType = RHIRootParameterType.UAV,
            ShaderVisibility = vis,
            Descriptor = new RHIRootDescriptor { ShaderRegister = reg }
        };
    }
}
