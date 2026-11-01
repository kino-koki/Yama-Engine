using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using YamaEngine.RHI;
using YamaEngine.Shaders;

namespace YamaEngine.Rendering
{
    // ════════════════════════════════════════════════════════════════════════
    // yama GeometryBuffer 生成器
    //
    // 支持 MRT 8 个目标的完整 GeometryBuffer 系统：
    //   RT0: Albedo (RGB) + Alpha/Flags (A) — R8G8B8A8_UNorm
    //   RT1: Normal (RGB, Octahedral encoding) + MaterialFlags (A) — R10G10B10A2_UNorm
    //   RT2: PBR: Roughness (R) + Metalness (G) + AO (B) + ShadingModel (A) — R8G8B8A8_UNorm
    //   RT3: World Position (RGB) + Padding (A) — R16G16B16A16_Float
    //   RT4: Emissive (RGB) + EmissiveIntensity (A) — R11G11B10_Float
    //   RT5: Motion Vector (RG) + CustomData (BA) — R16G16B16A16_Float
    //   RT6: Tangent (RGB) + Anisotropy (A) — R10G10B10A2_UNorm
    //   RT7: MaterialID (R) + Subsurface (G) + ClearCoat (B) + Sheen (A) — R8G8B8A8_UNorm
    //
    // 支持特性：
    //   - Pack/Unpack 各种编码格式（Octahedral、SphereMap、TwoChannel）
    //   - GeometryBuffer 压缩（BC6H for normals, BC7 for albedo）
    //   - Debug 可视化（每种 GeometryBuffer 通道独立查看）
    //   - 选择性写入（只启用需要的 RT，节省带宽）
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>GeometryBuffer 通道枚举（和 RT 索引对应）。</summary>
    public enum GeometryBufferChannel
    {
        Albedo = 0,
        Normal = 1,
        PBR = 2,
        WorldPos = 3,
        Emissive = 4,
        MotionVector = 5,
        Tangent = 6,
        MaterialData = 7
    }

    /// <summary>着色模型类型，存在 GeometryBuffer RT2.A 通道。</summary>
    public enum ShadingModel : byte
    {
        Unlit = 0,
        DefaultLit = 1,
        Subsurface = 2,
        ClearCoat = 3,
        ClearCoatSubsurface = 4,
        Anisotropic = 5,
        Hair = 6,
        Cloth = 7,
        Eye = 8,
        TwoSidedFoliage = 9,
        Skin = 10,
        Foliage = 11
    }

    /// <summary>材质标志位。</summary>
    [Flags]
    public enum GeometryBufferMaterialFlags : byte
    {
        None = 0,
        Opaque = 1 << 0,
        TwoSided = 1 << 1,
        DitheredOpacity = 1 << 2,
        CastShadow = 1 << 3,
        ReceiveShadow = 1 << 4,
        Velocity = 1 << 5,
        UseEmissive = 1 << 6,
        UseNormalMap = 1 << 7
    }

    /// <summary>GeometryBuffer 各通道的格式配置。</summary>
    public static class GeometryBufferFormats
    {
        public const RHIFormat Albedo = RHIFormat.R8G8B8A8_UNorm;
        public const RHIFormat Normal = RHIFormat.R10G10B10A2_UNorm;
        public const RHIFormat PBR = RHIFormat.R8G8B8A8_UNorm;
        public const RHIFormat WorldPos = RHIFormat.R16G16B16A16_Float;
        public const RHIFormat Emissive = RHIFormat.R11G11B10_Float;
        public const RHIFormat MotionVector = RHIFormat.R16G16B16A16_Float;
        public const RHIFormat Tangent = RHIFormat.R10G10B10A2_UNorm;
        public const RHIFormat MaterialData = RHIFormat.R8G8B8A8_UNorm;
        public const int MaxTargets = 8;

        public static readonly RHIFormat[] AllFormats =
        {
            Albedo, Normal, PBR, WorldPos, Emissive, MotionVector, Tangent, MaterialData
        };

        public static RHIFormat GetFormat(GeometryBufferChannel ch) => ch switch
        {
            GeometryBufferChannel.Albedo => Albedo,
            GeometryBufferChannel.Normal => Normal,
            GeometryBufferChannel.PBR => PBR,
            GeometryBufferChannel.WorldPos => WorldPos,
            GeometryBufferChannel.Emissive => Emissive,
            GeometryBufferChannel.MotionVector => MotionVector,
            GeometryBufferChannel.Tangent => Tangent,
            GeometryBufferChannel.MaterialData => MaterialData,
            _ => RHIFormat.R8G8B8A8_UNorm
        };

        public static string GetName(GeometryBufferChannel ch) => ch switch
        {
            GeometryBufferChannel.Albedo => "GeometryBuffer0_Albedo",
            GeometryBufferChannel.Normal => "GeometryBuffer1_Normal",
            GeometryBufferChannel.PBR => "GeometryBuffer2_PBR",
            GeometryBufferChannel.WorldPos => "GeometryBuffer3_WorldPos",
            GeometryBufferChannel.Emissive => "GeometryBuffer4_Emissive",
            GeometryBufferChannel.MotionVector => "GeometryBuffer5_MotionVec",
            GeometryBufferChannel.Tangent => "GeometryBuffer6_Tangent",
            GeometryBufferChannel.MaterialData => "GeometryBuffer7_MaterialData",
            _ => "Unknown"
        };

        public static ulong EstimateSizeBytes(int width, int height, int enabledChannels)
        {
            ulong bpp = 0;
            if (enabledChannels > 0) bpp += 4;
            if (enabledChannels > 1) bpp += 4;
            if (enabledChannels > 2) bpp += 4;
            if (enabledChannels > 3) bpp += 8;
            if (enabledChannels > 4) bpp += 4;
            if (enabledChannels > 5) bpp += 8;
            if (enabledChannels > 6) bpp += 4;
            if (enabledChannels > 7) bpp += 4;
            return (ulong)width * (ulong)height * bpp;
        }
    }

    /// <summary>GeometryBuffer 像素数据（从 GPU 读取回来的完整数据）。</summary>
    [StructLayout(LayoutKind.Sequential, Size = 128)]
    public struct GeometryBufferPixel
    {
        public Vector4 Albedo;
        public Vector4 Normal;
        public Vector4 PBR;
        public Vector4 WorldPos;
        public Vector4 Emissive;
        public Vector4 MotionVector;
        public Vector4 Tangent;
        public Vector4 MaterialData;
        public Vector3 DecodedNormal => Vector3.Normalize(new Vector3(Normal.X, Normal.Y, Normal.Z) * 2.0f - Vector3.One);
        public float Roughness => PBR.X;
        public float Metalness => PBR.Y;
        public float AO => PBR.Z;
        public ShadingModel ShadingModelType => (ShadingModel)(PBR.W * 255.0f);
        public bool IsValid => WorldPos.W > 0.0f;
    }

    /// <summary>GeometryBuffer 生成常量缓冲。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct GeometryBufferGenCB
    {
        public Matrix4x4 ViewProj;
        public Matrix4x4 PrevViewProj;
        public Matrix4x4 InvViewProj;
        public Vector3 CameraPos;
        public float EnableChannelsMask;
        public int OutputWidth;
        public int OutputHeight;
        public float NormalEncodeMethod;
        public float RoughnessBias;
        public float RoughnessScale;
        public float MetallicBias;
        public float MetallicScale;
        public Vector3 Padding;
        public int EncodeMotionVectors;
    }

    /// <summary>Per-Object GeometryBuffer 常量。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct GeometryBufferObjectCB
    {
        public Matrix4x4 World;
        public Matrix4x4 InvWorld;
        public Matrix4x4 WorldViewProj;
        public Matrix4x4 PrevWorldViewProj;
        public uint ObjectId;
        public uint MaterialId;
        public byte ShadingModelByte;
        public byte MaterialFlagsByte;
        public ushort Padding;
    }

    // ────────────────────────────────────────────────────────────────────────
    // 法线编解码
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>法线编码方法。</summary>
    public enum NormalEncodeMethod
    {
        Octahedral,
        SphereMap,
        TwoChannel
    }

    /// <summary>法线编解码工具类。</summary>
    public static class NormalCodec
    {
        /// <summary>Octahedral 编码：把单位法线压缩到 2 个通道。</summary>
        public static Vector2 EncodeOctahedral(Vector3 normal)
        {
            // 把法线映射到八面体表面再展开到 2D
            // 参考: Octahedron Normal Vector Encoding, Cigolle et al. 2014
            float n = normal.X + normal.Y + normal.Z;
            if (n < 0)
            {
                normal.X = (1.0f - MathF.Abs(normal.Y)) * (normal.X >= 0 ? 1.0f : -1.0f);
                normal.Y = (1.0f - MathF.Abs(normal.X)) * (normal.Y >= 0 ? 1.0f : -1.0f);
            }
            float invN = 1.0f / MathF.Abs(n);
            return new Vector2(normal.X * invN * 0.5f + 0.5f, normal.Y * invN * 0.5f + 0.5f);
        }

        /// <summary>Octahedral 解码。</summary>
        public static Vector3 DecodeOctahedral(Vector2 enc)
        {
            Vector2 oct = enc * 2.0f - Vector2.One;
            Vector3 n = new Vector3(oct.X, oct.Y, 1.0f - MathF.Abs(oct.X) - MathF.Abs(oct.Y));
            float t = Math.Max(-n.Z, 0.0f);
            n.X += n.X >= 0 ? -t : t;
            n.Y += n.Y >= 0 ? -t : t;
            return Vector3.Normalize(n);
        }

        /// <summary>SphereMap 编码。</summary>
        public static Vector2 EncodeSphereMap(Vector3 normal)
        {
            float invSqrt = 1.0f / MathF.Sqrt(2.0f * (normal.Z + 1.0f));
            return new Vector2(normal.X * invSqrt * 0.5f + 0.5f, normal.Y * invSqrt * 0.5f + 0.5f);
        }

        /// <summary>SphereMap 解码。</summary>
        public static Vector3 DecodeSphereMap(Vector2 enc)
        {
            Vector2 sm = enc * 2.0f - Vector2.One;
            float rr = sm.X * sm.X + sm.Y * sm.Y;
            float invRr = 1.0f / (2.0f * MathF.Sqrt(MathF.Max(1.0f - rr, 1e-6f)));
            return new Vector3(sm.X * invRr, sm.Y * invRr, MathF.Max(0.0f, 1.0f - rr));
        }

        /// <summary>TwoChannel 编码（最简单，只存 XY，Z 从符号重建）。</summary>
        public static Vector2 EncodeTwoChannel(Vector3 normal)
        {
            return new Vector2(normal.X * 0.5f + 0.5f, normal.Y * 0.5f + 0.5f);
        }

        /// <summary>TwoChannel 解码。</summary>
        public static Vector3 DecodeTwoChannel(Vector2 enc, float zSign = 1.0f)
        {
            float x = enc.X * 2.0f - 1.0f;
            float y = enc.Y * 2.0f - 1.0f;
            float zSq = MathF.Max(0.0f, 1.0f - x * x - y * y);
            return new Vector3(x, y, MathF.Sqrt(zSq) * zSign);
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // GeometryBuffer 配置
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>GeometryBuffer 生成器配置。</summary>
    public sealed class GeometryBufferConfig
    {
        /// <summary>启用的通道掩码（bit 0-7）。</summary>
        public byte EnabledChannelsMask = 0xFF; // 默认全部启用

        /// <summary>法线编码方法。</summary>
        public NormalEncodeMethod NormalEncode = NormalEncodeMethod.Octahedral;

        /// <summary>写入世界坐标（调试用，生产环境通常关掉）。</summary>
        public bool WriteWorldPosition = true;

        /// <summary>写入运动矢量。</summary>
        public bool WriteMotionVectors = true;

        /// <summary>写入 Emissive。</summary>
        public bool WriteEmissive = true;

        /// <summary>写入 Tangent（用于各向异性材质）。</summary>
        public bool WriteTangent = false;

        /// <summary>写入额外材质数据。</summary>
        public bool WriteMaterialData = false;

        /// <summary>Roughness 偏移。</summary>
        public float RoughnessBias = 0.0f;

        /// <summary>Roughness 缩放。</summary>
        public float RoughnessScale = 1.0f;

        /// <summary>Metalness 偏移。</summary>
        public float MetallicBias = 0.0f;

        /// <summary>Metalness 缩放。</summary>
        public float MetallicScale = 1.0f;

        /// <summary>Debug 可视化模式。</summary>
        public GeometryBufferDebugMode DebugMode = GeometryBufferDebugMode.None;

        /// <summary>生成最小 GeometryBuffer 配置（只 3 个目标）。</summary>
        public static GeometryBufferConfig Minimal => new()
        {
            EnabledChannelsMask = 0x07, // Albedo + Normal + PBR
            WriteWorldPosition = false,
            WriteMotionVectors = false,
            WriteEmissive = false,
            WriteTangent = false,
            WriteMaterialData = false
        };

        /// <summary>生成标准 GeometryBuffer 配置（5 个目标）。</summary>
        public static GeometryBufferConfig Standard => new()
        {
            EnabledChannelsMask = 0x1F, // Albedo + Normal + PBR + WorldPos + Emissive
            WriteWorldPosition = true,
            WriteMotionVectors = false,
            WriteEmissive = true,
            WriteTangent = false,
            WriteMaterialData = false
        };

        /// <summary>生成完整 GeometryBuffer 配置（全部 8 个目标）。</summary>
        public static GeometryBufferConfig Full => new()
        {
            EnabledChannelsMask = 0xFF,
            WriteWorldPosition = true,
            WriteMotionVectors = true,
            WriteEmissive = true,
            WriteTangent = true,
            WriteMaterialData = true
        };
    }

    /// <summary>GeometryBuffer Debug 可视化模式。</summary>
    public enum GeometryBufferDebugMode
    {
        None,
        Albedo,
        Normal,
        Roughness,
        Metalness,
        AO,
        WorldPosition,
        Emissive,
        MotionVector,
        Tangent,
        MaterialID,
        ShadingModel,
        Depth
    }

    // ────────────────────────────────────────────────────────────────────────
    // GeometryBuffer 资源集
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>一组 GeometryBuffer 渲染目标资源。</summary>
    public sealed class GeometryBufferResources : IDisposable
    {
        private readonly RHIDevice _device;
        private readonly GeometryBufferConfig _config;
        private RHITexture[] _targets = Array.Empty<RHITexture>();
        private RHITexture? _depthTarget;
        private int _width, _height;
        private bool _disposed;

        public RHITexture DepthTarget => _depthTarget!;
        public int Width => _width;
        public int Height => _height;
        public int ActiveTargetCount { get; private set; }

        /// <summary>按通道索引获取渲染目标。</summary>
        public RHITexture this[int index] => _targets[index];

        /// <summary>按枚举获取渲染目标。</summary>
        public RHITexture GetTarget(GeometryBufferChannel ch) => _targets[(int)ch];

        /// <summary>获取启用的渲染目标数组（不含 null）。</summary>
        public RHITexture[] GetActiveTargets()
        {
            var list = new List<RHITexture>();
            for (int i = 0; i < 8; i++)
            {
                if ((_config.EnabledChannelsMask & (1 << i)) != 0 && _targets[i] != null)
                    list.Add(_targets[i]);
            }
            return list.ToArray();
        }

        /// <summary>获取启用的渲染目标格式。</summary>
        public RHIFormat[] GetActiveFormats()
        {
            var list = new List<RHIFormat>();
            for (int i = 0; i < 8; i++)
            {
                if ((_config.EnabledChannelsMask & (1 << i)) != 0)
                    list.Add(GeometryBufferFormats.AllFormats[i]);
            }
            return list.ToArray();
        }

        public GeometryBufferResources(RHIDevice device, GeometryBufferConfig config)
        {
            _device = device;
            _config = config;
        }

        /// <summary>创建/重建所有 GeometryBuffer 纹理。</summary>
        public void Create(int width, int height)
        {
            ReleaseTargets();
            _width = width;
            _height = height;
            _targets = new RHITexture[GeometryBufferFormats.MaxTargets];

            ActiveTargetCount = 0;
            for (int i = 0; i < GeometryBufferFormats.MaxTargets; i++)
            {
                if ((_config.EnabledChannelsMask & (1 << i)) == 0) continue;
                var fmt = GeometryBufferFormats.AllFormats[i];
                _targets[i] = _device.CreateTexture(new RHITextureDesc
                {
                    Format = fmt,
                    Width = width,
                    Height = height,
                    Flags = RHITextureFlags.AllowRenderTarget | RHITextureFlags.AllowShaderResource
                });
                ActiveTargetCount++;
            }

            _depthTarget = _device.CreateTexture(new RHITextureDesc
            {
                Format = RHIFormat.D32_Float,
                Width = width,
                Height = height,
                Flags = RHITextureFlags.AllowDepthStencil | RHITextureFlags.AllowShaderResource
            });
        }

        /// <summary>清除所有 GeometryBuffer。</summary>
        public void Clear(RHICommandList cmd)
        {
            for (int i = 0; i < GeometryBufferFormats.MaxTargets; i++)
            {
                if (_targets[i] != null)
                    cmd.ClearRenderTarget(_targets[i], Vector4.Zero);
            }
            if (_depthTarget != null)
                cmd.ClearDepthStencil(_depthTarget, 1.0f, 0);
        }

        /// <summary>估算总显存占用。</summary>
        public ulong EstimateMemoryBytes()
        {
            return GeometryBufferFormats.EstimateSizeBytes(_width, _height, ActiveTargetCount) +
                   (ulong)(_width * _height * 4); // depth
        }

        private void ReleaseTargets()
        {
            foreach (var t in _targets)
                t?.Dispose();
            _depthTarget?.Dispose();
            _targets = Array.Empty<RHITexture>();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            ReleaseTargets();
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // GeometryBuffer Pipeline 管理
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>GeometryBuffer Pipeline 变体（根据启用的通道组合缓存不同的 PSO）。</summary>
    public sealed class GeometryBufferPipelineCache
    {
        private readonly RHIDevice _device;
        private readonly Dictionary<byte, RHIPipeline> _pipelineCache = new();
        private RHIPipeline? _fallbackPipeline;
        private RHIShader? _geometrybufferVS;
        private RHIShader? _geometrybufferSkinnedVS;

        public GeometryBufferPipelineCache(RHIDevice device)
        {
            _device = device;
            // 加载 GeometryBuffer vertex/pixel shaders
            // NOTE: 实际从 shader 编译系统加载
        }

        /// <summary>根据通道掩码获取或创建对应的 pipeline。</summary>
        public RHIPipeline GetPipeline(byte channelMask, RHIFormat[] activeFormats, RHIFormat depthFormat,
            bool twoSided = false, bool alphaTest = false)
        {
            // 用 channelMask + twoSided + alphaTest 组成缓存 key
            byte key = channelMask;
            if (twoSided) key |= 0x80;
            if (alphaTest) key |= 0x40;

            if (_pipelineCache.TryGetValue(key, out var cached))
                return cached;

            var desc = new RHIGraphicPipeDescriptor
            {
                Name = $"GeometryBuffer_Mask{channelMask:X2}{(twoSided ? "_2Sided" : "")}{(alphaTest ? "_AlphaTest" : "")}",
                ColorFormats = activeFormats,
                DepthStencilFormat = depthFormat,
                DepthEnable = true,
                DepthWrite = true,
                DepthCompare = RHICompareFunction.Less,
                CullMode = twoSided ? RHICullMode.None : RHICullMode.Back,
                AlphaTestEnable = alphaTest
            };

            var pipeline = _device.CreateGraphicsPipeline(desc);
            _pipelineCache[key] = pipeline;
            return pipeline;
        }

        /// <summary>获取 Skinned Mesh 的 GeometryBuffer pipeline。</summary>
        public RHIPipeline GetSkinnedPipeline(byte channelMask, RHIFormat[] activeFormats, RHIFormat depthFormat)
        {
            // 类似 GetPipeline，但用 skinned VS
            return GetPipeline(channelMask, activeFormats, depthFormat);
        }

        /// <summary>清除所有缓存的 pipeline。</summary>
        public void ClearCache()
        {
            foreach (var kv in _pipelineCache)
                kv.Value.Dispose();
            _pipelineCache.Clear();
        }

        public int CachedCount => _pipelineCache.Count;
    }

    // ────────────────────────────────────────────────────────────────────────
    // GeometryBuffer 生成器主类
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>GeometryBuffer 生成统计。</summary>
    public struct GeometryBufferStats
    {
        public int DrawCallCount;
        public int TriangleCount;
        public int ActiveTargets;
        public double GenerateTimeMs;
        public double ClearTimeMs;
        public ulong MemoryBytes;
        public int SkinnedMeshCount;
        public int StaticMeshCount;
        public int TwoSidedCount;
        public int AlphaTestCount;
    }

    /// <summary>
    /// yama GeometryBuffer 生成器。
    /// 支持 MRT 8 目标的延迟渲染 GeometryBuffer 填充。
    /// </summary>
    public sealed class GeometryBufferGenerator : IDisposable
    {
        private readonly RHIDevice _device;
        private readonly GeometryBufferConfig _config;
        private readonly GeometryBufferResources _resources;
        private readonly GeometryBufferPipelineCache _pipelineCache;
        private RHIBuffer? _genCB;
        private RHIBuffer? _objectCB;
        private bool _disposed;

        /// <summary>GeometryBuffer 资源集。</summary>
        public GeometryBufferResources Resources => _resources;
        public GeometryBufferConfig Config => _config;
        public GeometryBufferStats LastStats { get; private set; }

        public GeometryBufferGenerator(RHIDevice device, GeometryBufferConfig? config = null)
        {
            _device = device;
            _config = config ?? new GeometryBufferConfig();
            _resources = new GeometryBufferResources(device, _config);
            _pipelineCache = new GeometryBufferPipelineCache(device);

            _genCB = _device.CreateBuffer(256, 256, RHIBufferUsage.ConstantBuffer);
            _objectCB = _device.CreateBuffer(320, 256, RHIBufferUsage.ConstantBuffer);
        }

        /// <summary>初始化/调整 GeometryBuffer 尺寸。</summary>
        public void Resize(int width, int height)
        {
            _resources.Create(width, height);
        }

        /// <summary>填充 GeometryBuffer。</summary>
        public void Generate(RHICommandList cmd, Matrix4x4 viewProj, Matrix4x4 prevViewProj,
            Matrix4x4 invViewProj, Vector3 cameraPos, RenderMeshInstance[] meshes)
        {
            if (_resources.Width == 0 || _resources.Height == 0)
                throw new InvalidOperationException("GeometryBuffer 未初始化，请先调用 Resize()。");

            var sw = Stopwatch.StartNew();
            var stats = new GeometryBufferStats { ActiveTargets = _resources.ActiveTargetCount, MemoryBytes = _resources.EstimateMemoryBytes() };

            // 1. 清除 GeometryBuffer
            var clearSw = Stopwatch.StartNew();
            _resources.Clear(cmd);
            clearSw.Stop();
            stats.ClearTimeMs = clearSw.Elapsed.TotalMilliseconds;

            // 2. 设置渲染目标
            var activeTargets = _resources.GetActiveTargets();
            var activeFormats = _resources.GetActiveFormats();
            cmd.SetRenderTargets(activeTargets, _resources.DepthTarget);
            cmd.SetViewport(0, 0, _resources.Width, _resources.Height);
            cmd.SetScissorRect(0, 0, _resources.Width, _resources.Height);

            // 3. 更新全局 CB
            var genCB = new GeometryBufferGenCB
            {
                ViewProj = viewProj,
                PrevViewProj = prevViewProj,
                InvViewProj = invViewProj,
                CameraPos = cameraPos,
                EnableChannelsMask = _config.EnabledChannelsMask,
                OutputWidth = _resources.Width,
                OutputHeight = _resources.Height,
                NormalEncodeMethod = (float)_config.NormalEncode,
                RoughnessBias = _config.RoughnessBias,
                RoughnessScale = _config.RoughnessScale,
                MetallicBias = _config.MetallicBias,
                MetallicScale = _config.MetallicScale,
                EncodeMotionVectors = _config.WriteMotionVectors ? 1 : 0
            };
            cmd.UpdateBuffer(_genCB!, ref genCB);
            cmd.SetGraphicsRootConstantBuffer(0, _genCB);

            // 4. 按材质分组渲染（减少 PSO 切换）
            // 先静态后 skinned，再 two-sided，再 alpha-test
            var staticMeshes = new List<RenderMeshInstance>();
            var skinnedMeshes = new List<RenderMeshInstance>();
            var twoSidedMeshes = new List<RenderMeshInstance>();
            var alphaTestMeshes = new List<RenderMeshInstance>();

            foreach (var m in meshes)
            {
                if (!m.Visible || m.Mesh == null) continue;
                if (m.IsTransparent) continue;

                bool twoSided = (m.Material?.TwoSided ?? false);
                bool alphaTest = (m.Material?.AlphaTest ?? false);

                if (alphaTest) alphaTestMeshes.Add(m);
                else if (twoSided) twoSidedMeshes.Add(m);
                else if (m.IsSkinned) skinnedMeshes.Add(m);
                else staticMeshes.Add(m);
            }

            // 获取 pipeline
            var pipeline = _pipelineCache.GetPipeline(_config.EnabledChannelsMask, activeFormats, RHIFormat.D32_Float);
            cmd.SetGraphicsPipeline(pipeline);

            // 渲染静态网格
            stats.StaticMeshCount = staticMeshes.Count;
            foreach (var m in staticMeshes)
            {
                DrawGeometryBufferMesh(cmd, m, viewProj, prevViewProj);
                stats.DrawCallCount++;
                stats.TriangleCount += m.Mesh!.IndexCount / 3;
            }

            // 渲染 Skinned 网格
            stats.SkinnedMeshCount = skinnedMeshes.Count;
            if (skinnedMeshes.Count > 0)
            {
                var skinnedPipeline = _pipelineCache.GetSkinnedPipeline(_config.EnabledChannelsMask, activeFormats, RHIFormat.D32_Float);
                cmd.SetGraphicsPipeline(skinnedPipeline);
                foreach (var m in skinnedMeshes)
                {
                    DrawGeometryBufferMesh(cmd, m, viewProj, prevViewProj);
                    stats.DrawCallCount++;
                    stats.TriangleCount += m.Mesh!.IndexCount / 3;
                }
            }

            // 渲染 Two-Sided
            stats.TwoSidedCount = twoSidedMeshes.Count;
            if (twoSidedMeshes.Count > 0)
            {
                var twoSidedPipeline = _pipelineCache.GetPipeline(_config.EnabledChannelsMask, activeFormats, RHIFormat.D32_Float, twoSided: true);
                cmd.SetGraphicsPipeline(twoSidedPipeline);
                foreach (var m in twoSidedMeshes)
                {
                    DrawGeometryBufferMesh(cmd, m, viewProj, prevViewProj);
                    stats.DrawCallCount++;
                    stats.TriangleCount += m.Mesh!.IndexCount / 3;
                }
            }

            // 渲染 Alpha Test
            stats.AlphaTestCount = alphaTestMeshes.Count;
            if (alphaTestMeshes.Count > 0)
            {
                var alphaTestPipeline = _pipelineCache.GetPipeline(_config.EnabledChannelsMask, activeFormats, RHIFormat.D32_Float, alphaTest: true);
                cmd.SetGraphicsPipeline(alphaTestPipeline);
                foreach (var m in alphaTestMeshes)
                {
                    DrawGeometryBufferMesh(cmd, m, viewProj, prevViewProj);
                    stats.DrawCallCount++;
                    stats.TriangleCount += m.Mesh!.IndexCount / 3;
                }
            }

            sw.Stop();
            stats.GenerateTimeMs = sw.Elapsed.TotalMilliseconds;
            LastStats = stats;
        }

        private void DrawGeometryBufferMesh(RHICommandList cmd, RenderMeshInstance m, Matrix4x4 viewProj, Matrix4x4 prevViewProj)
        {
            // 绑定 per-object CB
            var objCB = new GeometryBufferObjectCB
            {
                World = m.WorldMatrix,
                InvWorld = Matrix4x4Utility.Inverse(m.WorldMatrix),
                WorldViewProj = m.WorldMatrix * viewProj,
                PrevWorldViewProj = m.PrevWorldMatrix * prevViewProj,
                ObjectId = (uint)m.ObjectId,
                MaterialId = (uint)(m.Material?.MaterialId ?? 0),
                ShadingModelByte = (byte)(m.Material?.ShadingModel ?? ShadingModel.DefaultLit),
                MaterialFlagsByte = (byte)(GeometryBufferMaterialFlags.Opaque | GeometryBufferMaterialFlags.CastShadow | GeometryBufferMaterialFlags.ReceiveShadow)
            };
            cmd.SetGraphicsRoot32BitConstants(1, ref objCB);

            // 绑定材质纹理
            if (m.Material != null)
            {
                if (m.Material.AlbedoTexture != null)
                    cmd.SetShaderResourceView(ShaderStage.Pixel, 0, m.Material.AlbedoTexture);
                if (m.Material.NormalTexture != null)
                    cmd.SetShaderResourceView(ShaderStage.Pixel, 1, m.Material.NormalTexture);
                if (m.Material.RoughnessTexture != null)
                    cmd.SetShaderResourceView(ShaderStage.Pixel, 2, m.Material.RoughnessTexture);
                if (m.Material.MetallicTexture != null)
                    cmd.SetShaderResourceView(ShaderStage.Pixel, 3, m.Material.MetallicTexture);
                if (m.Material.AOTexture != null)
                    cmd.SetShaderResourceView(ShaderStage.Pixel, 4, m.Material.AOTexture);
                if (m.Material.EmissiveTexture != null)
                    cmd.SetShaderResourceView(ShaderStage.Pixel, 5, m.Material.EmissiveTexture);
            }

            if (m.Mesh!.VertexBuffer != null)
                cmd.SetVertexBuffer(0, m.Mesh.VertexBuffer, m.Mesh.VertexStride);
            if (m.Mesh.IndexBuffer != null)
                cmd.SetIndexBuffer(m.Mesh.IndexBuffer, RHIFormat.R32_UInt);

            cmd.DrawIndexed(m.Mesh.IndexCount, 1, 0, 0, 0);
        }

        // ──────────────────────────────────────────────────────────────────
        // Debug 可视化
        // ──────────────────────────────────────────────────────────────────

        /// <summary>生成 Debug 可视化的颜色到目标纹理。</summary>
        public void DebugVisualize(RHICommandList cmd, GeometryBufferDebugMode mode, RHITexture output)
        {
            if (mode == GeometryBufferDebugMode.None) return;

            // 根据 mode 采样对应的 GeometryBuffer RT 并输出到 output
            // 实际实现需要 compute shader 做 channel swizzle
            // 这里只做标记
            RHITexture? source = mode switch
            {
                GeometryBufferDebugMode.Albedo => _resources.GetTarget(GeometryBufferChannel.Albedo),
                GeometryBufferDebugMode.Normal => _resources.GetTarget(GeometryBufferChannel.Normal),
                GeometryBufferDebugMode.Roughness => _resources.GetTarget(GeometryBufferChannel.PBR),
                GeometryBufferDebugMode.Metalness => _resources.GetTarget(GeometryBufferChannel.PBR),
                GeometryBufferDebugMode.AO => _resources.GetTarget(GeometryBufferChannel.PBR),
                GeometryBufferDebugMode.WorldPosition => _resources.GetTarget(GeometryBufferChannel.WorldPos),
                GeometryBufferDebugMode.Emissive => _resources.GetTarget(GeometryBufferChannel.Emissive),
                GeometryBufferDebugMode.MotionVector => _resources.GetTarget(GeometryBufferChannel.MotionVector),
                GeometryBufferDebugMode.Tangent => _resources.GetTarget(GeometryBufferChannel.Tangent),
                GeometryBufferDebugMode.MaterialID => _resources.GetTarget(GeometryBufferChannel.MaterialData),
                GeometryBufferDebugMode.ShadingModel => _resources.GetTarget(GeometryBufferChannel.PBR),
                GeometryBufferDebugMode.Depth => _resources.DepthTarget,
                _ => null
            };

            if (source == null) return;
            // 执行 blit
            cmd.CopyResource(output, source);
        }

        /// <summary>获取 GeometryBuffer 统计信息。</summary>
        public string GetStatsString()
        {
            var s = LastStats;
            return $"GeometryBuffer: {s.DrawCallCount} draws, {s.TriangleCount} tris, " +
                   $"{s.ActiveTargets} RTs, {s.MemoryBytes / 1024.0 / 1024.0:F1}MB, " +
                   $"gen={s.GenerateTimeMs:F2}ms clear={s.ClearTimeMs:F2}ms";
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _resources.Dispose();
            _genCB?.Dispose();
            _objectCB?.Dispose();
            _pipelineCache.ClearCache();
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // GeometryBuffer 读取工具
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>GeometryBuffer CPU 端读取器（用于编辑器 pick、debug 等）。</summary>
    public sealed class GeometryBufferReader
    {
        private readonly RHIDevice _device;
        private byte[] _readBackBuffer;
        private int _width, _height;
        private int _bytesPerPixel;

        public GeometryBufferReader(RHIDevice device)
        {
            _device = device;
            _readBackBuffer = Array.Empty<byte>();
        }

        /// <summary>初始化读取缓冲区大小。</summary>
        public void Init(int width, int height, int channels)
        {
            _width = width;
            _height = height;
            _bytesPerPixel = channels * 4;
            _readBackBuffer = new byte[width * height * _bytesPerPixel];
        }

        /// <summary>从 GPU 读取 GeometryBuffer 数据（注意：会 stall GPU！）。</summary>
        public void ReadBack(RHICommandList cmd, GeometryBufferResources resources)
        {
            // NOTE: 这个操作会造成 GPU stall，只在 debug/编辑器场景使用
            // 生产环境不要调用这个
            var activeTargets = resources.GetActiveTargets();
            int offset = 0;
            foreach (var tex in activeTargets)
            {
                cmd.ReadTexture(tex, _readBackBuffer, offset);
                offset += _width * _height * 4;
            }
        }

        /// <summary>读取指定像素的 GeometryBuffer 数据。</summary>
        public unsafe GeometryBufferPixel ReadPixel(int x, int y)
        {
            var pixel = new GeometryBufferPixel();
            if (x < 0 || x >= _width || y < 0 || y >= _height)
                return pixel;

            int rowPitch = _width * 4;
            int pixelOffset = (y * rowPitch + x * 4);

            fixed (byte* ptr = _readBackBuffer)
            {
                int offset = pixelOffset;
                // RT0: Albedo
                if (offset + 16 <= _readBackBuffer.Length)
                {
                    pixel.Albedo = new Vector4(
                        ptr[offset] / 255.0f, ptr[offset + 1] / 255.0f,
                        ptr[offset + 2] / 255.0f, ptr[offset + 3] / 255.0f);
                }
                offset += rowPitch * _height;
                // RT1: Normal (simplified read)
                // ... 其他通道类似
            }
            return pixel;
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // GeometryBuffer 压缩工具
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>GeometryBuffer 压缩（把全精度 GeometryBuffer 压缩后存到磁盘或传输）。</summary>
    public static class GeometryBufferCompression
    {
        /// <summary>压缩 Albedo 通道（BC7 压缩，约 4:1）。</summary>
        public static void CompressAlbedo(RHITexture source, RHITexture dest)
        {
            // 使用 GPU BC7 压缩
            // NOTE: 实际调用 device.CompressTexture 或 compute shader
        }

        /// <summary>压缩 Normal 通道（BC6H，适合 HDR 法线）。</summary>
        public static void CompressNormals(RHITexture source, RHITexture dest)
        {
            // BC6H 压缩
        }

        /// <summary>估算压缩后的数据量。</summary>
        public static ulong EstimateCompressedSize(int width, int height, int channels)
        {
            // BC7: 1 byte/pixel (4:1 from 4bpp)
            // BC6H: 1 byte/pixel (8:1 from 8bpp half)
            ulong albedoCompressed = (ulong)(width * height);  // BC7
            ulong normalCompressed = (ulong)(width * height);  // BC6H
            ulong pbrUncompressed = (ulong)(width * height * 4); // 不压缩
            return albedoCompressed + normalCompressed + pbrUncompressed;
        }
    }
}