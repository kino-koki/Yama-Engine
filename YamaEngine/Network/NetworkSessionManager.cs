// ============================================================================
//  YamaEngine.Network.NetworkSessionManager.cs
//  yama引擎 -- 网络会话管理器 (端到端连接生命周期)
// ----------------------------------------------------------------------------
//  闭合"完整会话管理"缺口:
//    * 把 NetworkTransportLayer(连接事件) + NetworkLobbySystem(大厅/房间)
//      + ReplicationGraphReal(兴趣管理) 整合为一条端到端会话管线:
//        连接到达 → 玩家入会话(大厅) → 建房/入房 → 开始游戏 → 结束 → 离开
//    * 连接生命周期: OnConnected 注册玩家+兴趣观察者, OnDisconnected 清理
//      玩家/房间成员/兴趣图/会话状态。
//    * 会话状态机: None → Lobby → InRoom → InGame → Lobby/None。
//  对 UE 的整合: 类比 UNetConnection + GameSession (UE4.7 会话/大厅)。
// ============================================================================

using System;
using System.Collections.Generic;
using System.Net;

namespace YamaEngine.Network
{
    /// <summary>会话状态。</summary>
    public enum SessionState
    {
        /// <summary>无会话。</summary>
        None,
        /// <summary>已连接, 在大厅中 (未入房)。</summary>
        Lobby,
        /// <summary>已在房间中。</summary>
        InRoom,
        /// <summary>游戏进行中。</summary>
        InGame,
        /// <summary>会话结束 (等待清理)。</summary>
        Ending
    }

    /// <summary>
    /// 网络会话管理器。整合传输层连接事件、大厅/房间系统与兴趣图,
    /// 提供端到端的会话生命周期。
    /// </summary>
    public sealed class NetworkSessionManager : IDisposable
    {
        private readonly NetworkTransportLayer? _transport;
        private readonly NetworkLobbySystem _lobby;
        private readonly ReplicationGraphReal? _interestGraph;

        private readonly Dictionary<int, string> _connectionToPlayer = new();
        private readonly Dictionary<string, int> _playerToConnection = new();
        private readonly Dictionary<int, IPEndPoint> _connectionToEndpoint = new();
        private bool _disposed;

        /// <summary>当前会话状态。</summary>
        public SessionState State { get; private set; } = SessionState.None;

        /// <summary>当前房间 ID (未入房为空)。</summary>
        public string? RoomId { get; private set; }

        /// <summary>是否服务器端会话。</summary>
        public bool IsServer { get; private set; }

        /// <summary>在线玩家数。</summary>
        public int OnlinePlayerCount => _connectionToPlayer.Count;

        // ── 事件 ────────────────────────────────────────────────────

        /// <summary>玩家加入会话 (连接到达) 时触发。</summary>
        public event Action<int, string>? OnPlayerJoinedSession;

        /// <summary>玩家离开会话 (连接断开) 时触发。</summary>
        public event Action<int, string>? OnPlayerLeftSession;

        /// <summary>游戏开始 (StartGame 成功) 时触发。</summary>
        public event Action<string>? OnGameStarted;

        /// <summary>游戏结束 (EndGame) 时触发。</summary>
        public event Action<string>? OnGameEnded;

        // ── 构造 ────────────────────────────────────────────────────

        /// <summary>
        /// 构造会话管理器。
        /// </summary>
        /// <param name="transport">传输层 (可空; 为空时需手动调用 HandleConnection*)。</param>
        /// <param name="lobby">大厅系统 (可空; 为空时内部创建)。</param>
        /// <param name="interestGraph">兴趣图 (可空)。</param>
        /// <param name="isServer">是否服务器端。</param>
        public NetworkSessionManager(NetworkTransportLayer? transport = null,
            NetworkLobbySystem? lobby = null,
            ReplicationGraphReal? interestGraph = null,
            bool isServer = true)
        {
            _transport = transport;
            _lobby = lobby ?? new NetworkLobbySystem();
            _interestGraph = interestGraph;
            IsServer = isServer;

            if (_transport != null)
            {
                _transport.OnConnected += (_, e) => HandleConnectionConnected(e.ConnectionId, e.RemoteEndpoint);
                _transport.OnDisconnected += (_, e) => HandleConnectionDisconnected(e.ConnectionId);
            }
        }

        // ── 连接生命周期 ────────────────────────────────────────────

        /// <summary>
        /// 连接到达: 注册玩家入会话 (大厅), 并添加兴趣图观察者。
        /// 可由传输层 OnConnected 驱动, 也可手动调用 (测试)。
        /// </summary>
        /// <param name="connectionId">连接 ID。</param>
        /// <param name="endpoint">远程端点 (可选)。</param>
        /// <param name="playerName">玩家显示名 (可选, 默认 player-{id})。</param>
        /// <returns>生成的玩家 ID。</returns>
        public string HandleConnectionConnected(int connectionId, IPEndPoint? endpoint = null, string? playerName = null)
        {
            string playerId = $"player-{connectionId}";
            _connectionToPlayer[connectionId] = playerId;
            _playerToConnection[playerId] = connectionId;

            // 缓存端点, 保证断开时即使无运输层也能移除兴趣图观察者
            if (endpoint != null)
                _connectionToEndpoint[connectionId] = endpoint;

            if (_interestGraph != null && endpoint != null)
                _interestGraph.AddConnection(endpoint, default, 1000f);

            if (State == SessionState.None)
                State = SessionState.Lobby;

            OnPlayerJoinedSession?.Invoke(connectionId, playerId);
            return playerId;
        }

        /// <summary>
        /// 连接断开: 离开房间, 清理兴趣图观察者, 移除会话玩家。
        /// 可由传输层 OnDisconnected 驱动, 也可手动调用 (测试)。
        /// </summary>
        public void HandleConnectionDisconnected(int connectionId)
        {
            if (!_connectionToPlayer.TryGetValue(connectionId, out var playerId))
                return;

            // 若在房间中, 先离开
            if (!string.IsNullOrEmpty(RoomId) || _lobby.TryGetPlayerRoom(playerId, out _))
                _lobby.LeaveRoom(playerId);

            // 优先使用缓存的端点; 无缓存时回退到运输层查找
            IPEndPoint? endpoint = _connectionToEndpoint.TryGetValue(connectionId, out var cached)
                ? cached
                : _transport?.GetConnection(connectionId)?.RemoteEndpoint;

            if (_interestGraph != null && endpoint != null)
                _interestGraph.RemoveConnection(endpoint);

            _connectionToEndpoint.Remove(connectionId);
            _connectionToPlayer.Remove(connectionId);
            _playerToConnection.Remove(playerId);
            OnPlayerLeftSession?.Invoke(connectionId, playerId);

            if (_connectionToPlayer.Count == 0)
                State = SessionState.None;
        }

        // ── 会话操作 ────────────────────────────────────────────────

        /// <summary>创建会话 (房主)。建房并自动加入。</summary>
        public LobbyResult CreateSession(int hostConnectionId, string roomName,
            string? password, int maxPlayers, string? hostName = null)
        {
            if (!_connectionToPlayer.TryGetValue(hostConnectionId, out var hostId))
                hostId = HandleConnectionConnected(hostConnectionId);

            var result = _lobby.CreateRoom(hostId, hostName ?? hostId, roomName, password, maxPlayers, out var room);
            if (result == LobbyResult.Success)
            {
                RoomId = room!.Id;
                State = SessionState.InRoom;
            }
            return result;
        }

        /// <summary>加入会话 (客户端)。</summary>
        public LobbyResult JoinSession(int clientConnectionId, string roomId,
            string? password, string? playerName = null)
        {
            if (!_connectionToPlayer.TryGetValue(clientConnectionId, out var playerId))
                playerId = HandleConnectionConnected(clientConnectionId, playerName: playerName);

            var result = _lobby.JoinRoom(playerId, playerName ?? playerId, roomId, password, out var room);
            if (result == LobbyResult.Success)
            {
                RoomId = room!.Id;
                State = SessionState.InRoom;
            }
            return result;
        }

        /// <summary>离开当前房间 (保留会话连接)。</summary>
        public LobbyResult LeaveRoom(int connectionId)
        {
            if (!_connectionToPlayer.TryGetValue(connectionId, out var playerId))
                return LobbyResult.PlayerNotFound;

            var result = _lobby.LeaveRoom(playerId);
            if (result == LobbyResult.Success)
            {
                RoomId = null;
                State = SessionState.Lobby;
            }
            return result;
        }

        /// <summary>开始游戏 (房主)。锁定房间并进入 InGame。</summary>
        public LobbyResult StartGame(int hostConnectionId, bool requireAllReady = false)
        {
            if (!_connectionToPlayer.TryGetValue(hostConnectionId, out var hostId))
                return LobbyResult.PlayerNotFound;

            var result = _lobby.StartGame(hostId, requireAllReady);
            if (result == LobbyResult.Success)
            {
                State = SessionState.InGame;
                OnGameStarted?.Invoke(RoomId ?? string.Empty);
            }
            return result;
        }

        /// <summary>结束游戏 (房主)。返回大厅。</summary>
        public LobbyResult EndGame(int hostConnectionId)
        {
            if (!_connectionToPlayer.TryGetValue(hostConnectionId, out var hostId))
                return LobbyResult.PlayerNotFound;

            var result = _lobby.EndGame(hostId);
            if (result == LobbyResult.Success)
            {
                State = SessionState.Lobby;
                OnGameEnded?.Invoke(RoomId ?? string.Empty);
            }
            return result;
        }

        /// <summary>心跳 (保持连接活跃, 更新延迟)。</summary>
        public void Heartbeat(int connectionId, float latencyMs = 0f)
        {
            if (_connectionToPlayer.TryGetValue(connectionId, out var playerId))
                _lobby.Heartbeat(playerId, latencyMs);
        }

        /// <summary>逐帧更新 (会话层可扩展: 心跳/超时检查)。</summary>
        public void Update(float deltaTime)
        {
            // 会话层当前无每帧必做逻辑 (心跳由 Heartbeat 显式驱动)
        }

        /// <summary>获取玩家 ID (未注册返回 null)。</summary>
        public string? GetPlayerId(int connectionId)
            => _connectionToPlayer.TryGetValue(connectionId, out var p) ? p : null;

        /// <summary>获取连接 ID (未注册返回 -1)。</summary>
        public int GetConnectionId(string playerId)
            => _playerToConnection.TryGetValue(playerId, out var c) ? c : -1;

        /// <summary>是否已注册某连接。</summary>
        public bool HasConnection(int connectionId) => _connectionToPlayer.ContainsKey(connectionId);

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            if (_transport != null)
            {
                _transport.OnConnected -= (_, e) => HandleConnectionConnected(e.ConnectionId, e.RemoteEndpoint);
                _transport.OnDisconnected -= (_, e) => HandleConnectionDisconnected(e.ConnectionId);
            }
        }
    }
}
