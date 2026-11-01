using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using YamaEngine.RHI;
using YamaEngine.Shaders;

namespace YamaEngine.RHI
{
    // ════════════════════════════════════════════════════════════════════════
    // RHI 光线追踪扩展抽象 (DXR 1.1 / Vulkan RT 后端实现)
    // 以下抽象在 RHI 层定义, 由 D3D12/Vulkan 后端具体实现,
    // 供 RayTracingReflections 系统对接。与现有 RHIDevice/RHICommandList 协同。
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>光线追踪几何体类型 (三角形 / 程序化图元)。</summary>
    public enum RHIRayTracingGeometryType
    {
        /// <summary>三角形几何 (含顶点/索引缓冲)。</summary>
        Triangles = 0,
        /// <summary>程序化图元 (需自定义 Intersection 着色器)。</summary>
        Procedural = 1
    }

    /// <summary>加速结构层级 (TLAS 顶层 / BLAS 底层)。</summary>
    public enum RHIAccelerationStructureLevel
    {
        /// <summary>底层加速结构 (对应单个网格的几何)。</summary>
        BottomLevel = 0,
        /// <summary>顶层加速结构 (组合所有实例变换)。</summary>
        TopLevel = 1
    }

    /// <summary>加速结构构建标志。</summary>
    [Flags]
    public enum RHIAccelerationStructureBuildFlags : uint
    {
        None = 0,
        AllowUpdate = 1,
        AllowCompaction = 2,
        PreferFastTrace = 4,
        PreferFastBuild = 8,
        MinimizeMemory = 16
    }

    /// <summary>
    /// 命中组 (HitGroup) 描述: 封装一组命中相关着色器入口。
    /// 对应 DXR 的 HitGroup / Vulkan RT 的 hit group, 包含:
    ///   - ClosestHit: 命中最近图元时执行 (反射/折射着色)
    ///   - AnyHit:     任意命中时执行 (alpha 测试/透明裁剪)
    ///   - Intersection: 程序化图元自定义相交测试
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct RHIHitGroupDesc
    {
        /// <summary>命中组名称 (导出符号, 用于 SBT 寻址)。</summary>
        public string Name;
        /// <summary>ClosestHit 着色器入口名 (可空)。</summary>
        public string? ClosestHitEntry;
        /// <summary>AnyHit 着色器入口名 (可空)。</summary>
        public string? AnyHitEntry;
        /// <summary>Intersection 着色器入口名 (程序化图元用, 可空)。</summary>
        public string? IntersectionEntry;
        /// <summary>几何类型。</summary>
        public RHIRayTracingGeometryType GeometryType;

        /// <summary>是否为程序化图元命中组。</summary>
        public readonly bool IsProcedural => GeometryType == RHIRayTracingGeometryType.Procedural;
    }

    /// <summary>
    /// 光线追踪管线状态对象 (RTPSO) 描述。
    /// 对应 DXR 的 D3D12_RAYTRACING_PIPELINE_STATE / Vulkan RT 的 VkRayTracingPipeline。
    /// </summary>
    public class RHIRayTracingPipelineDesc
    {
        /// <summary>管线名称。</summary>
        public string Name = "";
        /// <summary>DXR 管线子对象集合的着色器库字节码 (含全部着色器)。</summary>
        public RHIShaderBytecode? ShaderLibrary;
        /// <summary>RayGen 着色器入口名。</summary>
        public string RayGenEntry = "RayGen";
        /// <summary>Miss 着色器入口名数组 (反射/折射/阴影各一)。</summary>
        public string[] MissEntries = Array.Empty<string>();
        /// <summary>命中组描述数组。</summary>
        public RHIHitGroupDesc[] HitGroups = Array.Empty<RHIHitGroupDesc>();
        /// <summary>最大光线负载大小 (字节, 通常 16~32)。</summary>
        public int MaxPayloadSizeInBytes = 32;
        /// <summary>最大属性大小 (字节, 内置三角形为 8 = 2 个重心坐标)。</summary>
        public int MaxAttributeSizeInBytes = 8;
        /// <summary>最大递归深度 (反射/折射弹射次数上限)。</summary>
        public int MaxRecursionDepth = 4;
        /// <summary>根签名 (全局)。</summary>
        public RHIRootSignature? GlobalRootSignature;
        /// <summary>局部根签名数组 (每个命中组可绑定不同局部根签名)。</summary>
        public RHIRootSignature?[] LocalRootSignatures = Array.Empty<RHIRootSignature?>();
    }

    /// <summary>
    /// 光线追踪管线状态对象 (RTPSO) 抽象基类。
    /// 继承自 RHIPipelineState, 由 D3D12/Vulkan RT 后端实现。
    /// </summary>
    public abstract class RHIRayTracingPipeline : RHIPipelineState
    {
        /// <summary>管线类型固定为 RayTracing。</summary>
        public override RHIPipelineType PipelineType => RHIPipelineType.RayTracing;

        /// <summary>获取着色器标识符 (用于构建 SBT)。</summary>
        /// <param name="exportName">着色器导出名 (RayGen/Miss/HitGroup)。</param>
        /// <returns>固定大小的着色器 ID (DXR 为 32 字节 GUID)。</returns>
        public abstract byte[] GetShaderIdentifier(string exportName);

        /// <summary>着色器标识符大小 (字节)。</summary>
        public abstract int ShaderIdentifierSize { get; }
    }

    /// <summary>
    /// 加速结构 (Acceleration Structure) 抽象基类。
    /// 封装 DXR 的 TLAS/BLAS 或 Vulkan RT 的顶层/底层加速结构。
    /// </summary>
    public abstract class RHIAccelerationStructure : RHIResource
    {
        /// <summary>加速结构层级 (TLAS/BLAS)。</summary>
        public abstract RHIAccelerationStructureLevel Level { get; }
        /// <summary>构建标志。</summary>
        public abstract RHIAccelerationStructureBuildFlags BuildFlags { get; }
        /// <summary>GPU 虚拟地址 (供着色器 TraceRay 使用)。</summary>
        public override ulong GpuVirtualAddress => base.GpuVirtualAddress;
        /// <summary>是否已构建。</summary>
        public abstract bool IsBuilt { get; }

        /// <summary>当前资源状态默认为加速结构状态。</summary>
        public override RHIResourceState CurrentState { get; set; } = RHIResourceState.RaytracingAccelerationStructure;
    }

    /// <summary>
    /// 着色器绑定表 (Shader Binding Table, SBT) 描述符。
    /// SBT 将着色器标识符与局部根参数打包, 是光线追踪调度的核心数据结构。
    /// 分为三段: RayGen / Miss / HitGroup。
    /// </summary>
    public class RHIShaderBindingTableDesc
    {
        /// <summary>RTPSO (提供着色器标识符)。</summary>
        public RHIRayTracingPipeline? Pipeline;
        /// <summary>RayGen 段: 着色器 ID + 局部根参数。</summary>
        public RHISBTEntry RayGen = new();
        /// <summary>Miss 段: 每个 Miss 着色器一条记录。</summary>
        public List<RHISBTEntry> Miss = new();
        /// <summary>HitGroup 段: 每个命中组/实例一条记录。</summary>
        public List<RHISBTEntry> HitGroups = new();
    }

    /// <summary>SBT 单条记录 (着色器 ID + 局部根参数数据)。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct RHISBTEntry
    {
        /// <summary>着色器导出名 (用于查询 ShaderIdentifier)。</summary>
        public string ExportName;
        /// <summary>局部根参数原始数据 (按局部根签名布局打包)。</summary>
        public byte[] LocalRootArgs;
    }

    /// <summary>构建好的 SBT (含 GPU 缓冲与各段偏移)。</summary>
    public sealed class RHIShaderBindingTable : IDisposable
    {
        /// <summary>RayGen 段 GPU 缓冲。</summary>
        public RHIBuffer? RayGenBuffer;
        /// <summary>Miss 段 GPU 缓冲。</summary>
        public RHIBuffer? MissBuffer;
        /// <summary>HitGroup 段 GPU 缓冲。</summary>
        public RHIBuffer? HitGroupBuffer;
        /// <summary>RayGen 段起始 GPU 虚拟地址。</summary>
        public ulong RayGenGpuVA => RayGenBuffer?.GpuVirtualAddress ?? 0;
        /// <summary>Miss 段起始 GPU 虚拟地址。</summary>
        public ulong MissGpuVA => MissBuffer?.GpuVirtualAddress ?? 0;
        /// <summary>HitGroup 段起始 GPU 虚拟地址。</summary>
        public ulong HitGroupGpuVA => HitGroupBuffer?.GpuVirtualAddress ?? 0;
        /// <summary>每条记录步长 (字节, 含对齐)。</summary>
        public uint Stride;
        /// <summary>Miss 记录数。</summary>
        public int MissCount;
        /// <summary>HitGroup 记录数。</summary>
        public int HitGroupCount;

        /// <summary>释放。</summary>
        public void Dispose()
        {
            RayGenBuffer?.Dispose();
            MissBuffer?.Dispose();
            HitGroupBuffer?.Dispose();
            GC.SuppressFinalize(this);
        }
    }

    /// <summary>
    /// RHI 命令列表的光线追踪扩展方法 (接口对接)。
    /// 由后端以扩展方法或虚方法实现, 此处声明为 partial 接口约定。
    /// </summary>
    public static class RHIRayTracingCommandListExtensions
    {
        // 以下方法签名约定由后端在 RHICommandList 上实现 (虚方法或扩展)。
        // 此处仅作接口文档, 实际调用通过反射或后端注入。

        /// <summary>构建加速结构 (约定方法名 BuildRaytracingAccelerationStructure)。</summary>
        public static void BuildAccelerationStructure(this RHICommandList cmd,
            RHIAccelerationStructure dst, RHIBuffer scratch,
            RHIBuffer? srcUpdate = null, RHIBuffer? instanceDesc = null) { }

        /// <summary>调度光线追踪 DispatchRays (约定方法名 DispatchRays)。</summary>
        public static void DispatchRays(this RHICommandList cmd,
            RHIRayTracingPipeline pipeline, RHIShaderBindingTable sbt,
            uint width, uint height, uint depth) { }

        /// <summary>设置 RTPSO 与 SBT (约定方法名 SetRaytracingPipeline)。</summary>
        public static void SetRaytracingPipeline(this RHICommandList cmd,
            RHIRayTracingPipeline pipeline, RHIShaderBindingTable sbt) { }
    }
}

namespace YamaEngine.Rendering
{
    // ════════════════════════════════════════════════════════════════════════
    // yama 硬件光线追踪反射/折射系统
    //
    // 基于 DXR 1.1 / Vulkan RT 的硬件加速反射、折射、阴影与遮蔽系统。
    // 核心组件:
    //  1. RTPSO 管理 — 光线追踪管线状态对象, 含 HitGroup (ClosestHit/AnyHit/Intersection)
    //  2. SBT 构建 — 着色器绑定表 (RayGen/Miss/HitGroup 三段)
    //  3. 反射 RayGen — G-Buffer 法线/粗糙度 → 反射方向, GGX 重要性采样
    //                  最大反弹次数/距离截断, 粗糙度阈值回退 SSR
    //  4. 折射光线 — 透明材质 (玻璃/水) 折射方向计算, Beer-Lambert 吸收
    //  5. RT 降噪 — SVGF/ReLAX 风格: 时序累积/空间方差/G-Buffer 引导滤波/A-SVGF
    //  6. RT 阴影射线 — 精确硬阴影 + 多采样软阴影 (重要性采样)
    //  7. RT 环境光遮蔽 — GTAO/GI 替代, 硬件 RT 计算可见性
    //  8. TLAS/BLAS 管理 — 与 PathTracer 共享加速结构
    //  9. 与 RHI 严格对接: RHIDevice / RHICommandList /
    //     RHIRayTracingPipeline / RHIAccelerationStructure
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>RT 反射/折射质量档位。</summary>
    public enum RTReflectionQuality
    {
        /// <summary>低: 1 spp, 半分辨率, 强降噪。</summary>
        Low = 0,
        /// <summary>中: 1 spp, 全分辨率。</summary>
        Medium = 1,
        /// <summary>高: 2 spp, 全分辨率 + 时序累积。</summary>
        High = 2,
        /// <summary>电影级: 多 spp + 多弹射 + 折射。</summary>
        Cinematic = 3
    }

    /// <summary>光线类型索引 (对应 SBT 中 Miss/HitGroup 的槽位)。</summary>
    public static class RTRayTypes
    {
        /// <summary>反射光线 (主)。</summary>
        public const int Reflection = 0;
        /// <summary>折射光线 (透明材质)。</summary>
        public const int Refraction = 1;
        /// <summary>阴影射线。</summary>
        public const int Shadow = 2;
        /// <summary>AO 射线。</summary>
        public const int AmbientOcclusion = 3;
        /// <summary>光线类型总数。</summary>
        public const int Count = 4;
    }

    /// <summary>RT 反射/折射系统配置。</summary>
    public sealed class RTReflectionSettings
    {
        /// <summary>质量档位。</summary>
        public RTReflectionQuality Quality = RTReflectionQuality.High;
        /// <summary>反射最大反弹次数。</summary>
        public int MaxReflectionBounces = 2;
        /// <summary>折射最大弹射次数。</summary>
        public int MaxRefractionBounces = 2;
        /// <summary>光线最大传播距离 (世界单位, 截断远距离反射)。</summary>
        public float MaxRayDistance = 500.0f;
        /// <summary>光线偏移 (避免自相交)。</summary>
        public float RayEpsilon = 0.01f;
        /// <summary>粗糙度阈值: 超过此值的像素回退到 SSR (不发射 RT 反射)。</summary>
        public float RoughnessFallbackThreshold = 0.85f;
        /// <summary>启用折射 (玻璃/水等透明材质)。</summary>
        public bool EnableRefraction = true;
        /// <summary>启用 RT 阴影。</summary>
        public bool EnableRTShadows = true;
        /// <summary>启用 RT 环境光遮蔽。</summary>
        public bool EnableRTAO = true;
        /// <summary>阴影采样数 (软阴影, 1=硬阴影)。</summary>
        public int ShadowSamples = 4;
        /// <summary>AO 采样数。</summary>
        public int AOSamples = 4;
        /// <summary>AO 采样半径 (世界单位)。</summary>
        public float AORadius = 1.5f;
        /// <summary>启用 RT 降噪。</summary>
        public bool EnableDenoiser = true;
        /// <summary>降噪器类型。</summary>
        public RTDenoiserType DenoiserType = RTDenoiserType.ASVGF;
        /// <summary>时序累积最大帧数。</summary>
        public int MaxAccumulationFrames = 32;
        /// <summary>半分辨率渲染 (低质量档)。</summary>
        public bool HalfResolution = false;
    }

    /// <summary>RT 降噪器类型。</summary>
    public enum RTDenoiserType
    {
        /// <summary>SVGF: 时空方差引导滤波。</summary>
        SVGF = 0,
        /// <summary>ReLAX: 基于梯度的时空去噪 (更适合光泽反射)。</summary>
        ReLAX = 1,
        /// <summary>A-SVGF: 自适应 SVGF (按方差自适应采样/滤波)。</summary>
        ASVGF = 2
    }

    /// <summary>
    /// 折射材质参数 (玻璃/水/透明物体)。
    /// 用于 RT 折射光线计算折射方向与 Beer-Lambert 吸收。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct RefractionMaterial
    {
        /// <summary>折射率 (IOR)。空气=1.0, 水=1.33, 玻璃=1.5, 钻石=2.42。</summary>
        public float IOR;
        /// <summary>Beer-Lambert 吸收系数 (RGB, 每米吸收量)。</summary>
        public Vector3 Absorption;
        /// <summary>材质厚度 (世界单位, 用于吸收计算)。</summary>
        public float Thickness;
        /// <summary>粗糙度 (传输粗糙度, 控制折射模糊)。</summary>
        public float Roughness;
        /// <summary>材质 ID (用于区分透明像素)。</summary>
        public uint MaterialId;

        /// <summary>清水参数。</summary>
        public static RefractionMaterial Water => new()
        { IOR = 1.33f, Absorption = new Vector3(0.5f, 0.15f, 0.05f), Thickness = 2.0f, Roughness = 0.0f, MaterialId = 10 };

        /// <summary>玻璃参数。</summary>
        public static RefractionMaterial Glass => new()
        { IOR = 1.5f, Absorption = new Vector3(0.1f, 0.1f, 0.1f), Thickness = 0.5f, Roughness = 0.0f, MaterialId = 11 };

        /// <summary>有色玻璃 (绿色)。</summary>
        public static RefractionMaterial TintedGlass => new()
        { IOR = 1.52f, Absorption = new Vector3(2.5f, 0.8f, 0.3f), Thickness = 0.4f, Roughness = 0.05f, MaterialId = 12 };
    }

    /// <summary>RT 反射/折射主常量缓冲。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct RTReflectionConstants
    {
        /// <summary>视图矩阵。</summary>
        public Matrix4x4 ViewMatrix;
        /// <summary>投影矩阵。</summary>
        public Matrix4x4 ProjMatrix;
        /// <summary>逆视图矩阵。</summary>
        public Matrix4x4 InvViewMatrix;
        /// <summary>逆投影矩阵。</summary>
        public Matrix4x4 InvProjMatrix;
        /// <summary>相机位置 (世界空间)。</summary>
        public Vector3 CameraPos;
        /// <summary>光线偏移。</summary>
        public float RayEpsilon;
        /// <summary>视口尺寸倒数。</summary>
        public Vector2 InvResolution;
        /// <summary>最大反弹次数。</summary>
        public int MaxBounces;
        /// <summary>最大光线距离。</summary>
        public float MaxRayDistance;
        /// <summary>粗糙度回退阈值。</summary>
        public float RoughnessFallbackThreshold;
        /// <summary>启用折射。</summary>
        public int EnableRefraction;
        /// <summary>帧索引 (用于时序/降噪)。</summary>
        public int FrameIndex;
        /// <summary>每像素采样数。</summary>
        public int SamplesPerPixel;
        /// <summary>Halton 抖动偏移。</summary>
        public Vector2 Jitter;
        /// <summary>AO 半径。</summary>
        public float AORadius;
        /// <summary>AO 采样数。</summary>
        public int AOSamples;
        /// <summary>阴影采样数。</summary>
        public int ShadowSamples;
        /// <summary>填充。</summary>
        public float Pad0, Pad1, Pad2;
    }

    /// <summary>RT 降噪常量。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct RTDenoiserConstants
    {
        /// <summary>视口尺寸倒数。</summary>
        public Vector2 InvResolution;
        /// <summary>时序累积 alpha (指数移动平均系数)。</summary>
        public float TemporalAlpha;
        /// <summary>方差估计半径。</summary>
        public int VarianceRadius;
        /// <summary>atrous 滤波迭代次数。</summary>
        public int AtrousIterations;
        /// <summary>深度方差阈值 (边缘停止)。</summary>
        public float DepthSigma;
        /// <summary>法线方差阈值 (边缘停止)。</summary>
        public float NormalSigma;
        /// <summary>亮度方差阈值 (边缘停止)。</summary>
        public float LuminanceSigma;
        /// <summary>当前 atrous 步宽。</summary>
        public int StepWidth;
        /// <summary>降噪器类型 (0=SVGF/1=ReLAX/2=A-SVGF)。</summary>
        public int DenoiserType;
        /// <summary>自适应方差缩放 (A-SVGF)。</summary>
        public float AdaptiveScale;
        /// <summary>填充。</summary>
        public float Pad0, Pad1;
    }

    /// <summary>
    /// 加速结构管理器: 管理 TLAS/BLAS, 与 PathTracer 共享加速结构。
    /// 当 PathTracer 已构建 TLAS/BLAS 时, 反射系统复用同一结构避免重复构建。
    /// </summary>
    public sealed class RTAccelerationStructureManager : IDisposable
    {
        private readonly RHIDevice _device;
        private readonly Dictionary<Guid, RHIAccelerationStructure> _blasCache = new();
        private RHIAccelerationStructure? _tlas;
        private RHIBuffer? _scratchBuffer;
        private RHIBuffer? _instanceBuffer;
        private readonly List<TLASInstance> _instances = new();

        // 与 PathTracer 共享标志
        private PathTracerAccelerationStructure? _sharedPathTracerAS;

        /// <summary>当前 TLAS。</summary>
        public RHIAccelerationStructure? TLAS => _tlas;
        /// <summary>TLAS GPU 虚拟地址 (供 TraceRay 使用)。</summary>
        public ulong TlasGpuVA => _tlas?.GpuVirtualAddress ?? 0;
        /// <summary>是否已构建。</summary>
        public bool IsBuilt => _tlas != null && _tlas.IsBuilt;
        /// <summary>共享的 PathTracer 加速结构引用。</summary>
        public PathTracerAccelerationStructure? SharedPathTracerAS => _sharedPathTracerAS;

        /// <summary>构造。</summary>
        public RTAccelerationStructureManager(RHIDevice device)
        {
            _device = device;
        }

        /// <summary>已登记的光追实例数。</summary>
        public int InstanceCount => _instances.Count;

        /// <summary>添加一个 TLAS 实例 (供光追阴影系统使用)。</summary>
        public void AddInstance(TLASInstance instance)
        {
            _instances.Add(instance);
        }

        /// <summary>根据质量配置预配置加速结构构建参数 (占位实现)。</summary>
        public void BuildPipeline(RayTracedShadowConfig config)
        {
            // 由后端 RHI 在首次 Build 时提交 TLAS/BLAS 构建命令。
        }

        /// <summary>提交 TLAS 构建命令 (占位实现, 实际构建由后端完成)。</summary>
        public void Build(RHICommandList cmdList)
        {
            // 由后端 DXR/Vulkan RT 实现 TLAS 构建。
        }

        /// <summary>
        /// 与 PathTracer 共享加速结构。当 PathTracer 已构建 TLAS/BLAS 时调用,
        /// 反射系统直接复用, 避免重复构建消耗。
        /// </summary>
        public void ShareWithPathTracer(PathTracerAccelerationStructure pathTracerAS)
        {
            _sharedPathTracerAS = pathTracerAS;
        }

        /// <summary>为网格构建 BLAS (底层加速结构)。</summary>
        /// <param name="meshId">网格唯一 ID。</param>
        /// <param name="vertexBuffer">顶点缓冲。</param>
        /// <param name="indexBuffer">索引缓冲。</param>
        /// <param name="vertexCount">顶点数。</param>
        /// <param name="indexCount">索引数。</param>
        /// <param name="cmdList">用于提交构建命令的命令列表。</param>
        public void BuildBLAS(Guid meshId, RHIBuffer vertexBuffer, RHIBuffer indexBuffer,
            int vertexCount, int indexCount, RHICommandList cmdList)
        {
            // 若已与 PathTracer 共享, 复用其 BLAS 构建逻辑
            if (_sharedPathTracerAS != null)
            {
                _sharedPathTracerAS.BuildBLAS(meshId, vertexBuffer, indexBuffer, vertexCount, indexCount);
                return;
            }

            // 独立 BLAS 构建 (此处由后端 RHIAccelerationStructure 实现具体构建)
            // 估算 BLAS 所需 scratch 与结果缓冲大小
            ulong blasSize = (ulong)(indexCount / 3 * 64 + 4096);
            var scratchDesc = new RHIBufferDesc
            {
                SizeInBytes = blasSize,
                Flags = RHIBufferFlags.UnorderedAccess,
                HeapType = RHIResourceHeapType.Default
            };
            using var scratch = _device.CreateBuffer(scratchDesc, $"BLASScratch_{meshId}");
            // 实际构建命令由后端实现 BuildAccelerationStructure 扩展方法提交
            // cmdList.BuildAccelerationStructure(blas, scratch, ...);
        }

        /// <summary>构建 TLAS (顶层加速结构), 组合所有实例。</summary>
        /// <param name="cmdList">命令列表。</param>
        /// <param name="instanceCount">实例数。</param>
        public void BuildTLAS(RHICommandList cmdList, int instanceCount)
        {
            if (_sharedPathTracerAS != null)
            {
                _sharedPathTracerAS.BuildTLAS(cmdList, instanceCount);
                return;
            }

            // 实例缓冲: 每实例 64 字节 (3x4 变换 + 图元偏移 + 8 字节 BLAS GPU VA)
            var instDesc = new RHIBufferDesc
            {
                SizeInBytes = (ulong)(instanceCount * 64),
                Flags = RHIBufferFlags.Structured | RHIBufferFlags.ShaderResource,
                Stride = 64,
                HeapType = RHIResourceHeapType.Upload
            };
            _instanceBuffer?.Dispose();
            _instanceBuffer = _device.CreateBuffer(instDesc, "RTTLASInstances");

            // scratch 缓冲
            ulong tlasSize = (ulong)(instanceCount * 128 + 65536);
            _scratchBuffer?.Dispose();
            _scratchBuffer = _device.CreateBuffer(new RHIBufferDesc
            {
                SizeInBytes = tlasSize,
                Flags = RHIBufferFlags.UnorderedAccess,
                HeapType = RHIResourceHeapType.Default
            }, "RTTLASScratch");

            // 实际 TLAS 构建由后端实现 (此处仅分配资源)
        }

        /// <summary>释放。</summary>
        public void Dispose()
        {
            _tlas?.Dispose();
            _scratchBuffer?.Dispose();
            _instanceBuffer?.Dispose();
            foreach (var blas in _blasCache.Values) blas.Dispose();
            GC.SuppressFinalize(this);
        }
    }

    /// <summary>
    /// RTPSO 管理器: 创建并管理反射/折射/阴影/AO 所需的光线追踪管线状态对象。
    /// 每种用途一个 RTPSO, 含各自的 RayGen/Miss/HitGroup 配置。
    /// </summary>
    public sealed class RTPipelineManager : IDisposable
    {
        private readonly RHIDevice _device;
        private RHIRayTracingPipeline? _reflectionPipeline;
        private RHIRayTracingPipeline? _refractionPipeline;
        private RHIRayTracingPipeline? _shadowPipeline;
        private RHIRayTracingPipeline? _aoPipeline;
        private RHIRootSignature? _globalRootSig;

        /// <summary>反射 RTPSO。</summary>
        public RHIRayTracingPipeline? ReflectionPipeline => _reflectionPipeline;
        /// <summary>折射 RTPSO。</summary>
        public RHIRayTracingPipeline? RefractionPipeline => _refractionPipeline;
        /// <summary>阴影 RTPSO。</summary>
        public RHIRayTracingPipeline? ShadowPipeline => _shadowPipeline;
        /// <summary>AO RTPSO。</summary>
        public RHIRayTracingPipeline? AOPipeline => _aoPipeline;
        /// <summary>全局根签名。</summary>
        public RHIRootSignature? GlobalRootSignature => _globalRootSig;

        /// <summary>构造。</summary>
        public RTPipelineManager(RHIDevice device)
        {
            _device = device;
        }

        /// <summary>初始化所有 RTPSO 与根签名。</summary>
        public void Initialize()
        {
            CreateGlobalRootSignature();
            CreateReflectionPipeline();
            CreateRefractionPipeline();
            CreateShadowPipeline();
            CreateAOPipeline();
        }

        /// <summary>创建全局根签名 (所有 RT 管线共享)。</summary>
        private void CreateGlobalRootSignature()
        {
            var desc = new RHIRootSignatureDesc
            {
                Parameters = new RHIRootParameter[]
                {
                    // b0: 主常量 (RTReflectionConstants)
                    new() { ParameterType = RHIRootParameterType.CBV, ShaderVisibility = RHIShaderVisibility.All,
                            Descriptor = new RHIRootDescriptor { ShaderRegister = 0 } },
                    // b1: 降噪常量
                    new() { ParameterType = RHIRootParameterType.CBV, ShaderVisibility = RHIShaderVisibility.All,
                            Descriptor = new RHIRootDescriptor { ShaderRegister = 1 } },
                    // t0~t15: SRV 表 (G-Buffer/TLAS/材质/光照/历史)
                    new() { ParameterType = RHIRootParameterType.DescriptorTable, ShaderVisibility = RHIShaderVisibility.All,
                            DescriptorTable = new RHIRootDescriptorTable { Ranges = new[] {
                                new RHIRootDescriptorRange { RangeType = RHIDescriptorRangeType.SRV, NumDescriptors = 16, BaseShaderRegister = 0 } } } },
                    // u0~u7: UAV 表 (反射/折射/阴影/AO 输出)
                    new() { ParameterType = RHIRootParameterType.DescriptorTable, ShaderVisibility = RHIShaderVisibility.All,
                            DescriptorTable = new RHIRootDescriptorTable { Ranges = new[] {
                                new RHIRootDescriptorRange { RangeType = RHIDescriptorRangeType.UAV, NumDescriptors = 8, BaseShaderRegister = 0 } } } }
                },
                StaticSamplers = new[] { RHISamplerDesc.LinearWrap, RHISamplerDesc.PointClamp, RHISamplerDesc.AnisotropicWrap }
            };
            _globalRootSig = _device.CreateRootSignature(desc, "RTReflectionGlobalRootSig");
        }

        /// <summary>创建反射 RTPSO (含反射 HitGroup)。</summary>
        private void CreateReflectionPipeline()
        {
            var desc = new RHIRayTracingPipelineDesc
            {
                Name = "RTReflectionPipeline",
                RayGenEntry = "ReflectionRayGen",
                MaxPayloadSizeInBytes = 32,
                MaxAttributeSizeInBytes = 8,
                MaxRecursionDepth = 4,
                GlobalRootSignature = _globalRootSig,
                MissEntries = new[] { "ReflectionMiss", "RefractionMiss", "ShadowMiss", "AOMiss" },
                HitGroups = new[]
                {
                    new RHIHitGroupDesc
                    {
                        Name = "ReflectionHitGroup",
                        ClosestHitEntry = "ReflectionClosestHit",
                        AnyHitEntry = "ReflectionAnyHit",
                        GeometryType = RHIRayTracingGeometryType.Triangles
                    },
                    new RHIHitGroupDesc
                    {
                        Name = "RefractionHitGroup",
                        ClosestHitEntry = "RefractionClosestHit",
                        AnyHitEntry = "RefractionAnyHit",
                        GeometryType = RHIRayTracingGeometryType.Triangles
                    },
                    new RHIHitGroupDesc
                    {
                        Name = "ShadowHitGroup",
                        AnyHitEntry = "ShadowAnyHit",
                        GeometryType = RHIRayTracingGeometryType.Triangles
                    },
                    new RHIHitGroupDesc
                    {
                        Name = "AOHitGroup",
                        GeometryType = RHIRayTracingGeometryType.Triangles
                    }
                }
            };
            // 着色器字节码由编译器注入, 此处仅描述管线结构
            desc.ShaderLibrary = ShaderManager.Instance.GetBytecode("Reflections/RTReflections", "", RHIShaderStage.Library);
            // _reflectionPipeline = _device.CreateRayTracingPipelineState(desc);
            // 注: RHI 设备的 CreateRayTracingPipelineState 方法由后端实现, 此处省略具体调用。
        }

        /// <summary>创建折射 RTPSO。</summary>
        private void CreateRefractionPipeline()
        {
            var desc = new RHIRayTracingPipelineDesc
            {
                Name = "RTRefractionPipeline",
                RayGenEntry = "RefractionRayGen",
                MaxPayloadSizeInBytes = 40, // 折射负载含 IOR/吸收, 略大
                MaxAttributeSizeInBytes = 8,
                MaxRecursionDepth = 4,
                GlobalRootSignature = _globalRootSig,
                MissEntries = new[] { "RefractionMiss" },
                HitGroups = new[]
                {
                    new RHIHitGroupDesc
                    {
                        Name = "RefractionHitGroup",
                        ClosestHitEntry = "RefractionClosestHit",
                        AnyHitEntry = "RefractionAnyHit",
                        GeometryType = RHIRayTracingGeometryType.Triangles
                    }
                }
            };
            desc.ShaderLibrary = ShaderManager.Instance.GetBytecode("Reflections/RTReflections", "", RHIShaderStage.Library);
            // _refractionPipeline = _device.CreateRayTracingPipelineState(desc);
        }

        /// <summary>创建阴影 RTPSO (硬阴影 + 软阴影)。</summary>
        private void CreateShadowPipeline()
        {
            var desc = new RHIRayTracingPipelineDesc
            {
                Name = "RTShadowPipeline",
                RayGenEntry = "ShadowRayGen",
                MaxPayloadSizeInBytes = 8,  // 阴影负载仅可见性 (float)
                MaxAttributeSizeInBytes = 8,
                MaxRecursionDepth = 1,     // 阴影射线不反弹
                GlobalRootSignature = _globalRootSig,
                MissEntries = new[] { "ShadowMiss" },
                HitGroups = new[]
                {
                    new RHIHitGroupDesc
                    {
                        Name = "ShadowHitGroup",
                        AnyHitEntry = "ShadowAnyHit",
                        GeometryType = RHIRayTracingGeometryType.Triangles
                    }
                }
            };
            desc.ShaderLibrary = ShaderManager.Instance.GetBytecode("Reflections/RTReflections", "", RHIShaderStage.Library);
            // _shadowPipeline = _device.CreateRayTracingPipelineState(desc);
        }

        /// <summary>创建 AO RTPSO (GTAO/GI 替代)。</summary>
        private void CreateAOPipeline()
        {
            var desc = new RHIRayTracingPipelineDesc
            {
                Name = "RTAOPipeline",
                RayGenEntry = "AORayGen",
                MaxPayloadSizeInBytes = 8,
                MaxAttributeSizeInBytes = 8,
                MaxRecursionDepth = 1,     // AO 射线不反弹
                GlobalRootSignature = _globalRootSig,
                MissEntries = new[] { "AOMiss" },
                HitGroups = new[]
                {
                    new RHIHitGroupDesc
                    {
                        Name = "AOHitGroup",
                        GeometryType = RHIRayTracingGeometryType.Triangles
                    }
                }
            };
            desc.ShaderLibrary = ShaderManager.Instance.GetBytecode("Reflections/RTReflections", "", RHIShaderStage.Library);
            // _aoPipeline = _device.CreateRayTracingPipelineState(desc);
        }

        /// <summary>释放。</summary>
        public void Dispose()
        {
            _reflectionPipeline?.Dispose();
            _refractionPipeline?.Dispose();
            _shadowPipeline?.Dispose();
            _aoPipeline?.Dispose();
            _globalRootSig?.Dispose();
            GC.SuppressFinalize(this);
        }
    }

    /// <summary>
    /// SBT 构建器: 将 RTPSO 的着色器标识符与局部根参数打包成 GPU 缓冲,
    /// 生成 RayGen/Miss/HitGroup 三段, 供 DispatchRays 使用。
    /// </summary>
    public sealed class SBTBuilder : IDisposable
    {
        private readonly RHIDevice _device;
        private RHIShaderBindingTable? _currentSbt;

        /// <summary>当前构建的 SBT。</summary>
        public RHIShaderBindingTable? CurrentSBT => _currentSbt;

        /// <summary>构造。</summary>
        public SBTBuilder(RHIDevice device)
        {
            _device = device;
        }

        /// <summary>
        /// 构建 SBT。
        /// </summary>
        /// <param name="pipeline">RTPSO (提供着色器标识符)。</param>
        /// <param name="rayGenEntry">RayGen 导出名。</param>
        /// <param name="missEntries">Miss 导出名数组 (按光线类型索引)。</param>
        /// <param name="hitGroupEntries">HitGroup 导出名数组 (按实例)。</param>
        /// <param name="localRootArgs">局部根参数 (按命中组, 可空)。</param>
        public RHIShaderBindingTable Build(
            RHIRayTracingPipeline pipeline,
            string rayGenEntry,
            string[] missEntries,
            string[] hitGroupEntries,
            byte[][]? localRootArgs = null)
        {
            _currentSbt?.Dispose();
            var sbt = new RHIShaderBindingTable();

            int idSize = pipeline.ShaderIdentifierSize > 0 ? pipeline.ShaderIdentifierSize : 32;
            // 记录步长 = 着色器 ID + 局部根参数, 256 字节对齐 (DXR 要求)
            uint stride = (uint)((idSize + 8 + 255) & ~255);
            sbt.Stride = stride;

            // RayGen 段 (单条记录)
            byte[] rayGenId = pipeline.GetShaderIdentifier(rayGenEntry);
            sbt.RayGenBuffer = CreateSbtBuffer(rayGenId, stride, 1, "SBT_RayGen");
            sbt.MissCount = missEntries.Length;

            // Miss 段
            if (missEntries.Length > 0)
            {
                sbt.MissBuffer = CreateSbtBufferMulti(pipeline, missEntries, stride, null, "SBT_Miss");
            }

            // HitGroup 段
            sbt.HitGroupCount = hitGroupEntries.Length;
            if (hitGroupEntries.Length > 0)
            {
                sbt.HitGroupBuffer = CreateSbtBufferMulti(pipeline, hitGroupEntries, stride, localRootArgs, "SBT_HitGroup");
            }

            _currentSbt = sbt;
            return sbt;
        }

        /// <summary>创建单条记录的 SBT 缓冲 (RayGen)。</summary>
        private RHIBuffer CreateSbtBuffer(byte[] shaderId, uint stride, int count, string name)
        {
            var data = new byte[stride * count];
            Array.Copy(shaderId, 0, data, 0, Math.Min(shaderId.Length, stride));
            var desc = new RHIBufferDesc
            {
                SizeInBytes = stride * (uint)count,
                Flags = RHIBufferFlags.ShaderResource,
                HeapType = RHIResourceHeapType.Default
            };
            var buf = _device.CreateBuffer(desc, name);
            _device.UploadToBuffer(buf, data);
            return buf;
        }

        /// <summary>创建多条记录的 SBT 缓冲 (Miss/HitGroup)。</summary>
        private RHIBuffer CreateSbtBufferMulti(RHIRayTracingPipeline pipeline, string[] entries,
            uint stride, byte[][]? localRootArgs, string name)
        {
            int idSize = pipeline.ShaderIdentifierSize > 0 ? pipeline.ShaderIdentifierSize : 32;
            var data = new byte[stride * entries.Length];
            for (int i = 0; i < entries.Length; i++)
            {
                byte[] id = pipeline.GetShaderIdentifier(entries[i]);
                int offset = (int)(stride * i);
                Array.Copy(id, 0, data, offset, Math.Min(id.Length, idSize));
                // 追加局部根参数 (若有)
                if (localRootArgs != null && i < localRootArgs.Length && localRootArgs[i] != null)
                {
                    int argOffset = offset + idSize;
                    Array.Copy(localRootArgs[i], 0, data, argOffset,
                        Math.Min(localRootArgs[i].Length, stride - idSize));
                }
            }
            var desc = new RHIBufferDesc
            {
                SizeInBytes = stride * (uint)entries.Length,
                Flags = RHIBufferFlags.ShaderResource,
                HeapType = RHIResourceHeapType.Default
            };
            var buf = _device.CreateBuffer(desc, name);
            _device.UploadToBuffer(buf, data);
            return buf;
        }

        /// <summary>释放。</summary>
        public void Dispose()
        {
            _currentSbt?.Dispose();
            GC.SuppressFinalize(this);
        }
    }

    /// <summary>
    /// RT 降噪器 (SVGF / ReLAX / A-SVGF 风格)。
    /// 降噪低采样 (1~2 spp) 的 RT 反射/折射结果, 修复噪点:
    ///   1. 时序累积 — 指数移动平均 (EMA) 复用历史帧
    ///   2. 空间方差估计 — 在邻域内估计反射辐射度方差
    ///   3. G-Buffer 引导滤波 — 用法线/深度/粗糙度作为边缘停止函数
    ///   4. A-SVGF 自适应 — 按方差自适应调整滤波强度与采样
    /// </summary>
    public sealed class RTDenoiser : IDisposable
    {
        private readonly RHIDevice _device;
        private readonly RTDenoiserType _type;
        private RHIRootSignature? _rootSig;
        private RHIPipelineState? _temporalPso;    // 时序累积
        private RHIPipelineState? _variancePso;    // 空间方差估计
        private RHIPipelineState? _atrousPso;      // G-Buffer 引导 atrous 滤波
        private RHIPipelineState? _adaptivePso;    // A-SVGF 自适应

        private RHITexture? _historyBuffer;        // 历史帧累积
        private RHITexture? _varianceBuffer;       // 方差缓冲
        private RHITexture? _momentsBuffer;        // 一阶/二阶矩 (A-SVGF)
        private RHIBuffer? _constantsCB;

        private int _frameIndex;

        /// <summary>降噪器类型。</summary>
        public RTDenoiserType Type => _type;

        /// <summary>构造。</summary>
        public RTDenoiser(RHIDevice device, RTDenoiserType type = RTDenoiserType.ASVGF)
        {
            _device = device;
            _type = type;
        }

        /// <summary>初始化: 创建 PSO 与根签名。</summary>
        public void Initialize()
        {
            CreateRootSignature();
            CreatePipelines();
            CreateConstants();
        }

        private void CreateRootSignature()
        {
            var desc = new RHIRootSignatureDesc
            {
                Parameters = new RHIRootParameter[]
                {
                    new() { ParameterType = RHIRootParameterType.CBV, ShaderVisibility = RHIShaderVisibility.All,
                            Descriptor = new RHIRootDescriptor { ShaderRegister = 0 } },
                    new() { ParameterType = RHIRootParameterType.DescriptorTable, ShaderVisibility = RHIShaderVisibility.All,
                            DescriptorTable = new RHIRootDescriptorTable { Ranges = new[] {
                                new RHIRootDescriptorRange { RangeType = RHIDescriptorRangeType.SRV, NumDescriptors = 8, BaseShaderRegister = 0 } } } },
                    new() { ParameterType = RHIRootParameterType.DescriptorTable, ShaderVisibility = RHIShaderVisibility.All,
                            DescriptorTable = new RHIRootDescriptorTable { Ranges = new[] {
                                new RHIRootDescriptorRange { RangeType = RHIDescriptorRangeType.UAV, NumDescriptors = 4, BaseShaderRegister = 0 } } } }
                },
                StaticSamplers = new[] { RHISamplerDesc.PointClamp }
            };
            _rootSig = _device.CreateRootSignature(desc, "RTDenoiserRootSig");
        }

        private void CreatePipelines()
        {
            _temporalPso = _device.CreateComputePipelineState(new RHIComputePipelineDesc
            { Name = "RTDenoise_Temporal", CS = ShaderManager.Instance.GetBytecode("Reflections/RTReflections", "RTTemporalAccumCS", RHIShaderStage.Compute) });
            _variancePso = _device.CreateComputePipelineState(new RHIComputePipelineDesc
            { Name = "RTDenoise_Variance", CS = ShaderManager.Instance.GetBytecode("Reflections/RTReflections", "RTVarianceCS", RHIShaderStage.Compute) });
            _atrousPso = _device.CreateComputePipelineState(new RHIComputePipelineDesc
            { Name = "RTDenoise_Atrous", CS = ShaderManager.Instance.GetBytecode("Reflections/RTReflections", "RTAtrousCS", RHIShaderStage.Compute) });
            _adaptivePso = _device.CreateComputePipelineState(new RHIComputePipelineDesc
            { Name = "RTDenoise_Adaptive", CS = ShaderManager.Instance.GetBytecode("Reflections/RTReflections", "RTAdaptiveCS", RHIShaderStage.Compute) });
        }

        private void CreateConstants()
        {
            uint cbSize = (uint)((Marshal.SizeOf<RTDenoiserConstants>() + 255) & ~255);
            _constantsCB = _device.CreateBuffer(new RHIBufferDesc
            {
                SizeInBytes = cbSize,
                Flags = RHIBufferFlags.ConstantBuffer,
                HeapType = RHIResourceHeapType.Upload
            }, "RTDenoiserCB");
        }

        /// <summary>屏幕尺寸变化时重建缓冲。</summary>
        public void Resize(int width, int height)
        {
            _historyBuffer?.Dispose();
            _varianceBuffer?.Dispose();
            _momentsBuffer?.Dispose();

            RHITextureDesc MakeUAV(string name, RHIFormat fmt) => new()
            {
                Dimension = RHIResourceDimension.Texture2D,
                Format = fmt,
                Width = width, Height = height, DepthOrArraySize = 1,
                MipLevels = 1, SampleCount = 1,
                Flags = RHITextureFlags.UnorderedAccess | RHITextureFlags.ShaderResource
            };
            _historyBuffer = _device.CreateTexture(MakeUAV("RTDenoise_History", RHIFormat.R16G16B16A16_Float), "RTDenoise_History");
            _varianceBuffer = _device.CreateTexture(MakeUAV("RTDenoise_Variance", RHIFormat.R16_Float), "RTDenoise_Variance");
            _momentsBuffer = _device.CreateTexture(MakeUAV("RTDenoise_Moments", RHIFormat.R16G16B16A16_Float), "RTDenoise_Moments");
            _frameIndex = 0;
        }

        /// <summary>
        /// 执行降噪。输入当前帧的反射/折射辐射度与 G-Buffer, 输出降噪结果。
        /// </summary>
        public void Denoise(RHICommandList cmdList,
            RHIDescriptorHandle inputRadiance,
            RHIDescriptorHandle geometrybufferNormal,
            RHIDescriptorHandle geometrybufferDepth,
            RHIDescriptorHandle geometrybufferRoughness,
            RHIDescriptorHandle outputUav,
            int width, int height, bool cameraMoved)
        {
            cmdList.SetComputeRootSignature(_rootSig!);
            _frameIndex++;
            if (cameraMoved) _frameIndex = 0;

            // Pass 1: 时序累积 (EMA 复用历史帧)
            RunTemporalAccumulation(cmdList, inputRadiance, geometrybufferDepth, width, height, cameraMoved);

            // Pass 2: 空间方差估计
            RunVarianceEstimation(cmdList, geometrybufferNormal, geometrybufferDepth, width, height);

            // Pass 3: A-SVGF 自适应 (按方差调整)
            if (_type == RTDenoiserType.ASVGF)
            {
                RunAdaptivePass(cmdList, width, height);
            }

            // Pass 4: G-Buffer 引导 atrous 滤波 (多尺度迭代)
            RunAtrousFilter(cmdList, geometrybufferNormal, geometrybufferDepth, geometrybufferRoughness, outputUav, width, height);
        }

        /// <summary>Pass 1: 时序累积 (指数移动平均)。</summary>
        private void RunTemporalAccumulation(RHICommandList cmdList,
            RHIDescriptorHandle input, RHIDescriptorHandle depth, int w, int h, bool cameraMoved)
        {
            cmdList.SetPipelineState(_temporalPso!);
            cmdList.SetComputeRootDescriptorTable(1, input);

            var cb = new RTDenoiserConstants
            {
                InvResolution = new Vector2(1f / w, 1f / h),
                TemporalAlpha = cameraMoved ? 1.0f : 0.1f, // 相机移动时放弃历史
                VarianceRadius = 3,
                AtrousIterations = 5,
                DepthSigma = 0.1f,
                NormalSigma = 64f,
                LuminanceSigma = 0.2f,
                StepWidth = 1,
                DenoiserType = (int)_type,
                AdaptiveScale = 1.0f
            };
            _device.UploadToBuffer(_constantsCB!, new[] { cb });
            cmdList.SetComputeRootConstantBufferView(0, _constantsCB!);

            cmdList.TransitionTo(_historyBuffer!, RHIResourceState.UnorderedAccess);
            cmdList.Dispatch((w + 7) / 8, (h + 7) / 8, 1);
            cmdList.TransitionTo(_historyBuffer, RHIResourceState.NonPixelShaderResource);
        }

        /// <summary>Pass 2: 空间方差估计。</summary>
        private void RunVarianceEstimation(RHICommandList cmdList,
            RHIDescriptorHandle normal, RHIDescriptorHandle depth, int w, int h)
        {
            cmdList.SetPipelineState(_variancePso!);
            cmdList.SetComputeRootDescriptorTable(1, _device.CreateSRV(_historyBuffer!));
            cmdList.TransitionTo(_varianceBuffer!, RHIResourceState.UnorderedAccess);
            cmdList.Dispatch((w + 7) / 8, (h + 7) / 8, 1);
            cmdList.TransitionTo(_varianceBuffer, RHIResourceState.NonPixelShaderResource);
        }

        /// <summary>Pass 3: A-SVGF 自适应 (按方差调整滤波强度与采样)。</summary>
        private void RunAdaptivePass(RHICommandList cmdList, int w, int h)
        {
            cmdList.SetPipelineState(_adaptivePso!);
            cmdList.SetComputeRootDescriptorTable(1, _device.CreateSRV(_varianceBuffer!));
            cmdList.TransitionTo(_momentsBuffer!, RHIResourceState.UnorderedAccess);
            cmdList.Dispatch((w + 7) / 8, (h + 7) / 8, 1);
            cmdList.TransitionTo(_momentsBuffer, RHIResourceState.NonPixelShaderResource);
        }

        /// <summary>Pass 4: G-Buffer 引导 atrous 滤波 (多尺度迭代)。</summary>
        private void RunAtrousFilter(RHICommandList cmdList,
            RHIDescriptorHandle normal, RHIDescriptorHandle depth,
            RHIDescriptorHandle roughness, RHIDescriptorHandle output, int w, int h)
        {
            cmdList.SetPipelineState(_atrousPso!);
            int iterations = _type == RTDenoiserType.ReLAX ? 4 : 5;
            for (int i = 0; i < iterations; i++)
            {
                var cb = new RTDenoiserConstants
                {
                    InvResolution = new Vector2(1f / w, 1f / h),
                    TemporalAlpha = 0.1f,
                    VarianceRadius = 3,
                    AtrousIterations = iterations,
                    DepthSigma = 0.1f,
                    NormalSigma = 64f,
                    LuminanceSigma = 0.2f,
                    StepWidth = 1 << i,
                    DenoiserType = (int)_type,
                    AdaptiveScale = _type == RTDenoiserType.ASVGF ? 1.5f : 1.0f
                };
                _device.UploadToBuffer(_constantsCB!, new[] { cb });
                cmdList.SetComputeRootConstantBufferView(0, _constantsCB!);
                cmdList.SetComputeRootDescriptorTable(1, _device.CreateSRV(_historyBuffer!));
                cmdList.Dispatch((w + 7) / 8, (h + 7) / 8, 1);
            }
        }

        /// <summary>释放。</summary>
        public void Dispose()
        {
            _rootSig?.Dispose();
            _temporalPso?.Dispose();
            _variancePso?.Dispose();
            _atrousPso?.Dispose();
            _adaptivePso?.Dispose();
            _historyBuffer?.Dispose();
            _varianceBuffer?.Dispose();
            _momentsBuffer?.Dispose();
            _constantsCB?.Dispose();
            GC.SuppressFinalize(this);
        }
    }

    /// <summary>
    /// RT 阴影渲染器: 精确硬阴影 + 多采样软阴影。
    /// 阴影射线从着色点射向光源, 命中则被遮挡 (硬阴影);
    /// 软阴影通过在光源盘上多次采样 (重要性采样) 实现半影。
    /// </summary>
    public sealed class RTShadowRenderer : IDisposable
    {
        private readonly RHIDevice _device;
        private RHIPipelineState? _shadowPso;
        private RHITexture? _shadowRT;          // 阴影可见性 (R8)
        private RHIBuffer? _constantsCB;

        /// <summary>阴影输出纹理。</summary>
        public RHITexture? ShadowResult => _shadowRT;

        /// <summary>构造。</summary>
        public RTShadowRenderer(RHIDevice device) => _device = device;

        /// <summary>初始化。</summary>
        public void Initialize()
        {
            _shadowPso = _device.CreateComputePipelineState(new RHIComputePipelineDesc
            { Name = "RTShadowRayGen", CS = ShaderManager.Instance.GetBytecode("Reflections/RTReflections", "RTShadowCS", RHIShaderStage.Compute) });

            uint cbSize = (uint)((Marshal.SizeOf<RTReflectionConstants>() + 255) & ~255);
            _constantsCB = _device.CreateBuffer(new RHIBufferDesc
            { SizeInBytes = cbSize, Flags = RHIBufferFlags.ConstantBuffer, HeapType = RHIResourceHeapType.Upload },
            "RTShadowCB");
        }

        /// <summary>屏幕尺寸变化时重建阴影 RT。</summary>
        public void Resize(int width, int height)
        {
            _shadowRT?.Dispose();
            _shadowRT = _device.CreateTexture(new RHITextureDesc
            {
                Dimension = RHIResourceDimension.Texture2D,
                Format = RHIFormat.R8_UNorm,
                Width = width, Height = height, DepthOrArraySize = 1,
                MipLevels = 1, SampleCount = 1,
                Flags = RHITextureFlags.UnorderedAccess | RHITextureFlags.ShaderResource
            }, "RTShadowResult");
        }

        /// <summary>
        /// 渲染 RT 阴影。
        /// </summary>
        /// <param name="cmdList">命令列表。</param>
        /// <param name="geometrybufferNormalSrv">G-Buffer 法线 SRV。</param>
        /// <param name="geometrybufferDepthSrv">G-Buffer 深度 SRV。</param>
        /// <param name="lightDir">主光方向 (世界空间)。</param>
        /// <param name="lightRadius">光源半径 (软阴影, 0=硬阴影)。</param>
        /// <param name="samples">采样数。</param>
        /// <param name="width">视口宽。</param>
        /// <param name="height">视口高。</param>
        public void Render(RHICommandList cmdList,
            RHIDescriptorHandle geometrybufferNormalSrv,
            RHIDescriptorHandle geometrybufferDepthSrv,
            Vector3 lightDir, float lightRadius, int samples,
            int width, int height)
        {
            if (_shadowRT == null) return;
            cmdList.SetPipelineState(_shadowPso!);
            cmdList.SetComputeRootDescriptorTable(1, geometrybufferNormalSrv);

            var cb = new RTReflectionConstants
            {
                InvResolution = new Vector2(1f / width, 1f / height),
                MaxRayDistance = 1000f,
                ShadowSamples = samples,
                Pad0 = lightRadius // 复用 padding 槽位传光源半径
            };
            _device.UploadToBuffer(_constantsCB!, new[] { cb });
            cmdList.SetComputeRootConstantBufferView(0, _constantsCB!);

            cmdList.TransitionTo(_shadowRT, RHIResourceState.UnorderedAccess);
            cmdList.Dispatch((width + 7) / 8, (height + 7) / 8, 1);
            cmdList.TransitionTo(_shadowRT, RHIResourceState.NonPixelShaderResource);
        }

        /// <summary>释放。</summary>
        public void Dispose()
        {
            _shadowPso?.Dispose();
            _shadowRT?.Dispose();
            _constantsCB?.Dispose();
            GC.SuppressFinalize(this);
        }
    }

    /// <summary>
    /// RT 环境光遮蔽渲染器 (GTAO/GI 替代)。
    /// 从每个像素沿半球发射多条 AO 射线, 命中即遮蔽。
    /// 相比屏幕空间 AO (SSAO), RT AO 无屏幕外遗漏, 结果更精确。
    /// </summary>
    public sealed class RTAmbientOcclusion : IDisposable
    {
        private readonly RHIDevice _device;
        private RHIPipelineState? _aoPso;
        private RHITexture? _aoRT;              // AO 结果 (R8)
        private RHIBuffer? _constantsCB;

        /// <summary>AO 输出纹理。</summary>
        public RHITexture? AOResult => _aoRT;

        /// <summary>构造。</summary>
        public RTAmbientOcclusion(RHIDevice device) => _device = device;

        /// <summary>初始化。</summary>
        public void Initialize()
        {
            _aoPso = _device.CreateComputePipelineState(new RHIComputePipelineDesc
            { Name = "RTAO", CS = ShaderManager.Instance.GetBytecode("Reflections/RTReflections", "RTAOCS", RHIShaderStage.Compute) });

            uint cbSize = (uint)((Marshal.SizeOf<RTReflectionConstants>() + 255) & ~255);
            _constantsCB = _device.CreateBuffer(new RHIBufferDesc
            { SizeInBytes = cbSize, Flags = RHIBufferFlags.ConstantBuffer, HeapType = RHIResourceHeapType.Upload },
            "RTAOCB");
        }

        /// <summary>屏幕尺寸变化时重建 AO RT。</summary>
        public void Resize(int width, int height)
        {
            _aoRT?.Dispose();
            _aoRT = _device.CreateTexture(new RHITextureDesc
            {
                Dimension = RHIResourceDimension.Texture2D,
                Format = RHIFormat.R8_UNorm,
                Width = width, Height = height, DepthOrArraySize = 1,
                MipLevels = 1, SampleCount = 1,
                Flags = RHITextureFlags.UnorderedAccess | RHITextureFlags.ShaderResource
            }, "RTAOResult");
        }

        /// <summary>渲染 RT AO。</summary>
        public void Render(RHICommandList cmdList,
            RHIDescriptorHandle geometrybufferNormalSrv,
            RHIDescriptorHandle geometrybufferDepthSrv,
            float radius, int samples, int width, int height)
        {
            if (_aoRT == null) return;
            cmdList.SetPipelineState(_aoPso!);
            cmdList.SetComputeRootDescriptorTable(1, geometrybufferNormalSrv);

            var cb = new RTReflectionConstants
            {
                InvResolution = new Vector2(1f / width, 1f / height),
                AORadius = radius,
                AOSamples = samples,
                MaxRayDistance = radius
            };
            _device.UploadToBuffer(_constantsCB!, new[] { cb });
            cmdList.SetComputeRootConstantBufferView(0, _constantsCB!);

            cmdList.TransitionTo(_aoRT, RHIResourceState.UnorderedAccess);
            cmdList.Dispatch((width + 7) / 8, (height + 7) / 8, 1);
            cmdList.TransitionTo(_aoRT, RHIResourceState.NonPixelShaderResource);
        }

        /// <summary>释放。</summary>
        public void Dispose()
        {
            _aoPso?.Dispose();
            _aoRT?.Dispose();
            _constantsCB?.Dispose();
            GC.SuppressFinalize(this);
        }
    }

    /// <summary>
    /// 硬件光线追踪反射/折射系统主类。
    /// 编排 RTPSO 管理、SBT 构建、加速结构、反射/折射 RayGen、降噪、阴影与 AO。
    /// </summary>
    public sealed class RayTracingReflections : IDisposable
    {
        private readonly RHIDevice _device;
        private readonly RTReflectionSettings _settings;

        private RTPipelineManager? _pipelineManager;
        private SBTBuilder? _sbtBuilder;
        private RTAccelerationStructureManager? _asManager;
        private RTDenoiser? _denoiser;
        private RTShadowRenderer? _shadowRenderer;
        private RTAmbientOcclusion? _aoRenderer;

        // 输出纹理
        private RHITexture? _reflectionRT;     // 反射辐射度
        private RHITexture? _refractionRT;     // 折射辐射度
        private RHIBuffer? _constantsCB;

        private int _frameIndex;

        /// <summary>配置。</summary>
        public RTReflectionSettings Settings => _settings;
        /// <summary>是否支持硬件 RT。</summary>
        public bool IsHardwareRTSupported { get; }
        /// <summary>RTPSO 管理器。</summary>
        public RTPipelineManager? PipelineManager => _pipelineManager;
        /// <summary>SBT 构建器。</summary>
        public SBTBuilder? SBTBuilder => _sbtBuilder;
        /// <summary>加速结构管理器。</summary>
        public RTAccelerationStructureManager? AccelerationStructureManager => _asManager;
        /// <summary>降噪器。</summary>
        public RTDenoiser? Denoiser => _denoiser;
        /// <summary>RT 阴影渲染器。</summary>
        public RTShadowRenderer? ShadowRenderer => _shadowRenderer;
        /// <summary>RT AO 渲染器。</summary>
        public RTAmbientOcclusion? AORenderer => _aoRenderer;
        /// <summary>反射输出。</summary>
        public RHITexture? ReflectionResult => _reflectionRT;

        /// <summary>构造。</summary>
        public RayTracingReflections(RHIDevice device, RTReflectionSettings? settings = null)
        {
            _device = device;
            _settings = settings ?? new RTReflectionSettings();
            IsHardwareRTSupported = device.SupportsRayTracing;
        }

        /// <summary>初始化系统。</summary>
        public void Initialize()
        {
            if (!IsHardwareRTSupported) return;

            _pipelineManager = new RTPipelineManager(_device);
            _pipelineManager.Initialize();

            _sbtBuilder = new SBTBuilder(_device);
            _asManager = new RTAccelerationStructureManager(_device);
            _denoiser = new RTDenoiser(_device, _settings.DenoiserType);
            _denoiser.Initialize();

            if (_settings.EnableRTShadows)
            {
                _shadowRenderer = new RTShadowRenderer(_device);
                _shadowRenderer.Initialize();
            }
            if (_settings.EnableRTAO)
            {
                _aoRenderer = new RTAmbientOcclusion(_device);
                _aoRenderer.Initialize();
            }

            CreateConstants();
        }

        /// <summary>创建主常量缓冲。</summary>
        private void CreateConstants()
        {
            uint cbSize = (uint)((Marshal.SizeOf<RTReflectionConstants>() + 255) & ~255);
            _constantsCB = _device.CreateBuffer(new RHIBufferDesc
            { SizeInBytes = cbSize, Flags = RHIBufferFlags.ConstantBuffer, HeapType = RHIResourceHeapType.Upload },
            "RTReflectionCB");
        }

        /// <summary>屏幕尺寸变化时重建渲染目标。</summary>
        public void Resize(int width, int height)
        {
            int rw = _settings.HalfResolution ? width / 2 : width;
            int rh = _settings.HalfResolution ? height / 2 : height;

            _reflectionRT?.Dispose();
            _refractionRT?.Dispose();
            _reflectionRT = CreateUAV(rw, rh, RHIFormat.R16G16B16A16_Float, "RTReflection");
            _refractionRT = CreateUAV(rw, rh, RHIFormat.R16G16B16A16_Float, "RTRefraction");

            _denoiser?.Resize(rw, rh);
            _shadowRenderer?.Resize(width, height);
            _aoRenderer?.Resize(width, height);
        }

        private RHITexture CreateUAV(int w, int h, RHIFormat fmt, string name) => _device.CreateTexture(new RHITextureDesc
        {
            Dimension = RHIResourceDimension.Texture2D,
            Format = fmt,
            Width = w, Height = h, DepthOrArraySize = 1,
            MipLevels = 1, SampleCount = 1,
            Flags = RHITextureFlags.UnorderedAccess | RHITextureFlags.ShaderResource
        }, name);

        /// <summary>
        /// 渲染一帧 RT 反射/折射。
        /// </summary>
        /// <param name="cmdList">命令列表。</param>
        /// <param name="view">视图矩阵。</param>
        /// <param name="proj">投影矩阵。</param>
        /// <param name="cameraPos">相机位置。</param>
        /// <param name="geometrybufferNormalSrv">G-Buffer 法线 SRV。</param>
        /// <param name="geometrybufferDepthSrv">G-Buffer 深度 SRV。</param>
        /// <param name="geometrybufferRoughnessSrv">G-Buffer 粗糙度 SRV。</param>
        /// <param name="lightDir">主光方向。</param>
        /// <param name="width">视口宽。</param>
        /// <param name="height">视口高。</param>
        /// <param name="cameraMoved">相机是否移动 (用于重置时序)。</param>
        public void Render(RHICommandList cmdList,
            in Matrix4x4 view, in Matrix4x4 proj, Vector3 cameraPos,
            RHIDescriptorHandle geometrybufferNormalSrv,
            RHIDescriptorHandle geometrybufferDepthSrv,
            RHIDescriptorHandle geometrybufferRoughnessSrv,
            Vector3 lightDir, int width, int height, bool cameraMoved)
        {
            if (!IsHardwareRTSupported || _asManager == null || !_asManager.IsBuilt) return;
            _frameIndex++;

            int rw = _settings.HalfResolution ? width / 2 : width;
            int rh = _settings.HalfResolution ? height / 2 : height;

            // 写入主常量
            Matrix4x4.Invert(view, out var invView);
            Matrix4x4.Invert(proj, out var invProj);
            var cb = new RTReflectionConstants
            {
                ViewMatrix = view, ProjMatrix = proj,
                InvViewMatrix = invView, InvProjMatrix = invProj,
                CameraPos = cameraPos,
                RayEpsilon = _settings.RayEpsilon,
                InvResolution = new Vector2(1f / rw, 1f / rh),
                MaxBounces = _settings.MaxReflectionBounces,
                MaxRayDistance = _settings.MaxRayDistance,
                RoughnessFallbackThreshold = _settings.RoughnessFallbackThreshold,
                EnableRefraction = _settings.EnableRefraction ? 1 : 0,
                FrameIndex = _frameIndex,
                SamplesPerPixel = _settings.Quality >= RTReflectionQuality.High ? 2 : 1,
                Jitter = HaltonJitter(_frameIndex),
                AORadius = _settings.AORadius,
                AOSamples = _settings.AOSamples,
                ShadowSamples = _settings.ShadowSamples
            };
            _device.UploadToBuffer(_constantsCB!, new[] { cb });
            cmdList.SetComputeRootConstantBufferView(0, _constantsCB!);

            // Pass 1: RT 反射 RayGen
            RunReflectionRayGen(cmdList, geometrybufferNormalSrv, geometrybufferDepthSrv, geometrybufferRoughnessSrv, rw, rh);

            // Pass 2: RT 折射 RayGen (透明材质)
            if (_settings.EnableRefraction)
            {
                RunRefractionRayGen(cmdList, geometrybufferNormalSrv, geometrybufferDepthSrv, rw, rh);
            }

            // Pass 3: RT 降噪
            if (_settings.EnableDenoiser && _denoiser != null)
            {
                _denoiser.Denoise(cmdList, _device.CreateSRV(_reflectionRT!),
                    geometrybufferNormalSrv, geometrybufferDepthSrv, geometrybufferRoughnessSrv,
                    _device.CreateUAV(_reflectionRT), rw, rh, cameraMoved);
            }

            // Pass 4: RT 阴影
            if (_settings.EnableRTShadows && _shadowRenderer != null)
            {
                _shadowRenderer.Render(cmdList, geometrybufferNormalSrv, geometrybufferDepthSrv,
                    lightDir, 0.05f, _settings.ShadowSamples, width, height);
            }

            // Pass 5: RT AO
            if (_settings.EnableRTAO && _aoRenderer != null)
            {
                _aoRenderer.Render(cmdList, geometrybufferNormalSrv, geometrybufferDepthSrv,
                    _settings.AORadius, _settings.AOSamples, width, height);
            }
        }

        /// <summary>Pass 1: RT 反射 RayGen — G-Buffer 法线/粗糙度 → 反射方向 + GGX 重要性采样。</summary>
        private void RunReflectionRayGen(RHICommandList cmdList,
            RHIDescriptorHandle normalSrv, RHIDescriptorHandle depthSrv,
            RHIDescriptorHandle roughnessSrv, int w, int h)
        {
            // 绑定 RTPSO 与 SBT, 调度 DispatchRays
            // 反射方向 = reflect(-viewDir, normal), 粗糙度 > 阈值回退 SSR
            // 注: 实际 DispatchRays 由后端实现, 此处编排资源状态与常量
            cmdList.TransitionTo(_reflectionRT!, RHIResourceState.UnorderedAccess);
            cmdList.SetComputeRootDescriptorTable(1, normalSrv);
            cmdList.Dispatch((w + 7) / 8, (h + 7) / 8, 1);
            cmdList.TransitionTo(_reflectionRT, RHIResourceState.NonPixelShaderResource);
        }

        /// <summary>Pass 2: RT 折射 RayGen — 透明材质折射方向 + Beer-Lambert 吸收。</summary>
        private void RunRefractionRayGen(RHICommandList cmdList,
            RHIDescriptorHandle normalSrv, RHIDescriptorHandle depthSrv, int w, int h)
        {
            // 折射方向 = refract(-viewDir, normal, eta), eta = IOR_outside / IOR_inside
            // Beer-Lambert: 透射率 = exp(-absorption * distance)
            cmdList.TransitionTo(_refractionRT!, RHIResourceState.UnorderedAccess);
            cmdList.SetComputeRootDescriptorTable(1, normalSrv);
            cmdList.Dispatch((w + 7) / 8, (h + 7) / 8, 1);
            cmdList.TransitionTo(_refractionRT, RHIResourceState.NonPixelShaderResource);
        }

        /// <summary>Halton 序列抖动 (低差异采样, 用于时序抗锯齿)。</summary>
        private static Vector2 HaltonJitter(int frame)
        {
            return new Vector2(Halton(frame, 2) - 0.5f, Halton(frame, 3) - 0.5f);
        }

        /// <summary>Halton 序列 (基数 radix)。</summary>
        private static float Halton(int index, int radix)
        {
            float f = 1f, r = 0f;
            int i = index;
            while (i > 0)
            {
                f /= radix;
                r += f * (i % radix);
                i /= radix;
            }
            return r;
        }

        /// <summary>
        /// CPU 侧参考: GGX 重要性采样反射方向 (与 PathTracer.SampleGGX 一致)。
        /// 用于验证 GPU RayGen 反射方向正确性。
        /// </summary>
        public static Vector3 SampleGGXReflection(Vector2 uniform, float roughness, in Vector3 n, in Vector3 viewDir)
        {
            float a = roughness * roughness;
            float phi = 2 * MathF.PI * uniform.X;
            float cosTheta = MathF.Sqrt((1 - uniform.Y) / (1 + (a * a - 1) * uniform.Y));
            float sinTheta = MathF.Sqrt(1 - cosTheta * cosTheta);
            var h = new Vector3(sinTheta * MathF.Cos(phi), sinTheta * MathF.Sin(phi), cosTheta);
            // 切线空间转世界空间
            var up = MathF.Abs(n.Z) < 0.999f ? Vector3.UnitZ : Vector3.UnitX;
            var t = Vector3.Normalize(Vector3.Cross(up, n));
            var b = Vector3.Cross(n, t);
            var halfVec = Vector3.Normalize(t * h.X + b * h.Y + n * h.Z);
            // 反射方向 = reflect(-view, half)
            return Vector3.Normalize(2f * Vector3.Dot(viewDir, halfVec) * halfVec - viewDir);
        }

        /// <summary>
        /// CPU 侧参考: 折射方向计算 (Snell 定律)。
        /// 返回折射方向与是否发生全反射。
        /// </summary>
        /// <param name="incident">入射方向 (归一化)。</param>
        /// <param name="normal">表面法线 (归一化)。</param>
        /// <param name="eta">折射率比 (IOR_outside / IOR_inside)。</param>
        public static (Vector3 Refracted, bool TotalInternalReflection) Refract(
            in Vector3 incident, in Vector3 normal, float eta)
        {
            float cosI = -Vector3.Dot(normal, incident);
            float sinT2 = eta * eta * (1f - cosI * cosI);
            if (sinT2 > 1f) return (Vector3.Zero, true); // 全反射
            float cosT = MathF.Sqrt(1f - sinT2);
            return (Vector3.Normalize(eta * incident + (eta * cosI - cosT) * normal), false);
        }

        /// <summary>
        /// CPU 侧参考: Beer-Lambert 吸收计算。
        /// 透射率 T = exp(-absorption * distance)。
        /// </summary>
        /// <param name="absorption">吸收系数 (RGB, 每米)。</param>
        /// <param name="distance">光线在介质中传播距离。</param>
        public static Vector3 BeerLambert(in Vector3 absorption, float distance)
        {
            return new Vector3(
                MathF.Exp(-absorption.X * distance),
                MathF.Exp(-absorption.Y * distance),
                MathF.Exp(-absorption.Z * distance));
        }

        /// <summary>
        /// CPU 侧参考: Schlick 菲涅尔近似 (用于反射/折射能量分配)。
        /// </summary>
        public static float SchlickFresnelScalar(float cosTheta, float f0)
        {
            return f0 + (1f - f0) * MathF.Pow(1f - cosTheta, 5f);
        }

        /// <summary>与 PathTracer 共享加速结构。</summary>
        public void ShareAccelerationStructure(PathTracerAccelerationStructure pathTracerAS)
        {
            _asManager?.ShareWithPathTracer(pathTracerAS);
        }

        /// <summary>释放所有资源。</summary>
        public void Dispose()
        {
            _pipelineManager?.Dispose();
            _sbtBuilder?.Dispose();
            _asManager?.Dispose();
            _denoiser?.Dispose();
            _shadowRenderer?.Dispose();
            _aoRenderer?.Dispose();
            _reflectionRT?.Dispose();
            _refractionRT?.Dispose();
            _constantsCB?.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}
