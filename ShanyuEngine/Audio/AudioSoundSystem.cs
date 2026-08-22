// =============================================================================
// 山雨引擎 - 声音/音效系统 (AudioSoundSystem)
// 类似 FMOD Sound / Wwise Sound 的声音定义与管理系统
// =============================================================================
// 本文件实现完整的声音/音效系统，包含：
//   1. MWAudioSound        - 声音定义（文件路径/格式/3D属性/加载方式）
//   2. MWAudioSoundType    - 声音类型枚举
//   3. MWAudioSoundMode   - 加载模式枚举
//   4. 3D 属性             - 最小/最大距离/衰减曲线/锥角/多普勒系数/方向性
//   5. MWDistanceCurve     - 自定义距离→音量曲线
//   6. MWPreloadPool       - 预加载管理（常驻内存的热门音效）
// =============================================================================

using System;
using System.Collections.Generic;
using System.Numerics;

namespace ShanyuEngine.Audio.Middleware
{
    // ══════════════════════════════════════════════════════════════════
    //  一、枚举
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 声音类型。
    /// </summary>
    public enum MWAudioSoundType
    {
        /// <summary>静态声音（完全加载到内存）</summary>
        Static,
        /// <summary>流式声音（从磁盘实时读取）</summary>
        Stream,
        /// <summary>程序化生成声音（合成）</summary>
        Procedural,
        /// <summary>快照触发声音</summary>
        Snapshot,
        /// <summary>多乐器声音（多种音色随机选择）</summary>
        MultiInstrument
    }

    /// <summary>
    /// 声音加载模式。
    /// </summary>
    public enum MWAudioSoundMode
    {
        /// <summary>完全加载到内存</summary>
        Memory,
        /// <summary>实时流式播放</summary>
        Streaming,
        /// <summary>解码后流式</summary>
        DecompressedStream,
        /// <summary>从采样池共享</summary>
        SamplePool
    }

    /// <summary>
    /// 衰减曲线类型。
    /// </summary>
    public enum MWCurveType
    {
        Linear,
        Logarithmic,
        InverseSquare,
        Custom,
        Constant
    }

    // ══════════════════════════════════════════════════════════════════
    //  二、衰减曲线
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 距离衰减曲线。支持线性/对数/自定义采样点插值。
    /// </summary>
    public sealed class MWDistanceCurve
    {
        /// <summary>曲线类型</summary>
        public MWCurveType CurveType { get; set; } = MWCurveType.Logarithmic;

        /// <summary>自定义采样点列表 (距离, 音量系数)</summary>
        public List<(float Distance, float Volume)> ControlPoints { get; } = new();

        /// <summary>设置预设曲线类型</summary>
        public MWDistanceCurve(MWCurveType type = MWCurveType.Logarithmic)
        {
            CurveType = type;
            SetupDefaultCurve(type);
        }

        private void SetupDefaultCurve(MWCurveType type)
        {
            ControlPoints.Clear();
            switch (type)
            {
                case MWCurveType.Linear:
                    AddPoint(0f, 1f);
                    AddPoint(100f, 0f);
                    break;
                case MWCurveType.Logarithmic:
                    AddPoint(0f, 1f);
                    AddPoint(5f, 0.7f);
                    AddPoint(20f, 0.3f);
                    AddPoint(50f, 0.1f);
                    AddPoint(100f, 0.01f);
                    break;
                case MWCurveType.InverseSquare:
                    AddPoint(0f, 1f);
                    AddPoint(1f, 1f);
                    AddPoint(5f, 0.04f);
                    AddPoint(10f, 0.01f);
                    AddPoint(50f, 0.0004f);
                    break;
                case MWCurveType.Constant:
                    AddPoint(0f, 1f);
                    break;
            }
        }

        /// <summary>添加控制点</summary>
        public void AddPoint(float distance, float volume)
        {
            ControlPoints.Add((distance, Math.Clamp(volume, 0f, 1f)));
            ControlPoints.Sort((a, b) => a.Distance.CompareTo(b.Distance));
        }

        /// <summary>根据距离计算衰减值 (0~1)</summary>
        public float Evaluate(float distance)
        {
            if (ControlPoints.Count == 0) return 1f;
            if (ControlPoints.Count == 1) return ControlPoints[0].Volume;

            // 低于第一个点
            if (distance <= ControlPoints[0].Distance)
                return ControlPoints[0].Volume;

            // 高于最后一个点
            if (distance >= ControlPoints[^1].Distance)
                return ControlPoints[^1].Volume;

            // 找到插值区间
            for (int i = 0; i < ControlPoints.Count - 1; i++)
            {
                if (distance >= ControlPoints[i].Distance && distance < ControlPoints[i + 1].Distance)
                {
                    float t = (distance - ControlPoints[i].Distance) /
                              (ControlPoints[i + 1].Distance - ControlPoints[i].Distance);
                    return ControlPoints[i].Volume +
                           (ControlPoints[i + 1].Volume - ControlPoints[i].Volume) * t;
                }
            }

            return 0f;
        }
    }

    // ══════════════════════════════════════════════════════════════════
    //  三、3D 声音属性
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 锥角设置。定义方向性声源的内外锥角。
    /// </summary>
    public sealed class MWConeSettings
    {
        /// <summary>内锥角（度数），范围内音量不变</summary>
        public float InnerAngle { get; set; } = 360f;
        /// <summary>外锥角（度数），范围外音量衰减到 OutsideVolume</summary>
        public float OuterAngle { get; set; } = 360f;
        /// <summary>外锥角外音量系数</summary>
        public float OutsideVolume { get; set; } = 0f;

        /// <summary>根据角度计算锥角音量因子</summary>
        public float CalculateConeFactor(float angleDegrees)
        {
            if (angleDegrees <= InnerAngle) return 1.0f;
            if (angleDegrees >= OuterAngle) return OutsideVolume;
            float t = (angleDegrees - InnerAngle) / (OuterAngle - InnerAngle);
            return 1.0f - (1.0f - OutsideVolume) * t;
        }
    }

    /// <summary>
    /// 3D 声音属性。定义声音的空间化参数。
    /// </summary>
    public sealed class MWAudio3DProperties
    {
        /// <summary>最小距离（在此范围内音量不随距离衰减）</summary>
        public float MinDistance { get; set; } = 1.0f;
        /// <summary>最大距离（超出此距离声音完全静音）</summary>
        public float MaxDistance { get; set; } = 100.0f;
        /// <summary>自定义距离衰减曲线</summary>
        public MWDistanceCurve AttenuationCurve { get; set; } = new(MWCurveType.Logarithmic);
        /// <summary>锥角设置</summary>
        public MWConeSettings ConeSettings { get; set; } = new();
        /// <summary>多普勒系数 (0=禁用, 1=真实)</summary>
        public float DopplerFactor { get; set; } = 1.0f;
        /// <summary>方向性因子 (0=全向, 1=完全定向)</summary>
        public float Directionality { get; set; } = 0f;
        /// <summary>是否启用 3D</summary>
        public bool Is3D { get; set; } = true;
        /// <summary>是否启用空气吸收</summary>
        public bool EnableAirAbsorption { get; set; } = false;
        /// <summary>空气吸收系数</summary>
        public float AirAbsorptionFactor { get; set; } = 0.1f;

        /// <summary>根据距离计算最终音量</summary>
        public float CalculateVolume(float distance)
        {
            if (!Is3D) return 1.0f;
            if (distance <= MinDistance) return 1.0f;
            if (distance >= MaxDistance) return 0f;

            float attenuation = AttenuationCurve.Evaluate(distance - MinDistance);

            if (EnableAirAbsorption)
            {
                float airAbsorption = MathF.Exp(-AirAbsorptionFactor * distance * 0.01f);
                attenuation *= airAbsorption;
            }

            return attenuation;
        }
    }

    // ══════════════════════════════════════════════════════════════════
    //  四、声音定义
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 山雨引擎音频中间件声音定义。
    /// 类似 FMOD Sound / Wwise Sound，描述一个声音资源的所有属性。
    /// </summary>
    public sealed class MWAudioSound
    {
        /// <summary>声音唯一标识</summary>
        public Guid Id { get; } = Guid.NewGuid();
        /// <summary>声音名称</summary>
        public string Name { get; set; } = string.Empty;
        /// <summary>文件路径</summary>
        public string FilePath { get; set; } = string.Empty;
        /// <summary>音频格式</summary>
        public AudioFormat Format { get; set; } = AudioFormat.Float32;
        /// <summary>声音类型</summary>
        public MWAudioSoundType SoundType { get; set; } = MWAudioSoundType.Static;
        /// <summary>加载模式</summary>
        public MWAudioSoundMode LoadMode { get; set; } = MWAudioSoundMode.Memory;

        /// <summary>3D 属性</summary>
        public MWAudio3DProperties Properties3D { get; set; } = new();

        /// <summary>默认音量 (0~2)</summary>
        public float DefaultVolume { get; set; } = 1.0f;
        /// <summary>默认音调 (0.1~4.0)</summary>
        public float DefaultPitch { get; set; } = 1.0f;
        /// <summary>默认优先级 (0~255)</summary>
        public int DefaultPriority { get; set; } = 128;
        /// <summary>是否循环</summary>
        public bool Looping { get; set; }
        /// <summary>最小/最大循环次数（0=无限）</summary>
        public int MinLoopCount { get; set; }
        public int MaxLoopCount { get; set; }

        /// <summary>目标总线名称</summary>
        public string TargetBusName { get; set; } = "SFX";

        // ── 元信息（加载后填充） ──

        /// <summary>采样率</summary>
        public int SampleRate { get; set; }
        /// <summary>声道数</summary>
        public int Channels { get; set; }
        /// <summary>总采样帧数</summary>
        public int TotalFrames { get; set; }
        /// <summary>时长（秒）</summary>
        public float Duration => SampleRate > 0 ? (float)TotalFrames / SampleRate : 0f;
        /// <summary>文件大小（字节）</summary>
        public long FileSize { get; set; }

        // ── 运行时状态 ──

        /// <summary>是否已加载</summary>
        public bool IsLoaded { get; set; }
        /// <summary>是否正在加载</summary>
        public bool IsLoading { get; set; }
        /// <summary>引用计数</summary>
        public int RefCount { get; private set; }

        /// <summary>加载完成回调</summary>
        public event Action<MWAudioSound>? OnLoaded;
        /// <summary>卸载回调</summary>
        public event Action<MWAudioSound>? OnUnloaded;

        // ── 引用计数 ──

        /// <summary>增加引用</summary>
        public void AddRef() => RefCount++;

        /// <summary>减少引用，返回是否可释放</summary>
        public bool Release()
        {
            if (RefCount > 0) RefCount--;
            return RefCount <= 0;
        }

        // ── 工具 ──

        /// <summary>估算内存占用（字节）</summary>
        public long EstimateMemoryUsage()
        {
            return (long)TotalFrames * Channels * sizeof(float);
        }

        public override string ToString() =>
            $"Sound[{Name}] Type={SoundType} Mode={LoadMode} Format={Format} " +
            $"Duration={Duration:F2}s 3D={Properties3D.Is3D}";
    }

    // ══════════════════════════════════════════════════════════════════
    //  五、声音注册表
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 声音注册表。管理所有已注册的声音定义。
    /// </summary>
    public sealed class MWAudioSoundRegistry
    {
        private readonly Dictionary<string, MWAudioSound> _byName = new();
        private readonly Dictionary<Guid, MWAudioSound> _byId = new();
        private readonly Dictionary<string, List<MWAudioSound>> _byBus = new();
        private readonly object _lock = new();

        /// <summary>已注册声音数</summary>
        public int Count { get; private set; }

        /// <summary>注册声音</summary>
        public void Register(MWAudioSound sound)
        {
            lock (_lock)
            {
                _byName[sound.Name] = sound;
                _byId[sound.Id] = sound;

                if (!_byBus.TryGetValue(sound.TargetBusName, out var list))
                {
                    list = new List<MWAudioSound>();
                    _byBus[sound.TargetBusName] = list;
                }
                list.Add(sound);
                Count++;
            }
        }

        /// <summary>按名称获取声音</summary>
        public MWAudioSound? GetByName(string name)
        {
            lock (_lock)
                return _byName.TryGetValue(name, out var s) ? s : null;
        }

        /// <summary>按 ID 获取声音</summary>
        public MWAudioSound? GetById(Guid id)
        {
            lock (_lock)
                return _byId.TryGetValue(id, out var s) ? s : null;
        }

        /// <summary>获取指定总线的所有声音</summary>
        public List<MWAudioSound> GetByBus(string busName)
        {
            lock (_lock)
                return _byBus.TryGetValue(busName, out var list) ? new List<MWAudioSound>(list) : new();
        }

        /// <summary>注销声音</summary>
        public bool Unregister(string name)
        {
            lock (_lock)
            {
                if (!_byName.TryGetValue(name, out var sound)) return false;
                _byName.Remove(name);
                _byId.Remove(sound.Id);
                if (_byBus.TryGetValue(sound.TargetBusName, out var list))
                    list.Remove(sound);
                Count--;
                return true;
            }
        }

        /// <summary>获取所有声音</summary>
        public List<MWAudioSound> GetAll()
        {
            lock (_lock)
                return new List<MWAudioSound>(_byName.Values);
        }
    }

    // ══════════════════════════════════════════════════════════════════
    //  六、预加载池
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 预加载策略。
    /// </summary>
    public enum MWPreloadStrategy
    {
        /// <summary>启动时立即加载</summary>
        Immediate,
        /// <summary>空闲时后台加载</summary>
        Background,
        /// <summary>首次使用时加载</summary>
        OnDemand
    }

    /// <summary>
    /// 预加载项。
    /// </summary>
    public sealed class MWPreloadEntry
    {
        /// <summary>声音定义</summary>
        public MWAudioSound Sound { get; set; } = null!;
        /// <summary>加载策略</summary>
        public MWPreloadStrategy Strategy { get; set; } = MWPreloadStrategy.Immediate;
        /// <summary>优先级</summary>
        public int Priority { get; set; } = 100;
        /// <summary>是否已加载</summary>
        public bool IsLoaded { get; set; }
        /// <summary>预估内存</summary>
        public long EstimatedMemory { get; set; }
    }

    /// <summary>
    /// 预加载池。管理常驻内存的热门音效，支持预加载策略和内存上限。
    /// </summary>
    public sealed class MWPreloadPool
    {
        private readonly List<MWPreloadEntry> _entries = new();
        private readonly Dictionary<string, MWPreloadEntry> _byName = new();
        private long _memoryBudget = 64 * 1024 * 1024; // 默认 64MB
        private long _memoryUsed;
        private readonly object _lock = new();

        /// <summary>内存预算（字节）</summary>
        public long MemoryBudget
        {
            get => _memoryBudget;
            set => _memoryBudget = Math.Max(0, value);
        }

        /// <summary>已用内存</summary>
        public long MemoryUsed => _memoryUsed;
        /// <summary>内存利用率</summary>
        public float MemoryUtilization => _memoryBudget > 0 ? (float)_memoryUsed / _memoryBudget : 0f;

        /// <summary>添加预加载声音</summary>
        public void Add(MWAudioSound sound, MWPreloadStrategy strategy = MWPreloadStrategy.Immediate, int priority = 100)
        {
            lock (_lock)
            {
                if (_byName.ContainsKey(sound.Name)) return;

                var entry = new MWPreloadEntry
                {
                    Sound = sound,
                    Strategy = strategy,
                    Priority = priority,
                    EstimatedMemory = sound.EstimateMemoryUsage()
                };
                _entries.Add(entry);
                _byName[sound.Name] = entry;

                // 按优先级排序
                _entries.Sort((a, b) => b.Priority.CompareTo(a.Priority));
            }
        }

        /// <summary>移除预加载声音</summary>
        public bool Remove(string soundName)
        {
            lock (_lock)
            {
                if (!_byName.TryGetValue(soundName, out var entry)) return false;
                _entries.Remove(entry);
                _byName.Remove(soundName);
                if (entry.IsLoaded)
                    _memoryUsed -= entry.EstimatedMemory;
                return true;
            }
        }

        /// <summary>检查是否有足够内存</summary>
        public bool HasMemoryFor(long bytes)
        {
            return _memoryUsed + bytes <= _memoryBudget;
        }

        /// <summary>执行预加载（根据策略）</summary>
        public void ProcessPreloads()
        {
            lock (_lock)
            {
                foreach (var entry in _entries)
                {
                    if (entry.IsLoaded) continue;

                    if (entry.Strategy == MWPreloadStrategy.Immediate ||
                        entry.Strategy == MWPreloadStrategy.Background)
                    {
                        if (HasMemoryFor(entry.EstimatedMemory))
                        {
                            // 标记为已加载（实际加载在引擎层完成）
                            entry.IsLoaded = true;
                            _memoryUsed += entry.EstimatedMemory;
                        }
                    }
                }
            }
        }

        /// <summary>清除所有预加载</summary>
        public void Clear()
        {
            lock (_lock)
            {
                _entries.Clear();
                _byName.Clear();
                _memoryUsed = 0;
            }
        }

        /// <summary>强制卸载最低优先级的已加载项以释放内存</summary>
        public long EvictToBudget()
        {
            long freed = 0;
            lock (_lock)
            {
                for (int i = _entries.Count - 1; i >= 0 && _memoryUsed > _memoryBudget; i--)
                {
                    if (_entries[i].IsLoaded)
                    {
                        _entries[i].IsLoaded = false;
                        _memoryUsed -= _entries[i].EstimatedMemory;
                        freed += _entries[i].EstimatedMemory;
                    }
                }
            }
            return freed;
        }
    }
}
