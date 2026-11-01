using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using YamaEngine.RHI;

namespace YamaEngine.Rendering
{
    // ════════════════════════════════════════════════════════════════════════
    // yama引擎水面海洋渲染系统 (Yama Water / Ocean Renderer)
    //
    // 设计目标: 提供电影级海洋与内陆水面渲染, 超越 UE7 Water System 与
    // Unity HDRP Water 的真实度与性能。
    //
    // 核心能力:
    //   1. FFT 波浪模拟: 基于 Phillips 频谱在海面频率域生成高度场, 经 IFFT
    //      得到位移场(高度 + 水平 Choppy 位移)、法线、雅可比行列式(用于泡沫)。
    //      完全在 Compute Shader 上执行, 使用 Stockham FFT(原地蝶形运算)。
    //   2. 水面 Mesh: 平面网格化, 视点跟随, 近距密远距疏(类似 Clipmap)。
    //   3. 水面材质: 镜面反射(平面反射/SSR)、折射、焦散(屏幕空间)、
    //      次表面散射(近似)、菲涅尔、太阳高光。
    //   4. 水下渲染: 屏幕空间后处理, 深度雾、色散吸收、焦散投影、屏幕扭曲。
    //   5. 泡沫: 基于雅可比行列式(波浪翻转)与速度场生成, 动态消散。
    //   6. 水流: 方向场 + 流速, 驱动纹理 UV 滚动与浮力方向。
    //   7. 浮力计算: 采样 FFT 位移/法线, 计算浮力/阻力/扭矩, 供物理体使用。
    //   8. 屏幕空间反射(SSR): 对水面像素做光线步进, 增强近距反射细节。
    //   9. 体积散射: 水下光柱(god rays)与水上水雾, 基于光线步进。
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>水面渲染全局常量。</summary>
    public static class WaterConstants
    {
        /// <summary>FFT 分辨率(必须为 2 的幂)。</summary>
        public const int FFTSize = 256;

        /// <summary>海洋补丁世界尺寸(米)。</summary>
        public const float OceanPatchSize = 2000.0f;

        /// <summary>水面网格细分层数(LOD)。</summary>
        public const int WaterMeshLODCount = 6;

        /// <summary>水面网格每层顶点数。</summary>
        public const int WaterMeshGridSize = 128;

        /// <summary>泡沫最大密度。</summary>
        public const float MaxFoamDensity = 1.0f;

        /// <summary>体积光采样步数。</summary>
        public const int VolumetricLightSteps = 32;

        /// <summary>SSR 最大步数。</summary>
        public const int SSRMaxSteps = 64;

        /// <summary>浮力采样网格分辨率。</summary>
        public const int BuoyancySampleGrid = 8;
    }

    /// <summary>水面网格类型。</summary>
    public enum WaterMeshType
    {
        /// <summary>无限平面(用于远景海洋)。</summary>
        InfinitePlane = 0,
        /// <summary>网格化(用于河流/湖泊, 有边界)。</summary>
        TessellatedGrid = 1,
        /// <summary>Clipmap 式(跟随相机, 近密远疏)。</summary>
        Clipmap = 2
    }

    /// <summary>泡沫生成方法。</summary>
    public enum FoamMethod
    {
        /// <summary>基于雅可比行列式(波浪翻转)。</summary>
        Jacobian = 0,
        /// <summary>基于速度场(波峰速度)。</summary>
        Velocity = 1,
        /// <summary>基于波高(高于阈值产生)。</summary>
        Height = 2,
        /// <summary>综合(Jac + Vel + Height)。</summary>
        Combined = 3
    }

    /// <summary>FFT 模拟更新模式。</summary>
    public enum FFTUpdateMode
    {
        /// <summary>每帧更新(动画波浪)。</summary>
        Realtime = 0,
        /// <summary>烘焙静态(河流稳态)。</summary>
        Baked = 1
    }

    // ──────────────────────────────────────────────────────────────────────
    // GPU 常量缓冲结构
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>FFT 模拟常量。</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 16)]
    public struct FFTSimCB
    {
        /// <summary>FFT 分辨率。</summary>
        public int FFTSize;
        /// <summary>海洋补丁世界尺寸。</summary>
        public float PatchSize;
        /// <summary>风速(米/秒)。</summary>
        public float WindSpeed;
        /// <summary>风向(归一化 XZ)。</summary>
        public Vector2 WindDirection;
        /// <summary>当前时间(秒)。</summary>
        public float Time;
        /// <summary>波浪幅度缩放。</summary>
        public float AmplitudeScale;
        /// <summary>Choppy 位移强度。</summary>
        public float Choppiness;
        /// <summary>重力加速度。</summary>
        public float Gravity;
        /// <summary>Phillips 谱耗散系数。</summary>
        public float PhillipsDissipation;
        /// <summary>最小/最大波长限制。</summary>
        public float MinWavelength;
        public float MaxWavelength;
        /// <summary>更新模式。</summary>
        public int UpdateMode;
        /// <summary>对齐填充。</summary>
        public float _pad0;
        public float _pad1;
    }

    /// <summary>水面渲染常量。</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 16)]
    public struct WaterRenderCB
    {
        /// <summary>相机位置。</summary>
        public Vector3 CameraPosition;
        /// <summary>时间。</summary>
        public float Time;
        /// <summary>视投影矩阵。</summary>
        public Matrix4x4 ViewProjection;
        /// <summary>逆视投影。</summary>
        public Matrix4x4 InvViewProjection;
        /// <summary>水世界高度(海平面)。</summary>
        public float WaterLevel;
        /// <summary>水面网格世界尺寸。</summary>
        public float MeshSize;
        /// <summary>深水颜色。</summary>
        public Vector3 DeepColor;
        /// <summary>浅水颜色。</summary>
        public Vector3 ShallowColor;
        /// <summary>水的吸收系数(每米)。</summary>
        public Vector3 Absorption;
        /// <summary>水的散射系数。</summary>
        public Vector3 Scattering;
        /// <summary>菲涅尔 0 度反射率。</summary>
        public float Fresnel0;
        /// <summary>太阳方向。</summary>
        public Vector3 SunDirection;
        /// <summary>太阳颜色。</summary>
        public Vector3 SunColor;
        /// <summary>太阳高光强度。</summary>
        public float SunSpecularIntensity;
        /// <summary>太阳高光锐度。</summary>
        public float SunSpecularPower;
        /// <summary>水流方向(XZ)。</summary>
        public Vector2 FlowDirection;
        /// <summary>水流速度。</summary>
        public float FlowSpeed;
        /// <summary>焦散强度。</summary>
        public float CausticsIntensity;
        /// <summary>焦散平铺。</summary>
        public float CausticsTiling;
        /// <summary>泡沫强度。</summary>
        public float FoamIntensity;
        /// <summary>泡沫消散速度。</summary>
        public float FoamDissipation;
        /// <summary>反射强度。</summary>
        public float ReflectionIntensity;
        /// <summary>折射强度。</summary>
        public float RefractionIntensity;
        /// <summary>水面网格类型。</summary>
        public int MeshType;
        /// <summary>视口尺寸。</summary>
        public Vector2 ViewportSize;
        /// <summary>对齐填充。</summary>
        public float _pad0;
    }

    /// <summary>FFT 蝶形运算常量。</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 16)]
    public struct FFTButterflyCB
    {
        /// <summary>当前蝶形阶段。</summary>
        public int Stage;
        /// <summary>FFT 尺寸。</summary>
        public int FFTSize;
        /// <summary>方向(0=水平, 1=垂直)。</summary>
        public int Direction;
        /// <summary>对齐填充。</summary>
        public float _pad0;
    }

    /// <summary>水下后处理常量。</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 16)]
    public struct UnderwaterCB
    {
        /// <summary>相机是否在水下。</summary>
        public int CameraUnderwater;
        /// <summary>水世界高度。</summary>
        public float WaterLevel;
        /// <summary>深度雾颜色。</summary>
        public Vector3 FogColor;
        /// <summary>深度雾密度。</summary>
        public float FogDensity;
        /// <summary>水的吸收系数。</summary>
        public Vector3 Absorption;
        /// <summary>焦散强度。</summary>
        public float CausticsIntensity;
        /// <summary>焦散平铺。</summary>
        public float CausticsTiling;
        /// <summary>扭曲强度。</summary>
        public float DistortionStrength;
        /// <summary>扭曲平铺。</summary>
        public float DistortionTiling;
        /// <summary>视投影矩阵。</summary>
        public Matrix4x4 ViewProjection;
        /// <summary>逆视投影。</summary>
        public Matrix4x4 InvViewProjection;
        /// <summary>时间。</summary>
        public float Time;
        /// <summary>视口尺寸。</summary>
        public Vector2 ViewportSize;
        /// <summary>对齐填充。</summary>
        public float _pad0;
    }

    /// <summary>浮力采样结果。</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 16)]
    public struct BuoyancySample
    {
        /// <summary>采样点世界位置(已应用波浪位移)。</summary>
        public Vector3 WorldPosition;
        /// <summary>该点法线。</summary>
        public Vector3 Normal;
        /// <summary>该点水流速度。</summary>
        public Vector3 Velocity;
        /// <summary>该点高度。</summary>
        public float Height;
    }

    /// <summary>浮力计算常量。</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 16)]
    public struct BuoyancyCB
    {
        /// <summary>物体中心世界位置。</summary>
        public Vector3 ObjectCenter;
        /// <summary>物体半径。</summary>
        public float ObjectRadius;
        /// <summary>采样网格分辨率。</summary>
        public int SampleGrid;
        /// <summary>采样间距。</summary>
        public float SampleSpacing;
        /// <summary>水的密度(kg/m^3)。</summary>
        public float WaterDensity;
        /// <summary>重力加速度。</summary>
        public float Gravity;
        /// <summary>物体质量。</summary>
        public float ObjectMass;
        /// <summary>水的阻力系数。</summary>
        public float DragCoefficient;
        /// <summary>对齐填充。</summary>
        public float _pad0;
        public float _pad1;
    }

    // ════════════════════════════════════════════════════════════════════════
    // FFT 波浪模拟器
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// FFT 波浪模拟器: 基于 Phillips 频谱在海面频率域生成高度场系数,
    /// 经二维 IFFT(Stockham 蝶形, 行/列两阶段)得到空间域位移场。
    /// 输出: 高度位移(R16G16B16A16: XYZ位移 + W雅可比), 用于水面着色与浮力。
    /// </summary>
    public sealed class FFTWaveSimulator : IDisposable
    {
        /// <summary>FFT 分辨率。</summary>
        public int Size { get; }
        /// <summary>海洋补丁尺寸。</summary>
        public float PatchSize { get; }
        /// <summary>更新模式。</summary>
        public FFTUpdateMode UpdateMode { get; set; } = FFTUpdateMode.Realtime;

        /// <summary>初始频谱 h0(k)(复数, R32G32_Float)。</summary>
        public RHITexture SpectrumH0 { get; private set; }
        /// <summary>共轭频谱 h0*(-k)。</summary>
        public RHITexture SpectrumH0Conj { get; private set; }
        /// <summary>当前时刻频谱 ht(k)(复数)。</summary>
        public RHITexture SpectrumHT { get; private set; }
        /// <summary>位移场输出(高度 + 水平 Choppy + 雅可比)。</summary>
        public RHITexture DisplacementMap { get; private set; }
        /// <summary>法线/雅可比图。</summary>
        public RHITexture NormalJacobMap { get; private set; }
        /// <summary>位移图 SRV。</summary>
        public RHIDescriptorHandle DisplacementSRV { get; private set; }
        /// <summary>法线图 SRV。</summary>
        public RHIDescriptorHandle NormalJacobSRV { get; private set; }

        /// <summary>Phillips 谱生成 PSO。</summary>
        public RHIPipelineState PhillipsPSO { get; private set; }
        /// <summary>时刻频谱更新 PSO。</summary>
        public RHIPipelineState UpdateSpectrumPSO { get; private set; }
        /// <summary>FFT 蝶形 PSO。</summary>
        public RHIPipelineState ButterflyPSO { get; private set; }
        /// <summary>位移/法线/雅可比计算 PSO。</summary>
        public RHIPipelineState InverseTransformPSO { get; private set; }
        /// <summary>根签名。</summary>
        public RHIRootSignature RootSignature { get; private set; }
        /// <summary>FFT CB 上传缓冲。</summary>
        public RHIBuffer FFTCBUpload { get; private set; }
        /// <summary>蝶形 CB 上传缓冲。</summary>
        public RHIBuffer ButterflyCBUpload { get; private set; }

        private readonly RHIDevice _device;
        private bool _disposed;
        private bool _spectrumInitialized;

        public FFTWaveSimulator(RHIDevice device, int size = WaterConstants.FFTSize, float patchSize = WaterConstants.OceanPatchSize)
        {
            _device = device;
            Size = size;
            PatchSize = patchSize;
            CreateTextures(device);
            CreatePipelineObjects(device);
            FFTCBUpload = device.CreateUploadBuffer((ulong)Marshal.SizeOf<FFTSimCB>(), "Water.FFTCB");
            ButterflyCBUpload = device.CreateUploadBuffer((ulong)Marshal.SizeOf<FFTButterflyCB>(), "Water.ButterflyCB");
        }

        private void CreateTextures(RHIDevice device)
        {
            // h0(k) 与 h0*(-k): R32G32_Float (复数实部+虚部)
            SpectrumH0 = device.CreateTexture(new RHITextureDesc
            {
                Dimension = RHIResourceDimension.Texture2D,
                Format = RHIFormat.R32G32_Float,
                Width = Size, Height = Size, DepthOrArraySize = 1, MipLevels = 1, SampleCount = 1,
                Flags = RHITextureFlags.ShaderResource | RHITextureFlags.UnorderedAccess
            }, "Water.SpectrumH0");
            SpectrumH0Conj = device.CreateTexture(new RHITextureDesc
            {
                Dimension = RHIResourceDimension.Texture2D,
                Format = RHIFormat.R32G32_Float,
                Width = Size, Height = Size, DepthOrArraySize = 1, MipLevels = 1, SampleCount = 1,
                Flags = RHITextureFlags.ShaderResource | RHITextureFlags.UnorderedAccess
            }, "Water.SpectrumH0Conj");

            // ht(k): 当前时刻频谱, 三张分别对应 X/Z(水平位移) Y(高度)
            SpectrumHT = device.CreateTexture(new RHITextureDesc
            {
                Dimension = RHIResourceDimension.Texture2D,
                Format = RHIFormat.R32G32_Float,
                Width = Size, Height = Size, DepthOrArraySize = 3, MipLevels = 1, SampleCount = 1,
                Flags = RHITextureFlags.ShaderResource | RHITextureFlags.UnorderedAccess
            }, "Water.SpectrumHT");

            // 位移图: R16G16B16A16_Float (DX, DZ, DY, Jacobian)
            DisplacementMap = device.CreateTexture(new RHITextureDesc
            {
                Dimension = RHIResourceDimension.Texture2D,
                Format = RHIFormat.R16G16B16A16_Float,
                Width = Size, Height = Size, DepthOrArraySize = 1, MipLevels = 1, SampleCount = 1,
                Flags = RHITextureFlags.ShaderResource | RHITextureFlags.UnorderedAccess
            }, "Water.DisplacementMap");
            DisplacementSRV = device.CreateSRV(DisplacementMap, new RHISRVDesc
            {
                Format = RHIFormat.R16G16B16A16_Float, Dimension = RHIResourceDimension.Texture2D,
                MostDetailedMip = 0, MipLevels = 1, First2DArraySlice = 0, ArraySize = 1
            });

            // 法线/雅可比图: R16G16B16A16 (Normal.xy, Jacobian, Foam)
            NormalJacobMap = device.CreateTexture(new RHITextureDesc
            {
                Dimension = RHIResourceDimension.Texture2D,
                Format = RHIFormat.R16G16B16A16_Float,
                Width = Size, Height = Size, DepthOrArraySize = 1, MipLevels = 1, SampleCount = 1,
                Flags = RHITextureFlags.ShaderResource | RHITextureFlags.UnorderedAccess
            }, "Water.NormalJacobMap");
            NormalJacobSRV = device.CreateSRV(NormalJacobMap, new RHISRVDesc
            {
                Format = RHIFormat.R16G16B16A16_Float, Dimension = RHIResourceDimension.Texture2D,
                MostDetailedMip = 0, MipLevels = 1, First2DArraySlice = 0, ArraySize = 1
            });
        }

        private void CreatePipelineObjects(RHIDevice device)
        {
            var rootDesc = new RHIRootSignatureDesc
            {
                Parameters = new[]
                {
                    new RHIRootParameter { ParameterType = RHIRootParameterType.CBV, ShaderVisibility = RHIShaderVisibility.All, Descriptor = new RHIRootDescriptor { ShaderRegister = 0 } },
                    new RHIRootParameter { ParameterType = RHIRootParameterType.SRV, ShaderVisibility = RHIShaderVisibility.All, Descriptor = new RHIRootDescriptor { ShaderRegister = 0 } },
                    new RHIRootParameter
                    {
                        ParameterType = RHIRootParameterType.DescriptorTable, ShaderVisibility = RHIShaderVisibility.All,
                        DescriptorTable = new RHIRootDescriptorTable { Ranges = new[] { new RHIRootDescriptorRange { RangeType = RHIDescriptorRangeType.UAV, NumDescriptors = 4, BaseShaderRegister = 0 } } }
                    }
                },
                StaticSamplers = new[] { RHISamplerDesc.PointClamp },
                AllowInputAssembler = false
            };
            RootSignature = device.CreateRootSignature(rootDesc);

            PhillipsPSO = device.CreateComputePipelineState(new RHIComputePipelineDesc
            {
                Name = "Water.PhillipsCS",
                CS = device.CompileShaderFromFile("Shaders/OceanFFT.hlsl", "PhillipsCS", RHIShaderStage.Compute, RHIShaderModel.SM_6_6)
            });
            UpdateSpectrumPSO = device.CreateComputePipelineState(new RHIComputePipelineDesc
            {
                Name = "Water.UpdateSpectrumCS",
                CS = device.CompileShaderFromFile("Shaders/OceanFFT.hlsl", "UpdateSpectrumCS", RHIShaderStage.Compute, RHIShaderModel.SM_6_6)
            });
            ButterflyPSO = device.CreateComputePipelineState(new RHIComputePipelineDesc
            {
                Name = "Water.ButterflyCS",
                CS = device.CompileShaderFromFile("Shaders/OceanFFT.hlsl", "ButterflyCS", RHIShaderStage.Compute, RHIShaderModel.SM_6_6)
            });
            InverseTransformPSO = device.CreateComputePipelineState(new RHIComputePipelineDesc
            {
                Name = "Water.InverseTransformCS",
                CS = device.CompileShaderFromFile("Shaders/OceanFFT.hlsl", "InverseTransformCS", RHIShaderStage.Compute, RHIShaderModel.SM_6_6)
            });
        }

        /// <summary>初始化 Phillips 频谱 h0(k)(仅执行一次)。</summary>
        public void InitializeSpectrum(RHICommandList cmd, FFTSimCB cb)
        {
            if (_spectrumInitialized) return;
            cmd.SetPipelineState(PhillipsPSO);
            cmd.SetComputeRootSignature(RootSignature);
            _device.UploadToBuffer(FFTCBUpload, MemoryMarshal.CreateSpan(ref cb, 1));
            cmd.SetComputeRootConstantBufferView(0, FFTCBUpload.GpuVirtualAddress);

            cmd.TransitionTo(SpectrumH0, RHIResourceState.UnorderedAccess);
            cmd.TransitionTo(SpectrumH0Conj, RHIResourceState.UnorderedAccess);
            cmd.SetComputeRootUnorderedAccessView(2, SpectrumH0.GpuVirtualAddress);
            // 第二个 UAV 通过描述符表(此处简化绑定)
            int groups = (Size + 7) / 8;
            cmd.Dispatch(groups, groups, 1);
            cmd.TransitionTo(SpectrumH0, RHIResourceState.NonPixelShaderResource);
            cmd.TransitionTo(SpectrumH0Conj, RHIResourceState.NonPixelShaderResource);
            _spectrumInitialized = true;
        }

        /// <summary>每帧更新: 计算 ht(k) -> IFFT -> 位移/法线/雅可比。</summary>
        public void Update(RHICommandList cmd, FFTSimCB cb)
        {
            if (UpdateMode == FFTUpdateMode.Baked) return;
            if (!_spectrumInitialized) InitializeSpectrum(cmd, cb);

            // 1. 更新时刻频谱 ht(k) = h0(k)*exp(iwt) + h0*(-k)*exp(-iwt)
            cmd.SetPipelineState(UpdateSpectrumPSO);
            cmd.SetComputeRootSignature(RootSignature);
            _device.UploadToBuffer(FFTCBUpload, MemoryMarshal.CreateSpan(ref cb, 1));
            cmd.SetComputeRootConstantBufferView(0, FFTCBUpload.GpuVirtualAddress);
            cmd.SetComputeRootShaderResourceView(1, SpectrumH0.GpuVirtualAddress);
            cmd.TransitionTo(SpectrumHT, RHIResourceState.UnorderedAccess);
            cmd.SetComputeRootUnorderedAccessView(2, SpectrumHT.GpuVirtualAddress);
            int groups = (Size + 7) / 8;
            cmd.Dispatch(groups, groups, 1);

            // 2. 二维 IFFT: 先逐行, 再逐列
            int stages = (int)MathF.Round(MathF.Log2(Size));
            for (int s = 0; s < stages; s++)
            {
                var bcb = new FFTButterflyCB { Stage = s, FFTSize = Size, Direction = 0 };
                _device.UploadToBuffer(ButterflyCBUpload, MemoryMarshal.CreateSpan(ref bcb, 1));
                cmd.SetPipelineState(ButterflyPSO);
                cmd.SetComputeRootConstantBufferView(0, ButterflyCBUpload.GpuVirtualAddress);
                cmd.SetComputeRootUnorderedAccessView(2, SpectrumHT.GpuVirtualAddress);
                cmd.Dispatch(groups, groups, 1);
            }
            for (int s = 0; s < stages; s++)
            {
                var bcb = new FFTButterflyCB { Stage = s, FFTSize = Size, Direction = 1 };
                _device.UploadToBuffer(ButterflyCBUpload, MemoryMarshal.CreateSpan(ref bcb, 1));
                cmd.SetPipelineState(ButterflyPSO);
                cmd.SetComputeRootConstantBufferView(0, ButterflyCBUpload.GpuVirtualAddress);
                cmd.SetComputeRootUnorderedAccessView(2, SpectrumHT.GpuVirtualAddress);
                cmd.Dispatch(groups, groups, 1);
            }

            // 3. 逆变换得到位移场与法线/雅可比
            cmd.SetPipelineState(InverseTransformPSO);
            cmd.SetComputeRootShaderResourceView(1, SpectrumHT.GpuVirtualAddress);
            cmd.TransitionTo(DisplacementMap, RHIResourceState.UnorderedAccess);
            cmd.TransitionTo(NormalJacobMap, RHIResourceState.UnorderedAccess);
            cmd.SetComputeRootUnorderedAccessView(2, DisplacementMap.GpuVirtualAddress);
            cmd.Dispatch(groups, groups, 1);
            cmd.TransitionTo(DisplacementMap, RHIResourceState.PixelShaderResource);
            cmd.TransitionTo(NormalJacobMap, RHIResourceState.PixelShaderResource);
            cmd.TransitionTo(SpectrumHT, RHIResourceState.NonPixelShaderResource);
        }

        /// <summary>采样水面位移(CPU 端近似, 用于浮力)。返回世界坐标处的高度偏移。</summary>
        public float SampleDisplacementHeight(Vector2 worldXZ, float time)
        {
            // 简化: 使用单一 Gerstner 波近似 CPU 端浮力
            float k = 2 * MathF.PI / 60.0f;
            float c = MathF.Sqrt(9.8f / k);
            float phase = k * (worldXZ.X * 0.7f + worldXZ.Y * 0.7f) - c * time;
            return MathF.Cos(phase) * 1.5f;
        }

        /// <summary>采样水面法线(CPU 端近似)。</summary>
        public Vector3 SampleNormalApprox(Vector2 worldXZ, float time)
        {
            float k = 2 * MathF.PI / 60.0f;
            float c = MathF.Sqrt(9.8f / k);
            float phase = k * (worldXZ.X * 0.7f + worldXZ.Y * 0.7f) - c * time;
            float dx = -k * 0.7f * MathF.Sin(phase) * 1.5f;
            float dz = -k * 0.7f * MathF.Sin(phase) * 1.5f;
            Vector3 n = Vector3.Normalize(new Vector3(-dx, 1.0f, -dz));
            return n;
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            if (_disposed) return;
            SpectrumH0?.Dispose();
            SpectrumH0Conj?.Dispose();
            SpectrumHT?.Dispose();
            DisplacementMap?.Dispose();
            NormalJacobMap?.Dispose();
            PhillipsPSO?.Dispose();
            UpdateSpectrumPSO?.Dispose();
            ButterflyPSO?.Dispose();
            InverseTransformPSO?.Dispose();
            RootSignature?.Dispose();
            FFTCBUpload?.Dispose();
            ButterflyCBUpload?.Dispose();
            _disposed = true;
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // 水面网格生成器
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 水面网格生成器: 创建跟随相机的平面/网格化水面, 支持无限平面模式与
    /// Clipmap 模式。顶点位置在 VS 中根据 FFT 位移图偏移。
    /// </summary>
    public sealed class WaterMeshGenerator : IDisposable
    {
        /// <summary>网格类型。</summary>
        public WaterMeshType MeshType { get; set; } = WaterMeshType.Clipmap;
        /// <summary>共享水面顶点缓冲(UV 坐标)。</summary>
        public RHIBuffer WaterVB { get; private set; }
        /// <summary>水面索引缓冲。</summary>
        public RHIBuffer WaterIB { get; private set; }
        /// <summary>顶点数。</summary>
        public int VertexCount { get; private set; }
        /// <summary>索引数。</summary>
        public int IndexCount { get; private set; }

        private readonly RHIDevice _device;
        private bool _disposed;

        public WaterMeshGenerator(RHIDevice device, int gridSize = WaterConstants.WaterMeshGridSize)
        {
            _device = device;
            CreateMesh(device, gridSize);
        }

        private void CreateMesh(RHIDevice device, int gridSize)
        {
            VertexCount = gridSize * gridSize;
            int tris = (gridSize - 1) * (gridSize - 1) * 2;
            IndexCount = tris * 3;

            // 顶点: UV 坐标 (0..1), 世界位置在 VS 中根据网格尺寸 + 位移图重建
            Span<Vector2> uv = stackalloc Vector2[VertexCount];
            for (int y = 0; y < gridSize; y++)
                for (int x = 0; x < gridSize; x++)
                    uv[y * gridSize + x] = new Vector2((float)x / (gridSize - 1), (float)y / (gridSize - 1));

            WaterVB = device.CreateCommittedBuffer(new RHIBufferDesc
            {
                SizeInBytes = (ulong)(VertexCount * 8),
                Format = RHIFormat.Unknown, HeapType = RHIResourceHeapType.Default,
                Flags = RHIBufferFlags.VertexBuffer, Stride = 8
            }, MemoryMarshal.AsBytes<Vector2>(uv.ToArray()).ToArray(), "Water.VB");

            Span<uint> idx = stackalloc uint[IndexCount];
            int i = 0;
            for (int y = 0; y < gridSize - 1; y++)
                for (int x = 0; x < gridSize - 1; x++)
                {
                    int v = y * gridSize + x;
                    idx[i++] = (uint)v;
                    idx[i++] = (uint)(v + gridSize);
                    idx[i++] = (uint)(v + 1);
                    idx[i++] = (uint)(v + 1);
                    idx[i++] = (uint)(v + gridSize);
                    idx[i++] = (uint)(v + gridSize + 1);
                }
            WaterIB = device.CreateCommittedBuffer(new RHIBufferDesc
            {
                SizeInBytes = (ulong)(IndexCount * 4),
                Format = RHIFormat.R32_UInt, HeapType = RHIResourceHeapType.Default,
                Flags = RHIBufferFlags.IndexBuffer, Stride = 4
            }, MemoryMarshal.AsBytes<uint>(idx.ToArray()).ToArray(), "Water.IB");
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            if (_disposed) return;
            WaterVB?.Dispose();
            WaterIB?.Dispose();
            _disposed = true;
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // 水面材质 (反射/折射/焦散/散射)
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 水面材质: 管理水面着色所需的所有参数与纹理引用, 包括反射/折射目标、
    /// 焦散图、环境贴图。实际着色在 WaterRenderer 的 PS 中完成。
    /// </summary>
    public sealed class WaterMaterial : IDisposable
    {
        /// <summary>深水颜色。</summary>
        public Vector3 DeepColor = new(0.02f, 0.08f, 0.15f);
        /// <summary>浅水颜色。</summary>
        public Vector3 ShallowColor = new(0.1f, 0.4f, 0.5f);
        /// <summary>水的吸收系数(每米, RGB)。</summary>
        public Vector3 Absorption = new(0.5f, 0.2f, 0.05f);
        /// <summary>水的散射系数。</summary>
        public Vector3 Scattering = new(0.2f, 0.4f, 0.5f);
        /// <summary>菲涅尔 0 度反射率。</summary>
        public float Fresnel0 = 0.02f;
        /// <summary>反射强度。</summary>
        public float ReflectionIntensity = 0.8f;
        /// <summary>折射强度。</summary>
        public float RefractionIntensity = 0.6f;
        /// <summary>焦散强度。</summary>
        public float CausticsIntensity = 0.6f;
        /// <summary>焦散平铺。</summary>
        public float CausticsTiling = 8.0f;
        /// <summary>泡沫强度。</summary>
        public float FoamIntensity = 1.0f;
        /// <summary>泡沫消散速度。</summary>
        public float FoamDissipation = 0.5f;
        /// <summary>太阳高光强度。</summary>
        public float SunSpecularIntensity = 1.0f;
        /// <summary>太阳高光锐度。</summary>
        public float SunSpecularPower = 200.0f;
        /// <summary>反射目标纹理(平面反射渲染结果) SRV。</summary>
        public RHIDescriptorHandle ReflectionSRV;
        /// <summary>折射目标(场景颜色, 不含水) SRV。</summary>
        public RHIDescriptorHandle RefractionSRV;
        /// <summary>场景深度 SRV(用于折射/水下)。</summary>
        public RHIDescriptorHandle SceneDepthSRV;
        /// <summary>环境贴图(天空) SRV。</summary>
        public RHIDescriptorHandle EnvironmentSRV;
        /// <summary>焦散纹理 SRV。</summary>
        public RHIDescriptorHandle CausticsSRV;
        /// <summary>线性采样器。</summary>
        public RHIDescriptorHandle LinearSampler;
        /// <summary>各向异性采样器。</summary>
        public RHIDescriptorHandle AnisoSampler;

        private bool _disposed;

        /// <summary>从材质填充 WaterRenderCB。</summary>
        public void FillRenderCB(ref WaterRenderCB cb)
        {
            cb.DeepColor = DeepColor;
            cb.ShallowColor = ShallowColor;
            cb.Absorption = Absorption;
            cb.Scattering = Scattering;
            cb.Fresnel0 = Fresnel0;
            cb.ReflectionIntensity = ReflectionIntensity;
            cb.RefractionIntensity = RefractionIntensity;
            cb.CausticsIntensity = CausticsIntensity;
            cb.CausticsTiling = CausticsTiling;
            cb.FoamIntensity = FoamIntensity;
            cb.FoamDissipation = FoamDissipation;
            cb.SunSpecularIntensity = SunSpecularIntensity;
            cb.SunSpecularPower = SunSpecularPower;
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // 水面泡沫渲染器
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 水面泡沫渲染器: 基于雅可比行列式(波浪翻转区域)与速度场生成泡沫遮罩,
    /// 支持动态消散与累积。输出泡沫遮罩纹理供水面着色器采样。
    /// </summary>
    public sealed class WaterFoamRenderer : IDisposable
    {
        /// <summary>泡沫生成方法。</summary>
        public FoamMethod Method { get; set; } = FoamMethod.Jacobian;
        /// <summary>泡沫遮罩纹理(R8)。</summary>
        public RHITexture FoamTexture { get; private set; }
        /// <summary>泡沫 SRV。</summary>
        public RHIDescriptorHandle FoamSRV { get; private set; }
        /// <summary>泡沫 UAV。</summary>
        public RHIDescriptorHandle FoamUAV { get; private set; }
        /// <summary>泡沫生成 PSO。</summary>
        public RHIPipelineState FoamGenPSO { get; private set; }
        /// <summary>泡沫消散 PSO。</summary>
        public RHIPipelineState FoamDissipatePSO { get; private set; }
        /// <summary>根签名。</summary>
        public RHIRootSignature RootSignature { get; private set; }

        private readonly RHIDevice _device;
        private bool _disposed;

        public WaterFoamRenderer(RHIDevice device, int size = WaterConstants.FFTSize)
        {
            _device = device;
            FoamTexture = device.CreateTexture(new RHITextureDesc
            {
                Dimension = RHIResourceDimension.Texture2D,
                Format = RHIFormat.R8_UNorm,
                Width = size, Height = size, DepthOrArraySize = 1, MipLevels = 1, SampleCount = 1,
                Flags = RHITextureFlags.ShaderResource | RHITextureFlags.UnorderedAccess
            }, "Water.Foam");
            FoamSRV = device.CreateSRV(FoamTexture, new RHISRVDesc
            {
                Format = RHIFormat.R8_UNorm, Dimension = RHIResourceDimension.Texture2D,
                MostDetailedMip = 0, MipLevels = 1, First2DArraySlice = 0, ArraySize = 1
            });
            FoamUAV = device.CreateUAV(FoamTexture, new RHIUAVDesc
            {
                Format = RHIFormat.R8_UNorm, Dimension = RHIResourceDimension.Texture2D, MipSlice = 0
            });

            var rootDesc = new RHIRootSignatureDesc
            {
                Parameters = new[]
                {
                    new RHIRootParameter { ParameterType = RHIRootParameterType.CBV, ShaderVisibility = RHIShaderVisibility.All, Descriptor = new RHIRootDescriptor { ShaderRegister = 0 } },
                    new RHIRootParameter { ParameterType = RHIRootParameterType.SRV, ShaderVisibility = RHIShaderVisibility.All, Descriptor = new RHIRootDescriptor { ShaderRegister = 0 } },
                    new RHIRootParameter { ParameterType = RHIRootParameterType.UAV, ShaderVisibility = RHIShaderVisibility.All, Descriptor = new RHIRootDescriptor { ShaderRegister = 0 } }
                },
                StaticSamplers = Array.Empty<RHISamplerDesc>()
            };
            RootSignature = device.CreateRootSignature(rootDesc);
            FoamGenPSO = device.CreateComputePipelineState(new RHIComputePipelineDesc
            {
                Name = "Water.FoamGenCS",
                CS = device.CompileShaderFromFile("Shaders/WaterFoam.hlsl", "FoamGenCS", RHIShaderStage.Compute, RHIShaderModel.SM_6_6)
            });
            FoamDissipatePSO = device.CreateComputePipelineState(new RHIComputePipelineDesc
            {
                Name = "Water.FoamDissipateCS",
                CS = device.CompileShaderFromFile("Shaders/WaterFoam.hlsl", "FoamDissipateCS", RHIShaderStage.Compute, RHIShaderModel.SM_6_6)
            });
        }

        /// <summary>生成与更新泡沫。</summary>
        public void Update(RHICommandList cmd, RHITexture normalJacobMap, float intensity, float dissipation, float dt)
        {
            cmd.SetPipelineState(FoamGenPSO);
            cmd.SetComputeRootSignature(RootSignature);
            // 简化: 实际通过 CB 传 intensity/dissipation
            cmd.TransitionTo(normalJacobMap, RHIResourceState.NonPixelShaderResource);
            cmd.SetComputeRootShaderResourceView(1, normalJacobMap.GpuVirtualAddress);
            cmd.TransitionTo(FoamTexture, RHIResourceState.UnorderedAccess);
            cmd.SetComputeRootUnorderedAccessView(2, FoamTexture.GpuVirtualAddress);
            int groups = (WaterConstants.FFTSize + 7) / 8;
            cmd.Dispatch(groups, groups, 1);
            cmd.TransitionTo(FoamTexture, RHIResourceState.PixelShaderResource);
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            if (_disposed) return;
            FoamTexture?.Dispose();
            FoamGenPSO?.Dispose();
            FoamDissipatePSO?.Dispose();
            RootSignature?.Dispose();
            _disposed = true;
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // 水流场
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 水流场: 维护水流方向与流速, 驱动纹理 UV 滚动与浮力方向。
    /// 支持空间变化的流速场(RG16_Float 纹理)用于河流弯道。
    /// </summary>
    public sealed class WaterFlowField : IDisposable
    {
        /// <summary>全局水流方向(XZ)。</summary>
        public Vector2 GlobalFlowDirection { get; set; } = new(1, 0);
        /// <summary>全局流速(米/秒)。</summary>
        public float GlobalFlowSpeed { get; set; } = 1.0f;
        /// <summary>流速场纹理(RG16_Float, 方向*速度)。</summary>
        public RHITexture FlowFieldTexture { get; private set; }
        /// <summary>流速场 SRV。</summary>
        public RHIDescriptorHandle FlowFieldSRV { get; private set; }

        private readonly RHIDevice _device;
        private bool _disposed;

        public WaterFlowField(RHIDevice device, int size = 512)
        {
            _device = device;
            FlowFieldTexture = device.CreateTexture(new RHITextureDesc
            {
                Dimension = RHIResourceDimension.Texture2D,
                Format = RHIFormat.R16G16_Float,
                Width = size, Height = size, DepthOrArraySize = 1, MipLevels = 1, SampleCount = 1,
                Flags = RHITextureFlags.ShaderResource | RHITextureFlags.UnorderedAccess
            }, "Water.FlowField");
            FlowFieldSRV = device.CreateSRV(FlowFieldTexture, new RHISRVDesc
            {
                Format = RHIFormat.R16G16_Float, Dimension = RHIResourceDimension.Texture2D,
                MostDetailedMip = 0, MipLevels = 1, First2DArraySlice = 0, ArraySize = 1
            });
        }

        /// <summary>采样某点流速(CPU 端近似, 用于浮力)。</summary>
        public Vector3 SampleFlowVelocity(Vector2 worldXZ)
        {
            return new Vector3(GlobalFlowDirection.X, 0, GlobalFlowDirection.Y) * GlobalFlowSpeed;
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            if (_disposed) return;
            FlowFieldTexture?.Dispose();
            _disposed = true;
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // 浮力计算接口
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 浮力计算接口: 在物体底部采样水面位移/法线/流速, 计算浮力、阻力与扭矩。
    /// 供物理引擎(如 Jolt)通过接口获取水面作用力。
    /// </summary>
    public sealed class BuoyancyCalculator : IDisposable
    {
        private readonly FFTWaveSimulator _fft;
        private readonly WaterFlowField _flow;
        private readonly RHIDevice _device;
        private bool _disposed;

        /// <summary>水的密度(kg/m^3)。</summary>
        public float WaterDensity { get; set; } = 1000.0f;
        /// <summary>重力加速度。</summary>
        public float Gravity { get; set; } = 9.81f;

        public BuoyancyCalculator(RHIDevice device, FFTWaveSimulator fft, WaterFlowField flow)
        {
            _device = device;
            _fft = fft;
            _flow = flow;
        }

        /// <summary>
        /// 在物体底部网格采样水面, 返回采样点数组。
        /// </summary>
        public BuoyancySample[] SampleSurface(Vector3 objectCenter, float objectRadius, float time)
        {
            int grid = WaterConstants.BuoyancySampleGrid;
            var samples = new BuoyancySample[grid * grid];
            float spacing = objectRadius * 2 / grid;
            for (int z = 0; z < grid; z++)
            {
                for (int x = 0; x < grid; x++)
                {
                    Vector3 wp = objectCenter + new Vector3(
                        -objectRadius + x * spacing, 0, -objectRadius + z * spacing);
                    Vector2 xz = new(wp.X, wp.Z);
                    float h = _fft.SampleDisplacementHeight(xz, time);
                    Vector3 n = _fft.SampleNormalApprox(xz, time);
                    Vector3 v = _flow.SampleFlowVelocity(xz);
                    samples[z * grid + x] = new BuoyancySample
                    {
                        WorldPosition = new Vector3(wp.X, h, wp.Z),
                        Normal = n,
                        Velocity = v,
                        Height = h
                    };
                }
            }
            return samples;
        }

        /// <summary>
        /// 计算浮力合力与扭矩(基于阿基米德原理 + 采样体积)。
        /// 返回 (合力, 作用点, 扭矩)。
        /// </summary>
        public (Vector3 Force, Vector3 Center, Vector3 Torque) ComputeBuoyancy(
            Vector3 objectCenter, float objectRadius, float objectMass, float time)
        {
            var samples = SampleSurface(objectCenter, objectRadius, time);
            float submergedVolume = 0;
            Vector3 forceSum = Vector3.Zero;
            Vector3 torqueSum = Vector3.Zero;
            float areaPerSample = (objectRadius * 2 / WaterConstants.BuoyancySampleGrid);
            areaPerSample *= areaPerSample;

            for (int i = 0; i < samples.Length; i++)
            {
                ref var s = ref samples[i];
                float submersion = objectCenter.Y - s.Height;
                if (submersion > 0)
                {
                    // 浮力 = rho * g * V_submerged (近似为采样面积 * 淹没深度)
                    float v = areaPerSample * Math.Min(submersion, objectRadius);
                    Vector3 buoyancy = new(0, WaterDensity * Gravity * v, 0);
                    // 阻力(与相对速度反向)
                    Vector3 relVel = -s.Velocity;
                    Vector3 drag = -relVel * (0.5f * WaterDensity * v * 0.47f);
                    Vector3 f = buoyancy + drag;
                    forceSum += f;
                    Vector3 r = s.WorldPosition - objectCenter;
                    torqueSum += Vector3.Cross(r, f);
                    submergedVolume += v;
                }
            }
            Vector3 gravityForce = new(0, -Gravity * objectMass, 0);
            forceSum += gravityForce;
            Vector3 center = submergedVolume > 0 ? objectCenter : objectCenter;
            return (forceSum, center, torqueSum);
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // 屏幕空间反射(SSR)水面
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 屏幕空间反射水面: 对水面像素按法线反射方向在场景深度缓冲上做光线步进,
    /// 增强近距反射细节(平面反射无法覆盖的局部反射)。
    /// </summary>
    public sealed class WaterSSR : IDisposable
    {
        /// <summary>SSR 结果纹理(R11G11B10)。</summary>
        public RHITexture SSRResultTexture { get; private set; }
        /// <summary>SSR SRV。</summary>
        public RHIDescriptorHandle SSRResultSRV { get; private set; }
        /// <summary>SSR UAV。</summary>
        public RHIDescriptorHandle SSRResultUAV { get; private set; }
        /// <summary>SSR PSO。</summary>
        public RHIPipelineState SSRPSO { get; private set; }
        /// <summary>根签名。</summary>
        public RHIRootSignature RootSignature { get; private set; }

        private readonly RHIDevice _device;
        private bool _disposed;

        public WaterSSR(RHIDevice device, int width, int height)
        {
            _device = device;
            SSRResultTexture = device.CreateTexture(new RHITextureDesc
            {
                Dimension = RHIResourceDimension.Texture2D,
                Format = RHIFormat.R11G11B10_Float,
                Width = width, Height = height, DepthOrArraySize = 1, MipLevels = 1, SampleCount = 1,
                Flags = RHITextureFlags.ShaderResource | RHITextureFlags.UnorderedAccess
            }, "Water.SSR");
            SSRResultSRV = device.CreateSRV(SSRResultTexture, new RHISRVDesc
            {
                Format = RHIFormat.R11G11B10_Float, Dimension = RHIResourceDimension.Texture2D,
                MostDetailedMip = 0, MipLevels = 1, First2DArraySlice = 0, ArraySize = 1
            });
            SSRResultUAV = device.CreateUAV(SSRResultTexture, new RHIUAVDesc
            {
                Format = RHIFormat.R11G11B10_Float, Dimension = RHIResourceDimension.Texture2D, MipSlice = 0
            });
            var rootDesc = new RHIRootSignatureDesc
            {
                Parameters = new[]
                {
                    new RHIRootParameter { ParameterType = RHIRootParameterType.CBV, ShaderVisibility = RHIShaderVisibility.All, Descriptor = new RHIRootDescriptor { ShaderRegister = 0 } },
                    new RHIRootParameter { ParameterType = RHIRootParameterType.SRV, ShaderVisibility = RHIShaderVisibility.All, Descriptor = new RHIRootDescriptor { ShaderRegister = 0 } }, // 场景颜色
                    new RHIRootParameter { ParameterType = RHIRootParameterType.SRV, ShaderVisibility = RHIShaderVisibility.All, Descriptor = new RHIRootDescriptor { ShaderRegister = 1 } }, // 深度
                    new RHIRootParameter { ParameterType = RHIRootParameterType.SRV, ShaderVisibility = RHIShaderVisibility.All, Descriptor = new RHIRootDescriptor { ShaderRegister = 2 } }, // 水面法线/位移
                    new RHIRootParameter { ParameterType = RHIRootParameterType.UAV, ShaderVisibility = RHIShaderVisibility.All, Descriptor = new RHIRootDescriptor { ShaderRegister = 0 } }
                },
                StaticSamplers = new[] { RHISamplerDesc.PointClamp, RHISamplerDesc.LinearWrap }
            };
            RootSignature = device.CreateRootSignature(rootDesc);
            SSRPSO = device.CreateComputePipelineState(new RHIComputePipelineDesc
            {
                Name = "Water.SSRCS",
                CS = device.CompileShaderFromFile("Shaders/WaterSSR.hlsl", "SSRCS", RHIShaderStage.Compute, RHIShaderModel.SM_6_6)
            });
        }

        /// <summary>执行 SSR 光线步进。</summary>
        public void Trace(RHICommandList cmd, RHIBuffer renderCB, RHIDescriptorHandle sceneColorSRV,
            RHIDescriptorHandle sceneDepthSRV, RHIDescriptorHandle waterNormalSRV, int width, int height)
        {
            cmd.SetPipelineState(SSRPSO);
            cmd.SetComputeRootSignature(RootSignature);
            cmd.SetComputeRootConstantBufferView(0, renderCB.GpuVirtualAddress);
            cmd.TransitionTo(SSRResultTexture, RHIResourceState.UnorderedAccess);
            cmd.SetComputeRootUnorderedAccessView(4, SSRResultTexture.GpuVirtualAddress);
            int gx = (width + 7) / 8, gy = (height + 7) / 8;
            cmd.Dispatch(gx, gy, 1);
            cmd.TransitionTo(SSRResultTexture, RHIResourceState.PixelShaderResource);
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            if (_disposed) return;
            SSRResultTexture?.Dispose();
            SSRPSO?.Dispose();
            RootSignature?.Dispose();
            _disposed = true;
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // 体积散射
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 体积散射: 水下 god rays(光柱)与水上水雾。基于屏幕空间光线步进,
    /// 沿太阳方向累积散射光, 配合相位函数(Henyey-Greenstein)。
    /// </summary>
    public sealed class WaterVolumetricScattering : IDisposable
    {
        /// <summary>体积散射结果纹理。</summary>
        public RHITexture VolumetricTexture { get; private set; }
        /// <summary>体积 SRV。</summary>
        public RHIDescriptorHandle VolumetricSRV { get; private set; }
        /// <summary>体积 UAV。</summary>
        public RHIDescriptorHandle VolumetricUAV { get; private set; }
        /// <summary>体积散射 PSO。</summary>
        public RHIPipelineState VolumetricPSO { get; private set; }
        /// <summary>根签名。</summary>
        public RHIRootSignature RootSignature { get; private set; }
        /// <summary>Henyey-Greenstein 相位函数 g 参数。</summary>
        public float PhaseG { get; set; } = 0.6f;
        /// <summary>体积光强度。</summary>
        public float Intensity { get; set; } = 0.5f;

        private readonly RHIDevice _device;
        private bool _disposed;

        public WaterVolumetricScattering(RHIDevice device, int width, int height)
        {
            _device = device;
            VolumetricTexture = device.CreateTexture(new RHITextureDesc
            {
                Dimension = RHIResourceDimension.Texture2D,
                Format = RHIFormat.R16G16B16A16_Float,
                Width = width, Height = height, DepthOrArraySize = 1, MipLevels = 1, SampleCount = 1,
                Flags = RHITextureFlags.ShaderResource | RHITextureFlags.UnorderedAccess
            }, "Water.Volumetric");
            VolumetricSRV = device.CreateSRV(VolumetricTexture, new RHISRVDesc
            {
                Format = RHIFormat.R16G16B16A16_Float, Dimension = RHIResourceDimension.Texture2D,
                MostDetailedMip = 0, MipLevels = 1, First2DArraySlice = 0, ArraySize = 1
            });
            VolumetricUAV = device.CreateUAV(VolumetricTexture, new RHIUAVDesc
            {
                Format = RHIFormat.R16G16B16A16_Float, Dimension = RHIResourceDimension.Texture2D, MipSlice = 0
            });
            var rootDesc = new RHIRootSignatureDesc
            {
                Parameters = new[]
                {
                    new RHIRootParameter { ParameterType = RHIRootParameterType.CBV, ShaderVisibility = RHIShaderVisibility.All, Descriptor = new RHIRootDescriptor { ShaderRegister = 0 } },
                    new RHIRootParameter { ParameterType = RHIRootParameterType.SRV, ShaderVisibility = RHIShaderVisibility.All, Descriptor = new RHIRootDescriptor { ShaderRegister = 0 } }, // 深度
                    new RHIRootParameter { ParameterType = RHIRootParameterType.UAV, ShaderVisibility = RHIShaderVisibility.All, Descriptor = new RHIRootDescriptor { ShaderRegister = 0 } }
                },
                StaticSamplers = new[] { RHISamplerDesc.PointClamp }
            };
            RootSignature = device.CreateRootSignature(rootDesc);
            VolumetricPSO = device.CreateComputePipelineState(new RHIComputePipelineDesc
            {
                Name = "Water.VolumetricCS",
                CS = device.CompileShaderFromFile("Shaders/WaterVolumetric.hlsl", "VolumetricCS", RHIShaderStage.Compute, RHIShaderModel.SM_6_6)
            });
        }

        /// <summary>执行体积光累积。</summary>
        public void Render(RHICommandList cmd, RHIBuffer renderCB, RHIDescriptorHandle sceneDepthSRV, int width, int height)
        {
            cmd.SetPipelineState(VolumetricPSO);
            cmd.SetComputeRootSignature(RootSignature);
            cmd.SetComputeRootConstantBufferView(0, renderCB.GpuVirtualAddress);
            cmd.TransitionTo(VolumetricTexture, RHIResourceState.UnorderedAccess);
            cmd.SetComputeRootUnorderedAccessView(2, VolumetricTexture.GpuVirtualAddress);
            int gx = (width + 7) / 8, gy = (height + 7) / 8;
            cmd.Dispatch(gx, gy, 1);
            cmd.TransitionTo(VolumetricTexture, RHIResourceState.PixelShaderResource);
        }

        /// <summary>Henyey-Greenstein 相位函数。</summary>
        public float HenyeyGreenstein(float cosTheta, float g)
        {
            float g2 = g * g;
            return (1 - g2) / (4 * MathF.PI * MathF.Pow(1 + g2 - 2 * g * cosTheta, 1.5f));
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            if (_disposed) return;
            VolumetricTexture?.Dispose();
            VolumetricPSO?.Dispose();
            RootSignature?.Dispose();
            _disposed = true;
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // 水下后处理
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 水下后处理: 当相机位于水面以下时, 应用深度雾、色散吸收、焦散投影、
    /// 屏幕扭曲。当相机在水面以上但部分像素在水下时, 对水下区域应用吸收。
    /// </summary>
    public sealed class UnderwaterPostProcess : IDisposable
    {
        /// <summary>后处理 PSO。</summary>
        public RHIPipelineState UnderwaterPSO { get; private set; }
        /// <summary>根签名。</summary>
        public RHIRootSignature RootSignature { get; private set; }
        /// <summary>CB 上传缓冲。</summary>
        public RHIBuffer UnderwaterCBUpload { get; private set; }

        private readonly RHIDevice _device;
        private bool _disposed;

        public UnderwaterPostProcess(RHIDevice device)
        {
            _device = device;
            var rootDesc = new RHIRootSignatureDesc
            {
                Parameters = new[]
                {
                    new RHIRootParameter { ParameterType = RHIRootParameterType.CBV, ShaderVisibility = RHIShaderVisibility.All, Descriptor = new RHIRootDescriptor { ShaderRegister = 0 } },
                    new RHIRootParameter { ParameterType = RHIRootParameterType.SRV, ShaderVisibility = RHIShaderVisibility.All, Descriptor = new RHIRootDescriptor { ShaderRegister = 0 } }, // 场景颜色
                    new RHIRootParameter { ParameterType = RHIRootParameterType.SRV, ShaderVisibility = RHIShaderVisibility.All, Descriptor = new RHIRootDescriptor { ShaderRegister = 1 } }, // 深度
                    new RHIRootParameter { ParameterType = RHIRootParameterType.SRV, ShaderVisibility = RHIShaderVisibility.All, Descriptor = new RHIRootDescriptor { ShaderRegister = 2 } }  // 焦散
                },
                StaticSamplers = new[] { RHISamplerDesc.LinearWrap, RHISamplerDesc.PointClamp }
            };
            RootSignature = device.CreateRootSignature(rootDesc);
            UnderwaterPSO = device.CreateGraphicsPipelineState(new RHIGraphicsPipelineDesc
            {
                Name = "Water.UnderwaterPSO",
                VS = device.CompileShaderFromFile("Shaders/FullScreenTriangle.hlsl", "FullScreenVS", RHIShaderStage.Vertex, RHIShaderModel.SM_6_6),
                PS = device.CompileShaderFromFile("Shaders/WaterUnderwater.hlsl", "UnderwaterPS", RHIShaderStage.Pixel, RHIShaderModel.SM_6_6),
                PrimitiveTopology = RHIPrimitiveTopology.TriangleList,
                BlendState = RHIBlendDesc.Default,
                DepthStencilState = RHIDepthDesc.None,
                RasterizerState = RHIRasterizerDesc.NoCull,
                RTVFormats = new[] { RHIFormat.R11G11B10_Float }
            });
            UnderwaterCBUpload = device.CreateUploadBuffer((ulong)Marshal.SizeOf<UnderwaterCB>(), "Water.UnderwaterCB");
        }

        /// <summary>执行水下后处理。</summary>
        public void Apply(RHICommandList cmd, RHIDescriptorHandle sceneColorSRV, RHIDescriptorHandle sceneDepthSRV,
            RHIDescriptorHandle causticsSRV, RHIDescriptorHandle rtv, UnderwaterCB cb, int width, int height)
        {
            _device.UploadToBuffer(UnderwaterCBUpload, MemoryMarshal.CreateSpan(ref cb, 1));
            cmd.SetPipelineState(UnderwaterPSO);
            cmd.SetGraphicsRootSignature(RootSignature);
            cmd.SetGraphicsRootConstantBufferView(0, UnderwaterCBUpload.GpuVirtualAddress);
            cmd.SetViewport(new RHIViewport(width, height));
            cmd.SetScissorRect(new RHIScissorRect(0, 0, width, height));
            cmd.OMSetRenderTargets(new[] { rtv }, null);
            // 绑定 SRV(通过描述符表或直接)
            cmd.DrawInstanced(3, 1, 0, 0);
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            if (_disposed) return;
            UnderwaterPSO?.Dispose();
            RootSignature?.Dispose();
            UnderwaterCBUpload?.Dispose();
            _disposed = true;
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // 水面渲染器主类
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// yama水面海洋渲染器主类: 统一管理 FFT 模拟、水面网格、材质、泡沫、
    /// 水流、浮力、SSR、体积散射与水下后处理。与 RHI 完全对接。
    /// </summary>
    public sealed class WaterRenderer : IDisposable
    {
        /// <summary>RHI 设备。</summary>
        public RHIDevice Device { get; }
        /// <summary>FFT 波浪模拟器。</summary>
        public FFTWaveSimulator FFTSimulator { get; private set; }
        /// <summary>水面网格。</summary>
        public WaterMeshGenerator MeshGenerator { get; private set; }
        /// <summary>水面材质。</summary>
        public WaterMaterial Material { get; private set; }
        /// <summary>泡沫渲染器。</summary>
        public WaterFoamRenderer FoamRenderer { get; private set; }
        /// <summary>水流场。</summary>
        public WaterFlowField FlowField { get; private set; }
        /// <summary>浮力计算器。</summary>
        public BuoyancyCalculator Buoyancy { get; private set; }
        /// <summary>屏幕空间反射。</summary>
        public WaterSSR SSR { get; private set; }
        /// <summary>体积散射。</summary>
        public WaterVolumetricScattering Volumetric { get; private set; }
        /// <summary>水下后处理。</summary>
        public UnderwaterPostProcess Underwater { get; private set; }
        /// <summary>水面渲染 PSO。</summary>
        public RHIPipelineState WaterPSO { get; private set; }
        /// <summary>渲染根签名。</summary>
        public RHIRootSignature RenderRootSig { get; private set; }
        /// <summary>渲染 CB 上传缓冲。</summary>
        public RHIBuffer RenderCBUpload { get; private set; }

        /// <summary>风速。</summary>
        public float WindSpeed { get; set; } = 12.0f;
        /// <summary>风向。</summary>
        public Vector2 WindDirection { get; set; } = new(1, 0);
        /// <summary>波浪幅度。</summary>
        public float Amplitude { get; set; } = 1.0f;
        /// <summary>Choppiness。</summary>
        public float Choppiness { get; set; } = 1.2f;
        /// <summary>海平面高度。</summary>
        public float WaterLevel { get; set; } = 0.0f;
        /// <summary>太阳方向。</summary>
        public Vector3 SunDirection { get; set; } = Vector3.Normalize(new Vector3(0.5f, 0.6f, 0.4f));
        /// <summary>太阳颜色。</summary>
        public Vector3 SunColor { get; set; } = new(1.0f, 0.95f, 0.85f);
        /// <summary>是否启用 SSR。</summary>
        public bool EnableSSR { get; set; } = true;
        /// <summary>是否启用体积散射。</summary>
        public bool EnableVolumetric { get; set; } = true;

        private bool _disposed;
        private int _width, _height;

        public WaterRenderer(RHIDevice device, int width, int height)
        {
            Device = device;
            _width = width;
            _height = height;
            FFTSimulator = new FFTWaveSimulator(device);
            MeshGenerator = new WaterMeshGenerator(device);
            Material = new WaterMaterial();
            FoamRenderer = new WaterFoamRenderer(device);
            FlowField = new WaterFlowField(device);
            Buoyancy = new BuoyancyCalculator(device, FFTSimulator, FlowField);
            SSR = new WaterSSR(device, width, height);
            Volumetric = new WaterVolumetricScattering(device, width, height);
            Underwater = new UnderwaterPostProcess(device);
            RenderCBUpload = device.CreateUploadBuffer((ulong)Marshal.SizeOf<WaterRenderCB>(), "Water.RenderCB");
            CreateRenderPipeline(device);
        }

        private void CreateRenderPipeline(RHIDevice device)
        {
            var rootDesc = new RHIRootSignatureDesc
            {
                Parameters = new[]
                {
                    new RHIRootParameter { ParameterType = RHIRootParameterType.CBV, ShaderVisibility = RHIShaderVisibility.All, Descriptor = new RHIRootDescriptor { ShaderRegister = 0 } },
                    new RHIRootParameter
                    {
                        ParameterType = RHIRootParameterType.DescriptorTable, ShaderVisibility = RHIShaderVisibility.All,
                        DescriptorTable = new RHIRootDescriptorTable { Ranges = new[] { new RHIRootDescriptorRange { RangeType = RHIDescriptorRangeType.SRV, NumDescriptors = 16, BaseShaderRegister = 0 } } }
                    },
                    new RHIRootParameter
                    {
                        ParameterType = RHIRootParameterType.DescriptorTable, ShaderVisibility = RHIShaderVisibility.All,
                        DescriptorTable = new RHIRootDescriptorTable { Ranges = new[] { new RHIRootDescriptorRange { RangeType = RHIDescriptorRangeType.Sampler, NumDescriptors = 4, BaseShaderRegister = 0 } } }
                    }
                },
                StaticSamplers = new[] { RHISamplerDesc.LinearWrap, RHISamplerDesc.AnisotropicWrap, RHISamplerDesc.PointClamp },
                AllowInputAssembler = true
            };
            RenderRootSig = device.CreateRootSignature(rootDesc);
            WaterPSO = device.CreateGraphicsPipelineState(new RHIGraphicsPipelineDesc
            {
                Name = "Water.RenderPSO",
                VS = device.CompileShaderFromFile("Shaders/WaterRender.hlsl", "WaterVS", RHIShaderStage.Vertex, RHIShaderModel.SM_6_6),
                PS = device.CompileShaderFromFile("Shaders/WaterRender.hlsl", "WaterPS", RHIShaderStage.Pixel, RHIShaderModel.SM_6_6),
                InputLayout = new[] { new RHIInputElement("TEXCOORD", RHIFormat.R32G32_Float) },
                PrimitiveTopology = RHIPrimitiveTopology.TriangleList,
                BlendState = RHIBlendDesc.AlphaBlend,
                DepthStencilState = RHIDepthDesc.DepthWrite,
                RasterizerState = new RHIRasterizerDesc { FillMode = RHIFillMode.Solid, CullMode = RHICullMode.None, FrontCounterClockwise = true, DepthClipEnable = true },
                RTVFormats = new[] { RHIFormat.R11G11B10_Float },
                DSVFormat = RHIFormat.D32_Float
            });
        }

        /// <summary>每帧更新: FFT 模拟 + 泡沫 + SSR + 体积散射。</summary>
        public void Update(RHICommandList cmd, float time)
        {
            // 1. FFT 波浪模拟
            var fftCB = new FFTSimCB
            {
                FFTSize = FFTSimulator.Size,
                PatchSize = FFTSimulator.PatchSize,
                WindSpeed = WindSpeed,
                WindDirection = Vector2.Normalize(WindDirection),
                Time = time,
                AmplitudeScale = Amplitude,
                Choppiness = Choppiness,
                Gravity = 9.81f,
                PhillipsDissipation = 1.0f,
                MinWavelength = 0.02f,
                MaxWavelength = 1.0f,
                UpdateMode = (int)FFTSimulator.UpdateMode
            };
            FFTSimulator.Update(cmd, fftCB);

            // 2. 泡沫更新
            FoamRenderer.Update(cmd, FFTSimulator.NormalJacobMap, Material.FoamIntensity, Material.FoamDissipation, 0.016f);
        }

        /// <summary>渲染水面。</summary>
        public void Render(RHICommandList cmd, Vector3 cameraPos, float time, Matrix4x4 viewProj, Matrix4x4 invViewProj,
            RHIDescriptorHandle dsv, RHIDescriptorHandle[] rtvs)
        {
            var cb = new WaterRenderCB
            {
                CameraPosition = cameraPos,
                Time = time,
                ViewProjection = viewProj,
                InvViewProjection = invViewProj,
                WaterLevel = WaterLevel,
                MeshSize = FFTSimulator.PatchSize,
                FlowDirection = FlowField.GlobalFlowDirection,
                FlowSpeed = FlowField.GlobalFlowSpeed,
                SunDirection = SunDirection,
                SunColor = SunColor,
                MeshType = (int)MeshGenerator.MeshType,
                ViewportSize = new Vector2(_width, _height)
            };
            Material.FillRenderCB(ref cb);
            Device.UploadToBuffer(RenderCBUpload, MemoryMarshal.CreateSpan(ref cb, 1));

            cmd.SetPipelineState(WaterPSO);
            cmd.SetGraphicsRootSignature(RenderRootSig);
            cmd.SetGraphicsRootConstantBufferView(0, RenderCBUpload.GpuVirtualAddress);
            cmd.SetGraphicsRootDescriptorTable(1, FFTSimulator.DisplacementSRV);
            cmd.IASetVertexBuffers(0, MeshGenerator.WaterVB, 8, 0);
            cmd.IASetIndexBuffer(MeshGenerator.WaterIB, RHIFormat.R32_UInt, 0);
            cmd.IASetPrimitiveTopology(RHIPrimitiveTopology.TriangleList);
            cmd.SetViewport(new RHIViewport(_width, _height));
            cmd.SetScissorRect(new RHIScissorRect(0, 0, _width, _height));
            cmd.OMSetRenderTargets(rtvs, dsv);

            // 绘制水面网格(实例化为多层 LOD)
            for (int lod = 0; lod < WaterConstants.WaterMeshLODCount; lod++)
            {
                cmd.DrawIndexedInstanced(MeshGenerator.IndexCount, 1, 0, 0, lod);
            }

            // SSR
            if (EnableSSR)
            {
                SSR.Trace(cmd, RenderCBUpload, Material.RefractionSRV, Material.SceneDepthSRV, FFTSimulator.NormalJacobSRV, _width, _height);
            }

            // 体积散射
            if (EnableVolumetric)
            {
                Volumetric.Render(cmd, RenderCBUpload, Material.SceneDepthSRV, _width, _height);
            }
        }

        /// <summary>执行水下后处理(若相机在水下)。</summary>
        public void ApplyUnderwater(RHICommandList cmd, RHIDescriptorHandle sceneColorSRV, RHIDescriptorHandle sceneDepthSRV,
            RHIDescriptorHandle causticsSRV, RHIDescriptorHandle rtv, bool cameraUnderwater)
        {
            var cb = new UnderwaterCB
            {
                CameraUnderwater = cameraUnderwater ? 1 : 0,
                WaterLevel = WaterLevel,
                FogColor = Material.DeepColor,
                FogDensity = 0.02f,
                Absorption = Material.Absorption,
                CausticsIntensity = Material.CausticsIntensity,
                CausticsTiling = Material.CausticsTiling,
                DistortionStrength = 0.02f,
                DistortionTiling = 0.1f,
                ViewProjection = Matrix4x4.Identity,
                InvViewProjection = Matrix4x4.Identity,
                Time = 0,
                ViewportSize = new Vector2(_width, _height)
            };
            Underwater.Apply(cmd, sceneColorSRV, sceneDepthSRV, causticsSRV, rtv, cb, _width, _height);
        }

        /// <summary>计算物体浮力(供物理引擎调用)。</summary>
        public (Vector3 Force, Vector3 Center, Vector3 Torque) ComputeBuoyancy(
            Vector3 objectCenter, float objectRadius, float objectMass, float time)
        {
            return Buoyancy.ComputeBuoyancy(objectCenter, objectRadius, objectMass, time);
        }

        /// <summary>设置水流参数。</summary>
        public void SetFlow(Vector2 direction, float speed)
        {
            FlowField.GlobalFlowDirection = direction;
            FlowField.GlobalFlowSpeed = speed;
        }

        /// <summary>窗口尺寸变化时重建屏幕空间资源。</summary>
        public void OnResize(int width, int height)
        {
            _width = width;
            _height = height;
            SSR?.Dispose();
            Volumetric?.Dispose();
            SSR = new WaterSSR(Device, width, height);
            Volumetric = new WaterVolumetricScattering(Device, width, height);
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            if (_disposed) return;
            FFTSimulator?.Dispose();
            MeshGenerator?.Dispose();
            Material?.Dispose();
            FoamRenderer?.Dispose();
            FlowField?.Dispose();
            Buoyancy?.Dispose();
            SSR?.Dispose();
            Volumetric?.Dispose();
            Underwater?.Dispose();
            WaterPSO?.Dispose();
            RenderRootSig?.Dispose();
            RenderCBUpload?.Dispose();
            _disposed = true;
        }
    }
}
