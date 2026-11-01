using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;

namespace YamaEngine.Network
{
    // ════════════════════════════════════════════════════════════════════════
    // yama引擎 - 网络预测/回滚系统 (NetworkPredictionSystem)
    //
    // 类似 UE Network Prediction / GGPO 的完整预测回滚管线:
    //   * NetworkTick: 网络帧 (帧号/时间戳/Delta时间)
    //   * TickBuffer<T>: 帧缓冲 (环形缓冲区/按帧号查找/容量/清除旧帧)
    //   * InputState: 输入状态 (帧号+输入数据+时间戳)
    //   * InputHistoryBuffer: 输入历史缓冲
    //   * StateHistoryBuffer: 状态历史缓冲
    //   * ClientPrediction: 客户端预测 (保存输入历史/预测移动/服务端校正)
    //   * ServerReconciliation: 服务端协调 (回放确认帧/纠正位置/平滑插值)
    //   * RollbackNetcode: 回滚网码 (状态快照/回滚+重放)
    //   * LagCompensation: 延迟补偿 (服务端回退玩家视角的碰撞检测)
    //   * NetworkSmoothing: 插值/外推 (位置/旋转平滑/速度外推)
    //   * JitterBuffer: 抖动缓冲 (均衡网络延迟抖动)
    //   * NetworkSimulatedTime: 模拟时间 (同步服务端/时间窗口/回退保护)
    //
    // 对接系统: NetworkCoreSystem / NetworkObjectSystem
    // ════════════════════════════════════════════════════════════════════════

    // ──────────────────────────────────────────────────────────────
    // 1. 网络帧
    // ──────────────────────────────────────────────────────────────

    /// <summary>网络帧。包含帧号、时间戳和 Delta 时间。</summary>
    public readonly struct NetworkTick
    {
        /// <summary>帧号 (单调递增)。</summary>
        public int TickNumber { get; }

        /// <summary>帧时间戳 (秒)。</summary>
        public float Timestamp { get; }

        /// <summary>帧间 Delta 时间 (秒)。</summary>
        public float DeltaTime { get; }

        public NetworkTick(int tickNumber, float timestamp, float deltaTime)
        {
            TickNumber = tickNumber;
            Timestamp = timestamp;
            DeltaTime = deltaTime;
        }

        /// <summary>计算两个帧之间的帧差。</summary>
        public int Difference(NetworkTick other) => TickNumber - other.TickNumber;

        /// <summary>帧号是否在指定范围内。</summary>
        public bool IsInRange(int minTick, int maxTick) =>
            TickNumber >= minTick && TickNumber <= maxTick;
    }

    // ──────────────────────────────────────────────────────────────
    // 2. 帧缓冲 (环形缓冲区)
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 泛型帧缓冲区。环形缓冲区实现, 按帧号存储和查找数据。
    /// </summary>

    // ──────────────────────────────────────────────────────────────
    // 3. 输入状态与历史
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 输入状态。每帧的玩家输入数据。
    /// </summary>
    public struct InputState
    {
        /// <summary>帧号。</summary>
        public int TickNumber;

        /// <summary>移动方向 (标准化向量)。</summary>
        public Vector2 MoveDirection;

        /// <summary>视角旋转增量。</summary>
        public Vector2 LookDelta;

        /// <summary>跳跃输入。</summary>
        public bool Jump;

        /// <summary>冲刺输入。</summary>
        public bool Sprint;

        /// <summary>攻击输入。</summary>
        public bool Attack;

        /// <summary>交互输入。</summary>
        public bool Interact;

        /// <summary>输入时间戳 (秒)。</summary>
        public float Timestamp;

        /// <summary>原始输入数据 (自定义扩展)。</summary>
        public byte[]? RawData;

        public InputState(int tickNumber)
        {
            TickNumber = tickNumber;
            MoveDirection = Vector2.Zero;
            LookDelta = Vector2.Zero;
            Jump = false;
            Sprint = false;
            Attack = false;
            Interact = false;
            Timestamp = 0;
            RawData = null;
        }

        /// <summary>序列化输入状态。</summary>
        public void Serialize(NetworkWriter writer)
        {
            writer.WriteInt(TickNumber);
            writer.WriteVector2(MoveDirection);
            writer.WriteVector2(LookDelta);
            byte flags = 0;
            if (Jump) flags |= 0x01;
            if (Sprint) flags |= 0x02;
            if (Attack) flags |= 0x04;
            if (Interact) flags |= 0x08;
            writer.WriteByte(flags);
        }

        /// <summary>反序列化输入状态。</summary>
        public void Deserialize(NetworkReader reader)
        {
            TickNumber = reader.ReadInt();
            MoveDirection = reader.ReadVector2();
            LookDelta = reader.ReadVector2();
            byte flags = reader.ReadByte();
            Jump = (flags & 0x01) != 0;
            Sprint = (flags & 0x02) != 0;
            Attack = (flags & 0x04) != 0;
            Interact = (flags & 0x08) != 0;
        }
    }

    /// <summary>
    /// 输入历史缓冲。存储客户端发送的所有输入状态。
    /// </summary>
    public sealed class InputHistoryBuffer
    {
        private readonly TickBuffer<InputState> _buffer;
        private int _lastSentTick;

        /// <summary>缓冲区容量。</summary>
        public int Capacity => _buffer.Capacity;

        /// <summary>最新帧号。</summary>
        public int LatestTick => _buffer.LatestTick;

        public InputHistoryBuffer(int capacity = 120)
        {
            _buffer = new TickBuffer<InputState>(capacity);
        }

        /// <summary>添加输入状态。</summary>
        public void AddInput(InputState input)
        {
            _buffer.Add(input.TickNumber, input);
            _lastSentTick = input.TickNumber;
        }

        /// <summary>获取指定帧号的输入。</summary>
        public InputState GetInput(int tickNumber) => _buffer.Get(tickNumber);

        /// <summary>获取最近 N 帧的输入。</summary>
        public List<InputState> GetRecentInputs(int count)
        {
            var inputs = new List<InputState>();
            int fromTick = Math.Max(_buffer.OldestTick, _buffer.LatestTick - count + 1);
            var range = _buffer.GetRange(fromTick, _buffer.LatestTick);
            foreach (var (_, data) in range)
                inputs.Add(data);
            return inputs;
        }

        /// <summary>获取需要重发的输入 (服务端未确认的)。</summary>
        public List<InputState> GetUnacknowledgedInputs(int serverAckTick)
        {
            var result = new List<InputState>();
            for (int t = serverAckTick + 1; t <= _buffer.LatestTick; t++)
            {
                if (_buffer.Contains(t))
                    result.Add(_buffer.Get(t));
            }
            return result;
        }

        /// <summary>清除旧于指定帧号的输入。</summary>
        public void ClearOlderThan(int tickNumber) => _buffer.ClearOlderThan(tickNumber);
    }

    // ──────────────────────────────────────────────────────────────
    // 4. 状态快照与历史
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 完整游戏状态快照。用于回滚和重放。
    /// </summary>
    public struct StateSnapshot
    {
        /// <summary>帧号。</summary>
        public int TickNumber;

        /// <summary>位置。</summary>
        public Vector3 Position;

        /// <summary>旋转。</summary>
        public Vector3 Rotation; // 简化为欧拉角

        /// <summary>速度。</summary>
        public Vector3 Velocity;

        /// <summary>时间戳。</summary>
        public float Timestamp;

        public StateSnapshot(int tickNumber, Vector3 position, Vector3 rotation,
            Vector3 velocity, float timestamp)
        {
            TickNumber = tickNumber;
            Position = position;
            Rotation = rotation;
            Velocity = velocity;
            Timestamp = timestamp;
        }
    }

    /// <summary>
    /// 状态历史缓冲。存储历史状态快照用于回滚。
    /// </summary>
    public sealed class StateHistoryBuffer
    {
        private readonly TickBuffer<StateSnapshot> _buffer;

        /// <summary>最新可用帧号。</summary>
        public int LatestTick => _buffer.LatestTick;

        /// <summary>最旧可用帧号。</summary>
        public int OldestTick => _buffer.OldestTick;

        public StateHistoryBuffer(int capacity = 300)
        {
            _buffer = new TickBuffer<StateSnapshot>(capacity);
        }

        /// <summary>添加状态快照。</summary>
        public void AddSnapshot(StateSnapshot snapshot) => _buffer.Add(snapshot.TickNumber, snapshot);

        /// <summary>获取指定帧的状态快照。</summary>
        public StateSnapshot GetSnapshot(int tickNumber) => _buffer.Get(tickNumber);

        /// <summary>获取指定范围的状态快照。</summary>
        public List<StateSnapshot> GetRange(int fromTick, int toTick)
        {
            var result = new List<StateSnapshot>();
            var range = _buffer.GetRange(fromTick, toTick);
            foreach (var (_, data) in range)
                result.Add(data);
            return result;
        }

        /// <summary>清除旧快照。</summary>
        public void ClearOlderThan(int tickNumber) => _buffer.ClearOlderThan(tickNumber);
    }

    // ──────────────────────────────────────────────────────────────
    // 5. 客户端预测
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 客户端预测系统。本地立即模拟输入, 保存历史,
    /// 收到服务端确认后校正位置。
    /// </summary>
    public sealed class ClientPrediction
    {
        /// <summary>输入历史。</summary>
        public InputHistoryBuffer InputHistory { get; } = new InputHistoryBuffer(120);

        /// <summary>状态历史。</summary>
        public StateHistoryBuffer StateHistory { get; } = new StateHistoryBuffer(300);

        /// <summary>当前预测位置。</summary>
        public Vector3 PredictedPosition { get; private set; }

        /// <summary>当前预测旋转。</summary>
        public Vector3 PredictedRotation { get; private set; }

        /// <summary>当前速度。</summary>
        public Vector3 Velocity { get; private set; }

        /// <summary>是否已收到服务端确认。</summary>
        public bool HasServerConfirmation { get; private set; }

        /// <summary>最后服务端确认帧号。</summary>
        public int LastServerAckTick { get; private set; }

        /// <summary>记录的预测校正次数。</summary>
        public int CorrectionCount { get; private set; }

        /// <summary>校正事件。</summary>
        public event Action<int, Vector3, Vector3, Vector3>? OnCorrection;

        /// <summary>记录当前帧的预测状态。</summary>
        public void RecordPrediction(int tickNumber, Vector3 position, Vector3 rotation,
            Vector3 velocity, float timestamp)
        {
            PredictedPosition = position;
            PredictedRotation = rotation;
            Velocity = velocity;

            StateHistory.AddSnapshot(new StateSnapshot(
                tickNumber, position, rotation, velocity, timestamp));
        }

        /// <summary>
        /// 收到服务端状态确认。如果预测与服务端不一致,
        /// 从确认帧开始重放未确认的输入。
        /// </summary>
        public void OnServerStateReceived(int serverTick, Vector3 serverPosition,
            Vector3 serverRotation, float serverTimestamp)
        {
            LastServerAckTick = serverTick;
            HasServerConfirmation = true;

            // 检查是否需要校正
            if (StateHistory.GetSnapshot(serverTick).TickNumber == serverTick)
            {
                var predicted = StateHistory.GetSnapshot(serverTick);
                float positionError = Vector3.Distance(predicted.Position, serverPosition);

                if (positionError > 0.1f) // 校正阈值
                {
                    // 需要校正
                    CorrectionCount++;
                    Vector3 oldPos = PredictedPosition;

                    // 从服务端确认的帧开始, 重放所有未确认的输入
                    Vector3 correctedPos = serverPosition;
                    Vector3 correctedRot = serverRotation;

                    for (int t = serverTick + 1; t <= InputHistory.LatestTick; t++)
                    {
                        if (InputHistory.GetInput(t).TickNumber == t)
                        {
                            var input = InputHistory.GetInput(t);
                            // 模拟预测移动 (简化)
                            correctedPos += new Vector3(
                                input.MoveDirection.X * 5f * 0.016f,
                                0,
                                input.MoveDirection.Y * 5f * 0.016f);
                        }
                    }

                    PredictedPosition = correctedPos;
                    PredictedRotation = correctedRot;

                    OnCorrection?.Invoke(serverTick, oldPos, correctedPos, serverPosition);

                    // 清理已确认的输入
                    InputHistory.ClearOlderThan(serverTick);
                    StateHistory.ClearOlderThan(serverTick);
                }
                else
                {
                    // 预测准确, 只需清理旧数据
                    InputHistory.ClearOlderThan(serverTick);
                    StateHistory.ClearOlderThan(serverTick);
                }
            }
        }
    }

    // ──────────────────────────────────────────────────────────────
    // 6. 服务端协调
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 服务端协调系统。维护权威状态, 确认客户端输入,
    /// 发送校正消息。
    /// </summary>
    public sealed class ServerReconciliation
    {
        private int _currentTick;
        private readonly Dictionary<int, int> _clientAckTicks = new Dictionary<int, int>();

        /// <summary>当前服务端帧号。</summary>
        public int CurrentTick => _currentTick;

        /// <summary>记录客户端输入并推进帧。</summary>
        public void ProcessInput(int connectionId, InputState input)
        {
            // 存储客户端最后确认的输入帧
            _clientAckTicks[connectionId] = input.TickNumber;
        }

        /// <summary>推进服务端帧。</summary>
        public void AdvanceTick() => _currentTick++;

        /// <summary>获取客户端最后确认的帧号。</summary>
        public int GetClientAckTick(int connectionId)
        {
            return _clientAckTicks.TryGetValue(connectionId, out var tick) ? tick : 0;
        }
    }

    // ──────────────────────────────────────────────────────────────
    // 7. 回滚网码
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 回滚网码系统。类似 GGPO, 保存完整状态快照,
    /// 当服务端状态不一致时回滚到指定帧并重放输入。
    /// </summary>
    public sealed class RollbackNetcode
    {
        private readonly StateHistoryBuffer _stateHistory = new StateHistoryBuffer(600);
        private readonly InputHistoryBuffer _inputHistory = new InputHistoryBuffer(600);

        /// <summary>最大回滚帧数。</summary>
        public int MaxRollbackFrames { get; set; } = 15;

        /// <summary>回滚事件。</summary>
        public event Action<int, int>? OnRollback;

        /// <summary>回滚次数统计。</summary>
        public int RollbackCount { get; private set; }

        /// <summary>保存状态快照。</summary>
        public void SaveState(StateSnapshot snapshot)
        {
            _stateHistory.AddSnapshot(snapshot);
        }

        /// <summary>保存输入。</summary>
        public void SaveInput(InputState input)
        {
            _inputHistory.AddInput(input);
        }

        /// <summary>
        /// 执行回滚。从指定帧开始重放输入到当前帧。
        /// </summary>
        public StateSnapshot RollbackTo(int targetTick, int currentTick,
            Func<StateSnapshot, InputState, StateSnapshot> simulateFunc)
        {
            int rollbackFrames = currentTick - targetTick;
            if (rollbackFrames <= 0 || rollbackFrames > MaxRollbackFrames)
            {
                return _stateHistory.GetSnapshot(currentTick);
            }

            RollbackCount++;
            OnRollback?.Invoke(targetTick, currentTick);

            // 获取目标帧的状态
            var state = _stateHistory.GetSnapshot(targetTick);

            // 从目标帧+1 到当前帧, 依次重放输入
            for (int t = targetTick + 1; t <= currentTick; t++)
            {
                if (_inputHistory.GetInput(t).TickNumber == t)
                {
                    var input = _inputHistory.GetInput(t);
                    state = simulateFunc(state, input);
                }
            }

            return state;
        }
    }

    // ──────────────────────────────────────────────────────────────
    // 8. 延迟补偿
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 延迟补偿系统。服务端回退到玩家视角时刻的世界状态,
    /// 进行碰撞检测等判定。解决"命中判定延迟"问题。
    /// </summary>
    public sealed class LagCompensation
    {
        private readonly StateHistoryBuffer _stateHistory = new StateHistoryBuffer(120);

        /// <summary>最大补偿延迟 (秒)。</summary>
        public float MaxCompensationDelay { get; set; } = 0.5f;

        /// <summary>添加状态快照。</summary>
        public void RecordState(StateSnapshot snapshot) => _stateHistory.AddSnapshot(snapshot);

        /// <summary>
        /// 获取玩家在指定延迟前的状态。
        /// </summary>
        public StateSnapshot GetHistoricalState(int connectionId, float rttSeconds)
        {
            float delay = Math.Min(rttSeconds / 2f, MaxCompensationDelay);
            float targetTimestamp = DateTime.UtcNow.Ticks / 10000000f - delay;

            // 查找最接近的时间戳对应的状态
            int bestTick = -1;
            float bestDiff = float.MaxValue;

            for (int t = _stateHistory.GetSnapshot(_stateHistory.LatestTick).TickNumber;
                 t >= _stateHistory.GetSnapshot(_stateHistory.OldestTick).TickNumber; t--)
            {
                var snapshot = _stateHistory.GetSnapshot(t);
                float diff = Math.Abs(snapshot.Timestamp - targetTimestamp);
                if (diff < bestDiff)
                {
                    bestDiff = diff;
                    bestTick = t;
                }
            }

            return bestTick >= 0 ? _stateHistory.GetSnapshot(bestTick) : default;
        }
    }

    // ──────────────────────────────────────────────────────────────
    // 9. 网络平滑
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 网络平滑系统。对从网络接收的位置/旋转进行插值和外推,
    /// 消除网络更新频率低于渲染频率导致的抖动。
    /// </summary>
    public sealed class NetworkSmoothing
    {
        /// <summary>插值模式。</summary>
        public enum SmoothingMode
        {
            /// <summary>仅插值 (平滑但延迟)。</summary>
            Interpolation,
            /// <summary>插值+外推 (减少延迟但可能不准确)。</summary>
            Extrapolation
        }

        private Vector3 _currentPosition;
        private Vector3 _targetPosition;
        private Vector3 _previousTargetPosition;
        private Quaternion _currentRotation;
        private Quaternion _targetRotation;

        private Vector3 _startPosition;
        private Quaternion _startRotation;
        private float _interpolationTime;
        private float _interpolationDuration = 0.1f;

        private Vector3 _extrapolatedVelocity;

        /// <summary>平滑模式。</summary>
        public SmoothingMode Mode { get; set; } = SmoothingMode.Interpolation;

        /// <summary>插值速度 (越大越快, 1.0 = 瞬间到达)。</summary>
        public float InterpolationSpeed { get; set; } = 10f;

        /// <summary>外推系数 (0~1, 越大预测越远)。</summary>
        public float ExtrapolationFactor { get; set; } = 0.5f;

        /// <summary>当前位置。</summary>
        public Vector3 CurrentPosition => _currentPosition;

        /// <summary>当前旋转。</summary>
        public Quaternion CurrentRotation => _currentRotation;

        /// <summary>是否有新目标。</summary>
        public bool HasNewTarget { get; private set; }

        /// <summary>接收新的网络位置。</summary>
        public void ReceiveTarget(Vector3 position, Quaternion rotation, float deltaTime)
        {
            _previousTargetPosition = _targetPosition;
            _targetPosition = position;

            // 计算外推速度
            if (deltaTime > 0f)
                _extrapolatedVelocity = (position - _previousTargetPosition) / deltaTime;

            _startPosition = _currentPosition;
            _startRotation = _currentRotation;
            _targetRotation = rotation;
            _interpolationTime = 0f;
            HasNewTarget = true;
        }

        /// <summary>每帧更新平滑。</summary>
        public void Update(float deltaTime)
        {
            if (!HasNewTarget) return;

            _interpolationTime += deltaTime;

            float t = Math.Clamp(_interpolationTime / _interpolationDuration, 0f, 1f);

            switch (Mode)
            {
                case SmoothingMode.Interpolation:
                    // 球面插值旋转
                    _currentRotation = Quaternion.Slerp(
                        _startRotation, _targetRotation, t);
                    // 线性插值位置
                    _currentPosition = Vector3.Lerp(
                        _startPosition, _targetPosition, t);
                    break;

                case SmoothingMode.Extrapolation:
                    _currentRotation = Quaternion.Slerp(
                        _startRotation, _targetRotation, t);
                    // 插值 + 速度外推
                    Vector3 interpolated = Vector3.Lerp(
                        _startPosition, _targetPosition, t);
                    Vector3 extrapolated = _targetPosition + _extrapolatedVelocity *
                        (deltaTime * ExtrapolationFactor);
                    _currentPosition = Vector3.Lerp(interpolated, extrapolated,
                        ExtrapolationFactor);
                    break;
            }

            // 检查是否完成
            if (_interpolationTime >= _interpolationDuration)
                HasNewTarget = false;
        }

        /// <summary>重置。</summary>
        public void Reset(Vector3 position, Quaternion rotation)
        {
            _currentPosition = position;
            _targetPosition = position;
            _previousTargetPosition = position;
            _currentRotation = rotation;
            _targetRotation = rotation;
            _startPosition = position;
            _startRotation = rotation;
            HasNewTarget = false;
            _interpolationTime = 0f;
            _extrapolatedVelocity = Vector3.Zero;
        }
    }

    // ──────────────────────────────────────────────────────────────
    // 10. 抖动缓冲
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 抖动缓冲器。均衡网络延迟的抖动, 提供平滑的数据交付。
    /// 缓存接收到的数据, 延迟一段时间后再释放。
    /// </summary>
    public sealed class JitterBuffer<T>
    {
        private readonly Queue<(T data, float receiveTime)> _queue = new Queue<(T, float)>();
        private readonly int _maxSize;
        private readonly float _bufferDelay;

        /// <summary>缓冲区大小。</summary>
        public int Count => _queue.Count;

        /// <summary>平均延迟 (秒)。</summary>
        public float AverageDelay { get; private set; }

        private float _totalDelay;
        private int _sampleCount;

        /// <summary>
        /// 创建抖动缓冲器。
        /// </summary>
        /// <param name="bufferDelay">缓冲延迟 (秒), 通常设为平均 RTT 的一半。</param>
        /// <param name="maxSize">最大缓冲数据量。</param>
        public JitterBuffer(float bufferDelay = 0.05f, int maxSize = 64)
        {
            _bufferDelay = bufferDelay;
            _maxSize = maxSize;
        }

        /// <summary>添加数据到抖动缓冲。</summary>
        public void Enqueue(T data, float receiveTime)
        {
            if (_queue.Count >= _maxSize)
                _queue.Dequeue(); // 丢弃最旧数据

            _queue.Enqueue((data, receiveTime));
        }

        /// <summary>
        /// 尝试取出数据。只有在数据在缓冲中停留超过 bufferDelay 后才释放。
        /// </summary>
        public bool TryDequeue(float currentTime, out T data)
        {
            data = default!;

            while (_queue.Count > 0)
            {
                var (item, receiveTime) = _queue.Peek();
                float delay = currentTime - receiveTime;

                if (delay >= _bufferDelay)
                {
                    _queue.Dequeue();
                    data = item;

                    // 统计延迟
                    _totalDelay += delay;
                    _sampleCount++;
                    AverageDelay = _totalDelay / _sampleCount;
                    return true;
                }

                // 还没到释放时间
                return false;
            }

            return false;
        }

        /// <summary>立即释放所有数据 (用于降低延迟)。</summary>
        public List<T> Flush()
        {
            var result = new List<T>();
            while (_queue.Count > 0)
                result.Add(_queue.Dequeue().data);
            return result;
        }

        /// <summary>清空缓冲。</summary>
        public void Clear()
        {
            _queue.Clear();
        }
    }

    // ──────────────────────────────────────────────────────────────
    // 11. 网络模拟时间
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 网络模拟时间。将客户端模拟时间同步到服务端,
    /// 提供时间窗口和回退保护。
    /// </summary>
    public sealed class NetworkSimulatedTime
    {
        /// <summary>当前模拟时间 (秒)。</summary>
        public float CurrentTime { get; private set; }

        /// <summary>服务端时间 (上次同步的)。</summary>
        public float ServerTime { get; private set; }

        /// <summary>单程延迟估算 (秒)。</summary>
        public float OneWayLatency { get; private set; }

        /// <summary>模拟时间偏移 (客户端时间 - 服务端时间)。</summary>
        public float TimeOffset { get; private set; }

        /// <summary>时间窗口 (允许的时间偏差)。</summary>
        public float TimeWindow { get; set; } = 0.1f;

        /// <summary>是否已同步。</summary>
        public bool IsSynchronized { get; private set; }

        /// <summary>时间同步事件。</summary>
        public event Action<float, float>? OnTimeSynced;

        private float _smoothFactor = 0.1f;

        /// <summary>推进模拟时间。</summary>
        public void Advance(float deltaTime)
        {
            CurrentTime += deltaTime;
        }

        /// <summary>
        /// 接收服务端时间同步。
        /// </summary>
        /// <param name="serverTime">服务端当前时间。</param>
        /// <param name="rttSeconds">往返时延。</param>
        public void ReceiveServerTime(float serverTime, float rttSeconds)
        {
            float estimatedServerTime = serverTime + rttSeconds / 2f;
            float newOffset = CurrentTime - estimatedServerTime;

            // 平滑偏移
            TimeOffset = TimeOffset * (1f - _smoothFactor) + newOffset * _smoothFactor;
            ServerTime = serverTime;
            OneWayLatency = rttSeconds / 2f;

            // 检查时间窗口
            IsSynchronized = Math.Abs(TimeOffset) < TimeWindow;

            OnTimeSynced?.Invoke(serverTime, TimeOffset);
        }

        /// <summary>回退保护。不允许时间回退。</summary>
        public bool TrySetTime(float newTime)
        {
            if (newTime < CurrentTime - TimeWindow)
                return false; // 时间回退太多, 拒绝
            CurrentTime = newTime;
            return true;
        }

        /// <summary>重置。</summary>
        public void Reset()
        {
            CurrentTime = 0f;
            ServerTime = 0f;
            OneWayLatency = 0f;
            TimeOffset = 0f;
            IsSynchronized = false;
        }
    }
}
