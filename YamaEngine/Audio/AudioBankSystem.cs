using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using YamaEngine.Assets.Importers;

namespace YamaEngine.Audio
{
    // ════════════════════════════════════════════════════════════════════════
    // yama 音频资源库系统 (Audio Bank System)
    //
    // 类似 FMOD Sound Bank / Wwise SoundBank。
    // 音频资源库管理、加载/卸载、内存池、淘汰策略、
    // 声音定义、音频虚拟化。
    // ════════════════════════════════════════════════════════════════════════

    // ──────────────────────────────────────────────────────────────────────
    //  枚举
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>音频加载类型。</summary>
    public enum AudioLoadType
    {
        /// <summary>一次性加载到内存。</summary>
        Memory,
        /// <summary>流式播放 (从磁盘/网络)。</summary>
        Stream,
        /// <summary>运行时解码到内存。</summary>
        Decode,
        /// <summary>手动加载 (用户控制)。</summary>
        Manual,
        /// <summary>流式 (同 Stream)。</summary>
        Streaming
    }

    /// <summary>资源库加载状态。</summary>
    public enum AudioBankLoadState
    {
        Unloaded,
        Loading,
        Loaded,
        Unloading,
        Failed
    }

    /// <summary>内存池类型。</summary>
    public enum MemoryPoolType
    {
        /// <summary>固定大小池。</summary>
        Fixed,
        /// <summary>动态增长池。</summary>
        Dynamic
    }

    /// <summary>淘汰策略。</summary>
    public enum EvictionPolicy
    {
        /// <summary>最近最少使用。</summary>
        LRU,
        /// <summary>最不常使用。</summary>
        LFU,
        /// <summary>先进先出。</summary>
        FIFO,
        /// <summary>最低优先级优先淘汰。</summary>
        LowestPriority
    }

    // ──────────────────────────────────────────────────────────────────────
    //  SoundDefinition — 声音定义
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>声音定义 (描述一个音频资源如何加载和播放)。</summary>
    public sealed class SoundDefinition
    {
        /// <summary>声音名称。</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>声音 ID。</summary>
        public string SoundId { get; set; } = string.Empty;

        /// <summary>音频文件路径。</summary>
        public string FilePath { get; set; } = string.Empty;

        /// <summary>加载类型。</summary>
        public AudioLoadType LoadType { get; set; } = AudioLoadType.Memory;

        /// <summary>音频格式。</summary>
        public AudioFormat Format { get; set; } = AudioFormat.WAV;

        /// <summary>内存池 ID (0 = 默认池)。</summary>
        public int MemoryPoolId { get; set; }

        /// <summary>优先级 (用于虚拟化决策)。</summary>
        public int Priority { get; set; } = 128;

        /// <summary>是否流式。</summary>
        public bool IsStreaming => LoadType == AudioLoadType.Stream || LoadType == AudioLoadType.Streaming;

        /// <summary>目标采样率。</summary>
        public int TargetSampleRate { get; set; } = 44100;

        /// <summary>目标声道数。</summary>
        public int TargetChannels { get; set; } = 2;

        /// <summary>预估内存大小 (字节)。</summary>
        public long EstimatedMemoryBytes { get; set; }

        /// <summary>解码后的 PCM 缓冲 (Memory/Decode 类型)。</summary>
        public AudioBuffer? DecodedBuffer { get; set; }

        /// <summary>是否已加载。</summary>
        public bool IsLoaded => DecodedBuffer != null;

        /// <summary>最后访问时间 (用于 LRU)。</summary>
        public DateTime LastAccessTime { get; set; } = DateTime.UtcNow;

        /// <summary>使用计数。</summary>
        public int UseCount { get; set; }

        /// <summary>所属资源库名称。</summary>
        public string BankName { get; set; } = string.Empty;
    }

    // ──────────────────────────────────────────────────────────────────────
    //  AudioMemoryPool — 内存池
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>内存池，管理固定/动态内存分配。</summary>
    public sealed class AudioMemoryPool : IDisposable
    {
        /// <summary>池 ID。</summary>
        public int PoolId { get; }

        /// <summary>池名称。</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>池类型。</summary>
        public MemoryPoolType PoolType { get; set; } = MemoryPoolType.Dynamic;

        /// <summary>最大容量 (字节, 0 = 无限制)。</summary>
        public long MaxCapacityBytes { get; set; }

        /// <summary>当前已使用字节。</summary>
        public long UsedBytes { get; private set; }

        /// <summary>淘汰策略。</summary>
        public EvictionPolicy EvictionPolicy { get; set; } = EvictionPolicy.LRU;

        /// <summary>已加载的声音定义。</summary>
        private readonly Dictionary<string, SoundDefinition> _loadedSounds = new();

        /// <summary>使用频率计数。</summary>
        private readonly Dictionary<string, int> _frequencyCounter = new();

        /// <summary>加载顺序 (FIFO)。</summary>
        private readonly List<string> _loadOrder = new();

        private readonly object _lock = new();
        private bool _disposed;

        public AudioMemoryPool(int poolId)
        {
            PoolId = poolId;
        }

        /// <summary>分配内存 (检查容量)。</summary>
        public bool TryAllocate(long bytes)
        {
            if (MaxCapacityBytes > 0 && UsedBytes + bytes > MaxCapacityBytes)
                return false;
            UsedBytes += bytes;
            return true;
        }

        /// <summary>释放内存。</summary>
        public void Deallocate(long bytes)
        {
            UsedBytes = Math.Max(0, UsedBytes - bytes);
        }

        /// <summary>加载声音到池中。</summary>
        public bool LoadSound(SoundDefinition sound)
        {
            if (_disposed) return false;
            lock (_lock)
            {
                // 检查容量
                if (MaxCapacityBytes > 0 && UsedBytes + sound.EstimatedMemoryBytes > MaxCapacityBytes)
                {
                    // 尝试淘汰
                    Evict(sound.EstimatedMemoryBytes);
                    if (MaxCapacityBytes > 0 && UsedBytes + sound.EstimatedMemoryBytes > MaxCapacityBytes)
                        return false;
                }

                sound.DecodedBuffer = new AudioBuffer(sound.TargetChannels, sound.TargetSampleRate, 0);
                sound.LastAccessTime = DateTime.UtcNow;
                sound.UseCount++;
                _frequencyCounter[sound.Name] = _frequencyCounter.GetValueOrDefault(sound.Name) + 1;
                _loadOrder.Add(sound.Name);
                _loadedSounds[sound.Name] = sound;
                UsedBytes += sound.EstimatedMemoryBytes;
                return true;
            }
        }

        /// <summary>卸载声音。</summary>
        public bool UnloadSound(string soundName)
        {
            lock (_lock)
            {
                if (!_loadedSounds.TryGetValue(soundName, out var sound)) return false;
                UsedBytes = Math.Max(0, UsedBytes - sound.EstimatedMemoryBytes);
                sound.DecodedBuffer?.Dispose();
                sound.DecodedBuffer = null;
                _loadedSounds.Remove(soundName);
                _loadOrder.Remove(soundName);
                return true;
            }
        }

        /// <summary>获取声音。</summary>
        public SoundDefinition? GetSound(string soundName)
        {
            lock (_lock)
            {
                if (_loadedSounds.TryGetValue(soundName, out var sound))
                {
                    sound.LastAccessTime = DateTime.UtcNow;
                    sound.UseCount++;
                    _frequencyCounter[soundName] = _frequencyCounter.GetValueOrDefault(soundName) + 1;
                    return sound;
                }
                return null;
            }
        }

        /// <summary>淘汰声音直到释放足够空间。</summary>
        private void Evict(long neededBytes)
        {
            long targetUsed = UsedBytes - neededBytes;
            var toEvict = new List<string>();

            switch (EvictionPolicy)
            {
                case EvictionPolicy.LRU:
                    // 按最后访问时间排序
                    foreach (var kvp in _loadedSounds)
                        if (UsedBytes > targetUsed) toEvict.Add(kvp.Key);
                    toEvict.Sort((a, b) =>
                        _loadedSounds[a].LastAccessTime.CompareTo(_loadedSounds[b].LastAccessTime));
                    break;

                case EvictionPolicy.LFU:
                    foreach (var kvp in _frequencyCounter)
                        toEvict.Add(kvp.Key);
                    toEvict.Sort((a, b) => _frequencyCounter[a].CompareTo(_frequencyCounter[b]));
                    break;

                case EvictionPolicy.FIFO:
                    toEvict.AddRange(_loadOrder);
                    break;

                case EvictionPolicy.LowestPriority:
                    foreach (var kvp in _loadedSounds)
                        toEvict.Add(kvp.Key);
                    toEvict.Sort((a, b) => _loadedSounds[a].Priority.CompareTo(_loadedSounds[b].Priority));
                    break;
            }

            foreach (var name in toEvict)
            {
                if (UsedBytes <= targetUsed) break;
                UnloadSound(name);
            }
        }

        /// <summary>获取池使用率。</summary>
        public float GetUsageRatio()
        {
            if (MaxCapacityBytes <= 0) return 0f;
            return (float)UsedBytes / MaxCapacityBytes;
        }

        /// <summary>获取已加载声音数。</summary>
        public int LoadedCount
        {
            get
            {
                lock (_lock) return _loadedSounds.Count;
            }
        }

        /// <summary>卸载所有声音。</summary>
        public void UnloadAll()
        {
            lock (_lock)
            {
                foreach (var kvp in _loadedSounds)
                {
                    kvp.Value.DecodedBuffer?.Dispose();
                    kvp.Value.DecodedBuffer = null;
                }
                _loadedSounds.Clear();
                _frequencyCounter.Clear();
                _loadOrder.Clear();
                UsedBytes = 0;
            }
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                UnloadAll();
                _disposed = true;
            }
        }
    }

    // ──────────────────────────────────────────────────────────────────────
    //  AudioBank — 音频资源库
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>音频资源库 (类似 FMOD Sound Bank)。</summary>
    public sealed class AudioBank
    {
        /// <summary>资源库名称。</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>资源库 ID。</summary>
        public string BankId { get; set; } = string.Empty;

        /// <summary>语言代码 (空 = 默认)。</summary>
        public string Language { get; set; } = string.Empty;

        /// <summary>加载状态。</summary>
        public AudioBankLoadState LoadState { get; set; } = AudioBankLoadState.Unloaded;

        /// <summary>包含的声音定义。</summary>
        public List<SoundDefinition> Sounds { get; } = new();

        /// <summary>包含的事件名称引用。</summary>
        public List<string> EventRefs { get; } = new();

        /// <summary>包含的参数名称引用。</summary>
        public List<string> ParameterRefs { get; } = new();

        /// <summary>依赖的其他资源库 ID。</summary>
        public List<string> Dependencies { get; } = new();

        /// <summary>文件路径。</summary>
        public string FilePath { get; set; } = string.Empty;

        /// <summary>预估总内存大小。</summary>
        public long TotalEstimatedBytes { get; set; }

        /// <summary>加载时间 (秒)。</summary>
        public float LoadTimeSeconds { get; set; }

        /// <summary>添加声音定义。</summary>
        public void AddSound(SoundDefinition sound)
        {
            sound.BankName = Name;
            Sounds.Add(sound);
        }

        /// <summary>添加依赖。</summary>
        public void AddDependency(string bankId)
        {
            if (!Dependencies.Contains(bankId))
                Dependencies.Add(bankId);
        }
    }

    // ──────────────────────────────────────────────────────────────────────
    //  AudioBankReference — 资源库引用
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>资源库运行时引用，支持懒加载和依赖管理。</summary>
    public sealed class AudioBankReference
    {
        /// <summary>引用的资源库 ID。</summary>
        public string BankId { get; set; } = string.Empty;

        /// <summary>引用计数。</summary>
        public int RefCount { get; set; }

        /// <summary>是否已加载。</summary>
        public bool IsLoaded { get; set; }

        /// <summary>是否标记为需要加载。</summary>
        public bool MarkedForLoad { get; set; }

        /// <summary>增加引用。</summary>
        public void AddRef() => RefCount++;

        /// <summary>释放引用。</summary>
        public void Release() => RefCount = Math.Max(0, RefCount - 1);
    }

    // ──────────────────────────────────────────────────────────────────────
    //  AudioBankManager — 资源库管理器
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>资源库加载完成回调。</summary>
    public delegate void BankLoadCallback(AudioBank bank, bool success);

    /// <summary>音频资源库管理器。</summary>
    public sealed class AudioBankManager : IDisposable
    {
        /// <summary>所有资源库。</summary>
        private readonly Dictionary<string, AudioBank> _banks = new();

        /// <summary>资源库引用表。</summary>
        private readonly Dictionary<string, AudioBankReference> _references = new();

        /// <summary>内存池。</summary>
        private readonly Dictionary<int, AudioMemoryPool> _memoryPools = new();

        /// <summary>默认内存池。</summary>
        private AudioMemoryPool _defaultPool;

        /// <summary>全局最大内存限制 (字节, 0=无限制)。</summary>
        public long GlobalMemoryLimit { get; set; }

        /// <summary>是否启用自动虚拟化。</summary>
        public bool EnableVirtualization { get; set; } = true;

        /// <summary>虚拟化阈值 (内存使用率百分比)。</summary>
        public float VirtualizationThreshold { get; set; } = 0.9f;

        private readonly object _lock = new();

        public AudioBankManager()
        {
            _defaultPool = new AudioMemoryPool(0) { Name = "Default" };
            _memoryPools[0] = _defaultPool;
        }

        /// <summary>注册资源库 (不加载)。</summary>
        public void RegisterBank(AudioBank bank)
        {
            _banks[bank.Name] = bank;
            if (!_references.ContainsKey(bank.Name))
                _references[bank.Name] = new AudioBankReference { BankId = bank.Name };
        }

        /// <summary>加载资源库 (同步)。</summary>
        public bool LoadBank(string bankName)
        {
            lock (_lock)
            {
                if (!_banks.TryGetValue(bankName, out var bank)) return false;
                if (bank.LoadState == AudioBankLoadState.Loaded) return true;

                // 加载依赖
                foreach (var dep in bank.Dependencies)
                    LoadBank(dep);

                bank.LoadState = AudioBankLoadState.Loading;

                try
                {
                    var startTime = DateTime.UtcNow;

                    // 将声音定义加载到内存池
                    foreach (var sound in bank.Sounds)
                    {
                        int poolId = sound.MemoryPoolId;
                        if (!_memoryPools.TryGetValue(poolId, out var pool))
                            pool = _defaultPool;
                        pool.LoadSound(sound);
                    }

                    bank.LoadTimeSeconds = (float)(DateTime.UtcNow - startTime).TotalSeconds;
                    bank.LoadState = AudioBankLoadState.Loaded;

                    if (_references.TryGetValue(bankName, out var refInfo))
                    {
                        refInfo.IsLoaded = true;
                        refInfo.AddRef();
                    }

                    return true;
                }
                catch
                {
                    bank.LoadState = AudioBankLoadState.Failed;
                    return false;
                }
            }
        }

        /// <summary>加载资源库 (异步)。</summary>
        public async Task<bool> LoadBankAsync(string bankName, CancellationToken cancellationToken = default)
        {
            return await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return LoadBank(bankName);
            }, cancellationToken);
        }

        /// <summary>卸载资源库。</summary>
        public bool UnloadBank(string bankName)
        {
            lock (_lock)
            {
                if (!_banks.TryGetValue(bankName, out var bank)) return false;
                if (bank.LoadState != AudioBankLoadState.Loaded) return true;

                bank.LoadState = AudioBankLoadState.Unloading;

                // 卸载声音
                foreach (var sound in bank.Sounds)
                {
                    int poolId = sound.MemoryPoolId;
                    if (_memoryPools.TryGetValue(poolId, out var pool))
                        pool.UnloadSound(sound.Name);
                }

                bank.LoadState = AudioBankLoadState.Unloaded;

                if (_references.TryGetValue(bankName, out var refInfo))
                {
                    refInfo.IsLoaded = false;
                    refInfo.Release();
                }

                return true;
            }
        }

        /// <summary>获取已加载的资源库。</summary>
        public AudioBank? GetBank(string bankName)
        {
            return _banks.TryGetValue(bankName, out var bank) ? bank : null;
        }

        /// <summary>请求资源库引用 (懒加载)。</summary>
        public AudioBankReference? RequestBank(string bankName)
        {
            if (!_references.TryGetValue(bankName, out var refInfo))
                return null;

            refInfo.AddRef();

            if (!refInfo.IsLoaded && _banks.ContainsKey(bankName))
                LoadBank(bankName);

            return refInfo;
        }

        /// <summary>释放资源库引用。</summary>
        public void ReleaseBank(string bankName)
        {
            if (_references.TryGetValue(bankName, out var refInfo))
            {
                refInfo.Release();
                if (refInfo.RefCount <= 0 && refInfo.IsLoaded)
                    UnloadBank(bankName);
            }
        }

        /// <summary>创建自定义内存池。</summary>
        public AudioMemoryPool CreateMemoryPool(int poolId, string name, long maxCapacity = 0)
        {
            var pool = new AudioMemoryPool(poolId)
            {
                Name = name,
                MaxCapacityBytes = maxCapacity
            };
            _memoryPools[poolId] = pool;
            return pool;
        }

        /// <summary>获取内存池。</summary>
        public AudioMemoryPool? GetMemoryPool(int poolId)
        {
            return _memoryPools.TryGetValue(poolId, out var pool) ? pool : null;
        }

        /// <summary>查找声音定义 (跨所有资源库)。</summary>
        public SoundDefinition? FindSound(string soundName)
        {
            lock (_lock)
            {
                foreach (var bank in _banks.Values)
                {
                    if (bank.LoadState != AudioBankLoadState.Loaded) continue;
                    foreach (var sound in bank.Sounds)
                        if (sound.Name == soundName)
                            return sound;
                }
                return null;
            }
        }

        /// <summary>获取所有已加载资源库的总内存使用。</summary>
        public long GetTotalMemoryUsage()
        {
            long total = 0;
            foreach (var pool in _memoryPools.Values)
                total += pool.UsedBytes;
            return total;
        }

        /// <summary>执行虚拟化检查 (低优先级声音卸载)。</summary>
        public void PerformVirtualization()
        {
            if (!EnableVirtualization) return;

            long totalUsage = GetTotalMemoryUsage();
            if (GlobalMemoryLimit <= 0) return;

            float usageRatio = (float)totalUsage / GlobalMemoryLimit;
            if (usageRatio < VirtualizationThreshold) return;

            // 按优先级从低到高虚拟化
            var allSounds = new List<SoundDefinition>();
            foreach (var bank in _banks.Values)
            {
                if (bank.LoadState != AudioBankLoadState.Loaded) continue;
                allSounds.AddRange(bank.Sounds);
            }

            allSounds.Sort((a, b) => a.Priority.CompareTo(b.Priority));

            foreach (var sound in allSounds)
            {
                if (usageRatio < VirtualizationThreshold * 0.8f) break;
                if (sound.UseCount == 0 && sound.IsLoaded)
                {
                    int poolId = sound.MemoryPoolId;
                    if (_memoryPools.TryGetValue(poolId, out var pool))
                        pool.UnloadSound(sound.Name);
                    totalUsage = GetTotalMemoryUsage();
                    usageRatio = (float)totalUsage / GlobalMemoryLimit;
                }
            }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                foreach (var pool in _memoryPools.Values)
                    pool.Dispose();
                _memoryPools.Clear();
                _banks.Clear();
                _references.Clear();
            }
        }
    }
}
