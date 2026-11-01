// =============================================================================
// VulkanPipelineCache.cs - yama引擎 Vulkan 管线缓存系统
// =============================================================================
// vkCreateGraphicsPipelines 是个非常贵的调用，几百微秒到几毫秒一条。
// 这套缓存做几件事：
//   1. 序列化 VkPipelineCache 到磁盘，下次启动加载
//   2. 用哈希把 pipeline create info 缓存，避免重复创建
//   3. 后台异步编译 (PSO 实体化)，主线程不卡
//   4. 热加载：shader 改了之后，相关管线自动重建
//
// FIXME: 后台编译的 fence 等待逻辑还没写好，目前是同步
// TODO: 用 VK_EXT_pipeline_creation_cache_control 加 force-on 特性
//
// 作者: yama引擎团队
// =============================================================================

using Vulkan = Silk.NET.Vulkan;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Silk.NET.Vulkan;
using Semaphore = Silk.NET.Vulkan.Semaphore;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace YamaEngine.Vulkan
{
    // ════════════════════════════════════════════════════════════════════════════
    // 区域 1: 公共类型
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>管线缓存创建标志。</summary>
    [Flags]
    public enum PipelineCacheCreateFlagsExt : uint
    {
        None = 0,
        /// <summary>启动时加载磁盘缓存。</summary>
        LoadFromDisk = 1 << 0,
        /// <summary>退出时保存到磁盘。</summary>
        SaveToDisk = 1 << 1,
        /// <summary>后台异步编译。</summary>
        AsyncCompilation = 1 << 2,
        /// <summary>允许管线失败时 fallback。</summary>
        AllowFallback = 1 << 3,
        /// <summary>启用 VK_EXT_pipeline_creation_cache_control。</summary>
        UseCacheControl = 1 << 4,
    }

    /// <summary>管线状态 hash key。</summary>
    public readonly struct PipelineStateKey : IEquatable<PipelineStateKey>
    {
        public readonly ulong Hash;
        public PipelineStateKey(ulong hash) => Hash = hash;
        public bool Equals(PipelineStateKey other) => Hash == other.Hash;
        public override bool Equals(object obj) => obj is PipelineStateKey k && Equals(k);
        public override int GetHashCode() => (int)Hash;
        public static bool operator ==(PipelineStateKey a, PipelineStateKey b) => a.Hash == b.Hash;
        public static bool operator !=(PipelineStateKey a, PipelineStateKey b) => a.Hash != b.Hash;
        public override string ToString() => $"PSO@0x{Hash:X16}";
    }

    /// <summary>管线缓存统计。</summary>
    public struct PipelineCacheStats
    {
        public int CacheHits;
        public int CacheMisses;
        public int TotalCreated;
        public int CurrentlyCached;
        public int BackgroundQueueSize;
        public long TotalCompileTimeMs;
        public long SavedTimeMs;

        public float HitRate => (CacheHits + CacheMisses) == 0 ? 0 : (float)CacheHits / (CacheHits + CacheMisses);

        public override string ToString()
            => $"Pipeline Cache: hits={CacheHits}, misses={CacheMisses}, " +
               $"hit_rate={HitRate * 100:F1}%, cached={CurrentlyCached}, " +
               $"queue={BackgroundQueueSize}, total_compile={TotalCompileTimeMs}ms, " +
               $"saved={SavedTimeMs}ms";
    }

    /// <summary>管线创建请求。</summary>
    public struct PipelineCreateRequest
    {
        public PipelineStateKey Key;
        public GraphicsPipelineCreateInfo CreateInfo;
        public PipelineLayout Layout;
        public RenderPass RenderPass;
        public string DebugName;
        public int Priority;  // 数字越大优先级越高
        public long SubmittedFrame;
    }

    /// <summary>管线创建结果。</summary>
    public struct PipelineCreateResult
    {
        public Pipeline Pipeline;
        public PipelineStateKey Key;
        public bool FromCache;
        public bool Success;
        public Result VkResult;
        public long CompileTimeMs;
        public string Error;
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 2: 管线状态哈希
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 管线状态哈希器。把 graphics pipeline create info 的关键字段塞进一个 64 位 hash。
    /// 注意：Vulkan 的 CreateInfo 里有指针，不能直接 hash，得手动遍历每个字段。
    /// </summary>
    public static unsafe class PipelineStateHasher
    {
        public static PipelineStateKey ComputeHash(in GraphicsPipelineCreateInfo ci)
        {
            ulong hash = 14695981039346656037UL;
            unchecked
            {
                // stages
                if (ci.StageCount > 0 && ci.PStages != null)
                {
                    for (uint i = 0; i < ci.StageCount; i++)
                    {
                        var s = ci.PStages[i];
                        hash = Mix(hash, (ulong)s.Stage);
                        hash = Mix(hash, (ulong)s.Module.Handle);
                        hash = Mix(hash, s.PName != null ? (ulong)s.PName->GetHashCode() : 0UL);
                    }
                }

                // vertex input
                if (ci.PVertexInputState != null)
                {
                    var vi = ci.PVertexInputState;
                    for (uint i = 0; i < vi->VertexBindingDescriptionCount; i++)
                    {
                        var b = vi->PVertexBindingDescriptions[i];
                        hash = Mix(hash, b.Binding);
                        hash = Mix(hash, b.Stride);
                        hash = Mix(hash, (ulong)b.InputRate);
                    }
                    for (uint i = 0; i < vi->VertexAttributeDescriptionCount; i++)
                    {
                        var a = vi->PVertexAttributeDescriptions[i];
                        hash = Mix(hash, a.Location);
                        hash = Mix(hash, a.Binding);
                        hash = Mix(hash, (ulong)a.Format);
                        hash = Mix(hash, a.Offset);
                    }
                }

                // input assembly
                if (ci.PInputAssemblyState != null)
                {
                    hash = Mix(hash, (ulong)ci.PInputAssemblyState->Topology);
                    hash = Mix(hash, ci.PInputAssemblyState->PrimitiveRestartEnable ? 1UL : 0UL);
                }

                // rasterizer
                if (ci.PRasterizationState != null)
                {
                    var r = ci.PRasterizationState;
                    hash = Mix(hash, (ulong)r->PolygonMode);
                    hash = Mix(hash, (ulong)r->CullMode);
                    hash = Mix(hash, (ulong)r->FrontFace);
                    hash = Mix(hash, r->DepthClampEnable ? 1UL : 0UL);
                    hash = Mix(hash, r->DepthBiasEnable ? 1UL : 0UL);
                }

                // color blend
                if (ci.PColorBlendState != null)
                {
                    var cb = ci.PColorBlendState;
                    hash = Mix(hash, cb->LogicOpEnable ? 1UL : 0UL);
                    hash = Mix(hash, (ulong)cb->LogicOp);
                    for (uint i = 0; i < cb->AttachmentCount; i++)
                    {
                        var a = cb->PAttachments[i];
                        hash = Mix(hash, a.BlendEnable ? 1UL : 0UL);
                        hash = Mix(hash, (ulong)a.SrcColorBlendFactor);
                        hash = Mix(hash, (ulong)a.DstColorBlendFactor);
                        hash = Mix(hash, (ulong)a.ColorBlendOp);
                        hash = Mix(hash, (ulong)a.SrcAlphaBlendFactor);
                        hash = Mix(hash, (ulong)a.DstAlphaBlendFactor);
                        hash = Mix(hash, (ulong)a.AlphaBlendOp);
                        hash = Mix(hash, (uint)a.ColorWriteMask);
                    }
                }

                // depth stencil
                if (ci.PDepthStencilState != null)
                {
                    var ds = ci.PDepthStencilState;
                    hash = Mix(hash, ds->DepthTestEnable ? 1UL : 0UL);
                    hash = Mix(hash, ds->DepthWriteEnable ? 1UL : 0UL);
                    hash = Mix(hash, (ulong)ds->DepthCompareOp);
                    hash = Mix(hash, ds->StencilTestEnable ? 1UL : 0UL);
                }

                // dynamic state
                if (ci.PDynamicState != null)
                {
                    for (uint i = 0; i < ci.PDynamicState->DynamicStateCount; i++)
                    {
                        hash = Mix(hash, (ulong)ci.PDynamicState->PDynamicStates[i]);
                    }
                }

                // render pass / layout
                hash = Mix(hash, ci.RenderPass.Handle);
                hash = Mix(hash, ci.Layout.Handle);
                hash = Mix(hash, (ulong)ci.Subpass);
            }
            return new PipelineStateKey(hash);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ulong Mix(ulong hash, ulong value)
        {
            unchecked
            {
                hash ^= value;
                hash *= 1099511628211UL;
                return hash;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ulong Mix(ulong hash, uint value) => Mix(hash, (ulong)value);
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 3: 管线缓存条目
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>管线缓存条目。</summary>
    internal sealed class PipelineCacheEntry
    {
        public PipelineStateKey Key;
        public Pipeline Pipeline;
        public PipelineLayout Layout;
        public RenderPass RenderPass;
        public string DebugName;
        public DateTime CreatedAt;
        public long LastUsedFrame;
        public int UseCount;
        public bool IsCompiling;
        public Task<PipelineCreateResult> CompilationTask;
        public long CompileTimeMs;
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 4: 后台编译队列
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 后台编译队列。优先级队列 + worker 线程。
    /// </summary>
    internal sealed class BackgroundCompileQueue
    {
        private readonly Vk _vk;
        private readonly Device _device;
        private readonly PipelineCache _vkCache;
        private readonly PriorityQueue<PipelineCreateRequest, int> _queue = new();
        private readonly object _lock = new();
        private readonly SemaphoreSlim _signal = new(0);
        private readonly CancellationTokenSource _cts = new();
        private readonly Thread[] _workers;
        private readonly int _workerCount;
        private bool _stopped;

        public int QueueSize
        {
            get { lock (_lock) return _queue.Count; }
        }

        public BackgroundCompileQueue(Vk vk, Device device, PipelineCache cache, int workerCount = 2)
        {
            _vk = vk;
            _device = device;
            _vkCache = cache;
            _workerCount = workerCount;
            _workers = new Thread[workerCount];
            for (int i = 0; i < workerCount; i++)
            {
                _workers[i] = new Thread(WorkerLoop)
                {
                    Name = $"PipelineCompileWorker-{i}",
                    IsBackground = true,
                };
                _workers[i].Start();
            }
        }

        public void Enqueue(PipelineCreateRequest req)
        {
            lock (_lock)
            {
                _queue.Enqueue(req, -req.Priority);  // 优先级队列：priority 大的先出
            }
            _signal.Release();
        }

        public bool TryDequeue(out PipelineCreateRequest req)
        {
            lock (_lock)
            {
                if (_queue.Count == 0) { req = default; return false; }
                req = _queue.Dequeue();
                return true;
            }
        }

        private void WorkerLoop()
        {
            while (!_stopped)
            {
                _signal.Wait(_cts.Token);
                if (_stopped) break;

                if (TryDequeue(out var req))
                {
                    // 实际编译由 OnCompiled 回调处理
                    OnCompileRequested?.Invoke(req);
                }
            }
        }

        public event Action<PipelineCreateRequest> OnCompileRequested;

        public void Stop()
        {
            _stopped = true;
            _cts.Cancel();
            _signal.Release(_workerCount);
            foreach (var w in _workers) w.Join(1000);
        }

        public void Dispose()
        {
            Stop();
            _cts.Dispose();
            _signal.Dispose();
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 5: 磁盘缓存
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 磁盘缓存。VkPipelineCache 可以序列化到文件，下次启动加载。
    /// 这个能省 50% 以上的管线编译时间，必备。
    /// </summary>
    internal sealed unsafe class DiskPipelineCache
    {
        private readonly Vk _vk;
        private readonly Device _device;
        private readonly string _cachePath;
        private PipelineCache _vkCache;
        private byte[] _initialData;

        public PipelineCache VkCache => _vkCache;
        public string CachePath => _cachePath;

        public DiskPipelineCache(Vk vk, Device device, string cachePath)
        {
            _vk = vk;
            _device = device;
            _cachePath = cachePath;
        }

        /// <summary>加载磁盘缓存到内存。</summary>
        public bool LoadFromDisk()
        {
            if (!File.Exists(_cachePath)) return false;
            try
            {
                _initialData = File.ReadAllBytes(_cachePath);
                Console.WriteLine($"[PipelineCache] 从 {_cachePath} 加载 {_initialData.Length} 字节");
                return true;
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"[PipelineCache] 加载失败: {e.Message}");
                return false;
            }
        }

        /// <summary>创建 VkPipelineCache (带初始数据)。</summary>
        public PipelineCache CreateVkCache()
        {
            fixed (byte* pInitial = _initialData)
            {
                var info = new PipelineCacheCreateInfo
                {
                    SType = StructureType.PipelineCacheCreateInfo,
                    InitialDataSize = _initialData != null ? (nuint)_initialData.Length : 0,
                    PInitialData = pInitial,
                };

                PipelineCache cache;
                var r = _vk.CreatePipelineCache(_device, info, null, &cache);
                if (r != Result.Success)
                    throw new VulkanException(r, "CreatePipelineCache 失败");
                _vkCache = cache;
                return cache;
            }
        }

        /// <summary>保存 VkPipelineCache 到磁盘。</summary>
        public void SaveToDisk()
        {
            if (_vkCache.Handle == 0) return;
            nuint size = 0;
            var r = _vk.GetPipelineCacheData(_device, _vkCache, &size, null);
            if (r != Result.Success)
            {
                Console.Error.WriteLine($"[PipelineCache] GetPipelineCacheData (size) 失败: {r}");
                return;
            }

            var data = new byte[(int)size];
            fixed (byte* pData = data)
            {
                r = _vk.GetPipelineCacheData(_device, _vkCache, &size, pData);
                if (r != Result.Success)
                {
                    Console.Error.WriteLine($"[PipelineCache] GetPipelineCacheData 失败: {r}");
                    return;
                }
            }

            try
            {
                var dir = Path.GetDirectoryName(_cachePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                File.WriteAllBytes(_cachePath, data);
                Console.WriteLine($"[PipelineCache] 保存 {data.Length} 字节到 {_cachePath}");
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"[PipelineCache] 保存失败: {e.Message}");
            }
        }

        /// <summary>合并其他 VkPipelineCache 的数据。</summary>
        public void MergeCacheData(PipelineCache[] others)
        {
            if (_vkCache.Handle == 0 || others.Length == 0) return;
            fixed (PipelineCache* pOthers = others)
            {
                _vk.MergePipelineCaches(_device, _vkCache, (uint)others.Length, pOthers);
            }
        }

        /// <summary>销毁 VkPipelineCache。</summary>
        public void Destroy()
        {
            if (_vkCache.Handle != 0)
            {
                _vk.DestroyPipelineCache(_device, _vkCache, null);
                _vkCache = default;
            }
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 6: 管线缓存主类
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 管线缓存系统主类。
    /// </summary>
    public sealed unsafe class VulkanPipelineCache : IDisposable
    {
        private readonly Vk _vk;
        private readonly Device _device;
        private readonly PipelineCacheCreateFlagsExt _flags;
        private readonly DiskPipelineCache _diskCache;
        private readonly Dictionary<PipelineStateKey, PipelineCacheEntry> _cache = new();
        private readonly object _cacheLock = new();
        private readonly BackgroundCompileQueue _bgQueue;
        private PipelineCacheStats _stats;
        private long _currentFrame;

        // 等待编译的 fallback pipeline (用最低精度 shader 编的临时管线)
        private readonly Dictionary<PipelineStateKey, Pipeline> _fallbackPipelines = new();

        public PipelineCache VkCache => _diskCache?.VkCache ?? default;
        public ref PipelineCacheStats Stats => ref _stats;

        public VulkanPipelineCache(Vk vk, Device device, string cacheDir,
                                   PipelineCacheCreateFlagsExt flags =
                                       PipelineCacheCreateFlagsExt.LoadFromDisk |
                                       PipelineCacheCreateFlagsExt.SaveToDisk |
                                       PipelineCacheCreateFlagsExt.AsyncCompilation)
        {
            _vk = vk;
            _device = device;
            _flags = flags;
            _stats = new PipelineCacheStats();

            string cachePath = Path.Combine(cacheDir, "pipeline.cache");
            _diskCache = new DiskPipelineCache(vk, device, cachePath);

            if ((flags & PipelineCacheCreateFlagsExt.LoadFromDisk) != 0)
            {
                _diskCache.LoadFromDisk();
            }
            _diskCache.CreateVkCache();

            if ((flags & PipelineCacheCreateFlagsExt.AsyncCompilation) != 0)
            {
                _bgQueue = new BackgroundCompileQueue(vk, device, _diskCache.VkCache);
                _bgQueue.OnCompileRequested += OnBackgroundCompileRequested;
            }
        }

        /// <summary>同步创建/获取管线。命中缓存秒返回，没命中现场编译。</summary>
        public PipelineCreateResult GetOrCreate(ref GraphicsPipelineCreateInfo ci, string debugName = null)
        {
            var key = PipelineStateHasher.ComputeHash(ci);

            lock (_cacheLock)
            {
                if (_cache.TryGetValue(key, out var entry))
                {
                    if (entry.Pipeline.Handle != 0)
                    {
                        _stats.CacheHits++;
                        entry.UseCount++;
                        entry.LastUsedFrame = _currentFrame;
                        return new PipelineCreateResult
                        {
                            Pipeline = entry.Pipeline,
                            Key = key,
                            FromCache = true,
                            Success = true,
                        };
                    }
                    // 正在编译中，等待
                    if (entry.IsCompiling && entry.CompilationTask != null)
                    {
                        // 这里直接等待，因为同步接口要求拿到管线才能画
                        var result = entry.CompilationTask.Result;
                        return result;
                    }
                }
            }

            // 没命中，编译
            _stats.CacheMisses++;
            var sw = Stopwatch.StartNew();
            Pipeline pipeline;
            fixed (GraphicsPipelineCreateInfo* pCi = &ci)
            {
                var r = _vk.CreateGraphicsPipelines(_device, _diskCache.VkCache, 1, pCi, null, &pipeline);
                if (r != Result.Success)
                {
                    return new PipelineCreateResult
                    {
                        Key = key,
                        Success = false,
                        VkResult = r,
                        Error = $"CreateGraphicsPipelines 失败: {r}",
                    };
                }
            }
            sw.Stop();
            _stats.TotalCompileTimeMs += sw.ElapsedMilliseconds;

            // 缓存
            var newEntry = new PipelineCacheEntry
            {
                Key = key,
                Pipeline = pipeline,
                DebugName = debugName,
                CreatedAt = DateTime.UtcNow,
                LastUsedFrame = _currentFrame,
                CompileTimeMs = sw.ElapsedMilliseconds,
            };
            lock (_cacheLock)
            {
                _cache[key] = newEntry;
                _stats.CurrentlyCached = _cache.Count;
            }
            _stats.TotalCreated++;

            return new PipelineCreateResult
            {
                Pipeline = pipeline,
                Key = key,
                FromCache = false,
                Success = true,
                CompileTimeMs = sw.ElapsedMilliseconds,
            };
        }

        /// <summary>异步创建管线 (提交到后台队列)。立即返回，调用方需要轮询 IsReady。</summary>
        public PipelineStateKey CreateAsync(ref GraphicsPipelineCreateInfo ci, string debugName = null,
                                            int priority = 0)
        {
            var key = PipelineStateHasher.ComputeHash(ci);

            lock (_cacheLock)
            {
                if (_cache.TryGetValue(key, out var existing))
                {
                    return key;  // 已有或正在编译
                }

                var entry = new PipelineCacheEntry
                {
                    Key = key,
                    DebugName = debugName,
                    IsCompiling = true,
                };
                _cache[key] = entry;
            }

            var req = new PipelineCreateRequest
            {
                Key = key,
                CreateInfo = ci,
                DebugName = debugName,
                Priority = priority,
                SubmittedFrame = _currentFrame,
            };

            _bgQueue?.Enqueue(req);
            _stats.BackgroundQueueSize = _bgQueue?.QueueSize ?? 0;

            return key;
        }

        /// <summary>查询某个 key 的管线是否已就绪。</summary>
        public bool IsReady(PipelineStateKey key)
        {
            lock (_cacheLock)
            {
                if (_cache.TryGetValue(key, out var entry))
                {
                    return !entry.IsCompiling && entry.Pipeline.Handle != 0;
                }
                return false;
            }
        }

        /// <summary>获取已就绪的管线。未就绪返回 default。</summary>
        public Pipeline GetPipeline(PipelineStateKey key)
        {
            lock (_cacheLock)
            {
                if (_cache.TryGetValue(key, out var entry))
                {
                    if (!entry.IsCompiling && entry.Pipeline.Handle != 0)
                    {
                        entry.UseCount++;
                        entry.LastUsedFrame = _currentFrame;
                        return entry.Pipeline;
                    }
                }
                return default;
            }
        }

        /// <summary>获取管线 (未就绪时返回 fallback)。</summary>
        public Pipeline GetPipelineWithFallback(PipelineStateKey key, Pipeline fallback)
        {
            lock (_cacheLock)
            {
                if (_cache.TryGetValue(key, out var entry) && !entry.IsCompiling && entry.Pipeline.Handle != 0)
                {
                    return entry.Pipeline;
                }
            }
            return fallback;
        }

        /// <summary>等待管线编译完成。</summary>
        public PipelineCreateResult WaitAndGet(PipelineStateKey key, TimeSpan timeout)
        {
            lock (_cacheLock)
            {
                if (_cache.TryGetValue(key, out var entry))
                {
                    if (entry.IsCompiling && entry.CompilationTask != null)
                    {
                        if (entry.CompilationTask.Wait(timeout))
                        {
                            return entry.CompilationTask.Result;
                        }
                    }
                    else if (entry.Pipeline.Handle != 0)
                    {
                        return new PipelineCreateResult
                        {
                            Pipeline = entry.Pipeline,
                            Key = key,
                            FromCache = true,
                            Success = true,
                        };
                    }
                }
            }
            return new PipelineCreateResult { Key = key, Success = false, Error = "Timeout" };
        }

        private unsafe void OnBackgroundCompileRequested(PipelineCreateRequest req)
        {
            // 后台线程编译
            var task = Task.Run(() =>
            {
                var sw = Stopwatch.StartNew();
                Pipeline pipeline;
                // 复制到 lambda 局部变量，避免对已捕获的 req 直接取地址 (CS1686 / CS0213)
                var localCi = req.CreateInfo;
                GraphicsPipelineCreateInfo* pCi = &localCi;
                {
                    var r = _vk.CreateGraphicsPipelines(_device, _diskCache.VkCache, 1, pCi, null, &pipeline);
                    sw.Stop();
                    if (r != Result.Success)
                    {
                        return new PipelineCreateResult
                        {
                            Key = req.Key,
                            Success = false,
                            VkResult = r,
                            Error = $"BG CreateGraphicsPipelines 失败: {r}",
                        };
                    }
                }

                lock (_cacheLock)
                {
                    if (_cache.TryGetValue(req.Key, out var entry))
                    {
                        entry.Pipeline = pipeline;
                        entry.IsCompiling = false;
                        entry.CompileTimeMs = sw.ElapsedMilliseconds;
                    }
                }

                Interlocked.Increment(ref _stats.TotalCreated);
                Interlocked.Add(ref _stats.TotalCompileTimeMs, sw.ElapsedMilliseconds);

                return new PipelineCreateResult
                {
                    Pipeline = pipeline,
                    Key = req.Key,
                    FromCache = false,
                    Success = true,
                    CompileTimeMs = sw.ElapsedMilliseconds,
                };
            });

            lock (_cacheLock)
            {
                if (_cache.TryGetValue(req.Key, out var entry))
                {
                    entry.CompilationTask = task;
                }
            }
        }

        /// <summary>每帧调用，更新帧号 + 清理长时间不用的管线。</summary>
        public void TickFrame(long frameIndex)
        {
            _currentFrame = frameIndex;
            _stats.BackgroundQueueSize = _bgQueue?.QueueSize ?? 0;
        }

        /// <summary>清理长时间不用的管线。</summary>
        public void EvictUnused(long maxIdleFrames)
        {
            lock (_cacheLock)
            {
                var toRemove = new List<PipelineStateKey>();
                foreach (var kv in _cache)
                {
                    if (kv.Value.IsCompiling) continue;
                    if (_currentFrame - kv.Value.LastUsedFrame > maxIdleFrames)
                    {
                        toRemove.Add(kv.Key);
                    }
                }
                foreach (var k in toRemove)
                {
                    if (_cache.TryGetValue(k, out var entry) && entry.Pipeline.Handle != 0)
                    {
                        _vk.DestroyPipeline(_device, entry.Pipeline, null);
                    }
                    _cache.Remove(k);
                }
                _stats.CurrentlyCached = _cache.Count;
            }
        }

        /// <summary>让某个 key 失效 (shader 改了之后调用)。</summary>
        public void Invalidate(PipelineStateKey key)
        {
            lock (_cacheLock)
            {
                if (_cache.TryGetValue(key, out var entry))
                {
                    if (entry.Pipeline.Handle != 0)
                    {
                        _vk.DestroyPipeline(_device, entry.Pipeline, null);
                    }
                    _cache.Remove(key);
                }
            }
        }

        /// <summary>按 shader module 失效所有用它的管线。</summary>
        public void InvalidateByShaderModule(ShaderModule module)
        {
            lock (_cacheLock)
            {
                // 这个需要遍历所有 cache 条目的 CreateInfo，比对 shader module
                // 因为 CreateInfo 已经在 cache 里被释放，这个操作实际做不了
                // 实际工程里应该在 entry 里存 shader module 列表
                // TODO: 在 entry 里记录依赖的 shader modules
            }
        }

        /// <summary>保存磁盘缓存。退出前调用。</summary>
        public void SaveDiskCache()
        {
            if ((_flags & PipelineCacheCreateFlagsExt.SaveToDisk) != 0)
            {
                _diskCache?.SaveToDisk();
            }
        }

        /// <summary>获取统计。</summary>
        public PipelineCacheStats GetStats() => _stats;

        public void Dispose()
        {
            SaveDiskCache();

            _bgQueue?.Dispose();

            lock (_cacheLock)
            {
                foreach (var kv in _cache)
                {
                    if (kv.Value.Pipeline.Handle != 0)
                        _vk.DestroyPipeline(_device, kv.Value.Pipeline, null);
                }
                _cache.Clear();
            }

            foreach (var p in _fallbackPipelines.Values)
                _vk.DestroyPipeline(_device, p, null);
            _fallbackPipelines.Clear();

            _diskCache?.Destroy();
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 7: compute pipeline 缓存
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>compute pipeline 缓存。和 graphics 类似但用 ComputePipelineCreateInfo。</summary>
    public sealed unsafe class VulkanComputePipelineCache : IDisposable
    {
        private readonly Vk _vk;
        private readonly Device _device;
        private readonly PipelineCache _vkCache;
        private readonly Dictionary<ulong, Pipeline> _cache = new();
        private readonly object _lock = new();
        private int _hits, _misses;

        public VulkanComputePipelineCache(Vk vk, Device device, PipelineCache vkCache)
        {
            _vk = vk;
            _device = device;
            _vkCache = vkCache;
        }

        public Pipeline GetOrCreate(ref ComputePipelineCreateInfo ci)
        {
            ulong hash = ComputeHash(ref ci);
            lock (_lock)
            {
                if (_cache.TryGetValue(hash, out var p))
                {
                    _hits++;
                    return p;
                }
            }

            _misses++;
            Pipeline pipeline;
            fixed (ComputePipelineCreateInfo* pCi = &ci)
            {
                var r = _vk.CreateComputePipelines(_device, _vkCache, 1, pCi, null, &pipeline);
                if (r != Result.Success)
                    throw new VulkanException(r, "CreateComputePipelines 失败");
            }
            lock (_lock) _cache[hash] = pipeline;
            return pipeline;
        }

        private static ulong ComputeHash(ref ComputePipelineCreateInfo ci)
        {
            ulong hash = 14695981039346656037UL;
            unchecked
            {
                hash ^= ci.Stage.Module.Handle;
                hash *= 1099511628211UL;
                hash ^= (ulong)ci.Stage.Stage;
                hash *= 1099511628211UL;
                hash ^= ci.Layout.Handle;
                hash *= 1099511628211UL;
            }
            return hash;
        }

        public (int hits, int misses) GetStats() => (_hits, _misses);

        public void Dispose()
        {
            lock (_lock)
            {
                foreach (var p in _cache.Values)
                    _vk.DestroyPipeline(_device, p, null);
                _cache.Clear();
            }
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 8: 管线状态 builder
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>Graphics pipeline state builder。简化 CreateInfo 的构建。</summary>
    public sealed unsafe class GraphicsPipelineStateBuilder
    {
        private readonly List<PipelineShaderStageCreateInfo> _stages = new();
        private readonly List<VertexInputBindingDescription> _vertexBindings = new();
        private readonly List<VertexInputAttributeDescription> _vertexAttrs = new();
        private readonly List<PipelineColorBlendAttachmentState> _blendAttachments = new();
        private readonly List<DynamicState> _dynamicStates = new();

        private PipelineInputAssemblyStateCreateInfo _inputAssembly;
        private PipelineRasterizationStateCreateInfo _rasterizer;
        private PipelineMultisampleStateCreateInfo _multisample;
        private PipelineDepthStencilStateCreateInfo _depthStencil;
        private PipelineColorBlendStateCreateInfo _colorBlend;
        private PipelineViewportStateCreateInfo _viewport;
        private PipelineDynamicStateCreateInfo _dynamicState;
        private PipelineLayout _layout;
        private RenderPass _renderPass;
        private uint _subpass;

        public GraphicsPipelineStateBuilder()
        {
            _inputAssembly = new PipelineInputAssemblyStateCreateInfo
            {
                SType = StructureType.PipelineInputAssemblyStateCreateInfo,
                Topology = PrimitiveTopology.TriangleList,
            };
            _rasterizer = new PipelineRasterizationStateCreateInfo
            {
                SType = StructureType.PipelineRasterizationStateCreateInfo,
                PolygonMode = PolygonMode.Fill,
                CullMode = CullModeFlags.BackBit,
                FrontFace = FrontFace.CounterClockwise,
                LineWidth = 1f,
            };
            _multisample = new PipelineMultisampleStateCreateInfo
            {
                SType = StructureType.PipelineMultisampleStateCreateInfo,
                RasterizationSamples = SampleCountFlags.Count1Bit,
            };
            _depthStencil = new PipelineDepthStencilStateCreateInfo
            {
                SType = StructureType.PipelineDepthStencilStateCreateInfo,
            };
            _colorBlend = new PipelineColorBlendStateCreateInfo
            {
                SType = StructureType.PipelineColorBlendStateCreateInfo,
            };
            _viewport = new PipelineViewportStateCreateInfo
            {
                SType = StructureType.PipelineViewportStateCreateInfo,
            };
        }

        public GraphicsPipelineStateBuilder AddShaderStage(ShaderModule module, ShaderStageFlags stage, string entry = "main")
        {
            _stages.Add(new PipelineShaderStageCreateInfo
            {
                SType = StructureType.PipelineShaderStageCreateInfo,
                Stage = stage,
                Module = module,
                PName = (byte*)Marshal.StringToHGlobalAnsi(entry),
            });
            return this;
        }

        public GraphicsPipelineStateBuilder SetTopology(PrimitiveTopology topo)
        {
            _inputAssembly.Topology = topo;
            return this;
        }

        public GraphicsPipelineStateBuilder SetCullMode(CullModeFlags mode)
        {
            _rasterizer.CullMode = mode;
            return this;
        }

        public GraphicsPipelineStateBuilder SetPolygonMode(PolygonMode mode)
        {
            _rasterizer.PolygonMode = mode;
            return this;
        }

        public GraphicsPipelineStateBuilder SetDepthTest(bool enable, bool write, CompareOp op = CompareOp.Less)
        {
            _depthStencil.DepthTestEnable = enable;
            _depthStencil.DepthWriteEnable = write;
            _depthStencil.DepthCompareOp = op;
            return this;
        }

        public GraphicsPipelineStateBuilder AddBlendAttachment(bool enable = false,
            BlendFactor srcColor = BlendFactor.SrcAlpha, BlendFactor dstColor = BlendFactor.OneMinusSrcAlpha,
            BlendOp colorOp = BlendOp.Add,
            BlendFactor srcAlpha = BlendFactor.One, BlendFactor dstAlpha = BlendFactor.Zero,
            BlendOp alphaOp = BlendOp.Add,
            ColorComponentFlags writeMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit |
                                            ColorComponentFlags.BBit | ColorComponentFlags.ABit)
        {
            _blendAttachments.Add(new PipelineColorBlendAttachmentState
            {
                BlendEnable = enable,
                SrcColorBlendFactor = srcColor,
                DstColorBlendFactor = dstColor,
                ColorBlendOp = colorOp,
                SrcAlphaBlendFactor = srcAlpha,
                DstAlphaBlendFactor = dstAlpha,
                AlphaBlendOp = alphaOp,
                ColorWriteMask = writeMask,
            });
            return this;
        }

        public GraphicsPipelineStateBuilder AddVertexBinding(uint binding, uint stride,
            VertexInputRate rate = VertexInputRate.Vertex)
        {
            _vertexBindings.Add(new VertexInputBindingDescription
            {
                Binding = binding,
                Stride = stride,
                InputRate = rate,
            });
            return this;
        }

        public GraphicsPipelineStateBuilder AddVertexAttribute(uint location, uint binding, Format format, uint offset)
        {
            _vertexAttrs.Add(new VertexInputAttributeDescription
            {
                Location = location,
                Binding = binding,
                Format = format,
                Offset = offset,
            });
            return this;
        }

        public GraphicsPipelineStateBuilder AddDynamicState(DynamicState state)
        {
            _dynamicStates.Add(state);
            return this;
        }

        public GraphicsPipelineStateBuilder SetLayout(PipelineLayout layout)
        {
            _layout = layout;
            return this;
        }

        public GraphicsPipelineStateBuilder SetRenderPass(RenderPass rp, uint subpass = 0)
        {
            _renderPass = rp;
            _subpass = subpass;
            return this;
        }

        /// <summary>构建 GraphicsPipelineCreateInfo。注意返回的 struct 里指针指向 builder 内部，builder 不能提前释放。</summary>
        public GraphicsPipelineCreateInfo Build()
        {
            var vertexInput = new PipelineVertexInputStateCreateInfo
            {
                SType = StructureType.PipelineVertexInputStateCreateInfo,
                VertexBindingDescriptionCount = (uint)_vertexBindings.Count,
                VertexAttributeDescriptionCount = (uint)_vertexAttrs.Count,
            };

            // 这里 hack：直接返回 create info，但指针指向临时数组
            // 真实实现需要把数组存在 builder 里并保持稳定
            // TODO: 改成存储 + pin。这里用多个 fixed 把字段固定，避免 CS0212。
            fixed (VertexInputBindingDescription* pVB = _vertexBindings.ToArray())
            fixed (VertexInputAttributeDescription* pVA = _vertexAttrs.ToArray())
            fixed (PipelineColorBlendAttachmentState* pAttach = _blendAttachments.ToArray())
            fixed (PipelineShaderStageCreateInfo* pStages = _stages.ToArray())
            fixed (DynamicState* pDyn = _dynamicStates.ToArray())
            fixed (PipelineInputAssemblyStateCreateInfo* pIA = &_inputAssembly)
            fixed (PipelineRasterizationStateCreateInfo* pRS = &_rasterizer)
            fixed (PipelineMultisampleStateCreateInfo* pMS = &_multisample)
            fixed (PipelineDepthStencilStateCreateInfo* pDS = &_depthStencil)
            fixed (PipelineColorBlendStateCreateInfo* pCB = &_colorBlend)
            fixed (PipelineViewportStateCreateInfo* pVS = &_viewport)
            fixed (PipelineDynamicStateCreateInfo* pDynState = &_dynamicState)
            {
                vertexInput.PVertexBindingDescriptions = _vertexBindings.Count > 0 ? pVB : null;
                vertexInput.PVertexAttributeDescriptions = _vertexAttrs.Count > 0 ? pVA : null;
                _colorBlend.AttachmentCount = (uint)_blendAttachments.Count;
                _colorBlend.PAttachments = pAttach;
                _dynamicState = new PipelineDynamicStateCreateInfo
                {
                    SType = StructureType.PipelineDynamicStateCreateInfo,
                    DynamicStateCount = (uint)_dynamicStates.Count,
                    PDynamicStates = pDyn,
                };

                return new GraphicsPipelineCreateInfo
                {
                    SType = StructureType.GraphicsPipelineCreateInfo,
                    StageCount = (uint)_stages.Count,
                    PStages = pStages,
                    PVertexInputState = &vertexInput,
                    PInputAssemblyState = pIA,
                    PRasterizationState = pRS,
                    PMultisampleState = pMS,
                    PDepthStencilState = pDS,
                    PColorBlendState = pCB,
                    PViewportState = pVS,
                    PDynamicState = _dynamicStates.Count > 0 ? pDynState : null,
                    Layout = _layout,
                    RenderPass = _renderPass,
                    Subpass = _subpass,
                };
            }
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 9: 管线预热器
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 管线预热器。引擎启动时从配置文件加载所有 PSO，提前编译。
    /// </summary>
    public sealed class PipelinePreheater
    {
        private readonly VulkanPipelineCache _cache;

        public PipelinePreheater(VulkanPipelineCache cache)
        {
            _cache = cache;
        }

        /// <summary>从文件加载 PSO 描述并提交编译。</summary>
        public int PreheatFromFile(string path)
        {
            // 实际实现里会解析 JSON/二进制描述，转成 CreateInfo 然后调 CreateAsync
            // 这里只给骨架
            Console.WriteLine($"[PipelinePreheat] 从 {path} 预热");
            return 0;
        }

        /// <summary>等待所有后台编译完成。</summary>
        public bool WaitAll(TimeSpan timeout)
        {
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < timeout)
            {
                if (_cache.GetStats().BackgroundQueueSize == 0)
                    return true;
                Thread.Sleep(10);
            }
            return false;
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 10: 管线缓存监控
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>管线缓存监控。</summary>
    public sealed class PipelineCacheMonitor : IDisposable
    {
        private readonly VulkanPipelineCache _cache;
        private System.Threading.Timer _timer;
        private readonly object _lock = new();
        private readonly List<PipelineCacheStats> _history = new();
        private const int MaxHistory = 60;
        private bool _disposed;

        public event Action<PipelineCacheStats> OnStatsUpdated;

        public PipelineCacheMonitor(VulkanPipelineCache cache, TimeSpan? interval = null)
        {
            _cache = cache;
            _timer = new System.Threading.Timer(_ => Tick(), null,
                interval ?? TimeSpan.FromSeconds(1),
                interval ?? TimeSpan.FromSeconds(1));
        }

        private void Tick()
        {
            if (_disposed) return;
            var s = _cache.GetStats();
            lock (_lock)
            {
                _history.Add(s);
                if (_history.Count > MaxHistory) _history.RemoveAt(0);
            }
            OnStatsUpdated?.Invoke(s);
        }

        public PipelineCacheStats[] GetHistory()
        {
            lock (_lock) return _history.ToArray();
        }

        public void Dispose()
        {
            _disposed = true;
            _timer?.Dispose();
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 11: 管线缓存版本信息
    // ════════════════════════════════════════════════════════════════════════════

    public static class PipelineCacheVersion
    {
        public const string Version = "1.0.0";
        public const string Description = "YamaEngine Vulkan Pipeline Cache (with disk serialization + async compilation)";
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 12: 管线缓存使用示例
    // ════════════════════════════════════════════════════════════════════════════

    /*
    // === 使用示例 ===
    //
    // // 创建缓存
    // var cache = new VulkanPipelineCache(vk, device, "Cache/");
    //
    // // 构建 PSO
    // var builder = new GraphicsPipelineStateBuilder()
    //     .AddShaderStage(vertModule, ShaderStageFlags.VertexBit)
    //     .AddShaderStage(fragModule, ShaderStageFlags.FragmentBit)
    //     .AddVertexBinding(0, 32)
    //     .AddVertexAttribute(0, 0, Format.R32G32B32Sfloat, 0)
    //     .AddVertexAttribute(1, 0, Format.R32G32Sfloat, 12)
    //     .SetCullMode(CullModeFlags.BackBit)
    //     .SetDepthTest(true, true)
    //     .AddBlendAttachment()
    //     .SetLayout(layout)
    //     .SetRenderPass(rp);
    //
    // var ci = builder.Build();
    // var result = cache.GetOrCreate(ref ci, "MeshPSO");
    // if (result.Success) vkCmdBindPipeline(cmd, PipelineBindPoint.Graphics, result.Pipeline);
    //
    // // 异步创建
    // var key = cache.CreateAsync(ref ci, "MeshPSO");
    // // ... 后面轮询
    // if (cache.IsReady(key)) {
    //     var pipe = cache.GetPipeline(key);
    // }
    //
    // // 退出
    // cache.Dispose();  // 自动保存磁盘
    */

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 13: shader module 缓存 (管线缓存依赖)
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>ShaderModule 缓存。相同的 SPIR-V 只创建一次 module。</summary>
    public sealed unsafe class ShaderModuleCache : IDisposable
    {
        private readonly Vk _vk;
        private readonly Device _device;
        private readonly Dictionary<ulong, ShaderModule> _cache = new();
        private readonly object _lock = new();

        public ShaderModuleCache(Vk vk, Device device)
        {
            _vk = vk;
            _device = device;
        }

        public ShaderModule GetOrCreate(byte[] spirv)
        {
            ulong hash = ComputeHash(spirv);
            lock (_lock)
            {
                if (_cache.TryGetValue(hash, out var m)) return m;
            }

            fixed (byte* pCode = spirv)
            {
                var info = new ShaderModuleCreateInfo
                {
                    SType = StructureType.ShaderModuleCreateInfo,
                    CodeSize = (nuint)spirv.Length,
                    PCode = (uint*)pCode,
                };

                ShaderModule mod;
                var r = _vk.CreateShaderModule(_device, info, null, &mod);
                if (r != Result.Success)
                    throw new VulkanException(r, "CreateShaderModule 失败");
                lock (_lock) _cache[hash] = mod;
                return mod;
            }
        }

        private static ulong ComputeHash(byte[] data)
        {
            // 用 xxHash 简化版
            ulong hash = 14695981039346656037UL;
            unchecked
            {
                foreach (var b in data)
                {
                    hash ^= b;
                    hash *= 1099511628211UL;
                }
            }
            return hash;
        }

        public void Dispose()
        {
            lock (_lock)
            {
                foreach (var m in _cache.Values)
                    _vk.DestroyShaderModule(_device, m, null);
                _cache.Clear();
            }
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 14: 管线缓存统计导出
    // ════════════════════════════════════════════════════════════════════════════

    public static class PipelineCacheStatsExporter
    {
        public static string ToJson(in PipelineCacheStats s)
        {
            return $@"{{
  ""hits"": {s.CacheHits},
  ""misses"": {s.CacheMisses},
  ""hit_rate"": {s.HitRate:F4},
  ""total_created"": {s.TotalCreated},
  ""currently_cached"": {s.CurrentlyCached},
  ""bg_queue"": {s.BackgroundQueueSize},
  ""total_compile_ms"": {s.TotalCompileTimeMs},
  ""saved_ms"": {s.SavedTimeMs}
}}";
        }

        public static string ToHumanReadable(in PipelineCacheStats s) => s.ToString();
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 15: 单元测试桩
    // ════════════════════════════════════════════════════════════════════════════

    public static class PipelineCacheTestStub
    {
        public static ulong TestHash(uint a, uint b, uint c)
        {
            ulong hash = 14695981039346656037UL;
            unchecked
            {
                hash ^= a; hash *= 1099511628211UL;
                hash ^= b; hash *= 1099511628211UL;
                hash ^= c; hash *= 1099511628211UL;
            }
            return hash;
        }

        public static bool TestHitRate(int hits, int misses, float expected, float eps = 0.01f)
        {
            int total = hits + misses;
            if (total == 0) return expected == 0;
            float actual = (float)hits / total;
            return Math.Abs(actual - expected) < eps;
        }
    }
}

// 文件结束 - VulkanPipelineCache.cs
