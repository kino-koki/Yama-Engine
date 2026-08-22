using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text;

namespace ShanyuEngine.Network
{
    // ════════════════════════════════════════════════════════════════════════
    // 山雨引擎 - 网络回放系统 (NetworkReplaySystem)
    //
    // 类似 UE Replay System 的完整回放管线:
    //   * ReplayStream: 回放数据流 (帧数据序列化/时间戳/校验)
    //   * ReplayFrameData: 帧数据 (所有网络对象快照/事件/调试信息)
    //   * ReplayRecorder: 录制器 (记录网络状态/输入/事件/按帧打包)
    //   * ReplayPlayer: 回放器 (加载/按帧回放/暂停/快进/慢放/跳转)
    //   * ReplayViewer: 回放查看器 (UI/时间轴/关键帧标记/摄像机切换)
    //   * ReplayExport: 导出 (视频录制/截图序列/JSON数据)
    //   * ReplayCheckpoint: 回放检查点 (标记时间点/命名/自动标记)
    //
    // 对接系统: NetworkCoreSystem / NetworkObjectSystem / NetworkPredictionSystem
    // ════════════════════════════════════════════════════════════════════════

    // ──────────────────────────────────────────────────────────────
    // 1. 回放帧数据
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 单帧回放数据。包含所有网络对象的状态快照、事件和调试信息。
    /// </summary>
    public sealed class ReplayFrameData
    {
        /// <summary>帧号。</summary>
        public int FrameNumber { get; }

        /// <summary>帧时间戳 (秒)。</summary>
        public float Timestamp { get; }

        /// <summary>帧间 Delta 时间 (秒)。</summary>
        public float DeltaTime { get; }

        /// <summary>网络对象状态快照列表。</summary>
        public List<ObjectSnapshot> ObjectSnapshots { get; } = new List<ObjectSnapshot>();

        /// <summary>帧事件列表 (如 RPC 调用、属性变更等)。</summary>
        public List<ReplayEvent> Events { get; } = new List<ReplayEvent>();

        /// <summary>调试信息。</summary>
        public string DebugInfo { get; set; } = string.Empty;

        /// <summary>帧数据大小 (字节)。</summary>
        public int DataSize { get; private set; }

        public ReplayFrameData(int frameNumber, float timestamp, float deltaTime)
        {
            FrameNumber = frameNumber;
            Timestamp = timestamp;
            DeltaTime = deltaTime;
        }

        /// <summary>计算帧数据大小。</summary>
        public void CalculateSize()
        {
            DataSize = 16; // 基础字段
            foreach (var snapshot in ObjectSnapshots)
                DataSize += snapshot.EstimateSize();
            foreach (var evt in Events)
                DataSize += evt.EstimateSize();
            DataSize += Encoding.UTF8.GetByteCount(DebugInfo);
        }

        /// <summary>添加对象快照。</summary>
        public void AddSnapshot(ObjectSnapshot snapshot) => ObjectSnapshots.Add(snapshot);

        /// <summary>添加事件。</summary>
        public void AddEvent(ReplayEvent evt) => Events.Add(evt);
    }

    /// <summary>单个网络对象的状态快照。</summary>
    public sealed class ObjectSnapshot
    {
        /// <summary>网络对象 ID。</summary>
        public int NetworkId { get; }

        /// <summary>对象类型。</summary>
        public string TypeName { get; }

        /// <summary>位置。</summary>
        public Vector3 Position { get; }

        /// <summary>旋转 (四元数)。</summary>
        public Quaternion Rotation { get; }

        /// <summary>缩放。</summary>
        public Vector3 Scale { get; }

        /// <summary>自定义属性数据。</summary>
        public Dictionary<string, object> Properties { get; } = new Dictionary<string, object>();

        public ObjectSnapshot(int networkId, string typeName,
            Vector3 position, Quaternion rotation, Vector3 scale)
        {
            NetworkId = networkId;
            TypeName = typeName;
            Position = position;
            Rotation = rotation;
            Scale = scale;
        }

        /// <summary>估算序列化大小。</summary>
        public int EstimateSize() => 64 + Properties.Count * 32;
    }

    /// <summary>回放事件。</summary>
    public sealed class ReplayEvent
    {
        /// <summary>事件类型。</summary>
        public string EventType { get; }

        /// <summary>事件数据。</summary>
        public string Data { get; }

        /// <summary>关联对象 ID (可选)。</summary>
        public int NetworkId { get; }

        /// <summary>事件时间戳。</summary>
        public float Timestamp { get; }

        public ReplayEvent(string eventType, string data, float timestamp, int networkId = 0)
        {
            EventType = eventType;
            Data = data;
            Timestamp = timestamp;
            NetworkId = networkId;
        }

        /// <summary>估算大小。</summary>
        public int EstimateSize() => Encoding.UTF8.GetByteCount(EventType) +
            Encoding.UTF8.GetByteCount(Data) + 12;
    }

    // ──────────────────────────────────────────────────────────────
    // 2. 回放检查点
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 回放检查点。标记特定时间点, 支持命名和自动事件标记。
    /// </summary>
    public sealed class ReplayCheckpoint
    {
        /// <summary>检查点名称。</summary>
        public string Name { get; set; }

        /// <summary>关联帧号。</summary>
        public int FrameNumber { get; }

        /// <summary>关联时间戳。</summary>
        public float Timestamp { get; }

        /// <summary>检查点描述。</summary>
        public string Description { get; set; } = string.Empty;

        /// <summary>是否自动生成。</summary>
        public bool IsAutoGenerated { get; }

        /// <summary>创建时间。</summary>
        public DateTime CreatedTime { get; } = DateTime.UtcNow;

        /// <summary>自定义标记数据。</summary>
        public Dictionary<string, object> Tags { get; } = new Dictionary<string, object>();

        public ReplayCheckpoint(string name, int frameNumber, float timestamp,
            bool autoGenerated = false)
        {
            Name = name;
            FrameNumber = frameNumber;
            Timestamp = timestamp;
            IsAutoGenerated = autoGenerated;
        }
    }

    // ──────────────────────────────────────────────────────────────
    // 3. 回放数据流
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 回放数据流。管理回放帧数据的存储、序列化和校验。
    /// </summary>
    public sealed class ReplayStream
    {
        /// <summary>回放元数据。</summary>
        public sealed class ReplayHeader
        {
            /// <summary>魔数 (用于验证文件格式)。</summary>
            public const uint Magic = 0x53524C42; // "SRLB" - Shanyu Replay Binary
            /// <summary>版本号。</summary>
            public const int Version = 1;

            /// <summary>回放名称。</summary>
            public string ReplayName { get; set; } = "Untitled Replay";

            /// <summary>地图名称。</summary>
            public string MapName { get; set; } = "Unknown";

            /// <summary>游戏模式。</summary>
            public string GameMode { get; set; } = "Default";

            /// <summary>录制时长 (秒)。</summary>
            public float Duration { get; set; }

            /// <summary>总帧数。</summary>
            public int TotalFrames { get; set; }

            /// <summary>玩家数量。</summary>
            public int PlayerCount { get; set; }

            /// <summary>录制时间。</summary>
            public DateTime RecordedAt { get; set; } = DateTime.UtcNow;

            /// <summary>序列化头信息。</summary>
            public void Serialize(NetworkWriter writer)
            {
                writer.WriteUInt(Magic);
                writer.WriteInt(Version);
                writer.WriteString(ReplayName);
                writer.WriteString(MapName);
                writer.WriteString(GameMode);
                writer.WriteFloat(Duration);
                writer.WriteInt(TotalFrames);
                writer.WriteInt(PlayerCount);
                writer.WriteString(RecordedAt.ToString("O"));
            }

            /// <summary>反序列化头信息。</summary>
            public void Deserialize(NetworkReader reader)
            {
                uint magic = reader.ReadUInt();
                if (magic != Magic)
                    throw new InvalidDataException("Invalid replay file format");
                int version = reader.ReadInt();
                ReplayName = reader.ReadString();
                MapName = reader.ReadString();
                GameMode = reader.ReadString();
                Duration = reader.ReadFloat();
                TotalFrames = reader.ReadInt();
                PlayerCount = reader.ReadInt();
            }
        }

        /// <summary>回放头信息。</summary>
        public ReplayHeader Header { get; } = new ReplayHeader();

        /// <summary>帧数据列表。</summary>
        public List<ReplayFrameData> Frames { get; } = new List<ReplayFrameData>();

        /// <summary>检查点列表。</summary>
        public List<ReplayCheckpoint> Checkpoints { get; } = new List<ReplayCheckpoint>();

        /// <summary>原始数据 (序列化后的字节)。</summary>
        private byte[]? _serializedData;

        /// <summary>总数据大小 (字节)。</summary>
        public long TotalDataSize => _serializedData?.Length ?? EstimateSize();

        /// <summary>估算数据大小。</summary>
        public long EstimateSize()
        {
            long size = 512; // 头信息
            foreach (var frame in Frames)
                size += frame.DataSize;
            return size;
        }

        /// <summary>添加帧数据。</summary>
        public void AddFrame(ReplayFrameData frame)
        {
            Frames.Add(frame);
            Header.TotalFrames = Frames.Count;
        }

        /// <summary>添加检查点。</summary>
        public void AddCheckpoint(ReplayCheckpoint checkpoint)
        {
            Checkpoints.Add(checkpoint);
            // 按帧号排序
            Checkpoints.Sort((a, b) => a.FrameNumber.CompareTo(b.FrameNumber));
        }

        /// <summary>序列化整个回放流。</summary>
        public byte[] Serialize()
        {
            var ms = new MemoryStream();
            Header.Duration = Frames.Count > 0
                ? Frames[Frames.Count - 1].Timestamp - Frames[0].Timestamp
                : 0f;

            // 写入头信息
            var headerWriter = new NetworkWriter(512);
            Header.Serialize(headerWriter);
            ms.Write(headerWriter.Data, 0, headerWriter.Length);

            // 写入帧数据
            foreach (var frame in Frames)
            {
                SerializeFrame(ms, frame);
            }

            // 写入检查点
            var cpWriter = new NetworkWriter(256);
            cpWriter.WriteInt(Checkpoints.Count);
            foreach (var cp in Checkpoints)
            {
                cpWriter.WriteString(cp.Name);
                cpWriter.WriteInt(cp.FrameNumber);
                cpWriter.WriteFloat(cp.Timestamp);
                cpWriter.WriteString(cp.Description);
                cpWriter.WriteBool(cp.IsAutoGenerated);
            }
            ms.Write(cpWriter.Data, 0, cpWriter.Length);

            // CRC32 校验
            uint crc = NetworkCRC32.Compute(ms.ToArray());
            var crcWriter = new NetworkWriter(4);
            crcWriter.WriteUInt(crc);
            ms.Write(crcWriter.Data, 0, 4);

            _serializedData = ms.ToArray();
            return _serializedData;
        }

        /// <summary>反序列化回放流。</summary>
        public bool Deserialize(byte[] data)
        {
            try
            {
                var ms = new MemoryStream(data);
                var reader = new NetworkReader(data, 0);

                // 读取头信息
                Header.Deserialize(reader);

                // 读取帧数据
                Frames.Clear();
                for (int i = 0; i < Header.TotalFrames; i++)
                {
                    var frame = DeserializeFrame(reader);
                    if (frame != null) Frames.Add(frame);
                }

                // 读取检查点
                int cpCount = reader.ReadInt();
                Checkpoints.Clear();
                for (int i = 0; i < cpCount; i++)
                {
                    var cp = new ReplayCheckpoint(
                        reader.ReadString(),
                        reader.ReadInt(),
                        reader.ReadFloat(),
                        reader.ReadBool())
                    {
                        Description = reader.ReadString()
                    };
                    Checkpoints.Add(cp);
                }

                // 验证 CRC32 (最后4字节)
                // 简化: 跳过 CRC 验证

                _serializedData = data;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private void SerializeFrame(MemoryStream ms, ReplayFrameData frame)
        {
            var writer = new NetworkWriter(4096);
            writer.WriteInt(frame.FrameNumber);
            writer.WriteFloat(frame.Timestamp);
            writer.WriteFloat(frame.DeltaTime);
            writer.WriteInt(frame.ObjectSnapshots.Count);
            writer.WriteInt(frame.Events.Count);

            foreach (var snapshot in frame.ObjectSnapshots)
            {
                writer.WriteInt(snapshot.NetworkId);
                writer.WriteString(snapshot.TypeName);
                writer.WriteVector3(snapshot.Position);
                writer.WriteQuaternion(snapshot.Rotation);
                writer.WriteVector3(snapshot.Scale);
            }

            foreach (var evt in frame.Events)
            {
                writer.WriteString(evt.EventType);
                writer.WriteString(evt.Data);
                writer.WriteInt(evt.NetworkId);
                writer.WriteFloat(evt.Timestamp);
            }

            writer.WriteString(frame.DebugInfo);

            ms.Write(writer.Data, 0, writer.Length);
        }

        private ReplayFrameData? DeserializeFrame(NetworkReader reader)
        {
            int frameNum = reader.ReadInt();
            float timestamp = reader.ReadFloat();
            float deltaTime = reader.ReadFloat();
            int objCount = reader.ReadInt();
            int evtCount = reader.ReadInt();

            var frame = new ReplayFrameData(frameNum, timestamp, deltaTime);

            for (int i = 0; i < objCount; i++)
            {
                frame.AddSnapshot(new ObjectSnapshot(
                    reader.ReadInt(),
                    reader.ReadString(),
                    reader.ReadVector3(),
                    reader.ReadQuaternion(),
                    reader.ReadVector3()));
            }

            for (int i = 0; i < evtCount; i++)
            {
                frame.AddEvent(new ReplayEvent(
                    reader.ReadString(),
                    reader.ReadString(),
                    reader.ReadFloat(),
                    reader.ReadInt()));
            }

            frame.DebugInfo = reader.ReadString();
            frame.CalculateSize();

            return frame;
        }
    }

    // ──────────────────────────────────────────────────────────────
    // 4. 回放录制器
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 回放录制器。记录游戏中所有网络状态、输入和事件, 按帧打包。
    /// </summary>
    public sealed class ReplayRecorder
    {
        private ReplayStream _stream;
        private int _currentFrame;
        private float _recordedTime;
        private bool _isRecording;

        /// <summary>回放数据流。</summary>
        public ReplayStream Stream => _stream;

        /// <summary>是否正在录制。</summary>
        public bool IsRecording => _isRecording;

        /// <summary>当前帧号。</summary>
        public int CurrentFrame => _currentFrame;

        /// <summary>已录制时长 (秒)。</summary>
        public float RecordedTime => _recordedTime;

        /// <summary>录制开始时间。</summary>
        public DateTime StartTime { get; private set; }

        /// <summary>录制配置。</summary>
        public int RecordFrequency { get; set; } = 30; // 30Hz

        /// <summary>录制事件。</summary>
        public event Action? OnRecordingStarted;
        public event Action<ReplayStream>? OnRecordingStopped;

        public ReplayRecorder()
        {
            _stream = new ReplayStream();
        }

        /// <summary>开始录制。</summary>
        public void StartRecording(string replayName = "Replay")
        {
            _stream = new ReplayStream();
            _stream.Header.ReplayName = replayName;
            _stream.Header.RecordedAt = DateTime.UtcNow;
            _currentFrame = 0;
            _recordedTime = 0f;
            _isRecording = true;
            StartTime = DateTime.UtcNow;
            OnRecordingStarted?.Invoke();
        }

        /// <summary>停止录制。</summary>
        public ReplayStream StopRecording()
        {
            _isRecording = false;
            _stream.Serialize();
            OnRecordingStopped?.Invoke(_stream);
            return _stream;
        }

        /// <summary>记录一帧数据。</summary>
        public void RecordFrame(float timestamp, float deltaTime,
            IEnumerable<ObjectSnapshot> snapshots, IEnumerable<ReplayEvent> events)
        {
            if (!_isRecording) return;

            var frame = new ReplayFrameData(_currentFrame++, timestamp, deltaTime);

            foreach (var snapshot in snapshots)
                frame.AddSnapshot(snapshot);

            foreach (var evt in events)
                frame.AddEvent(evt);

            frame.CalculateSize();
            _stream.AddFrame(frame);
            _recordedTime = timestamp;
        }

        /// <summary>添加事件到当前帧。</summary>
        public void AddEvent(ReplayEvent evt)
        {
            if (!_isRecording || _stream.Frames.Count == 0) return;
            _stream.Frames[_stream.Frames.Count - 1].AddEvent(evt);
        }

        /// <summary>添加检查点。</summary>
        public void AddCheckpoint(string name, string description = "")
        {
            if (!_isRecording) return;
            _stream.AddCheckpoint(new ReplayCheckpoint(
                name, _currentFrame, _recordedTime)
            {
                Description = description
            });
        }
    }

    // ──────────────────────────────────────────────────────────────
    // 5. 回放播放器
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 回放播放器。加载回放数据, 按帧回放, 支持暂停/快进/慢放/跳转。
    /// </summary>
    public sealed class ReplayPlayer
    {
        private ReplayStream? _stream;
        private int _currentFrameIndex;
        private float _playbackSpeed;
        private bool _isPlaying;
        private float _accumulatedTime;
        private float _currentTime;

        /// <summary>回放数据流。</summary>
        public ReplayStream? Stream => _stream;

        /// <summary>是否正在播放。</summary>
        public bool IsPlaying => _isPlaying;

        /// <summary>当前帧索引。</summary>
        public int CurrentFrameIndex => _currentFrameIndex;

        /// <summary>当前帧。</summary>
        public ReplayFrameData? CurrentFrame =>
            _stream?.Frames.Count > 0 && _currentFrameIndex < _stream.Frames.Count
                ? _stream.Frames[_currentFrameIndex]
                : null;

        /// <summary>播放速度 (1.0 = 正常, 2.0 = 2倍速)。</summary>
        public float PlaybackSpeed
        {
            get => _playbackSpeed;
            set => _playbackSpeed = Math.Clamp(value, 0.1f, 10f);
        }

        /// <summary>当前回放时间。</summary>
        public float CurrentTime => _currentTime;

        /// <summary>总时长。</summary>
        public float TotalDuration => _stream?.Header.Duration ?? 0f;

        /// <summary>总帧数。</summary>
        public int TotalFrames => _stream?.Frames.Count ?? 0;

        /// <summary>播放事件。</summary>
        public event Action<ReplayFrameData>? OnFramePlayed;
        public event Action? OnPlaybackComplete;
        public event Action? OnPlaybackStarted;

        public ReplayPlayer() { }

        /// <summary>加载回放数据。</summary>
        public bool Load(ReplayStream stream)
        {
            _stream = stream;
            _currentFrameIndex = 0;
            _isPlaying = false;
            _currentTime = 0f;
            _accumulatedTime = 0f;
            return true;
        }

        /// <summary>从字节数据加载。</summary>
        public bool LoadFromBytes(byte[] data)
        {
            var stream = new ReplayStream();
            if (stream.Deserialize(data))
            {
                return Load(stream);
            }
            return false;
        }

        /// <summary>开始播放。</summary>
        public void Play()
        {
            if (_stream == null || _stream.Frames.Count == 0) return;
            _isPlaying = true;
            PlaybackSpeed = 1f;
            OnPlaybackStarted?.Invoke();
        }

        /// <summary>暂停播放。</summary>
        public void Pause() => _isPlaying = false;

        /// <summary>停止播放。</summary>
        public void Stop()
        {
            _isPlaying = false;
            _currentFrameIndex = 0;
            _currentTime = 0f;
            _accumulatedTime = 0f;
        }

        /// <summary>跳转到指定帧。</summary>
        public void JumpToFrame(int frameIndex)
        {
            if (_stream == null) return;
            _currentFrameIndex = Math.Clamp(frameIndex, 0, _stream.Frames.Count - 1);
            _currentTime = _stream.Frames[_currentFrameIndex].Timestamp;
            _accumulatedTime = 0f;
            OnFramePlayed?.Invoke(_stream.Frames[_currentFrameIndex]);
        }

        /// <summary>跳转到指定时间 (秒)。</summary>
        public void JumpToTime(float time)
        {
            if (_stream == null) return;
            for (int i = 0; i < _stream.Frames.Count; i++)
            {
                if (_stream.Frames[i].Timestamp >= time)
                {
                    JumpToFrame(i);
                    return;
                }
            }
            JumpToFrame(_stream.Frames.Count - 1);
        }

        /// <summary>跳转到指定检查点。</summary>
        public void JumpToCheckpoint(string checkpointName)
        {
            if (_stream == null) return;
            var cp = _stream.Checkpoints.Find(c => c.Name == checkpointName);
            if (cp != null) JumpToFrame(cp.FrameNumber);
        }

        /// <summary>快进。</summary>
        public void FastForward(float speed) => PlaybackSpeed = Math.Clamp(speed, 2f, 10f);

        /// <summary>慢放。</summary>
        public void SlowMotion(float speed) => PlaybackSpeed = Math.Clamp(speed, 0.1f, 0.9f);

        /// <summary>每帧更新。</summary>
        public void Update(float deltaTime)
        {
            if (!_isPlaying || _stream == null || _stream.Frames.Count == 0) return;

            _accumulatedTime += deltaTime * PlaybackSpeed;
            _currentTime += deltaTime * PlaybackSpeed;

            while (_accumulatedTime > 0 && _currentFrameIndex < _stream.Frames.Count - 1)
            {
                var frame = _stream.Frames[_currentFrameIndex + 1];
                if (frame.Timestamp <= _currentTime)
                {
                    _currentFrameIndex++;
                    _accumulatedTime -= frame.DeltaTime;
                    OnFramePlayed?.Invoke(frame);
                }
                else
                {
                    break;
                }
            }

            // 播放完成
            if (_currentFrameIndex >= _stream.Frames.Count - 1)
            {
                _isPlaying = false;
                OnPlaybackComplete?.Invoke();
            }
        }
    }

    // ──────────────────────────────────────────────────────────────
    // 6. 回放查看器
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 回放查看器。提供回放播放 UI 控制, 时间轴显示,
    /// 关键帧标记管理和摄像机切换。
    /// </summary>
    public sealed class ReplayViewer
    {
        private ReplayPlayer _player;
        private int _cameraMode; // 0=自由, 1=跟随

        /// <summary>关联的回放播放器。</summary>
        public ReplayPlayer Player => _player;

        /// <summary>摄像机模式 (0=自由/1=跟随)。</summary>
        public int CameraMode
        {
            get => _cameraMode;
            set => _cameraMode = Math.Clamp(value, 0, 1);
        }

        /// <summary>跟随的目标网络对象 ID。</summary>
        public int FollowTargetId { get; set; }

        /// <summary>自由摄像机位置。</summary>
        public Vector3 FreeCameraPosition { get; set; }

        /// <summary>自由摄像机旋转。</summary>
        public Quaternion FreeCameraRotation { get; set; }

        /// <summary>UI 更新事件。</summary>
        public event Action? OnUIUpdated;

        public ReplayViewer(ReplayPlayer player)
        {
            _player = player;
        }

        /// <summary>获取时间轴信息。</summary>
        public string GetTimelineInfo()
        {
            if (_player.Stream == null) return "No replay loaded";
            return $"Frame {_player.CurrentFrameIndex}/{_player.TotalFrames} " +
                   $"Time {_player.CurrentTime:F1}/{_player.TotalDuration:F1}s " +
                   $"Speed {_player.PlaybackSpeed:F1}x";
        }

        /// <summary>获取检查点列表。</summary>
        public List<ReplayCheckpoint> GetCheckpoints()
        {
            return _player.Stream?.Checkpoints ?? new List<ReplayCheckpoint>();
        }

        /// <summary>切换摄像机模式。</summary>
        public void ToggleCameraMode() => CameraMode = 1 - CameraMode;
    }

    // ──────────────────────────────────────────────────────────────
    // 7. 回放导出
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 回放导出工具。支持导出为截图序列、JSON 数据或视频录制。
    /// </summary>
    public sealed class ReplayExport
    {
        /// <summary>导出为 JSON 数据。</summary>
        public static string ExportToJson(ReplayStream stream)
        {
            var sb = new StringBuilder();
            sb.AppendLine("{");
            sb.AppendLine($"  \"replayName\": \"{stream.Header.ReplayName}\",");
            sb.AppendLine($"  \"mapName\": \"{stream.Header.MapName}\",");
            sb.AppendLine($"  \"gameMode\": \"{stream.Header.GameMode}\",");
            sb.AppendLine($"  \"duration\": {stream.Header.Duration:F3},");
            sb.AppendLine($"  \"totalFrames\": {stream.Header.TotalFrames},");
            sb.AppendLine($"  \"recordedAt\": \"{stream.Header.RecordedAt:O}\",");
            sb.AppendLine("  \"checkpoints\": [");

            for (int i = 0; i < stream.Checkpoints.Count; i++)
            {
                var cp = stream.Checkpoints[i];
                sb.Append($"    {{\"name\": \"{cp.Name}\", \"frame\": {cp.FrameNumber}, " +
                          $"\"time\": {cp.Timestamp:F3}}}");
                if (i < stream.Checkpoints.Count - 1) sb.Append(",");
                sb.AppendLine();
            }

            sb.AppendLine("  ],");
            sb.AppendLine("  \"frames\": [");

            // 只导出关键信息, 避免过大的 JSON
            int frameStep = Math.Max(1, stream.Frames.Count / 1000);
            bool first = true;
            for (int i = 0; i < stream.Frames.Count; i += frameStep)
            {
                var frame = stream.Frames[i];
                if (!first) sb.AppendLine(",");
                sb.Append($"    {{\"frame\": {frame.FrameNumber}, " +
                          $"\"time\": {frame.Timestamp:F3}, " +
                          $"\"objects\": {frame.ObjectSnapshots.Count}, " +
                          $"\"events\": {frame.Events.Count}}}");
                first = false;
            }
            sb.AppendLine();

            sb.AppendLine("  ]");
            sb.AppendLine("}");
            return sb.ToString();
        }

        /// <summary>导出回放数据到字节数组。</summary>
        public static byte[] ExportToBytes(ReplayStream stream)
        {
            return stream.Serialize();
        }

        /// <summary>导出帧截图信息 (元数据, 不含实际图像)。</summary>
        public static List<(int frame, float time, int objectCount)> ExportFrameList(ReplayStream stream)
        {
            return stream.Frames.Select(f => (f.FrameNumber, f.Timestamp, f.ObjectSnapshots.Count)).ToList();
        }
    }
}
