// ============================================================================
// NetworkLobbySystem.cs
// 山雨游戏引擎 - 网络大厅系统
// 命名空间: ShanyuEngine.Network
// 依赖:     System.Collections.Concurrent, System.Net, System.Threading
//
// 功能:
//   1. LobbyState      — 大厅房间状态枚举 (Waiting / InProgress / Ended)
//   2. LobbyPlayerRole — 玩家角色枚举 (Host / Client)
//   3. LobbyPlayer     — 大厅玩家 (ID / 名称 / 角色 / 状态 / 延迟 / 心跳)
//   4. LobbyRoom       — 大厅房间 (ID / 名称 / 密码 / 最大人数 / 玩家列表 / 状态)
//   5. NetworkLobbySystem — 网络大厅系统
//      - CreateRoom(name, password, maxPlayers)  — 创建房间
//      - JoinRoom(roomId, password)              — 加入房间
//      - LeaveRoom(playerId)                     — 离开房间
//      - KickPlayer(hostId, targetId)            — 踢出玩家
//      - StartGame(hostId)                       — 开始游戏
//      - GetRoomList()                           — 获取房间列表
//      - Heartbeat(playerId)                     — 心跳更新
//      - 房间过期清理 (后台定时回收)
//      - 事件通知 (RoomCreated / PlayerJoined / PlayerLeft / GameStarted)
//
// 对接系统: NetworkCoreSystem / RoomSystem
// ============================================================================

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;

namespace ShanyuEngine.Network
{
    // ════════════════════════════════════════════════════════════════════════
    // 1. 枚举定义
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>大厅房间状态。</summary>
    public enum LobbyState
    {
        /// <summary>等待玩家加入 / 准备中。</summary>
        Waiting,
        /// <summary>游戏进行中 (房间锁定，不可再加入)。</summary>
        InProgress,
        /// <summary>房间已结束 (等待回收)。</summary>
        Ended
    }

    /// <summary>大厅玩家角色。</summary>
    public enum LobbyPlayerRole
    {
        /// <summary>房主，拥有踢人 / 开始游戏等权限。</summary>
        Host,
        /// <summary>普通客户端玩家。</summary>
        Client
    }

    /// <summary>大厅玩家连接状态。</summary>
    public enum LobbyPlayerState
    {
        /// <summary>正在连接。</summary>
        Connecting,
        /// <summary>已连接 / 在大厅中。</summary>
        Connected,
        /// <summary>已准备。</summary>
        Ready,
        /// <summary>正在加载游戏场景。</summary>
        Loading,
        /// <summary>已进入游戏。</summary>
        InGame,
        /// <summary>已断开 / 离线。</summary>
        Disconnected
    }

    /// <summary>大厅操作结果。</summary>
    public enum LobbyResult
    {
        /// <summary>操作成功。</summary>
        Success,
        /// <summary>房间不存在。</summary>
        RoomNotFound,
        /// <summary>房间已满。</summary>
        RoomFull,
        /// <summary>密码错误。</summary>
        WrongPassword,
        /// <summary>房间已锁定 (游戏进行中)。</summary>
        RoomLocked,
        /// <summary>玩家不在此房间。</summary>
        PlayerNotInRoom,
        /// <summary>玩家已在某房间中。</summary>
        PlayerAlreadyInRoom,
        /// <summary>权限不足 (非房主)。</summary>
        NotHost,
        /// <summary>找不到玩家。</summary>
        PlayerNotFound,
        /// <summary>参数无效。</summary>
        InvalidArgument
    }

    // ════════════════════════════════════════════════════════════════════════
    // 2. 大厅玩家
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 大厅玩家。记录玩家身份、角色、连接状态、网络延迟与最近一次心跳时间。
    /// </summary>
    public sealed class LobbyPlayer
    {
        /// <summary>玩家唯一标识。</summary>
        public string Id { get; }

        /// <summary>玩家显示名称。</summary>
        public string Name { get; set; }

        /// <summary>玩家角色 (房主 / 客户端)。</summary>
        public LobbyPlayerRole Role { get; set; }

        /// <summary>玩家连接状态。</summary>
        public LobbyPlayerState State { get; set; }

        /// <summary>网络延迟 (毫秒)。由心跳往返计算。</summary>
        public float LatencyMs { get; set; }

        /// <summary>玩家所在房间的 ID (便于反向查找)。</summary>
        public string? RoomId { get; internal set; }

        /// <summary>最近一次心跳的 UTC 时间戳。</summary>
        public DateTime LastHeartbeatUtc { get; internal set; }

        /// <summary>加入房间的 UTC 时间戳。</summary>
        public DateTime JoinedAtUtc { get; }

        /// <summary>玩家远程终结点 (IP:Port)，可能为空。</summary>
        public IPEndPoint? RemoteEndPoint { get; set; }

        /// <summary>是否就绪。</summary>
        public bool IsReady
        {
            get => State == LobbyPlayerState.Ready || State == LobbyPlayerState.InGame;
            set => State = value ? LobbyPlayerState.Ready : LobbyPlayerState.Connected;
        }

        /// <summary>是否在线 (心跳未超时)。</summary>
        public bool IsOnline => State != LobbyPlayerState.Disconnected;

        public LobbyPlayer(string id, string name, LobbyPlayerRole role = LobbyPlayerRole.Client)
        {
            Id = id ?? throw new ArgumentNullException(nameof(id));
            Name = name ?? string.Empty;
            Role = role;
            State = LobbyPlayerState.Connecting;
            var now = DateTime.UtcNow;
            LastHeartbeatUtc = now;
            JoinedAtUtc = now;
        }

        /// <summary>更新心跳并记录延迟。</summary>
        internal void OnHeartbeat(float latencyMs)
        {
            LatencyMs = latencyMs;
            LastHeartbeatUtc = DateTime.UtcNow;
            if (State == LobbyPlayerState.Connecting || State == LobbyPlayerState.Disconnected)
                State = LobbyPlayerState.Connected;
        }

        /// <inheritdoc/>
        public override string ToString()
            => $"[{Role}] {Name}({Id}) {State} {LatencyMs:F0}ms";
    }

    // ════════════════════════════════════════════════════════════════════════
    // 3. 大厅房间
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 大厅房间。线程安全地管理房间元数据与玩家集合 (使用 ConcurrentDictionary)。
    /// </summary>
    public sealed class LobbyRoom
    {
        private readonly ConcurrentDictionary<string, LobbyPlayer> _players = new();

        /// <summary>房间唯一标识。</summary>
        public string Id { get; }

        /// <summary>房间名称。</summary>
        public string Name { get; set; }

        /// <summary>房间密码 (空表示无密码)。</summary>
        public string? Password { get; set; }

        /// <summary>最大玩家数。</summary>
        public int MaxPlayers { get; set; }

        /// <summary>房间状态。</summary>
        public LobbyState State { get; set; }

        /// <summary>房主玩家 ID。</summary>
        public string HostId { get; internal set; }

        /// <summary>房间创建 UTC 时间。</summary>
        public DateTime CreatedAtUtc { get; }

        /// <summary>最近一次活动 UTC 时间 (用于过期清理)。</summary>
        public DateTime LastActivityUtc { get; internal set; }

        /// <summary>当前玩家数量。</summary>
        public int PlayerCount => _players.Count;

        /// <summary>是否已锁定 (游戏进行中)。</summary>
        public bool IsLocked => State == LobbyState.InProgress;

        /// <summary>是否需要密码。</summary>
        public bool HasPassword => !string.IsNullOrEmpty(Password);

        /// <summary>房间内所有玩家 (快照)。</summary>
        public IReadOnlyCollection<LobbyPlayer> Players => _players.Values.ToArray();

        /// <summary>房间自定义元数据 (地图、模式等)。</summary>
        public ConcurrentDictionary<string, string> Metadata { get; } = new();

        public LobbyRoom(string id, string name, string? password, int maxPlayers, string hostId)
        {
            Id = id ?? throw new ArgumentNullException(nameof(id));
            Name = name ?? throw new ArgumentNullException(nameof(name));
            Password = password;
            MaxPlayers = maxPlayers;
            HostId = hostId;
            State = LobbyState.Waiting;
            var now = DateTime.UtcNow;
            CreatedAtUtc = now;
            LastActivityUtc = now;
        }

        /// <summary>尝试获取玩家。</summary>
        public bool TryGetPlayer(string playerId, out LobbyPlayer? player)
            => _players.TryGetValue(playerId, out player);

        /// <summary>添加玩家 (内部使用)。</summary>
        internal bool TryAddPlayer(LobbyPlayer player)
        {
            if (State == LobbyState.InProgress) return false;
            if (_players.Count >= MaxPlayers) return false;
            return _players.TryAdd(player.Id, player);
        }

        /// <summary>移除玩家 (内部使用)。</summary>
        internal bool TryRemovePlayer(string playerId, out LobbyPlayer? player)
        {
            var removed = _players.TryRemove(playerId, out player);
            if (removed) Touch();
            return removed;
        }

        /// <summary>更新最近活动时间。</summary>
        internal void Touch() => LastActivityUtc = DateTime.UtcNow;

        /// <summary>获取房间公开摘要 (不含密码)。</summary>
        public LobbyRoomInfo ToInfo()
        {
            return new LobbyRoomInfo
            {
                Id = Id,
                Name = Name,
                HasPassword = HasPassword,
                MaxPlayers = MaxPlayers,
                PlayerCount = PlayerCount,
                State = State,
                HostId = HostId
            };
        }

        /// <inheritdoc/>
        public override string ToString()
            => $"Room[{Name}] {Id} {State} {PlayerCount}/{MaxPlayers}";
    }

    /// <summary>房间公开摘要 (不含敏感信息)。</summary>
    public sealed class LobbyRoomInfo
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public bool HasPassword { get; set; }
        public int MaxPlayers { get; set; }
        public int PlayerCount { get; set; }
        public LobbyState State { get; set; }
        public string HostId { get; set; } = string.Empty;
    }

    // ════════════════════════════════════════════════════════════════════════
    // 4. 大厅事件参数
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>大厅事件参数基类。</summary>
    public sealed class LobbyEventArgs : EventArgs
    {
        /// <summary>相关房间。</summary>
        public LobbyRoom Room { get; }

        /// <summary>相关玩家 (可能为空)。</summary>
        public LobbyPlayer? Player { get; }

        /// <summary>事件发生 UTC 时间。</summary>
        public DateTime TimestampUtc { get; }

        public LobbyEventArgs(LobbyRoom room, LobbyPlayer? player = null)
        {
            Room = room;
            Player = player;
            TimestampUtc = DateTime.UtcNow;
        }
    }

    /// <summary>玩家离开原因。</summary>
    public enum LeaveReason
    {
        /// <summary>主动离开。</summary>
        Voluntary,
        /// <summary>被踢出。</summary>
        Kicked,
        /// <summary>心跳超时 / 掉线。</summary>
        Timeout,
        /// <summary>房间关闭。</summary>
        RoomClosed
    }

    /// <summary>玩家离开事件参数。</summary>
    public sealed class PlayerLeftEventArgs : EventArgs
    {
        public LobbyRoom Room { get; }
        public LobbyPlayer Player { get; }
        public LeaveReason Reason { get; }
        public DateTime TimestampUtc { get; }

        public PlayerLeftEventArgs(LobbyRoom room, LobbyPlayer player, LeaveReason reason)
        {
            Room = room;
            Player = player;
            Reason = reason;
            TimestampUtc = DateTime.UtcNow;
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // 5. 网络大厅系统
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 山雨引擎网络大厅系统。提供房间创建 / 加入 / 离开 / 踢人 / 开始游戏 /
    /// 房间列表 / 心跳维护 / 过期清理 / 事件通知等完整大厅功能。
    /// 全部状态使用 <see cref="ConcurrentDictionary{TKey,TValue}"/> 存储，线程安全。
    /// </summary>
    public sealed class NetworkLobbySystem : IDisposable
    {
        // ──────────────────────────────────────────────────────────────
        // 5.1 字段
        // ──────────────────────────────────────────────────────────────

        private readonly ConcurrentDictionary<string, LobbyRoom> _rooms = new();
        private readonly ConcurrentDictionary<string, LobbyPlayer> _players = new();
        private readonly ConcurrentDictionary<string, string> _playerToRoom = new();

        private readonly Timer _cleanupTimer;
        private readonly TimeSpan _heartbeatTimeout;
        private readonly TimeSpan _roomIdleTimeout;
        private long _roomCounter;
        private bool _disposed;

        // ──────────────────────────────────────────────────────────────
        // 5.2 公共属性
        // ──────────────────────────────────────────────────────────────

        /// <summary>当前房间总数。</summary>
        public int RoomCount => _rooms.Count;

        /// <summary>当前在线玩家总数。</summary>
        public int PlayerCount => _players.Count;

        /// <summary>心跳超时时长 (玩家超过此时长未心跳视为掉线)。</summary>
        public TimeSpan HeartbeatTimeout => _heartbeatTimeout;

        /// <summary>房间空闲超时时长 (Ended 状态房间超过此时长被回收)。</summary>
        public TimeSpan RoomIdleTimeout => _roomIdleTimeout;

        // ──────────────────────────────────────────────────────────────
        // 5.3 事件
        // ──────────────────────────────────────────────────────────────

        /// <summary>房间被创建时触发。</summary>
        public event EventHandler<LobbyEventArgs>? RoomCreated;

        /// <summary>玩家加入房间时触发。</summary>
        public event EventHandler<LobbyEventArgs>? PlayerJoined;

        /// <summary>玩家离开房间时触发。</summary>
        public event EventHandler<PlayerLeftEventArgs>? PlayerLeft;

        /// <summary>游戏开始时触发。</summary>
        public event EventHandler<LobbyEventArgs>? GameStarted;

        // ──────────────────────────────────────────────────────────────
        // 5.4 构造
        // ──────────────────────────────────────────────────────────────

        /// <summary>
        /// 构造网络大厅系统。
        /// </summary>
        /// <param name="heartbeatTimeout">心跳超时 (默认 15 秒)。</param>
        /// <param name="roomIdleTimeout">房间空闲回收时长 (默认 5 分钟)。</param>
        /// <param name="cleanupInterval">清理检查间隔 (默认 30 秒)。</param>
        public NetworkLobbySystem(
            TimeSpan? heartbeatTimeout = null,
            TimeSpan? roomIdleTimeout = null,
            TimeSpan? cleanupInterval = null)
        {
            _heartbeatTimeout = heartbeatTimeout ?? TimeSpan.FromSeconds(15);
            _roomIdleTimeout = roomIdleTimeout ?? TimeSpan.FromMinutes(5);

            var interval = cleanupInterval ?? TimeSpan.FromSeconds(30);
            _cleanupTimer = new Timer(OnCleanupTick, null, interval, interval);
        }

        // ──────────────────────────────────────────────────────────────
        // 5.5 创建房间
        // ──────────────────────────────────────────────────────────────

        /// <summary>
        /// 创建房间。创建者自动成为房主并加入房间。
        /// </summary>
        /// <param name="hostPlayerId">房主玩家 ID。</param>
        /// <param name="hostName">房主显示名称。</param>
        /// <param name="name">房间名称。</param>
        /// <param name="password">房间密码 (可空)。</param>
        /// <param name="maxPlayers">最大玩家数。</param>
        /// <param name="room">创建的房间。</param>
        /// <returns>操作结果。</returns>
        public LobbyResult CreateRoom(string hostPlayerId, string hostName, string name,
                                       string? password, int maxPlayers, out LobbyRoom? room)
        {
            room = null;
            if (string.IsNullOrWhiteSpace(hostPlayerId))
                return LobbyResult.InvalidArgument;
            if (string.IsNullOrWhiteSpace(name))
                return LobbyResult.InvalidArgument;
            if (maxPlayers < 1 || maxPlayers > 256)
                return LobbyResult.InvalidArgument;
            if (_playerToRoom.ContainsKey(hostPlayerId))
                return LobbyResult.PlayerAlreadyInRoom;

            var roomId = GenerateRoomId();
            var host = new LobbyPlayer(hostPlayerId, hostName ?? hostPlayerId, LobbyPlayerRole.Host)
            {
                State = LobbyPlayerState.Connected,
                RoomId = roomId
            };

            var newRoom = new LobbyRoom(roomId, name, password, maxPlayers, hostPlayerId);
            if (!newRoom.TryAddPlayer(host))
                return LobbyResult.RoomFull;

            _rooms[roomId] = newRoom;
            _players[hostPlayerId] = host;
            _playerToRoom[hostPlayerId] = roomId;

            room = newRoom;
            RoomCreated?.Invoke(this, new LobbyEventArgs(newRoom, host));
            return LobbyResult.Success;
        }

        // ──────────────────────────────────────────────────────────────
        // 5.6 加入房间
        // ──────────────────────────────────────────────────────────────

        /// <summary>
        /// 加入房间。
        /// </summary>
        /// <param name="playerId">玩家 ID。</param>
        /// <param name="playerName">玩家显示名称。</param>
        /// <param name="roomId">目标房间 ID。</param>
        /// <param name="password">房间密码 (房间无密码时可空)。</param>
        /// <param name="room">加入的房间。</param>
        /// <returns>操作结果。</returns>
        public LobbyResult JoinRoom(string playerId, string playerName, string roomId,
                                     string? password, out LobbyRoom? room)
        {
            room = null;
            if (string.IsNullOrWhiteSpace(playerId) || string.IsNullOrWhiteSpace(roomId))
                return LobbyResult.InvalidArgument;
            if (_playerToRoom.ContainsKey(playerId))
                return LobbyResult.PlayerAlreadyInRoom;

            if (!_rooms.TryGetValue(roomId, out var target))
                return LobbyResult.RoomNotFound;

            if (target.IsLocked)
                return LobbyResult.RoomLocked;

            if (target.HasPassword && !string.Equals(target.Password, password, StringComparison.Ordinal))
                return LobbyResult.WrongPassword;

            if (target.PlayerCount >= target.MaxPlayers)
                return LobbyResult.RoomFull;

            var player = new LobbyPlayer(playerId, playerName ?? playerId, LobbyPlayerRole.Client)
            {
                State = LobbyPlayerState.Connected,
                RoomId = roomId
            };

            if (!target.TryAddPlayer(player))
                return LobbyResult.RoomFull;

            _players[playerId] = player;
            _playerToRoom[playerId] = roomId;
            target.Touch();

            room = target;
            PlayerJoined?.Invoke(this, new LobbyEventArgs(target, player));
            return LobbyResult.Success;
        }

        // ──────────────────────────────────────────────────────────────
        // 5.7 离开房间
        // ──────────────────────────────────────────────────────────────

        /// <summary>
        /// 离开当前所在房间。若离开者为房主，则迁移房主或关闭房间。
        /// </summary>
        /// <param name="playerId">玩家 ID。</param>
        /// <returns>操作结果。</returns>
        public LobbyResult LeaveRoom(string playerId)
        {
            return LeaveRoomInternal(playerId, LeaveReason.Voluntary, out _);
        }

        /// <summary>离开房间的内部实现。</summary>
        private LobbyResult LeaveRoomInternal(string playerId, LeaveReason reason, out LobbyRoom? room)
        {
            room = null;
            if (string.IsNullOrWhiteSpace(playerId))
                return LobbyResult.InvalidArgument;

            if (!_playerToRoom.TryGetValue(playerId, out var roomId))
                return LobbyResult.PlayerNotInRoom;

            if (!_rooms.TryGetValue(roomId, out var target))
            {
                // 房间已不存在，清理玩家残留映射
                _playerToRoom.TryRemove(playerId, out _);
                _players.TryRemove(playerId, out _);
                return LobbyResult.RoomNotFound;
            }

            if (!target.TryRemovePlayer(playerId, out var player))
                return LobbyResult.PlayerNotInRoom;

            _players.TryRemove(playerId, out _);
            _playerToRoom.TryRemove(playerId, out _);
            player.State = LobbyPlayerState.Disconnected;
            target.Touch();
            room = target;

            PlayerLeft?.Invoke(this, new PlayerLeftEventArgs(target, player, reason));

            // 房主离开: 迁移房主或关闭房间
            if (target.HostId == playerId)
            {
                var newHost = target.Players.FirstOrDefault();
                if (newHost != null)
                {
                    target.HostId = newHost.Id;
                    newHost.Role = LobbyPlayerRole.Host;
                }
                else
                {
                    // 无人剩余，关闭房间
                    target.State = LobbyState.Ended;
                    _rooms.TryRemove(roomId, out _);
                }
            }

            return LobbyResult.Success;
        }

        // ──────────────────────────────────────────────────────────────
        // 5.8 踢出玩家
        // ──────────────────────────────────────────────────────────────

        /// <summary>
        /// 踢出指定玩家。仅房主可执行。
        /// </summary>
        /// <param name="hostId">执行踢人操作的房主 ID。</param>
        /// <param name="targetId">被踢玩家 ID。</param>
        /// <returns>操作结果。</returns>
        public LobbyResult KickPlayer(string hostId, string targetId)
        {
            if (string.IsNullOrWhiteSpace(hostId) || string.IsNullOrWhiteSpace(targetId))
                return LobbyResult.InvalidArgument;
            if (hostId == targetId)
                return LobbyResult.InvalidArgument;

            if (!_playerToRoom.TryGetValue(hostId, out var roomId))
                return LobbyResult.PlayerNotInRoom;

            if (!_rooms.TryGetValue(roomId, out var room))
                return LobbyResult.RoomNotFound;

            if (room.HostId != hostId)
                return LobbyResult.NotHost;

            if (!room.TryGetPlayer(targetId, out _))
                return LobbyResult.PlayerNotInRoom;

            return LeaveRoomInternal(targetId, LeaveReason.Kicked, out _);
        }

        // ──────────────────────────────────────────────────────────────
        // 5.9 开始游戏
        // ──────────────────────────────────────────────────────────────

        /// <summary>
        /// 开始游戏。仅房主可执行。将房间状态置为 InProgress 并锁定，禁止新玩家加入。
        /// </summary>
        /// <param name="hostId">房主 ID。</param>
        /// <param name="requireAllReady">是否要求所有玩家已准备。</param>
        /// <returns>操作结果。</returns>
        public LobbyResult StartGame(string hostId, bool requireAllReady = false)
        {
            if (string.IsNullOrWhiteSpace(hostId))
                return LobbyResult.InvalidArgument;

            if (!_playerToRoom.TryGetValue(hostId, out var roomId))
                return LobbyResult.PlayerNotInRoom;

            if (!_rooms.TryGetValue(roomId, out var room))
                return LobbyResult.RoomNotFound;

            if (room.HostId != hostId)
                return LobbyResult.NotHost;

            if (room.State != LobbyState.Waiting)
                return LobbyResult.RoomLocked;

            if (room.PlayerCount < 1)
                return LobbyResult.InvalidArgument;

            if (requireAllReady)
            {
                foreach (var p in room.Players)
                {
                    if (!p.IsReady)
                        return LobbyResult.InvalidArgument;
                }
            }

            room.State = LobbyState.InProgress;
            room.Touch();
            foreach (var p in room.Players)
                p.State = LobbyPlayerState.InGame;

            GameStarted?.Invoke(this, new LobbyEventArgs(room));
            return LobbyResult.Success;
        }

        /// <summary>结束游戏，将房间恢复为等待状态 (仅房主)。</summary>
        public LobbyResult EndGame(string hostId)
        {
            if (string.IsNullOrWhiteSpace(hostId))
                return LobbyResult.InvalidArgument;

            if (!_playerToRoom.TryGetValue(hostId, out var roomId))
                return LobbyResult.PlayerNotInRoom;

            if (!_rooms.TryGetValue(roomId, out var room))
                return LobbyResult.RoomNotFound;

            if (room.HostId != hostId)
                return LobbyResult.NotHost;

            if (room.State != LobbyState.InProgress)
                return LobbyResult.InvalidArgument;

            room.State = LobbyState.Waiting;
            room.Touch();
            foreach (var p in room.Players)
                p.State = LobbyPlayerState.Connected;

            return LobbyResult.Success;
        }

        // ──────────────────────────────────────────────────────────────
        // 5.10 房间列表与查询
        // ──────────────────────────────────────────────────────────────

        /// <summary>
        /// 获取房间列表 (公开摘要快照，不含密码)。
        /// </summary>
        /// <param name="includeLocked">是否包含已锁定 (游戏进行中) 的房间。</param>
        /// <returns>房间摘要列表。</returns>
        public List<LobbyRoomInfo> GetRoomList(bool includeLocked = false)
        {
            var result = new List<LobbyRoomInfo>(_rooms.Count);
            foreach (var kv in _rooms)
            {
                var room = kv.Value;
                if (!includeLocked && room.IsLocked)
                    continue;
                result.Add(room.ToInfo());
            }
            return result;
        }

        /// <summary>获取所有房间的快照。</summary>
        public IReadOnlyList<LobbyRoom> GetRooms() => _rooms.Values.ToArray();

        /// <summary>按 ID 获取房间。</summary>
        public bool TryGetRoom(string roomId, out LobbyRoom? room)
            => _rooms.TryGetValue(roomId, out room);

        /// <summary>获取玩家当前所在房间。</summary>
        public bool TryGetPlayerRoom(string playerId, out LobbyRoom? room)
        {
            room = null;
            if (!_playerToRoom.TryGetValue(playerId, out var roomId))
                return false;
            return _rooms.TryGetValue(roomId, out room);
        }

        /// <summary>获取玩家对象。</summary>
        public bool TryGetPlayer(string playerId, out LobbyPlayer? player)
            => _players.TryGetValue(playerId, out player);

        // ──────────────────────────────────────────────────────────────
        // 5.11 心跳更新
        // ──────────────────────────────────────────────────────────────

        /// <summary>
        /// 玩家心跳更新。刷新最近心跳时间并记录延迟。
        /// </summary>
        /// <param name="playerId">玩家 ID。</param>
        /// <param name="latencyMs">网络延迟 (毫秒)。</param>
        /// <returns>操作结果。</returns>
        public LobbyResult Heartbeat(string playerId, float latencyMs = 0f)
        {
            if (string.IsNullOrWhiteSpace(playerId))
                return LobbyResult.InvalidArgument;

            if (!_players.TryGetValue(playerId, out var player))
                return LobbyResult.PlayerNotFound;

            player.OnHeartbeat(latencyMs);

            // 同步房间活动时间
            if (_playerToRoom.TryGetValue(playerId, out var roomId) &&
                _rooms.TryGetValue(roomId, out var room))
            {
                room.Touch();
            }

            return LobbyResult.Success;
        }

        /// <summary>批量更新心跳 (用于服务端处理心跳包)。</summary>
        public void HeartbeatBatch(IEnumerable<KeyValuePair<string, float>> heartbeats)
        {
            if (heartbeats == null) return;
            foreach (var kv in heartbeats)
            {
                if (_players.TryGetValue(kv.Key, out var player))
                    player.OnHeartbeat(kv.Value);
            }
        }

        // ──────────────────────────────────────────────────────────────
        // 5.12 房间过期清理
        // ──────────────────────────────────────────────────────────────

        /// <summary>
        /// 执行一次过期清理:
        /// 1) 心跳超时的玩家被判定掉线并移出房间;
        /// 2) Ended 状态或长时间空闲的房间被回收。
        /// </summary>
        /// <returns>本次清理移除的玩家数与房间数。</returns>
        public (int removedPlayers, int removedRooms) CleanupExpired()
        {
            int removedPlayers = 0;
            int removedRooms = 0;
            var now = DateTime.UtcNow;

            // 1) 清理心跳超时玩家
            var timedOutPlayers = _players
                .Where(p => (now - p.Value.LastHeartbeatUtc) > _heartbeatTimeout)
                .Select(p => p.Key)
                .ToList();

            foreach (var pid in timedOutPlayers)
            {
                if (LeaveRoomInternal(pid, LeaveReason.Timeout, out _) == LobbyResult.Success)
                    removedPlayers++;
            }

            // 2) 清理过期房间
            var expiredRooms = _rooms
                .Where(kv =>
                {
                    var r = kv.Value;
                    if (r.State == LobbyState.Ended) return true;
                    if (r.PlayerCount == 0 && (now - r.LastActivityUtc) > _roomIdleTimeout) return true;
                    return false;
                })
                .Select(kv => kv.Key)
                .ToList();

            foreach (var rid in expiredRooms)
            {
                if (_rooms.TryRemove(rid, out var room))
                {
                    // 清理房间内残留玩家映射
                    foreach (var p in room.Players)
                    {
                        _players.TryRemove(p.Id, out _);
                        _playerToRoom.TryRemove(p.Id, out _);
                        PlayerLeft?.Invoke(this, new PlayerLeftEventArgs(room, p, LeaveReason.RoomClosed));
                    }
                    removedRooms++;
                }
            }

            return (removedPlayers, removedRooms);
        }

        /// <summary>定时清理回调。</summary>
        private void OnCleanupTick(object? state)
        {
            try
            {
                CleanupExpired();
            }
            catch
            {
                // 清理异常不应中断定时器
            }
        }

        // ──────────────────────────────────────────────────────────────
        // 5.13 辅助方法
        // ──────────────────────────────────────────────────────────────

        /// <summary>生成唯一房间 ID。</summary>
        private string GenerateRoomId()
        {
            var seq = Interlocked.Increment(ref _roomCounter);
            var ts = DateTime.UtcNow.Ticks;
            return $"R{ts:X}_{seq:X}";
        }

        /// <summary>设置玩家准备状态。</summary>
        public LobbyResult SetReady(string playerId, bool ready)
        {
            if (!_players.TryGetValue(playerId, out var player))
                return LobbyResult.PlayerNotFound;
            if (!_playerToRoom.TryGetValue(playerId, out var roomId) ||
                !_rooms.TryGetValue(roomId, out var room))
                return LobbyResult.PlayerNotInRoom;
            if (room.IsLocked)
                return LobbyResult.RoomLocked;

            player.IsReady = ready;
            room.Touch();
            return LobbyResult.Success;
        }

        /// <summary>关闭并移除指定房间 (仅房主或系统调用)。</summary>
        public bool CloseRoom(string roomId)
        {
            if (!_rooms.TryRemove(roomId, out var room))
                return false;

            room.State = LobbyState.Ended;
            foreach (var p in room.Players)
            {
                _players.TryRemove(p.Id, out _);
                _playerToRoom.TryRemove(p.Id, out _);
                p.State = LobbyPlayerState.Disconnected;
                PlayerLeft?.Invoke(this, new PlayerLeftEventArgs(room, p, LeaveReason.RoomClosed));
            }
            return true;
        }

        // ──────────────────────────────────────────────────────────────
        // 5.14 资源释放
        // ──────────────────────────────────────────────────────────────

        /// <summary>释放大厅系统，停止清理定时器并关闭所有房间。</summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _cleanupTimer.Dispose();

            foreach (var roomId in _rooms.Keys.ToList())
                CloseRoom(roomId);

            _players.Clear();
            _playerToRoom.Clear();
        }
    }
}
