using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using YamaEngine.RHI;
using YamaEngine.Audio;
using YamaEngine.Assets.Importers;

namespace YamaEngine.Assets
{
    // ====================================================================
    // ResourceLoader — 资源加载管线
    // ====================================================================
    // 提供纹理、着色器、音频、模型的统一加载接口。
    // 内置 LRU 缓存、优先级驱动异步加载队列。
    // ====================================================================

    // ──────────────────────────────────────────────────────────────
    // ResourceRequest — 加载请求
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 资源加载请求，描述加载任务的路径、类型、优先级和完成回调。
    /// </summary>
    public class ResourceRequest : IComparable<ResourceRequest>
    {
        /// <summary>资源虚拟路径</summary>
        public string Path { get; }

        /// <summary>资源类型</summary>
        public AssetType Type { get; }

        /// <summary>加载优先级（数字越大越优先）</summary>
        public int Priority { get; set; }

        /// <summary>加载完成回调，参数为加载结果对象或 null（失败时）</summary>
        public Action<object?>? OnCompleted { get; set; }

        /// <summary>加载失败回调</summary>
        public Action<Exception?>? OnFailed { get; set; }

        /// <summary>用户状态，用于关联上下文</summary>
        public object? UserState { get; set; }

        /// <summary>请求创建时间</summary>
        public DateTime CreatedAt { get; } = DateTime.UtcNow;

        /// <summary>是否已完成</summary>
        public bool IsCompleted { get; internal set; }

        /// <summary>取消令牌</summary>
        public CancellationToken CancellationToken { get; set; }

        public ResourceRequest(string path, AssetType type, int priority = 0)
        {
            Path = VFileSystem.NormalizePath(path);
            Type = type;
            Priority = priority;
        }

        public int CompareTo(ResourceRequest? other)
        {
            if (other is null) return 1;
            // 优先级高的排在前面；同优先级按创建时间升序
            int cmp = other.Priority.CompareTo(Priority);
            return cmp != 0 ? cmp : CreatedAt.CompareTo(other.CreatedAt);
        }
    }

    // ──────────────────────────────────────────────────────────────
    // ResourceCache — LRU 资源缓存
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// LRU（最近最少使用）资源缓存。
    /// 当缓存达到容量上限时，自动驱逐最久未使用的资源。
    /// </summary>
    public class ResourceCache : IDisposable
    {
        private readonly int _maxItems;
        private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);
        private readonly LinkedList<string> _lruList = new();
        private readonly object _lock = new();

        /// <summary>缓存条目数量</summary>
        public int Count
        {
            get
            {
                lock (_lock) return _cache.Count;
            }
        }

        /// <summary>缓存容量上限（条目数），0 表示无限制</summary>
        public int MaxItems => _maxItems;

        public ResourceCache(int maxItems = 256)
        {
            _maxItems = maxItems;
        }

        private class CacheEntry
        {
            public object Resource;
            public ulong SizeBytes;
            public DateTime LastAccessTime;
            public LinkedListNode<string> LruNode;
        }

        /// <summary>
        /// 将资源加入缓存。如果已存在则更新。
        /// </summary>
        public void Add(string key, object resource, ulong sizeBytes = 0)
        {
            key = VFileSystem.NormalizePath(key);

            lock (_lock)
            {
                if (_cache.TryGetValue(key, out var existing))
                {
                    existing.Resource = resource;
                    existing.SizeBytes = sizeBytes;
                    existing.LastAccessTime = DateTime.UtcNow;
                    // 移动到 LRU 链表头部
                    _lruList.Remove(existing.LruNode);
                    _lruList.AddFirst(existing.LruNode);
                    return;
                }

                // 驱逐超容量的资源
                while (_maxItems > 0 && _cache.Count >= _maxItems)
                {
                    EvictOne();
                }

                var node = _lruList.AddFirst(key);
                _cache[key] = new CacheEntry
                {
                    Resource = resource,
                    SizeBytes = sizeBytes,
                    LastAccessTime = DateTime.UtcNow,
                    LruNode = node
                };
            }
        }

        /// <summary>
        /// 从缓存获取资源。命中时更新 LRU 顺序。
        /// </summary>
        public object? Get(string key)
        {
            key = VFileSystem.NormalizePath(key);

            lock (_lock)
            {
                if (_cache.TryGetValue(key, out var entry))
                {
                    entry.LastAccessTime = DateTime.UtcNow;
                    _lruList.Remove(entry.LruNode);
                    _lruList.AddFirst(entry.LruNode);
                    return entry.Resource;
                }
            }

            return null;
        }

        /// <summary>
        /// 获取指定类型的缓存资源。
        /// </summary>
        public T? Get<T>(string key) where T : class
        {
            return Get(key) as T;
        }

        /// <summary>
        /// 判断缓存是否包含指定资源。
        /// </summary>
        public bool Contains(string key)
        {
            key = VFileSystem.NormalizePath(key);
            return _cache.ContainsKey(key);
        }

        /// <summary>
        /// 从缓存中移除指定资源。如果资源实现了 IDisposable，则自动释放。
        /// </summary>
        public bool Remove(string key)
        {
            key = VFileSystem.NormalizePath(key);

            lock (_lock)
            {
                if (_cache.TryRemove(key, out var entry))
                {
                    _lruList.Remove(entry.LruNode);
                    (entry.Resource as IDisposable)?.Dispose();
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 清空所有缓存。
        /// </summary>
        public void Clear()
        {
            lock (_lock)
            {
                foreach (var kvp in _cache)
                {
                    (kvp.Value.Resource as IDisposable)?.Dispose();
                }
                _cache.Clear();
                _lruList.Clear();
            }
        }

        /// <summary>
        /// 获取缓存中所有资源占用的总字节数。
        /// </summary>
        public ulong TotalSizeBytes
        {
            get
            {
                lock (_lock)
                {
                    ulong total = 0;
                    foreach (var kvp in _cache)
                    {
                        total += kvp.Value.SizeBytes;
                    }
                    return total;
                }
            }
        }

        private void EvictOne()
        {
            if (_lruList.Last is null) return;

            var lruKey = _lruList.Last.Value;
            if (_cache.TryRemove(lruKey, out var entry))
            {
                _lruList.RemoveLast();
                (entry.Resource as IDisposable)?.Dispose();
            }
        }

        public void Dispose()
        {
            Clear();
        }
    }

    // ──────────────────────────────────────────────────────────────
    // LoadedTexture — 加载后的纹理资源包装
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 纹理加载结果，包含 GPU 纹理对象和元数据。
    /// </summary>
    public class LoadedTexture : IDisposable
    {
        /// <summary>RHI GPU 纹理对象</summary>
        public RHITexture? GpuTexture { get; }

        /// <summary>纹理宽度</summary>
        public int Width { get; }

        /// <summary>纹理高度</summary>
        public int Height { get; }

        /// <summary>Mipmap 层数</summary>
        public int MipLevels { get; }

        /// <summary>纹理格式</summary>
        public RHIFormat Format { get; }

        /// <summary>原始像素数据（CPU 端，可选择性保留）</summary>
        public byte[]? PixelData { get; }

        /// <summary>源文件路径</summary>
        public string SourcePath { get; }

        public LoadedTexture(RHITexture? gpuTexture, int width, int height, int mipLevels,
            RHIFormat format, byte[]? pixelData, string sourcePath)
        {
            GpuTexture = gpuTexture;
            Width = width;
            Height = height;
            MipLevels = mipLevels;
            Format = format;
            PixelData = pixelData;
            SourcePath = sourcePath;
        }

        public void Dispose()
        {
            GpuTexture?.Dispose();
        }
    }

    // ──────────────────────────────────────────────────────────────
    // LoadedShader — 加载后的着色器资源包装
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 着色器加载结果。
    /// </summary>
    public class LoadedShader
    {
        /// <summary>着色器源代码文本</summary>
        public string SourceCode { get; }

        /// <summary>着色器入口函数名</summary>
        public string EntryPoint { get; }

        /// <summary>着色器模型（如 "vs_6_0"）</summary>
        public string ShaderModel { get; }

        /// <summary>源文件路径</summary>
        public string SourcePath { get; }

        public LoadedShader(string sourceCode, string entryPoint, string shaderModel, string sourcePath)
        {
            SourceCode = sourceCode;
            EntryPoint = entryPoint;
            ShaderModel = shaderModel;
            SourcePath = sourcePath;
        }
    }

    // ──────────────────────────────────────────────────────────────
    // LoadedAudio — 加载后的音频资源包装
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 音频加载结果。
    /// </summary>
    public class LoadedAudio : IDisposable
    {
        /// <summary>原始音频数据</summary>
        public byte[] Data { get; }

        /// <summary>音频格式</summary>
        public AudioFormat Format { get; }

        /// <summary>采样率</summary>
        public int SampleRate { get; }

        /// <summary>声道数</summary>
        public int Channels { get; }

        /// <summary>位深度</summary>
        public int BitsPerSample { get; }

        /// <summary>源文件路径</summary>
        public string SourcePath { get; }

        public LoadedAudio(byte[] data, AudioFormat format, int sampleRate, int channels,
            int bitsPerSample, string sourcePath)
        {
            Data = data;
            Format = format;
            SampleRate = sampleRate;
            Channels = channels;
            BitsPerSample = bitsPerSample;
            SourcePath = sourcePath;
        }

        public void Dispose() { /* 原始字节数据无需释放 */ }
    }

    /// <summary>音频格式枚举</summary>

    // ──────────────────────────────────────────────────────────────
    // LoadedModel — 加载后的模型资源包装
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 模型加载结果（占位实现）。
    /// </summary>
    public class LoadedModel : IDisposable
    {
        /// <summary>模型名称。</summary>
        public string Name { get; set; } = "";

        /// <summary>节点层级列表（导入器填充）。</summary>
        public List<LoadedNode> Nodes { get; } = new();

        /// <summary>顶点数据缓冲区（GPU）</summary>
        public RHIBuffer? VertexBuffer { get; set; }

        /// <summary>索引数据缓冲区（GPU）</summary>
        public RHIBuffer? IndexBuffer { get; set; }

        /// <summary>顶点数量</summary>
        public int VertexCount { get; set; }

        /// <summary>索引数量</summary>
        public int IndexCount { get; set; }

        /// <summary>顶点步长</summary>
        public int VertexStride { get; set; }

        /// <summary>子网格列表</summary>
        public List<ModelSubMesh> SubMeshes { get; } = new();

        /// <summary>源文件路径</summary>
        public string SourcePath { get; set; }

        /// <summary>CPU 端交错顶点数据（导入器填充，可直接上传 GPU）。null 表示尚未生成或已上传后释放。</summary>
        public byte[]? RawVertexData { get; set; }

        /// <summary>CPU 端索引数据（导入器填充，可直接上传 GPU）。</summary>
        public byte[]? RawIndexData { get; set; }

        /// <summary>是否使用 32 位索引；false 表示 16 位索引。</summary>
        public bool Use32BitIndices { get; set; }

        /// <summary>解析得到的材质列表（可选，导入器填充）。</summary>
        public List<LoadedMaterial> Materials { get; } = new();

        /// <summary>模型根节点的本地变换矩阵（来自导入文件的节点层级）。</summary>
        public System.Numerics.Matrix4x4 RootTransform { get; set; } = System.Numerics.Matrix4x4.Identity;

        public LoadedModel(string sourcePath = "")
        {
            SourcePath = sourcePath;
        }

        public void Dispose()
        {
            VertexBuffer?.Dispose();
            IndexBuffer?.Dispose();
        }
    }

    /// <summary>子网格描述</summary>

    // ──────────────────────────────────────────────────────────────
    // ResourceLoader — 主加载器
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 资源加载器，提供纹理、着色器、音频、模型的同步/异步加载。
    /// 内置 LRU 缓存和优先级驱动的后台加载队列。
    /// </summary>
    public sealed class ResourceLoader : IDisposable
    {
        private readonly RHIDevice _device;
        private readonly VFileSystem _vfs;
        private readonly ResourceCache _cache;
        private readonly YamaEngine.RHI.PriorityQueue<ResourceRequest, int> _requestQueue = new();
        private readonly Thread _workerThread;
        private readonly CancellationTokenSource _workerCts = new();
        private readonly SemaphoreSlim _queueSignal = new(0);
        private readonly object _queueLock = new();

        /// <summary>资源缓存</summary>
        public ResourceCache Cache => _cache;

        /// <summary>当前排队请求数</summary>
        public int PendingRequests
        {
            get
            {
                lock (_queueLock) return _requestQueue.Count;
            }
        }

        /// <summary>RHI 设备引用</summary>
        public RHIDevice Device => _device;

        public ResourceLoader(RHIDevice device, VFileSystem? vfs = null, int cacheSize = 256)
        {
            _device = device ?? throw new ArgumentNullException(nameof(device));
            _vfs = vfs ?? VFileSystem.Instance;
            _cache = new ResourceCache(cacheSize);

            // 启动后台加载线程
            _workerThread = new Thread(WorkerLoop)
            {
                Name = "YamaResourceLoader",
                IsBackground = true
            };
            _workerThread.Start();
        }

        // ══════════════════════════════════════════════════════════
        // 同步加载
        // ══════════════════════════════════════════════════════════

        /// <summary>
        /// 同步加载纹理。
        /// 支持 .png, .jpg, .jpeg, .bmp, .tga, .webp 格式。
        /// </summary>
        public LoadedTexture LoadTexture(string path)
        {
            path = VFileSystem.NormalizePath(path);

            // 检查缓存
            var cached = _cache.Get<LoadedTexture>(path);
            if (cached != null) return cached;

            // 从 VFileSystem 读取字节
            var bytes = _vfs.ReadAllBytes(path);

            // 使用 ImageSharp 解码
            using var image = SixLabors.ImageSharp.Image.Load<Rgba32>(bytes);
            int width = image.Width;
            int height = image.Height;

            // 提取像素数据
            var pixelData = new byte[width * height * 4];
            image.CopyPixelDataTo(pixelData);

            // 计算 Mipmap 层数
            int mipLevels = CalculateMipLevels(width, height);

            // 创建 RHI 纹理
            var texDesc = new RHITextureDesc
            {
                Dimension = RHIResourceDimension.Texture2D,
                Format = RHIFormat.R8G8B8A8_UNorm,
                Width = width,
                Height = height,
                DepthOrArraySize = 1,
                MipLevels = mipLevels,
                SampleCount = 1,
                SampleQuality = 0,
                Flags = RHITextureFlags.None,
                OptimizedClearValue = null
            };

            RHITexture? gpuTexture = null;
            try
            {
                gpuTexture = _device.CreateTexture(texDesc, $"Texture:{path}");

                // 上传主纹理（mip 0）
                _device.UploadToTexture(gpuTexture, pixelData, width, height, mipIndex: 0);

                // 如果支持多级 Mipmap 且有 GPU 生成能力，上传各级
                if (mipLevels > 1 && gpuTexture != null)
                {
                    GenerateAndUploadMipLevels(gpuTexture, pixelData, width, height, mipLevels);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"ResourceLoader: GPU 纹理创建失败 '{path}': {ex.Message}");
                gpuTexture?.Dispose();
                gpuTexture = null;
            }

            var result = new LoadedTexture(gpuTexture, width, height, mipLevels,
                RHIFormat.R8G8B8A8_UNorm, pixelData, path);

            _cache.Add(path, result, (ulong)(width * height * 4 * mipLevels));
            return result;
        }

        /// <summary>
        /// 同步加载着色器。
        /// 读取 .hlsl 文件的原始文本内容。
        /// </summary>
        public LoadedShader LoadShader(string path, string entryPoint = "main", string? shaderModel = null)
        {
            path = VFileSystem.NormalizePath(path);

            var cached = _cache.Get<LoadedShader>(path);
            if (cached != null) return cached;

            string source = _vfs.ReadAllText(path);

            // 根据文件名推断着色器模型
            shaderModel ??= InferShaderModel(path);

            var result = new LoadedShader(source, entryPoint, shaderModel, path);
            _cache.Add(path, result);
            return result;
        }

        /// <summary>
        /// 同步加载音频。
        /// 支持 .wav（PCM 解析）, .ogg/.mp3/.flac（返回原始字节）。
        /// </summary>
        public LoadedAudio LoadAudio(string path)
        {
            path = VFileSystem.NormalizePath(path);

            var cached = _cache.Get<LoadedAudio>(path);
            if (cached != null) return cached;

            var bytes = _vfs.ReadAllBytes(path);
            string ext = Path.GetExtension(path).ToLowerInvariant();

            int sampleRate = 44100;
            int channels = 2;
            int bitsPerSample = 16;
            AudioFormat format = AudioFormat.Unknown;

            if (ext == ".wav")
            {
                // 解析 WAV 头
                var header = ParseWavHeader(bytes);
                sampleRate = header.SampleRate;
                channels = header.Channels;
                bitsPerSample = header.BitsPerSample;
                format = header.IsFloat ? AudioFormat.WAV_Float : AudioFormat.WAV_PCM;
            }
            else if (ext == ".ogg")
            {
                format = AudioFormat.OGG_Vorbis;
                // OGG 需要 Miniaudio 解码，此处保留原始字节
            }
            else if (ext == ".flac")
            {
                format = AudioFormat.FLAC;
            }
            else if (ext == ".mp3")
            {
                format = AudioFormat.MP3;
            }

            var result = new LoadedAudio(bytes, format, sampleRate, channels, bitsPerSample, path);
            _cache.Add(path, result, (ulong)bytes.Length);
            return result;
        }

        /// <summary>
        /// 同步加载模型（占位实现，返回空 Mesh）。
        /// 框架已就绪，后续接入 glTF/FBX/USD 导入器即可。
        /// </summary>
        public LoadedModel LoadModel(string path)
        {
            path = VFileSystem.NormalizePath(path);

            var cached = _cache.Get<LoadedModel>(path);
            if (cached != null) return cached;

            // 占位：返回空的 LoadedModel
            var result = new LoadedModel(path);
            _cache.Add(path, result);
            return result;
        }

        // ══════════════════════════════════════════════════════════
        // 异步加载
        // ══════════════════════════════════════════════════════════

        /// <summary>
        /// 提交异步资源加载请求。
        /// 请求会被放入优先级队列，由后台线程按优先级依次处理。
        /// </summary>
        public void LoadAsync(ResourceRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));

            lock (_queueLock)
            {
                // 优先级取负，因为 PriorityQueue 是最小堆
                _requestQueue.Enqueue(request, -request.Priority);
            }
            _queueSignal.Release();
        }

        /// <summary>
        /// 提交异步纹理加载请求的便捷方法。
        /// </summary>
        public void LoadTextureAsync(string path, int priority = 0,
            Action<LoadedTexture?>? onCompleted = null, Action<Exception?>? onFailed = null)
        {
            var request = new ResourceRequest(path, AssetType.Texture2D, priority)
            {
                OnCompleted = obj => onCompleted?.Invoke(obj as LoadedTexture),
                OnFailed = onFailed
            };
            LoadAsync(request);
        }

        /// <summary>
        /// 提交异步着色器加载请求的便捷方法。
        /// </summary>
        public void LoadShaderAsync(string path, int priority = 0,
            string entryPoint = "main", string? shaderModel = null,
            Action<LoadedShader?>? onCompleted = null)
        {
            var request = new ResourceRequest(path, AssetType.Shader, priority)
            {
                UserState = new ShaderAsyncState { EntryPoint = entryPoint, ShaderModel = shaderModel },
                OnCompleted = obj => onCompleted?.Invoke(obj as LoadedShader)
            };
            LoadAsync(request);
        }

        private class ShaderAsyncState
        {
            public string EntryPoint { get; set; } = "main";
            public string? ShaderModel { get; set; }
        }

        /// <summary>
        /// 后台工作线程主循环：按优先级处理加载请求。
        /// </summary>
        private void WorkerLoop()
        {
            while (!_workerCts.IsCancellationRequested)
            {
                try
                {
                    _queueSignal.Wait(_workerCts.Token);

                    ResourceRequest? request = null;
                    lock (_queueLock)
                    {
                        if (_requestQueue.TryDequeue(out request, out _))
                        {
                            // 取到请求
                        }
                    }

                    if (request == null) continue;
                    if (request.CancellationToken.IsCancellationRequested) continue;

                    try
                    {
                        object? result = ProcessRequest(request);
                        request.IsCompleted = true;
                        request.OnCompleted?.Invoke(result);
                    }
                    catch (OperationCanceledException)
                    {
                        // 取消的请求，静默处理
                    }
                    catch (Exception ex)
                    {
                        request.IsCompleted = true;
                        request.OnFailed?.Invoke(ex);
                        System.Diagnostics.Debug.WriteLine(
                            $"ResourceLoader: 加载失败 '{request.Path}': {ex.Message}");
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        private object? ProcessRequest(ResourceRequest request)
        {
            return request.Type switch
            {
                AssetType.Texture2D => LoadTexture(request.Path),
                AssetType.Shader => LoadShader(request.Path, (request.UserState as ShaderAsyncState)?.EntryPoint ?? "main", (request.UserState as ShaderAsyncState)?.ShaderModel),
                AssetType.Audio => LoadAudio(request.Path),
                AssetType.Mesh => LoadModel(request.Path),
                _ => throw new NotSupportedException($"不支持的资源类型: {request.Type}")
            };
        }

        // ══════════════════════════════════════════════════════════
        // Mipmap 生成
        // ══════════════════════════════════════════════════════════

        /// <summary>
        /// 计算给定尺寸的 Mipmap 层数。
        /// </summary>
        public static int CalculateMipLevels(int width, int height)
        {
            int levels = 1;
            int maxDim = Math.Max(width, height);
            while (maxDim > 1)
            {
                maxDim >>= 1;
                levels++;
            }
            return levels;
        }

        /// <summary>
        /// 从 mip 0 像素数据生成各级 Mipmap 并上传到 GPU。
        /// 使用简单的 Box Filter（区域平均）进行降采样。
        /// </summary>
        private void GenerateAndUploadMipLevels(RHITexture texture, byte[] mip0Data,
            int width, int height, int totalMipLevels)
        {
            int mipWidth = width;
            int mipHeight = height;
            byte[] currentData = mip0Data;

            for (int mip = 1; mip < totalMipLevels; mip++)
            {
                int newWidth = Math.Max(1, mipWidth >> 1);
                int newHeight = Math.Max(1, mipHeight >> 1);
                int newRowPitch = newWidth * 4;

                var newData = new byte[newWidth * newHeight * 4];

                // Box filter 降采样：2x2 像素平均为一个像素
                for (int y = 0; y < newHeight; y++)
                {
                    for (int x = 0; x < newWidth; x++)
                    {
                        int srcX = x * 2;
                        int srcY = y * 2;

                        // 采样 4 个源像素并平均
                        uint r = 0, g = 0, b = 0, a = 0, count = 0;

                        for (int dy = 0; dy < 2 && (srcY + dy) < mipHeight; dy++)
                        {
                            for (int dx = 0; dx < 2 && (srcX + dx) < mipWidth; dx++)
                            {
                                int srcIdx = ((srcY + dy) * mipWidth + (srcX + dx)) * 4;
                                r += currentData[srcIdx + 0];
                                g += currentData[srcIdx + 1];
                                b += currentData[srcIdx + 2];
                                a += currentData[srcIdx + 3];
                                count++;
                            }
                        }

                        int dstIdx = (y * newWidth + x) * 4;
                        newData[dstIdx + 0] = (byte)(r / count);
                        newData[dstIdx + 1] = (byte)(g / count);
                        newData[dstIdx + 2] = (byte)(b / count);
                        newData[dstIdx + 3] = (byte)(a / count);
                    }
                }

                // 上传该级 Mipmap
                _device.UploadToTexture(texture, newData, newWidth, newHeight, mipIndex: mip);

                mipWidth = newWidth;
                mipHeight = newHeight;
                currentData = newData;
            }
        }

        // ══════════════════════════════════════════════════════════
        // WAV 解析
        // ══════════════════════════════════════════════════════════

        private struct WavHeaderInfo
        {
            public int SampleRate;
            public int Channels;
            public int BitsPerSample;
            public bool IsFloat;
        }

        private static WavHeaderInfo ParseWavHeader(byte[] data)
        {
            var info = new WavHeaderInfo
            {
                SampleRate = 44100,
                Channels = 2,
                BitsPerSample = 16,
                IsFloat = false
            };

            if (data.Length < 44) return info;

            // 检查 RIFF 标识
            string riff = System.Text.Encoding.ASCII.GetString(data, 0, 4);
            if (riff != "RIFF") return info;

            // 检查 WAVE 标识
            string wave = System.Text.Encoding.ASCII.GetString(data, 8, 4);
            if (wave != "WAVE") return info;

            // 搜索 fmt 子块
            int offset = 12;
            while (offset < data.Length - 8)
            {
                string chunkId = System.Text.Encoding.ASCII.GetString(data, offset, 4);
                int chunkSize = BitConverter.ToInt32(data, offset + 4);

                if (chunkId == "fmt ")
                {
                    if (offset + 8 + 16 <= data.Length)
                    {
                        ushort audioFormat = BitConverter.ToUInt16(data, offset + 8);
                        info.Channels = BitConverter.ToUInt16(data, offset + 10);
                        info.SampleRate = BitConverter.ToInt32(data, offset + 12);
                        info.BitsPerSample = BitConverter.ToUInt16(data, offset + 22);
                        info.IsFloat = (audioFormat == 3); // 3 = IEEE Float
                    }
                    break;
                }

                offset += 8 + chunkSize;
                // 对齐到偶数偏移
                if (offset % 2 != 0) offset++;
            }

            return info;
        }

        // ══════════════════════════════════════════════════════════
        // 着色器模型推断
        // ══════════════════════════════════════════════════════════

        private static string InferShaderModel(string path)
        {
            string fileName = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
            if (fileName.Contains(".vs") || fileName.Contains("_vs") || fileName.Contains("vertex"))
                return "vs_6_0";
            if (fileName.Contains(".ps") || fileName.Contains("_ps") || fileName.Contains("pixel"))
                return "ps_6_0";
            if (fileName.Contains(".cs") || fileName.Contains("_cs") || fileName.Contains("compute"))
                return "cs_6_0";
            if (fileName.Contains(".gs") || fileName.Contains("_gs") || fileName.Contains("geometry"))
                return "gs_6_0";
            if (fileName.Contains(".hs") || fileName.Contains("_hs") || fileName.Contains("hull"))
                return "hs_6_0";
            if (fileName.Contains(".ds") || fileName.Contains("_ds") || fileName.Contains("domain"))
                return "ds_6_0";
            return "vs_6_0"; // 默认
        }

        // ══════════════════════════════════════════════════════════
        // 缓存管理
        // ══════════════════════════════════════════════════════════

        /// <summary>
        /// 强制卸载指定缓存资源。
        /// </summary>
        public bool Unload(string path)
        {
            return _cache.Remove(path);
        }

        /// <summary>
        /// 清空所有缓存资源。
        /// </summary>
        public void UnloadAll()
        {
            _cache.Clear();
        }

        // ══════════════════════════════════════════════════════════
        // IDisposable
        // ══════════════════════════════════════════════════════════

        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _workerCts.Cancel();
            _queueSignal.Release(); // 唤醒工作线程以退出
            _workerThread.Join(3000);

            _cache.Dispose();
            _workerCts.Dispose();
            _queueSignal.Dispose();
        }
    }
}
