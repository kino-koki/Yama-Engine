using System;
using System.Collections.Generic;

namespace YamaEngine.Audio
{
    // ════════════════════════════════════════════════════════════════════════
    // yama 音频同步系统 (Audio Sync System)
    //
    // 同步标记点、节拍追踪、BPM 检测、速度图、音频时间线、
    // 同步回调、MIDI 同步、SMPTE 时间码。
    // ════════════════════════════════════════════════════════════════════════

    // ──────────────────────────────────────────────────────────────────────
    //  枚举
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>标记点类型。</summary>
    public enum SyncMarkerType
    {
        Beat,
        Bar,
        BPMChange,
        TimeSignature,
        CuePoint,
        Section,
        LoopStart,
        LoopEnd,
        Custom
    }

    /// <summary>MIDI 时钟模式。</summary>
    public enum MIDIClockMode
    {
        Off,
        Master,
        Slave
    }

    /// <summary>SMPTE 时间码格式。</summary>
    public enum SMPTEFormat
    {
        /// <summary>24 fps。</summary>
        FPS24,
        /// <summary>25 fps (PAL)。</summary>
        FPS25,
        /// <summary>29.97 fps drop-frame (NTSC)。</summary>
        FPS29_97DF,
        /// <summary>30 fps。</summary>
        FPS30,
        /// <summary>30 fps drop-frame。</summary>
        FPS30DF
    }

    // ──────────────────────────────────────────────────────────────────────
    //  AudioSyncMarker — 同步标记点
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>音频同步标记点。</summary>
    public sealed class AudioSyncMarker
    {
        /// <summary>标记名称。</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>标记类型。</summary>
        public SyncMarkerType Type { get; set; } = SyncMarkerType.Custom;

        /// <summary>标记位置 (采样帧)。</summary>
        public long SamplePosition { get; set; }

        /// <summary>标记位置 (秒)。</summary>
        public double TimePosition { get; set; }

        /// <summary>BPM (仅 BPMChange 类型有效)。</summary>
        public float BPM { get; set; }

        /// <summary>拍号分子 (仅 TimeSignature 有效)。</summary>
        public int Numerator { get; set; } = 4;

        /// <summary>拍号分母 (仅 TimeSignature 有效)。</summary>
        public int Denominator { get; set; } = 4;

        /// <summary>自定义数据。</summary>
        public Dictionary<string, string> UserData { get; } = new();

        /// <summary>是否已触发。</summary>
        public bool Fired { get; set; }

        /// <summary>触发回调。</summary>
        public event Action<AudioSyncMarker>? OnFire;

        /// <summary>触发此标记。</summary>
        public void Fire()
        {
            Fired = true;
            OnFire?.Invoke(this);
        }

        /// <summary>重置触发状态。</summary>
        public void Reset()
        {
            Fired = false;
        }
    }

    // ──────────────────────────────────────────────────────────────────────
    //  AudioBeatTracker — 节拍追踪器
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>节拍回调委托。</summary>
    public delegate void BeatCallback(int beat, int bar, double timePosition);

    /// <summary>节拍追踪器。</summary>
    public sealed class AudioBeatTracker
    {
        /// <summary>当前 BPM。</summary>
        public float CurrentBPM { get; set; } = 120f;

        /// <summary>拍号分子。</summary>
        public int Numerator { get; set; } = 4;

        /// <summary>拍号分母。</summary>
        public int Denominator { get; set; } = 4;

        /// <summary>当前拍数 (0-based)。</summary>
        public int CurrentBeat { get; private set; }

        /// <summary>当前小节。</summary>
        public int CurrentBar { get; private set; }

        /// <summary>当前播放位置 (秒)。</summary>
        public double CurrentTime { get; private set; }

        /// <summary>上一拍时间。</summary>
        public double LastBeatTime { get; private set; }

        /// <summary>下一拍时间 (预判)。</summary>
        public double NextBeatTime
        {
            get
            {
                float beatDuration = 60f / Math.Max(1f, CurrentBPM);
                return LastBeatTime + beatDuration;
            }
        }

        /// <summary>是否正在播放。</summary>
        public bool IsPlaying { get; set; }

        /// <summary>起始偏移 (秒)。</summary>
        public double StartOffset { get; set; }

        // 回调
        public event BeatCallback? OnBeat;
        public event Action<int, int, double>? OnBar;  // (bar, beat, time)
        public event Action? OnBeatMissed;

        private int _totalBeats;
        private float _prevBPM;
        private bool _started;

        public AudioBeatTracker() { }

        /// <summary>设置速度。</summary>
        public void SetBPM(float bpm)
        {
            if (Math.Abs(bpm - CurrentBPM) > 0.001f)
            {
                _prevBPM = CurrentBPM;
                CurrentBPM = bpm;
            }
        }

        /// <summary>重置。</summary>
        public void Reset()
        {
            CurrentBeat = 0;
            CurrentBar = 0;
            CurrentTime = StartOffset;
            LastBeatTime = StartOffset;
            _totalBeats = 0;
            _started = false;
        }

        /// <summary>手动设置位置。</summary>
        public void SetPosition(double timeSeconds)
        {
            CurrentTime = timeSeconds;
            float beatDuration = 60f / Math.Max(1f, CurrentBPM);
            double beatTime = (timeSeconds - StartOffset) / beatDuration;
            _totalBeats = Math.Max(0, (int)Math.Floor(beatTime));
            CurrentBeat = _totalBeats % Numerator;
            CurrentBar = _totalBeats / Numerator;
            LastBeatTime = StartOffset + _totalBeats * beatDuration;
        }

        /// <summary>每帧更新。</summary>
        public void Update(double deltaTime)
        {
            if (!IsPlaying) return;

            CurrentTime += deltaTime;
            float beatDuration = 60f / Math.Max(1f, CurrentBPM);

            // 检测是否有新拍
            while (CurrentTime >= LastBeatTime + beatDuration)
            {
                LastBeatTime += beatDuration;
                _totalBeats++;
                CurrentBeat = _totalBeats % Numerator;
                CurrentBar = _totalBeats / Numerator;

                OnBeat?.Invoke(CurrentBeat, CurrentBar, CurrentTime);

                if (CurrentBeat == 0)
                    OnBar?.Invoke(CurrentBar, 0, CurrentTime);
            }
        }

        /// <summary>获取指定时间对应的拍号信息。</summary>
        public (int bar, int beat, double time) GetBeatInfo(double time)
        {
            float beatDuration = 60f / Math.Max(1f, CurrentBPM);
            double beatTime = (time - StartOffset) / beatDuration;
            int totalBeats = Math.Max(0, (int)Math.Floor(beatTime));
            return (totalBeats / Numerator, totalBeats % Numerator, time);
        }

        /// <summary>获取从当前时间到下一拍的时间。</summary>
        public double GetTimeToNextBeat()
        {
            float beatDuration = 60f / Math.Max(1f, CurrentBPM);
            double nextBeat = LastBeatTime + beatDuration;
            return Math.Max(0, nextBeat - CurrentTime);
        }

        /// <summary>获取从当前时间到下一小节的时间。</summary>
        public double GetTimeToNextBar()
        {
            float beatDuration = 60f / Math.Max(1f, CurrentBPM);
            int beatsToNextBar = Numerator - CurrentBeat;
            if (beatsToNextBar == Numerator && CurrentBeat == 0)
                beatsToNextBar = Numerator;
            return beatsToNextBar * beatDuration - (CurrentTime - LastBeatTime);
        }
    }

    // ──────────────────────────────────────────────────────────────────────
    //  AudioTempoMap — 速度图
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>速度变化事件。</summary>
    public sealed class TempoChangeEvent
    {
        /// <summary>变化时间位置 (秒)。</summary>
        public double TimePosition { get; set; }

        /// <summary>新的 BPM。</summary>
        public float BPM { get; set; }

        /// <summary>拍号分子。</summary>
        public int Numerator { get; set; } = 4;

        /// <summary>拍号分母。</summary>
        public int Denominator { get; set; } = 4;
    }

    /// <summary>速度图 (BPM + 拍号随时间变化)。</summary>
    public sealed class AudioTempoMap
    {
        /// <summary>全局默认 BPM。</summary>
        public float DefaultBPM { get; set; } = 120f;

        /// <summary>全局默认拍号分子。</summary>
        public int DefaultNumerator { get; set; } = 4;

        /// <summary>全局默认拍号分母。</summary>
        public int DefaultDenominator { get; set; } = 4;

        /// <summary>速度变化事件列表 (按时间排序)。</summary>
        public List<TempoChangeEvent> TempoChanges { get; } = new();

        /// <summary>添加速度变化事件。</summary>
        public void AddTempoChange(double timePosition, float bpm, int numerator = 4, int denominator = 4)
        {
            var evt = new TempoChangeEvent
            {
                TimePosition = timePosition,
                BPM = bpm,
                Numerator = numerator,
                Denominator = denominator
            };
            TempoChanges.Add(evt);
            TempoChanges.Sort((a, b) => a.TimePosition.CompareTo(b.TimePosition));
        }

        /// <summary>获取指定时间的 BPM。</summary>
        public float GetBPMAt(double time)
        {
            float bpm = DefaultBPM;
            foreach (var change in TempoChanges)
            {
                if (change.TimePosition <= time)
                    bpm = change.BPM;
                else
                    break;
            }
            return bpm;
        }

        /// <summary>获取指定时间的拍号。</summary>
        public (int numerator, int denominator) GetTimeSignatureAt(double time)
        {
            int num = DefaultNumerator, den = DefaultDenominator;
            foreach (var change in TempoChanges)
            {
                if (change.TimePosition <= time)
                {
                    num = change.Numerator;
                    den = change.Denominator;
                }
                else break;
            }
            return (num, den);
        }

        /// <summary>计算从 start 到 end 的节拍数 (考虑 BPM 变化)。</summary>
        public double CalculateBeatRange(double start, double end)
        {
            double totalBeats = 0f;
            double currentTime = start;
            float currentBPM = GetBPMAt(start);

            foreach (var change in TempoChanges)
            {
                if (change.TimePosition > start && change.TimePosition < end)
                {
                    float beatDuration = 60f / Math.Max(1f, currentBPM);
                    double segmentBeats = (change.TimePosition - currentTime) / beatDuration;
                    totalBeats += segmentBeats;
                    currentTime = change.TimePosition;
                    currentBPM = change.BPM;
                }
            }

            float finalBeatDuration = 60f / Math.Max(1f, currentBPM);
            totalBeats += (end - currentTime) / finalBeatDuration;

            return totalBeats;
        }
    }

    // ──────────────────────────────────────────────────────────────────────
    //  AudioTimeline — 音频时间线
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>时间线事件 (标记 -> 动作)。</summary>
    public sealed class TimelineEvent
    {
        /// <summary>触发时间 (秒)。</summary>
        public double TriggerTime { get; set; }

        /// <summary>关联的标记。</summary>
        public AudioSyncMarker? Marker { get; set; }

        /// <summary>回调动作。</summary>
        public Action<double>? Action { get; set; }

        /// <summary>是否已触发。</summary>
        public bool Fired { get; set; }

        /// <summary>是否循环触发 (每次播放到此处都触发)。</summary>
        public bool LoopTrigger { get; set; } = true;
    }

    /// <summary>音频时间线，将标记点映射到游戏逻辑/动画/灯光触发。</summary>
    public sealed class AudioTimeline
    {
        /// <summary>时间线名称。</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>时间线上的事件列表。</summary>
        public List<TimelineEvent> Events { get; } = new();

        /// <summary>当前播放位置 (秒)。</summary>
        public double CurrentTime { get; set; }

        /// <summary>时长 (秒)。</summary>
        public double Duration { get; set; }

        /// <summary>是否循环播放。</summary>
        public bool Looping { get; set; }

        /// <summary>是否正在播放。</summary>
        public bool IsPlaying { get; set; }

        /// <summary>时间线完成回调。</summary>
        public event Action? OnComplete;

        /// <summary>添加事件。</summary>
        public void AddEvent(double triggerTime, Action<double> action, AudioSyncMarker? marker = null)
        {
            var evt = new TimelineEvent
            {
                TriggerTime = triggerTime,
                Action = action,
                Marker = marker
            };
            Events.Add(evt);
            Events.Sort((a, b) => a.TriggerTime.CompareTo(b.TriggerTime));
        }

        /// <summary>每帧更新。</summary>
        public void Update(double deltaTime)
        {
            if (!IsPlaying) return;

            CurrentTime += deltaTime;

            // 触发到达的事件
            foreach (var evt in Events)
            {
                if (!evt.Fired && CurrentTime >= evt.TriggerTime)
                {
                    evt.Fired = true;
                    evt.Action?.Invoke(CurrentTime);
                    evt.Marker?.Fire();
                }
            }

            // 检查结束
            if (Duration > 0 && CurrentTime >= Duration)
            {
                if (Looping)
                {
                    Reset();
                    CurrentTime = 0;
                }
                else
                {
                    IsPlaying = false;
                    OnComplete?.Invoke();
                }
            }
        }

        /// <summary>播放。</summary>
        public void Play()
        {
            IsPlaying = true;
        }

        /// <summary>停止。</summary>
        public void Stop()
        {
            IsPlaying = false;
            Reset();
        }

        /// <summary>重置所有事件的触发状态。</summary>
        public void Reset()
        {
            CurrentTime = 0;
            foreach (var evt in Events)
                evt.Fired = false;
        }

        /// <summary>查找指定时间之后的下一个事件。</summary>
        public TimelineEvent? GetNextEvent(double afterTime)
        {
            foreach (var evt in Events)
            {
                if (evt.TriggerTime > afterTime)
                    return evt;
            }
            return null;
        }
    }

    // ──────────────────────────────────────────────────────────────────────
    //  AudioSyncCallback — 同步回调管理
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>同步回调类型。</summary>
    public enum SyncCallbackType
    {
        Beat,
        Bar,
        Marker,
        BPMChange,
        TimeSignature,
        Custom
    }

    /// <summary>同步回调注册项。</summary>
    public sealed class SyncCallbackEntry
    {
        /// <summary>回调类型。</summary>
        public SyncCallbackType Type { get; set; }

        /// <summary>筛选条件 (如标记名称, Beat 编号等)。</summary>
        public string? Filter { get; set; }

        /// <summary>回调函数。</summary>
        public Action<double, string>? Callback { get; set; }

        /// <summary>是否只触发一次。</summary>
        public bool Once { get; set; }

        /// <summary>是否已触发 (仅 Once 模式有效)。</summary>
        public bool Fired { get; set; }
    }

    /// <summary>同步回调管理器。</summary>
    public sealed class AudioSyncCallbackManager
    {
        private readonly List<SyncCallbackEntry> _callbacks = new();

        /// <summary>注册回调。</summary>
        public void Register(SyncCallbackType type, Action<double, string> callback, string? filter = null, bool once = false)
        {
            _callbacks.Add(new SyncCallbackEntry
            {
                Type = type,
                Callback = callback,
                Filter = filter,
                Once = once
            });
        }

        /// <summary>触发指定类型的回调。</summary>
        public void Fire(SyncCallbackType type, double time, string info = "")
        {
            for (int i = _callbacks.Count - 1; i >= 0; i--)
            {
                var entry = _callbacks[i];
                if (entry.Type != type) continue;
                if (entry.Filter != null && entry.Filter != info) continue;
                if (entry.Once && entry.Fired) continue;

                entry.Callback?.Invoke(time, info);
                if (entry.Once)
                {
                    entry.Fired = true;
                    _callbacks.RemoveAt(i);
                }
            }
        }

        /// <summary>清除所有回调。</summary>
        public void Clear()
        {
            _callbacks.Clear();
        }

        /// <summary>按类型清除。</summary>
        public void ClearByType(SyncCallbackType type)
        {
            _callbacks.RemoveAll(c => c.Type == type);
        }
    }

    // ──────────────────────────────────────────────────────────────────────
    //  MIDISync — MIDI 同步
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>MIDI 同步系统。</summary>
    public sealed class MIDISync
    {
        /// <summary>时钟模式。</summary>
        public MIDIClockMode ClockMode { get; set; } = MIDIClockMode.Off;

        /// <summary>MIDI 时钟速率 (24 PPQ - 每四分音符 24 个时钟)。</summary>
        public const int PPQN = 24;

        /// <summary>当前 BPM (通过 MIDI 同步)。</summary>
        public float CurrentBPM { get; private set; } = 120f;

        /// <summary>SPP (Song Position Pointer, 以 16 分音符为单位)。</summary>
        public int SongPosition { get; private set; }

        /// <summary>当前 MIDI 钟计数。</summary>
        private int _clockCount;

        /// <summary>MIDI Start 回调。</summary>
        public event Action? OnStart;

        /// <summary>MIDI Stop 回调。</summary>
        public event Action? OnStop;

        /// <summary>MIDI Continue 回调。</summary>
        public event Action? OnContinue;

        /// <summary>MIDI 时钟 Tick 回调。</summary>
        public event Action? OnClockTick;

        /// <summary>SPP 变更回调。</summary>
        public event Action<int>? OnSongPositionPointer;

        /// <summary>处理 MIDI Start 消息。</summary>
        public void HandleStart()
        {
            _clockCount = 0;
            SongPosition = 0;
            OnStart?.Invoke();
        }

        /// <summary>处理 MIDI Stop 消息。</summary>
        public void HandleStop()
        {
            OnStop?.Invoke();
        }

        /// <summary>处理 MIDI Continue 消息。</summary>
        public void HandleContinue()
        {
            OnContinue?.Invoke();
        }

        /// <summary>处理 MIDI Clock Tick。</summary>
        public void HandleClockTick()
        {
            _clockCount++;
            if (_clockCount % PPQN == 0)
            {
                // 每四分音符
                SongPosition++;
            }
            OnClockTick?.Invoke();
        }

        /// <summary>处理 Song Position Pointer。</summary>
        public void HandleSPP(int position)
        {
            SongPosition = position;
            _clockCount = position * 6; // 16 分音符 = 6 个时钟
            OnSongPositionPointer?.Invoke(position);
        }

        /// <summary>通过 MIDI 时钟间隔计算 BPM。</summary>
        public void UpdateBPMFromClockInterval(double secondsPerClock)
        {
            if (secondsPerClock > 0)
                CurrentBPM = 60f / (float)(PPQN * secondsPerClock);
        }
    }

    // ──────────────────────────────────────────────────────────────────────
    //  SMPTE 时间码
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>SMPTE 时间码。</summary>
    public readonly struct SMPTETimeCode : IEquatable<SMPTETimeCode>
    {
        /// <summary>小时。</summary>
        public readonly int Hours;
        /// <summary>分钟。</summary>
        public readonly int Minutes;
        /// <summary>秒。</summary>
        public readonly int Seconds;
        /// <summary>帧。</summary>
        public readonly int Frames;

        public SMPTETimeCode(int hours, int minutes, int seconds, int frames)
        {
            Hours = hours; Minutes = minutes; Seconds = seconds; Frames = frames;
        }

        /// <summary>转换为总帧数。</summary>
        public long ToTotalFrames(SMPTEFormat format)
        {
            float fps = GetFPS(format);
            return (long)(Hours * 3600 * fps + Minutes * 60 * fps + Seconds * fps + Frames);
        }

        /// <summary>转换为总秒数。</summary>
        public double ToTotalSeconds(SMPTEFormat format)
        {
            return ToTotalFrames(format) / GetFPS(format);
        }

        /// <summary>从总帧数创建。</summary>
        public static SMPTETimeCode FromTotalFrames(long totalFrames, SMPTEFormat format)
        {
            float fps = GetFPS(format);
            int hours = (int)(totalFrames / (3600 * fps));
            totalFrames -= (long)(hours * 3600 * fps);
            int minutes = (int)(totalFrames / (60 * fps));
            totalFrames -= (long)(minutes * 60 * fps);
            int seconds = (int)(totalFrames / fps);
            int frames = (int)(totalFrames - seconds * fps);
            return new SMPTETimeCode(hours, minutes, seconds, frames);
        }

        public override string ToString()
        {
            return $"{Hours:D2}:{Minutes:D2}:{Seconds:D2}:{Frames:D2}";
        }

        public bool Equals(SMPTETimeCode other) =>
            Hours == other.Hours && Minutes == other.Minutes && Seconds == other.Seconds && Frames == other.Frames;

        public override bool Equals(object? obj) => obj is SMPTETimeCode tc && Equals(tc);
        public override int GetHashCode() => HashCode.Combine(Hours, Minutes, Seconds, Frames);

        internal static float GetFPS(SMPTEFormat format) => format switch
        {
            SMPTEFormat.FPS24 => 24f,
            SMPTEFormat.FPS25 => 25f,
            SMPTEFormat.FPS29_97DF => 29.97f,
            SMPTEFormat.FPS30 => 30f,
            SMPTEFormat.FPS30DF => 29.97f,
            _ => 30f
        };
    }

    /// <summary>SMPTE/LTC/VITC 时间码同步器。</summary>
    public sealed class SMPTEClock
    {
        /// <summary>时间码格式。</summary>
        public SMPTEFormat Format { get; set; } = SMPTEFormat.FPS30;

        /// <summary>当前时间码。</summary>
        public SMPTETimeCode CurrentTimeCode { get; private set; }

        /// <summary>是否正在运行。</summary>
        public bool IsRunning { get; private set; }

        /// <summary>帧率。</summary>
        public float FPS => SMPTETimeCode.GetFPS(Format);

        private double _elapsedSeconds;

        /// <summary>开始。</summary>
        public void Start(SMPTETimeCode startTimeCode)
        {
            CurrentTimeCode = startTimeCode;
            _elapsedSeconds = 0;
            IsRunning = true;
        }

        /// <summary>停止。</summary>
        public void Stop()
        {
            IsRunning = false;
        }

        /// <summary>每帧更新。</summary>
        public void Update(double deltaTime)
        {
            if (!IsRunning) return;
            _elapsedSeconds += deltaTime;
            long totalFrames = (long)(_elapsedSeconds * FPS);
            long startFrames = CurrentTimeCode.ToTotalFrames(Format);
            CurrentTimeCode = SMPTETimeCode.FromTotalFrames(startFrames + totalFrames, Format);
        }

        /// <summary>从秒数创建 SMPTE 时间码。</summary>
        public static SMPTETimeCode FromSeconds(double seconds, SMPTEFormat format)
        {
            float fps = SMPTETimeCode.GetFPS(format);
            long totalFrames = (long)(seconds * fps);
            return SMPTETimeCode.FromTotalFrames(totalFrames, format);
        }
    }
}
