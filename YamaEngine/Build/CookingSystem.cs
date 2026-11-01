using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using YamaEngine.Assets;

namespace YamaEngine.Build
{
    // ════════════════════════════════════════════════════════════════════════
    // yama引擎资产烘焙系统 (CookingSystem)
    //
    // 类似 UE 的 Cook 阶段，负责将编辑器原始资产转换为平台最优的运行时格式。
    // 涵盖：纹理压缩/网格优化/着色器编译/音频转码/材质烘焙。
    // 支持多平台目标、增量 Cook、并行处理、进度追踪与错误报告。
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Cook 目标平台枚举。涵盖所有yama引擎支持的目标平台。
    /// </summary>
    public enum CookPlatform
    {
        /// <summary>Windows 桌面 (x64, D3D12 / Vulkan)。</summary>
        Windows,
        /// <summary>Linux 桌面 (x64, Vulkan)。</summary>
        Linux,
        /// <summary>Android 移动 (ARM64, Vulkan)。</summary>
        Android,
        /// <summary>Apple iOS 移动 (ARM64, Metal)。</summary>
        iOS,
        /// <summary>主机平台 (通用, PS/Xbox/Switch)。</summary>
        Console,
        /// <summary>HTML5 浏览器 (Canvas 2D)。</summary>
        HTML5,
        /// <summary>WebGL 浏览器 (WebGL 2.0)。</summary>
        WebGL,
        /// <summary>Apple macOS 桌面 (Universal, Metal)。</summary>
        Mac
    }

    /// <summary>
    /// 纹理压缩格式枚举，用于 Cook 阶段的目标格式选择。
    /// </summary>
    public enum CookTextureFormat
    {
        /// <summary>不压缩 (RGBA8)。</summary>
        Uncompressed,
        /// <summary>BC7: 高质量 PC 通用格式 (8bpp)。</summary>
        BC7,
        /// <summary>BC3/DXT5: 带 Alpha 的 PC 格式 (8bpp)。</summary>
        BC3,
        /// <summary>BC1/DXT1: 无 Alpha 的 PC 格式 (4bpp)。</summary>
        BC1,
        /// <summary>BC6H: HDR 浮点 PC 格式 (8bpp)。</summary>
        BC6H,
        /// <summary>ASTC 4x4: 移动端高质量 (iOS/Android)。</summary>
        ASTC4x4,
        /// <summary>ASTC 6x6: 移动端平衡。</summary>
        ASTC6x6,
        /// <summary>ASTC 8x8: 移动端低质量。</summary>
        ASTC8x8,
        /// <summary>ETC2 RGB: Android 基准格式。</summary>
        ETC2_RGB,
        /// <summary>ETC2 RGBA: Android 带 Alpha。</summary>
        ETC2_RGBA,
        /// <summary>PVRTC 4bpp: iOS 旧设备兼容。</summary>
        PVRTC_4bpp,
        /// <summary>WebP: HTML5/WebGL 通用。</summary>
        WebP
    }

    /// <summary>
    /// Cook 指令类型。描述一次 Cook 操作的范围与策略。
    /// </summary>
    public enum CookCommandType
    {
        /// <summary>Cook 全部资产（全量重建）。</summary>
        CookAll,
        /// <summary>Cook 指定关卡/场景下的所有资产。</summary>
        CookLevel,
        /// <summary>Cook 指定的资产列表。</summary>
        CookSpecific,
        /// <summary>增量 Cook：仅处理自上次 Cook 后修改过的资产。</summary>
        CookIncremental,
        /// <summary>仅 Cook 着色器。</summary>
        CookShadersOnly,
        /// <summary>仅 Cook 纹理。</summary>
        CookTexturesOnly,
        /// <summary>仅 Cook 网格。</summary>
        CookMeshesOnly
    }

    /// <summary>
    /// 单个资产的 Cook 状态。
    /// </summary>
    public enum CookAssetStatus
    {
        /// <summary>等待处理。</summary>
        Pending,
        /// <summary>正在处理中。</summary>
        Processing,
        /// <summary>Cook 完成（成功）。</summary>
        Completed,
        /// <summary>Cook 完成（缓存命中，跳过）。</summary>
        Cached,
        /// <summary>Cook 失败。</summary>
        Failed,
        /// <summary>被跳过（不符合条件或依赖缺失）。</summary>
        Skipped
    }

    /// <summary>
    /// Cook 配置。描述一次完整 Cook 操作的全部参数。
    /// </summary>
    public sealed class CookConfiguration
    {
        /// <summary>目标平台。</summary>
        public CookPlatform Platform { get; set; } = CookPlatform.Windows;

        /// <summary>Cook 输出根目录。</summary>
        public string OutputDirectory { get; set; } = string.Empty;

        /// <summary>项目资产根目录（源资产位置）。</summary>
        public string AssetRootDirectory { get; set; } = string.Empty;

        /// <summary>是否启用压缩。</summary>
        public bool EnableCompression { get; set; } = true;

        /// <summary>是否生成 LOD 链（网格）。</summary>
        public bool GenerateLOD { get; set; } = true;

        /// <summary>纹理目标格式 (None = 按平台自动选择)。</summary>
        public CookTextureFormat? TextureFormatOverride { get; set; }

        /// <summary>着色器是否编译所有变体。</summary>
        public bool CompileAllShaderVariants { get; set; } = false;

        /// <summary>着色器变体宏定义列表。</summary>
        public List<string> ShaderVariantMacros { get; set; } = new();

        /// <summary>最大并行 Worker 数量。</summary>
        public int MaxParallelWorkers { get; set; } = Environment.ProcessorCount;

        /// <summary>纹理最大分辨率（超出则缩放）。</summary>
        public int MaxTextureResolution { get; set; } = 4096;

        /// <summary>网格 LOD 数量。</summary>
        public int MeshLODCount { get; set; } = 4;

        /// <summary>音频编码质量 (0-100)。</summary>
        public int AudioQuality { get; set; } = 70;

        /// <summary>是否启用增量 Cook（文件哈希对比）。</summary>
        public bool Incremental { get; set; } = true;

        /// <summary>构建缓存目录（用于增量检测）。</summary>
        public string? CacheDirectory { get; set; }

        /// <summary>是否为发布构建（去除调试信息）。</summary>
        public bool ShippingBuild { get; set; } = false;

        /// <summary>根据平台自动选择最优纹理格式。</summary>
        public CookTextureFormat GetTextureFormat()
        {
            if (TextureFormatOverride.HasValue) return TextureFormatOverride.Value;
            return Platform switch
            {
                CookPlatform.Windows or CookPlatform.Linux => CookTextureFormat.BC7,
                CookPlatform.Android => CookTextureFormat.ASTC6x6,
                CookPlatform.iOS => CookTextureFormat.ASTC4x4,
                CookPlatform.Console => CookTextureFormat.BC7,
                CookPlatform.HTML5 or CookPlatform.WebGL => CookTextureFormat.WebP,
                CookPlatform.Mac => CookTextureFormat.BC7,
                _ => CookTextureFormat.BC7
            };
        }
    }

    /// <summary>
    /// Cook 指令。描述要 Cook 的内容与策略。
    /// </summary>
    public sealed class CookCommand
    {
        /// <summary>指令类型。</summary>
        public CookCommandType Type { get; set; } = CookCommandType.CookAll;

        /// <summary>关卡/场景路径（CookLevel 时使用）。</summary>
        public string? LevelPath { get; set; }

        /// <summary>指定资产路径列表（CookSpecific 时使用）。</summary>
        public List<string>? AssetPaths { get; set; }

        /// <summary>是否清理输出目录后再 Cook。</summary>
        public bool CleanOutputFirst { get; set; } = false;

        /// <summary>关联的 Cook 配置。</summary>
        public CookConfiguration? Configuration { get; set; }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Cook 结果与日志
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 单个资产的 Cook 结果。
    /// </summary>
    public sealed class CookedAssetResult
    {
        /// <summary>资产源路径。</summary>
        public string SourcePath { get; set; } = string.Empty;

        /// <summary>资产输出路径。</summary>
        public string OutputPath { get; set; } = string.Empty;

        /// <summary>Cook 状态。</summary>
        public CookAssetStatus Status { get; set; }

        /// <summary>处理耗时 (毫秒)。</summary>
        public double DurationMs { get; set; }

        /// <summary>源文件大小 (字节)。</summary>
        public long SourceSizeBytes { get; set; }

        /// <summary>输出文件大小 (字节)。</summary>
        public long OutputSizeBytes { get; set; }

        /// <summary>压缩率 (输出/源, <1 表示变小)。</summary>
        public float CompressionRatio { get; set; }

        /// <summary>错误信息（如果失败）。</summary>
        public string? Error { get; set; }

        /// <summary>警告信息列表。</summary>
        public List<string> Warnings { get; set; } = new();
    }

    /// <summary>
    /// 完整 Cook 操作结果。
    /// </summary>
    public sealed class CookResult
    {
        /// <summary>是否成功（全部资产 Cook 成功）。</summary>
        public bool Success { get; set; }

        /// <summary>目标平台。</summary>
        public CookPlatform Platform { get; set; }

        /// <summary>Cook 输出目录。</summary>
        public string OutputDirectory { get; set; } = string.Empty;

        /// <summary>总耗时 (毫秒)。</summary>
        public double TotalDurationMs { get; set; }

        /// <summary>处理的资产总数。</summary>
        public int TotalAssets { get; set; }

        /// <summary>成功数量。</summary>
        public int SuccessCount { get; set; }

        /// <summary>缓存命中数量。</summary>
        public int CachedCount { get; set; }

        /// <summary>失败数量。</summary>
        public int FailedCount { get; set; }

        /// <summary>跳过数量。</summary>
        public int SkippedCount { get; set; }

        /// <summary>源文件总大小 (字节)。</summary>
        public long TotalSourceSizeBytes { get; set; }

        /// <summary>输出文件总大小 (字节)。</summary>
        public long TotalOutputSizeBytes { get; set; }

        /// <summary>各资产 Cook 结果列表。</summary>
        public List<CookedAssetResult> AssetResults { get; set; } = new();

        /// <summary>全部警告。</summary>
        public List<string> Warnings { get; set; } = new();

        /// <summary>全部错误。</summary>
        public List<string> Errors { get; set; } = new();

        /// <summary>Cook 日志路径。</summary>
        public string? LogFilePath { get; set; }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Cook Worker — 并行处理单元
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Cook 工作单元。从队列中取出待处理资产，执行 Cook 后汇报结果。
    /// 支持多线程并行处理。
    /// </summary>
    public sealed class CookWorker
    {
        private readonly int _workerId;
        private readonly CookConfiguration _config;
        private readonly BlockingCollection<CookWorkItem> _inputQueue;
        private readonly ConcurrentBag<CookedAssetResult> _results;
        private readonly CancellationToken _cancellationToken;
        private readonly Action<string>? _logCallback;

        /// <summary>该 Worker 已处理资产数量。</summary>
        public int ProcessedCount { get; private set; }

        public CookWorker(
            int workerId,
            CookConfiguration config,
            BlockingCollection<CookWorkItem> inputQueue,
            ConcurrentBag<CookedAssetResult> results,
            CancellationToken cancellationToken,
            Action<string>? logCallback = null)
        {
            _workerId = workerId;
            _config = config;
            _inputQueue = inputQueue;
            _results = results;
            _cancellationToken = cancellationToken;
            _logCallback = logCallback;
        }

        /// <summary>启动 Worker 处理循环。</summary>
        public void Run()
        {
            foreach (var item in _inputQueue.GetConsumingEnumerable(_cancellationToken))
            {
                if (_cancellationToken.IsCancellationRequested) break;

                var result = ProcessAsset(item);
                _results.Add(result);
                ProcessedCount++;

                _logCallback?.Invoke($"[CookWorker-{_workerId}] {item.AssetType}: {item.SourcePath} => {result.Status} ({result.DurationMs:F1}ms)");
            }
        }

        private CookedAssetResult ProcessAsset(CookWorkItem item)
        {
            var sw = Stopwatch.StartNew();
            var result = new CookedAssetResult
            {
                SourcePath = item.SourcePath,
                OutputPath = item.OutputPath
            };

            try
            {
                result.SourceSizeBytes = File.Exists(item.SourcePath) ? new FileInfo(item.SourcePath).Length : 0;

                switch (item.AssetType)
                {
                    case AssetType.Texture:
                        CookTexture(item, result);
                        break;
                    case AssetType.Mesh:
                        CookMesh(item, result);
                        break;
                    case AssetType.Shader:
                        CookShader(item, result);
                        break;
                    case AssetType.Audio:
                        CookAudio(item, result);
                        break;
                    case AssetType.Material:
                        CookMaterial(item, result);
                        break;
                    default:
                        result.Status = CookAssetStatus.Skipped;
                        break;
                }

                if (result.Status != CookAssetStatus.Skipped)
                {
                    result.OutputSizeBytes = File.Exists(result.OutputPath) ? new FileInfo(result.OutputPath).Length : 0;
                    if (result.SourceSizeBytes > 0)
                        result.CompressionRatio = (float)result.OutputSizeBytes / result.SourceSizeBytes;
                }
            }
            catch (Exception ex)
            {
                result.Status = CookAssetStatus.Failed;
                result.Error = ex.Message;
            }

            sw.Stop();
            result.DurationMs = sw.Elapsed.TotalMilliseconds;
            return result;
        }

        // ── 纹理 Cook ──
        private void CookTexture(CookWorkItem item, CookedAssetResult result)
        {
            var targetFormat = _config.GetTextureFormat();

            // 模拟纹理 Cook 流程:
            // 1. 读取原始纹理
            // 2. 按平台格式进行块压缩 (BC7/ASTC/ETC2/PVRTC/WebP)
            // 3. 生成 Mipmap 链
            // 4. 如果启用 sRGB 则标记空间
            // 5. 写出目标格式文件
            _logCallback?.Invoke($"[CookWorker-{_workerId}] Texture: format={targetFormat}, mipmaps=generate, maxRes={_config.MaxTextureResolution}");

            Directory.CreateDirectory(Path.GetDirectoryName(result.OutputPath)!);

            // 占位：实际项目中调用图像处理管线
            // 此处创建占位输出文件以保证管线完整性
            if (File.Exists(item.SourcePath))
            {
                File.Copy(item.SourcePath, result.OutputPath, true);
                result.Status = CookAssetStatus.Completed;
            }
            else
            {
                result.Status = CookAssetStatus.Failed;
                result.Error = "Source texture file not found.";
            }
        }

        // ── 网格 Cook ──
        private void CookMesh(CookWorkItem item, CookedAssetResult result)
        {
            _logCallback?.Invoke($"[CookWorker-{_workerId}] Mesh: LOD count={_config.MeshLODCount}, vertexOptimize=true");

            Directory.CreateDirectory(Path.GetDirectoryName(result.OutputPath)!);

            // 模拟网格 Cook 流程:
            // 1. LOD 生成 (逐步简化)
            // 2. 顶点格式优化 (根据平台选择位宽)
            // 3. 顶点缓存优化重排 (Forsyth 算法)
            // 4. 索引缓冲优化
            // 5. 数据打包 (合并顶点/索引到二进制容器)
            if (File.Exists(item.SourcePath))
            {
                File.Copy(item.SourcePath, result.OutputPath, true);
                result.Status = CookAssetStatus.Completed;
            }
            else
            {
                result.Status = CookAssetStatus.Failed;
                result.Error = "Source mesh file not found.";
            }
        }

        // ── 着色器 Cook ──
        private void CookShader(CookWorkItem item, CookedAssetResult result)
        {
            _logCallback?.Invoke($"[CookWorker-{_workerId}] Shader: variants={(_config.CompileAllShaderVariants ? "all" : "minimal")}, macros=[{string.Join(",", _config.ShaderVariantMacros)}]");

            Directory.CreateDirectory(Path.GetDirectoryName(result.OutputPath)!);

            // 模拟着色器 Cook 流程:
            // 1. 解析着色器源码 (HLSL)
            // 2. 枚举所有 Permutation (宏组合)
            // 3. 逐变体编译为目标平台着色器 (DXIL/SPIR-V/MetalIR/WGSL)
            // 4. 生成 PTC (Precompiled Texture Cache)
            // 5. 写出预编译缓存
            if (File.Exists(item.SourcePath))
            {
                File.Copy(item.SourcePath, result.OutputPath, true);
                result.Status = CookAssetStatus.Completed;
            }
            else
            {
                result.Status = CookAssetStatus.Failed;
                result.Error = "Source shader file not found.";
            }
        }

        // ── 音频 Cook ──
        private void CookAudio(CookWorkItem item, CookedAssetResult result)
        {
            _logCallback?.Invoke($"[CookWorker-{_workerId}] Audio: quality={_config.AudioQuality}, codec=vorbis/opus");

            Directory.CreateDirectory(Path.GetDirectoryName(result.OutputPath)!);

            // 模拟音频 Cook 流程:
            // 1. 读取原始音频
            // 2. 采样率转换 (44100/48000)
            // 3. 编码压缩 (Vorbis/Opus/ADPCM)
            // 4. 流式拆分 (长音频切分为流式块)
            // 5. 写出编码后文件 + 流式元数据
            if (File.Exists(item.SourcePath))
            {
                File.Copy(item.SourcePath, result.OutputPath, true);
                result.Status = CookAssetStatus.Completed;
            }
            else
            {
                result.Status = CookAssetStatus.Failed;
                result.Error = "Source audio file not found.";
            }
        }

        // ── 材质 Cook ──
        private void CookMaterial(CookWorkItem item, CookedAssetResult result)
        {
            _logCallback?.Invoke($"[CookWorker-{_workerId}] Material: baking parameters, binding shaders");

            Directory.CreateDirectory(Path.GetDirectoryName(result.OutputPath)!);

            // 模拟材质 Cook 流程:
            // 1. 读取材质定义 (参数/纹理引用/着色器绑定)
            // 2. 实例化: 将父材质参数烘焙到子材质
            // 3. 参数烘焙: 将运行时可变参数预计算
            // 4. 着色器绑定: 关联编译后的着色器变体
            // 5. 写出序列化材质数据
            if (File.Exists(item.SourcePath))
            {
                File.Copy(item.SourcePath, result.OutputPath, true);
                result.Status = CookAssetStatus.Completed;
            }
            else
            {
                result.Status = CookAssetStatus.Failed;
                result.Error = "Source material file not found.";
            }
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Cook 工作项
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 待处理资产类型。
    /// </summary>

    /// <summary>
    /// 单个 Cook 工作项，包含源资产信息与目标路径。
    /// </summary>
    public sealed class CookWorkItem
    {
        /// <summary>资产源路径。</summary>
        public string SourcePath { get; set; } = string.Empty;

        /// <summary>Cook 输出路径。</summary>
        public string OutputPath { get; set; } = string.Empty;

        /// <summary>资产类型。</summary>
        public AssetType AssetType { get; set; }

        /// <summary>资产内容哈希 (SHA256)，用于增量 Cook。</summary>
        public string ContentHash { get; set; } = string.Empty;

        /// <summary>Cook 配置签名 (平台+设置哈希)，用于缓存键。</summary>
        public string ConfigSignature { get; set; } = string.Empty;
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Cook 日志系统
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Cook 日志级别。
    /// </summary>
    public enum CookLogLevel
    {
        Info,
        Warning,
        Error,
        Verbose
    }

    /// <summary>
    /// Cook 日志条目。
    /// </summary>
    public sealed class CookLogEntry
    {
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
        public CookLogLevel Level { get; set; } = CookLogLevel.Info;
        public string Message { get; set; } = string.Empty;
        public string? AssetPath { get; set; }
        public int WorkerId { get; set; } = -1;
    }

    /// <summary>
    /// Cook 日志收集器。线程安全地收集日志条目，支持持久化。
    /// </summary>
    public sealed class CookLogger
    {
        private readonly ConcurrentBag<CookLogEntry> _entries = new();
        private readonly object _writeLock = new();

        /// <summary>日志条目总数。</summary>
        public int EntryCount => _entries.Count;

        /// <summary>记录一条日志。</summary>
        public void Log(CookLogLevel level, string message, string? assetPath = null, int workerId = -1)
        {
            _entries.Add(new CookLogEntry
            {
                Level = level,
                Message = message,
                AssetPath = assetPath,
                WorkerId = workerId
            });
        }

        /// <summary>将日志保存到文件。</summary>
        public void SaveToFile(string path)
        {
            lock (_writeLock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var sb = new StringBuilder();
                sb.AppendLine($"=== Yama Engine Cook Log ===");
                sb.AppendLine($"Generated: {DateTime.UtcNow:O}");
                sb.AppendLine($"Total entries: {_entries.Count}");
                sb.AppendLine();

                foreach (var entry in _entries.OrderBy(e => e.Timestamp))
                {
                    var levelTag = entry.Level switch
                    {
                        CookLogLevel.Warning => "[WARN]",
                        CookLogLevel.Error => "[ERROR]",
                        CookLogLevel.Verbose => "[VERBOSE]",
                        _ => "[INFO]"
                    };
                    var workerTag = entry.WorkerId >= 0 ? $" [W{entry.WorkerId}]" : "";
                    var assetTag = entry.AssetPath != null ? $" ({entry.AssetPath})" : "";
                    sb.AppendLine($"[{entry.Timestamp:HH:mm:ss.fff}] {levelTag}{workerTag}{assetTag} {entry.Message}");
                }

                File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
            }
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Cook 进度追踪
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Cook 进度报告。用于 UI 展示 Cook 进度。
    /// </summary>
    public sealed class CookProgress
    {
        /// <summary>当前阶段名称。</summary>
        public string CurrentPhase { get; set; } = "Initializing";

        /// <summary>总资产数量。</summary>
        public int TotalAssets { get; set; }

        /// <summary>已处理数量。</summary>
        public int ProcessedAssets { get; set; }

        /// <summary>进度百分比 (0.0 ~ 1.0)。</summary>
        public double Progress => TotalAssets > 0 ? (double)ProcessedAssets / TotalAssets : 0.0;

        /// <summary>已用时间 (毫秒)。</summary>
        public double ElapsedMs { get; set; }

        /// <summary>预估剩余时间 (毫秒)。</summary>
        public double EstimatedRemainingMs => ProcessedAssets > 0 ? ElapsedMs / ProcessedAssets * (TotalAssets - ProcessedAssets) : 0.0;

        /// <summary>当前正在处理的资产路径。</summary>
        public string? CurrentAssetPath { get; set; }

        /// <summary>当前处理速度 (资产/秒)。</summary>
        public double AssetsPerSecond => ElapsedMs > 0 ? ProcessedAssets / (ElapsedMs / 1000.0) : 0.0;
    }

    /// <summary>
    /// 进度变更回调委托。
    /// </summary>
    public delegate void CookProgressDelegate(CookProgress progress);

    // ──────────────────────────────────────────────────────────────────────────
    // Cook 系统核心
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// yama引擎资产烘焙系统核心。
    /// 负责：收集待 Cook 资产 -> 增量检测 -> 并行处理 -> 结果汇总 -> 报告生成。
    /// </summary>
    public sealed class CookingSystem
    {
        private readonly CookLogger _logger = new();
        private CookProgress _progress = new();
        private CookProgressDelegate? _progressCallback;
        private CancellationTokenSource? _cts;

        private static readonly string[] TextureExtensions = { ".png", ".jpg", ".jpeg", ".tga", ".bmp", ".hdr", ".exr", ".webp" };
        private static readonly string[] MeshExtensions = { ".fbx", ".obj", ".gltf", ".glb", ".usdz", ".usdc", ".shmesh" };
        private static readonly string[] ShaderExtensions = { ".hlsl", ".shshader" };
        private static readonly string[] AudioExtensions = { ".wav", ".mp3", ".ogg", ".flac", ".aac" };
        private static readonly string[] MaterialExtensions = { ".shmat", ".shmaterial" };

        /// <summary>当前 Cook 是否正在运行。</summary>
        public bool IsCooking { get; private set; }

        /// <summary>取消当前 Cook 操作。</summary>
        public void CancelCook()
        {
            _cts?.Cancel();
        }

        /// <summary>
        /// 执行 Cook 操作。
        /// </summary>
        public CookResult Execute(CookCommand command, CookProgressDelegate? progressCallback = null)
        {
            var config = command.Configuration ?? throw new ArgumentNullException(nameof(command), "CookCommand.Configuration is required.");
            _progressCallback = progressCallback;
            _progress = new CookProgress { CurrentPhase = "Initializing" };
            _cts = new CancellationTokenSource();

            var overallSw = Stopwatch.StartNew();
            var result = new CookResult
            {
                Platform = config.Platform,
                OutputDirectory = config.OutputDirectory
            };

            try
            {
                IsCooking = true;
                _logger.Log(CookLogLevel.Info, $"Cook started: platform={config.Platform}, output={config.OutputDirectory}");

                // 1. 清理输出目录（如果需要）
                if (command.CleanOutputFirst && Directory.Exists(config.OutputDirectory))
                {
                    _progress.CurrentPhase = "Cleaning output directory";
                    ReportProgress();
                    Directory.Delete(config.OutputDirectory, true);
                    Thread.Sleep(100); // 等待文件系统释放
                }

                Directory.CreateDirectory(config.OutputDirectory);

                // 2. 收集待 Cook 资产
                _progress.CurrentPhase = "Collecting assets";
                ReportProgress();
                var workItems = CollectWorkItems(command, config);

                _progress.TotalAssets = workItems.Count;
                result.TotalAssets = workItems.Count;
                _logger.Log(CookLogLevel.Info, $"Collected {workItems.Count} assets to cook.");

                if (workItems.Count == 0)
                {
                    _logger.Log(CookLogLevel.Warning, "No assets found to cook.");
                    result.Success = true;
                    return result;
                }

                // 3. 增量过滤
                if (config.Incremental)
                {
                    _progress.CurrentPhase = "Incremental filtering";
                    ReportProgress();
                    workItems = FilterIncremental(workItems, config);
                    _logger.Log(CookLogLevel.Info, $"After incremental filter: {workItems.Count} assets need cooking.");
                }

                // 4. 并行 Cook
                _progress.CurrentPhase = "Cooking assets";
                ReportProgress();

                var assetResults = ExecuteParallelCook(workItems, config, _cts.Token);
                result.AssetResults = assetResults.ToList();

                // 5. 统计结果
                foreach (var ar in result.AssetResults)
                {
                    switch (ar.Status)
                    {
                        case CookAssetStatus.Completed:
                            result.SuccessCount++;
                            result.TotalSourceSizeBytes += ar.SourceSizeBytes;
                            result.TotalOutputSizeBytes += ar.OutputSizeBytes;
                            break;
                        case CookAssetStatus.Cached:
                            result.CachedCount++;
                            break;
                        case CookAssetStatus.Failed:
                            result.FailedCount++;
                            result.Errors.Add($"Cook failed: {ar.SourcePath} - {ar.Error}");
                            break;
                        case CookAssetStatus.Skipped:
                            result.SkippedCount++;
                            break;
                    }
                    result.Warnings.AddRange(ar.Warnings);
                }

                result.Success = result.FailedCount == 0;

                // 6. 生成 Cook 清单
                _progress.CurrentPhase = "Generating manifest";
                ReportProgress();
                GenerateCookManifest(result, config);

                // 7. 保存日志
                var logPath = Path.Combine(config.OutputDirectory, "cook_log.txt");
                _logger.SaveToFile(logPath);
                result.LogFilePath = logPath;
            }
            catch (OperationCanceledException)
            {
                result.Success = false;
                result.Errors.Add("Cook operation was cancelled by user.");
                _logger.Log(CookLogLevel.Warning, "Cook cancelled by user.");
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Errors.Add($"Cook system error: {ex.Message}");
                _logger.Log(CookLogLevel.Error, $"Cook system error: {ex.Message}");
            }
            finally
            {
                overallSw.Stop();
                result.TotalDurationMs = overallSw.Elapsed.TotalMilliseconds;
                _progress.ProcessedAssets = _progress.TotalAssets;
                ReportProgress();
                IsCooking = false;
                _cts?.Dispose();
                _cts = null;
            }

            return result;
        }

        /// <summary>
        /// 收集待 Cook 的工作项。
        /// </summary>
        private List<CookWorkItem> CollectWorkItems(CookCommand command, CookConfiguration config)
        {
            var items = new List<CookWorkItem>();
            var assetRoot = string.IsNullOrWhiteSpace(config.AssetRootDirectory)
                ? "Assets"
                : config.AssetRootDirectory;

            if (!Directory.Exists(assetRoot))
            {
                _logger.Log(CookLogLevel.Warning, $"Asset root directory not found: {assetRoot}");
                return items;
            }

            switch (command.Type)
            {
                case CookCommandType.CookAll:
                case CookCommandType.CookIncremental:
                    items = CollectAllAssets(assetRoot, config);
                    break;

                case CookCommandType.CookLevel:
                    items = CollectLevelAssets(command.LevelPath ?? assetRoot, assetRoot, config);
                    break;

                case CookCommandType.CookSpecific:
                    items = CollectSpecificAssets(command.AssetPaths ?? new List<string>(), assetRoot, config);
                    break;

                case CookCommandType.CookShadersOnly:
                    items = CollectAssetsByExtension(assetRoot, config, ShaderExtensions, AssetType.Shader);
                    break;

                case CookCommandType.CookTexturesOnly:
                    items = CollectAssetsByExtension(assetRoot, config, TextureExtensions, AssetType.Texture);
                    break;

                case CookCommandType.CookMeshesOnly:
                    items = CollectAssetsByExtension(assetRoot, config, MeshExtensions, AssetType.Mesh);
                    break;
            }

            // 为每个工作项计算哈希
            foreach (var item in items)
            {
                item.ContentHash = ComputeFileHash(item.SourcePath);
                item.ConfigSignature = ComputeConfigSignature(config);
            }

            return items;
        }

        private List<CookWorkItem> CollectAllAssets(string assetRoot, CookConfiguration config)
        {
            var items = new List<CookWorkItem>();
            CollectRecursive(assetRoot, assetRoot, config, items);
            return items;
        }

        private void CollectRecursive(string currentDir, string assetRoot, CookConfiguration config, List<CookWorkItem> items)
        {
            foreach (var file in Directory.EnumerateFiles(currentDir))
            {
                var ext = Path.GetExtension(file).ToLowerInvariant();
                var assetType = GetAssetType(ext);
                if (assetType == AssetType.Other) continue;

                var relativePath = Path.GetRelativePath(assetRoot, file);
                var outputDir = Path.Combine(config.OutputDirectory, "Cooked", Path.GetDirectoryName(relativePath)!);
                var outputPath = Path.Combine(outputDir, Path.GetFileNameWithoutExtension(relativePath) + ".cooked" + ext);

                items.Add(new CookWorkItem
                {
                    SourcePath = file,
                    OutputPath = outputPath,
                    AssetType = assetType
                });
            }

            foreach (var dir in Directory.EnumerateDirectories(currentDir))
            {
                CollectRecursive(dir, assetRoot, config, items);
            }
        }

        private List<CookWorkItem> CollectLevelAssets(string levelPath, string assetRoot, CookConfiguration config)
        {
            // 收集关卡引用的所有资产（简化实现：收集关卡目录下所有资产）
            var levelDir = Path.Combine(assetRoot, levelPath);
            if (!Directory.Exists(levelDir))
                levelDir = levelPath;

            return Directory.Exists(levelDir)
                ? CollectAllAssets(levelDir, config)
                : new List<CookWorkItem>();
        }

        private List<CookWorkItem> CollectSpecificAssets(List<string> assetPaths, string assetRoot, CookConfiguration config)
        {
            var items = new List<CookWorkItem>();
            foreach (var path in assetPaths)
            {
                var fullPath = Path.Combine(assetRoot, path);
                if (!File.Exists(fullPath))
                {
                    _logger.Log(CookLogLevel.Warning, $"Specified asset not found: {path}");
                    continue;
                }

                var ext = Path.GetExtension(fullPath).ToLowerInvariant();
                var assetType = GetAssetType(ext);

                var outputDir = Path.Combine(config.OutputDirectory, "Cooked", Path.GetDirectoryName(path)!);
                var outputPath = Path.Combine(outputDir, Path.GetFileNameWithoutExtension(path) + ".cooked" + ext);

                items.Add(new CookWorkItem
                {
                    SourcePath = fullPath,
                    OutputPath = outputPath,
                    AssetType = assetType
                });
            }
            return items;
        }

        private List<CookWorkItem> CollectAssetsByExtension(string assetRoot, CookConfiguration config, string[] extensions, AssetType type)
        {
            var items = new List<CookWorkItem>();
            foreach (var file in Directory.EnumerateFiles(assetRoot, "*.*", SearchOption.AllDirectories))
            {
                var ext = Path.GetExtension(file).ToLowerInvariant();
                if (!extensions.Contains(ext)) continue;

                var relativePath = Path.GetRelativePath(assetRoot, file);
                var outputDir = Path.Combine(config.OutputDirectory, "Cooked", Path.GetDirectoryName(relativePath)!);
                var outputPath = Path.Combine(outputDir, Path.GetFileNameWithoutExtension(relativePath) + ".cooked" + ext);

                items.Add(new CookWorkItem
                {
                    SourcePath = file,
                    OutputPath = outputPath,
                    AssetType = type
                });
            }
            return items;
        }

        /// <summary>
        /// 增量过滤：与缓存对比，移除未变更的资产。
        /// </summary>
        private List<CookWorkItem> FilterIncremental(List<CookWorkItem> items, CookConfiguration config)
        {
            if (string.IsNullOrWhiteSpace(config.CacheDirectory)) return items;

            var cacheDir = Path.Combine(config.CacheDirectory, "CookCache");
            var cacheFile = Path.Combine(cacheDir, "cook_hash_cache.json");

            Dictionary<string, CookCacheEntry> cacheEntries = new();
            if (File.Exists(cacheFile))
            {
                try
                {
                    var json = File.ReadAllText(cacheFile, Encoding.UTF8);
                    cacheEntries = JsonSerializer.Deserialize<Dictionary<string, CookCacheEntry>>(json)
                        ?? new Dictionary<string, CookCacheEntry>();
                }
                catch
                {
                    // 缓存损坏，全部重新 Cook
                    return items;
                }
            }

            var filtered = new List<CookWorkItem>();
            foreach (var item in items)
            {
                var cacheKey = $"{item.SourcePath}|{item.ConfigSignature}";
                if (cacheEntries.TryGetValue(cacheKey, out var cached)
                    && cached.ContentHash == item.ContentHash
                    && File.Exists(cached.OutputPath))
                {
                    // 缓存命中，跳过
                    filtered.Add(item); // 保留但标记为缓存命中
                    item.ContentHash = "__CACHED__"; // 特殊标记
                }
                else
                {
                    filtered.Add(item);
                }
            }

            return filtered;
        }

        /// <summary>
        /// 并行执行 Cook 工作项。
        /// </summary>
        private List<CookedAssetResult> ExecuteParallelCook(List<CookWorkItem> items, CookConfiguration config, CancellationToken ct)
        {
            var results = new ConcurrentBag<CookedAssetResult>();
            var queue = new BlockingCollection<CookWorkItem>(new ConcurrentQueue<CookWorkItem>(items));

            // 启动 Worker 线程
            var workerCount = Math.Min(config.MaxParallelWorkers, items.Count);
            var workers = new List<Thread>();
            var processedCounter = 0;

            for (var i = 0; i < workerCount; i++)
            {
                var worker = new Thread(() =>
                {
                    var cw = new CookWorker(i, config, queue, results, ct, msg =>
                    {
                        _logger.Log(CookLogLevel.Verbose, msg);
                        Interlocked.Increment(ref processedCounter);
                        _progress.ProcessedAssets = processedCounter;
                        _progress.CurrentAssetPath = msg;
                        ReportProgress();
                    });
                    cw.Run();
                })
                {
                    Name = $"CookWorker-{i}",
                    IsBackground = true
                };
                workers.Add(worker);
                worker.Start();
            }

            // 等待所有 Worker 完成
            foreach (var worker in workers)
            {
                worker.Join();
            }

            return results.ToList();
        }

        /// <summary>
        /// 生成 Cook 清单文件。
        /// </summary>
        private void GenerateCookManifest(CookResult result, CookConfiguration config)
        {
            var manifest = new CookManifest
            {
                Platform = config.Platform.ToString(),
                CookedAtUtc = DateTime.UtcNow,
                TotalAssets = result.TotalAssets,
                SuccessCount = result.SuccessCount,
                CachedCount = result.CachedCount,
                FailedCount = result.FailedCount,
                TotalSourceSizeBytes = result.TotalSourceSizeBytes,
                TotalOutputSizeBytes = result.TotalOutputSizeBytes,
                DurationMs = result.TotalDurationMs,
                Assets = result.AssetResults
                    .Where(r => r.Status is CookAssetStatus.Completed or CookAssetStatus.Cached)
                    .Select(r => new CookManifestEntry
                    {
                        SourcePath = r.SourcePath,
                        OutputPath = r.OutputPath,
                        SizeBytes = r.OutputSizeBytes,
                        Hash = ComputeFileHash(r.OutputPath)
                    })
                    .ToList()
            };

            var manifestPath = Path.Combine(config.OutputDirectory, "cook_manifest.json");
            var json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(manifestPath, json, Encoding.UTF8);

            // 更新增量缓存
            if (config.Incremental && !string.IsNullOrWhiteSpace(config.CacheDirectory))
            {
                UpdateCookCache(result.AssetResults, config);
            }
        }

        private void UpdateCookCache(List<CookedAssetResult> results, CookConfiguration config)
        {
            var cacheDir = Path.Combine(config.CacheDirectory, "CookCache");
            Directory.CreateDirectory(cacheDir);
            var cacheFile = Path.Combine(cacheDir, "cook_hash_cache.json");

            Dictionary<string, CookCacheEntry> cacheEntries = new();
            if (File.Exists(cacheFile))
            {
                try
                {
                    var json = File.ReadAllText(cacheFile, Encoding.UTF8);
                    cacheEntries = JsonSerializer.Deserialize<Dictionary<string, CookCacheEntry>>(json)
                        ?? new Dictionary<string, CookCacheEntry>();
                }
                catch { }
            }

            foreach (var r in results)
            {
                if (r.Status != CookAssetStatus.Completed) continue;
                // 需要从工作项中获取 ConfigSignature，此处简化处理
                var cacheKey = r.SourcePath;
                cacheEntries[cacheKey] = new CookCacheEntry
                {
                    ContentHash = ComputeFileHash(r.SourcePath),
                    OutputPath = r.OutputPath,
                    Timestamp = DateTime.UtcNow
                };
            }

            var jsonOut = JsonSerializer.Serialize(cacheEntries, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(cacheFile, jsonOut, Encoding.UTF8);
        }

        /// <summary>
        /// 计算文件内容 SHA256 哈希。
        /// </summary>
        internal static string ComputeFileHash(string filePath)
        {
            if (!File.Exists(filePath)) return string.Empty;
            using var sha = SHA256.Create();
            using var stream = File.OpenRead(filePath);
            var hashBytes = sha.ComputeHash(stream);
            return Convert.ToHexString(hashBytes).ToLowerInvariant();
        }

        /// <summary>
        /// 计算 Cook 配置签名。
        /// </summary>
        private string ComputeConfigSignature(CookConfiguration config)
        {
            var data = $"{config.Platform}|{config.GetTextureFormat()}|{config.EnableCompression}|{config.GenerateLOD}|" +
                       $"{config.CompileAllShaderVariants}|{config.MeshLODCount}|{config.MaxTextureResolution}|{config.AudioQuality}";
            using var sha = SHA256.Create();
            var hashBytes = sha.ComputeHash(Encoding.UTF8.GetBytes(data));
            return Convert.ToHexString(hashBytes).ToLowerInvariant().Substring(0, 16);
        }

        /// <summary>
        /// 根据文件扩展名判断资产类型。
        /// </summary>
        private static AssetType GetAssetType(string extension)
        {
            if (TextureExtensions.Contains(extension)) return AssetType.Texture;
            if (MeshExtensions.Contains(extension)) return AssetType.Mesh;
            if (ShaderExtensions.Contains(extension)) return AssetType.Shader;
            if (AudioExtensions.Contains(extension)) return AssetType.Audio;
            if (MaterialExtensions.Contains(extension)) return AssetType.Material;
            return AssetType.Other;
        }

        private void ReportProgress()
        {
            _progressCallback?.Invoke(_progress);
        }

        // ── Cook 缓存条目（增量检测用） ──
        private sealed class CookCacheEntry
        {
            public string ContentHash { get; set; } = string.Empty;
            public string OutputPath { get; set; } = string.Empty;
            public DateTime Timestamp { get; set; }
        }

        // ── Cook 清单序列化类型 ──
        private sealed class CookManifest
        {
            public string Platform { get; set; } = string.Empty;
            public DateTime CookedAtUtc { get; set; }
            public int TotalAssets { get; set; }
            public int SuccessCount { get; set; }
            public int CachedCount { get; set; }
            public int FailedCount { get; set; }
            public long TotalSourceSizeBytes { get; set; }
            public long TotalOutputSizeBytes { get; set; }
            public double DurationMs { get; set; }
            public List<CookManifestEntry> Assets { get; set; } = new();
        }

        private sealed class CookManifestEntry
        {
            public string SourcePath { get; set; } = string.Empty;
            public string OutputPath { get; set; } = string.Empty;
            public long SizeBytes { get; set; }
            public string Hash { get; set; } = string.Empty;
        }
    }
}
