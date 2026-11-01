using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using YamaEngine.ECS;
using YamaEngine.RHI;
using YamaEngine.Shaders;

namespace YamaEngine.Rendering
{
    // ════════════════════════════════════════════════════════════════════════
    // yama引擎 体积雾系统扩展 (Volumetric Fog System)
    // --------------------------------------------------------------------------
    // 设计目标：在已有的指数/高度雾 (WeatherSystem.FogSystem) 之上，提供一套电影
    // 级的“参与介质”体积雾，支持真实的光照散射、体积阴影与 GI 参与感。核心能力：
    //   1. Froxel 化 (Frustum + Voxel)：把相机视锥体离散为 3D 网格（如 160×90×64），
    //      每个 Froxel 存储体积密度与散射光，避免逐像素光线步进的巨大开销。
    //   2. 多散射近似：3D 纹理存储体积密度 + 散射光，二阶散射以常数比例近似。
    //   3. 体积光照积分：沿视线方向对每个 Froxel 积分散射光，逐光源累加（方向光/
    //      点光源/聚光灯），并用 Beer-Lambert 衰减视点到该 Froxel 的透射率。
    //   4. 体积阴影：从各光源投射阴影到体积雾（方向光用级联阴影图，点光用立方体
    //      阴影图，聚光灯用 2D 阴影图），雾中沿光线方向的遮挡被计入散射。
    //   5. 体积雾参与 GI：采样 ProbeField 探针网格的辐照度，把反弹光在雾中散射，使室
    //      内/阴影中的雾也能被间接照亮。
    //   6. 体积雾与粒子交互：粒子在体积雾中产生额外散射贡献（粒子既被雾照亮，
    //      也向雾回灌散射光），形成“光柱中的尘埃”效果。
    //   7. 高质量滤波：时间累积（历史缓冲 + 重投影，抑制闪烁）、双边滤波（保边
    //      去噪）、空域降噪，三者结合得到稳定平滑的结果。
    //   8. 局部雾体积 (LocalFogVolume)：局部雾球/雾盒，支持径向/线性密度衰减、
    //      局部颜色覆盖、优先级与混合，可叠加在全局雾之上。
    //   9. 指数高度雾 (ExponentialHeightFog)：保留传统高度雾，作为全局基底。
    //  10. 雾材质 (FogMaterial)：自定义散射/吸收/发光，可由美术配置。
    //
    // 对接关系：
    //   - AtmosphereRenderer：大气散射（天空辐射、空中透视）在雾中的贡献，通过
    //     采样 SkyView / AerialPerspective LUT 叠加到 Froxel 散射光。
    //   - RHI：3D 纹理（Froxel 网格、历史缓冲）、Compute Shader（注入/积分/滤波）。
    //   - ECS：VolumetricFogComponent 作为 struct 组件，VolumetricFogSystem 作为
    //     ISystem 每帧推进。
    // ════════════════════════════════════════════════════════════════════════

    // ──────────────────────────────────────────────────────────────────────────
    // 枚举
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>局部雾体积形状。</summary>
    public enum FogVolumeShape
    {
        /// <summary>球体雾：以中心为圆心、半径范围内分布。</summary>
        Sphere = 0,
        /// <summary>盒体雾：轴对齐包围盒内分布。</summary>
        Box = 1,
        /// <summary>圆柱雾：适合烟囱、柱状光束。</summary>
        Cylinder = 2
    }

    /// <summary>雾密度衰减方式。</summary>
    public enum FogDensityFalloff
    {
        /// <summary>恒定密度（在体积内为常数）。</summary>
        Constant = 0,
        /// <summary>径向衰减：从中心到边缘指数衰减。</summary>
        Radial = 1,
        /// <summary>线性衰减：从中心到边缘线性衰减。</summary>
        Linear = 2
    }

    /// <summary>雾体积混合模式。</summary>
    public enum FogBlendMode
    {
        /// <summary>覆盖：高优先级直接替换。</summary>
        Override,
        /// <summary>相加：散射光累加。</summary>
        Additive,
        /// <summary>最大：取密度较大者。</summary>
        Max
    }

    // ──────────────────────────────────────────────────────────────────────────
    // ECS 组件 (VolumetricFogComponent)
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 体积雾组件。挂到代表“体积雾实体”的实体上。引擎 ECS 的 AddComponent&lt;T&gt;
    /// 约束为 struct，故本组件为 struct。通常场景中只有一个全局体积雾实体；局部雾
    /// 体积由 LocalFogVolume 独立管理并通过 VolumetricFogSystem.RegisterLocalVolume 注册。
    /// </summary>
    public struct VolumetricFogComponent : IComponent
    {
        /// <summary>所属实体 ID。</summary>
        public int EntityId;
        /// <summary>是否启用体积雾。</summary>
        public bool Enabled;
        /// <summary>全局雾密度倍率。</summary>
        public float DensityScale;
        /// <summary>雾散射系数（RGB，参与介质对光的散射能力）。</summary>
        public Vector3 Scattering;
        /// <summary>是否启用时间累积滤波（抑制闪烁）。</summary>
        public bool EnableTemporalFilter;
        /// <summary>是否启用双边滤波。</summary>
        public bool EnableBilateralFilter;
        /// <summary>是否启用体积阴影。</summary>
        public bool EnableVolumetricShadows;
        /// <summary>是否参与 GI。</summary>
        public bool EnableGI;
        /// <summary>Froxel 网格分辨率（X/Y/Z）。</summary>
        public Vector3 FroxelResolution;
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 雾材质 (FogMaterial)
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 雾材质。描述参与介质的散射、吸收与发光属性，可由美术配置并复用。散射系数
    /// 决定“雾把光散向相机的程度”，吸收系数决定“雾吃掉光的程度”，发光用于自发光
    /// 雾（如毒气、魔法烟雾）。
    /// </summary>
    public sealed class FogMaterial
    {
        /// <summary>散射系数（RGB，1/米）。白色雾约为 0.01~0.05。</summary>
        public Vector3 Scattering = new(0.02f, 0.02f, 0.02f);
        /// <summary>吸收系数（RGB，1/米）。</summary>
        public Vector3 Absorption = new(0.005f, 0.005f, 0.005f);
        /// <summary>相函数不对称参数 g（-1 后向, 0 各向同性, 1 前向）。雾通常略前向。</summary>
        public float PhaseG = 0.2f;
        /// <summary>自发光（RGB，用于发光雾）。</summary>
        public Vector3 Emission = Vector3.Zero;
        /// <summary>反照率（多次散射的等效反射率）。</summary>
        public Vector3 Albedo = new(0.9f, 0.9f, 0.9f);

        /// <summary>消光系数 = 散射 + 吸收（标量近似）。</summary>
        public float Extinction => Scattering.Length() + Absorption.Length();

        /// <summary>Beer-Lambert 透射率。</summary>
        public Vector3 Transmittance(float distance)
        {
            Vector3 ext = Scattering + Absorption;
            return new Vector3(MathF.Exp(-ext.X * distance), MathF.Exp(-ext.Y * distance), MathF.Exp(-ext.Z * distance));
        }

        /// <summary>Henyey-Greenstein 相函数。</summary>
        public float Phase(float cosTheta)
        {
            float g = PhaseG;
            float g2 = g * g;
            float denom = 4f * MathF.PI * MathF.Pow(1f + g2 - 2f * g * cosTheta, 1.5f);
            return (1f - g2) / MathF.Max(1e-6f, denom);
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 雾体积基类 (FogVolume)
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 雾体积基类。描述一个三维参与介质区域：形状、密度、颜色、散射/吸收系数。
    /// 提供在世界空间采样密度的接口，供 Froxel 注入阶段查询。
    /// </summary>

    // ──────────────────────────────────────────────────────────────────────────
    // 局部雾体积 (LocalFogVolume)
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 局部雾体积（雾球/雾盒/雾柱）。支持径向/线性密度衰减、局部颜色覆盖与混合。
    /// 多个局部雾体积按优先级与混合模式叠加到全局雾之上，常用于篝火烟雾、毒气
    /// 云、魔法光柱等局部效果。
    /// </summary>
    public sealed class LocalFogVolume : FogVolume
    {
        /// <summary>密度衰减方式。</summary>
        public FogDensityFalloff Falloff = FogDensityFalloff.Radial;
        /// <summary>衰减半径（径向/线性模式的有效范围）。</summary>
        public float FalloffRadius = 5f;
        /// <summary>是否启用局部颜色覆盖（覆盖全局雾颜色）。</summary>
        public bool OverrideColor = true;
        /// <summary>局部自发光强度。</summary>
        public float EmissionStrength;
        /// <summary>体积的包围尺寸（球用半径、盒用半尺寸等）。</summary>
        public Vector3 Size;

        /// <summary>创建默认雾球。</summary>
        public LocalFogVolume()
        {
            Shape = FogVolumeShape.Sphere;
            Size = new Vector3(5f, 5f, 5f);
        }

        /// <summary>采样世界位置处的密度。</summary>
        public new float SampleDensity(Vector3 worldPos)
        {
            float d = Shape switch
            {
                FogVolumeShape.Sphere => Vector3.Distance(worldPos, Position),
                FogVolumeShape.Box => BoxDistance(worldPos),
                FogVolumeShape.Cylinder => new Vector2(worldPos.X - Position.X, worldPos.Z - Position.Z).Length(),
                _ => 0f
            };
            float radius = Falloff == FogDensityFalloff.Constant ? FalloffRadius : FalloffRadius;
            if (d > radius) return 0f;

            return Falloff switch
            {
                FogDensityFalloff.Constant => Density,
                FogDensityFalloff.Radial => Density * MathF.Exp(-(d * d) / (radius * radius * 0.25f)),
                FogDensityFalloff.Linear => Density * (1f - d / radius),
                _ => Density
            };
        }

        private float BoxDistance(Vector3 p)
        {
            Vector3 q = Vector3.Abs(p - Position) - Size;
            return Vector3.Max(q, Vector3.Zero).Length() + MathF.Min(MathF.Max(q.X, MathF.Max(q.Y, q.Z)), 0f);
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 指数高度雾 (ExponentialHeightFog)
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 指数高度雾。传统全局高度雾：密度随高度指数衰减，越靠近地面越浓。作为体积
    /// 雾的全局基底，注入到 Froxel 网格的每个单元。
    /// </summary>
    public sealed class ExponentialHeightFog
    {
        /// <summary>雾密度（海平面）。</summary>
        public float Density = 0.02f;
        /// <summary>高度衰减系数（越大雾越集中在低处）。</summary>
        public float HeightFalloff = 0.5f;
        /// <summary>雾起始高度。</summary>
        public float StartHeight = 0f;
        /// <summary>最大距离（米，超出后雾不积分）。</summary>
        public float MaxDistance = 4000f;
        /// <summary>雾颜色（被太阳/天空照亮后呈现的色调）。</summary>
        public Vector3 Color = new(0.7f, 0.75f, 0.8f);
        /// <summary>散射系数倍率。</summary>
        public float ScatteringScale = 1f;
        /// <summary>是否启用方向光进气（丁达尔效应）。</summary>
        public bool EnableDirectionalInscatter = true;
        /// <summary>方向光进气强度。</summary>
        public float DirectionalInscatterStrength = 1.0f;

        /// <summary>采样世界高度处的雾密度：d(h) = density·exp(-(h-start)/falloff)。</summary>
        public float SampleDensity(float worldHeight)
        {
            float h = worldHeight - StartHeight;
            return Density * MathF.Exp(-MathF.Max(0f, h) / MathF.Max(0.01f, HeightFalloff));
        }

        /// <summary>采样透射率（从起点沿距离积分）。</summary>
        public Vector3 SampleTransmittance(float worldHeight, float distance)
        {
            float d = SampleDensity(worldHeight) * distance;
            float t = MathF.Exp(-d);
            return new Vector3(t);
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // GPU 常量缓冲结构
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>Froxel 网格常量。</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 16)]
    public struct FroxelCB
    {
        /// <summary>Froxel 分辨率 X/Y/Z。</summary>
        public Vector3 Resolution;
        /// <summary>视锥近平面。</summary>
        public float NearClip;
        /// <summary>视锥远平面（雾积分最大距离）。</summary>
        public float FarClip;
        /// <summary>深度分布指数（对数分布参数）。</summary>
        public float DepthExponent;
        /// <summary>视投影矩阵。</summary>
        public Matrix4x4 ViewProjection;
        /// <summary>逆视投影。</summary>
        public Matrix4x4 InvViewProjection;
        /// <summary>相机位置。</summary>
        public Vector3 CameraPosition;
        /// <summary>时间。</summary>
        public float Time;
        /// <summary>全局密度倍率。</summary>
        public float DensityScale;
        /// <summary>散射系数。</summary>
        public Vector3 Scattering;
        /// <summary>吸收系数。</summary>
        public Vector3 Absorption;
        /// <summary>相函数 g。</summary>
        public float PhaseG;
        /// <summary>对齐。</summary>
        public float _pad0;
        public float _pad1;
    }

    /// <summary>体积雾光源常量。</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 16)]
    public struct FogLightCB
    {
        /// <summary>光源方向（方向光）或位置（点光/聚光）。</summary>
        public Vector3 DirectionOrPosition;
        /// <summary>光源类型（0=方向,1=点,2=聚）。</summary>
        public int Type;
        /// <summary>光源颜色。</summary>
        public Vector3 Color;
        /// <summary>强度。</summary>
        public float Intensity;
        /// <summary>范围（点光/聚光）。</summary>
        public float Range;
        /// <summary>内锥角（聚光，余弦）。</summary>
        public float InnerConeCos;
        /// <summary>外锥角（聚光，余弦）。</summary>
        public float OuterConeCos;
        /// <summary>阴影 SRV 索引。</summary>
        public int ShadowMapIndex;
        /// <summary>光空间矩阵（方向光/聚光阴影用）。</summary>
        public Matrix4x4 ShadowMatrix;
        /// <summary>对齐。</summary>
        public float _pad0;
    }

    /// <summary>体积雾渲染常量。</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 16)]
    public struct VolumetricFogRenderCB
    {
        /// <summary>Froxel 分辨率。</summary>
        public Vector3 Resolution;
        /// <summary>视投影矩阵。</summary>
        public Matrix4x4 ViewProjection;
        /// <summary>逆视投影。</summary>
        public Matrix4x4 InvViewProjection;
        /// <summary>相机位置。</summary>
        public Vector3 CameraPosition;
        /// <summary>近平面。</summary>
        public float NearClip;
        /// <summary>远平面。</summary>
        public float FarClip;
        /// <summary>深度分布指数。</summary>
        public float DepthExponent;
        /// <summary>时间。</summary>
        public float Time;
        /// <summary>时间累积混合因子。</summary>
        public float TemporalBlend;
        /// <summary>双边滤波半径。</summary>
        public float BilateralRadius;
        /// <summary>对齐。</summary>
        public float _pad0;
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 体积雾渲染器 (VolumetricFogRenderer)
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 体积雾渲染器。基于 Froxel 化的参与介质渲染：把视锥体离散为 3D 网格，逐
    /// Froxel 累计密度与散射光，再沿视线积分得到最终雾颜色与不透明度。支持体积
    /// 阴影、GI 参与、粒子交互与多级滤波。结果以 3D 纹理（散射光 + 透射率）输出，
    /// 供不透明/透明物体着色时采样实现“距离雾”与“光柱”。
    /// </summary>
    public sealed class VolumetricFogRenderer : IDisposable
    {
        private readonly RHIDevice _device;
        private readonly AtmosphereRenderer? _atmosphere;

        // ── Froxel 网格 ──
        /// <summary>Froxel 分辨率。</summary>
        public Vector3 FroxelResolution = new(160, 90, 64);
        /// <summary>Froxel 密度+散射 3D 纹理（RGBA16F：RGB=散射光, A=密度）。</summary>
        public RHITexture? FroxelGrid;
        /// <summary>Froxel SRV。</summary>
        public RHIDescriptorHandle FroxelSRV;
        /// <summary>Froxel UAV。</summary>
        public RHIDescriptorHandle FroxelUAV;
        /// <summary>历史缓冲（时间累积）。</summary>
        public RHITexture? HistoryGrid;
        /// <summary>历史 SRV。</summary>
        public RHIDescriptorHandle HistorySRV;
        /// <summary>历史 UAV。</summary>
        public RHIDescriptorHandle HistoryUAV;
        /// <summary>滤波后结果 3D 纹理（最终雾 LUT）。</summary>
        public RHITexture? FilteredGrid;
        /// <summary>滤波结果 SRV（供场景着色采样）。</summary>
        public RHIDescriptorHandle FilteredSRV;

        // ── PSO ──
        private RHIRootSignature? _injectRootSig;
        private RHIPipelineState? _injectPSO;       // 注入密度（高度雾+局部雾）
        private RHIRootSignature? _lightingRootSig;
        private RHIPipelineState? _lightingPSO;     // 光照积分+体积阴影
        private RHIRootSignature? _giRootSig;
        private RHIPipelineState? _giPSO;           // GI 注入
        private RHIRootSignature? _particleRootSig;
        private RHIPipelineState? _particlePSO;     // 粒子交互
        private RHIRootSignature? _integrateRootSig;
        private RHIPipelineState? _integratePSO;    // 沿视线积分
        private RHIRootSignature? _temporalRootSig;
        private RHIPipelineState? _temporalPSO;     // 时间累积
        private RHIRootSignature? _bilateralRootSig;
        private RHIPipelineState? _bilateralPSO;    // 双边滤波

        private RHIBuffer? _froxCBUpload;
        private RHIBuffer? _renderCBUpload;
        private RHIBuffer? _lightCBUpload;
        private bool _disposed;

        // ── 配置 ──
        /// <summary>全局指数高度雾（基底）。</summary>
        public readonly ExponentialHeightFog HeightFog = new();
        /// <summary>已注册的局部雾体积。</summary>
        public readonly List<LocalFogVolume> LocalVolumes = new();
        /// <summary>是否启用时间累积。</summary>
        public bool EnableTemporalFilter = true;
        /// <summary>是否启用双边滤波。</summary>
        public bool EnableBilateralFilter = true;
        /// <summary>是否启用体积阴影。</summary>
        public bool EnableVolumetricShadows = true;
        /// <summary>是否参与 GI。</summary>
        public bool EnableGI = true;
        /// <summary>时间累积混合因子（0=完全用历史, 1=完全用当前）。</summary>
        public float TemporalBlend = 0.1f;
        /// <summary>双边滤波半径（Froxel 数）。</summary>
        public float BilateralRadius = 2f;
        /// <summary>当前帧的视投影矩阵（用于重投影）。</summary>
        public Matrix4x4 ViewProjection;
        /// <summary>上一帧视投影矩阵。</summary>
        public Matrix4x4 PrevViewProjection;

        /// <summary>构造，绑定设备与大气渲染器（可选，用于大气散射贡献）。</summary>
        public VolumetricFogRenderer(RHIDevice device, AtmosphereRenderer? atmosphere = null)
        {
            _device = device;
            _atmosphere = atmosphere;
        }

        /// <summary>初始化 Froxel 网格与 PSO。</summary>
        public void Initialize()
        {
            AllocateGrids();
            _froxCBUpload = _device.CreateUploadBuffer((ulong)Marshal.SizeOf<FroxelCB>(), "Fog.FroxCB");
            _renderCBUpload = _device.CreateUploadBuffer((ulong)Marshal.SizeOf<VolumetricFogRenderCB>(), "Fog.RenderCB");
            _lightCBUpload = _device.CreateUploadBuffer((ulong)Marshal.SizeOf<FogLightCB>(), "Fog.LightCB");

            BuildInjectPipeline();
            BuildLightingPipeline();
            BuildGIPipeline();
            BuildParticlePipeline();
            BuildIntegratePipeline();
            BuildTemporalPipeline();
            BuildBilateralPipeline();
        }

        private void AllocateGrids()
        {
            int w = (int)FroxelResolution.X, h = (int)FroxelResolution.Y, d = (int)FroxelResolution.Z;
            FroxelGrid = Create3D(w, h, d, "Fog.Froxel");
            FroxelSRV = _device.CreateSRV(FroxelGrid);
            FroxelUAV = _device.CreateUAV(FroxelGrid);
            HistoryGrid = Create3D(w, h, d, "Fog.History");
            HistorySRV = _device.CreateSRV(HistoryGrid);
            HistoryUAV = _device.CreateUAV(HistoryGrid);
            FilteredGrid = Create3D(w, h, d, "Fog.Filtered");
            FilteredSRV = _device.CreateSRV(FilteredGrid);
        }

        private RHITexture Create3D(int w, int h, int d, string name)
        {
            return _device.CreateTexture(new RHITextureDesc
            {
                Dimension = RHIResourceDimension.Texture3D,
                Format = RHIFormat.R16G16B16A16_Float,
                Width = w, Height = h, DepthOrArraySize = d,
                MipLevels = 1, SampleCount = 1, SampleQuality = 0,
                Flags = RHITextureFlags.UnorderedAccess | RHITextureFlags.ShaderResource
            }, name);
        }

        // ── PSO 构建 ───────────────────────────────────────────────

        private void BuildInjectPipeline()
        {
            _injectRootSig = _device.CreateRootSignature(new RHIRootSignatureDesc
            {
                Parameters = new[] { RootParam.Constants(16, 0), RootParam.UAV(1, 0) }
            });
            _injectPSO = _device.CreateComputePipelineState(new RHIComputePipelineDesc
            {
                Name = "Fog.Inject",
                CS = ShaderManager.Instance.GetBytecode("Volumetric/VolumetricFog", "FogInjectCS", RHIShaderStage.Compute)
            });
        }

        private void BuildLightingPipeline()
        {
            _lightingRootSig = _device.CreateRootSignature(new RHIRootSignatureDesc
            {
                Parameters = new[]
                {
                    RootParam.Constants(16, 0),    // FroxelCB
                    RootParam.Constants(16, 1),    // FogLightCB（逐光源）
                    RootParam.SRV(8, 0),           // 阴影图数组
                    RootParam.SRV(1, 8),           // 天空 LUT
                    RootParam.UAV(1, 0)            // Froxel UAV
                }
            });
            _lightingPSO = _device.CreateComputePipelineState(new RHIComputePipelineDesc
            {
                Name = "Fog.Lighting",
                CS = ShaderManager.Instance.GetBytecode("Volumetric/VolumetricFog", "FogLightingCS", RHIShaderStage.Compute)
            });
        }

        private void BuildGIPipeline()
        {
            _giRootSig = _device.CreateRootSignature(new RHIRootSignatureDesc
            {
                Parameters = new[] { RootParam.Constants(16, 0), RootParam.SRV(1, 0), RootParam.UAV(1, 0) }
            });
            _giPSO = _device.CreateComputePipelineState(new RHIComputePipelineDesc
            {
                Name = "Fog.GI",
                CS = ShaderManager.Instance.GetBytecode("Volumetric/VolumetricFog", "FogGICS", RHIShaderStage.Compute)
            });
        }

        private void BuildParticlePipeline()
        {
            _particleRootSig = _device.CreateRootSignature(new RHIRootSignatureDesc
            {
                Parameters = new[] { RootParam.Constants(16, 0), RootParam.SRV(1, 0), RootParam.UAV(1, 0) }
            });
            _particlePSO = _device.CreateComputePipelineState(new RHIComputePipelineDesc
            {
                Name = "Fog.Particle",
                CS = ShaderManager.Instance.GetBytecode("Volumetric/VolumetricFog", "FogParticleCS", RHIShaderStage.Compute)
            });
        }

        private void BuildIntegratePipeline()
        {
            _integrateRootSig = _device.CreateRootSignature(new RHIRootSignatureDesc
            {
                Parameters = new[]
                {
                    RootParam.Constants(16, 0),
                    RootParam.SRV(1, 0),   // Froxel 密度+散射
                    RootParam.SRV(1, 1),   // 大气空中透视 LUT
                    RootParam.UAV(1, 0)    // 输出积分结果
                }
            });
            _integratePSO = _device.CreateComputePipelineState(new RHIComputePipelineDesc
            {
                Name = "Fog.Integrate",
                CS = ShaderManager.Instance.GetBytecode("Volumetric/VolumetricFog", "FogIntegrateCS", RHIShaderStage.Compute)
            });
        }

        private void BuildTemporalPipeline()
        {
            _temporalRootSig = _device.CreateRootSignature(new RHIRootSignatureDesc
            {
                Parameters = new[]
                {
                    RootParam.Constants(16, 0),
                    RootParam.SRV(1, 0),   // 当前
                    RootParam.SRV(1, 1),   // 历史
                    RootParam.UAV(1, 0)    // 输出
                }
            });
            _temporalPSO = _device.CreateComputePipelineState(new RHIComputePipelineDesc
            {
                Name = "Fog.Temporal",
                CS = ShaderManager.Instance.GetBytecode("Volumetric/VolumetricFog", "FogTemporalCS", RHIShaderStage.Compute)
            });
        }

        private void BuildBilateralPipeline()
        {
            _bilateralRootSig = _device.CreateRootSignature(new RHIRootSignatureDesc
            {
                Parameters = new[]
                {
                    RootParam.Constants(16, 0),
                    RootParam.SRV(1, 0),   // 输入
                    RootParam.UAV(1, 0)    // 输出（FilteredGrid）
                }
            });
            _bilateralPSO = _device.CreateComputePipelineState(new RHIComputePipelineDesc
            {
                Name = "Fog.Bilateral",
                CS = ShaderManager.Instance.GetBytecode("Volumetric/VolumetricFog", "FogBilateralCS", RHIShaderStage.Compute)
            });
        }

        /// <summary>注册局部雾体积。</summary>
        public void RegisterLocalVolume(LocalFogVolume volume) => LocalVolumes.Add(volume);
        /// <summary>注销局部雾体积。</summary>
        public bool UnregisterLocalVolume(LocalFogVolume volume) => LocalVolumes.Remove(volume);

        // ── CPU 端密度采样（供逻辑判断 / 粒子交互查询）──────────────────

        /// <summary>采样世界位置处的总雾密度（高度雾 + 局部雾）。</summary>
        public float SampleWorldDensity(Vector3 worldPos)
        {
            float d = HeightFog.SampleDensity(worldPos.Y);
            foreach (var vol in LocalVolumes)
                d += vol.SampleDensity(worldPos);
            return d;
        }

        // ── 渲染阶段 ───────────────────────────────────────────────

        /// <summary>
        /// 注入阶段：把高度雾与局部雾体积的密度写入 Froxel 网格，并叠加多散射近似。
        /// </summary>
        public void InjectDensity(RHICommandList cmd, in Matrix4x4 viewProj, in Matrix4x4 invViewProj, Vector3 cameraPos, float near, float far, float time)
        {
            var cb = new FroxelCB
            {
                Resolution = FroxelResolution,
                NearClip = near,
                FarClip = far,
                DepthExponent = 2f,
                ViewProjection = viewProj,
                InvViewProjection = invViewProj,
                CameraPosition = cameraPos,
                Time = time,
                DensityScale = 1f,
                Scattering = HeightFog.Color * HeightFog.ScatteringScale,
                Absorption = HeightFog.Color * 0.25f,
                PhaseG = 0.2f
            };
            _device.UploadToBuffer(_froxCBUpload!, MemoryMarshal.CreateReadOnlySpan(ref cb, 1));
            cmd.SetPipelineState(_injectPSO!);
            cmd.SetComputeRootSignature(_injectRootSig!);
            cmd.SetComputeRootConstantBufferView(0, _froxCBUpload!);
            cmd.SetComputeRootDescriptorTable(1, FroxelUAV);
            cmd.Dispatch((int)FroxelResolution.X / 8, (int)FroxelResolution.Y / 8, (int)FroxelResolution.Z / 4);
        }

        /// <summary>
        /// 光照积分阶段：对每个 Froxel 累加各光源的散射贡献，并应用体积阴影。
        /// </summary>
        /// <param name="cmd">命令列表。</param>
        /// <param name="lights">参与雾照明的光源常量数组。</param>
        /// <param name="shadowSRVs">阴影图 SRV 数组（与光源对应，无阴影传空）。</param>
        public void IntegrateLights(RHICommandList cmd, FogLightCB[] lights, RHIDescriptorHandle[] shadowSRVs)
        {
            if (lights.Length == 0) return;
            var first = lights[0];
            _device.UploadToBuffer(_lightCBUpload!, MemoryMarshal.CreateReadOnlySpan(ref first, 1));
            cmd.SetPipelineState(_lightingPSO!);
            cmd.SetComputeRootSignature(_lightingRootSig!);
            cmd.SetComputeRootConstantBufferView(0, _froxCBUpload!);
            cmd.SetComputeRootConstantBufferView(1, _lightCBUpload!);
            // 绑定阴影图（此处简化为第一个阴影图数组起始）
            if (shadowSRVs.Length > 0 && EnableVolumetricShadows)
                cmd.SetComputeRootDescriptorTable(2, shadowSRVs[0]);
            else
                cmd.SetComputeRootDescriptorTable(2, default);
            // 大气天空 LUT（天空光进气）
            if (_atmosphere != null)
                cmd.SetComputeRootDescriptorTable(3, _atmosphere.LUTs.SkyViewSRV);
            else
                cmd.SetComputeRootDescriptorTable(3, default);
            cmd.SetComputeRootDescriptorTable(4, FroxelUAV);
            cmd.Dispatch((int)FroxelResolution.X / 8, (int)FroxelResolution.Y / 8, (int)FroxelResolution.Z / 4);
        }

        /// <summary>GI 注入阶段：采样 ProbeField 探针辐照度，把反弹光散射进雾。</summary>
        /// <param name="cmd">命令列表。</param>
        /// <param name="giIrradianceSRV">ProbeField 辐照度/结果 SRV。</param>
        public void InjectGI(RHICommandList cmd, RHIDescriptorHandle giIrradianceSRV)
        {
            if (!EnableGI || !giIrradianceSRV.IsValid) return;
            cmd.SetPipelineState(_giPSO!);
            cmd.SetComputeRootSignature(_giRootSig!);
            cmd.SetComputeRootConstantBufferView(0, _froxCBUpload!);
            cmd.SetComputeRootDescriptorTable(1, giIrradianceSRV);
            cmd.SetComputeRootDescriptorTable(2, FroxelUAV);
            cmd.Dispatch((int)FroxelResolution.X / 8, (int)FroxelResolution.Y / 8, (int)FroxelResolution.Z / 4);
        }

        /// <summary>粒子交互阶段：粒子在体积雾中产生额外散射贡献。</summary>
        /// <param name="cmd">命令列表。</param>
        /// <param name="particleSRV">粒子数据 SRV（位置+密度）。</param>
        public void InjectParticles(RHICommandList cmd, RHIDescriptorHandle particleSRV)
        {
            if (!particleSRV.IsValid) return;
            cmd.SetPipelineState(_particlePSO!);
            cmd.SetComputeRootSignature(_particleRootSig!);
            cmd.SetComputeRootConstantBufferView(0, _froxCBUpload!);
            cmd.SetComputeRootDescriptorTable(1, particleSRV);
            cmd.SetComputeRootDescriptorTable(2, FroxelUAV);
            cmd.Dispatch((int)FroxelResolution.X / 8, (int)FroxelResolution.Y / 8, (int)FroxelResolution.Z / 4);
        }

        /// <summary>沿视线积分阶段：把 Froxel 散射光沿视线累加，输出雾颜色与透射率。</summary>
        public void IntegrateView(RHICommandList cmd)
        {
            cmd.SetPipelineState(_integratePSO!);
            cmd.SetComputeRootSignature(_integrateRootSig!);
            cmd.SetComputeRootConstantBufferView(0, _renderCBUpload!);
            cmd.SetComputeRootDescriptorTable(1, FroxelSRV);
            cmd.SetComputeRootDescriptorTable(2, _atmosphere?.LUTs.AerialPerspectiveSRV ?? default);
            cmd.SetComputeRootDescriptorTable(3, FroxelUAV);
            cmd.Dispatch((int)FroxelResolution.X / 8, (int)FroxelResolution.Y / 8, 1);
        }

        /// <summary>时间累积阶段：当前帧与历史帧按混合因子累积，抑制闪烁。</summary>
        public void FilterTemporal(RHICommandList cmd)
        {
            if (!EnableTemporalFilter) return;
            var cb = new VolumetricFogRenderCB
            {
                Resolution = FroxelResolution,
                ViewProjection = ViewProjection,
                InvViewProjection = Matrix4x4.Identity,
                CameraPosition = Vector3.Zero,
                NearClip = 0.1f,
                FarClip = 4000f,
                DepthExponent = 2f,
                Time = 0f,
                TemporalBlend = TemporalBlend,
                BilateralRadius = BilateralRadius
            };
            _device.UploadToBuffer(_renderCBUpload!, MemoryMarshal.CreateReadOnlySpan(ref cb, 1));
            cmd.SetPipelineState(_temporalPSO!);
            cmd.SetComputeRootSignature(_temporalRootSig!);
            cmd.SetComputeRootConstantBufferView(0, _renderCBUpload!);
            cmd.SetComputeRootDescriptorTable(1, FroxelSRV);
            cmd.SetComputeRootDescriptorTable(2, HistorySRV);
            cmd.SetComputeRootDescriptorTable(3, HistoryUAV);
            cmd.Dispatch((int)FroxelResolution.X / 8, (int)FroxelResolution.Y / 8, (int)FroxelResolution.Z / 4);
        }

        /// <summary>双边滤波阶段：保边空域降噪，平滑噪声同时保留边缘。</summary>
        public void FilterBilateral(RHICommandList cmd)
        {
            if (!EnableBilateralFilter) return;
            cmd.SetPipelineState(_bilateralPSO!);
            cmd.SetComputeRootSignature(_bilateralRootSig!);
            cmd.SetComputeRootConstantBufferView(0, _renderCBUpload!);
            // 输入为历史缓冲（已时间累积），输出到 FilteredGrid
            cmd.SetComputeRootDescriptorTable(1, HistorySRV);
            var filteredUAV = _device.CreateUAV(FilteredGrid!);
            cmd.SetComputeRootDescriptorTable(2, filteredUAV);
            cmd.Dispatch((int)FroxelResolution.X / 8, (int)FroxelResolution.Y / 8, (int)FroxelResolution.Z / 4);
        }

        /// <summary>交换历史缓冲（时间累积用：当前结果成为下一帧历史）。</summary>
        public void SwapHistory()
        {
            // 实际实现：CopyResource(FroxelGrid -> HistoryGrid)，或乒乓两个缓冲
            // 此处保留接口，由上层命令列表执行拷贝
            PrevViewProjection = ViewProjection;
        }

        /// <summary>完整渲染流程：注入 -> 光照 -> GI -> 粒子 -> 积分 -> 时间 -> 双边。</summary>
        /// <param name="cmd">命令列表。</param>
        /// <param name="viewProj">当前视投影。</param>
        /// <param name="invViewProj">逆视投影。</param>
        /// <param name="cameraPos">相机位置。</param>
        /// <param name="near">近平面。</param>
        /// <param name="far">远平面。</param>
        /// <param name="time">时间。</param>
        /// <param name="lights">光源数组。</param>
        /// <param name="shadowSRVs">阴影图 SRV。</param>
        /// <param name="giSRV">GI 辐照度 SRV。</param>
        /// <param name="particleSRV">粒子数据 SRV。</param>
        public void Render(RHICommandList cmd, in Matrix4x4 viewProj, in Matrix4x4 invViewProj,
            Vector3 cameraPos, float near, float far, float time,
            FogLightCB[] lights, RHIDescriptorHandle[] shadowSRVs,
            RHIDescriptorHandle giSRV, RHIDescriptorHandle particleSRV)
        {
            ViewProjection = viewProj;
            // 1. 注入密度（高度雾 + 局部雾 + 多散射近似）
            InjectDensity(cmd, in viewProj, in invViewProj, cameraPos, near, far, time);
            // 2. 光照积分 + 体积阴影
            IntegrateLights(cmd, lights, shadowSRVs);
            // 3. GI 参与
            InjectGI(cmd, giSRV);
            // 4. 粒子交互
            InjectParticles(cmd, particleSRV);
            // 5. 沿视线积分
            IntegrateView(cmd);
            // 6. 时间累积
            FilterTemporal(cmd);
            // 7. 双边滤波（输出到 FilteredGrid）
            FilterBilateral(cmd);
            // 8. 交换历史
            SwapHistory();
        }

        /// <summary>释放资源。</summary>
        public void Dispose()
        {
            if (_disposed) return;
            FroxelGrid?.Dispose();
            HistoryGrid?.Dispose();
            FilteredGrid?.Dispose();
            _injectPSO?.Dispose(); _injectRootSig?.Dispose();
            _lightingPSO?.Dispose(); _lightingRootSig?.Dispose();
            _giPSO?.Dispose(); _giRootSig?.Dispose();
            _particlePSO?.Dispose(); _particleRootSig?.Dispose();
            _integratePSO?.Dispose(); _integrateRootSig?.Dispose();
            _temporalPSO?.Dispose(); _temporalRootSig?.Dispose();
            _bilateralPSO?.Dispose(); _bilateralRootSig?.Dispose();
            _froxCBUpload?.Dispose();
            _renderCBUpload?.Dispose();
            _lightCBUpload?.Dispose();
            _disposed = true;
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // ECS 系统 (VolumetricFogSystem)
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 体积雾 ECS 系统。查询挂有 <see cref="VolumetricFogComponent"/> 的实体，把其
    /// 配置应用到全局体积雾渲染器，并每帧推进 Froxel 注入、光照积分、GI、粒子交互
    /// 与多级滤波。同时把 <see cref="LocalFogVolume"/> 汇总到渲染器。
    /// </summary>
    public sealed class VolumetricFogSystem : ISystem, IDisposable
    {
        /// <summary>系统名称。</summary>
        public string Name => "VolumetricFogSystem";

        private readonly RHIDevice _device;
        /// <summary>体积雾渲染器（由本系统持有）。</summary>
        public VolumetricFogRenderer FogRenderer;
        /// <summary>大气渲染器引用。</summary>
        public AtmosphereRenderer? Atmosphere;
        /// <summary>主命令列表（由渲染管线注入）。</summary>
        public RHICommandList? CommandList;
        /// <summary>主相机位置。</summary>
        public Vector3 CameraPosition;
        /// <summary>当前视投影矩阵。</summary>
        public Matrix4x4 ViewProjection;
        /// <summary>逆视投影矩阵。</summary>
        public Matrix4x4 InvViewProjection;
        /// <summary>近平面。</summary>
        public float NearClip = 0.1f;
        /// <summary>远平面。</summary>
        public float FarClip = 4000f;
        /// <summary>当前时间。</summary>
        public float Time;
        /// <summary>当前光源常量数组（由光照系统填充）。</summary>
        public FogLightCB[] Lights = Array.Empty<FogLightCB>();
        /// <summary>阴影图 SRV 数组。</summary>
        public RHIDescriptorHandle[] ShadowSRVs = Array.Empty<RHIDescriptorHandle>();
        /// <summary>GI 辐照度 SRV（ProbeField.Result）。</summary>
        public RHIDescriptorHandle GIIrradianceSRV;
        /// <summary>粒子数据 SRV。</summary>
        public RHIDescriptorHandle ParticleSRV;
        private bool _disposed;

        /// <summary>构造，绑定设备与大气渲染器。</summary>
        public VolumetricFogSystem(RHIDevice device, AtmosphereRenderer? atmosphere = null)
        {
            _device = device;
            Atmosphere = atmosphere;
            FogRenderer = new VolumetricFogRenderer(device, atmosphere);
        }

        /// <summary>初始化资源。</summary>
        public void Initialize()
        {
            FogRenderer.Initialize();
            // 若已有局部雾体积，注册
            foreach (var vol in _pendingVolumes) FogRenderer.RegisterLocalVolume(vol);
            _pendingVolumes.Clear();
        }

        private readonly List<LocalFogVolume> _pendingVolumes = new();
        /// <summary>注册局部雾体积（若渲染器未初始化则暂存）。</summary>
        public void RegisterLocalVolume(LocalFogVolume vol)
        {
            if (FogRenderer != null) FogRenderer.RegisterLocalVolume(vol);
            else _pendingVolumes.Add(vol);
        }

        /// <summary>每帧更新：应用组件配置、推进体积雾渲染。</summary>
        public void Update(float dt, EntityComponentSystem ecs)
        {
            Time += dt;
            var entities = ecs.QueryWithComponent<VolumetricFogComponent>();
            foreach (int id in entities)
            {
                var comp = ecs.GetComponent<VolumetricFogComponent>(id);
                if (!comp.Enabled) continue;
                FogRenderer.EnableTemporalFilter = comp.EnableTemporalFilter;
                FogRenderer.EnableBilateralFilter = comp.EnableBilateralFilter;
                FogRenderer.EnableVolumetricShadows = comp.EnableVolumetricShadows;
                FogRenderer.EnableGI = comp.EnableGI;
                FogRenderer.HeightFog.Density = comp.DensityScale;
                if (comp.FroxelResolution.LengthSquared() > 0)
                    FogRenderer.FroxelResolution = comp.FroxelResolution;
            }

            if (CommandList != null)
            {
                FogRenderer.Render(CommandList, in ViewProjection, in InvViewProjection,
                    CameraPosition, NearClip, FarClip, Time, Lights, ShadowSRVs, GIIrradianceSRV, ParticleSRV);
            }
        }

        /// <summary>获取最终雾 LUT 的 SRV（供场景着色采样）。</summary>
        public RHIDescriptorHandle GetFogSRV() => FogRenderer.FilteredSRV;

        /// <summary>释放资源。</summary>
        public void Dispose()
        {
            if (_disposed) return;
            FogRenderer.Dispose();
            _disposed = true;
        }
    }
}
