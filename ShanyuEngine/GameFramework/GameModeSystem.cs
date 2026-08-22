// ============================================================================================
//  GameModeSystem.cs — 游戏模式框架（深化版）
//
//  命名空间: ShanyuEngine.GameFramework
//
//  本系统提供完整的游戏模式框架，覆盖常见的多人/单人游戏模式：
//    - GameMode             ：游戏模式基类（规则/玩家/分数/胜负条件）
//    - GameModeDeathmatch   ：死亡竞赛（FFA / TeamDM）
//    - GameModeCoop         ：合作模式（波次生成/敌人定义）
//    - GameState            ：游戏状态机（Waiting/Starting/InProgress/Ending）
//    - PlayerController     ：玩家控制器（Pawn/PlayerState/Input/Spectator）
//    - GameStateManager     ：游戏状态管理（Authority/Replicated/RPC）
//
//  与引擎其他系统的对接：
//    - 与 ECS ActorSystem 集成：PlayerController/Pawn 作为 Actor
//    - 与 NetworkSystem 对接：Server RPC / Client RPC
//    - 与 InputSystem 对接：输入绑定
//    - 与 UISystem 对接：计分板/击杀信息/游戏状态HUD
// ============================================================================================

using System;
using System.Collections.Generic;
using System.Numerics;

namespace ShanyuEngine.GameFramework
{
    // ──────────────────────────────────────────────────────────────
    //  GameMode —— 游戏模式基类
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 游戏模式基类。定义游戏的核心规则和流程。
    /// </summary>

    // ──────────────────────────────────────────────────────────────
    //  GameModeDeathmatch —— 死亡竞赛
    // ──────────────────────────────────────────────────────────────

    /// <summary>死亡竞赛类型。</summary>
    public enum DeathmatchType : byte
    {
        /// <summary>自由混战（Free For All）。</summary>
        FreeForAll = 0,
        /// <summary>团队死亡竞赛（Team DM）。</summary>
        TeamDM = 1,
    }

    /// <summary>
    /// 死亡竞赛游戏模式。支持 FFA（自由混战）和 Team DM（团队死亡竞赛）。
    /// </summary>
    public class GameModeDeathmatch : GameMode
    {
        /// <summary>死亡竞赛类型。</summary>
        public DeathmatchType DMType { get; set; } = DeathmatchType.FreeForAll;

        /// <summary>重生时间（秒）。</summary>
        public float RespawnTime { get; set; } = 3f;

        /// <summary>分数上限。</summary>
        public new int ScoreLimit { get; set; } = 30;

        /// <summary>击杀信息队列。</summary>
        public Queue<KillFeedEntry> KillFeed { get; } = new Queue<KillFeedEntry>();

        /// <summary>击杀信息最大队列长度。</summary>
        public int MaxKillFeedEntries { get; set; } = 8;

        /// <summary>重生计时器（每个玩家）。</summary>
        private Dictionary<int, float> _respawnTimers = new Dictionary<int, float>();

        /// <summary>击杀信息事件。</summary>
        public Action<KillFeedEntry> OnKill;

        public GameModeDeathmatch(DeathmatchType type)
        {
            ModeName = type == DeathmatchType.FreeForAll ? "FreeForAll" : "TeamDeathmatch";
            Description = type == DeathmatchType.FreeForAll
                ? "Free-for-all deathmatch"
                : "Team-based deathmatch";
            DMType = type;
            TeamCount = type == DeathmatchType.TeamDM ? 2 : 1;
            MaxPlayers = type == DeathmatchType.TeamDM ? 16 : 12;
        }

        /// <summary>处理击杀。</summary>
        public void OnPlayerKill(PlayerController killer, PlayerController victim, string weapon)
        {
            if (!IsMatchInProgress) return;

            // 更新分数
            if (DMType == DeathmatchType.FreeForAll)
            {
                killer.PlayerStateRef.Score += 100;
                killer.PlayerStateRef.Kills++;
            }
            else
            {
                // Team DM：友军击杀不加分
                if (killer.Team != victim.Team)
                {
                    killer.PlayerStateRef.Score += 100;
                    killer.PlayerStateRef.Kills++;
                }
                else
                {
                    killer.PlayerStateRef.Score -= 50; // 友军击杀扣分
                }
            }
            victim.PlayerStateRef.Deaths++;

            // 添加击杀信息
            var entry = new KillFeedEntry
            {
                KillerName = killer.PlayerStateRef.PlayerName,
                KillerTeam = killer.Team,
                VictimName = victim.PlayerStateRef.PlayerName,
                VictimTeam = victim.Team,
                WeaponName = weapon,
                Timestamp = ElapsedTime,
            };
            KillFeed.Enqueue(entry);
            if (KillFeed.Count > MaxKillFeedEntries)
                KillFeed.Dequeue();

            OnKill?.Invoke(entry);

            // 启动重生计时器
            _respawnTimers[victim.PlayerStateRef.PlayerId] = RespawnTime;
        }

        public override void Update(float deltaTime)
        {
            base.Update(deltaTime);

            // 更新重生计时器
            var toRespawn = new List<int>();
            foreach (var kvp in _respawnTimers)
            {
                _respawnTimers[kvp.Key] = kvp.Value - deltaTime;
                if (_respawnTimers[kvp.Key] <= 0f)
                    toRespawn.Add(kvp.Key);
            }

            foreach (var playerId in toRespawn)
            {
                _respawnTimers.Remove(playerId);
                var player = ConnectedPlayers.Find(p => p.PlayerStateRef.PlayerId == playerId);
                if (player != null) RespawnPlayer(player);
            }
        }

        public override int CheckWinCondition()
        {
            if (DMType == DeathmatchType.FreeForAll)
            {
                // 个人达到分数上限
                foreach (var p in ConnectedPlayers)
                {
                    if (p.PlayerStateRef.Kills >= ScoreLimit)
                        return p.Team; // FFA 用 Team 存个人索引
                }
            }
            else
            {
                // Team DM：某队总分达到上限
                int[] teamScores = new int[TeamCount];
                foreach (var p in ConnectedPlayers)
                {
                    if (p.Team >= 0 && p.Team < TeamCount)
                        teamScores[p.Team] += p.PlayerStateRef.Kills;
                }

                for (int i = 0; i < TeamCount; i++)
                {
                    if (teamScores[i] >= ScoreLimit) return i;
                }
            }
            return -2;
        }
    }

    /// <summary>击杀信息条目。</summary>
    public struct KillFeedEntry
    {
        public string KillerName;
        public int KillerTeam;
        public string VictimName;
        public int VictimTeam;
        public string WeaponName;
        public float Timestamp;
    }

    // ──────────────────────────────────────────────────────────────
    //  GameModeCoop —— 合作模式
    // ──────────────────────────────────────────────────────────────

    /// <summary>波次定义。</summary>
    public class WaveDefinition
    {
        /// <summary>波次编号。</summary>
        public int WaveIndex { get; set; }

        /// <summary>敌人类型列表。</summary>
        public List<EnemySpawnEntry> Enemies { get; set; } = new List<EnemySpawnEntry>();

        /// <summary>波次开始延迟（秒）。</summary>
        public float StartDelay { get; set; } = 3f;

        /// <summary>波次描述。</summary>
        public string Description { get; set; }

        /// <summary>波次完成奖励分数。</summary>
        public int CompletionBonus { get; set; } = 500;
    }

    /// <summary>敌人生成条目。</summary>
    public class EnemySpawnEntry
    {
        /// <summary>敌人类型名称。</summary>
        public string EnemyType { get; set; }

        /// <summary>生成数量。</summary>
        public int Count { get; set; } = 1;

        /// <summary>生成点位置。</summary>
        public Vector3 SpawnPosition { get; set; }

        /// <summary>生成间隔（秒）。</summary>
        public float SpawnInterval { get; set; } = 1f;

        /// <summary>敌人难度等级。</summary>
        public int DifficultyLevel { get; set; } = 1;
    }

    /// <summary>
    /// 合作模式游戏模式。支持波次生成敌人。
    /// </summary>
    public class GameModeCoop : GameMode
    {
        /// <summary>波次定义列表。</summary>
        public List<WaveDefinition> Waves { get; set; } = new List<WaveDefinition>();

        /// <summary>当前波次索引（从0开始）。</summary>
        public int CurrentWave { get; set; } = -1;

        /// <summary>总波次数。</summary>
        public int TotalWaves => Waves.Count;

        /// <summary>波次间延迟。</summary>
        public float WaveTransitionDelay { get; set; } = 5f;

        /// <summary>波次过渡计时器。</summary>
        private float _waveTransitionTimer;

        /// <summary>是否所有波次已完成。</summary>
        public bool AllWavesCompleted => CurrentWave >= TotalWaves - 1 && IsCurrentWaveCleared();

        /// <summary>当前波次剩余敌人。</summary>
        public int RemainingEnemies { get; private set; }

        /// <summary>波次开始事件。</summary>
        public Action<int> OnWaveStarted;

        /// <summary>波次完成事件。</summary>
        public Action<int> OnWaveCompleted;

        /// <summary>所有波次完成事件。</summary>
        public Action OnAllWavesCompleted;

        public GameModeCoop()
        {
            ModeName = "Coop";
            Description = "Cooperative wave-based survival mode";
            TeamCount = 1; // 合作模式所有玩家同队
            MaxPlayers = 4;
        }

        public override void OnMatchStartHandler()
        {
            base.OnMatchStartHandler();
            CurrentWave = -1;
            _waveTransitionTimer = 3f; // 初始延迟
        }

        public override void Update(float deltaTime)
        {
            base.Update(deltaTime);

            if (!IsMatchInProgress) return;

            // 所有玩家死亡 → 游戏结束
            bool allDead = true;
            foreach (var p in ConnectedPlayers)
            {
                if (p.IsAlive) { allDead = false; break; }
            }
            if (allDead && ConnectedPlayers.Count > 0)
            {
                TeamWinnerIndex = -1; // 失败
                OnMatchEndHandler();
                return;
            }

            // 波次逻辑
            if (CurrentWave < 0 || IsCurrentWaveCleared())
            {
                if (CurrentWave >= 0 && CurrentWave < Waves.Count)
                {
                    // 当前波次完成
                    OnWaveCompleted?.Invoke(CurrentWave);

                    // 完成奖励
                    foreach (var p in ConnectedPlayers)
                    {
                        p.PlayerStateRef.Score += Waves[CurrentWave].CompletionBonus;
                    }
                }

                if (AllWavesCompleted)
                {
                    // 所有波次完成 → 胜利
                    TeamWinnerIndex = 0;
                    OnAllWavesCompleted?.Invoke();
                    OnMatchEndHandler();
                    return;
                }

                // 波次过渡
                _waveTransitionTimer -= deltaTime;
                if (_waveTransitionTimer <= 0f)
                {
                    StartNextWave();
                }
            }
            else
            {
                // 当前波次进行中
                UpdateCurrentWave(deltaTime);
            }
        }

        /// <summary>开始下一波。</summary>
        private void StartNextWave()
        {
            CurrentWave++;
            if (CurrentWave >= Waves.Count) return;

            var wave = Waves[CurrentWave];
            RemainingEnemies = 0;
            foreach (var entry in wave.Enemies)
                RemainingEnemies += entry.Count;

            OnWaveStarted?.Invoke(CurrentWave);
            OnRoundStartHandler(CurrentWave);
        }

        /// <summary>更新当前波次（生成敌人）。</summary>
        private void UpdateCurrentWave(float deltaTime)
        {
            // 简化：波次开始时一次性生成所有敌人
            // 实际实现应按 SpawnInterval 逐个生成
        }

        /// <summary>当前波次是否已清除。</summary>
        private bool IsCurrentWaveCleared()
        {
            return RemainingEnemies <= 0 && CurrentWave >= 0;
        }

        /// <summary>敌人被击杀时调用。</summary>
        public void OnEnemyKilled()
        {
            RemainingEnemies = Math.Max(0, RemainingEnemies - 1);
            foreach (var p in ConnectedPlayers)
            {
                if (p.IsAlive) p.PlayerStateRef.Score += 10;
            }
        }
    }

    // ──────────────────────────────────────────────────────────────
    //  GameState —— 游戏状态机
    // ──────────────────────────────────────────────────────────────

    /// <summary>游戏状态枚举。</summary>
    public enum GamePlayState : byte
    {
        /// <summary>等待玩家。</summary>
        Waiting = 0,
        /// <summary>正在初始化/倒计时。</summary>
        Starting = 1,
        /// <summary>比赛进行中。</summary>
        InProgress = 2,
        /// <summary>比赛结束。</summary>
        Ending = 3,
        /// <summary>场景过渡中（加载下一关等）。</summary>
        Transitioning = 4,
    }

    /// <summary>
    /// 游戏状态机。管理游戏的状态转换。
    /// </summary>

    // ──────────────────────────────────────────────────────────────
    //  PlayerController —— 玩家控制器
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 玩家控制器。管理单个玩家的输入、Pawn 和状态。
    /// </summary>

    /// <summary>
    /// 玩家状态数据。
    /// </summary>
    public class PlayerStateData
    {
        /// <summary>玩家ID。</summary>
        public int PlayerId { get; set; }

        /// <summary>玩家名称。</summary>
        public string PlayerName { get; set; } = "Player";

        /// <summary>队伍。</summary>
        public int Team { get; set; }

        /// <summary>总分。</summary>
        public int Score { get; set; }

        /// <summary>击杀数。</summary>
        public int Kills { get; set; }

        /// <summary>死亡数。</summary>
        public int Deaths { get; set; }

        /// <summary>助攻数。</summary>
        public int Assists { get; set; }

        /// <summary>生命值。</summary>
        public int Health { get; set; } = 100;

        /// <summary>最大生命值。</summary>
        public int MaxHealth { get; set; } = 100;

        /// <summary>K/D比。</summary>
        public float KDRatio => Deaths > 0 ? (float)Kills / Deaths : Kills;
    }

    /// <summary>
    /// 玩家输入组件。管理输入绑定。
    /// </summary>
    public class PlayerInputComponent
    {
        /// <summary>输入绑定映射。</summary>
        public Dictionary<string, Action<float>> ActionBindings { get; } = new Dictionary<string, Action<float>>();

        /// <summary>轴向输入绑定映射。</summary>
        public Dictionary<string, Action<Vector2>> AxisBindings { get; } = new Dictionary<string, Action<Vector2>>();

        /// <summary>绑定动作输入。</summary>
        public void BindAction(string actionName, Action<float> callback)
        {
            ActionBindings[actionName] = callback;
        }

        /// <summary>绑定轴向输入。</summary>
        public void BindAxis(string axisName, Action<Vector2> callback)
        {
            AxisBindings[axisName] = callback;
        }

        /// <summary>触发动作输入。</summary>
        public void TriggerAction(string actionName, float value)
        {
            if (ActionBindings.TryGetValue(actionName, out var callback))
                callback(value);
        }

        /// <summary>触发轴向输入。</summary>
        public void TriggerAxis(string axisName, Vector2 value)
        {
            if (AxisBindings.TryGetValue(axisName, out var callback))
                callback(value);
        }
    }

    /// <summary>
    /// 观察者模式配置。
    /// </summary>
    public class SpectatorMode
    {
        /// <summary>观察者类型。</summary>
        public SpectatorType Type { get; set; } = SpectatorType.FreeCamera;

        /// <summary>观察目标玩家ID。</summary>
        public int SpectateTargetId { get; set; } = -1;

        /// <summary>自由相机移动速度。</summary>
        public float FreeCameraSpeed { get; set; } = 20f;

        /// <summary>是否允许切换观察目标。</summary>
        public bool AllowTargetSwitch { get; set; } = true;
    }

    /// <summary>观察者类型枚举。</summary>
    public enum SpectatorType : byte
    {
        /// <summary>自由相机。</summary>
        FreeCamera = 0,
        /// <summary>锁定到玩家视角。</summary>
        PlayerFollow = 1,
        /// <summary>死亡回放。</summary>
        DeathReplay = 2,
        /// <summary>固定相机位置。</summary>
        FixedCamera = 3,
    }

    // ──────────────────────────────────────────────────────────────
    //  GameStateManager —— 游戏状态管理器
    // ──────────────────────────────────────────────────────────────

    /// <summary>网络角色（Authority/Replicated）。</summary>
    public enum NetRole : byte
    {
        /// <summary>无角色。</summary>
        None = 0,
        /// <summary>权威（服务器）。</summary>
        Authority = 1,
        /// <summary>自动代理。</summary>
        AutonomousProxy = 2,
        /// <summary>模拟代理。</summary>
        SimulatedProxy = 3,
    }

    /// <summary>
    /// 游戏状态管理器。处理服务器权威 vs 客户端复制。
    /// </summary>
    public class GameStateManager
    {
        /// <summary>是否为服务器权威。</summary>
        public bool Authority { get; set; }

        /// <summary>网络角色。</summary>
        public NetRole Role { get; set; } = NetRole.Authority;

        /// <summary>需要复制的属性列表。</summary>
        public List<string> ReplicatedProperties { get; } = new List<string>();

        /// <summary>RPC 注册表。</summary>
        public Dictionary<string, Action<object[]>> ServerRPCs { get; } = new Dictionary<string, Action<object[]>>();
        public Dictionary<string, Action<object[]>> ClientRPCs { get; } = new Dictionary<string, Action<object[]>>();

        /// <summary>上次属性复制时间戳。</summary>
        public float LastReplicationTime { get; set; }

        /// <summary>属性复制间隔（秒）。</summary>
        public float ReplicationInterval { get; set; } = 0.05f; // 20Hz

        /// <summary>是否需要属性更新。</summary>
        public bool NeedsReplication { get; set; }

        /// <summary>注册需要复制的属性。</summary>
        public void RegisterReplicatedProperty(string propertyName)
        {
            if (!ReplicatedProperties.Contains(propertyName))
                ReplicatedProperties.Add(propertyName);
        }

        /// <summary>注册服务器RPC。</summary>
        public void RegisterServerRPC(string name, Action<object[]> handler)
        {
            ServerRPCs[name] = handler;
        }

        /// <summary>注册客户端RPC。</summary>
        public void RegisterClientRPC(string name, Action<object[]> handler)
        {
            ClientRPCs[name] = handler;
        }

        /// <summary>调用服务器RPC。</summary>
        public void CallServerRPC(string name, params object[] args)
        {
            if (Authority)
            {
                // 本地调用
                if (ServerRPCs.TryGetValue(name, out var handler))
                    handler(args);
            }
            else
            {
                // 发送到服务器
                SendToServer(name, args);
            }
        }

        /// <summary>调用客户端RPC。</summary>
        public void CallClientRPC(string name, params object[] args)
        {
            if (!Authority)
            {
                if (ClientRPCs.TryGetValue(name, out var handler))
                    handler(args);
            }
            else
            {
                // 服务器广播到客户端
                BroadcastToClients(name, args);
            }
        }

        /// <summary>发送到服务器（实际通过NetworkSystem）。</summary>
        protected virtual void SendToServer(string rpcName, object[] args)
        {
            // 占位：实际实现通过NetworkSystem序列化并发送
        }

        /// <summary>广播到所有客户端。</summary>
        protected virtual void BroadcastToClients(string rpcName, object[] args)
        {
            // 占位：实际实现通过NetworkSystem广播
        }

        /// <summary>处理属性复制。</summary>
        public virtual void ProcessReplication(float deltaTime)
        {
            LastReplicationTime += deltaTime;
            if (LastReplicationTime >= ReplicationInterval)
            {
                LastReplicationTime = 0f;
                // 实际实现：序列化 ReplicatedProperties 并发送
            }
        }
    }
}
