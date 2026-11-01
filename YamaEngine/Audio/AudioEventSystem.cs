using System;
using System.Collections.Generic;
using System.Numerics;

namespace YamaEngine.Audio
{
    // ════════════════════════════════════════════════════════════════════════
    // yama 音频事件系统 (Audio Event System)
    //
    // 类似 FMOD Event / Wwise Event。
    // 事件描述、运行时实例、参数系统、Cue 播放列表、开关切换、
    // 参数曲线映射、播放模式、优先级管理。
    // ════════════════════════════════════════════════════════════════════════

    // ──────────────────────────────────────────────────────────────────────
    //  枚举定义
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>事件重放模式。</summary>
    public enum AudioEventPlaybackMode
    {
        /// <summary>播放一次。</summary>
        Once,
        /// <summary>随机播放。</summary>
        Random,
        /// <summary>随机不重复 (上次播放的不重复)。</summary>
        RandomNoRepeat,
        /// <summary>顺序播放。</summary>
        Sequence,
        /// <summary>顺序播放 (同 Sequence)。</summary>
        Sequential,
        /// <summary>随机洗牌 (播放全部后重新洗牌)。</summary>
        Shuffle
    }

    /// <summary>事件优先级。</summary>
    public enum AudioEventPriority
    {
        Lowest = 0,
        Low = 64,
        Medium = 128,
        High = 192,
        Highest = 255,
        /// <summary>虚拟化 (不占实际并发位)。</summary>
        Virtual = 256,
        /// <summary>至关重要 (永不虚拟化)。</summary>
        Vital = 512
    }

    /// <summary>事件参数类型。</summary>
    public enum AudioParameterType
    {
        Float,
        Int,
        Bool,
        Switch
    }

    /// <summary>事件实例播放状态。</summary>
    public enum AudioEventInstanceState
    {
        Stopped,
        Playing,
        Paused,
        Virtualized,
        Starting,
        Stopping
    }

    // ──────────────────────────────────────────────────────────────────────
    //  AudioEventParameter — 事件参数
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>事件参数定义 (模板)。</summary>
    public sealed class AudioEventParameter
    {
        /// <summary>参数名称。</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>参数 ID。</summary>
        public string ParameterId { get; set; } = string.Empty;

        /// <summary>参数类型。</summary>
        public AudioParameterType Type { get; set; } = AudioParameterType.Float;

        /// <summary>最小值 (Float/Int)。</summary>
        public float MinValue { get; set; }

        /// <summary>最大值 (Float/Int)。</summary>
        public float MaxValue { get; set; } = 1f;

        /// <summary>默认值。</summary>
        public float DefaultValue { get; set; }

        /// <summary>标签值 (Switch 类型)。</summary>
        public Dictionary<string, int> LabelValues { get; } = new();

        /// <summary>添加 Switch 标签。</summary>
        public void AddLabel(string label, int value)
        {
            LabelValues[label] = value;
        }

        /// <summary>获取 Switch 标签值。</summary>
        public int GetLabelValue(string label)
        {
            return LabelValues.TryGetValue(label, out var v) ? v : 0;
        }
    }

    // ──────────────────────────────────────────────────────────────────────
    //  AudioParameterCurve — 参数曲线映射
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>曲线插值类型。</summary>
    public enum CurveInterpolationType
    {
        Linear,
        /// <summary>平滑阶梯。</summary>
        SmoothStep,
        /// <summary>三次样条。</summary>
        Cubic,
        /// <summary>指数曲线。</summary>
        Exponential,
        /// <summary>对数曲线。</summary>
        Logarithmic
    }

    /// <summary>曲线控制点。</summary>
    public struct CurvePoint
    {
        /// <summary>X 值 (输入参数)。</summary>
        public float X;
        /// <summary>Y 值 (输出值)。</summary>
        public float Y;

        public CurvePoint(float x, float y)
        {
            X = x;
            Y = y;
        }
    }

    /// <summary>参数 -> 值曲线映射 (距离衰减 / 速度->音高等)。</summary>
    public sealed class AudioParameterCurve
    {
        /// <summary>曲线名称。</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>控制点列表 (按 X 排序)。</summary>
        public List<CurvePoint> Points { get; } = new();

        /// <summary>插值类型。</summary>
        public CurveInterpolationType Interpolation { get; set; } = CurveInterpolationType.Linear;

        /// <summary>Y 轴范围最小。</summary>
        public float OutputMin { get; set; } = 0f;

        /// <summary>Y 轴范围最大。</summary>
        public float OutputMax { get; set; } = 1f;

        public AudioParameterCurve() { }

        public AudioParameterCurve(string name)
        {
            Name = name;
            // 默认添加首尾控制点
            Points.Add(new CurvePoint(0f, 0f));
            Points.Add(new CurvePoint(1f, 1f));
        }

        /// <summary>添加控制点。</summary>
        public void AddPoint(float x, float y)
        {
            Points.Add(new CurvePoint(x, y));
            Points.Sort((a, b) => a.X.CompareTo(b.X));
        }

        /// <summary>根据输入值计算曲线输出。</summary>
        public float Evaluate(float input)
        {
            if (Points.Count == 0) return OutputMin;
            if (Points.Count == 1) return Points[0].Y;

            // 边界
            if (input <= Points[0].X) return Points[0].Y;
            if (input >= Points[Points.Count - 1].X) return Points[Points.Count - 1].Y;

            // 查找区间
            for (int i = 0; i < Points.Count - 1; i++)
            {
                if (input >= Points[i].X && input <= Points[i + 1].X)
                {
                    float t = (input - Points[i].X) / (Points[i + 1].X - Points[i].X);
                    t = ApplyInterpolation(t);
                    return Points[i].Y + (Points[i + 1].Y - Points[i].Y) * t;
                }
            }

            return OutputMin;
        }

        private float ApplyInterpolation(float t)
        {
            return Interpolation switch
            {
                CurveInterpolationType.Linear => t,
                CurveInterpolationType.SmoothStep => t * t * (3f - 2f * t),
                CurveInterpolationType.Cubic => t * t * t,
                CurveInterpolationType.Exponential => (float)Math.Pow(Math.Clamp(t, 0.001, 1f), 2.0),
                CurveInterpolationType.Logarithmic => (float)Math.Log(Math.Clamp(t, 0.001, 1f) + 1f) / 0.693f,
                _ => t
            };
        }
    }

    // ──────────────────────────────────────────────────────────────────────
    //  AudioSwitch — 离散状态开关
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>离散开关 (如 SurfaceType: Concrete/Wood/Metal)。</summary>
    public sealed class AudioSwitch
    {
        /// <summary>开关名称。</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>开关 ID。</summary>
        public string SwitchId { get; set; } = string.Empty;

        /// <summary>当前状态值。</summary>
        public int CurrentValue { get; private set; }

        /// <summary>所有可能的标签/值。</summary>
        public Dictionary<string, int> States { get; } = new();

        /// <summary>状态变更回调。</summary>
        public event Action<AudioSwitch, int, int>? OnStateChanged;

        public AudioSwitch(string name = "")
        {
            Name = name;
        }

        /// <summary>添加状态。</summary>
        public void AddState(string label, int value)
        {
            States[label] = value;
        }

        /// <summary>设置当前状态。</summary>
        public void SetValue(int value)
        {
            int old = CurrentValue;
            CurrentValue = value;
            OnStateChanged?.Invoke(this, old, value);
        }

        /// <summary>通过标签设置状态。</summary>
        public void SetLabel(string label)
        {
            if (States.TryGetValue(label, out var value))
                SetValue(value);
        }

        /// <summary>通过标签获取值。</summary>
        public int GetLabelValue(string label)
        {
            return States.TryGetValue(label, out var v) ? v : -1;
        }
    }

    // ──────────────────────────────────────────────────────────────────────
    //  AudioCue — Cue 播放列表
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>Cue 中的单个声音条目。</summary>
    public struct AudioCueEntry
    {
        /// <summary>声音名称/ID。</summary>
        public string SoundName;
        /// <summary>权重 (用于随机加权)。</summary>
        public float Weight;
        /// <summary>最小延迟 (秒)。</summary>
        public float MinDelay;
        /// <summary>最大延迟 (秒)。</summary>
        public float MaxDelay;

        public AudioCueEntry(string soundName, float weight = 1f, float minDelay = 0f, float maxDelay = 0f)
        {
            SoundName = soundName;
            Weight = weight;
            MinDelay = minDelay;
            MaxDelay = maxDelay;
        }
    }

    /// <summary>Cue：多个 Sound 的播放列表 (随机/序列) + 过渡。</summary>
    public sealed class AudioCue
    {
        /// <summary>Cue 名称。</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>播放列表条目。</summary>
        public List<AudioCueEntry> Entries { get; } = new();

        /// <summary>播放模式。</summary>
        public AudioEventPlaybackMode PlaybackMode { get; set; } = AudioEventPlaybackMode.Once;

        /// <summary>交叉淡入淡出时间 (秒)。</summary>
        public float CrossfadeTime { get; set; } = 0f;

        /// <summary>上一次播放的索引 (用于 RandomNoRepeat)。</summary>
        public int LastPlayedIndex { get; private set; } = -1;

        /// <summary>当前序列位置 (用于 Sequence)。</summary>
        public int SequenceIndex { get; private set; }

        /// <summary>洗牌顺序缓冲。</summary>
        private List<int> _shuffleBuffer = new();
        private int _shuffleIndex;

        /// <summary>添加条目。</summary>
        public void AddEntry(string soundName, float weight = 1f, float minDelay = 0f, float maxDelay = 0f)
        {
            Entries.Add(new AudioCueEntry(soundName, weight, minDelay, maxDelay));
        }

        /// <summary>根据播放模式选择下一个声音。</summary>
        public string SelectNextSound(Random? random = null)
        {
            random ??= new Random();

            if (Entries.Count == 0) return string.Empty;
            if (Entries.Count == 1) return Entries[0].SoundName;

            return PlaybackMode switch
            {
                AudioEventPlaybackMode.Once => Entries[0].SoundName,
                AudioEventPlaybackMode.Random => SelectRandom(random),
                AudioEventPlaybackMode.RandomNoRepeat => SelectRandomNoRepeat(random),
                AudioEventPlaybackMode.Sequence or AudioEventPlaybackMode.Sequential => SelectSequential(),
                AudioEventPlaybackMode.Shuffle => SelectShuffle(random),
                _ => Entries[0].SoundName
            };
        }

        private string SelectRandom(Random random)
        {
            float totalWeight = 0f;
            foreach (var e in Entries) totalWeight += e.Weight;
            float r = (float)random.NextDouble() * totalWeight;
            float cumulative = 0f;
            for (int i = 0; i < Entries.Count; i++)
            {
                cumulative += Entries[i].Weight;
                if (r <= cumulative)
                {
                    LastPlayedIndex = i;
                    return Entries[i].SoundName;
                }
            }
            LastPlayedIndex = Entries.Count - 1;
            return Entries[^1].SoundName;
        }

        private string SelectRandomNoRepeat(Random random)
        {
            if (Entries.Count == 1) return Entries[0].SoundName;
            int idx;
            do
            {
                idx = random.Next(Entries.Count);
            } while (idx == LastPlayedIndex);
            LastPlayedIndex = idx;
            return Entries[idx].SoundName;
        }

        private string SelectSequential()
        {
            if (SequenceIndex >= Entries.Count) SequenceIndex = 0;
            string sound = Entries[SequenceIndex].SoundName;
            LastPlayedIndex = SequenceIndex;
            SequenceIndex++;
            return sound;
        }

        private string SelectShuffle(Random random)
        {
            if (_shuffleBuffer.Count == 0 || _shuffleIndex >= _shuffleBuffer.Count)
            {
                _shuffleBuffer.Clear();
                for (int i = 0; i < Entries.Count; i++) _shuffleBuffer.Add(i);
                // Fisher-Yates 洗牌
                for (int i = _shuffleBuffer.Count - 1; i > 0; i--)
                {
                    int j = random.Next(i + 1);
                    (_shuffleBuffer[i], _shuffleBuffer[j]) = (_shuffleBuffer[j], _shuffleBuffer[i]);
                }
                _shuffleIndex = 0;
            }
            int idx = _shuffleBuffer[_shuffleIndex++];
            LastPlayedIndex = idx;
            return Entries[idx].SoundName;
        }

        /// <summary>重置播放状态。</summary>
        public void Reset()
        {
            LastPlayedIndex = -1;
            SequenceIndex = 0;
            _shuffleBuffer.Clear();
            _shuffleIndex = 0;
        }
    }

    // ──────────────────────────────────────────────────────────────────────
    //  AudioEvent — 事件描述 (模板)
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>3D 空间属性模板。</summary>
    public sealed class AudioEvent3DProperties
    {
        /// <summary>最小距离 (1.0 以内不衰减)。</summary>
        public float MinDistance { get; set; } = 1f;

        /// <summary>最大距离 (衰减到 0)。</summary>
        public float MaxDistance { get; set; } = 50f;

        /// <summary>内锥角度 (度)。</summary>
        public float InnerConeAngle { get; set; } = 360f;

        /// <summary>外锥角度 (度)。</summary>
        public float OuterConeAngle { get; set; } = 360f;

        /// <summary>外锥音量 (0 = 静音)。</summary>
        public float OuterConeVolume { get; set; } = 0f;

        /// <summary>衰减曲线。</summary>
        public AudioParameterCurve? AttenuationCurve { get; set; }

        /// <summary>是否启用 3D 空间化。</summary>
        public bool Is3D { get; set; }
    }

    /// <summary>音频事件描述模板 (类似 FMOD Event Description)。</summary>
    public sealed class AudioEvent
    {
        private static int _nextId;

        /// <summary>事件 ID。</summary>
        public int EventId { get; }

        /// <summary>事件名称。</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>事件 GUID。</summary>
        public string Guid { get; set; } = System.Guid.NewGuid().ToString("N");

        /// <summary>事件优先级。</summary>
        public AudioEventPriority Priority { get; set; } = AudioEventPriority.Medium;

        /// <summary>最大并发实例数 (0 = 不限制)。</summary>
        public int MaxInstances { get; set; }

        /// <summary>超出并发时的行为 (true = 虚拟化 oldest, false = 拒绝新实例)。</summary>
        public bool VirtualizeOldest { get; set; } = true;

        /// <summary>默认音量。</summary>
        public float DefaultVolume { get; set; } = 1f;

        /// <summary>默认音调。</summary>
        public float DefaultPitch { get; set; } = 1f;

        /// <summary>播放模式。</summary>
        public AudioEventPlaybackMode PlaybackMode { get; set; } = AudioEventPlaybackMode.Once;

        /// <summary>Cue (播放列表)。</summary>
        public AudioCue? Cue { get; set; }

        /// <summary>参数列表 (模板)。</summary>
        public List<AudioEventParameter> Parameters { get; } = new();

        /// <summary>3D 空间属性。</summary>
        public AudioEvent3DProperties SpatialProperties { get; set; } = new();

        /// <summary>所属总线名称。</summary>
        public string BusName { get; set; } = "SFX";

        /// <summary>所属资源库名称。</summary>
        public string BankName { get; set; } = string.Empty;

        public AudioEvent()
        {
            EventId = ++_nextId;
        }

        /// <summary>添加参数。</summary>
        public AudioEventParameter AddParameter(string name, AudioParameterType type,
            float min = 0f, float max = 1f, float defaultValue = 0f)
        {
            var param = new AudioEventParameter
            {
                Name = name,
                ParameterId = name,
                Type = type,
                MinValue = min,
                MaxValue = max,
                DefaultValue = defaultValue
            };
            Parameters.Add(param);
            return param;
        }

        /// <summary>创建运行时实例。</summary>
        public AudioEventInstance CreateInstance()
        {
            return new AudioEventInstance(this);
        }

        /// <summary>查找参数 (按名称)。</summary>
        public AudioEventParameter? FindParameter(string name)
        {
            foreach (var p in Parameters)
                if (p.Name == name)
                    return p;
            return null;
        }
    }

    // ──────────────────────────────────────────────────────────────────────
    //  AudioEventInstance — 运行时实例
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>音频事件运行时实例。</summary>
    public sealed class AudioEventInstance : IDisposable
    {
        private static int _nextInstanceId;

        /// <summary>实例 ID。</summary>
        public int InstanceId { get; }

        /// <summary>所属事件模板。</summary>
        public AudioEvent Event { get; }

        /// <summary>当前状态。</summary>
        public AudioEventInstanceState State { get; private set; } = AudioEventInstanceState.Stopped;

        /// <summary>音量。</summary>
        public float Volume { get; set; } = 1f;

        /// <summary>音调。</summary>
        public float Pitch { get; set; } = 1f;

        /// <summary>3D 位置。</summary>
        public Vector3 Position { get; set; }

        /// <summary>3D 速度。</summary>
        public Vector3 Velocity { get; set; }

        /// <summary>当前播放的 Sound 名称 (来自 Cue)。</summary>
        public string? CurrentCueSound { get; private set; }

        /// <summary>当前 Cue 索引。</summary>
        public int CurrentCueIndex { get; private set; }

        /// <summary>运行时参数值 (名称 -> 值)。</summary>
        private readonly Dictionary<string, float> _parameterValues = new();

        /// <summary>当前 Cue 副本 (实例独立)。</summary>
        private AudioCue? _cueInstance;

        /// <summary>底层 Voice。</summary>
        public AudioVoice? Voice { get; set; }

        /// <summary>是否已释放。</summary>
        public bool IsDisposed { get; private set; }

        /// <summary>创建时间。</summary>
        public DateTime CreatedTime { get; } = DateTime.UtcNow;

        /// <summary>事件完成回调。</summary>
        public event Action<AudioEventInstance>? OnCompleted;
        /// <summary>事件启动回调。</summary>
        public event Action<AudioEventInstance>? OnStarted;
        /// <summary>状态变化回调。</summary>
        public event Action<AudioEventInstance, AudioEventInstanceState, AudioEventInstanceState>? OnStateChanged;

        private Random _random = new();
        private static readonly object _idLock = new();

        public AudioEventInstance(AudioEvent audioEvent)
        {
            lock (_idLock)
                InstanceId = ++_nextInstanceId;
            Event = audioEvent ?? throw new ArgumentNullException(nameof(audioEvent));
            Volume = audioEvent.DefaultVolume;
            Pitch = audioEvent.DefaultPitch;

            // 初始化参数默认值
            foreach (var param in audioEvent.Parameters)
                _parameterValues[param.Name] = param.DefaultValue;

            // 复制 Cue
            if (audioEvent.Cue != null)
            {
                _cueInstance = new AudioCue
                {
                    Name = audioEvent.Cue.Name,
                    PlaybackMode = audioEvent.Cue.PlaybackMode,
                    CrossfadeTime = audioEvent.Cue.CrossfadeTime
                };
                foreach (var entry in audioEvent.Cue.Entries)
                    _cueInstance.Entries.Add(entry);
            }
        }

        /// <summary>开始播放。</summary>
        public void Play()
        {
            if (IsDisposed) return;

            var oldState = State;
            State = AudioEventInstanceState.Playing;

            // 从 Cue 选择声音
            if (_cueInstance != null)
            {
                CurrentCueSound = _cueInstance.SelectNextSound(_random);
            }

            OnStateChanged?.Invoke(this, oldState, State);
            OnStarted?.Invoke(this);
        }

        /// <summary>停止播放。</summary>
        public void Stop()
        {
            var oldState = State;
            State = AudioEventInstanceState.Stopped;
            Voice?.Stop();
            OnStateChanged?.Invoke(this, oldState, State);
            OnCompleted?.Invoke(this);
        }

        /// <summary>暂停播放。</summary>
        public void Pause()
        {
            var oldState = State;
            if (State == AudioEventInstanceState.Playing)
            {
                State = AudioEventInstanceState.Paused;
                Voice?.Pause();
                OnStateChanged?.Invoke(this, oldState, State);
            }
        }

        /// <summary>恢复播放。</summary>
        public void Resume()
        {
            var oldState = State;
            if (State == AudioEventInstanceState.Paused)
            {
                State = AudioEventInstanceState.Playing;
                Voice?.Resume();
                OnStateChanged?.Invoke(this, oldState, State);
            }
        }

        /// <summary>设置参数值。</summary>
        public void SetParameter(string name, float value)
        {
            var paramDef = Event.FindParameter(name);
            if (paramDef != null)
            {
                value = Math.Clamp(value, paramDef.MinValue, paramDef.MaxValue);
            }
            _parameterValues[name] = value;
        }

        /// <summary>获取参数值。</summary>
        public float GetParameter(string name)
        {
            return _parameterValues.TryGetValue(name, out var val) ? val : 0f;
        }

        /// <summary>设置 Switch 状态。</summary>
        public void SetSwitch(string switchName, string label)
        {
            var paramDef = Event.FindParameter(switchName);
            if (paramDef != null && paramDef.Type == AudioParameterType.Switch)
            {
                _parameterValues[switchName] = paramDef.GetLabelValue(label);
            }
        }

        /// <summary>获取当前 Cue (只读访问)。</summary>
        public AudioCue? GetCurrentCue() => _cueInstance;

        /// <summary>获取播放状态。</summary>
        public AudioEventInstanceState GetPlaybackState() => State;

        /// <summary>虚拟化此实例。</summary>
        public void Virtualize()
        {
            var oldState = State;
            if (State == AudioEventInstanceState.Playing)
            {
                State = AudioEventInstanceState.Virtualized;
                Voice?.SetVirtualized(true);
                OnStateChanged?.Invoke(this, oldState, State);
            }
        }

        /// <summary>取消虚拟化。</summary>
        public void Devirtualize()
        {
            var oldState = State;
            if (State == AudioEventInstanceState.Virtualized)
            {
                State = AudioEventInstanceState.Playing;
                Voice?.SetVirtualized(false);
                OnStateChanged?.Invoke(this, oldState, State);
            }
        }

        /// <summary>每帧更新。</summary>
        public void Update(float deltaTime)
        {
            if (State == AudioEventInstanceState.Playing && Voice != null)
            {
                if (Voice.State == AudioVoiceState.Stopped)
                {
                    // 如果 Cue 有更多条目，继续播放下一个
                    if (_cueInstance != null && _cueInstance.Entries.Count > 1)
                    {
                        CurrentCueSound = _cueInstance.SelectNextSound(_random);
                        Voice.Play();
                    }
                    else
                    {
                        Stop();
                    }
                }
            }
        }

        public void Dispose()
        {
            if (IsDisposed) return;
            Stop();
            Voice?.Dispose();
            IsDisposed = true;
        }
    }

    // ──────────────────────────────────────────────────────────────────────
    //  AudioEventManager — 事件管理器
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>音频事件管理器。</summary>
    public sealed class AudioEventManager : IDisposable
    {
        /// <summary>事件模板注册表 (名称 -> Event)。</summary>
        private readonly Dictionary<string, AudioEvent> _events = new();

        /// <summary>活跃实例列表。</summary>
        private readonly List<AudioEventInstance> _activeInstances = new();

        /// <summary>全局开关注册表。</summary>
        private readonly Dictionary<string, AudioSwitch> _switches = new();

        /// <summary>全局参数值 (跨实例共享)。</summary>
        private readonly Dictionary<string, float> _globalParameters = new();

        private readonly object _lock = new();

        /// <summary>注册事件模板。</summary>
        public void RegisterEvent(AudioEvent audioEvent)
        {
            _events[audioEvent.Name] = audioEvent;
        }

        /// <summary>注册开关。</summary>
        public void RegisterSwitch(AudioSwitch audioSwitch)
        {
            _switches[audioSwitch.Name] = audioSwitch;
        }

        /// <summary>设置全局参数值。</summary>
        public void SetGlobalParameter(string name, float value)
        {
            _globalParameters[name] = value;
        }

        /// <summary>获取全局参数值。</summary>
        public float GetGlobalParameter(string name)
        {
            return _globalParameters.TryGetValue(name, out var val) ? val : 0f;
        }

        /// <summary>按名称获取事件模板。</summary>
        public AudioEvent? GetEvent(string name)
        {
            return _events.TryGetValue(name, out var ev) ? ev : null;
        }

        /// <summary>按名称获取开关。</summary>
        public AudioSwitch? GetSwitch(string name)
        {
            return _switches.TryGetValue(name, out var sw) ? sw : null;
        }

        /// <summary>触发事件 (创建并播放实例)。</summary>
        public AudioEventInstance? TriggerEvent(string eventName)
        {
            var ev = GetEvent(eventName);
            if (ev == null) return null;

            var instance = ev.CreateInstance();
            lock (_lock)
                _activeInstances.Add(instance);

            // 并发限制
            if (ev.MaxInstances > 0)
                EnforceInstanceLimit(ev);

            return instance;
        }

        /// <summary>每帧更新所有活跃实例。</summary>
        public void Update(float deltaTime)
        {
            lock (_lock)
            {
                for (int i = _activeInstances.Count - 1; i >= 0; i--)
                {
                    var instance = _activeInstances[i];
                    instance.Update(deltaTime);
                    if (instance.State == AudioEventInstanceState.Stopped)
                        _activeInstances.RemoveAt(i);
                }
            }
        }

        /// <summary>停止所有活跃实例。</summary>
        public void StopAll()
        {
            lock (_lock)
            {
                foreach (var inst in _activeInstances)
                    inst.Stop();
                _activeInstances.Clear();
            }
        }

        /// <summary>获取当前活跃实例数。</summary>
        public int ActiveInstanceCount
        {
            get
            {
                lock (_lock) return _activeInstances.Count;
            }
        }

        private void EnforceInstanceLimit(AudioEvent ev)
        {
            int count = 0;
            for (int i = _activeInstances.Count - 1; i >= 0; i--)
            {
                if (_activeInstances[i].Event == ev)
                    count++;
                if (count > ev.MaxInstances)
                {
                    if (ev.VirtualizeOldest)
                        _activeInstances[i].Virtualize();
                    else
                    {
                        _activeInstances[i].Stop();
                        _activeInstances.RemoveAt(i);
                    }
                }
            }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                foreach (var inst in _activeInstances)
                    inst.Dispose();
                _activeInstances.Clear();
            }
            _events.Clear();
            _switches.Clear();
        }
    }
}
