using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace ShanyuEngine.GameFramework
{
    // ════════════════════════════════════════════════════════════════════════
    // 山雨引擎 - GameMode (GameFramework)
    //
    // 类似 UE GameMode / Unity GameManager:
    //   * 定义游戏规则的核心类
    //   * 默认 Pawn/PlayerController/GameState/PlayerState/HUD 类型引用
    //   * 登录流程: PreLogin -> Login -> PostLogin -> SpawnDefaultPawnFor -> RestartPlayer
    //   * 游戏流程: InitGame -> StartMatch -> EndMatch -> StartNextMatch
    //   * 波次系统: WaveManager 控制敌人波次生成
    //   * 玩家重生: RestartPlayerAtPlayerStart
    //   * 游戏规则: 胜利条件/失败条件/计时器
    //   * 静态 Instance 单例访问
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>匹配状态。</summary>
    public enum MatchState
    {
        /// <summary>未开始。</summary>
        Waiting,
        /// <summary>初始化中。</summary>
        Initializing,
        /// <summary>进行中。</summary>
        InProgress,
        /// <summary>已结束。</summary>
        Ended,
        /// <summary>等待下一轮。</summary>
        WaitingForNext
    }

    /// <summary>游戏结果。</summary>
    public enum GameResult
    {
        None,
        Victory,
        Defeat,
        Draw
    }

    /// <summary>波次配置。</summary>
    public class WaveConfig
    {
        /// <summary>波次编号 (从 1 开始)。</summary>
        public int WaveNumber { get; set; }

        /// <summary>本波次生成的敌人数量。</summary>
        public int EnemyCount { get; set; }

        /// <summary>本波次生成的敌人类型列表 (类名或预制体路径)。</summary>
        public List<string> EnemyTypes { get; set; } = new List<string>();

        /// <summary>本波次之间间隔时间 (秒)。</summary>
        public float DelayBeforeWave { get; set; } = 3f;

        /// <summary>本波次生成完成后的延迟 (秒) 才进入下一波。</summary>
        public float DelayAfterWave { get; set; } = 2f;

        /// <summary>生成位置标签 (按 PlayerStart 标签匹配)。</summary>
        public string SpawnTag { get; set; } = "Enemy";
    }

    /// <summary>波次管理器 (管理敌人波次生成与状态)。</summary>
    public class WaveManager
    {
        /// <summary>所有波次配置。</summary>
        public List<WaveConfig> Waves { get; set; } = new List<WaveConfig>();

        /// <summary>当前波次索引 (从 0 开始)。</summary>
        public int CurrentWaveIndex { get; private set; } = -1;

        /// <summary>当前波次剩余敌人数量。</summary>
        public int RemainingEnemies { get; set; }

        /// <summary>总敌人数 (已生成计数)。</summary>
        public int TotalEnemiesSpawned { get; set; }

        /// <summary>是否所有波次已完成。</summary>
        public bool AllWavesCompleted { get; private set; }

        /// <summary>是否正在生成中。</summary>
        public bool IsSpawning { get; set; }

        /// <summary>波次间计时器。</summary>
        public float WaveTimer { get; set; }

        /// <summary>波次开始事件。(波次编号, 敌人数)</summary>
        public event Action<int, int> OnWaveStarted;

        /// <summary>波次完成事件。(波次编号)</summary>
        public event Action<int> OnWaveCompleted;

        /// <summary>所有波次完成事件。</summary>
        public event Action OnAllWavesCompleted;

        /// <summary>添加波次配置。</summary>
        public void AddWave(WaveConfig config)
        {
            if (config != null) Waves.Add(config);
        }

        /// <summary>开始下一波。</summary>
        public WaveConfig StartNextWave()
        {
            if (CurrentWaveIndex + 1 >= Waves.Count)
            {
                AllWavesCompleted = true;
                OnAllWavesCompleted?.Invoke();
                return null;
            }

            CurrentWaveIndex++;
            var wave = Waves[CurrentWaveIndex];
            RemainingEnemies = wave.EnemyCount;
            TotalEnemiesSpawned += wave.EnemyCount;
            IsSpawning = true;
            WaveTimer = 0f;

            OnWaveStarted?.Invoke(wave.WaveNumber, wave.EnemyCount);
            return wave;
        }

        /// <summary>通知一个敌人被消灭。</summary>
        public void NotifyEnemyKilled()
        {
            if (RemainingEnemies > 0)
                RemainingEnemies--;

            if (RemainingEnemies <= 0 && IsSpawning)
            {
                IsSpawning = false;
                var wave = GetCurrentWave();
                if (wave != null)
                {
                    WaveTimer = wave.DelayAfterWave;
                    OnWaveCompleted?.Invoke(wave.WaveNumber);
                }
            }
        }

        /// <summary>获取当前波次配置。</summary>
        public WaveConfig GetCurrentWave()
        {
            return CurrentWaveIndex >= 0 && CurrentWaveIndex < Waves.Count
                ? Waves[CurrentWaveIndex] : null;
        }

        /// <summary>获取总波次数。</summary>
        public int TotalWaves => Waves.Count;

        /// <summary>当前波次编号 (从 1 开始, 0=未开始)。</summary>
        public int CurrentWaveNumber => CurrentWaveIndex + 1;

        /// <summary>重置波次系统。</summary>
        public void Reset()
        {
            CurrentWaveIndex = -1;
            RemainingEnemies = 0;
            TotalEnemiesSpawned = 0;
            AllWavesCompleted = false;
            IsSpawning = false;
            WaveTimer = 0f;
        }
    }

    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 游戏模式 —— 定义游戏规则的核心类。
    /// 全局单例 (Instance)，每个 GameplayWorld 持有唯一实例。
    /// </summary>
    public class GameMode : Actor
    {
        // ── 单例 ──────────────────────────────────────────────────────────

        /// <summary>全局 GameMode 实例。</summary>
        public static GameMode Instance { get; internal set; }

        // ── 类型引用 (默认类) ────────────────────────────────────────────

        /// <summary>默认 Pawn 类。</summary>
        public Type DefaultPawnType { get; set; } = typeof(Pawn);

        /// <summary>默认 PlayerController 类。</summary>
        public Type DefaultPlayerControllerType { get; set; } = typeof(PlayerController);

        /// <summary>默认 GameState 类。</summary>
        public Type DefaultGameStateType { get; set; } = typeof(GameState);

        /// <summary>默认 HUD 类。</summary>
        public Type DefaultHUDType { get; set; }

        /// <summary>默认 PlayerState 类。</summary>
        public Type DefaultPlayerStateType { get; set; }

        /// <summary>默认 AI Controller 类。</summary>
        public Type DefaultAIControllerType { get; set; } = typeof(AIController);

        // ── 匹配状态 ──────────────────────────────────────────────────────

        /// <summary>当前匹配状态。</summary>
        public MatchState CurrentMatchState { get; private set; } = MatchState.Waiting;

        /// <summary>匹配状态变化事件。</summary>
        public event Action<MatchState, MatchState> OnMatchStateChanged;

        // ── 游戏结果 ──────────────────────────────────────────────────────

        /// <summary>游戏结果。</summary>
        public GameResult Result { get; private set; } = GameResult.None;

        /// <summary>结果描述文本。</summary>
        public string ResultMessage { get; set; } = string.Empty;

        /// <summary>胜利条件委托。</summary>
        public Func<bool> WinCondition { get; set; }

        /// <summary>失败条件委托。</summary>
        public Func<bool> LoseCondition { get; set; }

        /// <summary>计时器 (秒)。</summary>
        public float GameTimer { get; set; }

        /// <summary>自定义游戏规则检查间隔 (秒)。</summary>
        public float RuleCheckInterval { get; set; } = 1f;

        private float _ruleCheckTimer;

        // ── 波次系统 ──────────────────────────────────────────────────────

        /// <summary>波次管理器。</summary>
        public WaveManager WaveManager { get; private set; } = new WaveManager();

        /// <summary>是否启用了波次系统。</summary>
        public bool WaveSystemEnabled { get; set; }

        // ── 引用 ──────────────────────────────────────────────────────────

        /// <summary>当前 GameState (由 GameMode 创建)。</summary>
        public GameState GameState { get; private set; }

        /// <summary>所有 PlayerStart 出生点。</summary>
        public readonly List<PlayerStart> PlayerStarts = new List<PlayerStart>();

        /// <summary>所有已生成的玩家 Pawn。</summary>
        public readonly Dictionary<string, Pawn> PlayerPawns = new Dictionary<string, Pawn>();

        /// <summary>HUD 引用 (如实现)。</summary>
        public Actor HUD { get; set; }

        // ── 事件 ──────────────────────────────────────────────────────────

        /// <summary>玩家登录事件。(PlayerController)</summary>
        public event Action<PlayerController> OnPlayerLoggedIn;

        /// <summary>玩家登出事件。(PlayerController)</summary>
        public event Action<PlayerController> OnPlayerLoggedOut;

        /// <summary>游戏胜利事件。</summary>
        public event Action OnGameVictory;

        /// <summary>游戏失败事件。</summary>
        public event Action OnGameDefeat;

        // ════════════════════════════════════════════════════════════════════
        // 生命周期
        // ════════════════════════════════════════════════════════════════════

        protected override void OnInitialize()
        {
            base.OnInitialize();
            Instance = this;
            Name = "GameMode";
        }

        protected override void OnBeginPlay()
        {
            base.OnBeginPlay();
            InitGame();
        }

        protected override void OnTick(float deltaTime)
        {
            base.OnTick(deltaTime);

            if (CurrentMatchState != MatchState.InProgress) return;

            // 计时器
            if (GameTimer > 0f)
            {
                GameTimer -= deltaTime;
                if (GameTimer <= 0f)
                {
                    GameTimer = 0f;
                    OnTimerExpired();
                }
            }

            // 波次系统
            if (WaveSystemEnabled)
                UpdateWaves(deltaTime);

            // 规则检查
            _ruleCheckTimer += deltaTime;
            if (_ruleCheckTimer >= RuleCheckInterval)
            {
                _ruleCheckTimer = 0f;
                CheckGameRules();
            }
        }

        protected override void OnEndPlay()
        {
            base.OnEndPlay();
            EndMatch("World shutting down");
            Instance = Instance == this ? null : Instance;
        }

        // ════════════════════════════════════════════════════════════════════
        // 游戏流程: InitGame -> StartMatch -> EndMatch -> StartNextMatch
        // ════════════════════════════════════════════════════════════════════

        /// <summary>初始化游戏 (设置 GameState, 查找 PlayerStart 等)。</summary>
        public virtual void InitGame()
        {
            SetMatchState(MatchState.Initializing);

            // 创建 GameState
            if (DefaultGameStateType != null && GameState == null)
            {
                GameState = Activator.CreateInstance(DefaultGameStateType) as GameState;
                if (GameState != null)
                {
                    GameState.Name = "GameState";
                    World?.RegisterActor(GameState);
                }
            }

            // 收集 PlayerStart 列表 (从 World 查找)
            if (World != null)
            {
                var starts = World.FindActors<PlayerStart>();
                PlayerStarts.Clear();
                PlayerStarts.AddRange(starts);
            }

            OnInitGame();
            SetMatchState(MatchState.Waiting);
        }

        /// <summary>开始游戏 (进入 Playing 阶段)。</summary>
        public virtual void StartMatch()
        {
            if (CurrentMatchState == MatchState.InProgress) return;

            SetMatchState(MatchState.InProgress);

            // 通知 GameState
            GameState?.SetGameStage(GameStage.Playing);

            // 启动波次系统
            if (WaveSystemEnabled)
                WaveManager.StartNextWave();

            OnMatchStarted();
        }

        /// <summary>结束游戏。</summary>
        public virtual void EndMatch(string reason = "")
        {
            if (CurrentMatchState == MatchState.Ended) return;

            ResultMessage = reason;
            SetMatchState(MatchState.Ended);

            GameState?.SetGameStage(GameStage.Ended);

            OnMatchEnded();
        }

        /// <summary>开始下一轮 (重置后重新开始)。</summary>
        public virtual void StartNextMatch()
        {
            if (CurrentMatchState != MatchState.Ended &&
                CurrentMatchState != MatchState.WaitingForNext) return;

            // 清理旧的 Pawn
            foreach (var kvp in PlayerPawns)
            {
                if (kvp.Value != null && !kvp.Value.IsDead)
                    World?.DestroyActor(kvp.Value);
            }
            PlayerPawns.Clear();

            // 重置波次
            WaveManager.Reset();

            // 重置 GameState
            GameState?.SetGameStage(GameStage.Waiting);
            GameTimer = 0f;
            Result = GameResult.None;
            ResultMessage = string.Empty;

            // 重新开始
            InitGame();
            StartMatch();
        }

        // ════════════════════════════════════════════════════════════════════
        // 登录流程: PreLogin -> Login -> PostLogin
        // ════════════════════════════════════════════════════════════════════

        /// <summary>预登录检查 (可用于踢人/IP 限制等)。返回 null 表示允许, 字符串为拒绝原因。</summary>
        public virtual string PreLogin(string connection)
        {
            // 可在这里做 IP 过滤、人数限制等
            if (GameState != null && GameState.PlayerCount >= 4)
                return "Server full (max 4 players)";
            return null; // 允许登录
        }

        /// <summary>玩家登录。创建并初始化 PlayerController。</summary>
        public virtual PlayerController Login(string playerId, string playerName = null)
        {
            string rejectReason = PreLogin(playerId);
            if (rejectReason != null)
            {
                System.Diagnostics.Debug.WriteLine($"Login rejected for {playerId}: {rejectReason}");
                return null;
            }

            // 创建 PlayerController
            PlayerController controller = Activator.CreateInstance(DefaultPlayerControllerType) as PlayerController;
            if (controller == null)
            {
                controller = new PlayerController();
            }

            controller.PlayerName = playerName ?? $"Player_{playerId}";
            controller.Name = controller.PlayerName;
            controller.IsLocalPlayer = true;

            // 注册到世界并初始化
            World?.RegisterActor(controller);

            // 添加到 GameState
            GameState?.AddPlayer(controller);

            // 后处理
            PostLogin(controller);

            OnPlayerLoggedIn?.Invoke(controller);
            return controller;
        }

        /// <summary>登录后处理。</summary>
        public virtual void PostLogin(PlayerController controller)
        {
            if (controller == null) return;

            // 自动生成 Pawn
            SpawnDefaultPawnFor(controller);

            // 如果匹配已开始, 重置玩家
            if (CurrentMatchState == MatchState.InProgress)
                RestartPlayer(controller);
        }

        /// <summary>玩家登出。</summary>
        public virtual void Logout(PlayerController controller)
        {
            if (controller == null) return;

            // 释放 Pawn
            if (controller.PossessedPawn != null)
            {
                string key = controller.PlayerName;
                if (PlayerPawns.ContainsKey(key))
                    PlayerPawns.Remove(key);
                World?.DestroyActor(controller.PossessedPawn);
                controller.UnPossess();
            }

            GameState?.RemovePlayer(controller);
            World?.DestroyActor(controller);

            OnPlayerLoggedOut?.Invoke(controller);
        }

        // ════════════════════════════════════════════════════════════════════
        // Pawn 生成与重生
        // ════════════════════════════════════════════════════════════════════

        /// <summary>为 Controller 生成默认 Pawn。</summary>
        public virtual Pawn SpawnDefaultPawnFor(PlayerController controller)
        {
            if (controller == null) return null;

            // 选择出生点
            PlayerStart start = ChoosePlayerStart(controller);
            Vector3 spawnLocation = start?.GetSpawnLocation() ?? Vector3.Zero;
            Quaternion spawnRotation = start?.GetSpawnRotation() ?? Quaternion.Identity;

            // 创建 Pawn
            Pawn pawn = Activator.CreateInstance(DefaultPawnType) as Pawn;
            if (pawn == null) return null;

            pawn.Name = $"{controller.PlayerName}_Pawn";
            pawn.Position = spawnLocation;
            pawn.Rotation = spawnRotation;

            World?.RegisterActor(pawn);

            // 占据
            controller.Possess(pawn);

            // 记录
            PlayerPawns[controller.PlayerName] = pawn;

            return pawn;
        }

        /// <summary>重新开始玩家 (重生)。</summary>
        public virtual void RestartPlayer(PlayerController controller)
        {
            if (controller == null) return;

            // 如果已有 Pawn 且没死, 先处理
            if (controller.PossessedPawn != null)
            {
                if (!controller.PossessedPawn.IsDead)
                    return; // 还活着, 不需要重生

                // 清除死亡 Pawn
                string key = controller.PlayerName;
                if (PlayerPawns.ContainsKey(key))
                    PlayerPawns.Remove(key);
                World?.DestroyActor(controller.PossessedPawn);
                controller.UnPossess();
            }

            // 选择出生点
            PlayerStart start = ChoosePlayerStart(controller);

            // 创建新 Pawn
            SpawnDefaultPawnFor(controller);
            if (start != null)
                RestartPlayerAtPlayerStart(controller, start);
        }

        /// <summary>在指定出生点重生玩家。</summary>
        public virtual void RestartPlayerAtPlayerStart(PlayerController controller, PlayerStart start)
        {
            if (controller == null || start == null) return;

            if (controller.PossessedPawn != null)
            {
                controller.PossessedPawn.Position = start.GetSpawnLocation();
                controller.PossessedPawn.Rotation = start.GetSpawnRotation();
            }

            start.Claim(controller.PlayerName, controller.PossessedPawn);
        }

        /// <summary>选择最佳 PlayerStart (基于优先级/未占用/标签)。</summary>
        public virtual PlayerStart ChoosePlayerStart(PlayerController controller)
        {
            if (PlayerStarts.Count == 0) return null;

            // 优先选未占用的、高优先级的
            var candidates = PlayerStarts
                .Where(s => s.IsEnabled)
                .OrderByDescending(s => s.Priority)
                .ThenBy(s => s.IsOccupied ? 1 : 0)
                .ToList();

            return candidates.FirstOrDefault();
        }

        // ════════════════════════════════════════════════════════════════════
        // 波次系统更新
        // ════════════════════════════════════════════════════════════════════

        /// <summary>每帧更新波次逻辑。</summary>
        protected virtual void UpdateWaves(float deltaTime)
        {
            if (WaveManager.AllWavesCompleted) return;

            // 如果当前波次已无敌人且不在生成中, 等待延迟后进入下一波
            if (!WaveManager.IsSpawning && WaveManager.RemainingEnemies <= 0)
            {
                WaveManager.WaveTimer -= deltaTime;
                if (WaveManager.WaveTimer <= 0f)
                {
                    WaveManager.StartNextWave();
                    if (WaveManager.GetCurrentWave() != null)
                        OnWaveStarted(WaveManager.GetCurrentWave());
                }
            }
        }

        /// <summary>波次开始回调 (开发者可重写以自定义生成逻辑)。</summary>
        protected virtual void OnWaveStarted(WaveConfig wave)
        {
            // 子类重写以实现具体敌人的生成
            System.Diagnostics.Debug.WriteLine(
                $"Wave {wave.WaveNumber} started: {wave.EnemyCount} enemies");
        }

        // ════════════════════════════════════════════════════════════════════
        // 游戏规则检查
        // ════════════════════════════════════════════════════════════════════

        /// <summary>检查胜利/失败条件。</summary>
        protected virtual void CheckGameRules()
        {
            if (CurrentMatchState != MatchState.InProgress) return;

            // 胜利条件检查
            if (WinCondition != null && WinCondition())
            {
                DeclareVictory();
                return;
            }

            // 失败条件检查
            if (LoseCondition != null && LoseCondition())
            {
                DeclareDefeat();
                return;
            }

            // 波次通关胜利 (所有波次完成且无敌人)
            if (WaveSystemEnabled && WaveManager.AllWavesCompleted && WaveManager.RemainingEnemies <= 0)
            {
                DeclareVictory("All waves completed!");
            }
        }

        /// <summary>声明胜利。</summary>
        public virtual void DeclareVictory(string message = "Victory!")
        {
            if (CurrentMatchState == MatchState.Ended) return;
            Result = GameResult.Victory;
            ResultMessage = message;
            EndMatch(message);
            OnGameVictory?.Invoke();
        }

        /// <summary>声明失败。</summary>
        public virtual void DeclareDefeat(string message = "Defeat!")
        {
            if (CurrentMatchState == MatchState.Ended) return;
            Result = GameResult.Defeat;
            ResultMessage = message;
            EndMatch(message);
            OnGameDefeat?.Invoke();
        }

        /// <summary>计时器到期时调用。</summary>
        protected virtual void OnTimerExpired()
        {
            // 默认: 时间到判定为平局或无操作
            if (Result == GameResult.None)
                EndMatch("Time's up!");
        }

        // ════════════════════════════════════════════════════════════════════
        // 辅助
        // ════════════════════════════════════════════════════════════════════

        private void SetMatchState(MatchState newState)
        {
            if (CurrentMatchState == newState) return;
            var prev = CurrentMatchState;
            CurrentMatchState = newState;
            OnMatchStateChanged?.Invoke(prev, newState);
        }

        /// <summary>初始化游戏时回调 (InitGame 内部调用)。</summary>
        protected virtual void OnInitGame() { }

        /// <summary>匹配开始时回调。</summary>
        protected virtual void OnMatchStarted() { }

        /// <summary>匹配结束时回调。</summary>
        protected virtual void OnMatchEnded() { }

        /// <summary>通知一个敌人被击杀 (波次系统使用)。</summary>
        public void NotifyEnemyKilled()
        {
            if (WaveSystemEnabled)
                WaveManager.NotifyEnemyKilled();
        }
    }
}
