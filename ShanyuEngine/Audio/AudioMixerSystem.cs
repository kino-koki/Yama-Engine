using System;
using System.Collections.Generic;
using System.Numerics;

namespace ShanyuEngine.Audio
{
    // ════════════════════════════════════════════════════════════════════════
    // 山雨 混音器系统 (Audio Mixer System)
    //
    // 类似 FMOD Mixer / Wwise Mixer。
    // 总线树结构 (AudioBus)、效果链、快照过渡、自动避让 (Duck)、
    // 侧链压缩、VU表/频谱分析 (FFT)。
    // ════════════════════════════════════════════════════════════════════════

    // ──────────────────────────────────────────────────────────────────────
    //  AudioBusEffect — 总线效果槽位
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>效果插入位置。</summary>
    public enum EffectInsertPoint
    {
        /// <summary>前级效果 (输入端，在音量/声像之前)。</summary>
        PreFader,
        /// <summary>后级效果 (输出端，在音量/声像之后)。</summary>
        PostFader
    }

    /// <summary>总线效果槽位，用于挂载效果处理器。</summary>
    public sealed class AudioBusEffectSlot
    {
        /// <summary>槽位索引。</summary>
        public int Index { get; set; }

        /// <summary>插入位置 (前级/后级)。</summary>
        public EffectInsertPoint InsertPoint { get; set; } = EffectInsertPoint.PostFader;

        /// <summary>效果处理器实例 (可从 AudioEffectSystem 中创建)。</summary>
        public object? Effect { get; set; }

        /// <summary>是否启用。</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>是否旁路此效果。</summary>
        public bool Bypassed { get; set; }

        /// <summary>效果混合量 (Wet/Dry) [0,1]。</summary>
        public float WetLevel { get; set; } = 1.0f;
    }

    // ──────────────────────────────────────────────────────────────────────
    //  AudioBus — 混音总线
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>混音总线，类似 FMOD Bus / Wwise Bus。</summary>
    public class AudioBus
    {
        private static int _nextId;

        /// <summary>总线唯一 ID。</summary>
        public int BusId { get; }

        /// <summary>总线名称。</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>总线音量 [0, inf)，1.0 为原始。</summary>
        public float Volume { get; set; } = 1.0f;

        /// <summary>总线音调系数。</summary>
        public float Pitch { get; set; } = 1.0f;

        /// <summary>声像位置 [-1(左), 0(中), 1(右)]。</summary>
        public float Pan { get; set; } = 0f;

        /// <summary>是否静音。</summary>
        public bool Muted { get; set; }

        /// <summary>是否独奏。</summary>
        public bool Soloed { get; set; }

        /// <summary>是否旁路效果链。</summary>
        public bool EffectsBypassed { get; set; }

        /// <summary>父总线 (null 表示挂接到 MasterBus)。</summary>
        public AudioBus? ParentBus { get; set; }

        /// <summary>子总线列表。</summary>
        public List<AudioBus> ChildBuses { get; } = new();

        /// <summary>前级效果链。</summary>
        public List<AudioBusEffectSlot> PreEffects { get; } = new();

        /// <summary>后级效果链。</summary>
        public List<AudioBusEffectSlot> PostEffects { get; } = new();

        /// <summary>当前通过此总线的活跃 Voice 数。</summary>
        public int ActiveVoiceCount { get; set; }

        /// <summary>VU 表 (左右声道峰值, 范围 [0,1])。</summary>
        public float LeftLevel { get; set; }
        public float RightLevel { get; set; }

        /// <summary>RMS 响度 (分贝)。</summary>
        public float RmsLevelDb { get; set; }

        /// <summary>频谱数据 (FFT 输出，最近一帧)。</summary>
        public float[]? SpectrumData { get; set; }

        /// <summary>频谱分辨率 (FFT 大小)。</summary>
        public int SpectrumResolution { get; set; } = 512;

        /// <summary>最终输出增益 (含父链级联)。</summary>
        public float GetEffectiveVolume()
        {
            float vol = Muted ? 0f : Volume;
            var parent = ParentBus;
            while (parent != null)
            {
                vol *= parent.Muted ? 0f : parent.Volume;
                parent = parent.ParentBus;
            }
            return vol;
        }

        /// <summary>添加子总线。</summary>
        public AudioBus AddChildBus(string name)
        {
            var child = new AudioBus { Name = name, ParentBus = this };
            ChildBuses.Add(child);
            return child;
        }

        /// <summary>添加效果到指定位置。</summary>
        public AudioBusEffectSlot AddEffect(EffectInsertPoint point, object effect)
        {
            var slot = new AudioBusEffectSlot
            {
                Index = point == EffectInsertPoint.PreFader ? PreEffects.Count : PostEffects.Count,
                InsertPoint = point,
                Effect = effect
            };
            if (point == EffectInsertPoint.PreFader)
                PreEffects.Add(slot);
            else
                PostEffects.Add(slot);
            return slot;
        }

        /// <summary>移除指定效果。</summary>
        public bool RemoveEffect(AudioBusEffectSlot slot)
        {
            return slot.InsertPoint == EffectInsertPoint.PreFader
                ? PreEffects.Remove(slot)
                : PostEffects.Remove(slot);
        }

        public AudioBus()
        {
            BusId = ++_nextId;
        }

        public AudioBus(string name) : this()
        {
            Name = name;
        }
    }

    // ──────────────────────────────────────────────────────────────────────
    //  MasterBus — 主输出总线
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>主输出总线，混音树的根节点。</summary>
    public sealed class MasterBus : AudioBus
    {
        /// <summary>设备输出音量 (系统级)。</summary>
        public float DeviceVolume { get; set; } = 1.0f;

        /// <summary>系统主音量 (用户设置)。</summary>
        public float SystemVolume { get; set; } = 1.0f;

        /// <summary>最终输出增益 = Volume * DeviceVolume * SystemVolume。</summary>
        public float GetFinalOutputVolume()
        {
            return GetEffectiveVolume() * DeviceVolume * SystemVolume;
        }

        /// <summary>主 VU 表左声道。</summary>
        public float MasterLeft { get; set; }

        /// <summary>主 VU 表右声道。</summary>
        public float MasterRight { get; set; }

        /// <summary>主频谱。</summary>
        public float[]? MasterSpectrum { get; set; }

        /// <summary>削波指示。</summary>
        public bool Clipping { get; set; }

        public MasterBus() : base("Master") { }

        /// <summary>混合所有子总线的输出到最终缓冲。</summary>
        public void MixDown(float[] outputBuffer, int channels, int sampleRate)
        {
            // 递归收集所有子总线信号，应用级联增益
            float masterVol = GetFinalOutputVolume();
            for (int i = 0; i < outputBuffer.Length; i++)
            {
                outputBuffer[i] *= masterVol;
            }

            // 削波检测
            Clipping = false;
            for (int i = 0; i < outputBuffer.Length; i++)
            {
                if (outputBuffer[i] > 1.0f || outputBuffer[i] < -1.0f)
                {
                    Clipping = true;
                    outputBuffer[i] = Math.Clamp(outputBuffer[i], -1f, 1f);
                }
            }
        }
    }

    // ──────────────────────────────────────────────────────────────────────
    //  AudioBusGroup — 预设总线分组
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>预设总线分组类型。</summary>
    public enum AudioBusGroupType
    {
        Music,
        SFX,
        Ambience,
        Voice,
        UI,
        Physics,
        Custom
    }

    /// <summary>总线分组管理器。</summary>
    public sealed class AudioBusGroup
    {
        private readonly Dictionary<AudioBusGroupType, AudioBus> _groups = new();

        /// <summary>所属混音器。</summary>
        public AudioMixer Mixer { get; }

        /// <summary>获取指定类型的总线。</summary>
        public AudioBus? GetBus(AudioBusGroupType type)
        {
            return _groups.TryGetValue(type, out var bus) ? bus : null;
        }

        /// <summary>创建标准分组 (Music/SFX/Ambience/Voice/UI/Physics)。</summary>
        public void CreateStandardGroups(MasterBus master)
        {
            var types = new[]
            {
                AudioBusGroupType.Music,
                AudioBusGroupType.SFX,
                AudioBusGroupType.Ambience,
                AudioBusGroupType.Voice,
                AudioBusGroupType.UI,
                AudioBusGroupType.Physics
            };

            foreach (var type in types)
            {
                var bus = master.AddChildBus(type.ToString());
                _groups[type] = bus;
            }
        }

        /// <summary>添加自定义分组。</summary>
        public AudioBus AddCustomGroup(string name, MasterBus master)
        {
            var bus = master.AddChildBus(name);
            _groups[AudioBusGroupType.Custom] = bus;
            return bus;
        }

        public AudioBusGroup(AudioMixer mixer)
        {
            Mixer = mixer;
        }
    }

    // ──────────────────────────────────────────────────────────────────────
    //  MixerSnapshot — 混音快照
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>快照过渡类型。</summary>
    public enum SnapshotTransitionType
    {
        /// <summary>瞬间切换。</summary>
        Instant,
        /// <summary>线性渐变。</summary>
        Linear,
        /// <summary>指数曲线渐变 (更平滑)。</summary>
        Exponential,
        /// <summary>对数曲线渐变 (模拟人耳响度感知)。</summary>
        Logarithmic
    }

    /// <summary>混音快照，保存某一时刻所有总线状态用于快速切换/过渡。</summary>
    public sealed class MixerSnapshot
    {
        /// <summary>快照名称。</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>总线 ID -> 音量映射。</summary>
        public Dictionary<int, float> BusVolumes { get; } = new();

        /// <summary>总线 ID -> 音调映射。</summary>
        public Dictionary<int, float> BusPitches { get; } = new();

        /// <summary>总线 ID -> 效果参数映射 (效果索引 -> 参数名 -> 值)。</summary>
        public Dictionary<int, Dictionary<int, Dictionary<string, float>>> EffectParameters { get; } = new();

        /// <summary>过渡类型。</summary>
        public SnapshotTransitionType TransitionType { get; set; } = SnapshotTransitionType.Linear;

        /// <summary>过渡时长 (秒)。</summary>
        public float TransitionDuration { get; set; } = 1.0f;

        /// <summary>从当前混音状态捕获快照。</summary>
        public void Capture(AudioMixer mixer)
        {
            BusVolumes.Clear();
            BusPitches.Clear();
            CaptureBusRecursive(mixer.RootBus);
        }

        /// <summary>将快照应用到混音器。</summary>
        public void Apply(AudioMixer mixer)
        {
            ApplyBusRecursive(mixer.RootBus);
        }

        private void CaptureBusRecursive(AudioBus bus)
        {
            BusVolumes[bus.BusId] = bus.Volume;
            BusPitches[bus.BusId] = bus.Pitch;
            foreach (var child in bus.ChildBuses)
                CaptureBusRecursive(child);
        }

        private void ApplyBusRecursive(AudioBus bus)
        {
            if (BusVolumes.TryGetValue(bus.BusId, out var vol))
                bus.Volume = vol;
            if (BusPitches.TryGetValue(bus.BusId, out var pitch))
                bus.Pitch = pitch;
            foreach (var child in bus.ChildBuses)
                ApplyBusRecursive(child);
        }
    }

    // ──────────────────────────────────────────────────────────────────────
    //  AudioDucker — 自动避让
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>避让状态。</summary>
    public enum DuckState
    {
        Idle,
        Attacking,
        Sustaining,
        Releasing
    }

    /// <summary>自动避让 (Audio Ducking)，语音触发时自动降低音乐/环境音等。</summary>
    public sealed class AudioDucker
    {
        /// <summary>避让器名称。</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>目标总线 (被避让的总线)。</summary>
        public AudioBus? TargetBus { get; set; }

        /// <summary>触发总线 (触发射避让的总线，如 Voice)。</summary>
        public AudioBus? TriggerBus { get; set; }

        /// <summary>目标降低音量 (0 = 完全静音, 0.5 = 降低50%)。</summary>
        public float TargetVolume { get; set; } = 0.2f;

        /// <summary>攻击时间 (秒, 下降)。</summary>
        public float AttackTime { get; set; } = 0.1f;

        /// <summary>保持时间 (秒, 最低音量持续时间)。</summary>
        public float HoldTime { get; set; } = 0.5f;

        /// <summary>释放时间 (秒, 恢复)。</summary>
        public float ReleaseTime { get; set; } = 0.5f;

        /// <summary>触发阈值 (触发总线活跃 Voice 数 > 此值时触发)。</summary>
        public int TriggerThreshold { get; set; } = 1;

        /// <summary>当前避让状态。</summary>
        public DuckState State { get; private set; } = DuckState.Idle;

        /// <summary>原始音量 (避让前保存)。</summary>
        private float _originalVolume = 1.0f;

        /// <summary>当前避让进度 [0,1]。</summary>
        public float CurrentProgress { get; private set; }

        /// <summary>是否正在避让。</summary>
        public bool IsDucking => State != DuckState.Idle;

        /// <summary>每帧更新避让逻辑。</summary>
        public void Update(float deltaTime)
        {
            if (TargetBus == null || TriggerBus == null) return;

            bool shouldTrigger = TriggerBus.ActiveVoiceCount >= TriggerThreshold;

            switch (State)
            {
                case DuckState.Idle:
                    if (shouldTrigger)
                    {
                        State = DuckState.Attacking;
                        _originalVolume = TargetBus.Volume;
                        CurrentProgress = 0f;
                    }
                    break;

                case DuckState.Attacking:
                    CurrentProgress += deltaTime / Math.Max(0.001f, AttackTime);
                    if (CurrentProgress >= 1f)
                    {
                        CurrentProgress = 1f;
                        State = DuckState.Sustaining;
                        _holdTimer = HoldTime;
                    }
                    TargetBus.Volume = Lerp(_originalVolume, TargetVolume, CurrentProgress);
                    break;

                case DuckState.Sustaining:
                    _holdTimer -= deltaTime;
                    if (_holdTimer <= 0f || !shouldTrigger)
                        State = DuckState.Releasing;
                    break;

                case DuckState.Releasing:
                    CurrentProgress -= deltaTime / Math.Max(0.001f, ReleaseTime);
                    if (CurrentProgress <= 0f)
                    {
                        CurrentProgress = 0f;
                        TargetBus.Volume = _originalVolume;
                        State = DuckState.Idle;
                    }
                    else
                    {
                        TargetBus.Volume = Lerp(_originalVolume, TargetVolume, CurrentProgress);
                    }
                    break;
            }
        }

        /// <summary>重置避让器。</summary>
        public void Reset()
        {
            if (TargetBus != null && State != DuckState.Idle)
                TargetBus.Volume = _originalVolume;
            State = DuckState.Idle;
            CurrentProgress = 0f;
        }

        private float _holdTimer;

        private static float Lerp(float a, float b, float t)
        {
            return a + (b - a) * Math.Clamp(t, 0f, 1f);
        }
    }

    // ──────────────────────────────────────────────────────────────────────
    //  VU 表 / 频谱分析
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>VU 表分析器，计算峰值和 RMS。</summary>
    public sealed class VUMeter
    {
        /// <summary>左声道峰值 [0,1]。</summary>
        public float PeakL { get; private set; }

        /// <summary>右声道峰值 [0,1]。</summary>
        public float PeakR { get; private set; }

        /// <summary>RMS 响度 (线性) [0,1]。</summary>
        public float Rms { get; private set; }

        /// <summary>RMS 响度 (分贝)。</summary>
        public float RmsDb => Rms > 0.0001f ? 20f * (float)Math.Log10(Rms) : -96f;

        /// <summary>峰值衰减速度 (每秒)。</summary>
        public float PeakDecay { get; set; } = 3.0f;

        /// <summary>分析一段 PCM 采样数据。</summary>
        public void Analyze(float[] samples, int channels)
        {
            if (samples.Length == 0 || channels <= 0) return;

            float peakL = 0f, peakR = 0f, sumSquares = 0f;

            for (int i = 0; i < samples.Length; i++)
            {
                float abs = Math.Abs(samples[i]);
                if (abs > peakL && (channels == 1 || i % channels == 0))
                    peakL = abs;
                if (abs > peakR && channels >= 2 && i % channels == 1)
                    peakR = abs;
                sumSquares += samples[i] * samples[i];
            }

            // 峰值衰减
            float decayFactor = (float)Math.Exp(-PeakDecay * 0.016); // ~60fps
            PeakL = Math.Max(peakL, PeakL * decayFactor);
            PeakR = Math.Max(peakR, PeakR * decayFactor);

            // RMS
            Rms = (float)Math.Sqrt(sumSquares / samples.Length);
        }

        /// <summary>重置。</summary>
        public void Reset()
        {
            PeakL = 0f;
            PeakR = 0f;
            Rms = 0f;
        }
    }

    /// <summary>频谱分析器 (简易 DFT/FFT)。</summary>

    // ──────────────────────────────────────────────────────────────────────
    //  AudioMixer — 混音器主管理器
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>混音器，管理总线树、快照、避让、侧链。</summary>
    public sealed class AudioMixer : IDisposable
    {
        /// <summary>根总线 (主输出)。</summary>
        public MasterBus RootBus { get; }

        /// <summary>总线分组。</summary>
        public AudioBusGroup Groups { get; }

        /// <summary>所有总线注册表 (ID -> Bus)。</summary>
        private readonly Dictionary<int, AudioBus> _busRegistry = new();

        /// <summary>快照注册表 (名称 -> Snapshot)。</summary>
        private readonly Dictionary<string, MixerSnapshot> _snapshots = new();

        /// <summary>避让器列表。</summary>
        public List<AudioDucker> Duckers { get; } = new();

        /// <summary>VU 表 (主总线)。</summary>
        public VUMeter MasterVU { get; } = new();

        /// <summary>频谱分析器 (主总线)。</summary>
        public SpectrumAnalyzer MasterSpectrum { get; } = new();

        /// <summary>当前正在过渡的快照。</summary>
        private MixerSnapshot? _transitioningSnapshot;
        private float _transitionTime;
        private float _transitionDuration;

        /// <summary>是否正在过渡快照。</summary>
        public bool IsTransitioning => _transitioningSnapshot != null;

        public AudioMixer()
        {
            RootBus = new MasterBus();
            Groups = new AudioBusGroup(this);
            _busRegistry[RootBus.BusId] = RootBus;
        }

        /// <summary>初始化标准总线分组。</summary>
        public void InitializeStandardGroups()
        {
            Groups.CreateStandardGroups(RootBus);
            RegisterBusesRecursive(RootBus);
        }

        /// <summary>按 ID 查找总线。</summary>
        public AudioBus? FindBus(int busId)
        {
            return _busRegistry.TryGetValue(busId, out var bus) ? bus : null;
        }

        /// <summary>按名称查找总线。</summary>
        public AudioBus? FindBusByName(string name)
        {
            foreach (var bus in _busRegistry.Values)
                if (bus.Name == name)
                    return bus;
            return null;
        }

        /// <summary>保存快照。</summary>
        public MixerSnapshot SaveSnapshot(string name)
        {
            var snapshot = new MixerSnapshot { Name = name };
            snapshot.Capture(this);
            _snapshots[name] = snapshot;
            return snapshot;
        }

        /// <summary>加载并应用快照 (瞬间切换)。</summary>
        public bool LoadSnapshot(string name)
        {
            if (!_snapshots.TryGetValue(name, out var snapshot)) return false;
            snapshot.Apply(this);
            return true;
        }

        /// <summary>加载快照并渐变过渡。</summary>
        public bool TransitionToSnapshot(string name, float duration, SnapshotTransitionType type)
        {
            if (!_snapshots.TryGetValue(name, out var snapshot)) return false;

            _transitioningSnapshot = snapshot;
            _transitionDuration = Math.Max(0.001f, duration);
            _transitionTime = 0f;
            _transitioningSnapshot.TransitionType = type;
            _transitioningSnapshot.TransitionDuration = duration;
            return true;
        }

        /// <summary>添加避让器。</summary>
        public AudioDucker AddDucker(string name, AudioBus target, AudioBus trigger,
            float targetVolume = 0.2f, float attack = 0.1f, float hold = 0.5f, float release = 0.5f)
        {
            var ducker = new AudioDucker
            {
                Name = name,
                TargetBus = target,
                TriggerBus = trigger,
                TargetVolume = targetVolume,
                AttackTime = attack,
                HoldTime = hold,
                ReleaseTime = release
            };
            Duckers.Add(ducker);
            return ducker;
        }

        /// <summary>每帧更新混音器。</summary>
        public void Update(float deltaTime)
        {
            // 更新避让器
            foreach (var ducker in Duckers)
                ducker.Update(deltaTime);

            // 更新快照过渡
            if (_transitioningSnapshot != null)
            {
                _transitionTime += deltaTime;
                float t = Math.Clamp(_transitionTime / _transitionDuration, 0f, 1f);

                // 简单线性插值过渡 (可扩展为其他曲线)
                InterpolateSnapshot(RootBus, _transitioningSnapshot, t);

                if (t >= 1f)
                    _transitioningSnapshot = null;
            }
        }

        /// <summary>执行混音处理 (收集所有 Voice 输出并混合到最终缓冲)。</summary>
        public void ProcessMix(float[] outputBuffer, int channels, int sampleRate)
        {
            // 更新 VU 表
            MasterVU.Analyze(outputBuffer, channels);

            // 更新频谱
            MasterSpectrum.Analyze(outputBuffer, channels, sampleRate);

            // 主总线混合
            RootBus.MixDown(outputBuffer, channels, sampleRate);

            // 主 VU 表赋值到总线
            RootBus.MasterLeft = MasterVU.PeakL;
            RootBus.MasterRight = MasterVU.PeakR;
            RootBus.RmsLevelDb = MasterVU.RmsDb;
            RootBus.SpectrumData = MasterSpectrum.Spectrum;
        }

        public void Dispose()
        {
            _snapshots.Clear();
            _busRegistry.Clear();
            Duckers.Clear();
        }

        private void RegisterBusesRecursive(AudioBus bus)
        {
            _busRegistry[bus.BusId] = bus;
            foreach (var child in bus.ChildBuses)
                RegisterBusesRecursive(child);
        }

        private void InterpolateSnapshot(AudioBus bus, MixerSnapshot snapshot, float t)
        {
            if (snapshot.BusVolumes.TryGetValue(bus.BusId, out var targetVol))
            {
                float startVol = bus.Volume; // 简化：使用当前值作为起始
                bus.Volume = startVol + (targetVol - startVol) * t;
            }
            if (snapshot.BusPitches.TryGetValue(bus.BusId, out var targetPitch))
            {
                float startPitch = bus.Pitch;
                bus.Pitch = startPitch + (targetPitch - startPitch) * t;
            }
            foreach (var child in bus.ChildBuses)
                InterpolateSnapshot(child, snapshot, t);
        }
    }
}
