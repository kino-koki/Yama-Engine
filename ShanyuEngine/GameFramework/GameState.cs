using System;
using System.Collections.Generic;

namespace ShanyuEngine.GameFramework
{
    // ════════════════════════════════════════════════════════════════════════
    // 山雨引擎 - GameState (GameFramework)
    //
    // 类似 UE GameState / Unity NetworkBehaviour:
    //   * 游戏运行时全局状态
    //   * 当前游戏阶段 (Waiting/Starting/Playing/Paused/Ended)
    //   * 已连接玩家列表
    //   * 游戏时间 (经过/剩余)
    //   * 分数/统计数据
    //   * 网络同步标记 (IsServer/IsClient)
    //   * OnGameStageChanged / OnPlayerAdded / OnPlayerRemoved 事件
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>游戏阶段枚举。</summary>
    public enum GameStage
    {
        /// <summary>等待玩家加入。</summary>
        Waiting,
        /// <summary>游戏开始前准备。</summary>
        Starting,
        /// <summary>游戏进行中。</summary>
        Playing,
        /// <summary>游戏暂停。</summary>
        Paused,
        /// <summary>游戏已结束。</summary>
        Ended
    }

    /// <summary>
    /// 游戏运行时全局状态。每个 GameplayWorld 持有唯一实例，
    /// 向所有 Actor/PlayerController 广播游戏状态变化。
    /// </summary>
    public class GameState : Actor
    {
        // ── 游戏阶段 ──────────────────────────────────────────────────────

        /// <summary>当前游戏阶段。</summary>
        public GameStage CurrentStage { get; private set; } = GameStage.Waiting;

        /// <summary>上一游戏阶段 (用于过渡检测)。</summary>
        public GameStage PreviousStage { get; private set; } = GameStage.Waiting;

        /// <summary>游戏阶段变化事件 (参数: 上一阶段, 当前阶段)。</summary>
        public event Action<GameStage, GameStage> OnGameStageChanged;

        // ── 玩家管理 ──────────────────────────────────────────────────────

        /// <summary>已连接玩家控制器列表。</summary>
        public readonly List<PlayerController> PlayerControllers = new List<PlayerController>();

        /// <summary>玩家数量。</summary>
        public int PlayerCount => PlayerControllers.Count;

        /// <summary>玩家加入事件。</summary>
        public event Action<PlayerController> OnPlayerAdded;

        /// <summary>玩家离开事件。</summary>
        public event Action<PlayerController> OnPlayerRemoved;

        // ── 游戏时间 ──────────────────────────────────────────────────────

        /// <summary>游戏已耗时 (秒)。</summary>
        public float ElapsedTime { get; private set; }

        /// <summary>游戏最大时长 (秒, 0=无限制)。</summary>
        public float TimeLimit { get; set; }

        /// <summary>剩余时间 (秒, TimeLimit > 0 时有效)。</summary>
        public float RemainingTime => TimeLimit > 0f ? MathF.Max(0f, TimeLimit - ElapsedTime) : 0f;

        /// <summary>是否超时。</summary>
        public bool IsTimeUp => TimeLimit > 0f && ElapsedTime >= TimeLimit;

        // ── 分数/统计数据 ──────────────────────────────────────────────────

        /// <summary>总分数 (各队总和或全局分数)。</summary>
        public int TotalScore { get; set; }

        /// <summary>玩家得分字典 (PlayerId -> 分数)。</summary>
        public readonly Dictionary<string, int> PlayerScores = new Dictionary<string, int>();

        /// <summary>自定义统计数据 (键值对)。</summary>
        public readonly Dictionary<string, string> CustomStats = new Dictionary<string, string>();

        // ── 网络 ──────────────────────────────────────────────────────────

        /// <summary>是否为服务器端。</summary>
        public bool IsServer { get; set; } = true;

        /// <summary>是否为客户端。</summary>
        public bool IsClient => !IsServer;

        /// <summary>是否已网络同步。</summary>
        public bool IsReplicated { get; set; }

        // ── 游戏阶段切换 ──────────────────────────────────────────────────

        /// <summary>切换到目标游戏阶段。</summary>
        public void SetGameStage(GameStage newStage)
        {
            if (CurrentStage == newStage) return;
            PreviousStage = CurrentStage;
            CurrentStage = newStage;
            OnGameStageChanged?.Invoke(PreviousStage, CurrentStage);

            switch (newStage)
            {
                case GameStage.Playing:
                    OnGameStarted();
                    break;
                case GameStage.Paused:
                    OnGamePaused();
                    break;
                case GameStage.Ended:
                    OnGameEnded();
                    break;
            }
        }

        /// <summary>判断当前是否在指定阶段。</summary>
        public bool IsInStage(GameStage stage) => CurrentStage == stage;

        /// <summary>判断游戏是否活跃 (Playing 且未超时)。</summary>
        public bool IsGameActive => CurrentStage == GameStage.Playing && !IsTimeUp;

        // ── 玩家管理 ──────────────────────────────────────────────────────

        /// <summary>添加玩家 (由 GameMode 调用)。</summary>
        public void AddPlayer(PlayerController player)
        {
            if (player == null || PlayerControllers.Contains(player)) return;
            PlayerControllers.Add(player);
            PlayerScores[player.PlayerName] = 0;
            OnPlayerAdded?.Invoke(player);
        }

        /// <summary>移除玩家 (由 GameMode 调用)。</summary>
        public void RemovePlayer(PlayerController player)
        {
            if (player == null || !PlayerControllers.Remove(player)) return;
            PlayerScores.Remove(player.PlayerName);
            OnPlayerRemoved?.Invoke(player);
        }

        /// <summary>获取指定索引的玩家。</summary>
        public PlayerController GetPlayer(int index)
        {
            return index >= 0 && index < PlayerControllers.Count
                ? PlayerControllers[index] : null;
        }

        /// <summary>按名称查找玩家。</summary>
        public PlayerController FindPlayer(string playerName)
        {
            return PlayerControllers.Find(p => p.PlayerName == playerName);
        }

        // ── 分数 ──────────────────────────────────────────────────────────

        /// <summary>为指定玩家加分。</summary>
        public void AddScore(string playerName, int points)
        {
            if (PlayerScores.ContainsKey(playerName))
                PlayerScores[playerName] += points;
            else
                PlayerScores[playerName] = points;
            TotalScore += points;
        }

        /// <summary>设置玩家分数。</summary>
        public void SetScore(string playerName, int score)
        {
            int oldScore = PlayerScores.TryGetValue(playerName, out var s) ? s : 0;
            PlayerScores[playerName] = score;
            TotalScore += (score - oldScore);
        }

        /// <summary>获取玩家分数。</summary>
        public int GetPlayerScore(string playerName)
        {
            return PlayerScores.TryGetValue(playerName, out var score) ? score : 0;
        }

        /// <summary>获取最高分玩家。</summary>
        public KeyValuePair<string, int> GetTopScorer()
        {
            string topPlayer = null;
            int topScore = int.MinValue;
            foreach (var kvp in PlayerScores)
            {
                if (kvp.Value > topScore) { topScore = kvp.Value; topPlayer = kvp.Key; }
            }
            return new KeyValuePair<string, int>(topPlayer, topScore);
        }

        // ── 映射 统计 ──────────────────────────────────────────────────────

        /// <summary>设置自定义统计。</summary>
        public void SetStat(string key, string value) => CustomStats[key] = value;

        /// <summary>获取自定义统计。</summary>
        public string GetStat(string key) =>
            CustomStats.TryGetValue(key, out var v) ? v : string.Empty;

        // ── 生命周期回调 ──────────────────────────────────────────────────

        protected override void OnBeginPlay()
        {
            base.OnBeginPlay();
            Name = "GameState";
            SetGameStage(GameStage.Waiting);
        }

        protected override void OnTick(float deltaTime)
        {
            base.OnTick(deltaTime);

            if (CurrentStage == GameStage.Playing)
            {
                ElapsedTime += deltaTime;

                if (IsTimeUp)
                    SetGameStage(GameStage.Ended);
            }
        }

        /// <summary>游戏开始时调用。</summary>
        protected virtual void OnGameStarted() { }

        /// <summary>游戏暂停时调用。</summary>
        protected virtual void OnGamePaused() { }

        /// <summary>游戏结束时调用。</summary>
        protected virtual void OnGameEnded() { }
    }
}
