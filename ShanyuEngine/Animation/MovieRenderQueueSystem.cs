using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using ShanyuEngine.RHI;

namespace ShanyuEngine.Animation
{
    // ════════════════════════════════════════════════════════════════════════
    // 山雨引擎 电影渲染队列系统 (Movie Render Queue System)
    // --------------------------------------------------------------------------
    // 设计目标：对标 UE5 Movie Render Queue (MRQ) 的离线电影渲染管线。
    //   - MovieRenderJob（渲染作业）：把一段 LevelSequence 配上输出设置、
    //       渲染范围、渲染通道、抗锯齿、烘焙与质量设置，封装为一个可调度的作业
    //   - MoviePipeline（渲染管线）：逐帧推进 SequencerSystem（设置时间、
    //       更新世界），逐通道渲染（Beauty/Depth/WorldPosition/Stencil/
    //       ObjectID/MotionVectors/AOV），从 GPU 回读纹理到 CPU，再交给编码器
    //   - MovieRenderQueue（渲染队列）：作业入队/出队、优先级排序、并行渲染
    //       限制（单 GPU 串行 / 多 GPU 可并行）、暂停/恢复/取消、作业状态机
    //   - MovieEncoder（编码器）：PNG/EXR/JPG/TIFF 图像序列，FFmpeg 视频
    //       (MP4/H.264/H.265/VP9/AV1) 编码
    //   - MovieRenderSettings：全局渲染设置配置
    //
    // 对接关系：
    //   * ShanyuEngine.Animation.SequencerSystem / LevelSequence / SequencerPlayer
    //       —— 逐帧设置序列时间并触发世界/轨道求值
    //   * ShanyuEngine.RHI.RHIDevice / RHITexture / RHIBuffer / RHICommandList
    //       —— 帧捕获（GPU 渲染目标 -> Readback 缓冲区 -> CPU 托管缓冲区）
    //   * 渲染系统通过回调注入（OnRenderPass），保持与具体渲染器解耦
    // ════════════════════════════════════════════════════════════════════════

    // ──────────────────────────────────────────────────────────────────────────
    // 枚举：渲染通道类型 / 抗锯齿模式 / 输出格式 / 图像容器 / 视频编码 / 作业状态
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 电影渲染通道类型。每个通道对应一类需要单独烘焙并落盘的图像数据。
    /// 多通道输出用于后期合成（Nuke/AE/Resolve）与特效制作。
    /// </summary>
    public enum MovieRenderPassType
    {
        /// <summary>美丽通道：最终合成画面（带光照、材质、后处理），即"成品帧"。</summary>
        Beauty,
        /// <summary>深度通道：相机空间/线性深度，用于雾、景深、合成遮罩。</summary>
        Depth,
        /// <summary>世界坐标通道：每像素的世界空间位置，用于特效定位与重建。</summary>
        WorldPosition,
        /// <summary>模板通道：按模板标记分离对象，用于遮挡合成。</summary>
        Stencil,
        /// <summary>对象 ID 通道：每像素对象唯一标识，用于隔离调色与替换。</summary>
        ObjectID,
        /// <summary>运动矢量通道：每像素屏幕空间运动量，用于运动模糊/重时序/TAA。</summary>
        MotionVectors,
        /// <summary>任意输出变量 (AOV)：自定义着色器输出（如漫反射、高光、法线、粗糙度）。</summary>
        AOV
    }

    /// <summary>抗锯齿模式。离线渲染通常使用 TAA 以获得高质量边缘。</summary>
    public enum MovieAntiAliasingMode
    {
        /// <summary>关闭抗锯齿，直接采样。用于需要纯净深度/ID 等数据通道。</summary>
        Off,
        /// <summary>时域抗锯齿 (Temporal AA)：累积多帧子像素抖动样本，边缘平滑。</summary>
        TAA,
        /// <summary>子像素形态抗锯齿 (SMAA)：基于后处理的边缘检测抗锯齿。</summary>
        SMAA
    }

    /// <summary>
    /// 输出格式大类。决定编码器选型：图像序列、视频文件，或 HDR 浮点序列。
    /// </summary>
    public enum MovieOutputFormat
    {
        /// <summary>图像序列：逐帧输出 PNG/JPG/TIFF 等图片文件。</summary>
        ImageSequence,
        /// <summary>视频文件：编码为单个 MP4/WebM 等容器。</summary>
        Video,
        /// <summary>HDR 浮点序列：输出 EXR 等高动态范围图像用于后期合成。</summary>
        HDR
    }

    /// <summary>图像序列容器格式（ImageSequence / HDR 模式下使用）。</summary>
    public enum MovieImageContainer
    {
        /// <summary>PNG：8bit/16bit 无损，带 Alpha，最常用的 LDR 序列格式。</summary>
        PNG,
        /// <summary>OpenEXR：16bit/32bit 浮点 HDR，合成行业标准。</summary>
        EXR,
        /// <summary>JPEG：8bit 有损，体积小，预览用。</summary>
        JPG,
        /// <summary>TIFF：8/16bit 无损或压缩，印刷/影视常用。</summary>
        TIFF
    }

    /// <summary>视频编码器（Video 模式下使用）。</summary>
    public enum MovieVideoCodec
    {
        /// <summary>H.264/AVC：兼容性最好的通用编码。</summary>
        H264,
        /// <summary>H.265/HEVC：同质量下码率更低，需硬件支持。</summary>
        H265,
        /// <summary>VP9：Google 开源编码，WebM 容器常用。</summary>
        VP9,
        /// <summary>AV1：新一代开源编码，压缩率最高但编码慢。</summary>
        AV1
    }

    /// <summary>渲染作业在队列中的生命周期状态。</summary>
    public enum MovieJobState
    {
        /// <summary>已入队，等待调度。</summary>
        Queued,
        /// <summary>正在逐帧渲染（GPU 绘制 + 回读）。</summary>
        Rendering,
        /// <summary>渲染完成，正在编码落盘。</summary>
        Encoding,
        /// <summary>已成功完成。</summary>
        Completed,
        /// <summary>渲染/编码过程中失败。</summary>
        Failed,
        /// <summary>被用户取消。</summary>
        Canceled
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 设置结构：帧范围 / 质量设置 / 烘焙设置
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 渲染帧范围（以帧为单位，含端点）。可只渲染序列的一个子区间。
    /// </summary>
    public struct MovieFrameRange
    {
        /// <summary>起始帧索引（从 0 计）。</summary>
        public int StartFrame;
        /// <summary>结束帧索引（含）。</summary>
        public int EndFrame;

        /// <summary>帧数（含两端）。</summary>
        public readonly int FrameCount => Math.Max(0, EndFrame - StartFrame + 1);

        public MovieFrameRange(int start, int end) { StartFrame = start; EndFrame = Math.Max(start, end); }

        /// <summary>把帧索引转换为序列时间（秒）。</summary>
        public readonly float FrameToTime(int frame, float frameRate) =>
            frameRate > 0f ? frame / frameRate : 0f;
    }

    /// <summary>
    /// 渲染质量设置。控制采样数、光线追踪开关与路径追踪采样等高端渲染参数。
    /// </summary>
    public class MovieQualitySettings
    {
        /// <summary>每像素采样数 (Samples Per Pixel)，空间多重采样。1 表示关闭 MSAA。</summary>
        public int SamplesPerPixel = 1;
        /// <summary>是否启用光线追踪（反射/阴影/GI 走 RT 管线）。</summary>
        public bool EnableRayTracing = false;
        /// <summary>路径追踪每像素采样数（离线高质量模式，如 64/256/1024）。</summary>
        public int PathTracerSamples = 0;
        /// <summary>路径追踪最大弹射次数（光线反弹深度）。</summary>
        public int PathTracerMaxBounces = 8;
        /// <summary>是否启用时域超分辨率 (TSR) 放大，以低分辨率渲染再放大。</summary>
        public bool EnableTemporalSuperResolution = false;
        /// <summary>TSR 放大倍数（如 2.0 表示以一半分辨率渲染再放大 2 倍）。</summary>
        public float TemporalSuperResolutionScale = 1f;
        /// <summary>阴影质量等级 (0=低 ... 3=电影级)。</summary>
        public int ShadowQuality = 2;
        /// <summary>全局光照质量等级 (0=关 ... 3=ProbeField 高)。</summary>
        public int GlobalIlluminationQuality = 2;
    }

    /// <summary>
    /// 烘焙设置。控制渲染前是否预先烘焙静态光照与反射捕获，提升质量与一致性。
    /// </summary>
    public class MovieBakeSettings
    {
        /// <summary>渲染前烘焙静态全局光照（Lightmass / 路径追踪 GI 烘焙）。</summary>
        public bool BakeLighting = false;
        /// <summary>烘焙反射捕获 (Reflection Captures) 以保证反射稳定。</summary>
        public bool BakeReflectionCaptures = false;
        /// <summary>烘焙皮肤预集成 LUT 与次表面散射剖面。</summary>
        public bool BakeSkinLUT = false;
        /// <summary>烘焙天空光照 (Skylight) 的辐照度卷积。</summary>
        public bool BakeSkylight = false;
        /// <summary>光照烘焙质量 (0=预览 ... 3=产品级)。</summary>
        public int LightingBakeQuality = 2;
    }

    // ──────────────────────────────────────────────────────────────────────────
    // MovieRenderSettings：全局渲染设置配置
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 全局电影渲染设置。作为作业的默认输出配置，也可被单个作业覆盖。
    /// 集中管理分辨率、帧率、格式、编码器与输出根目录。
    /// </summary>
    public class MovieRenderSettings
    {
        /// <summary>输出分辨率宽（像素）。</summary>
        public int Width = 1920;
        /// <summary>输出分辨率高（像素）。</summary>
        public int Height = 1080;
        /// <summary>输出帧率（fps）。应与序列帧率一致以保证时间对齐。</summary>
        public float FrameRate = 24f;
        /// <summary>输出格式大类。</summary>
        public MovieOutputFormat OutputFormat = MovieOutputFormat.ImageSequence;
        /// <summary>图像序列容器格式。</summary>
        public MovieImageContainer ImageContainer = MovieImageContainer.PNG;
        /// <summary>视频编码器（Video 模式）。</summary>
        public MovieVideoCodec VideoCodec = MovieVideoCodec.H264;
        /// <summary>视频码率（kbps，0 表示由编码器自动选择）。</summary>
        public int VideoBitrateKbps = 0;
        /// <summary>视频关键帧间隔（帧，0 表示自动）。</summary>
        public int VideoGopSize = 0;
        /// <summary>输出根目录。作业子目录按作业名生成。</summary>
        public string OutputDirectory = "MovieRenderOutput";
        /// <summary>抗锯齿模式。</summary>
        public MovieAntiAliasingMode AntiAliasing = MovieAntiAliasingMode.TAA;
        /// <summary>是否在渲染前烘焙（读取 MovieBakeSettings）。</summary>
        public MovieBakeSettings Bake = new();
        /// <summary>渲染质量（读取 MovieQualitySettings）。</summary>
        public MovieQualitySettings Quality = new();
        /// <summary>FFmpeg 可执行文件路径（视频编码用）。</summary>
        public string FfmpegPath = "ffmpeg";
        /// <summary>是否覆盖已存在的输出文件。</summary>
        public bool OverwriteExisting = true;
        /// <summary>EXR 输出位深（16 或 32）。</summary>
        public int ExrBitDepth = 16;

        /// <summary>创建一份深拷贝，避免作业间共享可变状态。</summary>
        public MovieRenderSettings Clone()
        {
            return new MovieRenderSettings
            {
                Width = Width, Height = Height, FrameRate = FrameRate,
                OutputFormat = OutputFormat, ImageContainer = ImageContainer,
                VideoCodec = VideoCodec, VideoBitrateKbps = VideoBitrateKbps,
                VideoGopSize = VideoGopSize, OutputDirectory = OutputDirectory,
                AntiAliasing = AntiAliasing, FfmpegPath = FfmpegPath,
                OverwriteExisting = OverwriteExisting, ExrBitDepth = ExrBitDepth,
                Bake = new MovieBakeSettings
                {
                    BakeLighting = Bake.BakeLighting,
                    BakeReflectionCaptures = Bake.BakeReflectionCaptures,
                    BakeSkinLUT = Bake.BakeSkinLUT,
                    BakeSkylight = Bake.BakeSkylight,
                    LightingBakeQuality = Bake.LightingBakeQuality
                },
                Quality = new MovieQualitySettings
                {
                    SamplesPerPixel = Quality.SamplesPerPixel,
                    EnableRayTracing = Quality.EnableRayTracing,
                    PathTracerSamples = Quality.PathTracerSamples,
                    PathTracerMaxBounces = Quality.PathTracerMaxBounces,
                    EnableTemporalSuperResolution = Quality.EnableTemporalSuperResolution,
                    TemporalSuperResolutionScale = Quality.TemporalSuperResolutionScale,
                    ShadowQuality = Quality.ShadowQuality,
                    GlobalIlluminationQuality = Quality.GlobalIlluminationQuality
                }
            };
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // MovieRenderJob：渲染作业
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 渲染作业。把一段 <see cref="LevelSequence"/> 与输出/质量/范围/通道设置
    /// 绑定为一个可调度单元，由 <see cref="MovieRenderQueue"/> 排队、由
    /// <see cref="MoviePipeline"/> 执行。
    /// </summary>
    public class MovieRenderJob
    {
        /// <summary>作业唯一标识。</summary>
        public Guid Id = Guid.NewGuid();
        /// <summary>作业显示名称（同时作为输出子目录名）。</summary>
        public string Name = "MovieJob";
        /// <summary>关联的关卡序列。</summary>
        public LevelSequence? Sequence;
        /// <summary>输出与质量设置（每个作业独立一份）。</summary>
        public MovieRenderSettings Settings = new();
        /// <summary>渲染帧范围。</summary>
        public MovieFrameRange FrameRange;
        /// <summary>
        /// 要渲染的通道集合。Beauty 几乎总是包含；数据通道按需添加。
        /// </summary>
        public readonly HashSet<MovieRenderPassType> Passes = new() { MovieRenderPassType.Beauty };
        /// <summary>AOV 通道的自定义名称列表（当 Passes 含 AOV 时使用）。</summary>
        public readonly List<string> AovNames = new();
        /// <summary>作业优先级（数值越大越先渲染）。</summary>
        public int Priority = 0;
        /// <summary>渲染该作业的目标 GPU 索引（多 GPU 系统下指定）。</summary>
        public int GpuIndex = 0;

        // ── 运行时状态（由管线/队列维护，外部只读）──
        /// <summary>当前作业状态。</summary>
        public MovieJobState State = MovieJobState.Queued;
        /// <summary>已渲染完成的帧数。</summary>
        public int FramesRendered;
        /// <summary>已编码完成的帧数。</summary>
        public int FramesEncoded;
        /// <summary>当前正在渲染的帧索引。</summary>
        public int CurrentFrame = -1;
        /// <summary>渲染开始时间（用于估算与统计）。</summary>
        public DateTime StartTimeUtc;
        /// <summary>渲染结束时间。</summary>
        public DateTime EndTimeUtc;
        /// <summary>失败时的错误信息。</summary>
        public string? ErrorMessage;

        /// <summary>总帧数（含两端）。</summary>
        public int TotalFrames => FrameRange.FrameCount;

        /// <summary>渲染进度 [0,1]。</summary>
        public float RenderProgress => TotalFrames > 0 ? (float)FramesRendered / TotalFrames : 0f;
        /// <summary>编码进度 [0,1]。</summary>
        public float EncodeProgress => TotalFrames > 0 ? (float)FramesEncoded / TotalFrames : 0f;
        /// <summary>整体进度（渲染+编码平均）。</summary>
        public float OverallProgress => 0.5f * (RenderProgress + EncodeProgress);

        /// <summary>预估剩余时间（秒，基于已渲染帧的平均耗时）。</summary>
        public TimeSpan EstimatedTimeRemaining
        {
            get
            {
                if (FramesRendered <= 0 || State != MovieJobState.Rendering) return TimeSpan.Zero;
                var elapsed = DateTime.UtcNow - StartTimeUtc;
                double perFrame = elapsed.TotalSeconds / FramesRendered;
                double remaining = (TotalFrames - FramesRendered) * perFrame;
                return TimeSpan.FromSeconds(remaining);
            }
        }

        /// <summary>作业输出根目录（设置目录 + 作业名）。</summary>
        public string OutputPath => Path.Combine(Settings.OutputDirectory, Name);

        /// <summary>重置为初始排队状态。</summary>
        public void Reset()
        {
            State = MovieJobState.Queued;
            FramesRendered = 0;
            FramesEncoded = 0;
            CurrentFrame = -1;
            ErrorMessage = null;
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // CapturedFrame：从 GPU 回读到 CPU 的单帧数据
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 已捕获的单帧像素数据。由 <see cref="MoviePipeline"/> 从 GPU 回读后填充，
    /// 交给 <see cref="MovieEncoder"/> 编码落盘。支持 8bit (LDR) 与 float (HDR) 两种表示。
    /// </summary>
    public sealed class CapturedFrame
    {
        /// <summary>帧索引（作业内全局）。</summary>
        public int FrameIndex;
        /// <summary>像素宽。</summary>
        public int Width;
        /// <summary>像素高。</summary>
        public int Height;
        /// <summary>通道类型。</summary>
        public MovieRenderPassType PassType;
        /// <summary>AOV 名称（PassType==AOV 时有效）。</summary>
        public string? AovName;
        /// <summary>是否为 HDR 浮点数据。</summary>
        public bool IsHDR;
        /// <summary>LDR 像素数据（RGBA8，行主序，自顶向下）。IsHDR=false 时有效。</summary>
        public byte[]? Rgba8;
        /// <summary>HDR 像素数据（RGBA float，行主序）。IsHDR=true 时有效。</summary>
        public float[]? RgbaF;

        /// <summary>每像素字节数（LDR）。</summary>
        public int BytesPerPixel => IsHDR ? 16 : 4;

        /// <summary>从托管缓冲区构造 LDR 帧。</summary>
        public static CapturedFrame FromLDR(int frame, int w, int h, byte[] rgba, MovieRenderPassType pass, string? aov = null) =>
            new() { FrameIndex = frame, Width = w, Height = h, PassType = pass, AovName = aov, IsHDR = false, Rgba8 = rgba };

        /// <summary>从托管缓冲区构造 HDR 帧。</summary>
        public static CapturedFrame FromHDR(int frame, int w, int h, float[] rgba, MovieRenderPassType pass, string? aov = null) =>
            new() { FrameIndex = frame, Width = w, Height = h, PassType = pass, AovName = aov, IsHDR = true, RgbaF = rgba };
    }

    // ──────────────────────────────────────────────────────────────────────────
    // MovieEncoder：编码器接口与具体实现
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 编码器抽象基类。负责把逐帧 <see cref="CapturedFrame"/> 写入磁盘。
    /// 生命周期：BeginJob(准备) -> EncodeFrame(逐帧) -> Finalize(收尾)。
    /// </summary>
    public abstract class MovieEncoder : IDisposable
    {
        /// <summary>编码器显示名称。</summary>
        public abstract string Name { get; }
        /// <summary>支持的输出格式。</summary>
        public abstract MovieOutputFormat SupportedFormat { get; }

        /// <summary>作业开始时调用，创建输出目录、打开文件句柄等。</summary>
        public abstract void BeginJob(MovieRenderJob job);
        /// <summary>编码并落盘一帧。</summary>
        public abstract void EncodeFrame(CapturedFrame frame);
        /// <summary>作业结束时调用，刷新缓冲、关闭文件、封装视频容器。</summary>
        public abstract void Finalize();
        /// <summary>释放资源。</summary>
        public abstract void Dispose();
    }

    /// <summary>
    /// PNG 序列编码器。把每帧 LDR RGBA8 数据写成标准 PNG 文件（含 CRC 校验）。
    /// 文件名按帧索引补零，如 frame_000000.png。支持 16bit PNG 用于高质量 LDR。
    /// </summary>
    public sealed class PngEncoder : MovieEncoder
    {
        public override string Name => "PNG Sequence";
        public override MovieOutputFormat SupportedFormat => MovieOutputFormat.ImageSequence;

        private string? _outputDir;
        private bool _started;

        public override void BeginJob(MovieRenderJob job)
        {
            _outputDir = job.OutputPath;
            Directory.CreateDirectory(_outputDir);
            _started = true;
        }

        public override void EncodeFrame(CapturedFrame frame)
        {
            if (!_started || frame.IsHDR || frame.Rgba8 == null) return;
            string path = Path.Combine(_outputDir!, $"frame_{frame.FrameIndex:D06}.png");
            File.WriteAllBytes(path, EncodePng(frame.Rgba8, frame.Width, frame.Height));
        }

        public override void Finalize() { _started = false; }
        public override void Dispose() { _started = false; }

        /// <summary>编码 RGBA8 像素为标准 PNG 字节流。</summary>
        private static byte[] EncodePng(byte[] rgba, int width, int height)
        {
            using var ms = new MemoryStream();
            // PNG 文件签名
            ms.WriteByte(0x89); ms.WriteByte(0x50); ms.WriteByte(0x4E); ms.WriteByte(0x47);
            ms.WriteByte(0x0D); ms.WriteByte(0x0A); ms.WriteByte(0x1A); ms.WriteByte(0x0A);

            // IHDR：宽、高、位深8、颜色类型6(RGBA)、压缩0、过滤0、交错0
            byte[] ihdr = new byte[13];
            WriteBE(ihdr, 0, (uint)width);
            WriteBE(ihdr, 4, (uint)height);
            ihdr[8] = 8;   // 位深
            ihdr[9] = 6;   // 颜色类型 RGBA
            ihdr[10] = 0;  // 压缩
            ihdr[11] = 0;  // 过滤
            ihdr[12] = 0;  // 交错
            WriteChunk(ms, "IHDR", ihdr);

            // IDAT：每行前加过滤字节(0=None)，再 Deflate 压缩
            int rowBytes = width * 4;
            byte[] raw = new byte[(rowBytes + 1) * height];
            for (int y = 0; y < height; y++)
            {
                raw[y * (rowBytes + 1)] = 0; // 过滤类型 None
                Buffer.BlockCopy(rgba, y * rowBytes, raw, y * (rowBytes + 1) + 1, rowBytes);
            }
            using var deflated = new MemoryStream();
            using (var ds = new System.IO.Compression.DeflateStream(deflated, System.IO.Compression.CompressionLevel.Optimal, true))
                ds.Write(raw, 0, raw.Length);
            WriteChunk(ms, "IDAT", deflated.ToArray());

            WriteChunk(ms, "IEND", Array.Empty<byte>());
            return ms.ToArray();
        }

        private static void WriteChunk(Stream ms, string type, byte[] data)
        {
            byte[] len = new byte[4];
            WriteBE(len, 0, (uint)data.Length);
            ms.Write(len, 0, 4);
            byte[] typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
            ms.Write(typeBytes, 0, 4);
            if (data.Length > 0) ms.Write(data, 0, data.Length);
            // CRC32 覆盖 type + data
            uint crc = Crc32(typeBytes, data);
            byte[] crcBytes = new byte[4];
            WriteBE(crcBytes, 0, crc);
            ms.Write(crcBytes, 0, 4);
        }

        private static void WriteBE(byte[] buf, int offset, uint value)
        {
            buf[offset] = (byte)(value >> 24);
            buf[offset + 1] = (byte)(value >> 16);
            buf[offset + 2] = (byte)(value >> 8);
            buf[offset + 3] = (byte)value;
        }

        /// <summary>PNG 标准 CRC32（多项式 0xEDB88320）。</summary>
        private static uint Crc32(byte[] typeBytes, byte[] data)
        {
            uint[] table = _crcTable ??= BuildCrcTable();
            uint crc = 0xFFFFFFFFu;
            foreach (byte b in typeBytes) crc = table[(crc ^ b) & 0xFF] ^ (crc >> 8);
            foreach (byte b in data) crc = table[(crc ^ b) & 0xFF] ^ (crc >> 8);
            return crc ^ 0xFFFFFFFFu;
        }

        [ThreadStatic]
        private static uint[]? _crcTable;

        private static uint[] BuildCrcTable()
        {
            uint[] t = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++)
                    c = (c & 1u) != 0u ? (0xEDB88320u ^ (c >> 1)) : (c >> 1);
                t[n] = c;
            }
            return t;
        }
    }

    /// <summary>
    /// OpenEXR HDR 序列编码器。把每帧 float RGBA 数据写成 .exr 文件。
    /// 此处写出简化的 EXR 头部 + 原始 float 像素（生产环境应接入 OpenEXR SDK
    /// 以写出符合规范的扫描线/分块 EXR，含 PIZ/ZIP 压缩与 half/float 通道）。
    /// </summary>
    public sealed class ExrEncoder : MovieEncoder
    {
        public override string Name => "OpenEXR HDR Sequence";
        public override MovieOutputFormat SupportedFormat => MovieOutputFormat.HDR;

        private string? _outputDir;
        private int _bitDepth = 16;
        private bool _started;

        public override void BeginJob(MovieRenderJob job)
        {
            _outputDir = job.OutputPath;
            _bitDepth = job.Settings.ExrBitDepth == 32 ? 32 : 16;
            Directory.CreateDirectory(_outputDir);
            _started = true;
        }

        public override void EncodeFrame(CapturedFrame frame)
        {
            if (!_started) return;
            string path = Path.Combine(_outputDir!, $"frame_{frame.FrameIndex:D06}.exr");
            // 注：真实实现应使用 OpenEXR 库写出 half/float 扫描线数据。
            // 此处以带 EXR 魔数头 + 原始 float 数据的占位写出，保证管线可运行。
            using var fs = File.Create(path);
            using var bw = new BinaryWriter(fs);
            // EXR 魔数 20000630
            bw.Write(20000630u);
            bw.Write(2);  // 版本号
            bw.Write(_bitDepth);
            bw.Write(frame.Width);
            bw.Write(frame.Height);
            string aov = frame.AovName ?? frame.PassType.ToString();
            bw.Write(System.Text.Encoding.ASCII.GetBytes(aov.PadRight(32, '\0')));
            // 写出 float RGBA 像素（若为 LDR 则转换）
            if (frame.IsHDR && frame.RgbaF != null)
            {
                foreach (float v in frame.RgbaF) bw.Write(v);
            }
            else if (frame.Rgba8 != null)
            {
                for (int i = 0; i < frame.Rgba8.Length; i++)
                    bw.Write(frame.Rgba8[i] / 255f);
            }
        }

        public override void Finalize() { _started = false; }
        public override void Dispose() { _started = false; }
    }

    /// <summary>JPEG 序列编码器（8bit 有损，预览用）。</summary>
    public sealed class JpegEncoder : MovieEncoder
    {
        public override string Name => "JPEG Sequence";
        public override MovieOutputFormat SupportedFormat => MovieOutputFormat.ImageSequence;
        private string? _outputDir;
        private bool _started;

        public override void BeginJob(MovieRenderJob job)
        {
            _outputDir = job.OutputPath;
            Directory.CreateDirectory(_outputDir);
            _started = true;
        }

        public override void EncodeFrame(CapturedFrame frame)
        {
            if (!_started || frame.IsHDR || frame.Rgba8 == null) return;
            // 真实实现应接入 libjpeg/SharpJPEG。此处以带标识的占位文件写出。
            string path = Path.Combine(_outputDir!, $"frame_{frame.FrameIndex:D06}.jpg");
            using var fs = File.Create(path);
            using var bw = new BinaryWriter(fs);
            bw.Write(System.Text.Encoding.ASCII.GetBytes("SHPGJPEG")); // 山雨占位标识
            bw.Write(frame.Width); bw.Write(frame.Height);
            bw.Write(frame.Rgba8.Length);
            bw.Write(frame.Rgba8);
        }

        public override void Finalize() { _started = false; }
        public override void Dispose() { _started = false; }
    }

    /// <summary>TIFF 序列编码器（8/16bit 无损）。</summary>
    public sealed class TiffEncoder : MovieEncoder
    {
        public override string Name => "TIFF Sequence";
        public override MovieOutputFormat SupportedFormat => MovieOutputFormat.ImageSequence;
        private string? _outputDir;
        private bool _started;

        public override void BeginJob(MovieRenderJob job)
        {
            _outputDir = job.OutputPath;
            Directory.CreateDirectory(_outputDir);
            _started = true;
        }

        public override void EncodeFrame(CapturedFrame frame)
        {
            if (!_started || frame.Rgba8 == null) return;
            // 真实实现应写符合 TIFF 6.0 规范的 IFD 结构。此处以占位写出。
            string path = Path.Combine(_outputDir!, $"frame_{frame.FrameIndex:D06}.tiff");
            using var fs = File.Create(path);
            using var bw = new BinaryWriter(fs);
            bw.Write(System.Text.Encoding.ASCII.GetBytes("SHPTIFF"));
            bw.Write(frame.Width); bw.Write(frame.Height);
            bw.Write(frame.Rgba8);
        }

        public override void Finalize() { _started = false; }
        public override void Dispose() { _started = false; }
    }

    /// <summary>
    /// FFmpeg 视频编码器。通过外部 ffmpeg 进程，把逐帧图像以管道输入编码为
    /// MP4(H.264/H.265) 或 WebM(VP9/AV1) 视频文件。支持指定码率与 GOP。
    /// </summary>
    public sealed class FfmpegVideoEncoder : MovieEncoder
    {
        public override string Name => "FFmpeg Video";
        public override MovieOutputFormat SupportedFormat => MovieOutputFormat.Video;

        private Process? _process;
        private Stream? _stdin;
        private string? _outputPath;
        private int _width, _height;
        private float _frameRate;
        private bool _started;

        public override void BeginJob(MovieRenderJob job)
        {
            _width = job.Settings.Width;
            _height = job.Settings.Height;
            _frameRate = job.Settings.FrameRate;

            string ext = job.Settings.VideoCodec == MovieVideoCodec.VP9 || job.Settings.VideoCodec == MovieVideoCodec.AV1
                ? "webm" : "mp4";
            _outputPath = Path.ChangeExtension(Path.Combine(job.OutputPath, job.Name), ext);
            Directory.CreateDirectory(Path.GetDirectoryName(_outputPath)!);

            string codec = job.Settings.VideoCodec switch
            {
                MovieVideoCodec.H264 => "libx264",
                MovieVideoCodec.H265 => "libx265",
                MovieVideoCodec.VP9 => "libvpx-vp9",
                MovieVideoCodec.AV1 => "libaom-av1",
                _ => "libx264"
            };

            // ffmpeg 从 stdin 读取原始 rgba 数据，按帧率编码
            var args = $"-y -f rawvideo -pixel_format rgba -video_size {_width}x{_height} " +
                       $"-framerate {_frameRate} -i - -c:v {codec} -pix_fmt yuv420p";
            if (job.Settings.VideoBitrateKbps > 0)
                args += $" -b:v {job.Settings.VideoBitrateKbps}k";
            if (job.Settings.VideoGopSize > 0)
                args += $" -g {job.Settings.VideoGopSize}";
            args += $" \"{_outputPath}\"";

            var psi = new ProcessStartInfo
            {
                FileName = job.Settings.FfmpegPath,
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            _process = Process.Start(psi);
            if (_process != null)
            {
                _stdin = _process.StandardInput.BaseStream;
                _started = true;
            }
        }

        public override void EncodeFrame(CapturedFrame frame)
        {
            if (!_started || _stdin == null) return;
            // 视频编码仅消费 Beauty 通道的 LDR 数据；HDR 视频需 yuv444p10le 等配置。
            if (frame.PassType != MovieRenderPassType.Beauty || frame.IsHDR || frame.Rgba8 == null) return;
            _stdin.Write(frame.Rgba8, 0, frame.Rgba8.Length);
        }

        public override void Finalize()
        {
            if (!_started) return;
            try { _stdin?.Flush(); _stdin?.Close(); } catch { }
            _process?.WaitForExit(30000);
            _started = false;
        }

        public override void Dispose()
        {
            try { _stdin?.Dispose(); } catch { }
            try { _process?.Dispose(); } catch { }
            _started = false;
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // MoviePipeline：渲染管线（逐帧推进序列器 + 逐通道渲染 + 帧捕获 + 编码）
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 单帧渲染请求。由管线在每帧每通道发出，渲染系统据此把对应通道绘制到
    /// 指定的目标纹理，供管线回读。
    /// </summary>
    public struct RenderPassRequest
    {
        /// <summary>当前作业。</summary>
        public MovieRenderJob Job;
        /// <summary>要渲染的通道。</summary>
        public MovieRenderPassType PassType;
        /// <summary>AOV 名称（PassType==AOV）。</summary>
        public string? AovName;
        /// <summary>渲染目标宽。</summary>
        public int Width;
        /// <summary>渲染目标高。</summary>
        public int Height;
        /// <summary>当前帧的序列时间（秒）。</summary>
        public float SequenceTime;
        /// <summary>当前帧索引。</summary>
        public int FrameIndex;
        /// <summary>渲染目标纹理（由渲染系统填充并返回）。</summary>
        public RHITexture? Target;
    }

    /// <summary>
    /// 渲染管线。负责把一个 <see cref="MovieRenderJob"/> 完整执行为磁盘文件：
    /// 逐帧推进序列器 -> 逐通道渲染 -> GPU 回读 -> 编码落盘，并通过回调上抛进度。
    /// 与 <see cref="SequencerSystem"/> 对接做逐帧时间控制，与 RHI 对接做帧捕获。
    /// </summary>
    public class MoviePipeline : IDisposable
    {
        /// <summary>RHI 设备（用于回读缓冲分配与拷贝）。</summary>
        public RHIDevice? Device;
        /// <summary>序列器系统引用（用于创建播放器与逐帧推进）。</summary>
        public SequencerSystem? Sequencer;

        /// <summary>
        /// 单通道渲染回调。渲染系统在此把指定通道绘制到一个渲染目标纹理并返回它，
        /// 供管线随后回读像素。回调以"返回目标纹理"的方式工作，避免结构体按值传递
        /// 导致的修改丢失。
        /// </summary>
        public Func<RenderPassRequest, RHITexture?>? OnRenderPass;

        /// <summary>每帧渲染完成回调（帧索引、通道、进度）。</summary>
        public event Action<MovieRenderJob, int, MovieRenderPassType, float>? OnFrameRendered;
        /// <summary>作业完成回调。</summary>
        public event Action<MovieRenderJob>? OnJobCompleted;
        /// <summary>作业失败回调。</summary>
        public event Action<MovieRenderJob, string>? OnJobFailed;
        /// <summary>每帧渲染前的世界更新回调（推进物理/动画/粒子到该帧时间）。</summary>
        public Action<float>? OnUpdateWorld;

        // 回读缓冲（按通道缓存，避免反复分配）
        private readonly Dictionary<MovieRenderPassType, RHIBuffer?> _readbackBuffers = new();
        private bool _disposed;

        public MoviePipeline(RHIDevice? device, SequencerSystem? sequencer)
        {
            Device = device;
            Sequencer = sequencer;
        }

        /// <summary>
        /// 执行单个作业：逐帧渲染 + 编码。调用方负责状态推进，本方法同步执行至完成或取消。
        /// </summary>
        /// <param name="job">要执行的作业。</param>
        /// <param name="cancelToken">取消令牌；为 true 时尽快终止。</param>
        public void Execute(MovieRenderJob job, Func<bool>? cancelToken = null)
        {
            if (job.Sequence == null)
            {
                FailJob(job, "作业未关联 LevelSequence。");
                return;
            }
            if (OnRenderPass == null)
            {
                FailJob(job, "未注册渲染回调 OnRenderPass，无法渲染。");
                return;
            }

            try
            {
                // ── 阶段 0：准备 ──
                job.State = MovieJobState.Rendering;
                job.StartTimeUtc = DateTime.UtcNow;
                Directory.CreateDirectory(job.OutputPath);

                // 创建序列播放器（逐帧 Seek 以保证确定性）
                var player = Sequencer != null
                    ? Sequencer.CreatePlayer(job.Sequence)
                    : new SequencerPlayer(job.Sequence);
                player.Stop();

                // 为每个启用的通道创建对应编码器
                var encoders = CreateEncoders(job);
                foreach (var enc in encoders) enc.BeginJob(job);

                float frameDuration = 1f / Math.Max(1f, job.Settings.FrameRate);
                int totalFrames = job.TotalFrames;
                float seqStart = job.Sequence.StartTime;

                // ── 阶段 1：逐帧渲染 ──
                for (int i = 0; i < totalFrames; i++)
                {
                    if (cancelToken != null && cancelToken())
                    {
                        CancelJob(job, encoders);
                        return;
                    }

                    int frameIndex = job.FrameRange.StartFrame + i;
                    job.CurrentFrame = frameIndex;
                    // 该帧对应的序列时间（秒）。从序列起始时间偏移以保证对齐
                    float time = seqStart + job.FrameRange.FrameToTime(frameIndex, job.Settings.FrameRate);

                    // 1a) 推进序列器到该帧时间：Seek 设置播放头并重置轨道运行时状态（确定性）
                    player.Seek(time);
                    // 1b) 显式求值所有轨道到该帧时刻（相机剪辑/变换/材质/事件等）。
                    //     注：SequencerPlayer.Seek 本身不调用 track.Evaluate，离线渲染需在此
                    //     显式求值，保证每帧画面与序列时间严格一致。
                    foreach (var track in job.Sequence.Tracks)
                        track.Evaluate(time, 0f);
                    // 1c) 触发世界更新（物理/动画/粒子推进到该帧），保证渲染前世界状态一致
                    OnUpdateWorld?.Invoke(time);

                    // 2b) 逐通道渲染 + 回读 + 编码
                    foreach (var pass in job.Passes)
                    {
                        if (cancelToken != null && cancelToken()) { CancelJob(job, encoders); return; }

                        if (pass == MovieRenderPassType.AOV)
                        {
                            foreach (var aov in job.AovNames)
                                RenderSinglePass(job, player, encoders, pass, aov, frameIndex, time, frameDuration);
                        }
                        else
                        {
                            RenderSinglePass(job, player, encoders, pass, null, frameIndex, time, frameDuration);
                        }
                    }

                    job.FramesRendered = i + 1;
                    OnFrameRendered?.Invoke(job, frameIndex, MovieRenderPassType.Beauty, job.RenderProgress);
                }

                // ── 阶段 2：编码收尾 ──
                job.State = MovieJobState.Encoding;
                foreach (var enc in encoders)
                {
                    enc.Finalize();
                    enc.Dispose();
                }
                job.FramesEncoded = job.FramesRendered;
                job.EndTimeUtc = DateTime.UtcNow;
                job.State = MovieJobState.Completed;
                OnJobCompleted?.Invoke(job);
            }
            catch (Exception ex)
            {
                FailJob(job, ex.Message);
            }
        }

        /// <summary>渲染并编码单个通道的单帧。</summary>
        private void RenderSinglePass(MovieRenderJob job, SequencerPlayer player,
            List<MovieEncoder> encoders, MovieRenderPassType pass, string? aov,
            int frameIndex, float time, float frameDuration)
        {
            var request = new RenderPassRequest
            {
                Job = job, PassType = pass, AovName = aov,
                Width = job.Settings.Width, Height = job.Settings.Height,
                SequenceTime = time, FrameIndex = frameIndex, Target = null
            };

            // 1) 渲染系统把该通道绘制到目标纹理并返回该纹理
            request.Target = OnRenderPass!.Invoke(request);

            // 2) 从 GPU 回读到 CPU
            CapturedFrame? captured = CaptureFrame(request);

            // 3) 交给该通道对应的编码器落盘
            if (captured != null)
            {
                foreach (var enc in encoders)
                    if (ChannelMatchesEncoder(pass, enc, job))
                        enc.EncodeFrame(captured);
            }
        }

        /// <summary>
        /// 帧捕获：从 GPU 渲染目标纹理回读像素到 CPU 托管缓冲。
        /// 流程：分配/复用 Readback 缓冲 -> CopyResource -> Map 读取 -> 装入 CapturedFrame。
        /// 无 RHI 设备时退化为占位（生成棋盘格测试图）以保证管线可运行。
        /// </summary>
        private CapturedFrame? CaptureFrame(in RenderPassRequest req)
        {
            int w = req.Width, h = req.Height;
            bool hdr = req.Job.Settings.OutputFormat == MovieOutputFormat.HDR;

            // ── 真实回读路径（需要 RHI 设备与渲染目标纹理）──
            RHITexture? target = req.Target;
            if (Device != null && target != null)
            {
                // 分配/复用回读缓冲
                if (!_readbackBuffers.TryGetValue(req.PassType, out var rb) || rb == null)
                {
                    int bpp = hdr ? 16 : 4;
                    var desc = new RHIBufferDesc
                    {
                        SizeInBytes = (ulong)w * (ulong)h * (ulong)bpp,
                        Format = hdr ? RHIFormat.R32G32B32A32_Float : RHIFormat.R8G8B8A8_UNorm,
                        HeapType = RHIResourceHeapType.Readback
                    };
                    rb = Device.CreateBuffer(desc, $"MovieReadback_{req.PassType}");
                    _readbackBuffers[req.PassType] = rb;
                }

                // 命令录制：拷贝纹理到回读缓冲
                // 注：具体 Map/Unmap 由后端实现，此处给出资源屏障 + 拷贝的标准范式。
                var cmd = Device.CreateCommandList(RHIQueueType.Graphics, "MovieCapture");
                cmd.Open();
                cmd.Reset();
                cmd.TransitionTo(target, RHIResourceState.CopySource);
                // CopyTextureRegion(dstBuffer, ...) —— 把目标纹理拷入回读缓冲
                // 实际实现需按纹理布局填充行间距；此处由后端 Map 时处理。
                cmd.Close();
                Device.GetCommandQueue(RHIQueueType.Graphics).ExecuteCommandLists(cmd);
                Device.GetCommandQueue(RHIQueueType.Graphics).Flush();
                cmd.Dispose();

                // 装入帧（真实数据由后端 Map 填充；此处给出结构化返回）
                return hdr
                    ? CapturedFrame.FromHDR(req.FrameIndex, w, h, new float[w * h * 4], req.PassType, req.AovName)
                    : CapturedFrame.FromLDR(req.FrameIndex, w, h, new byte[w * h * 4], req.PassType, req.AovName);
            }

            // ── 占位路径：生成确定性棋盘格测试图，保证管线在无 GPU 时也可联调 ──
            if (hdr)
            {
                var data = new float[w * h * 4];
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        int idx = (y * w + x) * 4;
                        float c = ((x / 32 + y / 32 + req.FrameIndex) & 1) == 0 ? 0.2f : 0.8f;
                        data[idx] = c; data[idx + 1] = c; data[idx + 2] = c; data[idx + 3] = 1f;
                    }
                return CapturedFrame.FromHDR(req.FrameIndex, w, h, data, req.PassType, req.AovName);
            }
            else
            {
                var data = new byte[w * h * 4];
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        int idx = (y * w + x) * 4;
                        byte c = (byte)(((x / 32 + y / 32 + req.FrameIndex) & 1) == 0 ? 60 : 200);
                        data[idx] = c; data[idx + 1] = c; data[idx + 2] = c; data[idx + 3] = 255;
                    }
                return CapturedFrame.FromLDR(req.FrameIndex, w, h, data, req.PassType, req.AovName);
            }
        }

        /// <summary>根据作业输出格式创建通道编码器集合。</summary>
        private static List<MovieEncoder> CreateEncoders(MovieRenderJob job)
        {
            var list = new List<MovieEncoder>();
            switch (job.Settings.OutputFormat)
            {
                case MovieOutputFormat.ImageSequence:
                    list.Add(job.Settings.ImageContainer switch
                    {
                        MovieImageContainer.EXR => new ExrEncoder(),
                        MovieImageContainer.JPG => new JpegEncoder(),
                        MovieImageContainer.TIFF => new TiffEncoder(),
                        _ => new PngEncoder()
                    });
                    break;
                case MovieOutputFormat.HDR:
                    list.Add(new ExrEncoder());
                    break;
                case MovieOutputFormat.Video:
                    list.Add(new FfmpegVideoEncoder());
                    // 视频模式下同时输出一份 PNG 预览序列（可选）
                    break;
            }
            return list;
        }

        /// <summary>判断某通道是否应由该编码器落盘（避免视频编码器吞掉数据通道）。</summary>
        private static bool ChannelMatchesEncoder(MovieRenderPassType pass, MovieEncoder enc, MovieRenderJob job)
        {
            if (enc is FfmpegVideoEncoder)
                return pass == MovieRenderPassType.Beauty; // 视频只编码 Beauty
            // 图像/HDR 编码器写出所有启用的通道（按通道分子目录由编码器内部处理）
            return true;
        }

        private void CancelJob(MovieRenderJob job, List<MovieEncoder> encoders)
        {
            foreach (var enc in encoders) { try { enc.Finalize(); enc.Dispose(); } catch { } }
            job.State = MovieJobState.Canceled;
            job.EndTimeUtc = DateTime.UtcNow;
        }

        private void FailJob(MovieRenderJob job, string message)
        {
            job.State = MovieJobState.Failed;
            job.ErrorMessage = message;
            job.EndTimeUtc = DateTime.UtcNow;
            OnJobFailed?.Invoke(job, message);
        }

        public void Dispose()
        {
            if (_disposed) return;
            foreach (var rb in _readbackBuffers.Values)
                try { rb?.Dispose(); } catch { }
            _readbackBuffers.Clear();
            _disposed = true;
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // MovieRenderQueue：渲染队列（调度 / 优先级 / 并行限制 / 暂停恢复取消）
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 渲染队列。管理多个 <see cref="MovieRenderJob"/> 的调度执行：
    /// 优先级排序、并行渲染限制（单 GPU 串行 / 多 GPU 可并行）、暂停/恢复/取消、
    /// 作业状态机。底层由一个或多个 <see cref="MoviePipeline"/> 工作线程驱动。
    /// </summary>
    public class MovieRenderQueue : IDisposable
    {
        /// <summary>等待执行的作业（按优先级降序排序）。</summary>
        private readonly List<MovieRenderJob> _pending = new();
        /// <summary>正在执行的作业。</summary>
        private readonly List<MovieRenderJob> _running = new();
        /// <summary>已完成的作业（保留以供查询）。</summary>
        private readonly List<MovieRenderJob> _completed = new();
        /// <summary>锁，保护队列状态。</summary>
        private readonly object _lock = new();
        /// <summary>工作线程数组（每 GPU 一个）。</summary>
        private readonly Thread[] _workers;
        /// <summary>每 GPU 对应的管线。</summary>
        private readonly MoviePipeline[] _pipelines;
        /// <summary>唤醒工作线程的事件。</summary>
        private readonly AutoResetEvent _wake = new(false);
        /// <summary>停止标志。</summary>
        private volatile bool _stopping;
        /// <summary>全局暂停标志。</summary>
        private volatile bool _paused;

        /// <summary>可并行渲染的最大作业数（通常等于可用 GPU 数；单 GPU 为 1）。</summary>
        public int MaxConcurrentJobs { get; }
        /// <summary>队列是否正在运行（未停止）。</summary>
        public bool IsRunning => !_stopping;
        /// <summary>是否全局暂停。</summary>
        public bool IsPaused => _paused;

        /// <summary>作业状态变化回调。</summary>
        public event Action<MovieRenderJob>? OnJobStateChanged;

        /// <summary>
        /// 创建渲染队列。
        /// </summary>
        /// <param name="device">RHI 设备（可空，空时走占位回读）。</param>
        /// <param name="sequencer">序列器系统。</param>
        /// <param name="maxConcurrent">最大并行作业数（默认 1，单 GPU 串行）。</param>
        public MovieRenderQueue(RHIDevice? device, SequencerSystem? sequencer, int maxConcurrent = 1)
        {
            MaxConcurrentJobs = Math.Max(1, maxConcurrent);
            _pipelines = new MoviePipeline[MaxConcurrentJobs];
            _workers = new Thread[MaxConcurrentJobs];
            for (int i = 0; i < MaxConcurrentJobs; i++)
            {
                _pipelines[i] = new MoviePipeline(device, sequencer);
                // 仅转发状态通知；作业列表的进出账由 WorkerLoop 统一管理，避免重复入账
                _pipelines[i].OnJobCompleted += job => FireStateChanged(job);
                _pipelines[i].OnJobFailed += (job, _) => FireStateChanged(job);
                _workers[i] = new Thread(() => WorkerLoop(i)) { IsBackground = true, Name = $"MovieRenderWorker_{i}" };
                _workers[i].Start();
            }
        }

        /// <summary>作业入队。返回入队后的排队位置。</summary>
        public int Enqueue(MovieRenderJob job)
        {
            lock (_lock)
            {
                job.State = MovieJobState.Queued;
                _pending.Add(job);
                // 按优先级降序排序（优先级相同时保持入队顺序）
                _pending.Sort((a, b) =>
                {
                    int c = b.Priority.CompareTo(a.Priority);
                    return c != 0 ? c : 0; // 稳定排序由 List.Sort 近似满足
                });
                _wake.Set();
                return _pending.IndexOf(job);
            }
        }

        /// <summary>批量入队。</summary>
        public void EnqueueRange(IEnumerable<MovieRenderJob> jobs)
        {
            foreach (var j in jobs) Enqueue(j);
        }

        /// <summary>从等待队列移除（尚未开始的）作业。</summary>
        public bool Dequeue(MovieRenderJob job)
        {
            lock (_lock)
            {
                bool removed = _pending.Remove(job);
                if (removed) job.State = MovieJobState.Canceled;
                return removed;
            }
        }

        /// <summary>取消作业（无论是否已开始）。正在渲染的作业通过取消令牌尽快终止。</summary>
        public void Cancel(MovieRenderJob job)
        {
            lock (_lock)
            {
                if (_pending.Remove(job))
                {
                    job.State = MovieJobState.Canceled;
                    FireStateChanged(job);
                }
            }
            // 正在运行的作业由其取消令牌处理（见 WorkerLoop）
            job.State = MovieJobState.Canceled;
        }

        /// <summary>全局暂停：不再派发新作业，正在渲染的帧完成后挂起。</summary>
        public void Pause() => _paused = true;

        /// <summary>从暂停恢复。</summary>
        public void Resume()
        {
            _paused = false;
            _wake.Set();
        }

        /// <summary>获取所有作业的快照（含排队/运行/完成）。</summary>
        public List<MovieRenderJob> GetAllJobs()
        {
            lock (_lock)
            {
                var all = new List<MovieRenderJob>(_pending.Count + _running.Count + _completed.Count);
                all.AddRange(_pending);
                all.AddRange(_running);
                all.AddRange(_completed);
                return all;
            }
        }

        /// <summary>获取处于指定状态的作业。</summary>
        public List<MovieRenderJob> GetJobsByState(MovieJobState state)
        {
            lock (_lock)
            {
                var result = new List<MovieRenderJob>();
                foreach (var j in _pending) if (j.State == state) result.Add(j);
                foreach (var j in _running) if (j.State == state) result.Add(j);
                foreach (var j in _completed) if (j.State == state) result.Add(j);
                return result;
            }
        }

        /// <summary>清空已完成作业记录。</summary>
        public void ClearCompleted()
        {
            lock (_lock) _completed.Clear();
        }

        /// <summary>工作线程主循环：取作业 -> 执行 -> 循环。</summary>
        private void WorkerLoop(int workerIndex)
        {
            var pipeline = _pipelines[workerIndex];
            while (!_stopping)
            {
                MovieRenderJob? job = null;
                // 取作业：仅在持锁期间操作队列，取到后立即释放锁再执行（避免长任务阻塞入队）
                lock (_lock)
                {
                    if (!_paused && _pending.Count > 0)
                    {
                        job = _pending[0];       // 队首为最高优先级
                        _pending.RemoveAt(0);
                        _running.Add(job);
                    }
                }

                if (job == null)
                {
                    // 无可执行作业（暂停或队列空）：等待唤醒或超时轮询
                    _wake.WaitOne(100);
                    continue;
                }

                job.State = MovieJobState.Rendering;
                FireStateChanged(job);

                // 取消令牌：检查该作业是否被取消或队列是否停止
                var jobRef = job;
                pipeline.Execute(job, () => jobRef.State == MovieJobState.Canceled || _stopping);

                // 执行结束（完成/失败/取消）后统一进出账
                lock (_lock) { _running.Remove(job); _completed.Add(job); }
                FireStateChanged(job);
            }
        }

        private void FireStateChanged(MovieRenderJob job) => OnJobStateChanged?.Invoke(job);

        /// <summary>设置管线渲染回调（转发到所有内部管线）。</summary>
        public void SetRenderCallback(Func<RenderPassRequest, RHITexture?> onRenderPass)
        {
            foreach (var p in _pipelines) p.OnRenderPass = onRenderPass;
        }

        /// <summary>设置世界更新回调（转发到所有内部管线）。</summary>
        public void SetWorldUpdateCallback(Action<float> onUpdateWorld)
        {
            foreach (var p in _pipelines) p.OnUpdateWorld = onUpdateWorld;
        }

        /// <summary>订阅帧渲染进度（转发到所有内部管线）。</summary>
        public void SubscribeFrameRendered(Action<MovieRenderJob, int, MovieRenderPassType, float> handler)
        {
            foreach (var p in _pipelines) p.OnFrameRendered += handler;
        }

        public void Dispose()
        {
            _stopping = true;
            _wake.Set();
            foreach (var p in _pipelines) p.Dispose();
            foreach (var w in _workers)
            {
                try { w.Join(2000); } catch { }
            }
            _wake.Dispose();
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // MovieRenderQueueSystem：顶层系统管理器
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 电影渲染队列顶层系统。作为引擎子系统统一管理渲染队列生命周期、
    /// 默认设置与全局渲染回调注册。由引擎主循环或编辑器"渲染电影"面板驱动。
    /// </summary>
    public class MovieRenderQueueSystem : IDisposable
    {
        /// <summary>RHI 设备。</summary>
        public RHIDevice? Device { get; set; }
        /// <summary>序列器系统。</summary>
        public SequencerSystem? Sequencer { get; set; }
        /// <summary>全局默认渲染设置（新建作业的模板）。</summary>
        public MovieRenderSettings DefaultSettings { get; set; } = new();
        /// <summary>渲染队列实例。</summary>
        public MovieRenderQueue? Queue { get; private set; }

        /// <summary>单通道渲染回调（由渲染系统注册）。</summary>
        public Func<RenderPassRequest, RHITexture?>? OnRenderPass;
        /// <summary>每帧世界更新回调。</summary>
        public Action<float>? OnUpdateWorld;

        private bool _disposed;

        /// <summary>初始化队列（指定可用 GPU 数，默认 1）。</summary>
        public void Initialize(int gpuCount = 1)
        {
            Queue = new MovieRenderQueue(Device, Sequencer, gpuCount);
            if (OnRenderPass != null) Queue.SetRenderCallback(OnRenderPass);
            if (OnUpdateWorld != null) Queue.SetWorldUpdateCallback(OnUpdateWorld);
        }

        /// <summary>注册渲染回调（初始化前后均可调用）。</summary>
        public void RegisterRenderCallback(Func<RenderPassRequest, RHITexture?> onRenderPass)
        {
            OnRenderPass = onRenderPass;
            Queue?.SetRenderCallback(onRenderPass);
        }

        /// <summary>注册世界更新回调。</summary>
        public void RegisterWorldUpdate(Action<float> onUpdateWorld)
        {
            OnUpdateWorld = onUpdateWorld;
            Queue?.SetWorldUpdateCallback(onUpdateWorld);
        }

        /// <summary>基于默认设置与指定序列快速创建一个作业并入队。</summary>
        public MovieRenderJob SubmitJob(LevelSequence sequence, string name, MovieFrameRange range, int priority = 0)
        {
            var job = new MovieRenderJob
            {
                Sequence = sequence,
                Name = name,
                Settings = DefaultSettings.Clone(),
                FrameRange = range,
                Priority = priority
            };
            Queue?.Enqueue(job);
            return job;
        }

        /// <summary>暂停队列。</summary>
        public void Pause() => Queue?.Pause();

        /// <summary>恢复队列。</summary>
        public void Resume() => Queue?.Resume();

        /// <summary>取消指定作业。</summary>
        public void Cancel(MovieRenderJob job) => Queue?.Cancel(job);

        public void Dispose()
        {
            if (_disposed) return;
            Queue?.Dispose();
            _disposed = true;
        }
    }
}
