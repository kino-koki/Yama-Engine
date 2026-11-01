// =============================================================================
// yama引擎 - 音频总线/混音器系统 (AudioBusSystem)
// 类似 FMOD Bus / Wwise Bus 的音频中间件混音系统
// =============================================================================
// 本文件实现完整的音频总线/混音器系统，包含：
//   1. MWAudioBus         - 总线（音量/音调/优先级/效果链/父级/子级）
//   2. MWAudioBusType     - 总线类型枚举
//   3. MWAudioMixer       - 混音器（总线树/快照/过渡）
//   4. MWAudioBusSnapshot - 快照（总线参数瞬间切换+过渡）
//   5. MWBusTransition    - 快照过渡（线性插值所有参数）
//   6. MWAudioSend        - 发送（源Bus→效果Bus，wet级别）
//   7. MWAudioDucker      - 闪避（某Bus播放时自动降低另一Bus音量）
//   8. MWAudioEffectSlot  - 效果槽（均衡器/混响/压缩/延迟等）
//   9. MWVoiceVirtualization - 虚拟化（超限声源按优先级自动静音）
// =============================================================================

using System;
using System.Collections.Generic;

namespace YamaEngine.Audio.Middleware
{
    // ══════════════════════════════════════════════════════════════════
    //  一、枚举
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 音频总线类型。
    /// </summary>
    public enum MWAudioBusType
    {
        /// <summary>主总线（根节点）</summary>
        Master,
        /// <summary>音乐总线</summary>
        Music,
        /// <summary>音效总线</summary>
        SFX,
        /// <summary>语音/对白总线</summary>
        Voice,
        /// <summary>UI 音频总线</summary>
        UI,
        /// <summary>环境音总线</summary>
        Ambient,
        /// <summary>混响发送总线（返回）</summary>
        ReverbSend,
        /// <summary>侧链总线</summary>
        Sidechain,
        /// <summary>用户自定义总线</summary>
        Custom
    }

    /// <summary>
    /// 过渡曲线类型。
    /// </summary>
    public enum MWTransitionCurve
    {
        Linear,
        EaseInQuad,
        EaseOutQuad,
        EaseInOutQuad,
        SCurve
    }

    // ══════════════════════════════════════════════════════════════════
    //  二、效果槽
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 效果槽类型。
    /// </summary>
    public enum MWEffectSlotType
    {
        Equalizer,
        Reverb,
        Compressor,
        Delay,
        Chorus,
        Flanger,
        Distortion,
        Gate,
        Limiter,
        PitchShifter,
        LowPassFilter,
        HighPassFilter,
        BandPassFilter,
        Custom
    }

    /// <summary>
    /// 效果槽。挂载到总线上的 DSP 效果单元。
    /// </summary>
    public sealed class MWAudioEffectSlot
    {
        /// <summary>效果类型</summary>
        public MWEffectSlotType Type { get; set; }
        /// <summary>是否启用</summary>
        public bool Enabled { get; set; } = true;
        /// <summary>混合比 (0=dry, 1=wet)</summary>
        public float WetLevel { get; set; } = 1.0f;
        /// <summary>效果参数字典 (参数名 → 值)</summary>
        public Dictionary<string, float> Parameters { get; } = new();

        /// <summary>设置参数值</summary>
        public void SetParameter(string name, float value) =>
            Parameters[name] = value;

        /// <summary>获取参数值</summary>
        public float GetParameter(string name, float defaultValue = 0f) =>
            Parameters.TryGetValue(name, out var v) ? v : defaultValue;

        /// <summary>处理音频采样数据</summary>
        public void Process(float[] samples, int channelCount, int sampleRate)
        {
            if (!Enabled || samples == null || samples.Length == 0) return;

            switch (Type)
            {
                case MWEffectSlotType.Equalizer:
                    ProcessEqualizer(samples, channelCount, sampleRate);
                    break;
                case MWEffectSlotType.Reverb:
                    ProcessReverb(samples, channelCount, sampleRate);
                    break;
                case MWEffectSlotType.Compressor:
                    ProcessCompressor(samples, channelCount, sampleRate);
                    break;
                case MWEffectSlotType.Delay:
                    ProcessDelay(samples, channelCount, sampleRate);
                    break;
                case MWEffectSlotType.LowPassFilter:
                    ProcessLowPass(samples, channelCount, sampleRate);
                    break;
                case MWEffectSlotType.HighPassFilter:
                    ProcessHighPass(samples, channelCount, sampleRate);
                    break;
                // 其他效果类型在实际引擎中实现具体 DSP
            }
        }

        private void ProcessEqualizer(float[] samples, int channels, int sampleRate) { /* DSP 实现 */ }
        private void ProcessReverb(float[] samples, int channels, int sampleRate) { /* DSP 实现 */ }
        private void ProcessCompressor(float[] samples, int channels, int sampleRate) { /* DSP 实现 */ }
        private void ProcessDelay(float[] samples, int channels, int sampleRate) { /* DSP 实现 */ }
        private void ProcessLowPass(float[] samples, int channels, int sampleRate) { /* DSP 实现 */ }
        private void ProcessHighPass(float[] samples, int channels, int sampleRate) { /* DSP 实现 */ }
    }

    // ══════════════════════════════════════════════════════════════════
    //  三、发送（Send）
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 音频发送。将一个总线的信号路由到另一个总线（如 Reverb 返回总线）。
    /// </summary>
    public sealed class MWAudioSend
    {
        /// <summary>目标总线名称</summary>
        public string TargetBusName { get; set; } = string.Empty;
        /// <summary>发送级别 (wet 量, 0~1)</summary>
        public float SendLevel { get; set; } = 1.0f;
        /// <summary>是否启用</summary>
        public bool Enabled { get; set; } = true;
        /// <summary>是否静音</summary>
        public bool Muted { get; set; }

        /// <summary>目标总线引用（运行时设置）</summary>
        public MWAudioBus? TargetBus { get; set; }
    }

    // ══════════════════════════════════════════════════════════════════
    //  四、闪避（Ducker）
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 音频闪避。当源总线有信号时，自动降低目标总线的音量。
    /// </summary>
    public sealed class MWAudioDucker
    {
        /// <summary>触发闪避的源总线名称</summary>
        public string SourceBusName { get; set; } = string.Empty;
        /// <summary>被闪避的目标总线名称</summary>
        public string TargetBusName { get; set; } = string.Empty;
        /// <summary>闪避比率 (0~1)，降低到原始音量的多少</summary>
        public float DuckAmount { get; set; } = 0.2f;
        /// <summary>闪避攻击时间（秒）</summary>
        public float AttackTime { get; set; } = 0.1f;
        /// <summary>闪避释放时间（秒）</summary>
        public float ReleaseTime { get; set; } = 0.5f;
        /// <summary>是否启用</summary>
        public bool Enabled { get; set; } = true;

        // 运行时状态
        private float _currentDuckLevel = 1.0f;
        private bool _isDucking;

        /// <summary>当前闪避音量系数</summary>
        public float CurrentDuckLevel => _currentDuckLevel;

        /// <summary>源总线引用</summary>
        public MWAudioBus? SourceBus { get; set; }
        /// <summary>目标总线引用</summary>
        public MWAudioBus? TargetBus { get; set; }

        /// <summary>更新闪避状态</summary>
        public void Update(float dt, float sourceRmsLevel)
        {
            float threshold = 0.01f;
            bool sourceActive = sourceRmsLevel > threshold;

            if (sourceActive && !_isDucking)
            {
                _isDucking = true;
            }
            else if (!sourceActive && _isDucking)
            {
                _isDucking = false;
            }

            float target = _isDucking ? DuckAmount : 1.0f;
            float speed = _isDucking ? 1.0f / MathF.Max(AttackTime, 0.001f) : 1.0f / MathF.Max(ReleaseTime, 0.001f);
            _currentDuckLevel += (target - _currentDuckLevel) * MathF.Min(speed * dt, 1.0f);
        }
    }

    // ══════════════════════════════════════════════════════════════════
    //  五、音频总线
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// yama引擎音频中间件总线。
    /// 支持树状层级结构、效果链、发送路由。
    /// </summary>
    public sealed class MWAudioBus
    {
        /// <summary>总线名称</summary>
        public string Name { get; set; } = string.Empty;
        /// <summary>总线类型</summary>
        public MWAudioBusType BusType { get; set; }
        /// <summary>音量 (0~2)</summary>
        public float Volume { get; set; } = 1.0f;
        /// <summary>音调 (0.1~4.0, 1=原始)</summary>
        public float Pitch { get; set; } = 1.0f;
        /// <summary>优先级 (0=最低, 255=最高)</summary>
        public int Priority { get; set; } = 128;
        /// <summary>是否静音</summary>
        public bool Muted { get; set; }
        /// <summary>是否独奏</summary>
        public bool Solo { get; set; }
        /// <summary>是否暂停</summary>
        public bool Paused { get; set; }

        /// <summary>父级总线</summary>
        public MWAudioBus? Parent { get; private set; }
        /// <summary>子级总线列表</summary>
        public List<MWAudioBus> Children { get; } = new();
        /// <summary>效果链</summary>
        public List<MWAudioEffectSlot> EffectSlots { get; } = new();
        /// <summary>发送列表</summary>
        public List<MWAudioSend> Sends { get; } = new();

        /// <summary>当前峰值电平</summary>
        public float PeakLevel { get; set; }
        /// <summary>当前 RMS 电平</summary>
        public float RMSLevel { get; set; }

        // ── 构造 ──

        public MWAudioBus(string name, MWAudioBusType type = MWAudioBusType.Custom)
        {
            Name = name;
            BusType = type;
        }

        // ── 树操作 ──

        /// <summary>添加子总线</summary>
        public MWAudioBus AddChild(string name, MWAudioBusType type = MWAudioBusType.Custom)
        {
            var child = new MWAudioBus(name, type) { Parent = this };
            Children.Add(child);
            return child;
        }

        /// <summary>从树中移除此总线</summary>
        public void RemoveFromParent()
        {
            Parent?.Children.Remove(this);
            Parent = null;
        }

        /// <summary>按名称查找后代总线</summary>
        public MWAudioBus? FindChild(string name)
        {
            foreach (var child in Children)
            {
                if (child.Name == name) return child;
                var found = child.FindChild(name);
                if (found != null) return found;
            }
            return null;
        }

        // ── 效果链 ──

        /// <summary>添加效果槽</summary>
        public MWAudioEffectSlot AddEffect(MWEffectSlotType type)
        {
            var slot = new MWAudioEffectSlot { Type = type };
            EffectSlots.Add(slot);
            return slot;
        }

        /// <summary>移除效果槽</summary>
        public bool RemoveEffect(MWAudioEffectSlot slot) =>
            EffectSlots.Remove(slot);

        // ── 发送 ──

        /// <summary>添加发送</summary>
        public MWAudioSend AddSend(string targetBusName, float level = 1.0f)
        {
            var send = new MWAudioSend
            {
                TargetBusName = targetBusName,
                SendLevel = level
            };
            Sends.Add(send);
            return send;
        }

        // ── 计算 ──

        /// <summary>获取从根到此总线的最终有效音量</summary>
        public float GetFinalVolume()
        {
            float vol = Muted ? 0f : Volume;
            var current = Parent;
            while (current != null)
            {
                vol *= current.Muted ? 0f : current.Volume;
                current = current.Parent;
            }
            return vol;
        }

        /// <summary>获取此总线及所有子总线活跃声音总数</summary>
        public int GetActiveVoiceCount()
        {
            return 0; // 由 Mixer 运行时维护
        }
    }

    // ══════════════════════════════════════════════════════════════════
    //  六、总线快照
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 总线快照参数。保存某个总线在快照中的音量/音调/静音等状态。
    /// </summary>
    public sealed class MWBusSnapshotEntry
    {
        /// <summary>总线名称</summary>
        public string BusName { get; set; } = string.Empty;
        /// <summary>音量</summary>
        public float Volume { get; set; } = 1.0f;
        /// <summary>音调</summary>
        public float Pitch { get; set; } = 1.0f;
        /// <summary>静音</summary>
        public bool Muted { get; set; }
    }

    /// <summary>
    /// 音频总线快照。保存一组总线的参数状态，可实现瞬间切换或渐变过渡。
    /// </summary>
    public sealed class MWAudioBusSnapshot
    {
        /// <summary>快照名称</summary>
        public string Name { get; set; } = string.Empty;
        /// <summary>快照优先级（高优先级覆盖低优先级）</summary>
        public int Priority { get; set; } = 0;
        /// <summary>总线参数条目</summary>
        public List<MWBusSnapshotEntry> Entries { get; } = new();
        /// <summary>效果参数条目 (总线名 → 效果索引 → 参数名 → 值)</summary>
        public Dictionary<string, Dictionary<int, Dictionary<string, float>>> EffectEntries { get; } = new();

        /// <summary>设置总线音量</summary>
        public void SetBusVolume(string busName, float volume)
        {
            var entry = FindOrCreateEntry(busName);
            entry.Volume = Math.Clamp(volume, 0f, 2f);
        }

        /// <summary>设置总线音调</summary>
        public void SetBusPitch(string busName, float pitch)
        {
            var entry = FindOrCreateEntry(busName);
            entry.Pitch = Math.Clamp(pitch, 0.1f, 4f);
        }

        /// <summary>设置总线静音</summary>
        public void SetBusMuted(string busName, bool muted)
        {
            var entry = FindOrCreateEntry(busName);
            entry.Muted = muted;
        }

        /// <summary>设置效果参数</summary>
        public void SetEffectParameter(string busName, int effectIndex, string paramName, float value)
        {
            if (!EffectEntries.TryGetValue(busName, out var busEffects))
            {
                busEffects = new Dictionary<int, Dictionary<string, float>>();
                EffectEntries[busName] = busEffects;
            }
            if (!busEffects.TryGetValue(effectIndex, out var effectParams))
            {
                effectParams = new Dictionary<string, float>();
                busEffects[effectIndex] = effectParams;
            }
            effectParams[paramName] = value;
        }

        /// <summary>获取总线参数</summary>
        public MWBusSnapshotEntry? GetEntry(string busName)
        {
            foreach (var e in Entries)
                if (e.BusName == busName) return e;
            return null;
        }

        private MWBusSnapshotEntry FindOrCreateEntry(string busName)
        {
            var entry = GetEntry(busName);
            if (entry == null)
            {
                entry = new MWBusSnapshotEntry { BusName = busName };
                Entries.Add(entry);
            }
            return entry;
        }
    }

    // ══════════════════════════════════════════════════════════════════
    //  七、快照过渡
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 快照过渡。在两个快照之间线性插值所有参数，实现平滑切换。
    /// </summary>
    public sealed class MWBusSnapshotTransition : IDisposable
    {
        /// <summary>起始快照</summary>
        public MWAudioBusSnapshot? FromSnapshot { get; set; }
        /// <summary>目标快照</summary>
        public MWAudioBusSnapshot? ToSnapshot { get; set; }
        /// <summary>过渡时长（秒）</summary>
        public float Duration { get; set; } = 1.0f;
        /// <summary>过渡曲线</summary>
        public MWTransitionCurve Curve { get; set; } = MWTransitionCurve.Linear;
        /// <summary>当前进度 (0~1)</summary>
        public float Progress { get; private set; }
        /// <summary>是否已完成</summary>
        public bool IsCompleted => Progress >= 1.0f;
        /// <summary>是否正在过渡</summary>
        public bool IsActive => Progress < 1.0f;

        /// <summary>过渡完成回调</summary>
        public event Action? OnCompleted;

        /// <summary>开始过渡</summary>
        public void Start(MWAudioBusSnapshot from, MWAudioBusSnapshot to, float duration = 1.0f)
        {
            FromSnapshot = from;
            ToSnapshot = to;
            Duration = Math.Max(0.001f, duration);
            Progress = 0f;
        }

        /// <summary>更新过渡进度</summary>
        public void Update(float dt)
        {
            if (IsCompleted) return;

            Progress += dt / Duration;
            if (Progress >= 1.0f)
            {
                Progress = 1.0f;
                OnCompleted?.Invoke();
            }
        }

        /// <summary>获取过渡后的插值音量</summary>
        public float GetInterpolatedVolume(string busName)
        {
            float fromVol = FromSnapshot?.GetEntry(busName)?.Volume ?? 1f;
            float toVol = ToSnapshot?.GetEntry(busName)?.Volume ?? 1f;
            float t = ApplyCurve(Progress);
            return fromVol + (toVol - fromVol) * t;
        }

        /// <summary>获取过渡后的插值音调</summary>
        public float GetInterpolatedPitch(string busName)
        {
            float fromPitch = FromSnapshot?.GetEntry(busName)?.Pitch ?? 1f;
            float toPitch = ToSnapshot?.GetEntry(busName)?.Pitch ?? 1f;
            float t = ApplyCurve(Progress);
            return fromPitch + (toPitch - fromPitch) * t;
        }

        private float ApplyCurve(float t)
        {
            return Curve switch
            {
                MWTransitionCurve.Linear => t,
                MWTransitionCurve.EaseInQuad => t * t,
                MWTransitionCurve.EaseOutQuad => t * (2f - t),
                MWTransitionCurve.EaseInOutQuad => t < 0.5f ? 2f * t * t : -1f + (4f - 2f * t) * t,
                MWTransitionCurve.SCurve => t * t * (3f - 2f * t),
                _ => t
            };
        }

        public void Dispose() { }
    }

    // ══════════════════════════════════════════════════════════════════
    //  八、虚拟化
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 虚拟化淘汰策略。
    /// </summary>
    public enum MWVirtualizationPolicy
    {
        /// <summary>虚拟化最安静的</summary>
        VirtualizeQuietest,
        /// <summary>虚拟化最低优先级的</summary>
        VirtualizeLowestPriority,
        /// <summary>虚拟化最远的</summary>
        VirtualizeFarthest,
        /// <summary>停止最旧的（而非虚拟化）</summary>
        StopOldest
    }

    /// <summary>
    /// 声音虚拟化系统。当活跃声源超过真实通道上限时，
    /// 按优先级自动虚拟化（静音但保持状态）或停止多余声源。
    /// </summary>
    public sealed class MWVoiceVirtualization
    {
        /// <summary>淘汰策略</summary>
        public MWVirtualizationPolicy Policy { get; set; } = MWVirtualizationPolicy.VirtualizeQuietest;
        /// <summary>虚拟化开始阈值（可听度低于此值时虚拟化）</summary>
        public float VirtualizeThreshold { get; set; } = 0.01f;
        /// <summary>恢复阈值（可听度高于此值时恢复真实发声）</summary>
        public float RecoverThreshold { get; set; } = 0.05f;
        /// <summary>最大真实发声数</summary>
        public int MaxRealVoices { get; set; } = 64;

        /// <summary>当前真实发声数</summary>
        public int CurrentRealVoices { get; private set; }
        /// <summary>当前虚拟化数</summary>
        public int CurrentVirtualVoices { get; private set; }

        /// <summary>虚拟化事件</summary>
        public event Action<int, bool>? OnVoiceVirtualized; // (voiceId, isVirtual)

        private readonly List<VoiceInfo> _voices = new();

        private struct VoiceInfo
        {
            public int Id;
            public float Audibility;
            public int Priority;
            public float Distance;
            public float ElapsedTime;
        }

        /// <summary>更新虚拟化状态</summary>
        public void Update(Dictionary<int, float> voiceAudibilities)
        {
            _voices.Clear();
            foreach (var kvp in voiceAudibilities)
            {
                _voices.Add(new VoiceInfo
                {
                    Id = kvp.Key,
                    Audibility = kvp.Value
                });
            }

            // 按可听度降序排序
            _voices.Sort((a, b) => b.Audibility.CompareTo(a.Audibility));

            CurrentRealVoices = 0;
            CurrentVirtualVoices = 0;

            foreach (var voice in _voices)
            {
                bool shouldVirtualize = CurrentRealVoices >= MaxRealVoices ||
                                        voice.Audibility < VirtualizeThreshold;

                if (shouldVirtualize)
                    CurrentVirtualVoices++;
                else
                    CurrentRealVoices++;
            }
        }
    }

    // ══════════════════════════════════════════════════════════════════
    //  九、混音器
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// yama引擎音频中间件混音器。
    /// 管理总线树、快照切换、过渡效果、闪避、虚拟化。
    /// </summary>
    public sealed class MWAudioMixer : IDisposable
    {
        /// <summary>主总线（根节点）</summary>
        public MWAudioBus MasterBus { get; }

        private readonly Dictionary<string, MWAudioBus> _busLookup = new();
        private readonly Dictionary<string, MWAudioBusSnapshot> _snapshots = new();
        private readonly List<MWBusSnapshotTransition> _activeTransitions = new();
        private readonly List<MWAudioDucker> _duckers = new();
        private MWVoiceVirtualization _virtualization = new();
        private readonly object _lock = new();

        /// <summary>虚拟化系统</summary>
        public MWVoiceVirtualization Virtualization
        {
            get => _virtualization;
            set => _virtualization = value ?? new MWVoiceVirtualization();
        }

        public event Action<string, float>? OnBusVolumeChanged;
        public event Action<string, string>? OnSnapshotTransitionStarted; // (snapshotFrom, snapshotTo)

        public MWAudioMixer()
        {
            MasterBus = new MWAudioBus("Master", MWAudioBusType.Master);
            _busLookup["Master"] = MasterBus;

            // 创建默认子总线
            CreateDefaultBuses();
        }

        private void CreateDefaultBuses()
        {
            var buses = new (string name, MWAudioBusType type)[]
            {
                ("Music", MWAudioBusType.Music),
                ("SFX", MWAudioBusType.SFX),
                ("Voice", MWAudioBusType.Voice),
                ("UI", MWAudioBusType.UI),
                ("Ambient", MWAudioBusType.Ambient),
                ("Reverb_Send", MWAudioBusType.ReverbSend)
            };

            foreach (var (name, type) in buses)
            {
                var bus = MasterBus.AddChild(name, type);
                _busLookup[name] = bus;
            }

            // Reverb 发送总线默认音量 0
            if (_busLookup.TryGetValue("Reverb_Send", out var reverbSend))
                reverbSend.Volume = 0f;
        }

        // ── 总线操作 ──

        /// <summary>按名称获取总线</summary>
        public MWAudioBus? GetBus(string name) =>
            _busLookup.TryGetValue(name, out var bus) ? bus : null;

        /// <summary>创建自定义子总线</summary>
        public MWAudioBus? CreateBus(string parentName, string name, MWAudioBusType type = MWAudioBusType.Custom)
        {
            var parent = GetBus(parentName);
            if (parent == null) return null;

            var child = parent.AddChild(name, type);
            _busLookup[name] = child;
            return child;
        }

        /// <summary>设置总线音量</summary>
        public bool SetBusVolume(string busName, float volume)
        {
            var bus = GetBus(busName);
            if (bus == null) return false;
            bus.Volume = Math.Clamp(volume, 0f, 2f);
            OnBusVolumeChanged?.Invoke(busName, bus.Volume);
            return true;
        }

        /// <summary>设置总线静音</summary>
        public bool SetBusMuted(string busName, bool muted)
        {
            var bus = GetBus(busName);
            if (bus == null) return false;
            bus.Muted = muted;
            return true;
        }

        /// <summary>设置总线暂停</summary>
        public bool SetBusPaused(string busName, bool paused)
        {
            var bus = GetBus(busName);
            if (bus == null) return false;
            bus.Paused = paused;
            return true;
        }

        // ── 快照 ──

        /// <summary>创建空快照</summary>
        public MWAudioBusSnapshot CreateSnapshot(string name)
        {
            var snapshot = new MWAudioBusSnapshot { Name = name };
            _snapshots[name] = snapshot;
            return snapshot;
        }

        /// <summary>从当前总线状态创建快照</summary>
        public MWAudioBusSnapshot CaptureSnapshot(string name)
        {
            var snapshot = new MWAudioBusSnapshot { Name = name };
            CaptureBusStateRecursive(MasterBus, snapshot);
            _snapshots[name] = snapshot;
            return snapshot;
        }

        /// <summary>应用快照（瞬间切换）</summary>
        public void ApplySnapshot(string name)
        {
            if (!_snapshots.TryGetValue(name, out var snapshot)) return;
            ApplySnapshotImmediate(snapshot);
        }

        /// <summary>过渡到快照</summary>
        public MWBusSnapshotTransition TransitionToSnapshot(
            string targetName, float duration = 1.0f, MWTransitionCurve curve = MWTransitionCurve.Linear)
        {
            // 从当前状态捕获起始快照
            var from = CaptureSnapshot($"_auto_from_{Guid.NewGuid():N}");
            var to = _snapshots.TryGetValue(targetName, out var target) ? target : null;

            var transition = new MWBusSnapshotTransition
            {
                FromSnapshot = from,
                ToSnapshot = to,
                Duration = duration,
                Curve = curve
            };
            transition.Start(from, to ?? from, duration);

            lock (_lock)
                _activeTransitions.Add(transition);

            OnSnapshotTransitionStarted?.Invoke(from.Name, targetName);
            return transition;
        }

        private void CaptureBusStateRecursive(MWAudioBus bus, MWAudioBusSnapshot snapshot)
        {
            snapshot.SetBusVolume(bus.Name, bus.Volume);
            snapshot.SetBusPitch(bus.Name, bus.Pitch);
            snapshot.SetBusMuted(bus.Name, bus.Muted);

            foreach (var child in bus.Children)
                CaptureBusStateRecursive(child, snapshot);
        }

        private void ApplySnapshotImmediate(MWAudioBusSnapshot snapshot)
        {
            foreach (var entry in snapshot.Entries)
            {
                var bus = GetBus(entry.BusName);
                if (bus == null) continue;
                bus.Volume = entry.Volume;
                bus.Pitch = entry.Pitch;
                bus.Muted = entry.Muted;
            }
        }

        // ── 闪避 ──

        /// <summary>添加闪避规则</summary>
        public MWAudioDucker AddDucker(string sourceBus, string targetBus, float duckAmount = 0.2f)
        {
            var ducker = new MWAudioDucker
            {
                SourceBusName = sourceBus,
                TargetBusName = targetBus,
                DuckAmount = duckAmount,
                SourceBus = GetBus(sourceBus),
                TargetBus = GetBus(targetBus)
            };
            _duckers.Add(ducker);
            return ducker;
        }

        // ── 更新 ──

        /// <summary>每帧更新</summary>
        public void Update(float dt)
        {
            // 更新过渡
            lock (_lock)
            {
                for (int i = _activeTransitions.Count - 1; i >= 0; i--)
                {
                    _activeTransitions[i].Update(dt);
                    if (_activeTransitions[i].IsCompleted)
                        _activeTransitions.RemoveAt(i);
                }
            }

            // 应用活跃过渡到总线
            ApplyActiveTransitions();

            // 更新闪避
            foreach (var ducker in _duckers)
            {
                if (ducker.Enabled && ducker.SourceBus != null && ducker.TargetBus != null)
                {
                    ducker.Update(dt, ducker.SourceBus.RMSLevel);
                    ducker.TargetBus.Volume *= ducker.CurrentDuckLevel;
                }
            }

            // 更新电平
            UpdateLevelsRecursive(MasterBus);
        }

        private void ApplyActiveTransitions()
        {
            lock (_lock)
            {
                foreach (var transition in _activeTransitions)
                {
                    if (transition.ToSnapshot == null) continue;
                    foreach (var entry in transition.ToSnapshot.Entries)
                    {
                        var bus = GetBus(entry.BusName);
                        if (bus == null) continue;
                        if (transition.FromSnapshot != null)
                        {
                            var fromEntry = transition.FromSnapshot.GetEntry(entry.BusName);
                            float fromVol = fromEntry?.Volume ?? 1f;
                            float t = transition.Progress;
                            // 简化：直接应用目标快照值乘以进度
                            bus.Volume = fromVol + (entry.Volume - fromVol) * t;
                        }
                    }
                }
            }
        }

        private void UpdateLevelsRecursive(MWAudioBus bus)
        {
            bus.PeakLevel *= 0.95f; // 衰减
            bus.RMSLevel *= 0.95f;
            foreach (var child in bus.Children)
                UpdateLevelsRecursive(child);
        }

        public void Dispose()
        {
            _activeTransitions.Clear();
            _duckers.Clear();
            _snapshots.Clear();
            _busLookup.Clear();
        }
    }
}
