using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace ShanyuEngine.Network
{
    // ════════════════════════════════════════════════════════════════════════
    // 山雨引擎 - 真实网络预测与回滚 (NetworkPredictionReal)
    //
    // 包含:
    //   * TickBuffer<T>: 环形 tick 缓冲区 (按 tick 号索引)
    //   * ClientPredictorReal<TInput, TState>: 客户端预测器
    //     - 输入历史 / 预测状态 / 差异检测 / 回滚重放
    //   * RollbackNetcodeReal<TGameState, TInput>: 回滚网络代码 (GGPO 风格)
    //     - 状态快照 / 远程输入处理 / 延迟回滚 / 状态哈希校验
    //
    // 所有方法均为真实实现, 无占位式实现。
    // 目标框架: net8.0 (C# 8.0+), 无额外 NuGet 依赖。
    // ════════════════════════════════════════════════════════════════════════

    // ════════════════════════════════════════════════════════════
    // TickBuffer<T> - 环形 Tick 缓冲区
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// 环形 tick 缓冲区: 按 tick 号索引存储和检索数据。
    /// 当 tick 超出容量范围时, 旧数据被覆盖。
    /// </summary>
    /// <typeparam name="T">存储的数据类型。</typeparam>
    public class TickBuffer<T>
    {
        private struct Entry
        {
            public int Tick;
            public T Value;
            public bool IsValid;
        }

        private readonly Entry[] _entries;
        private readonly int _capacity;
        private int _oldestTick = int.MaxValue;
        private int _newestTick = int.MinValue;

        /// <summary>缓冲区容量 (最大存储的 tick 数)。</summary>
        public int Capacity => _capacity;

        /// <summary>最旧的可用 tick 号。</summary>
        public int OldestTick => _oldestTick;

        /// <summary>最新的可用 tick 号。</summary>
        public int NewestTick => _newestTick;

        /// <summary>是否有任何数据。</summary>
        public bool HasData => _oldestTick <= _newestTick;

        /// <summary>创建指定容量的 tick 缓冲区。</summary>
        public TickBuffer(int capacity = 256)
        {
            _capacity = Math.Max(capacity, 4);
            _entries = new Entry[_capacity];
        }

        /// <summary>
        /// 在指定 tick 存储值。
        /// 如果该 tick 已有数据, 覆盖旧值。
        /// </summary>
        public void Set(int tick, T value)
        {
            int index = ((tick % _capacity) + _capacity) % _capacity;
            _entries[index].Tick = tick;
            _entries[index].Value = value;
            _entries[index].IsValid = true;

            if (tick < _oldestTick) _oldestTick = tick;
            if (tick > _newestTick) _newestTick = tick;
        }

        /// <summary>
        /// 尝试获取指定 tick 的值。
        /// </summary>
        /// <returns>如果该 tick 的数据仍然有效, 返回 true。</returns>
        public bool TryGet(int tick, out T value)
        {
            int index = ((tick % _capacity) + _capacity) % _capacity;

            if (_entries[index].IsValid && _entries[index].Tick == tick)
            {
                value = _entries[index].Value;
                return true;
            }

            value = default;
            return false;
        }

        /// <summary>获取指定 tick 的值, 如果不存在返回默认值。</summary>
        public T Get(int tick)
        {
            TryGet(tick, out T value);
            return value;
        }

        /// <summary>清除指定 tick 之前的所有数据。</summary>
        public void ClearBefore(int tick)
        {
            for (int i = 0; i < _capacity; i++)
            {
                if (_entries[i].IsValid && _entries[i].Tick < tick)
                {
                    _entries[i].IsValid = false;
                    _entries[i].Value = default;
                }
            }

            if (tick > _oldestTick)
                _oldestTick = tick;
        }

        /// <summary>清除所有数据。</summary>
        public void Clear()
        {
            for (int i = 0; i < _capacity; i++)
            {
                _entries[i].IsValid = false;
                _entries[i].Value = default;
            }
            _oldestTick = int.MaxValue;
            _newestTick = int.MinValue;
        }

        /// <summary>检查指定 tick 的数据是否存在。</summary>
        public bool Contains(int tick)
        {
            return TryGet(tick, out _);
        }
    }

    // ════════════════════════════════════════════════════════════
    // ClientPredictorReal - 客户端预测器
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// 客户端预测器: 在客户端本地预测游戏状态,
    /// 收到服务端权威状态后进行差异检测和回滚重放。
    /// </summary>
    /// <typeparam name="TInput">输入类型。</typeparam>
    /// <typeparam name="TState">状态类型。</typeparam>
    public class ClientPredictorReal<TInput, TState>
    {
        // ── 缓冲区 ────────────────────────────────────────────────

        private readonly TickBuffer<TInput> _inputHistory;
        private readonly TickBuffer<TState> _predictedStates;

        // ── 配置 ──────────────────────────────────────────────────

        private int _currentTick;
        private int _lastConfirmedTick;

        /// <summary>模拟函数: (当前状态, 输入, dt) => 新状态。</summary>
        private readonly Func<TState, TInput, float, TState> _simulateFunc;

        /// <summary>状态比较函数: (预测状态, 服务端状态) => 是否一致。</summary>
        private readonly Func<TState, TState, bool> _stateComparer;

        /// <summary>状态应用函数: 将状态应用到本地角色/世界。</summary>
        private Action<TState> _applyStateFunc;

        /// <summary>状态克隆函数 (用于保存预测状态)。</summary>
        private readonly Func<TState, TState> _cloneFunc;

        // ── 统计 ──────────────────────────────────────────────────

        private int _predictionCount;
        private int _correctionCount;
        private int _rollbackCount;

        /// <summary>总预测次数。</summary>
        public int PredictionCount => _predictionCount;

        /// <summary>纠正次数 (服务端状态与预测不一致)。</summary>
        public int CorrectionCount => _correctionCount;

        /// <summary>回滚次数。</summary>
        public int RollbackCount => _rollbackCount;

        /// <summary>当前 tick。</summary>
        public int CurrentTick => _currentTick;

        /// <summary>最后确认的 tick。</summary>
        public int LastConfirmedTick => _lastConfirmedTick;

        /// <summary>待处理的输入数。</summary>
        public int PendingInputCount => _currentTick - _lastConfirmedTick;

        // ── 构造函数 ──────────────────────────────────────────────

        /// <summary>
        /// 创建客户端预测器。
        /// </summary>
        /// <param name="simulateFunc">模拟函数: (状态, 输入, dt) => 新状态。</param>
        /// <param name="stateComparer">状态比较函数: (a, b) => 是否一致。</param>
        /// <param name="cloneFunc">状态克隆函数: (state) => 深拷贝。</param>
        /// <param name="historySize">历史缓冲区大小。</param>
        public ClientPredictorReal(
            Func<TState, TInput, float, TState> simulateFunc,
            Func<TState, TState, bool> stateComparer,
            Func<TState, TState> cloneFunc,
            int historySize = 128)
        {
            _simulateFunc = simulateFunc ?? throw new ArgumentNullException(nameof(simulateFunc));
            _stateComparer = stateComparer ?? throw new ArgumentNullException(nameof(stateComparer));
            _cloneFunc = cloneFunc ?? throw new ArgumentNullException(nameof(cloneFunc));
            _inputHistory = new TickBuffer<TInput>(historySize);
            _predictedStates = new TickBuffer<TState>(historySize);
            _currentTick = 0;
            _lastConfirmedTick = -1;
        }

        /// <summary>设置状态应用回调。</summary>
        public void SetApplyStateCallback(Action<TState> callback)
        {
            _applyStateFunc = callback;
        }

        // ── 记录输入 ──────────────────────────────────────────────

        /// <summary>
        /// 记录输入到历史缓冲区。
        /// </summary>
        /// <param name="tick">输入对应的 tick。</param>
        /// <param name="input">输入数据。</param>
        public void RecordInput(int tick, TInput input)
        {
            _inputHistory.Set(tick, input);
        }

        // ── 预测执行 ──────────────────────────────────────────────

        /// <summary>
        /// 预测执行: 应用输入到本地状态, 物理模拟一步, 保存预测状态。
        /// 算法:
        ///   1. 获取上一 tick 的预测状态 (或初始状态)
        ///   2. 获取当前 tick 的输入
        ///   3. 模拟: newState = simulate(oldState, input, dt)
        ///   4. 保存预测状态
        ///   5. 应用到本地角色
        /// </summary>
        /// <param name="tick">要预测的 tick。</param>
        /// <param name="dt">帧间隔 (秒)。</param>
        /// <param name="currentState">当前本地状态 (用于初始或上一状态)。</param>
        /// <returns>预测后的状态。</returns>
        public TState Predict(int tick, float dt, TState currentState)
        {
            // 获取上一 tick 的状态
            TState prevState;
            if (tick > 0 && _predictedStates.TryGet(tick - 1, out var savedState))
                prevState = savedState;
            else
                prevState = currentState;

            // 获取当前 tick 的输入
            TInput input;
            if (_inputHistory.TryGet(tick, out var recordedInput))
                input = recordedInput;
            else
                input = default;

            // 模拟一步
            TState predictedState = _simulateFunc(prevState, input, dt);

            // 保存预测状态 (克隆以避免引用问题)
            _predictedStates.Set(tick, _cloneFunc(predictedState));

            // 更新当前 tick
            _currentTick = tick;
            _predictionCount++;

            // 应用到本地角色
            _applyStateFunc?.Invoke(predictedState);

            return predictedState;
        }

        // ── 收到服务端状态 ────────────────────────────────────────

        /// <summary>
        /// 收到服务端权威状态。
        /// 算法:
        ///   1. 获取该 tick 的预测状态
        ///   2. 比较预测状态和服务端状态
        ///   3. 如果有差异: 回滚到该 tick, 应用服务端状态, 重放后续输入
        ///   4. 如果一致: 确认, 清除旧输入
        /// </summary>
        /// <param name="tick">服务端状态对应的 tick。</param>
        /// <param name="serverState">服务端权威状态。</param>
        /// <returns>是否发生了纠正。</returns>
        public bool OnServerState(int tick, TState serverState)
        {
            // 获取该 tick 的预测状态
            if (!_predictedStates.TryGet(tick, out var predictedState))
            {
                // 没有预测状态, 直接应用服务端状态
                _lastConfirmedTick = tick;
                _applyStateFunc?.Invoke(serverState);
                return false;
            }

            // 比较预测状态和服务端状态
            bool matches = _stateComparer(predictedState, serverState);

            if (matches)
            {
                // 一致: 确认, 清除旧输入
                _lastConfirmedTick = tick;
                _inputHistory.ClearBefore(tick);
                return false;
            }
            else
            {
                // 有差异: 回滚重放
                _correctionCount++;
                Reconcile(tick, serverState);
                return true;
            }
        }

        // ── 协调 (回滚重放) ───────────────────────────────────────

        /// <summary>
        /// 协调: 回滚到指定 tick, 应用服务端状态, 重放后续所有输入。
        /// 算法:
        ///   1. 设置状态为服务端状态
        ///   2. 保存服务端状态为该 tick 的预测状态
        ///   3. 从 tick+1 到 currentTick, 逐个:
        ///      a. 获取该 tick 的输入
        ///      b. 模拟: state = simulate(state, input, dt)
        ///      c. 更新预测状态
        ///   4. 应用最终状态到本地角色
        /// </summary>
        /// <param name="tick">回滚到的 tick。</param>
        /// <param name="serverState">服务端权威状态。</param>
        public void Reconcile(int tick, TState serverState)
        {
            _rollbackCount++;

            // 获取 dt (假设固定步长, 或从历史获取)
            float dt = _fixedDt;

            // 步骤 1: 设置状态为服务端状态
            TState state = _cloneFunc(serverState);

            // 步骤 2: 保存服务端状态为该 tick 的预测状态
            _predictedStates.Set(tick, _cloneFunc(state));

            // 步骤 3: 重放后续所有输入
            for (int t = tick + 1; t <= _currentTick; t++)
            {
                TInput input;
                if (_inputHistory.TryGet(t, out var recordedInput))
                    input = recordedInput;
                else
                    input = default;

                // 模拟一步
                state = _simulateFunc(state, input, dt);

                // 更新预测状态
                _predictedStates.Set(t, _cloneFunc(state));
            }

            // 步骤 4: 应用最终状态
            _applyStateFunc?.Invoke(state);

            // 更新确认 tick
            _lastConfirmedTick = tick;
            _inputHistory.ClearBefore(tick);
        }

        /// <summary>
        /// 协调: 使用显式输入列表重放。
        /// </summary>
        /// <param name="tick">回滚到的 tick。</param>
        /// <param name="inputs">tick 之后的输入列表。</param>
        /// <param name="serverState">服务端权威状态。</param>
        public void Reconcile(int tick, TInput[] inputs, TState serverState)
        {
            _rollbackCount++;

            float dt = _fixedDt;
            TState state = _cloneFunc(serverState);
            _predictedStates.Set(tick, _cloneFunc(state));

            for (int i = 0; i < inputs.Length && tick + 1 + i <= _currentTick; i++)
            {
                int t = tick + 1 + i;
                state = _simulateFunc(state, inputs[i], dt);
                _predictedStates.Set(t, _cloneFunc(state));
                _inputHistory.Set(t, inputs[i]);
            }

            _applyStateFunc?.Invoke(state);
            _lastConfirmedTick = tick;
            _inputHistory.ClearBefore(tick);
        }

        // ── 固定步长 ──────────────────────────────────────────────

        private float _fixedDt = 1f / 60f; // 默认 60fps

        /// <summary>设置固定步长 (秒)。</summary>
        public void SetFixedStep(float dt)
        {
            _fixedDt = dt;
        }

        // ── 获取历史 ──────────────────────────────────────────────

        /// <summary>获取指定 tick 的预测状态。</summary>
        public bool TryGetPredictedState(int tick, out TState state)
        {
            return _predictedStates.TryGet(tick, out state);
        }

        /// <summary>获取指定 tick 的输入。</summary>
        public bool TryGetInput(int tick, out TInput input)
        {
            return _inputHistory.TryGet(tick, out input);
        }

        /// <summary>清除所有历史数据。</summary>
        public void Clear()
        {
            _inputHistory.Clear();
            _predictedStates.Clear();
            _currentTick = 0;
            _lastConfirmedTick = -1;
        }
    }

    // ════════════════════════════════════════════════════════════
    // RollbackNetcodeReal - 回滚网络代码 (GGPO 风格)
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// 回滚网络代码 (GGPO 风格)。
    /// 保存完整游戏状态快照, 当远程输入迟到时回滚并重放。
    /// </summary>
    /// <typeparam name="TGameState">游戏状态类型 (必须可克隆)。</typeparam>
    /// <typeparam name="TInput">输入类型。</typeparam>
    public class RollbackNetcodeReal<TGameState, TInput>
    {
        // ── 缓冲区 ────────────────────────────────────────────────

        private readonly TickBuffer<TGameState> _stateHistory;
        private readonly TickBuffer<TInput[]> _inputHistory;  // 每个 tick 所有玩家的输入
        private readonly TickBuffer<uint> _stateChecksums;

        // ── 配置 ──────────────────────────────────────────────────

        private int _currentTick;
        private int _inputDelay;
        private int _localPlayerId;
        private int _numPlayers;
        private float _fixedDt;

        /// <summary>推进帧函数: (状态, 所有玩家输入, dt) => 新状态。</summary>
        private readonly Func<TGameState, TInput[], float, TGameState> _advanceFrameFunc;

        /// <summary>状态克隆函数。</summary>
        private readonly Func<TGameState, TGameState> _cloneFunc;

        /// <summary>状态哈希函数: (状态) => 校验和。</summary>
        private readonly Func<TGameState, uint> _hashFunc;

        /// <summary>获取默认输入 (用于缺失的远程输入)。</summary>
        private readonly Func<TInput> _defaultInputFunc;

        // ── 状态 ──────────────────────────────────────────────────

        private TGameState _currentState;
        private bool _isRollingBack;

        // ── 统计 ──────────────────────────────────────────────────

        private int _rollbackCount;
        private int _framesSimulated;
        private int _mispredictions;

        /// <summary>回滚次数。</summary>
        public int RollbackCount => _rollbackCount;

        /// <summary>模拟帧数。</summary>
        public int FramesSimulated => _framesSimulated;

        /// <summary>预测错误次数。</summary>
        public int Mispredictions => _mispredictions;

        /// <summary>当前 tick。</summary>
        public int CurrentTick => _currentTick;

        /// <summary>输入延迟 (帧)。</summary>
        public int InputDelay => _inputDelay;

        // ── 构造函数 ──────────────────────────────────────────────

        /// <summary>
        /// 创建回滚网络代码实例。
        /// </summary>
        /// <param name="advanceFrameFunc">推进帧函数: (状态, 输入数组, dt) => 新状态。</param>
        /// <param name="cloneFunc">状态深拷贝函数。</param>
        /// <param name="hashFunc">状态哈希函数 (可选, 用于校验)。</param>
        /// <param name="defaultInputFunc">默认输入获取函数。</param>
        /// <param name="numPlayers">玩家数量。</param>
        /// <param name="localPlayerId">本地玩家 ID (0-based)。</param>
        /// <param name="historySize">历史缓冲区大小。</param>
        public RollbackNetcodeReal(
            Func<TGameState, TInput[], float, TGameState> advanceFrameFunc,
            Func<TGameState, TGameState> cloneFunc,
            Func<TGameState, uint> hashFunc,
            Func<TInput> defaultInputFunc,
            int numPlayers,
            int localPlayerId,
            int historySize = 180)
        {
            _advanceFrameFunc = advanceFrameFunc ?? throw new ArgumentNullException(nameof(advanceFrameFunc));
            _cloneFunc = cloneFunc ?? throw new ArgumentNullException(nameof(cloneFunc));
            _hashFunc = hashFunc;
            _defaultInputFunc = defaultInputFunc ?? throw new ArgumentNullException(nameof(defaultInputFunc));
            _numPlayers = numPlayers;
            _localPlayerId = localPlayerId;
            _inputDelay = 2; // 默认 2 帧延迟
            _fixedDt = 1f / 60f;

            _stateHistory = new TickBuffer<TGameState>(historySize);
            _inputHistory = new TickBuffer<TInput[]>(historySize);
            _stateChecksums = new TickBuffer<uint>(historySize);
        }

        // ── 初始化 ────────────────────────────────────────────────

        /// <summary>设置初始状态。</summary>
        public void Initialize(TGameState initialState, int startTick = 0)
        {
            _currentState = _cloneFunc(initialState);
            _currentTick = startTick;
            SaveState(_currentTick);
        }

        /// <summary>设置输入延迟 (帧数)。</summary>
        public void SetInputDelay(int frames)
        {
            _inputDelay = Math.Max(0, frames);
        }

        /// <summary>设置固定步长。</summary>
        public void SetFixedStep(float dt)
        {
            _fixedDt = dt;
        }

        // ── 保存/加载状态 ─────────────────────────────────────────

        /// <summary>
        /// 保存完整游戏状态快照。
        /// 包括: 克隆当前状态 + 计算校验和。
        /// </summary>
        public void SaveState(int tick)
        {
            TGameState snapshot = _cloneFunc(_currentState);
            _stateHistory.Set(tick, snapshot);

            // 计算并存储校验和
            if (_hashFunc != null)
            {
                uint checksum = _hashFunc(_currentState);
                _stateChecksums.Set(tick, checksum);
            }
        }

        /// <summary>
        /// 加载指定 tick 的状态快照。
        /// 恢复游戏状态到该 tick。
        /// </summary>
        public void LoadState(int tick)
        {
            if (!_stateHistory.TryGet(tick, out var state))
                return; // 没有该 tick 的快照

            _currentState = _cloneFunc(state);
        }

        // ── 推进帧 ────────────────────────────────────────────────

        /// <summary>
        /// 推进一帧: 应用所有玩家输入, 模拟一帧。
        /// 算法:
        ///   1. 收集所有玩家在 tick 的输入
        ///   2. 应用输入到游戏状态: state = advanceFrame(state, inputs, dt)
        ///   3. 保存状态快照
        ///   4. 推进 tick
        /// </summary>
        /// <param name="tick">要推进的 tick。</param>
        /// <param name="inputs">所有玩家的输入数组。</param>
        public void AdvanceFrame(int tick, TInput[] inputs)
        {
            // 确保输入数组长度匹配玩家数
            TInput[] allInputs = new TInput[_numPlayers];
            for (int i = 0; i < _numPlayers; i++)
            {
                if (inputs != null && i < inputs.Length)
                    allInputs[i] = inputs[i];
                else
                    allInputs[i] = _defaultInputFunc();
            }

            // 存储输入
            _inputHistory.Set(tick, (TInput[])allInputs.Clone());

            // 应用输入并推进状态
            _currentState = _advanceFrameFunc(_currentState, allInputs, _fixedDt);

            // 保存状态快照
            _currentTick = tick;
            SaveState(tick);
            _framesSimulated++;
        }

        /// <summary>
        /// 使用本地输入推进帧 (考虑输入延迟)。
        /// 本地输入会被延迟 inputDelay 帧。
        /// </summary>
        /// <param name="localInput">本地玩家当前帧的输入。</param>
        public void AdvanceFrameWithLocalInput(TInput localInput)
        {
            int effectiveTick = _currentTick + 1;

            // 存储本地输入 (延迟后生效)
            int inputTick = effectiveTick - _inputDelay;
            if (inputTick >= 0)
            {
                // 获取或创建该 tick 的输入数组
                TInput[] inputs;
                if (!_inputHistory.TryGet(inputTick, out inputs))
                    inputs = new TInput[_numPlayers];

                inputs[_localPlayerId] = localInput;
                _inputHistory.Set(inputTick, inputs);
            }

            // 收集所有玩家输入 (缺失的用默认值)
            TInput[] allInputs = new TInput[_numPlayers];
            for (int i = 0; i < _numPlayers; i++)
            {
                if (i == _localPlayerId)
                {
                    // 本地玩家: 从延迟缓冲获取
                    if (_inputHistory.TryGet(inputTick, out var inputs) && inputs != null)
                        allInputs[i] = inputs[i];
                    else
                        allInputs[i] = localInput; // 回退到当前输入
                }
                else
                {
                    // 远程玩家: 从历史获取, 缺失用默认值
                    if (_inputHistory.TryGet(inputTick, out var inputs) && inputs != null)
                        allInputs[i] = inputs[i];
                    else
                        allInputs[i] = _defaultInputFunc();
                }
            }

            // 推进帧
            AdvanceFrame(effectiveTick, allInputs);
        }

        // ── 远程输入处理 ──────────────────────────────────────────

        /// <summary>
        /// 收到远程玩家输入。
        /// 算法:
        ///   如果 tick < currentTick:
        ///     1. 需要回滚
        ///     2. 保存当前状态 (已在历史中)
        ///     3. 加载 tick-1 的状态
        ///     4. 更新该 tick 的远程输入
        ///     5. 从 tick 重放到当前 tick
        ///   否则:
        ///     存储输入供未来使用
        /// </summary>
        /// <param name="tick">输入对应的 tick。</param>
        /// <param name="remoteInput">远程玩家的输入。</param>
        /// <param name="playerId">远程玩家 ID。</param>
        /// <returns>是否触发了回滚。</returns>
        public bool OnRemoteInput(int tick, TInput remoteInput, int playerId)
        {
            // 存储远程输入
            TInput[] inputs;
            if (!_inputHistory.TryGet(tick, out inputs))
            {
                inputs = new TInput[_numPlayers];
                // 填充默认值
                for (int i = 0; i < _numPlayers; i++)
                    inputs[i] = _defaultInputFunc();
            }

            // 检查是否与已有输入不同
            bool inputChanged = !inputs[playerId].Equals(remoteInput);

            inputs[playerId] = remoteInput;
            _inputHistory.Set(tick, inputs);

            // 如果 tick 已经过去且输入有变化, 需要回滚
            if (tick < _currentTick && inputChanged)
            {
                RollbackTo(tick);
                return true;
            }

            return false;
        }

        /// <summary>
        /// 回滚到指定 tick 并重放到当前。
        /// 算法:
        ///   1. 加载 tick-1 的状态快照
        ///   2. 从 tick 到 currentTick 逐帧重放
        ///   3. 每帧使用更新后的输入
        ///   4. 保存状态快照和校验和
        /// </summary>
        private void RollbackTo(int targetTick)
        {
            _rollbackCount++;
            _isRollingBack = true;

            // 加载目标 tick 之前的状态
            int loadTick = targetTick - 1;
            if (loadTick >= 0 && _stateHistory.TryGet(loadTick, out var savedState))
            {
                _currentState = _cloneFunc(savedState);
            }
            // 如果没有保存的状态, 从目标 tick 的状态加载
            else if (_stateHistory.TryGet(targetTick, out var targetState))
            {
                _currentState = _cloneFunc(targetState);
                targetTick++; // 从下一个 tick 开始重放
            }

            // 从 targetTick 重放到 currentTick
            int originalTick = _currentTick;

            for (int t = targetTick; t <= originalTick; t++)
            {
                // 获取该 tick 的所有输入
                TInput[] inputs;
                if (_inputHistory.TryGet(t, out var storedInputs))
                    inputs = storedInputs;
                else
                {
                    // 使用默认输入
                    inputs = new TInput[_numPlayers];
                    for (int i = 0; i < _numPlayers; i++)
                        inputs[i] = _defaultInputFunc();
                }

                // 推进一帧
                _currentState = _advanceFrameFunc(_currentState, inputs, _fixedDt);
                _currentTick = t;

                // 保存更新后的状态
                SaveState(t);
            }

            _isRollingBack = false;
        }

        // ── 校验 ──────────────────────────────────────────────────

        /// <summary>
        /// 获取指定 tick 的状态校验和。
        /// 用于验证回滚正确性。
        /// </summary>
        public bool TryGetChecksum(int tick, out uint checksum)
        {
            return _stateChecksums.TryGet(tick, out checksum);
        }

        /// <summary>
        /// 验证两个 tick 的状态校验和是否一致。
        /// </summary>
        public bool VerifyChecksums(int tickA, int tickB)
        {
            if (!_stateChecksums.TryGet(tickA, out var checksumA))
                return false;
            if (!_stateChecksums.TryGet(tickB, out var checksumB))
                return false;
            return checksumA == checksumB;
        }

        /// <summary>计算当前状态的校验和。</summary>
        public uint ComputeCurrentChecksum()
        {
            return _hashFunc != null ? _hashFunc(_currentState) : 0;
        }

        // ── 获取状态 ──────────────────────────────────────────────

        /// <summary>获取当前游戏状态 (只读引用)。</summary>
        public TGameState CurrentState => _currentState;

        /// <summary>获取指定 tick 的状态快照。</summary>
        public bool TryGetState(int tick, out TGameState state)
        {
            return _stateHistory.TryGet(tick, out state);
        }

        /// <summary>获取指定 tick 的输入。</summary>
        public bool TryGetInputs(int tick, out TInput[] inputs)
        {
            return _inputHistory.TryGet(tick, out inputs);
        }

        /// <summary>清除所有历史 (重置)。</summary>
        public void Clear()
        {
            _stateHistory.Clear();
            _inputHistory.Clear();
            _stateChecksums.Clear();
            _currentTick = 0;
            _rollbackCount = 0;
            _framesSimulated = 0;
            _mispredictions = 0;
        }
    }
}
