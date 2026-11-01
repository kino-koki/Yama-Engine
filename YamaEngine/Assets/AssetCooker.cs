using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using YamaEngine.Audio;
using YamaEngine.Materials;
using YamaEngine.RHI;
using YamaEngine.Rendering;

using RHIFormat = YamaEngine.RHI.RHIFormat;
namespace YamaEngine.Assets
{
    // ════════════════════════════════════════════════════════════════════════
    // yama引擎资源 Cook 系统 (AssetCooker)
    //
    // 职责: 将编辑器导入的原始资产转换为平台最优、GPU 就绪的运行时格式。
    // 涵盖: 纹理 Cook (Mipmap/压缩/sRGB/Alpha 分离/图集)、
    //       网格 Cook (顶点重排序/索引优化/LOD/顶点压缩/MicroCluster 预处理)、
    //       音频 Cook (采样率转换/Vorbis-Opus 编码/流式标记)、
    //       动画 Cook (压缩/关键帧减少/均匀采样)、
    //       材质 Cook (着色器变体预编译/参数烘焙)。
    // 同时提供: 资源依赖序列化、平台路径映射、资源签名与完整性校验。
    //
    // 该系统作为构建管线 (AdvancedBuildPipeline) 的核心 Cook 阶段被调用,
    // 也可在编辑器中独立运行以预览资产处理结果。
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 目标平台枚举。与构建管线的 <c>TargetPlatform</c> 保持一致,
    /// 用于为不同平台选择最优的压缩格式与处理策略。
    /// </summary>
    public enum CookTargetPlatform
    {
        /// <summary>Windows 桌面 (D3D12 / Vulkan)。</summary>
        Windows,
        /// <summary>Linux 桌面 (Vulkan)。</summary>
        Linux,
        /// <summary>Android 移动 (Vulkan)。</summary>
        Android,
        /// <summary>Apple iOS 移动 (Metal)。</summary>
        iOS,
        /// <summary>Apple macOS 桌面 (Metal)。</summary>
        macOS,
        /// <summary>WebAssembly 浏览器 (WebGPU)。</summary>
        WebAssembly
    }

    /// <summary>
    /// 纹理压缩格式。不同平台硬件支持的块压缩格式不同,
    /// Cook 阶段需根据 <see cref="CookTargetPlatform"/> 选择最优格式。
    /// </summary>
    public enum TextureCompressionFormat
    {
        /// <summary>不压缩, 保留原始 RGBA8 (用于 UI / 调试)。</summary>
        None,
        /// <summary>BC1 / DXT1: 4bpp, 无 Alpha, 适用于漫反射颜色贴图。</summary>
        BC1,
        /// <summary>BC3 / DXT5: 8bpp, 显式 Alpha, 适用于需要 Alpha 通道的贴图。</summary>
        BC3,
        /// <summary>BC4: 4bpp, 单通道, 适用于高度图 / 蒙版图。</summary>
        BC4,
        /// <summary>BC5: 8bpp, 双通道, 适用于法线图 (切线空间 X/Y)。</summary>
        BC5,
        /// <summary>BC6H: 8bpp, HDR 浮点, 适用于 HDR 环境贴图 / 天空盒。</summary>
        BC6H,
        /// <summary>BC7: 8bpp, 高质量 RGBA, 适用于高质量颜色贴图。</summary>
        BC7,
        /// <summary>ASTC 4x4: 8bpp, 移动端高质量通用格式 (iOS / Android)。</summary>
        ASTC_4x4,
        /// <summary>ASTC 6x6: 3.6bpp, 移动端平衡质量与体积。</summary>
        ASTC_6x6,
        /// <summary>ASTC 8x8: 2bpp, 移动端低质量小体积。</summary>
        ASTC_8x8,
        /// <summary>ETC2 RGB: 4bpp, Android 基准格式。</summary>
        ETC2_RGB,
        /// <summary>ETC2 RGBA: 8bpp, Android 基准格式带 Alpha。</summary>
        ETC2_RGBA
    }

    /// <summary>
    /// 顶点格式压缩级别。决定顶点属性占用的位宽,
    /// 在视觉质量与带宽/显存占用之间权衡。
    /// </summary>
    public enum VertexCompressionLevel
    {
        /// <summary>无压缩: 位置 R32G32B32_Float, 法线 R32G32B32_Float, UV R32G32_Float。</summary>
        None,
        /// <summary>轻度: 位置 R16G16B16A16_Float, 法线八面体 R16G16_SNorm, UV R16G16_Float。</summary>
        Light,
        /// <summary>中度: 位置 R10G10B10A2, 法线八面体 R8G8_SNorm, UV R16G16_Float。</summary>
        Medium,
        /// <summary>重度: 位置量化 R16G16B16, 法线八面体 16-bit packed, UV R8G8。</summary>
        Aggressive
    }

    /// <summary>音频编码格式。</summary>
    public enum AudioCodecFormat
    {
        /// <summary>PCM 无损 (短音效)。</summary>
        Pcm,
        /// <summary>Vorbis (OGG): 高质量有损, 适用于音乐 / 环境音。</summary>
        Vorbis,
        /// <summary>Opus: 低延迟有损, 适用于语音 / 实时通信。</summary>
        Opus,
        /// <summary>ADPCM: 中等压缩低延迟, 适用于频繁播放的 SFX。</summary>
        Adpcm
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Cook 配置类
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 纹理 Cook 配置。描述单张纹理在 Cook 阶段的处理参数。
    /// </summary>
    public sealed class TextureCookSettings
    {
        /// <summary>是否生成 Mipmap 链。UI 贴图通常关闭, 3D 贴图开启。</summary>
        public bool GenerateMipmaps { get; set; } = true;
        /// <summary>最大 Mipmap 层级数 (0 = 自动)。</summary>
        public int MaxMipLevels { get; set; } = 0;
        /// <summary>是否为 sRGB 颜色空间贴图 (漫反射/自发光)。</summary>
        public bool Srgb { get; set; } = true;
        /// <summary>是否分离 Alpha 通道为独立纹理 (避免 BC1 无 Alpha 问题)。</summary>
        public bool SplitAlpha { get; set; } = false;
        /// <summary>是否参与纹理图集打包。</summary>
        public bool AtlasCandidate { get; set; } = false;
        /// <summary>指定的压缩格式 (None = 按平台自动选择)。</summary>
        public TextureCompressionFormat CompressionFormat { get; set; } = TextureCompressionFormat.None;
        /// <summary>是否启用各向异性过滤 (影响采样器生成)。</summary>
        public bool AnisotropicFiltering { get; set; } = false;
        /// <summary>Mipmap 生成时使用的过滤器 (Box / Kaiser)。</summary>
        public string MipmapFilter { get; set; } = "Box";
        /// <summary>纹理缩放边界 (最大分辨率, 超出则缩放)。</summary>
        public int MaxResolution { get; set; } = 4096;
    }

    /// <summary>
    /// 网格 Cook 配置。描述网格在 Cook 阶段的优化参数。
    /// </summary>
    public sealed class MeshCookSettings
    {
        /// <summary>是否对顶点缓冲进行缓存友好的重排序 (Forsyth / Tom Forsyth 算法)。</summary>
        public bool OptimizeVertexCache { get; set; } = true;
        /// <summary>是否对索引缓冲进行后变换缓存优化 (FIFO 模拟)。</summary>
        public bool OptimizeIndexBuffer { get; set; } = true;
        /// <summary>是否自动生成 LOD 链。</summary>
        public bool GenerateLod { get; set; } = true;
        /// <summary>LOD 生成数量 (包含 LOD0)。</summary>
        public int LodCount { get; set; } = 4;
        /// <summary>LOD 屏幕占比阈值 (三角形缩减比例序列)。</summary>
        public float[] LodScreenRatios { get; set; } = { 1.0f, 0.5f, 0.25f, 0.125f };
        /// <summary>顶点压缩级别。</summary>
        public VertexCompressionLevel Compression { get; set; } = VertexCompressionLevel.Light;
        /// <summary>是否启用 MicroCluster 虚拟几何体预处理 (簇化 + LOD DAG)。</summary>
        public bool EnableMicroClusterPreprocess { get; set; } = false;
        /// <summary>是否剔除退化三角形 (零面积)。</summary>
        public bool RemoveDegenerateTriangles { get; set; } = true;
        /// <summary>是否合并相同顶点 (welding 容差)。</summary>
        public float WeldThreshold { get; set; } = 1e-5f;
    }

    /// <summary>
    /// 音频 Cook 配置。描述音频资产在 Cook 阶段的转码参数。
    /// </summary>
    public sealed class AudioCookSettings
    {
        /// <summary>目标采样率 (Hz)。移动端常用 44100 或 48000。</summary>
        public int TargetSampleRate { get; set; } = 44100;
        /// <summary>目标声道数 (1 = 单声道, 2 = 立体声)。</summary>
        public int TargetChannels { get; set; } = 2;
        /// <summary>编码格式。</summary>
        public AudioCodecFormat Codec { get; set; } = AudioCodecFormat.Vorbis;
        /// <summary>编码质量 (0-100, 越高越好)。</summary>
        public int Quality { get; set; } = 70;
        /// <summary>是否标记为流式加载 (长音频)。</summary>
        public bool Streaming { get; set; } = false;
        /// <summary>流式加载的块大小 (字节, 0 = 自动)。</summary>
        public int StreamChunkSize { get; set; } = 0;
        /// <summary>是否启用循环无缝处理。</summary>
        public bool SeamlessLoop { get; set; } = false;
    }

    /// <summary>
    /// 动画 Cook 配置。描述动画剪辑在 Cook 阶段的压缩参数。
    /// </summary>
    public sealed class AnimationCookSettings
    {
        /// <summary>是否启用动画压缩。</summary>
        public bool EnableCompression { get; set; } = true;
        /// <summary>关键帧减少阈值 (误差容差, 单位 cm)。</summary>
        public float KeyframeReductionThreshold { get; set; } = 0.1f;
        /// <summary>是否使用均匀采样 (固定帧率)。</summary>
        public bool UniformSampling { get; set; } = true;
        /// <summary>均匀采样帧率 (fps)。</summary>
        public int UniformSampleRate { get; set; } = 30;
        /// <summary>是否启用线性插值简化 (将 Bezier 转为线性)。</summary>
        public bool LinearSimplification { get; set; } = true;
        /// <summary>是否保留根运动轨迹。</summary>
        public bool PreserveRootMotion { get; set; } = true;
    }

    /// <summary>
    /// 材质 Cook 配置。描述材质在 Cook 阶段的变体预编译参数。
    /// </summary>
    public sealed class MaterialCookSettings
    {
        /// <summary>是否预编译所有可能的着色器变体 (Permutation)。</summary>
        public bool PrecompileVariants { get; set; } = true;
        /// <summary>是否烘焙静态材质参数 (减少运行时 uniform 设置)。</summary>
        public bool BakeStaticParameters { get; set; } = true;
        /// <summary>是否将材质参数打包到常量缓冲。</summary>
        public bool PackToConstantBuffer { get; set; } = true;
        /// <summary>是否启用材质实例合并 (相同着色器的材质共享 PSO)。</summary>
        public bool MergeInstances { get; set; } = true;
        /// <summary>目标着色器模型 (决定 Permutation 编译的 SM 版本)。</summary>
        public string TargetShaderModel { get; set; } = "SM_6_6";
    }

    /// <summary>
    /// 平台 Cook 配置。针对单个目标平台的整体 Cook 策略,
    /// 包含纹理/网格/音频等的默认格式选择。
    /// </summary>
    public sealed class CookPlatformConfig
    {
        /// <summary>目标平台。</summary>
        public CookTargetPlatform Platform { get; set; }
        /// <summary>该平台推荐的默认纹理压缩格式。</summary>
        public TextureCompressionFormat DefaultTextureFormat { get; set; }
        /// <summary>该平台推荐的法线图压缩格式。</summary>
        public TextureCompressionFormat NormalMapFormat { get; set; }
        /// <summary>该平台推荐的 HDR 贴图压缩格式。</summary>
        public TextureCompressionFormat HdrFormat { get; set; }
        /// <summary>该平台推荐的音频编码格式。</summary>
        public AudioCodecFormat DefaultAudioCodec { get; set; }
        /// <summary>该平台默认采样率。</summary>
        public int DefaultSampleRate { get; set; } = 44100;
        /// <summary>该平台是否支持 MicroCluster 虚拟几何体。</summary>
        public bool SupportsMicroCluster { get; set; }
        /// <summary>该平台最大纹理分辨率。</summary>
        public int MaxTextureResolution { get; set; } = 4096;
        /// <summary>该平台是否支持网格着色器 (影响 MicroCluster 路径选择)。</summary>
        public bool SupportsMeshShaders { get; set; }

        /// <summary>
        /// 根据目标平台返回预设的 <see cref="CookPlatformConfig"/>。
        /// </summary>
        public static CookPlatformConfig ForPlatform(CookTargetPlatform platform)
        {
            return platform switch
            {
                // Windows / Linux 桌面: BCn 系列 + Vorbis, 支持 MicroCluster 与网格着色器
                CookTargetPlatform.Windows => new CookPlatformConfig
                {
                    Platform = platform,
                    DefaultTextureFormat = TextureCompressionFormat.BC7,
                    NormalMapFormat = TextureCompressionFormat.BC5,
                    HdrFormat = TextureCompressionFormat.BC6H,
                    DefaultAudioCodec = AudioCodecFormat.Vorbis,
                    DefaultSampleRate = 48000,
                    SupportsMicroCluster = true,
                    MaxTextureResolution = 16384,
                    SupportsMeshShaders = true
                },
                CookTargetPlatform.Linux => new CookPlatformConfig
                {
                    Platform = platform,
                    DefaultTextureFormat = TextureCompressionFormat.BC7,
                    NormalMapFormat = TextureCompressionFormat.BC5,
                    HdrFormat = TextureCompressionFormat.BC6H,
                    DefaultAudioCodec = AudioCodecFormat.Vorbis,
                    DefaultSampleRate = 48000,
                    SupportsMicroCluster = true,
                    MaxTextureResolution = 16384,
                    SupportsMeshShaders = true
                },
                // Android: ASTC 优先, 退回 ETC2; 不支持 MicroCluster
                CookTargetPlatform.Android => new CookPlatformConfig
                {
                    Platform = platform,
                    DefaultTextureFormat = TextureCompressionFormat.ASTC_6x6,
                    NormalMapFormat = TextureCompressionFormat.ASTC_4x4,
                    HdrFormat = TextureCompressionFormat.ASTC_4x4,
                    DefaultAudioCodec = AudioCodecFormat.Vorbis,
                    DefaultSampleRate = 44100,
                    SupportsMicroCluster = false,
                    MaxTextureResolution = 4096,
                    SupportsMeshShaders = false
                },
                // iOS / macOS: ASTC (移动) 或 BCn (桌面 Metal); Opus 语音
                CookTargetPlatform.iOS => new CookPlatformConfig
                {
                    Platform = platform,
                    DefaultTextureFormat = TextureCompressionFormat.ASTC_6x6,
                    NormalMapFormat = TextureCompressionFormat.ASTC_4x4,
                    HdrFormat = TextureCompressionFormat.ASTC_4x4,
                    DefaultAudioCodec = AudioCodecFormat.Opus,
                    DefaultSampleRate = 44100,
                    SupportsMicroCluster = false,
                    MaxTextureResolution = 4096,
                    SupportsMeshShaders = false
                },
                CookTargetPlatform.macOS => new CookPlatformConfig
                {
                    Platform = platform,
                    DefaultTextureFormat = TextureCompressionFormat.BC7,
                    NormalMapFormat = TextureCompressionFormat.BC5,
                    HdrFormat = TextureCompressionFormat.BC6H,
                    DefaultAudioCodec = AudioCodecFormat.Vorbis,
                    DefaultSampleRate = 48000,
                    SupportsMicroCluster = true,
                    MaxTextureResolution = 8192,
                    SupportsMeshShaders = true
                },
                // WebAssembly: ASTC 降级 / 无压缩; 流式音频
                CookTargetPlatform.WebAssembly => new CookPlatformConfig
                {
                    Platform = platform,
                    DefaultTextureFormat = TextureCompressionFormat.ASTC_6x6,
                    NormalMapFormat = TextureCompressionFormat.ASTC_4x4,
                    HdrFormat = TextureCompressionFormat.None,
                    DefaultAudioCodec = AudioCodecFormat.Opus,
                    DefaultSampleRate = 44100,
                    SupportsMicroCluster = false,
                    MaxTextureResolution = 2048,
                    SupportsMeshShaders = false
                },
                _ => new CookPlatformConfig
                {
                    Platform = platform,
                    DefaultTextureFormat = TextureCompressionFormat.BC7,
                    NormalMapFormat = TextureCompressionFormat.BC5,
                    HdrFormat = TextureCompressionFormat.BC6H,
                    DefaultAudioCodec = AudioCodecFormat.Vorbis
                }
            };
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Cook 结果与报告
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 单个资产的 Cook 结果。记录处理后的输出路径、压缩前后大小与处理耗时。
    /// </summary>
    public sealed class AssetCookResult
    {
        /// <summary>源资产 GUID (字符串形式)。</summary>
        public string AssetGuid { get; set; } = string.Empty;
        /// <summary>源资产路径。</summary>
        public string SourcePath { get; set; } = string.Empty;
        /// <summary>Cook 后的输出路径 (相对于 Cook 根目录)。</summary>
        public string OutputPath { get; set; } = string.Empty;
        /// <summary>资产类型。</summary>
        public AssetType AssetType { get; set; }
        /// <summary>Cook 前大小 (字节)。</summary>
        public long OriginalSize { get; set; }
        /// <summary>Cook 后大小 (字节)。</summary>
        public long CookedSize { get; set; }
        /// <summary>Cook 耗时 (毫秒)。</summary>
        public double CookTimeMs { get; set; }
        /// <summary>应用的压缩格式 (纹理 / 网格)。</summary>
        public string AppliedCompression { get; set; } = string.Empty;
        /// <summary>处理过程中的警告信息。</summary>
        public List<string> Warnings { get; set; } = new();
        /// <summary>是否成功。</summary>
        public bool Success { get; set; }

        /// <summary>压缩率 (0-1, 越小压缩越多)。</summary>
        public float CompressionRatio => OriginalSize > 0 ? (float)CookedSize / OriginalSize : 1f;
    }

    /// <summary>
    /// 整体 Cook 报告。汇总所有资产的 Cook 结果, 供构建报告使用。
    /// </summary>
    public sealed class CookReport
    {
        /// <summary>目标平台。</summary>
        public CookTargetPlatform Platform { get; set; }
        /// <summary>Cook 开始时间 (UTC)。</summary>
        public DateTime StartedAtUtc { get; set; }
        /// <summary>Cook 结束时间 (UTC)。</summary>
        public DateTime FinishedAtUtc { get; set; }
        /// <summary>总耗时 (秒)。</summary>
        public double TotalSeconds => (FinishedAtUtc - StartedAtUtc).TotalSeconds;
        /// <summary>各资产的 Cook 结果列表。</summary>
        public List<AssetCookResult> Results { get; set; } = new();
        /// <summary>按类型分组的大小统计。</summary>
        public Dictionary<string, long> SizeByType { get; set; } = new();
        /// <summary>成功资产数。</summary>
        public int SuccessCount => Results.Count(r => r.Success);
        /// <summary>失败资产数。</summary>
        public int FailureCount => Results.Count(r => !r.Success);
        /// <summary>Cook 前总大小 (字节)。</summary>
        public long TotalOriginalSize => Results.Sum(r => r.OriginalSize);
        /// <summary>Cook 后总大小 (字节)。</summary>
        public long TotalCookedSize => Results.Sum(r => r.CookedSize);
        /// <summary>全局压缩率。</summary>
        public float GlobalCompressionRatio =>
            TotalOriginalSize > 0 ? (float)TotalCookedSize / TotalOriginalSize : 1f;
        /// <summary>警告汇总。</summary>
        public List<string> Warnings { get; set; } = new();

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        };

        /// <summary>将报告序列化为 JSON 文件。</summary>
        public void Write(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 纹理 Cooker
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 纹理 Cook 模块。
    /// 负责: Mipmap 生成、压缩格式选择、sRGB 处理、Alpha 通道分离、纹理图集。
    /// 输出为平台最优的块压缩格式, 对应 RHI 的 <see cref="RHIFormat"/> 枚举。
    /// </summary>
    public static class TextureCooker
    {
        /// <summary>
        /// 将 <see cref="TextureCompressionFormat"/> 映射到 RHI 纹理格式。
        /// sRGB 变体会根据 <paramref name="srgb"/> 参数选择。
        /// </summary>
        public static RHIFormat ToRHIFormat(TextureCompressionFormat fmt, bool srgb)
        {
            return fmt switch
            {
                TextureCompressionFormat.None => srgb ? RHIFormat.R8G8B8A8_UNorm : RHIFormat.R8G8B8A8_UNorm,
                TextureCompressionFormat.BC1 => srgb ? RHIFormat.BC1_UNorm_SRGB : RHIFormat.BC1_UNorm,
                TextureCompressionFormat.BC3 => srgb ? RHIFormat.BC3_UNorm_SRGB : RHIFormat.BC3_UNorm,
                TextureCompressionFormat.BC4 => RHIFormat.BC4_UNorm,
                TextureCompressionFormat.BC5 => RHIFormat.BC5_UNorm,
                TextureCompressionFormat.BC6H => RHIFormat.BC6H_UF16,
                TextureCompressionFormat.BC7 => srgb ? RHIFormat.BC7_UNorm_SRGB : RHIFormat.BC7_UNorm,
                // ASTC / ETC2 在当前 RHI 枚举中暂未定义, 退回最接近的格式
                // 实际引擎中应扩展 RHIFormat 枚举以支持移动端格式
                TextureCompressionFormat.ASTC_4x4 or
                TextureCompressionFormat.ASTC_6x6 or
                TextureCompressionFormat.ASTC_8x8 => srgb ? RHIFormat.BC7_UNorm_SRGB : RHIFormat.BC7_UNorm,
                TextureCompressionFormat.ETC2_RGB or
                TextureCompressionFormat.ETC2_RGBA => srgb ? RHIFormat.BC1_UNorm_SRGB : RHIFormat.BC1_UNorm,
                _ => RHIFormat.R8G8B8A8_UNorm
            };
        }

        /// <summary>
        /// 计算指定压缩格式每像素的位数 (bits-per-pixel)。
        /// 用于估算 Cook 后纹理体积与显存占用。
        /// </summary>
        public static float BitsPerPixel(TextureCompressionFormat fmt)
        {
            return fmt switch
            {
                TextureCompressionFormat.None => 32f,
                TextureCompressionFormat.BC1 => 4f,
                TextureCompressionFormat.BC3 => 8f,
                TextureCompressionFormat.BC4 => 4f,
                TextureCompressionFormat.BC5 => 8f,
                TextureCompressionFormat.BC6H => 8f,
                TextureCompressionFormat.BC7 => 8f,
                TextureCompressionFormat.ASTC_4x4 => 8f,
                TextureCompressionFormat.ASTC_6x6 => 3.56f,
                TextureCompressionFormat.ASTC_8x8 => 2f,
                TextureCompressionFormat.ETC2_RGB => 4f,
                TextureCompressionFormat.ETC2_RGBA => 8f,
                _ => 32f
            };
        }

        /// <summary>
        /// 估算纹理 Cook 后的字节大小 (含 Mipmap 链)。
        /// 公式: 主层面积 * bpp/8 * (4/3) (Mipmap 链约为 1/3 额外)。
        /// </summary>
        public static long EstimateCookedSize(int width, int height, TextureCompressionFormat fmt, bool mipmaps)
        {
            float bpp = BitsPerPixel(fmt);
            long mainLevel = (long)(width * height * bpp / 8f);
            if (!mipmaps) return mainLevel;
            // Mipmap 链总大小 = 主层 * (1 + 1/4 + 1/16 + ...) = 主层 * 4/3
            return (long)(mainLevel * 4f / 3f);
        }

        /// <summary>
        /// 根据纹理用途与平台配置自动选择压缩格式。
        /// 法线图使用法线专用格式; HDR 使用 BC6H; 普通颜色图使用默认格式。
        /// </summary>
        public static TextureCompressionFormat SelectFormat(
            AssetMetadata asset, CookPlatformConfig platform, TextureCookSettings settings)
        {
            // 显式指定优先
            if (settings.CompressionFormat != TextureCompressionFormat.None)
                return settings.CompressionFormat;

            var name = asset.Name.ToLowerInvariant();
            var isNormalMap = name.Contains("normal") || name.Contains("nrm") || name.Contains("_n");
            var isHdr = name.Contains("hdr") || name.Contains("env") || name.Contains("sky");

            if (isNormalMap) return platform.NormalMapFormat;
            if (isHdr) return platform.HdrFormat;
            return platform.DefaultTextureFormat;
        }

        /// <summary>
        /// 执行纹理 Cook。
        /// 实际实现需调用 ImageSharp 或平台压缩 SDK 进行块压缩编码,
        /// 此处提供完整的处理流程框架与大小估算。
        /// </summary>
        public static AssetCookResult Cook(
            AssetMetadata asset, string sourceFile, string outputDir,
            CookPlatformConfig platform, TextureCookSettings settings)
        {
            var result = new AssetCookResult
            {
                AssetGuid = asset.Guid.ToString("N"),
                SourcePath = asset.AssetPath,
                AssetType = asset.Type,
                Success = false
            };

            var sw = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                if (!File.Exists(sourceFile))
                {
                    result.Warnings.Add($"源文件不存在: {sourceFile}");
                    result.CookTimeMs = sw.Elapsed.TotalMilliseconds;
                    return result;
                }

                result.OriginalSize = new FileInfo(sourceFile).Length;

                // 1. 读取纹理头信息 (实际实现使用 ImageSharp)
                // 此处用文件大小估算分辨率
                int estimatedWidth = (int)Math.Sqrt(result.OriginalSize / 4);
                int width = Math.Min(estimatedWidth, settings.MaxResolution);
                int height = width;
                width = Math.Max(width, platform.MaxTextureResolution > 0
                    ? Math.Min(width, platform.MaxTextureResolution) : width);

                // 2. 选择压缩格式
                var format = SelectFormat(asset, platform, settings);
                result.AppliedCompression = format.ToString();

                // 3. Mipmap 决策
                bool genMips = settings.GenerateMipmaps && width > 4;

                // 4. sRGB 处理: 法线图 / 蒙版图必须为线性
                bool srgb = settings.Srgb;
                var lowerName = asset.Name.ToLowerInvariant();
                if (lowerName.Contains("normal") || lowerName.Contains("metallic") ||
                    lowerName.Contains("roughness") || lowerName.Contains("occlusion"))
                {
                    srgb = false;
                }

                // 5. Alpha 通道分离 (当格式不支持 Alpha 且贴图有 Alpha 时)
                bool alphaSplit = settings.SplitAlpha &&
                    (format == TextureCompressionFormat.BC1 || format == TextureCompressionFormat.BC4 ||
                     format == TextureCompressionFormat.ETC2_RGB);
                if (alphaSplit)
                {
                    result.Warnings.Add("Alpha 通道已分离为独立 BC4 纹理");
                }

                // 6. 估算 Cook 后大小
                result.CookedSize = EstimateCookedSize(width, height, format, genMips);
                if (alphaSplit)
                {
                    // Alpha 纹理额外占用
                    result.CookedSize += EstimateCookedSize(width, height, TextureCompressionFormat.BC4, genMips);
                }

                // 7. 生成输出路径 (平台特定后缀)
                var rhiFormat = ToRHIFormat(format, srgb);
                var ext = Path.GetExtension(sourceFile);
                var baseName = Path.GetFileNameWithoutExtension(sourceFile);
                var platformSuffix = platform.Platform.ToString().ToLowerInvariant();
                var outFileName = $"{baseName}_{platformSuffix}.cookedtex";
                result.OutputPath = Path.Combine(outputDir, outFileName);

                // 8. 写入 Cook 头 (实际实现写入压缩纹理数据)
                WriteCookedTextureHeader(result.OutputPath, width, height, format, rhiFormat, srgb, genMips, alphaSplit);

                result.Success = true;
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"纹理 Cook 异常: {ex.Message}");
            }

            sw.Stop();
            result.CookTimeMs = sw.Elapsed.TotalMilliseconds;
            return result;
        }

        /// <summary>
        /// 写入 Cook 后纹理的头文件 (包含格式元数据)。
        /// 实际实现应写入完整的压缩纹理数据块。
        /// </summary>
        private static void WriteCookedTextureHeader(
            string outputPath, int width, int height,
            TextureCompressionFormat format, RHIFormat rhiFormat,
            bool srgb, bool mipmaps, bool alphaSplit)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            using var fs = File.Create(outputPath);
            using var bw = new BinaryWriter(fs);

            // 魔数 "SHTEX"
            bw.Write(Encoding.ASCII.GetBytes("SHTEX"));
            // 版本
            bw.Write((byte)1);
            // 宽高
            bw.Write(width);
            bw.Write(height);
            // 压缩格式
            bw.Write((int)format);
            // RHI 格式
            bw.Write((int)rhiFormat);
            // sRGB 标志
            bw.Write(srgb);
            // Mipmap 标志
            bw.Write(mipmaps);
            // Alpha 分离标志
            bw.Write(alphaSplit);
            // Mip 层数 (0 = 自动)
            int mipCount = mipmaps ? (int)Math.Floor(Math.Log2(Math.Max(width, height))) + 1 : 1;
            bw.Write(mipCount);
            // 预留数据区占位 (实际写入压缩块数据)
            bw.Write(0L);
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 网格 Cooker
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 网格 Cook 模块。
    /// 负责: 顶点缓冲缓存友好重排序、索引缓冲优化、LOD 生成、顶点格式压缩、
    /// MicroCluster 虚拟几何体预处理 (簇化 + LOD DAG)。
    /// 与 <see cref="MicroClusterClusterBuilder"/> 对接完成虚拟几何体构建。
    /// </summary>
    public static class MeshCooker
    {
        /// <summary>
        /// 执行网格 Cook。
        /// 处理流程: 剔除退化三角形 -> 顶点焊接 -> 顶点缓存优化 -> 索引优化 ->
        /// LOD 生成 -> 顶点压缩 -> (可选) MicroCluster 预处理 -> 序列化输出。
        /// </summary>
        public static AssetCookResult Cook(
            AssetMetadata asset, string sourceFile, string outputDir,
            CookPlatformConfig platform, MeshCookSettings settings)
        {
            var result = new AssetCookResult
            {
                AssetGuid = asset.Guid.ToString("N"),
                SourcePath = asset.AssetPath,
                AssetType = asset.Type,
                Success = false
            };

            var sw = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                if (!File.Exists(sourceFile))
                {
                    result.Warnings.Add($"源文件不存在: {sourceFile}");
                    result.CookTimeMs = sw.Elapsed.TotalMilliseconds;
                    return result;
                }

                result.OriginalSize = new FileInfo(sourceFile).Length;

                // 1. 加载网格数据 (实际实现通过 DCCBridge.ImportScene)
                // 此处使用占位数据模拟处理流程
                int vertexCount = Math.Max(100, (int)(result.OriginalSize / 64));
                int indexCount = vertexCount * 3;
                int triangleCount = indexCount / 3;

                // 2. 剔除退化三角形
                int degenerateRemoved = 0;
                if (settings.RemoveDegenerateTriangles)
                {
                    degenerateRemoved = triangleCount / 100; // 模拟: 约 1% 退化三角形
                    triangleCount -= degenerateRemoved;
                    indexCount = triangleCount * 3;
                }

                // 3. 顶点焊接 (合并相同顶点)
                int weldedVertices = 0;
                if (settings.WeldThreshold > 0)
                {
                    weldedVertices = vertexCount / 50; // 模拟: 约 2% 重复顶点
                    vertexCount -= weldedVertices;
                }

                // 4. 顶点缓存优化 (Forsyth 算法)
                // 优化后顶点缓冲的 post-T&L 缓存命中率提升
                float cacheOptimizationGain = 1.0f;
                if (settings.OptimizeVertexCache)
                {
                    cacheOptimizationGain = 2.5f; // 模拟: 缓存命中率提升 2.5x
                }

                // 5. 索引缓冲优化 (去除冗余图元、对预变换缓存友好)
                if (settings.OptimizeIndexBuffer)
                {
                    // 索引重排序不改变数据量, 仅改变访问顺序
                }

                // 6. LOD 生成
                int lodLevels = settings.GenerateLod ? settings.LodCount : 1;
                var lodTriangleCounts = new int[lodLevels];
                lodTriangleCounts[0] = triangleCount;
                for (int i = 1; i < lodLevels; i++)
                {
                    float ratio = i < settings.LodScreenRatios.Length
                        ? settings.LodScreenRatios[i] : 1f / (1 << i);
                    lodTriangleCounts[i] = (int)(triangleCount * ratio);
                }

                // 7. 顶点压缩
                int bytesPerVertex = GetBytesPerVertex(settings.Compression);
                long compressedVBSize = (long)vertexCount * bytesPerVertex;
                long indexSize = (long)indexCount * (vertexCount > 65535 ? 4 : 2);
                long lodDataSize = 0;
                for (int i = 1; i < lodLevels; i++)
                {
                    lodDataSize += (long)lodTriangleCounts[i] * 3 * (vertexCount > 65535 ? 4 : 2);
                    lodDataSize += (long)(vertexCount * settings.LodScreenRatios[Math.Min(i, settings.LodScreenRatios.Length - 1)]) * bytesPerVertex;
                }

                result.CookedSize = compressedVBSize + indexSize + lodDataSize;
                result.AppliedCompression = $"{settings.Compression} ({bytesPerVertex}B/vert, {lodLevels} LOD)";

                // 8. MicroCluster 预处理 (簇化 + LOD DAG)
                long microclusterSize = 0;
                if (settings.EnableMicroClusterPreprocess && platform.SupportsMicroCluster)
                {
                    // 调用 MicroClusterClusterBuilder 进行簇化
                    // 实际实现需传入真实的位置/索引/法线/UV 数据
                    int clusterCount = (triangleCount + MicroClusterConstants.TrianglesPerCluster - 1)
                        / MicroClusterConstants.TrianglesPerCluster;
                    microclusterSize = (long)clusterCount * MicroClusterConstants.ClusterPageSize;
                    result.CookedSize += microclusterSize;
                    result.AppliedCompression += $", MicroCluster({clusterCount} clusters)";
                }
                else if (settings.EnableMicroClusterPreprocess && !platform.SupportsMicroCluster)
                {
                    result.Warnings.Add($"平台 {platform.Platform} 不支持 MicroCluster, 跳过虚拟几何体预处理");
                }

                // 9. 序列化输出
                var baseName = Path.GetFileNameWithoutExtension(sourceFile);
                var platformSuffix = platform.Platform.ToString().ToLowerInvariant();
                var outFileName = $"{baseName}_{platformSuffix}.cookedmesh";
                result.OutputPath = Path.Combine(outputDir, outFileName);

                WriteCookedMeshHeader(result.OutputPath, vertexCount, indexCount, lodLevels,
                    settings.Compression, settings.EnableMicroClusterPreprocess && platform.SupportsMicroCluster,
                    degenerateRemoved, weldedVertices, cacheOptimizationGain);

                result.Success = true;
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"网格 Cook 异常: {ex.Message}");
            }

            sw.Stop();
            result.CookTimeMs = sw.Elapsed.TotalMilliseconds;
            return result;
        }

        /// <summary>
        /// 获取指定压缩级别下每个顶点占用的字节数。
        /// </summary>
        public static int GetBytesPerVertex(VertexCompressionLevel level)
        {
            return level switch
            {
                VertexCompressionLevel.None => 48,       // pos(12) + nrm(12) + tan(12) + uv(8) + col(4)
                VertexCompressionLevel.Light => 24,      // pos(8) + nrm(4) + tan(4) + uv(4) + col(4)
                VertexCompressionLevel.Medium => 16,     // pos(4) + nrm(4) + tan(4) + uv(4)
                VertexCompressionLevel.Aggressive => 12, // pos(6) + nrm(3) + uv(2) + col(1)
                _ => 48
            };
        }

        /// <summary>写入 Cook 后网格的头文件。</summary>
        private static void WriteCookedMeshHeader(
            string outputPath, int vertexCount, int indexCount, int lodLevels,
            VertexCompressionLevel compression, bool microcluster,
            int degenerateRemoved, int weldedVertices, float cacheGain)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            using var fs = File.Create(outputPath);
            using var bw = new BinaryWriter(fs);

            bw.Write(Encoding.ASCII.GetBytes("SHMESH"));
            bw.Write((byte)1);
            bw.Write(vertexCount);
            bw.Write(indexCount);
            bw.Write(lodLevels);
            bw.Write((int)compression);
            bw.Write(microcluster);
            bw.Write(degenerateRemoved);
            bw.Write(weldedVertices);
            bw.Write(cacheGain);
            bw.Write(0L); // 数据区占位
        }

        /// <summary>
        /// 顶点缓存优化 (Forsyth 算法) 的简化实现。
        /// 通过模拟 FIFO 缓存对索引进行重排序, 提升 post-T&L 缓存命中率。
        /// </summary>
        public static int[] OptimizeVertexCacheOrder(int[] indices, int cacheSize = 32)
        {
            if (indices == null || indices.Length < 3) return indices;

            int triCount = indices.Length / 3;
            var triangleScores = new float[triCount];
            var cache = new int[cacheSize];
            Array.Fill(cache, -1);

            var result = new int[indices.Length];
            var used = new bool[triCount];
            int writePos = 0;

            // 贪心: 每次选择当前在缓存中顶点得分最高的三角形
            for (int iter = 0; iter < triCount; iter++)
            {
                int bestTri = -1;
                float bestScore = -1;

                for (int t = 0; t < triCount; t++)
                {
                    if (used[t]) continue;
                    float score = 0;
                    for (int v = 0; v < 3; v++)
                    {
                        int idx = indices[t * 3 + v];
                        int cachePos = Array.IndexOf(cache, idx);
                        if (cachePos >= 0)
                            score += 1f - cachePos / (float)cacheSize;
                    }
                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestTri = t;
                    }
                }

                if (bestTri < 0) break;
                used[bestTri] = true;

                // 将该三角形的顶点推入缓存 (FIFO)
                for (int v = 0; v < 3; v++)
                {
                    result[writePos++] = indices[bestTri * 3 + v];
                    // 移动缓存
                    for (int c = cacheSize - 1; c > 0; c--)
                        cache[c] = cache[c - 1];
                    cache[0] = indices[bestTri * 3 + v];
                }
            }

            return result;
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 音频 Cooker
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 音频 Cook 模块。
    /// 负责: 采样率转换、Vorbis/Opus/ADPCM 编码、流式 vs 内存加载标记。
    /// 与 <see cref="AudioClip"/> 系统对接, 输出平台最优的编码格式。
    /// </summary>
    public static class AudioCooker
    {
        /// <summary>
        /// 执行音频 Cook。
        /// 根据音频类型 (SFX/Music/Voice) 自动选择编码格式与加载策略。
        /// </summary>
        public static AssetCookResult Cook(
            AssetMetadata asset, string sourceFile, string outputDir,
            CookPlatformConfig platform, AudioCookSettings settings)
        {
            var result = new AssetCookResult
            {
                AssetGuid = asset.Guid.ToString("N"),
                SourcePath = asset.AssetPath,
                AssetType = asset.Type,
                Success = false
            };

            var sw = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                if (!File.Exists(sourceFile))
                {
                    result.Warnings.Add($"源文件不存在: {sourceFile}");
                    result.CookTimeMs = sw.Elapsed.TotalMilliseconds;
                    return result;
                }

                result.OriginalSize = new FileInfo(sourceFile).Length;

                // 1. 解析原始音频信息 (实际实现使用 MiniAudio 解码)
                // 估算: WAV 文件约 10.5MB/分钟 (44.1kHz 16bit 立体声)
                double estimatedDurationSec = result.OriginalSize / (44100.0 * 2 * 2);
                int sourceSampleRate = 44100;
                int sourceChannels = 2;

                // 2. 采样率转换决策
                int targetRate = settings.TargetSampleRate > 0
                    ? settings.TargetSampleRate : platform.DefaultSampleRate;
                int targetChannels = settings.TargetChannels;

                // 3. 编码格式选择
                var codec = settings.Codec;
                // 长音频 (>30s) 自动标记为流式
                bool streaming = settings.Streaming || estimatedDurationSec > 30;
                // 语音类型自动选择 Opus
                var nameLower = asset.Name.ToLowerInvariant();
                if (nameLower.Contains("voice") || nameLower.Contains("speech"))
                {
                    codec = AudioCodecFormat.Opus;
                }
                // 短音效使用 ADPCM (低延迟)
                else if (estimatedDurationSec < 3 && codec == AudioCodecFormat.Vorbis)
                {
                    codec = AudioCodecFormat.Adpcm;
                }

                // 4. 估算编码后大小
                float compressionRatio = codec switch
                {
                    AudioCodecFormat.Pcm => 1.0f,
                    AudioCodecFormat.Vorbis => 0.12f * (100 - settings.Quality) / 100f + 0.08f,
                    AudioCodecFormat.Opus => 0.08f,
                    AudioCodecFormat.Adpcm => 0.25f,
                    _ => 0.5f
                };

                // 采样率转换比例
                float rateRatio = (float)targetRate / sourceSampleRate;
                float channelRatio = (float)targetChannels / sourceChannels;
                result.CookedSize = (long)(result.OriginalSize * compressionRatio * rateRatio * channelRatio);

                result.AppliedCompression = $"{codec} (q{settings.Quality}), {targetRate}Hz, {targetChannels}ch";

                if (streaming)
                {
                    result.AppliedCompression += ", Streaming";
                    int chunkSize = settings.StreamChunkSize > 0
                        ? settings.StreamChunkSize : 64 * 1024;
                    result.AppliedCompression += $" ({chunkSize / 1024}KB chunks)";
                }

                // 5. 无缝循环处理
                if (settings.SeamlessLoop)
                {
                    result.AppliedCompression += ", SeamlessLoop";
                }

                // 6. 序列化输出
                var baseName = Path.GetFileNameWithoutExtension(sourceFile);
                var platformSuffix = platform.Platform.ToString().ToLowerInvariant();
                var ext = codec switch
                {
                    AudioCodecFormat.Vorbis => ".ogg",
                    AudioCodecFormat.Opus => ".opus",
                    AudioCodecFormat.Adpcm => ".adpcm",
                    _ => ".wav"
                };
                var outFileName = $"{baseName}_{platformSuffix}{ext}";
                result.OutputPath = Path.Combine(outputDir, outFileName);

                WriteCookedAudioHeader(result.OutputPath, targetRate, targetChannels, codec,
                    streaming, settings.Quality, estimatedDurationSec);

                result.Success = true;
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"音频 Cook 异常: {ex.Message}");
            }

            sw.Stop();
            result.CookTimeMs = sw.Elapsed.TotalMilliseconds;
            return result;
        }

        /// <summary>写入 Cook 后音频的头文件。</summary>
        private static void WriteCookedAudioHeader(
            string outputPath, int sampleRate, int channels, AudioCodecFormat codec,
            bool streaming, int quality, double duration)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            using var fs = File.Create(outputPath);
            using var bw = new BinaryWriter(fs);

            bw.Write(Encoding.ASCII.GetBytes("SHAUD"));
            bw.Write((byte)1);
            bw.Write(sampleRate);
            bw.Write(channels);
            bw.Write((int)codec);
            bw.Write(streaming);
            bw.Write((byte)quality);
            bw.Write(duration);
            bw.Write(0L); // 数据区占位
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 动画 Cooker
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 动画 Cook 模块。
    /// 负责: 动画压缩、关键帧减少、均匀采样。
    /// 输出压缩后的关键帧数据, 减少运行时内存占用与采样开销。
    /// </summary>
    public static class AnimationCooker
    {
        /// <summary>
        /// 执行动画 Cook。
        /// 处理: 关键帧减少 -> 均匀采样 -> 线性简化 -> 根运动保留 -> 序列化。
        /// </summary>
        public static AssetCookResult Cook(
            AssetMetadata asset, string sourceFile, string outputDir,
            CookPlatformConfig platform, AnimationCookSettings settings)
        {
            var result = new AssetCookResult
            {
                AssetGuid = asset.Guid.ToString("N"),
                SourcePath = asset.AssetPath,
                AssetType = asset.Type,
                Success = false
            };

            var sw = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                if (!File.Exists(sourceFile))
                {
                    result.Warnings.Add($"源文件不存在: {sourceFile}");
                    result.CookTimeMs = sw.Elapsed.TotalMilliseconds;
                    return result;
                }

                result.OriginalSize = new FileInfo(sourceFile).Length;

                // 1. 估算原始动画参数 (实际实现从 AnimationClip 解析)
                int boneCount = 64; // 默认骨骼数
                double durationSec = result.OriginalSize / (1024.0 * 10); // 粗略估算
                int originalKeyframes = (int)(durationSec * 30); // 30fps 原始采样

                // 2. 关键帧减少
                int reducedKeyframes = originalKeyframes;
                float keyframeReductionRatio = 1f;
                if (settings.EnableCompression && settings.KeyframeReductionThreshold > 0)
                {
                    // 基于误差容差减少关键帧 (模拟: 保留约 30-50%)
                    keyframeReductionRatio = 0.4f;
                    reducedKeyframes = (int)(originalKeyframes * keyframeReductionRatio);
                }

                // 3. 均匀采样 (固定帧率重采样)
                int uniformKeyframes = reducedKeyframes;
                if (settings.UniformSampling)
                {
                    uniformKeyframes = (int)(durationSec * settings.UniformSampleRate);
                    // 均匀采样可能进一步减少或增加关键帧
                    keyframeReductionRatio = (float)uniformKeyframes / originalKeyframes;
                }

                // 4. 线性简化 (Bezier -> Linear)
                // 线性插值数据量更小 (每通道 2 个 float 而非 3 个)
                float linearCompressionFactor = settings.LinearSimplification ? 0.7f : 1f;

                // 5. 估算压缩后大小
                // 每关键帧每骨骼: 位置(12) + 旋转(8 quat compressed) + 缩放(12) = 32 bytes
                int bytesPerKeyframePerBone = settings.LinearSimplification ? 24 : 32;
                result.CookedSize = (long)(uniformKeyframes * boneCount * bytesPerKeyframePerBone * linearCompressionFactor);

                // 根运动额外保留 (全精度)
                if (settings.PreserveRootMotion)
                {
                    result.CookedSize += (long)(originalKeyframes * 32); // 根运动全精度
                }

                result.AppliedCompression =
                    $"{(settings.EnableCompression ? "Compressed" : "Raw")}, " +
                    $"{uniformKeyframes}/{originalKeyframes} keyframes " +
                    $"({keyframeReductionRatio * 100:F0}%), " +
                    $"{(settings.UniformSampling ? settings.UniformSampleRate + "fps uniform" : "variable rate")}, " +
                    $"{boneCount} bones";

                // 6. 序列化输出
                var baseName = Path.GetFileNameWithoutExtension(sourceFile);
                var platformSuffix = platform.Platform.ToString().ToLowerInvariant();
                var outFileName = $"{baseName}_{platformSuffix}.cookedanim";
                result.OutputPath = Path.Combine(outputDir, outFileName);

                WriteCookedAnimHeader(result.OutputPath, boneCount, uniformKeyframes,
                    settings.UniformSampleRate, settings.EnableCompression, durationSec);

                result.Success = true;
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"动画 Cook 异常: {ex.Message}");
            }

            sw.Stop();
            result.CookTimeMs = sw.Elapsed.TotalMilliseconds;
            return result;
        }

        /// <summary>写入 Cook 后动画的头文件。</summary>
        private static void WriteCookedAnimHeader(
            string outputPath, int boneCount, int keyframeCount,
            int sampleRate, bool compressed, double duration)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            using var fs = File.Create(outputPath);
            using var bw = new BinaryWriter(fs);

            bw.Write(Encoding.ASCII.GetBytes("SHANIM"));
            bw.Write((byte)1);
            bw.Write(boneCount);
            bw.Write(keyframeCount);
            bw.Write(sampleRate);
            bw.Write(compressed);
            bw.Write(duration);
            bw.Write(0L);
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 材质 Cooker
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 材质 Cook 模块。
    /// 负责: 着色器变体预编译、材质参数烘焙、材质实例合并。
    /// 与 <see cref="PbrMaterial"/> 和 <see cref="MaterialGraph"/> 系统对接。
    /// </summary>
    public static class MaterialCooker
    {
        /// <summary>
        /// 执行材质 Cook。
        /// 处理: 收集着色器关键字 -> 枚举 Permutation -> 预编译变体 ->
        /// 烘焙静态参数 -> 打包常量缓冲 -> 序列化材质包。
        /// </summary>
        public static AssetCookResult Cook(
            AssetMetadata asset, string sourceFile, string outputDir,
            CookPlatformConfig platform, MaterialCookSettings settings)
        {
            var result = new AssetCookResult
            {
                AssetGuid = asset.Guid.ToString("N"),
                SourcePath = asset.AssetPath,
                AssetType = asset.Type,
                Success = false
            };

            var sw = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                if (!File.Exists(sourceFile))
                {
                    result.Warnings.Add($"源文件不存在: {sourceFile}");
                    result.CookTimeMs = sw.Elapsed.TotalMilliseconds;
                    return result;
                }

                result.OriginalSize = new FileInfo(sourceFile).Length;

                // 1. 加载材质 (实际实现反序列化 PbrMaterial / MaterialGraph)
                // 此处估算材质参数与关键字
                int keywordCount = 8; // 平均关键字数
                int textureSlotCount = 6; // 基础 PBR 贴图槽数

                // 2. 着色器变体枚举
                int variantCount = 1;
                if (settings.PrecompileVariants)
                {
                    // 变体数 = 2^keywordCount (每个关键字开/关)
                    // 实际实现会剔除无效组合, 保留活跃变体
                    variantCount = Math.Min(1 << keywordCount, 64);
                }

                // 3. 烘焙静态参数
                long parameterDataSize = 0;
                if (settings.BakeStaticParameters)
                {
                    // 静态参数烘焙到常量缓冲 (256 bytes 对齐)
                    parameterDataSize = 256; // CB 对齐
                }

                // 4. 估算 Cook 后大小
                // 每个变体: 着色器字节码大小 (VS+PS 约 16KB) + 变体元数据
                long variantSize = variantCount * (16 * 1024 + 64);
                long textureReferenceSize = textureSlotCount * 32; // 贴图引用 (GUID + 采样器)
                result.CookedSize = variantSize + parameterDataSize + textureReferenceSize;

                result.AppliedCompression =
                    $"{variantCount} variants ({settings.TargetShaderModel}), " +
                    $"{(settings.BakeStaticParameters ? "params baked" : "params dynamic")}, " +
                    $"{textureSlotCount} tex slots";

                if (settings.PackToConstantBuffer)
                {
                    result.AppliedCompression += ", CB packed";
                }
                if (settings.MergeInstances)
                {
                    result.AppliedCompression += ", merge enabled";
                }

                // 5. 序列化输出
                var baseName = Path.GetFileNameWithoutExtension(sourceFile);
                var platformSuffix = platform.Platform.ToString().ToLowerInvariant();
                var outFileName = $"{baseName}_{platformSuffix}.cookedmat";
                result.OutputPath = Path.Combine(outputDir, outFileName);

                WriteCookedMaterialHeader(result.OutputPath, variantCount, keywordCount,
                    textureSlotCount, settings.TargetShaderModel, settings.BakeStaticParameters);

                result.Success = true;
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"材质 Cook 异常: {ex.Message}");
            }

            sw.Stop();
            result.CookTimeMs = sw.Elapsed.TotalMilliseconds;
            return result;
        }

        /// <summary>写入 Cook 后材质的头文件。</summary>
        private static void WriteCookedMaterialHeader(
            string outputPath, int variantCount, int keywordCount,
            int textureSlots, string shaderModel, bool bakedParams)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            using var fs = File.Create(outputPath);
            using var bw = new BinaryWriter(fs);

            bw.Write(Encoding.ASCII.GetBytes("SHMAT"));
            bw.Write((byte)1);
            bw.Write(variantCount);
            bw.Write(keywordCount);
            bw.Write(textureSlots);
            bw.Write(Encoding.ASCII.GetBytes(shaderModel.PadRight(16, '\0')));
            bw.Write(bakedParams);
            bw.Write(0L);
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 资源依赖序列化
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 资源依赖序列化器。
    /// 将 <see cref="AssetDependencyGraph"/> 序列化为运行时可读的依赖表,
    /// 用于运行时按需加载与依赖追踪。
    /// </summary>
    public static class AssetDependencySerializer
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = false,
            PropertyNameCaseInsensitive = true
        };

        /// <summary>
        /// 将依赖图序列化为 JSON 依赖清单文件。
        /// 输出包含: 节点列表、依赖边列表、缺失引用列表。
        /// </summary>
        public static void Serialize(AssetDependencyGraph graph, string outputPath)
        {
            var manifest = new DependencyManifest
            {
                Nodes = graph.Nodes.Select(n => new DependencyNodeEntry
                {
                    Id = n.Id,
                    Type = n.Type.ToString(),
                    Name = n.Name
                }).ToList(),
                Edges = graph.Edges.Select(e => new DependencyEdgeEntry
                {
                    Source = e.SourceId,
                    Target = e.TargetId
                }).ToList(),
                MissingReferences = graph.MissingReferences.Select(m => new MissingRefEntry
                {
                    Source = m.SourceId,
                    Target = m.TargetId,
                    Reason = m.Reason
                }).ToList(),
                SerializedAt = DateTime.UtcNow
            };

            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            File.WriteAllText(outputPath, JsonSerializer.Serialize(manifest, JsonOptions));
        }

        /// <summary>
        /// 从 JSON 文件反序列化依赖清单。
        /// 运行时使用此清单进行按需加载与依赖检查。
        /// </summary>
        public static DependencyManifest Deserialize(string inputPath)
        {
            if (!File.Exists(inputPath))
                throw new FileNotFoundException($"依赖清单文件不存在: {inputPath}");
            return JsonSerializer.Deserialize<DependencyManifest>(
                File.ReadAllText(inputPath), JsonOptions)
                ?? throw new InvalidOperationException("无效的依赖清单文件");
        }

        /// <summary>
        /// 为指定资产构建传递依赖闭包 (所有需要先加载的资源)。
        /// 用于增量 Cook: 当某资产变化时, 需重新 Cook 其所有依赖者。
        /// </summary>
        public static List<string> GetTransitiveClosure(AssetDependencyGraph graph, string assetId)
        {
            return graph.GetTransitiveDependencies(assetId).ToList();
        }
    }

    /// <summary>运行时依赖清单 (序列化格式)。</summary>
    public sealed class DependencyManifest
    {
        /// <summary>节点列表。</summary>
        public List<DependencyNodeEntry> Nodes { get; set; } = new();
        /// <summary>依赖边列表。</summary>
        public List<DependencyEdgeEntry> Edges { get; set; } = new();
        /// <summary>缺失引用列表。</summary>
        public List<MissingRefEntry> MissingReferences { get; set; } = new();
        /// <summary>序列化时间 (UTC)。</summary>
        public DateTime SerializedAt { get; set; }
    }

    /// <summary>依赖清单中的节点条目。</summary>
    public sealed class DependencyNodeEntry
    {
        /// <summary>节点 ID (资产路径)。</summary>
        public string Id { get; set; } = string.Empty;
        /// <summary>节点类型。</summary>
        public string Type { get; set; } = string.Empty;
        /// <summary>节点名称。</summary>
        public string Name { get; set; } = string.Empty;
    }

    /// <summary>依赖清单中的边条目。</summary>
    public sealed class DependencyEdgeEntry
    {
        /// <summary>依赖方 (源)。</summary>
        public string Source { get; set; } = string.Empty;
        /// <summary>被依赖方 (目标)。</summary>
        public string Target { get; set; } = string.Empty;
    }

    /// <summary>依赖清单中的缺失引用条目。</summary>
    public sealed class MissingRefEntry
    {
        /// <summary>引用方。</summary>
        public string Source { get; set; } = string.Empty;
        /// <summary>缺失的目标。</summary>
        public string Target { get; set; } = string.Empty;
        /// <summary>引用原因。</summary>
        public string Reason { get; set; } = string.Empty;
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 平台特定的资源路径映射
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 平台资源路径映射器。
    /// 将编辑器中的统一资产路径 (如 "Assets/Textures/hero.png")
    /// 映射为各平台 Cook 后的运行时路径 (如 "cooked/windows/hero_windows.cookedtex")。
    /// 
    /// 不同平台的文件系统规范不同 (大小写敏感、路径分隔符、最大路径长度),
    /// 映射器负责处理这些差异。
    /// </summary>
    public sealed class PlatformAssetPathMapper
    {
        /// <summary>平台配置。</summary>
        private readonly CookPlatformConfig _platform;
        /// <summary>路径缓存 (编辑器路径 -> 运行时路径)。</summary>
        private readonly Dictionary<string, string> _pathCache = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 构造路径映射器。
        /// </summary>
        /// <param name="platform">目标平台配置。</param>
        public PlatformAssetPathMapper(CookPlatformConfig platform)
        {
            _platform = platform;
        }

        /// <summary>
        /// 将编辑器资产路径映射为运行时 Cook 后路径。
        /// 规则: 添加平台子目录前缀 + 类型特定后缀 + 平台后缀。
        /// </summary>
        public string MapToRuntimePath(string editorAssetPath, AssetType assetType)
        {
            if (_pathCache.TryGetValue(editorAssetPath, out var cached))
                return cached;

            // 1. 规范化路径 (统一正斜杠)
            var normalized = editorAssetPath.Replace('\\', '/').TrimStart('/');

            // 2. 移除 "Assets/" 前缀
            if (normalized.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
                normalized = normalized.Substring(7);

            // 3. 移除原始扩展名, 替换为 Cook 后扩展名
            var baseName = Path.GetFileNameWithoutExtension(normalized);
            var directory = Path.GetDirectoryName(normalized)?.Replace('\\', '/') ?? string.Empty;

            // 4. 平台特定处理
            var platformDir = _platform.Platform.ToString().ToLowerInvariant();
            var extension = GetCookedExtension(assetType);
            var platformSuffix = platformDir;

            // 5. 大小写处理: Linux 区分大小写, 统一转小写
            if (_platform.Platform == CookTargetPlatform.Linux)
            {
                baseName = baseName.ToLowerInvariant();
                directory = directory.ToLowerInvariant();
            }

            // 6. 构建运行时路径
            string runtimePath;
            if (string.IsNullOrEmpty(directory))
                runtimePath = $"{platformDir}/{baseName}_{platformSuffix}{extension}";
            else
                runtimePath = $"{platformDir}/{directory}/{baseName}_{platformSuffix}{extension}";

            // 7. WebAssembly: 路径需 URL 安全
            if (_platform.Platform == CookTargetPlatform.WebAssembly)
            {
                runtimePath = Uri.EscapeDataString(runtimePath);
            }

            _pathCache[editorAssetPath] = runtimePath;
            return runtimePath;
        }

        /// <summary>
        /// 获取 Cook 后资产的文件扩展名 (按资产类型)。
        /// </summary>
        public static string GetCookedExtension(AssetType assetType)
        {
            return assetType switch
            {
                AssetType.Texture2D or AssetType.TextureCube => ".cookedtex",
                AssetType.Mesh => ".cookedmesh",
                AssetType.Audio => ".cookedaudio",
                AssetType.Animation => ".cookedanim",
                AssetType.Material => ".cookedmat",
                AssetType.Shader => ".cookedshd",
                AssetType.Skeleton => ".cookedskel",
                AssetType.Prefab => ".cookedprefab",
                AssetType.Scene => ".cookedscene",
                _ => ".cooked"
            };
        }

        /// <summary>
        /// 获取平台特定的内容根目录名。
        /// 例如 Windows 平台返回 "cooked/windows"。
        /// </summary>
        public string GetContentRoot()
        {
            return $"cooked/{_platform.Platform.ToString().ToLowerInvariant()}";
        }

        /// <summary>清空路径缓存。</summary>
        public void ClearCache() => _pathCache.Clear();
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 资源签名与完整性校验
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 资源签名器。
    /// 为每个 Cook 后的资产生成 SHA-256 签名, 用于运行时完整性校验
    /// (防篡改、热更新包验证、增量构建缓存键)。
    /// </summary>
    public static class AssetSigner
    {
        /// <summary>签名文件扩展名。</summary>
        public const string SignatureExtension = ".sig";

        /// <summary>
        /// 为单个资产生成签名文件。
        /// 签名内容: SHA-256(文件内容) + 文件大小 + 资产 GUID + 时间戳。
        /// </summary>
        public static AssetSignature Sign(string assetPath, string assetGuid)
        {
            if (!File.Exists(assetPath))
                throw new FileNotFoundException($"无法签名的资产文件: {assetPath}");

            byte[] content;
            using (var fs = File.OpenRead(assetPath))
            {
                content = new byte[fs.Length];
                fs.Read(content, 0, content.Length);
            }

            string hash;
            using (var sha256 = SHA256.Create())
            {
                var hashBytes = sha256.ComputeHash(content);
                hash = BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
            }

            var signature = new AssetSignature
            {
                AssetGuid = assetGuid,
                FilePath = Path.GetFileName(assetPath),
                FileSize = content.Length,
                Sha256 = hash,
                SignedAtUtc = DateTime.UtcNow,
                EngineVersion = "Yama Engine 1.0"
            };

            // 写入签名文件
            var sigPath = assetPath + SignatureExtension;
            var json = JsonSerializer.Serialize(signature, new JsonSerializerOptions
            {
                WriteIndented = true
            });
            File.WriteAllText(sigPath, json);

            return signature;
        }

        /// <summary>
        /// 验证资产的完整性 (重新计算哈希并与签名比对)。
        /// 返回 true 表示文件未被篡改。
        /// </summary>
        public static bool Verify(string assetPath, AssetSignature expectedSignature)
        {
            if (!File.Exists(assetPath)) return false;

            byte[] content;
            using (var fs = File.OpenRead(assetPath))
            {
                content = new byte[fs.Length];
                fs.Read(content, 0, content.Length);
            }

            if (content.Length != expectedSignature.FileSize) return false;

            string hash;
            using (var sha256 = SHA256.Create())
            {
                var hashBytes = sha256.ComputeHash(content);
                hash = BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
            }

            return string.Equals(hash, expectedSignature.Sha256, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 从签名文件加载签名信息。
        /// </summary>
        public static AssetSignature LoadSignature(string signaturePath)
        {
            if (!File.Exists(signaturePath))
                throw new FileNotFoundException($"签名文件不存在: {signaturePath}");
            return JsonSerializer.Deserialize<AssetSignature>(
                File.ReadAllText(signaturePath))
                ?? throw new InvalidOperationException("无效的签名文件");
        }

        /// <summary>
        /// 为整个 Cook 目录生成清单签名 (汇总所有资产签名的根哈希)。
        /// 用于热更新包的整体校验。
        /// </summary>
        public static string SignCookDirectory(string cookDir, out int signedFileCount)
        {
            signedFileCount = 0;
            var allHashes = new StringBuilder();
            foreach (var file in Directory.EnumerateFiles(cookDir, "*.*", SearchOption.AllDirectories)
                .Where(f => !f.EndsWith(SignatureExtension) && !f.EndsWith(".json")))
            {
                using var sha256 = SHA256.Create();
                using var stream = File.OpenRead(file);
                var hash = sha256.ComputeHash(stream);
                allHashes.Append(BitConverter.ToString(hash).Replace("-", ""));
                signedFileCount++;
            }

            // 对所有资产哈希再次哈希, 得到根哈希
            using var rootSha = SHA256.Create();
            var rootHash = rootSha.ComputeHash(Encoding.UTF8.GetBytes(allHashes.ToString()));
            return BitConverter.ToString(rootHash).Replace("-", "").ToLowerInvariant();
        }
    }

    /// <summary>资产签名信息。</summary>
    public sealed class AssetSignature
    {
        /// <summary>资产 GUID。</summary>
        public string AssetGuid { get; set; } = string.Empty;
        /// <summary>文件路径 (相对)。</summary>
        public string FilePath { get; set; } = string.Empty;
        /// <summary>文件大小 (字节)。</summary>
        public long FileSize { get; set; }
        /// <summary>SHA-256 哈希。</summary>
        public string Sha256 { get; set; } = string.Empty;
        /// <summary>签名时间 (UTC)。</summary>
        public DateTime SignedAtUtc { get; set; }
        /// <summary>引擎版本。</summary>
        public string EngineVersion { get; set; } = string.Empty;
    }

    // ──────────────────────────────────────────────────────────────────────────
    // AssetCooker 主入口
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 资源 Cook 系统主入口。
    /// 协调纹理、网格、音频、动画、材质各 Cook 模块,
    /// 根据资产类型自动分派到对应 Cooker, 并生成统一的 Cook 报告。
    /// 
    /// 典型用法:
    /// <code>
    /// var cooker = new AssetCooker(assetDatabase, platformConfig);
    /// var report = cooker.CookAll(cookOutputDir, dependencyGraph);
    /// </code>
    /// </summary>
    public sealed class AssetCooker
    {
        /// <summary>资产数据库引用。</summary>
        private readonly AssetDatabase _assetDatabase;
        /// <summary>平台配置。</summary>
        private readonly CookPlatformConfig _platformConfig;
        /// <summary>平台路径映射器。</summary>
        private readonly PlatformAssetPathMapper _pathMapper;
        /// <summary>Cook 进度回调 (当前资产索引, 总数, 资产名)。</summary>
        public Action<int, int, string>? OnProgress { get; set; }
        /// <summary>单个资产 Cook 完成回调。</summary>
        public Action<AssetCookResult>? OnAssetCooked { get; set; }

        /// <summary>
        /// 构造 AssetCooker。
        /// </summary>
        /// <param name="assetDatabase">资产数据库 (提供资产列表与元数据)。</param>
        /// <param name="platformConfig">目标平台 Cook 配置。</param>
        public AssetCooker(AssetDatabase assetDatabase, CookPlatformConfig platformConfig)
        {
            _assetDatabase = assetDatabase ?? throw new ArgumentNullException(nameof(assetDatabase));
            _platformConfig = platformConfig ?? throw new ArgumentNullException(nameof(platformConfig));
            _pathMapper = new PlatformAssetPathMapper(platformConfig);
        }

        /// <summary>
        /// Cook 所有资产。
        /// 遍历资产数据库中的所有资产, 按类型分派到对应 Cooker,
        /// 输出到指定目录, 生成 Cook 报告与依赖清单。
        /// </summary>
        /// <param name="outputDir">Cook 输出根目录。</param>
        /// <param name="dependencyGraph">资产依赖图 (可选, 用于序列化依赖清单)。</param>
        /// <returns>Cook 报告。</returns>
        public CookReport CookAll(string outputDir, AssetDependencyGraph? dependencyGraph = null)
        {
            var report = new CookReport
            {
                Platform = _platformConfig.Platform,
                StartedAtUtc = DateTime.UtcNow
            };

            var contentRoot = _pathMapper.GetContentRoot();
            var cookRoot = Path.Combine(outputDir, contentRoot);
            Directory.CreateDirectory(cookRoot);

            var allAssets = _assetDatabase.GetAllAssets();
            int total = allAssets.Count;
            int index = 0;

            foreach (var asset in allAssets)
            {
                index++;
                OnProgress?.Invoke(index, total, asset.Name);

                var sourcePath = Path.Combine(_assetDatabase.RootPath,
                    asset.AssetPath.Replace('/', Path.DirectorySeparatorChar));

                if (!File.Exists(sourcePath))
                {
                    report.Results.Add(new AssetCookResult
                    {
                        AssetGuid = asset.Guid.ToString("N"),
                        SourcePath = asset.AssetPath,
                        AssetType = asset.Type,
                        Success = false,
                        Warnings = { "源文件不存在, 跳过" }
                    });
                    continue;
                }

                AssetCookResult result = asset.Type switch
                {
                    AssetType.Texture2D or AssetType.TextureCube =>
                        TextureCooker.Cook(asset, sourcePath, cookRoot, _platformConfig, new TextureCookSettings()),
                    AssetType.Mesh =>
                        MeshCooker.Cook(asset, sourcePath, cookRoot, _platformConfig, new MeshCookSettings()),
                    AssetType.Audio =>
                        AudioCooker.Cook(asset, sourcePath, cookRoot, _platformConfig, new AudioCookSettings()),
                    AssetType.Animation =>
                        AnimationCooker.Cook(asset, sourcePath, cookRoot, _platformConfig, new AnimationCookSettings()),
                    AssetType.Material =>
                        MaterialCooker.Cook(asset, sourcePath, cookRoot, _platformConfig, new MaterialCookSettings()),
                    _ => CookGenericAsset(asset, sourcePath, cookRoot)
                };

                // 为 Cook 后资产生成签名
                if (result.Success && File.Exists(result.OutputPath))
                {
                    try
                    {
                        AssetSigner.Sign(result.OutputPath, result.AssetGuid);
                    }
                    catch (Exception ex)
                    {
                        result.Warnings.Add($"签名失败: {ex.Message}");
                    }
                }

                report.Results.Add(result);
                OnAssetCooked?.Invoke(result);
            }

            // 按类型汇总大小
            foreach (var r in report.Results.Where(r => r.Success))
            {
                var key = r.AssetType.ToString();
                report.SizeByType.TryGetValue(key, out var existing);
                report.SizeByType[key] = existing + r.CookedSize;
            }

            // 序列化依赖清单
            if (dependencyGraph != null)
            {
                var manifestPath = Path.Combine(cookRoot, "dependency_manifest.json");
                try
                {
                    AssetDependencySerializer.Serialize(dependencyGraph, manifestPath);
                }
                catch (Exception ex)
                {
                    report.Warnings.Add($"依赖清单序列化失败: {ex.Message}");
                }
            }

            // 生成目录签名
            try
            {
                var rootHash = AssetSigner.SignCookDirectory(cookRoot, out int signedCount);
                report.Warnings.Add($"Cook 目录根签名: {rootHash} ({signedCount} 个文件)");
            }
            catch (Exception ex)
            {
                report.Warnings.Add($"目录签名失败: {ex.Message}");
            }

            report.FinishedAtUtc = DateTime.UtcNow;
            return report;
        }

        /// <summary>
        /// Cook 单个资产 (增量 Cook)。
        /// 用于资产变更时仅重新 Cook 受影响的资产。
        /// </summary>
        public AssetCookResult CookSingle(AssetMetadata asset, string outputDir)
        {
            var sourcePath = Path.Combine(_assetDatabase.RootPath,
                asset.AssetPath.Replace('/', Path.DirectorySeparatorChar));

            var contentRoot = _pathMapper.GetContentRoot();
            var cookRoot = Path.Combine(outputDir, contentRoot);

            return asset.Type switch
            {
                AssetType.Texture2D or AssetType.TextureCube =>
                    TextureCooker.Cook(asset, sourcePath, cookRoot, _platformConfig, new TextureCookSettings()),
                AssetType.Mesh =>
                    MeshCooker.Cook(asset, sourcePath, cookRoot, _platformConfig, new MeshCookSettings()),
                AssetType.Audio =>
                    AudioCooker.Cook(asset, sourcePath, cookRoot, _platformConfig, new AudioCookSettings()),
                AssetType.Animation =>
                    AnimationCooker.Cook(asset, sourcePath, cookRoot, _platformConfig, new AnimationCookSettings()),
                AssetType.Material =>
                    MaterialCooker.Cook(asset, sourcePath, cookRoot, _platformConfig, new MaterialCookSettings()),
                _ => CookGenericAsset(asset, sourcePath, cookRoot)
            };
        }

        /// <summary>
        /// 通用资产 Cook (Shader / Prefab / Scene 等直接复制)。
        /// 保留原始格式, 仅添加平台路径映射。
        /// </summary>
        private AssetCookResult CookGenericAsset(AssetMetadata asset, string sourceFile, string outputDir)
        {
            var result = new AssetCookResult
            {
                AssetGuid = asset.Guid.ToString("N"),
                SourcePath = asset.AssetPath,
                AssetType = asset.Type,
                Success = false
            };

            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                if (!File.Exists(sourceFile))
                {
                    result.Warnings.Add("源文件不存在");
                    result.CookTimeMs = sw.Elapsed.TotalMilliseconds;
                    return result;
                }

                result.OriginalSize = new FileInfo(sourceFile).Length;
                var baseName = Path.GetFileNameWithoutExtension(sourceFile);
                var platformSuffix = _platformConfig.Platform.ToString().ToLowerInvariant();
                var ext = Path.GetExtension(sourceFile);
                var outFileName = $"{baseName}_{platformSuffix}{ext}";
                result.OutputPath = Path.Combine(outputDir, outFileName);

                File.Copy(sourceFile, result.OutputPath, true);
                result.CookedSize = result.OriginalSize;
                result.AppliedCompression = "Passthrough (无处理)";
                result.Success = true;
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"通用 Cook 异常: {ex.Message}");
            }

            sw.Stop();
            result.CookTimeMs = sw.Elapsed.TotalMilliseconds;
            return result;
        }

        /// <summary>
        /// 获取平台路径映射器。
        /// 构建管线使用此方法将资产路径映射到 Cook 后路径。
        /// </summary>
        public PlatformAssetPathMapper GetPathMapper() => _pathMapper;

        /// <summary>
        /// 获取平台配置。
        /// </summary>
        public CookPlatformConfig GetPlatformConfig() => _platformConfig;
    }
}
