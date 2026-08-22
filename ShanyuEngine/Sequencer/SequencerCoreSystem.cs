using System;
using System.Collections.Generic;
using System.Numerics;

namespace ShanyuEngine.Sequencer
{
    // ════════════════════════════════════════════════════════════════════════
    // 山雨引擎 - 序列器核心系统 (Sequencer Core System)
    // --------------------------------------------------------------------------
    // 提供序列器框架的基础设施：序列定义、时间管理、播放控制、绑定、标记。
    // 对标 UE5 Sequencer Core / Unity Timeline PlayableDirector。
    // ════════════════════════════════════════════════════════════════════════

    // ──────────────────────────────────────────────────────────────────────────
    // 枚举定义
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>序列播放状态枚举，覆盖所有可能的播放模式。</summary>
    public enum SequencePlaybackState
    {
        /// <summary>已停止，播放头回到起始位置。</summary>
        Stopped,
        /// <summary>正在播放中。</summary>
        Playing,
        /// <summary>暂停中，播放头停留当前位置。</summary>
        Paused,
        /// <summary>拖拽/梳理模式，用户手动拖动播放头。</summary>
        Scrubbing,
        /// <summary>正向播放。</summary>
        Forward,
        /// <summary>反向播放。</summary>
        Backward
    }

    /// <summary>循环播放模式。</summary>
    public enum SequenceLoopMode
    {
        /// <summary>不循环。</summary>
        None,
        /// <summary>循环播放。</summary>
        Loop,
        /// <summary>乒乓循环：正播到末尾后反向播放到起始。</summary>
        PingPong
    }

    /// <summary>序列标记类型。</summary>
    public enum SequenceMarkerType
    {
        /// <summary>事件标记，触发回调。</summary>
        Event,
        /// <summary>注释标记，仅用于编辑器内标注说明。</summary>
        Comment,
        /// <summary>调试标记，运行时调试辅助。</summary>
        Debug,
        /// <summary>章节标记，用于划分序列章节。</summary>
        Chapter
    }

    /// <summary>绑定类型。</summary>
    public enum BindingType
    {
        /// <summary>静态绑定：编译时确定绑定目标。</summary>
        Static,
        /// <summary>运行时绑定：播放时动态解析。</summary>
        Runtime
    }

    // ──────────────────────────────────────────────────────────────────────────
    // SequencerTime - 时间管理工具
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 序列器时间管理工具。
    /// 提供帧-时间双向转换、帧率转换、子帧精度支持。
    /// </summary>
    public static class SequencerTime
    {
        /// <summary>帧→时间（秒）。</summary>
        public static double FrameToTime(int frame, double frameRate)
        {
            if (frameRate <= 0) throw new ArgumentOutOfRangeException(nameof(frameRate));
            return frame / frameRate;
        }

        /// <summary>时间（秒）→帧（四舍五入到最近整数帧）。</summary>
        public static int TimeToFrame(double time, double frameRate)
        {
            if (frameRate <= 0) throw new ArgumentOutOfRangeException(nameof(frameRate));
            return (int)Math.Round(time * frameRate);
        }

        /// <summary>时间（秒）→帧（向下取整）。</summary>
        public static int TimeToFloorFrame(double time, double frameRate)
        {
            if (frameRate <= 0) throw new ArgumentOutOfRangeException(nameof(frameRate));
            return (int)Math.Floor(time * frameRate);
        }

        /// <summary>时间（秒）→帧（向上取整）。</summary>
        public static int TimeToCeilFrame(double time, double frameRate)
        {
            if (frameRate <= 0) throw new ArgumentOutOfRangeException(nameof(frameRate));
            return (int)Math.Ceiling(time * frameRate);
        }

        /// <summary>获取子帧偏移（0.0~1.0），表示当前时间在两帧之间的位置。</summary>
        public static double GetSubFrame(double time, double frameRate)
        {
            if (frameRate <= 0) throw new ArgumentOutOfRangeException(nameof(frameRate));
            double frame = time * frameRate;
            return frame - Math.Floor(frame);
        }

        /// <summary>帧率转换：将帧号从源帧率转换为目标帧率。</summary>
        public static double ConvertFrameRate(int sourceFrame, double sourceRate, double targetRate)
        {
            if (sourceRate <= 0 || targetRate <= 0)
                throw new ArgumentOutOfRangeException("帧率必须大于零");
            return sourceFrame * (targetRate / sourceRate);
        }

        /// <summary>将浮点时间钳制在 [0, duration] 范围内。</summary>
        public static double ClampTime(double time, double duration)
        {
            return Math.Clamp(time, 0, Math.Max(0, duration));
        }

        /// <summary>将时间按循环模式处理，返回有效播放时间。</summary>
        public static double ApplyLoopMode(double time, double duration, SequenceLoopMode loopMode)
        {
            if (duration <= 0) return 0;

            switch (loopMode)
            {
                case SequenceLoopMode.None:
                    return ClampTime(time, duration);

                case SequenceLoopMode.Loop:
                {
                    double wrapped = time % duration;
                    return wrapped < 0 ? wrapped + duration : wrapped;
                }

                case SequenceLoopMode.PingPong:
                {
                    double cycle = duration * 2;
                    double wrapped = ((time % cycle) + cycle) % cycle;
                    return wrapped <= duration ? wrapped : cycle - wrapped;
                }

                default:
                    return ClampTime(time, duration);
            }
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // PlayRange - 播放范围
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>播放范围定义，限制序列播放的时间区间。</summary>
    public class PlayRange
    {
        /// <summary>播放起始时间（秒）。</summary>
        public double StartTime { get; set; }

        /// <summary>播放结束时间（秒）。</summary>
        public double EndTime { get; set; }

        /// <summary>循环范围起始。</summary>
        public double LoopStartTime { get; set; }

        /// <summary>循环范围结束。</summary>
        public double LoopEndTime { get; set; }

        /// <summary>是否启用播放范围限制。</summary>
        public bool IsEnabled { get; set; }

        /// <summary>范围时长。</summary>
        public double Duration => Math.Max(0, EndTime - StartTime);

        public PlayRange()
        {
            StartTime = 0;
            EndTime = 0;
            LoopStartTime = 0;
            LoopEndTime = 0;
            IsEnabled = false;
        }

        public PlayRange(double startTime, double endTime)
        {
            StartTime = Math.Min(startTime, endTime);
            EndTime = Math.Max(startTime, endTime);
            LoopStartTime = StartTime;
            LoopEndTime = EndTime;
            IsEnabled = true;
        }

        /// <summary>将时间钳制到播放范围内。</summary>
        public double Clamp(double time)
        {
            if (!IsEnabled) return time;
            return Math.Clamp(time, StartTime, EndTime);
        }

        /// <summary>检查时间是否在播放范围内。</summary>
        public bool Contains(double time)
        {
            if (!IsEnabled) return true;
            return time >= StartTime && time <= EndTime;
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // SequenceMarker - 序列标记
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>序列标记：在时间轴特定位置放置的注记点。</summary>
    public class SequenceMarker
    {
        /// <summary>标记名称。</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>标记所在时间点（秒）。</summary>
        public double Time { get; set; }

        /// <summary>标记颜色（ARGB 格式）。</summary>
        public uint Color { get; set; } = 0xFFFFFFFF;

        /// <summary>标记类型。</summary>
        public SequenceMarkerType MarkerType { get; set; } = SequenceMarkerType.Event;

        /// <summary>关联的数据（自定义参数、脚本引用等）。</summary>
        public object? UserData { get; set; }

        /// <summary>标记触发时回调。</summary>
        public Action<SequenceMarker>? OnTrigger { get; set; }

        public SequenceMarker() { }

        public SequenceMarker(string name, double time, SequenceMarkerType type = SequenceMarkerType.Event)
        {
            Name = name;
            Time = time;
            MarkerType = type;
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // SequencerBinding - 绑定系统
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 序列器绑定：将轨道连接到运行时对象。
    /// 支持静态绑定（编辑时确定）和运行时绑定（动态解析）。
    /// </summary>
    public class SequencerBinding
    {
        /// <summary>绑定类型。</summary>
        public BindingType Type { get; set; } = BindingType.Static;

        /// <summary>绑定的对象引用（静态绑定时有效）。</summary>
        public object? BoundObject { get; set; }

        /// <summary>绑定的对象名称（运行时绑定时用于查找）。</summary>
        public string ObjectName { get; set; } = string.Empty;

        /// <summary>绑定的属性路径（如 "Transform.Position"）。</summary>
        public string PropertyPath { get; set; } = string.Empty;

        /// <summary>绑定的轨道。</summary>
        public SequenceTrack? Track { get; set; }

        /// <summary>运行时解析委托：根据上下文解析绑定目标。</summary>
        public Func<object?, object?>? RuntimeResolver { get; set; }

        /// <summary>绑定是否已解析。</summary>
        public bool IsResolved { get; private set; }

        public SequencerBinding() { }

        public SequencerBinding(object boundObject, string propertyPath = "")
        {
            Type = BindingType.Static;
            BoundObject = boundObject;
            PropertyPath = propertyPath;
            IsResolved = boundObject != null;
        }

        public SequencerBinding(string objectName, string propertyPath, Func<object?, object?> resolver)
        {
            Type = BindingType.Runtime;
            ObjectName = objectName;
            PropertyPath = propertyPath;
            RuntimeResolver = resolver;
        }

        /// <summary>解析绑定，返回绑定的目标对象。</summary>
        public object? Resolve(object? context)
        {
            if (Type == BindingType.Static)
            {
                IsResolved = BoundObject != null;
                return BoundObject;
            }

            if (RuntimeResolver != null)
            {
                BoundObject = RuntimeResolver(context);
                IsResolved = BoundObject != null;
                return BoundObject;
            }

            IsResolved = false;
            return null;
        }

        /// <summary>重置绑定状态（用于重新解析）。</summary>
        public void Reset()
        {
            if (Type == BindingType.Runtime)
            {
                BoundObject = null;
                IsResolved = false;
            }
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // SequenceSection - 片段基类
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 序列片段基类：时间轴上的一段可放置内容。
    /// 类似 UE 的 UMovieSceneSection / Unity Timeline 中的 Clip。
    /// </summary>
    public abstract class SequenceSection
    {
        /// <summary>片段唯一标识。</summary>
        public Guid Id { get; } = Guid.NewGuid();

        /// <summary>片段显示名称。</summary>
        public string Name { get; set; } = "Section";

        /// <summary>片段起始时间（秒）。</summary>
        public double StartTime { get; set; }

        /// <summary>片段结束时间（秒）。</summary>
        public double EndTime { get; set; }

        /// <summary>片段时长。</summary>
        public double Duration => Math.Max(0, EndTime - StartTime);

        /// <summary>所在轨道引用。</summary>
        public SequenceTrack? OwnerTrack { get; set; }

        /// <summary>是否循环播放。</summary>
        public bool IsLooping { get; set; }

        /// <summary>是否可混合（同一轨道多片段重叠时是否参与混合）。</summary>
        public virtual bool CanBlend { get; set; } = false;

        /// <summary>混合权重（0.0~1.0）。</summary>
        public float BlendWeight { get; set; } = 1.0f;

        /// <summary>是否启用。</summary>
        public bool IsEnabled { get; set; } = true;

        /// <summary>是否锁定（锁定后不可编辑）。</summary>
        public bool IsLocked { get; set; }

        /// <summary>显示颜色。</summary>
        public uint Color { get; set; } = 0xFF4488CC;

        /// <summary>检查时间是否在此片段范围内。</summary>
        public bool ContainsTime(double time)
        {
            return time >= StartTime && time <= EndTime;
        }

        /// <summary>获取片段内的局部时间（相对起始的偏移）。</summary>
        public double GetLocalTime(double globalTime)
        {
            if (!ContainsTime(globalTime)) return -1;
            double local = globalTime - StartTime;

            if (IsLooping && Duration > 0)
            {
                local = local % Duration;
            }

            return local;
        }

        /// <summary>
        /// 在指定时间评估此片段。子类实现具体的评估逻辑。
        /// </summary>
        /// <param name="globalTime">全局时间（秒）。</param>
        /// <param name="context">评估上下文。</param>
        public abstract void Evaluate(double globalTime, object? context);

        /// <summary>片段开始播放时调用。</summary>
        public virtual void OnEnter() { }

        /// <summary>片段播放结束时调用。</summary>
        public virtual void OnExit() { }

        /// <summary>在播放头越过片段内某个时间点时调用（用于触发内部事件）。</summary>
        public virtual void OnTick(double localTime) { }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // SequenceTrack - 轨道基类
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 序列轨道基类：承载同类型片段的水平通道。
    /// 类似 UE 的 UMovieSceneTrack / Unity Timeline Track。
    /// </summary>
    public abstract class SequenceTrack
    {
        /// <summary>轨道唯一标识。</summary>
        public Guid Id { get; } = Guid.NewGuid();

        /// <summary>轨道显示名称。</summary>
        public string Name { get; set; } = "Track";

        /// <summary>轨道类型标识（子类重写）。</summary>
        public abstract string TrackTypeName { get; }

        /// <summary>绑定的对象引用。</summary>
        public object? BoundObject { get; set; }

        /// <summary>绑定信息。</summary>
        public SequencerBinding? Binding { get; set; }

        /// <summary>轨道是否可见（编辑器中是否显示）。</summary>
        public bool IsVisible { get; set; } = true;

        /// <summary>轨道是否锁定（锁定后不可编辑）。</summary>
        public bool IsLocked { get; set; }

        /// <summary>轨道是否静音（静音后不参与评估）。</summary>
        public bool IsMuted { get; set; }

        /// <summary>轨道优先级（数值越大优先级越高，用于混合排序）。</summary>
        public int Priority { get; set; }

        /// <summary>轨道显示颜色。</summary>
        public uint Color { get; set; } = 0xFF6688AA;

        /// <summary>所属序列引用。</summary>
        public Sequence? OwnerSequence { get; set; }

        /// <summary>轨道上的所有片段。</summary>
        private readonly List<SequenceSection> _sections = new();

        /// <summary>获取所有片段（只读）。</summary>
        public IReadOnlyList<SequenceSection> Sections => _sections;

        /// <summary>片段数量。</summary>
        public int SectionCount => _sections.Count;

        public SequenceTrack() { }

        /// <summary>添加片段到轨道。</summary>
        public void AddSection(SequenceSection section)
        {
            if (section == null) throw new ArgumentNullException(nameof(section));
            section.OwnerTrack = this;
            _sections.Add(section);
            _sections.Sort((a, b) => a.StartTime.CompareTo(b.StartTime));
        }

        /// <summary>从轨道中移除片段。</summary>
        public bool RemoveSection(SequenceSection section)
        {
            if (section == null) return false;
            if (_sections.Remove(section))
            {
                section.OwnerTrack = null;
                return true;
            }
            return false;
        }

        /// <summary>按 ID 移除片段。</summary>
        public bool RemoveSectionById(Guid sectionId)
        {
            for (int i = 0; i < _sections.Count; i++)
            {
                if (_sections[i].Id == sectionId)
                {
                    _sections[i].OwnerTrack = null;
                    _sections.RemoveAt(i);
                    return true;
                }
            }
            return false;
        }

        /// <summary>获取指定时间范围内活跃的片段列表。</summary>
        public List<SequenceSection> GetActiveSections(double time)
        {
            var result = new List<SequenceSection>();
            if (IsMuted) return result;

            foreach (var section in _sections)
            {
                if (section.IsEnabled && section.ContainsTime(time))
                {
                    result.Add(section);
                }
            }
            return result;
        }

        /// <summary>获取轨道的时间范围（最早片段起始到最晚片段结束）。</summary>
        public (double startTime, double endTime) GetTimeRange()
        {
            if (_sections.Count == 0) return (0, 0);

            double start = double.MaxValue;
            double end = double.MinValue;

            foreach (var section in _sections)
            {
                if (section.StartTime < start) start = section.StartTime;
                if (section.EndTime > end) end = section.EndTime;
            }

            return (start, end);
        }

        /// <summary>在指定时间评估轨道（评估所有活跃片段并混合）。</summary>
        public virtual void Evaluate(double time, object? context)
        {
            if (IsMuted) return;

            var activeSections = GetActiveSections(time);
            if (activeSections.Count == 0) return;

            // 如果只有一个活跃片段，直接评估
            if (activeSections.Count == 1)
            {
                activeSections[0].Evaluate(time, context);
                return;
            }

            // 多片段重叠，计算混合权重后评估
            EvaluateWithBlending(activeSections, time, context);
        }

        /// <summary>多片段混合评估（子类可重写自定义混合逻辑）。</summary>
        protected virtual void EvaluateWithBlending(List<SequenceSection> sections, double time, object? context)
        {
            foreach (var section in sections)
            {
                section.Evaluate(time, context);
            }
        }

        /// <summary>轨道开始播放时调用。</summary>
        public virtual void OnBeginPlay() { }

        /// <summary>轨道停止播放时调用。</summary>
        public virtual void OnStop() { }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Sequence - 序列定义
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 序列：一条完整的时间轴，包含多个轨道、标记和绑定。
    /// 类似 UE 的 ULevelSequence / Unity Timeline Asset。
    /// </summary>
    public class Sequence
    {
        /// <summary>序列唯一标识。</summary>
        public Guid Id { get; } = Guid.NewGuid();

        /// <summary>序列名称。</summary>
        public string Name { get; set; } = "Untitled Sequence";

        /// <summary>序列总时长（秒）。</summary>
        public double Duration { get; set; } = 10.0;

        /// <summary>序列帧率（帧/秒）。</summary>
        public double FrameRate { get; set; } = 30.0;

        /// <summary>当前播放状态。</summary>
        public SequencePlaybackState PlaybackState { get; private set; } = SequencePlaybackState.Stopped;

        /// <summary>当前播放时间（秒）。</summary>
        public double CurrentTime { get; private set; }

        /// <summary>当前帧号。</summary>
        public int CurrentFrame => SequencerTime.TimeToFrame(CurrentTime, FrameRate);

        /// <summary>播放速率倍率（1.0 = 正常速度）。</summary>
        public double PlaybackRate { get; set; } = 1.0;

        /// <summary>循环模式。</summary>
        public SequenceLoopMode LoopMode { get; set; } = SequenceLoopMode.None;

        /// <summary>播放范围。</summary>
        public PlayRange PlayRange { get; set; } = new PlayRange();

        /// <summary>轨道列表。</summary>
        private readonly List<SequenceTrack> _tracks = new();

        /// <summary>获取所有轨道（只读）。</summary>
        public IReadOnlyList<SequenceTrack> Tracks => _tracks;

        /// <summary>标记列表。</summary>
        private readonly List<SequenceMarker> _markers = new();

        /// <summary>获取所有标记（只读）。</summary>
        public IReadOnlyList<SequenceMarker> Markers => _markers;

        /// <summary>绑定列表。</summary>
        private readonly List<SequencerBinding> _bindings = new();

        /// <summary>获取所有绑定（只读）。</summary>
        public IReadOnlyList<SequencerBinding> Bindings => _bindings;

        /// <summary>是否自动更新时长以适应所有轨道内容。</summary>
        public bool AutoExtendDuration { get; set; } = true;

        /// <summary>播放时间变化事件。</summary>
        public event Action<double>? OnTimeChanged;

        /// <summary>播放状态变化事件。</summary>
        public event Action<SequencePlaybackState, SequencePlaybackState>? OnPlaybackStateChanged;

        /// <summary>序列播放完成事件。</summary>
        public event Action? OnCompleted;

        /// <summary>标记触发事件。</summary>
        public event Action<SequenceMarker>? OnMarkerTriggered;

        /// <summary>已触发的标记集合（避免重复触发）。</summary>
        private readonly HashSet<Guid> _triggeredMarkers = new();

        public Sequence() { }

        public Sequence(string name, double duration = 10.0, double frameRate = 30.0)
        {
            Name = name;
            Duration = duration;
            FrameRate = frameRate;
        }

        // ── 轨道管理 ──────────────────────────────────────────────────

        /// <summary>添加轨道到序列。</summary>
        public void AddTrack(SequenceTrack track)
        {
            if (track == null) throw new ArgumentNullException(nameof(track));
            track.OwnerSequence = this;
            _tracks.Add(track);
            _tracks.Sort((a, b) => a.Priority.CompareTo(b.Priority));
        }

        /// <summary>从序列中移除轨道。</summary>
        public bool RemoveTrack(SequenceTrack track)
        {
            if (track == null) return false;
            if (_tracks.Remove(track))
            {
                track.OwnerSequence = null;
                return true;
            }
            return false;
        }

        /// <summary>按轨道类型查找轨道。</summary>
        public List<T> FindTracksOfType<T>() where T : SequenceTrack
        {
            var result = new List<T>();
            foreach (var track in _tracks)
            {
                if (track is T typed) result.Add(typed);
            }
            return result;
        }

        /// <summary>按名称查找轨道。</summary>
        public SequenceTrack? FindTrackByName(string name)
        {
            foreach (var track in _tracks)
            {
                if (track.Name == name) return track;
            }
            return null;
        }

        // ── 标记管理 ──────────────────────────────────────────────────

        /// <summary>添加标记。</summary>
        public void AddMarker(SequenceMarker marker)
        {
            if (marker == null) throw new ArgumentNullException(nameof(marker));
            _markers.Add(marker);
            _markers.Sort((a, b) => a.Time.CompareTo(b.Time));
        }

        /// <summary>移除标记。</summary>
        public bool RemoveMarker(SequenceMarker marker)
        {
            return marker != null && _markers.Remove(marker);
        }

        /// <summary>获取指定时间附近的标记。</summary>
        public List<SequenceMarker> GetMarkersAtTime(double time, double epsilon = 0.01)
        {
            var result = new List<SequenceMarker>();
            foreach (var marker in _markers)
            {
                if (Math.Abs(marker.Time - time) < epsilon)
                    result.Add(marker);
            }
            return result;
        }

        // ── 绑定管理 ──────────────────────────────────────────────────

        /// <summary>添加绑定。</summary>
        public void AddBinding(SequencerBinding binding)
        {
            if (binding == null) throw new ArgumentNullException(nameof(binding));
            _bindings.Add(binding);
        }

        /// <summary>解析所有运行时绑定。</summary>
        public void ResolveBindings(object? context)
        {
            foreach (var binding in _bindings)
            {
                binding.Resolve(context);
            }
        }

        // ── 序列评估 ──────────────────────────────────────────────────

        /// <summary>在指定时间评估整个序列。</summary>
        public void Evaluate(double time, object? context)
        {
            double evalTime = time;

            if (PlayRange.IsEnabled)
            {
                evalTime = PlayRange.Clamp(evalTime);
            }

            // 评估所有轨道
            foreach (var track in _tracks)
            {
                track.Evaluate(evalTime, context);
            }

            // 触发标记
            EvaluateMarkers(evalTime);
        }

        /// <summary>评估并触发标记。</summary>
        private void EvaluateMarkers(double time)
        {
            foreach (var marker in _markers)
            {
                if (!_triggeredMarkers.Contains(marker.Id) && Math.Abs(marker.Time - time) < 0.005)
                {
                    _triggeredMarkers.Add(marker.Id);
                    marker.OnTrigger?.Invoke(marker);
                    OnMarkerTriggered?.Invoke(marker);
                }
            }

            // 清理已过去的标记
            _triggeredMarkers.RemoveWhere(id =>
            {
                foreach (var m in _markers)
                {
                    if (m.Id == id && m.Time > time) return false;
                }
                return true;
            });
        }

        // ── 内部状态管理 ──────────────────────────────────────────────

        /// <summary>设置当前时间（供控制器使用）。</summary>
        internal void SetCurrentTime(double time)
        {
            CurrentTime = time;
            OnTimeChanged?.Invoke(time);
        }

        /// <summary>设置播放状态。</summary>
        internal void SetPlaybackState(SequencePlaybackState newState)
        {
            var oldState = PlaybackState;
            PlaybackState = newState;
            OnPlaybackStateChanged?.Invoke(oldState, newState);
        }

        /// <summary>触发完成事件。</summary>
        internal void RaiseCompleted()
        {
            OnCompleted?.Invoke();
        }

        /// <summary>更新序列时长以适应所有轨道内容。</summary>
        public void UpdateDurationFromTracks()
        {
            if (!AutoExtendDuration) return;

            double maxEnd = 0;
            foreach (var track in _tracks)
            {
                var (start, end) = track.GetTimeRange();
                if (end > maxEnd) maxEnd = end;
            }

            if (maxEnd > Duration)
            {
                Duration = maxEnd;
            }
        }

        /// <summary>重置序列到初始状态。</summary>
        public void Reset()
        {
            SetCurrentTime(0);
            SetPlaybackState(SequencePlaybackState.Stopped);
            _triggeredMarkers.Clear();
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // SequencerController - 播放控制器
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 序列器播放控制器：管理序列的播放、暂停、停止、跳转等操作。
    /// 类似 UE 的 UMovieScenePlaybackController / Unity PlayableDirector。
    /// </summary>
    public class SequencerController
    {
        /// <summary>控制的序列引用。</summary>
        public Sequence? TargetSequence { get; set; }

        /// <summary>播放速率倍率。</summary>
        public double PlaybackRate
        {
            get => _playbackRate;
            set => _playbackRate = Math.Clamp(value, -10.0, 10.0);
        }
        private double _playbackRate = 1.0;

        /// <summary>循环模式。</summary>
        public SequenceLoopMode LoopMode
        {
            get => _loopMode;
            set => _loopMode = value;
        }
        private SequenceLoopMode _loopMode = SequenceLoopMode.None;

        /// <summary>评估上下文（传递给轨道评估）。</summary>
        public object? EvaluationContext { get; set; }

        /// <summary>播放状态变化事件。</summary>
        public event Action<SequencePlaybackState>? OnPlaybackStateChanged;

        /// <summary>时间更新事件（每帧触发）。</summary>
        public event Action<double>? OnTimeUpdated;

        /// <summary>上一帧已触发的标记时间集合（避免重复触发）。</summary>
        private double _lastEvaluatedTime = -1;

        /// <summary>上一帧激活的片段集合（用于检测片段进入/退出）。</summary>
        private readonly Dictionary<Guid, bool> _sectionActiveState = new();

        public SequencerController() { }

        public SequencerController(Sequence sequence)
        {
            TargetSequence = sequence;
        }

        // ── 播放控制 ──────────────────────────────────────────────────

        /// <summary>开始/继续正向播放。</summary>
        public void Play()
        {
            if (TargetSequence == null) return;
            TargetSequence.SetPlaybackState(SequencePlaybackState.Playing);
            PlaybackRate = Math.Abs(PlaybackRate) * (_playbackRate < 0 ? -1 : 1);
            if (Math.Abs(PlaybackRate) < 0.0001) PlaybackRate = 1.0;
            TargetSequence.PlaybackRate = PlaybackRate;
            OnPlaybackStateChanged?.Invoke(SequencePlaybackState.Playing);
        }

        /// <summary>反向播放。</summary>
        public void PlayBackward()
        {
            if (TargetSequence == null) return;
            PlaybackRate = -Math.Abs(PlaybackRate);
            TargetSequence.PlaybackRate = PlaybackRate;
            TargetSequence.SetPlaybackState(SequencePlaybackState.Backward);
            OnPlaybackStateChanged?.Invoke(SequencePlaybackState.Backward);
        }

        /// <summary>暂停播放。</summary>
        public void Pause()
        {
            if (TargetSequence == null) return;
            TargetSequence.SetPlaybackState(SequencePlaybackState.Paused);
            OnPlaybackStateChanged?.Invoke(SequencePlaybackState.Paused);
        }

        /// <summary>停止播放，回到起始。</summary>
        public void Stop()
        {
            if (TargetSequence == null) return;

            // 触发所有活跃片段的退出
            NotifySectionsExit();

            TargetSequence.SetCurrentTime(0);
            TargetSequence.SetPlaybackState(SequencePlaybackState.Stopped);
            _lastEvaluatedTime = -1;
            _sectionActiveState.Clear();
            OnPlaybackStateChanged?.Invoke(SequencePlaybackState.Stopped);
        }

        /// <summary>切换播放/暂停。</summary>
        public void TogglePlayPause()
        {
            if (TargetSequence == null) return;
            if (TargetSequence.PlaybackState == SequencePlaybackState.Playing ||
                TargetSequence.PlaybackState == SequencePlaybackState.Backward)
            {
                Pause();
            }
            else
            {
                Play();
            }
        }

        /// <summary>跳转到指定时间（秒）。</summary>
        public void JumpTo(double time)
        {
            if (TargetSequence == null) return;
            double clamped = SequencerTime.ClampTime(time, TargetSequence.Duration);
            TargetSequence.SetCurrentTime(clamped);
            TargetSequence.SetPlaybackState(SequencePlaybackState.Scrubbing);
        }

        /// <summary>跳转到指定帧。</summary>
        public void JumpToFrame(int frame)
        {
            if (TargetSequence == null) return;
            double time = SequencerTime.FrameToTime(frame, TargetSequence.FrameRate);
            JumpTo(time);
        }

        /// <summary>跳转到序列起始。</summary>
        public void JumpToStart()
        {
            JumpTo(0);
        }

        /// <summary>跳转到序列末尾。</summary>
        public void JumpToEnd()
        {
            if (TargetSequence != null) JumpTo(TargetSequence.Duration);
        }

        /// <summary>向前跳转指定秒数。</summary>
        public void Forward(double deltaSeconds)
        {
            if (TargetSequence == null) return;
            double newTime = TargetSequence.CurrentTime + deltaSeconds;
            JumpTo(newTime);
        }

        /// <summary>向后跳转指定秒数。</summary>
        public void Backward(double deltaSeconds)
        {
            if (TargetSequence == null) return;
            double newTime = TargetSequence.CurrentTime - deltaSeconds;
            JumpTo(newTime);
        }

        /// <summary>向前跳一帧。</summary>
        public void StepForward()
        {
            if (TargetSequence == null) return;
            JumpTo(TargetSequence.CurrentTime + 1.0 / TargetSequence.FrameRate);
        }

        /// <summary>向后跳一帧。</summary>
        public void StepBackward()
        {
            if (TargetSequence == null) return;
            JumpTo(TargetSequence.CurrentTime - 1.0 / TargetSequence.FrameRate);
        }

        // ── 帧更新 ──────────────────────────────────────────────────

        /// <summary>
        /// 每帧更新，推进播放时间并评估序列。
        /// 由游戏循环调用。
        /// </summary>
        /// <param name="deltaTime">上一帧到当前帧的实际经过时间（秒）。</param>
        public void Tick(double deltaTime)
        {
            if (TargetSequence == null) return;

            var state = TargetSequence.PlaybackState;
            if (state != SequencePlaybackState.Playing &&
                state != SequencePlaybackState.Backward)
                return;

            // 推进时间
            double newTime = TargetSequence.CurrentTime + deltaTime * TargetSequence.PlaybackRate;

            // 应用播放范围
            if (TargetSequence.PlayRange.IsEnabled)
            {
                newTime = TargetSequence.PlayRange.Clamp(newTime);
            }

            // 应用循环模式
            newTime = SequencerTime.ApplyLoopMode(newTime, TargetSequence.Duration, LoopMode);

            // 检查是否到达末尾（非循环模式）
            if (LoopMode == SequenceLoopMode.None)
            {
                if (newTime >= TargetSequence.Duration || newTime <= 0)
                {
                    newTime = SequencerTime.ClampTime(newTime, TargetSequence.Duration);
                    TargetSequence.SetCurrentTime(newTime);

                    // 评估最后一帧
                    TargetSequence.Evaluate(newTime, EvaluationContext);

                    // 通知片段退出
                    NotifySectionsExit();

                    TargetSequence.SetPlaybackState(SequencePlaybackState.Stopped);
                    TargetSequence.RaiseCompleted();
                    OnPlaybackStateChanged?.Invoke(SequencePlaybackState.Stopped);
                    return;
                }
            }

            TargetSequence.SetCurrentTime(newTime);
            TargetSequence.Evaluate(newTime, EvaluationContext);

            // 通知片段进入/退出
            NotifySectionsEnterExit(newTime);

            _lastEvaluatedTime = newTime;
            OnTimeUpdated?.Invoke(newTime);
        }

        /// <summary>通知片段进入/退出。</summary>
        private void NotifySectionsEnterExit(double time)
        {
            if (TargetSequence == null) return;

            foreach (var track in TargetSequence.Tracks)
            {
                var activeSections = track.GetActiveSections(time);

                foreach (var section in track.Sections)
                {
                    bool isActive = activeSections.Contains(section);
                    bool wasActive = _sectionActiveState.ContainsKey(section.Id) && _sectionActiveState[section.Id];

                    if (isActive && !wasActive)
                    {
                        section.OnEnter();
                    }
                    else if (!isActive && wasActive)
                    {
                        section.OnExit();
                    }

                    _sectionActiveState[section.Id] = isActive;
                }
            }
        }

        /// <summary>通知所有活跃片段退出。</summary>
        private void NotifySectionsExit()
        {
            if (TargetSequence == null) return;

            foreach (var track in TargetSequence.Tracks)
            {
                foreach (var section in track.Sections)
                {
                    if (_sectionActiveState.ContainsKey(section.Id) && _sectionActiveState[section.Id])
                    {
                        section.OnExit();
                    }
                }
            }
        }

        // ── 速率控制 ──────────────────────────────────────────────────

        /// <summary>设置播放速率。</summary>
        public void SetPlaybackRate(double rate)
        {
            PlaybackRate = rate;
            if (TargetSequence != null)
            {
                TargetSequence.PlaybackRate = rate;
            }
        }

        /// <summary>加快播放速率。</summary>
        public void SpeedUp(double step = 0.5)
        {
            SetPlaybackRate(PlaybackRate + step);
        }

        /// <summary>减慢播放速率。</summary>
        public void SlowDown(double step = 0.5)
        {
            SetPlaybackRate(PlaybackRate - step);
        }
    }
}
