// ============================================================================
// NetworkSynchronizer.cs
// yama游戏引擎 - 状态同步系统
// Delta压缩 + 状态快照 + 全量同步
// ============================================================================
// Delta压缩是减少带宽的关键 只有变化的数据才发送
// 状态快照用于断线重连后的恢复
// ============================================================================

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace YamaEngine.Network
{
    /// <summary>
    /// 同步模式
    /// </summary>
    public enum SyncMode
    {
        /// <summary>不同步</summary>
        None,
        /// <summary>Delta同步 - 只发送变化的部分</summary>
        Delta,
        /// <summary>全量同步 - 每次发送完整状态</summary>
        Full,
        /// <summary>混合模式 - 定期全量 + 每帧Delta</summary>
        Mixed
    }

    /// <summary>
    /// 状态变化标记
    /// </summary>
    [Flags]
    public enum StateFlags : uint
    {
        None = 0,
        Position = 1 << 0,
        Rotation = 1 << 1,
        Scale = 1 << 2,
        Velocity = 1 << 3,
        Health = 1 << 4,
        Animation = 1 << 5,
        Custom1 = 1 << 6,
        Custom2 = 1 << 7,
        Custom3 = 1 << 8,
        Custom4 = 1 << 9,
        Custom5 = 1 << 10,
        Custom6 = 1 << 11,
        Custom7 = 1 << 12,
        Custom8 = 1 << 13,
        InputState = 1 << 14,
        All = 0xFFFFFFFF
    }

    /// <summary>
    /// 状态快照 - 某一时刻的完整游戏状态
    /// </summary>

    /// <summary>
    /// 对象状态
    /// </summary>
    public class ObjectState
    {
        /// <summary>对象ID</summary>
        public uint ObjectId { get; set; }

        /// <summary>对象类型ID</summary>
        public ushort TypeId { get; set; }

        /// <summary>变化标记</summary>
        public StateFlags ChangedFlags { get; set; }

        /// <summary>位置</summary>
        public System.Numerics.Vector3 Position { get; set; }

        /// <summary>旋转(四元数)</summary>
        public System.Numerics.Quaternion Rotation { get; set; }

        /// <summary>缩放</summary>
        public System.Numerics.Vector3 Scale { get; set; } = System.Numerics.Vector3.One;

        /// <summary>速度</summary>
        public System.Numerics.Vector3 Velocity { get; set; }

        /// <summary>生命值</summary>
        public float Health { get; set; }

        /// <summary>最大生命值</summary>
        public float MaxHealth { get; set; }

        /// <summary>动画状态ID</summary>
        public uint AnimationState { get; set; }

        /// <summary>动画时间</summary>
        public float AnimationTime { get; set; }

        /// <summary>自定义数据</summary>
        public Dictionary<string, object> CustomData { get; set; } = new Dictionary<string, object>();

        /// <summary>原始字节数据(Delta编码用)</summary>
        public byte[] RawData { get; set; }
    }

    /// <summary>
    /// Delta编码结果
    /// </summary>
    public struct DeltaResult
    {
        /// <summary>Delta数据</summary>
        public byte[] DeltaData;

        /// <summary>变化标记</summary>
        public StateFlags ChangedFlags;

        /// <summary>Delta大小</summary>
        public int DeltaSize => DeltaData?.Length ?? 0;
    }

    /// <summary>
    /// 同步统计
    /// </summary>
    public class SyncStatistics
    {
        /// <summary>全量同步次数</summary>
        public int FullSyncCount;

        /// <summary>Delta同步次数</summary>
        public int DeltaSyncCount;

        /// <summary>全量同步总字节</summary>
        public long FullSyncBytes;

        /// <summary>Delta同步总字节</summary>
        public long DeltaSyncBytes;

        /// <summary>全量平均大小</summary>
        public float AverageFullSize => FullSyncCount > 0 ? (float)FullSyncBytes / FullSyncCount : 0;

        /// <summary>Delta平均大小</summary>
        public float AverageDeltaSize => DeltaSyncCount > 0 ? (float)DeltaSyncBytes / DeltaSyncCount : 0;

        /// <summary>压缩率(越小越好)</summary>
        public float CompressionRatio => AverageFullSize > 0 ? AverageDeltaSize / AverageFullSize : 0;

        /// <summary>快照保存次数</summary>
        public int SnapshotSaveCount;

        /// <summary>快照恢复次数</summary>
        public int SnapshotRestoreCount;

        /// <summary>快照总大小</summary>
        public long SnapshotTotalBytes;
    }

    /// <summary>
    /// 同步器状态快照。包含全局时间戳、序列号及各网络对象状态。
    /// </summary>
    public sealed class SyncStateSnapshot
    {
        /// <summary>序列号。</summary>
        public uint Sequence;

        /// <summary>时间戳(毫秒)。</summary>
        public long Timestamp;

        /// <summary>世界时间(秒)。</summary>
        public double WorldTime;

        /// <summary>对象状态表。</summary>
        public Dictionary<uint, ObjectState> ObjectStates = new();

        /// <summary>变化标志。</summary>
        public StateFlags ChangedFlags;
    }

    /// <summary>
    /// 网络状态同步器
    /// 负责游戏状态的Delta压缩同步和快照管理
    /// </summary>
    public class NetworkSynchronizer : IDisposable
    {
        // 传输层
        private readonly NetworkTransportLayer _transport;

        // 同步模式
        private SyncMode _syncMode = SyncMode.Mixed;

        // 当前状态
        private Dictionary<uint, ObjectState> _currentState = new Dictionary<uint, ObjectState>();

        // 上一帧状态(Delta用)
        private Dictionary<uint, ObjectState> _previousState = new Dictionary<uint, ObjectState>();

        // 状态锁
        private readonly object _stateLock = new object();

        // 快照环形缓冲区
        private SyncStateSnapshot[] _snapshotBuffer;
        private int _snapshotBufferIndex;
        private int _snapshotCount;
        private readonly int _maxSnapshots = 120; // 保留最近120个快照

        // 快照序列号
        private uint _snapshotSequence;

        // 同步间隔(毫秒)
        private int _syncInterval = 33; // ~30Hz

        // 全量同步间隔
        private int _fullSyncInterval = 2000; // 每2秒全量同步一次

        // 上一帧全量同步时间
        private long _lastFullSyncTime;

        // 上一帧同步时间
        private long _lastSyncTime;

        // 统计
        private SyncStatistics _stats = new SyncStatistics();
        private readonly object _statsLock = new object();

        // 压缩开关
        private bool _enableCompression = true;

        // 量化精度(位置精度0.01, 旋转精度0.01弧度等)
        private float _positionQuantization = 0.01f;
        private float _rotationQuantization = 0.005f;
        private float _velocityQuantization = 0.05f;

        // 运行标志
        private volatile bool _running;
        private Thread _syncThread;

        // 是否是服务端
        private bool _isServer;

        // 已注册的状态回调
        private Func<Dictionary<uint, ObjectState>> _stateProvider;
        private Action<Dictionary<uint, ObjectState>> _stateConsumer;

        // 对象状态变化事件
        public event Action<uint, ObjectState> OnStateUpdated;

        // 快照保存事件
        public event Action<SyncStateSnapshot> OnSnapshotSaved;

        // 快照恢复事件
        public event Action<SyncStateSnapshot> OnSnapshotRestored;

        // ==================== 构造函数 ====================

        public NetworkSynchronizer(NetworkTransportLayer transport)
        {
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
            _snapshotBuffer = new SyncStateSnapshot[_maxSnapshots];

            // 注册包接收
            _transport.OnPacketReceived += OnPacketReceived;
        }

        // ==================== 配置 ====================

        /// <summary>
        /// 设置同步模式
        /// </summary>
        public void SetSyncMode(SyncMode mode)
        {
            _syncMode = mode;
            Console.WriteLine($"[Synchronizer] 同步模式设置为: {mode}");
        }

        /// <summary>
        /// 设置同步间隔
        /// </summary>
        public void SetSyncInterval(int intervalMs)
        {
            _syncInterval = Math.Max(10, intervalMs);
        }

        /// <summary>
        /// 设置全量同步间隔
        /// </summary>
        public void SetFullSyncInterval(int intervalMs)
        {
            _fullSyncInterval = Math.Max(100, intervalMs);
        }

        /// <summary>
        /// 设置量化精度
        /// </summary>
        public void SetQuantization(float position, float rotation, float velocity)
        {
            _positionQuantization = Math.Max(0.0001f, position);
            _rotationQuantization = Math.Max(0.0001f, rotation);
            _velocityQuantization = Math.Max(0.0001f, velocity);
        }

        /// <summary>
        /// 设置状态提供者(服务端用, 返回当前世界状态)
        /// </summary>
        public void SetStateProvider(Func<Dictionary<uint, ObjectState>> provider)
        {
            _stateProvider = provider;
        }

        /// <summary>
        /// 设置状态消费者(客户端用, 接收同步的状态)
        /// </summary>
        public void SetStateConsumer(Action<Dictionary<uint, ObjectState>> consumer)
        {
            _stateConsumer = consumer;
        }

        /// <summary>
        /// 设置服务端/客户端模式
        /// </summary>
        public void SetServerMode(bool isServer)
        {
            _isServer = isServer;
        }

        // ==================== 启动/停止 ====================

        /// <summary>
        /// 启动同步
        /// </summary>
        public void Start()
        {
            if (_running) return;
            _running = true;
            _syncThread = new Thread(SyncLoop)
            {
                Name = "StateSynchronizer",
                IsBackground = true
            };
            _syncThread.Start();
            Console.WriteLine("[Synchronizer] 状态同步已启动");
        }

        /// <summary>
        /// 停止同步
        /// </summary>
        public void Stop()
        {
            _running = false;
            _syncThread?.Join(2000);
        }

        // ==================== 状态更新 ====================

        /// <summary>
        /// 更新单个对象的状态(服务端调用)
        /// </summary>
        public void UpdateObjectState(uint objectId, ObjectState state)
        {
            lock (_stateLock)
            {
                _currentState[objectId] = state;
            }
            OnStateUpdated?.Invoke(objectId, state);
        }

        /// <summary>
        /// 批量更新对象状态
        /// </summary>
        public void UpdateObjectStates(Dictionary<uint, ObjectState> states)
        {
            lock (_stateLock)
            {
                foreach (var kvp in states)
                {
                    _currentState[kvp.Key] = kvp.Value;
                }
            }
        }

        /// <summary>
        /// 移除对象状态
        /// </summary>
        public void RemoveObjectState(uint objectId)
        {
            lock (_stateLock)
            {
                _currentState.Remove(objectId);
                _previousState.Remove(objectId);
            }
        }

        /// <summary>
        /// 获取当前状态(线程安全)
        /// </summary>
        public Dictionary<uint, ObjectState> GetCurrentState()
        {
            lock (_stateLock)
            {
                return new Dictionary<uint, ObjectState>(_currentState);
            }
        }

        /// <summary>
        /// 获取单个对象状态
        /// </summary>
        public ObjectState GetObjectState(uint objectId)
        {
            lock (_stateLock)
            {
                if (_currentState.TryGetValue(objectId, out var state))
                {
                    return state;
                }
            }
            return null;
        }

        // ==================== 同步循环 ====================

        /// <summary>
        /// 同步线程主循环
        /// </summary>
        private void SyncLoop()
        {
            while (_running)
            {
                long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                if (now - _lastSyncTime >= _syncInterval)
                {
                    _lastSyncTime = now;

                    if (_isServer)
                    {
                        ServerSync(now);
                    }
                }

                Thread.Sleep(5);
            }
        }

        /// <summary>
        /// 服务端同步逻辑
        /// </summary>
        private void ServerSync(long now)
        {
            // 获取当前状态
            Dictionary<uint, ObjectState> current;

            if (_stateProvider != null)
            {
                current = _stateProvider();
            }
            else
            {
                lock (_stateLock)
                {
                    current = new Dictionary<uint, ObjectState>(_currentState);
                }
            }

            if (current.Count == 0) return;

            // 判断是否需要全量同步
            bool needFullSync = (now - _lastFullSyncTime >= _fullSyncInterval);

            // 保存快照
            SaveSnapshot(current, now);

            byte[] syncData;

            if (_syncMode == SyncMode.Full || needFullSync || _syncMode == SyncMode.Mixed && _previousState.Count == 0)
            {
                // 全量同步
                syncData = EncodeFullState(current);
                _lastFullSyncTime = now;

                lock (_statsLock)
                {
                    _stats.FullSyncCount++;
                    _stats.FullSyncBytes += syncData.Length;
                }
            }
            else if (_syncMode == SyncMode.Delta || _syncMode == SyncMode.Mixed)
            {
                // Delta同步
                syncData = EncodeDeltaState(current);

                lock (_statsLock)
                {
                    _stats.DeltaSyncCount++;
                    _stats.DeltaSyncBytes += syncData.Length;
                }
            }
            else
            {
                return;
            }

            // 发送
            if (syncData != null && syncData.Length > 0)
            {
                var packet = NetworkPacket.Create(PacketType.Sync, syncData, false);
                _transport.Broadcast(packet.Payload, false, PacketPriority.High);
            }

            // 保存当前状态为上一帧
            lock (_stateLock)
            {
                _previousState = new Dictionary<uint, ObjectState>(current);
            }
        }

        // ==================== 编码 ====================

        /// <summary>
        /// 编码完整状态
        /// </summary>
        private byte[] EncodeFullState(Dictionary<uint, ObjectState> states)
        {
            var writer = new NetworkWriter(4096);

            // 头部: 标记为全量同步
            writer.WriteByte(0); // 0 = Full, 1 = Delta
            writer.WriteUInt32(Interlocked.Increment(ref _snapshotSequence));
            writer.WriteDouble(DateTime.UtcNow.TimeOfDay.TotalSeconds);
            writer.WriteInt32(states.Count);

            foreach (var kvp in states)
            {
                var state = kvp.Value;
                writer.WriteUInt32(kvp.Key); // ObjectId
                writer.WriteUInt16(state.TypeId);
                writer.WriteUInt32((uint)ComputeChangedFlags(state, null)); // All changed
                WriteObjectState(writer, state);
            }

            var data = writer.ToArray();

            if (_enableCompression && data.Length > 100)
            {
                data = Compress(data);
            }

            return data;
        }

        /// <summary>
        /// 编码Delta状态
        /// </summary>
        private byte[] EncodeDeltaState(Dictionary<uint, ObjectState> current)
        {
            var writer = new NetworkWriter(2048);

            // 头部: 标记为Delta同步
            writer.WriteByte(1); // 1 = Delta
            writer.WriteUInt32(Interlocked.Increment(ref _snapshotSequence));
            writer.WriteDouble(DateTime.UtcNow.TimeOfDay.TotalSeconds);

            int changedCount = 0;
            var changedStates = new List<KeyValuePair<uint, ObjectState>>();

            lock (_stateLock)
            {
                foreach (var kvp in current)
                {
                    if (_previousState.TryGetValue(kvp.Key, out var prev))
                    {
                        var flags = ComputeChangedFlags(kvp.Value, prev);
                        if (flags != StateFlags.None)
                        {
                            kvp.Value.ChangedFlags = flags;
                            changedStates.Add(kvp);
                        }
                    }
                    else
                    {
                        // 新对象, 全量发送
                        kvp.Value.ChangedFlags = StateFlags.All;
                        changedStates.Add(kvp);
                    }
                }

                // 检查被删除的对象
                foreach (var kvp in _previousState)
                {
                    if (!current.ContainsKey(kvp.Key))
                    {
                        // 标记为删除
                        changedStates.Add(new KeyValuePair<uint, ObjectState>(
                            kvp.Key, new ObjectState { ObjectId = kvp.Key, TypeId = 0 }));
                    }
                }
            }

            writer.WriteInt32(changedStates.Count);

            foreach (var kvp in changedStates)
            {
                var state = kvp.Value;

                // TypeId == 0 表示删除
                if (state.TypeId == 0 && state.ChangedFlags == StateFlags.None)
                {
                    writer.WriteByte(0); // Delete marker
                    writer.WriteUInt32(kvp.Key);
                }
                else
                {
                    writer.WriteByte(1); // Update marker
                    writer.WriteUInt32(kvp.Key);
                    writer.WriteUInt16(state.TypeId);
                    writer.WriteUInt32((uint)state.ChangedFlags);
                    WriteDeltaObjectState(writer, state, _previousState.ContainsKey(kvp.Key) ? _previousState[kvp.Key] : null);
                }

                changedCount++;
            }

            var data = writer.ToArray();

            if (_enableCompression && data.Length > 50)
            {
                data = Compress(data);
            }

            return data;
        }

        /// <summary>
        /// 写入完整对象状态
        /// </summary>
        private void WriteObjectState(NetworkWriter writer, ObjectState state)
        {
            // 量化位置
            writer.WriteSingle(Quantize(state.Position.X, _positionQuantization));
            writer.WriteSingle(Quantize(state.Position.Y, _positionQuantization));
            writer.WriteSingle(Quantize(state.Position.Z, _positionQuantization));

            // 量化旋转
            writer.WriteSingle(Quantize(state.Rotation.X, _rotationQuantization));
            writer.WriteSingle(Quantize(state.Rotation.Y, _rotationQuantization));
            writer.WriteSingle(Quantize(state.Rotation.Z, _rotationQuantization));
            writer.WriteSingle(Quantize(state.Rotation.W, _rotationQuantization));

            // 量化缩放
            writer.WriteSingle(Quantize(state.Scale.X, 0.01f));
            writer.WriteSingle(Quantize(state.Scale.Y, 0.01f));
            writer.WriteSingle(Quantize(state.Scale.Z, 0.01f));

            // 量化速度
            writer.WriteSingle(Quantize(state.Velocity.X, _velocityQuantization));
            writer.WriteSingle(Quantize(state.Velocity.Y, _velocityQuantization));
            writer.WriteSingle(Quantize(state.Velocity.Z, _velocityQuantization));

            // 生命值
            writer.WriteSingle(state.Health);
            writer.WriteSingle(state.MaxHealth);

            // 动画
            writer.WriteUInt32(state.AnimationState);
            writer.WriteSingle(state.AnimationTime);
        }

        /// <summary>
        /// 写入Delta对象状态(只写变化的部分)
        /// </summary>
        private void WriteDeltaObjectState(NetworkWriter writer, ObjectState current, ObjectState previous)
        {
            var flags = current.ChangedFlags;

            if ((flags & StateFlags.Position) != 0)
            {
                writer.WriteSingle(Quantize(current.Position.X, _positionQuantization));
                writer.WriteSingle(Quantize(current.Position.Y, _positionQuantization));
                writer.WriteSingle(Quantize(current.Position.Z, _positionQuantization));
            }

            if ((flags & StateFlags.Rotation) != 0)
            {
                writer.WriteSingle(Quantize(current.Rotation.X, _rotationQuantization));
                writer.WriteSingle(Quantize(current.Rotation.Y, _rotationQuantization));
                writer.WriteSingle(Quantize(current.Rotation.Z, _rotationQuantization));
                writer.WriteSingle(Quantize(current.Rotation.W, _rotationQuantization));
            }

            if ((flags & StateFlags.Scale) != 0)
            {
                writer.WriteSingle(Quantize(current.Scale.X, 0.01f));
                writer.WriteSingle(Quantize(current.Scale.Y, 0.01f));
                writer.WriteSingle(Quantize(current.Scale.Z, 0.01f));
            }

            if ((flags & StateFlags.Velocity) != 0)
            {
                writer.WriteSingle(Quantize(current.Velocity.X, _velocityQuantization));
                writer.WriteSingle(Quantize(current.Velocity.Y, _velocityQuantization));
                writer.WriteSingle(Quantize(current.Velocity.Z, _velocityQuantization));
            }

            if ((flags & StateFlags.Health) != 0)
            {
                writer.WriteSingle(current.Health);
                writer.WriteSingle(current.MaxHealth);
            }

            if ((flags & StateFlags.Animation) != 0)
            {
                writer.WriteUInt32(current.AnimationState);
                writer.WriteSingle(current.AnimationTime);
            }
        }

        // ==================== 解码 ====================

        /// <summary>
        /// 处理接收到的同步数据
        /// </summary>
        private void OnPacketReceived(object sender, PacketReceivedEventArgs e)
        {
            if (e.Packet.Type != PacketType.Sync) return;
            if (e.Packet.Payload == null || e.Packet.Payload.Length == 0) return;

            var data = e.Packet.Payload;

            if (_enableCompression && data.Length > 20)
            {
                data = Decompress(data);
                if (data == null) return;
            }

            try
            {
                var reader = new NetworkReader(data);
                byte syncType = reader.ReadByte();
                uint seq = reader.ReadUInt32();
                double worldTime = reader.ReadDouble();

                var states = new Dictionary<uint, ObjectState>();

                if (syncType == 0)
                {
                    // 全量同步
                    int count = reader.ReadInt32();
                    for (int i = 0; i < count; i++)
                    {
                        var state = ReadFullObjectState(reader);
                        if (state != null)
                        {
                            states[state.ObjectId] = state;
                        }
                    }

                    lock (_stateLock)
                    {
                        _currentState = states;
                        _previousState = new Dictionary<uint, ObjectState>(states);
                    }

                    Console.WriteLine($"[Synchronizer] 收到全量同步: {states.Count}个对象, 序列号:{seq}");
                }
                else if (syncType == 1)
                {
                    // Delta同步
                    int count = reader.ReadInt32();
                    lock (_stateLock)
                    {
                        for (int i = 0; i < count; i++)
                        {
                            byte marker = reader.ReadByte();
                            uint objId = reader.ReadUInt32();

                            if (marker == 0)
                            {
                                // 删除
                                _currentState.Remove(objId);
                                _previousState.Remove(objId);
                            }
                            else
                            {
                                var state = ReadDeltaObjectState(reader, objId);
                                if (state != null)
                                {
                                    if (_currentState.ContainsKey(objId))
                                    {
                                        // 合并: 只更新变化的部分
                                        var existing = _currentState[objId];
                                        MergeState(existing, state);
                                        states[objId] = existing;
                                    }
                                    else
                                    {
                                        // 新对象
                                        _currentState[objId] = state;
                                        states[objId] = state;
                                    }
                                }
                            }
                        }

                        _previousState = new Dictionary<uint, ObjectState>(_currentState);
                    }
                }

                // 通知消费者
                if (states.Count > 0)
                {
                    _stateConsumer?.Invoke(states);

                    foreach (var kvp in states)
                    {
                        OnStateUpdated?.Invoke(kvp.Key, kvp.Value);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Synchronizer] 解码同步数据失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 读取完整对象状态
        /// </summary>
        private ObjectState ReadFullObjectState(NetworkReader reader)
        {
            var state = new ObjectState
            {
                ObjectId = reader.ReadUInt32(),
                TypeId = reader.ReadUInt16()
            };

            var flags = (StateFlags)reader.ReadUInt32();
            state.ChangedFlags = flags;

            state.Position = new System.Numerics.Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            state.Rotation = new System.Numerics.Quaternion(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            state.Scale = new System.Numerics.Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            state.Velocity = new System.Numerics.Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            state.Health = reader.ReadSingle();
            state.MaxHealth = reader.ReadSingle();
            state.AnimationState = reader.ReadUInt32();
            state.AnimationTime = reader.ReadSingle();

            return state;
        }

        /// <summary>
        /// 读取Delta对象状态
        /// </summary>
        private ObjectState ReadDeltaObjectState(NetworkReader reader, uint objectId)
        {
            var state = new ObjectState
            {
                ObjectId = objectId,
                TypeId = reader.ReadUInt16()
            };

            var flags = (StateFlags)reader.ReadUInt32();
            state.ChangedFlags = flags;

            if ((flags & StateFlags.Position) != 0)
            {
                state.Position = new System.Numerics.Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            }

            if ((flags & StateFlags.Rotation) != 0)
            {
                state.Rotation = new System.Numerics.Quaternion(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            }

            if ((flags & StateFlags.Scale) != 0)
            {
                state.Scale = new System.Numerics.Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            }

            if ((flags & StateFlags.Velocity) != 0)
            {
                state.Velocity = new System.Numerics.Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            }

            if ((flags & StateFlags.Health) != 0)
            {
                state.Health = reader.ReadSingle();
                state.MaxHealth = reader.ReadSingle();
            }

            if ((flags & StateFlags.Animation) != 0)
            {
                state.AnimationState = reader.ReadUInt32();
                state.AnimationTime = reader.ReadSingle();
            }

            return state;
        }

        /// <summary>
        /// 合并Delta到现有状态
        /// </summary>
        private void MergeState(ObjectState target, ObjectState delta)
        {
            var flags = delta.ChangedFlags;

            if ((flags & StateFlags.Position) != 0)
            {
                target.Position = delta.Position;
            }

            if ((flags & StateFlags.Rotation) != 0)
            {
                target.Rotation = delta.Rotation;
            }

            if ((flags & StateFlags.Scale) != 0)
            {
                target.Scale = delta.Scale;
            }

            if ((flags & StateFlags.Velocity) != 0)
            {
                target.Velocity = delta.Velocity;
            }

            if ((flags & StateFlags.Health) != 0)
            {
                target.Health = delta.Health;
                target.MaxHealth = delta.MaxHealth;
            }

            if ((flags & StateFlags.Animation) != 0)
            {
                target.AnimationState = delta.AnimationState;
                target.AnimationTime = delta.AnimationTime;
            }

            target.ChangedFlags |= flags;
        }

        // ==================== 快照管理 ====================

        /// <summary>
        /// 保存快照
        /// </summary>
        private void SaveSnapshot(Dictionary<uint, ObjectState> states, long timestamp)
        {
            var snapshot = new SyncStateSnapshot
            {
                Sequence = _snapshotSequence,
                Timestamp = timestamp,
                WorldTime = DateTime.UtcNow.TimeOfDay.TotalSeconds,
                ObjectStates = new Dictionary<uint, ObjectState>(states),
                ChangedFlags = StateFlags.All
            };

            lock (_stateLock)
            {
                _snapshotBuffer[_snapshotBufferIndex] = snapshot;
                _snapshotBufferIndex = (_snapshotBufferIndex + 1) % _maxSnapshots;
                _snapshotCount = Math.Min(_snapshotCount + 1, _maxSnapshots);
            }

            lock (_statsLock)
            {
                _stats.SnapshotSaveCount++;
            }

            OnSnapshotSaved?.Invoke(snapshot);
        }

        /// <summary>
        /// 获取最近的快照
        /// </summary>
        public SyncStateSnapshot GetLatestSnapshot()
        {
            lock (_stateLock)
            {
                if (_snapshotCount == 0) return null;

                int index = (_snapshotBufferIndex - 1 + _maxSnapshots) % _maxSnapshots;
                return _snapshotBuffer[index];
            }
        }

        /// <summary>
        /// 根据序列号获取快照
        /// </summary>
        public SyncStateSnapshot GetSnapshotBySequence(uint sequence)
        {
            lock (_stateLock)
            {
                for (int i = 0; i < _snapshotCount; i++)
                {
                    int index = (_snapshotBufferIndex - 1 - i + _maxSnapshots * 2) % _maxSnapshots;
                    var snapshot = _snapshotBuffer[index];
                    if (snapshot != null && snapshot.Sequence == sequence)
                    {
                        return snapshot;
                    }
                }
            }
            return null;
        }

        /// <summary>
        /// 恢复到指定快照
        /// </summary>
        public bool RestoreSnapshot(uint sequence)
        {
            var snapshot = GetSnapshotBySequence(sequence);
            if (snapshot == null)
            {
                Console.WriteLine($"[Synchronizer] 快照 {sequence} 不存在");
                return false;
            }

            lock (_stateLock)
            {
                _currentState = new Dictionary<uint, ObjectState>(snapshot.ObjectStates);
                _previousState = new Dictionary<uint, ObjectState>(_currentState);
            }

            lock (_statsLock)
            {
                _stats.SnapshotRestoreCount++;
            }

            OnSnapshotRestored?.Invoke(snapshot);
            Console.WriteLine($"[Synchronizer] 恢复到快照 {sequence}, 对象数: {snapshot.ObjectStates.Count}");

            return true;
        }

        /// <summary>
        /// 获取快照列表(用于断线重连)
        /// </summary>
        public List<SyncStateSnapshot> GetRecentSnapshots(int count = 10)
        {
            var result = new List<SyncStateSnapshot>();
            lock (_stateLock)
            {
                int toGet = Math.Min(count, _snapshotCount);
                for (int i = 0; i < toGet; i++)
                {
                    int index = (_snapshotBufferIndex - 1 - i + _maxSnapshots * 2) % _maxSnapshots;
                    if (_snapshotBuffer[index] != null)
                    {
                        result.Add(_snapshotBuffer[index]);
                    }
                }
            }
            return result;
        }

        /// <summary>
        /// 导出快照到字节
        /// </summary>
        public byte[] ExportSnapshot(SyncStateSnapshot snapshot)
        {
            var writer = new NetworkWriter(8192);
            writer.WriteUInt32(snapshot.Sequence);
            writer.WriteDouble(snapshot.WorldTime);
            writer.WriteInt32(snapshot.ObjectStates.Count);

            foreach (var kvp in snapshot.ObjectStates)
            {
                var state = kvp.Value;
                writer.WriteUInt32(kvp.Key);
                writer.WriteUInt16(state.TypeId);
                writer.WriteUInt32((uint)state.ChangedFlags);
                WriteObjectState(writer, state);
            }

            return Compress(writer.ToArray());
        }

        /// <summary>
        /// 从字节导入快照
        /// </summary>
        public SyncStateSnapshot ImportSnapshot(byte[] data)
        {
            data = Decompress(data);
            if (data == null) return null;

            var reader = new NetworkReader(data);
            var snapshot = new SyncStateSnapshot
            {
                Sequence = reader.ReadUInt32(),
                WorldTime = reader.ReadDouble()
            };

            int count = reader.ReadInt32();
            for (int i = 0; i < count; i++)
            {
                var state = ReadFullObjectState(reader);
                if (state != null)
                {
                    snapshot.ObjectStates[state.ObjectId] = state;
                }
            }

            return snapshot;
        }

        // ==================== 工具方法 ====================

        /// <summary>
        /// 计算变化标记
        /// </summary>
        private StateFlags ComputeChangedFlags(ObjectState current, ObjectState previous)
        {
            var flags = StateFlags.None;

            if (previous == null) return StateFlags.All;

            if (current.Position != previous.Position)
            {
                flags |= StateFlags.Position;
            }

            if (current.Rotation != previous.Rotation)
            {
                flags |= StateFlags.Rotation;
            }

            if (current.Scale != previous.Scale)
            {
                flags |= StateFlags.Scale;
            }

            if (current.Velocity != previous.Velocity)
            {
                flags |= StateFlags.Velocity;
            }

            if (Math.Abs(current.Health - previous.Health) > 0.001f ||
                Math.Abs(current.MaxHealth - previous.MaxHealth) > 0.001f)
            {
                flags |= StateFlags.Health;
            }

            if (current.AnimationState != previous.AnimationState ||
                Math.Abs(current.AnimationTime - previous.AnimationTime) > 0.01f)
            {
                flags |= StateFlags.Animation;
            }

            return flags;
        }

        /// <summary>
        /// 量化浮点数
        /// </summary>
        private float Quantize(float value, float precision)
        {
            return (float)Math.Round(value / precision) * precision;
        }

        /// <summary>
        /// 压缩数据
        /// </summary>
        private byte[] Compress(byte[] data)
        {
            try
            {
                using (var ms = new MemoryStream())
                using (var gzip = new GZipStream(ms, CompressionLevel.Fastest))
                {
                    gzip.Write(data, 0, data.Length);
                    gzip.Flush();
                    return ms.ToArray();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Synchronizer] 压缩失败: {ex.Message}");
                return data;
            }
        }

        /// <summary>
        /// 解压数据
        /// </summary>
        private byte[] Decompress(byte[] data)
        {
            try
            {
                using (var ms = new MemoryStream(data))
                using (var gzip = new GZipStream(ms, CompressionMode.Decompress))
                using (var output = new MemoryStream())
                {
                    gzip.CopyTo(output);
                    return output.ToArray();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Synchronizer] 解压失败: {ex.Message}");
                return null;
            }
        }

        // ==================== 统计 ====================

        /// <summary>
        /// 获取统计信息
        /// </summary>
        public SyncStatistics GetStatistics()
        {
            lock (_statsLock)
            {
                return new SyncStatistics
                {
                    FullSyncCount = _stats.FullSyncCount,
                    DeltaSyncCount = _stats.DeltaSyncCount,
                    FullSyncBytes = _stats.FullSyncBytes,
                    DeltaSyncBytes = _stats.DeltaSyncBytes,
                    SnapshotSaveCount = _stats.SnapshotSaveCount,
                    SnapshotRestoreCount = _stats.SnapshotRestoreCount
                };
            }
        }

        /// <summary>
        /// 获取统计报告
        /// </summary>
        public string GetStatisticsReport()
        {
            var stats = GetStatistics();
            var sb = new StringBuilder();
            sb.AppendLine("===== 状态同步统计 =====");
            sb.AppendLine($"全量同步: {stats.FullSyncCount}次, 平均{stats.AverageFullSize:F0}字节");
            sb.AppendLine($"Delta同步: {stats.DeltaSyncCount}次, 平均{stats.AverageDeltaSize:F0}字节");
            sb.AppendLine($"压缩率: {stats.CompressionRatio:P1}");
            sb.AppendLine($"快照保存: {stats.SnapshotSaveCount}次");
            sb.AppendLine($"快照恢复: {stats.SnapshotRestoreCount}次");
            sb.AppendLine($"当前对象数: {_currentState.Count}");
            sb.AppendLine($"快照缓冲: {_snapshotCount}/{_maxSnapshots}");
            return sb.ToString();
        }

        // ==================== Dispose ====================

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                Stop();

                if (_transport != null)
                {
                    _transport.OnPacketReceived -= OnPacketReceived;
                }

                lock (_stateLock)
                {
                    _currentState.Clear();
                    _previousState.Clear();
                    _snapshotBuffer = null;
                }
            }
        }

        ~NetworkSynchronizer()
        {
            Dispose(false);
        }
    }
}
