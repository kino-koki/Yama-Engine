using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading;

namespace ShanyuEngine.Network
{
    // ════════════════════════════════════════════════════════════════════════
    // 山雨引擎 - 房间/大厅系统 (RoomSystem)
    //
    // 类似 UE Session / Unity Lobby / Steam Lobby 的完整房间管理管线:
    //   * Room: 房间 (ID/名称/最大玩家/密码/地图/模式/状态/设置)
    //   * RoomState 枚举: Waiting/Starting/Playing/Paused/Ending/Closing/Full
    //   * RoomSettings: 房间设置 (自定义键值对/模式参数/规则)
    //   * RoomManager: 房间管理器 (创建/加入/离开/踢出/搜索/列表/过滤)
    //   * RoomPlayer: 房间玩家 (ID/名称/团队/准备/角色/自定义属性)
    //   * 房间事件: OnPlayerJoined/OnPlayerLeft/OnSettingsChanged/OnGameStarted
    //   * 房间匹配: 简单匹配/技能匹配/区域匹配/自定义匹配规则
    //   * 房间可见性: Public/Private/FriendsOnly/InviteOnly/Invisible
    //   * 房间聊天: 文本/语音
    //   * 房间锁定/解锁/密码变更
    //
    // 对接系统: NetworkCoreSystem / NetworkObjectSystem
    // ════════════════════════════════════════════════════════════════════════

    // ──────────────────────────────────────────────────────────────
    // 1. 枚举定义
    // ──────────────────────────────────────────────────────────────

    /// <summary>房间状态。</summary>
    public enum RoomState
    {
        /// <summary>等待玩家加入。</summary>
        Waiting,
        /// <summary>正在开始 (倒计时)。</summary>
        Starting,
        /// <summary>游戏进行中。</summary>
        Playing,
        /// <summary>游戏暂停。</summary>
        Paused,
        /// <summary>游戏结束。</summary>
        Ending,
        /// <summary>房间正在关闭。</summary>
        Closing,
        /// <summary>房间已满。</summary>
        Full
    }

    /// <summary>房间可见性。</summary>
    public enum RoomVisibility
    {
        /// <summary>公开, 任何人可搜索并加入。</summary>
        Public,
        /// <summary>私有, 需要邀请或密码。</summary>
        Private,
        /// <summary>仅好友可见。</summary>
        FriendsOnly,
        /// <summary>仅邀请可见。</summary>
        InviteOnly,
        /// <summary>完全隐藏, 不出现在搜索结果中。</summary>
        Invisible
    }

    /// <summary>聊天消息类型。</summary>
    public enum ChatMessageType
    {
        /// <summary>系统消息。</summary>
        System,
        /// <summary>玩家文本消息。</summary>
        Text,
        /// <summary>队伍消息。</summary>
        Team,
        /// <summary>私人消息。</summary>
        Whisper,
        /// <summary>语音消息。</summary>
        Voice
    }

    // ──────────────────────────────────────────────────────────────
    // 2. 房间设置
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 房间设置。存储自定义键值对、模式参数、游戏规则等。
    /// </summary>
    public sealed class RoomSettings
    {
        private readonly Dictionary<string, object> _values = new Dictionary<string, object>();

        /// <summary>游戏模式名称。</summary>
        public string GameMode { get; set; } = "Default";

        /// <summary>地图名称。</summary>
        public string MapName { get; set; } = "MainMap";

        /// <summary>最大回合数。</summary>
        public int MaxRounds { get; set; } = 5;

        /// <summary>每回合时间限制 (秒)。</summary>
        public int RoundTimeLimit { get; set; } = 300;

        /// <summary>团队数量。</summary>
        public int TeamCount { get; set; } = 2;

        /// <summary>每队最大玩家数。</summary>
        public int MaxTeamSize { get; set; } = 5;

        /// <summary>是否启用友好伤害。</summary>
        public bool FriendlyFire { get; set; }

        /// <summary>获取自定义值。</summary>
        public T? Get<T>(string key)
        {
            if (_values.TryGetValue(key, out var value) && value is T typed)
                return typed;
            return default;
        }

        /// <summary>设置自定义值。</summary>
        public void Set<T>(string key, T value)
        {
            _values[key] = value!;
        }

        /// <summary>移除自定义值。</summary>
        public bool Remove(string key) => _values.Remove(key);

        /// <summary>检查键是否存在。</summary>
        public bool Contains(string key) => _values.ContainsKey(key);

        /// <summary>获取所有键。</summary>
        public IEnumerable<string> GetAllKeys() => _values.Keys;

        /// <summary>序列化设置到写入器。</summary>
        public void Serialize(NetworkWriter writer)
        {
            writer.WriteString(GameMode);
            writer.WriteString(MapName);
            writer.WriteInt(MaxRounds);
            writer.WriteInt(RoundTimeLimit);
            writer.WriteInt(TeamCount);
            writer.WriteInt(MaxTeamSize);
            writer.WriteBool(FriendlyFire);

            // 自定义值
            var keys = _values.Keys.ToList();
            writer.WriteInt(keys.Count);
            foreach (var key in keys)
            {
                writer.WriteString(key);
                var value = _values[key];
                writer.WriteString(value?.ToString() ?? string.Empty);
            }
        }

        /// <summary>从读取器反序列化设置。</summary>
        public void Deserialize(NetworkReader reader)
        {
            GameMode = reader.ReadString();
            MapName = reader.ReadString();
            MaxRounds = reader.ReadInt();
            RoundTimeLimit = reader.ReadInt();
            TeamCount = reader.ReadInt();
            MaxTeamSize = reader.ReadInt();
            FriendlyFire = reader.ReadBool();

            int customCount = reader.ReadInt();
            for (int i = 0; i < customCount; i++)
            {
                string key = reader.ReadString();
                string value = reader.ReadString();
                _values[key] = value;
            }
        }
    }

    // ──────────────────────────────────────────────────────────────
    // 3. 房间玩家
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 房间玩家信息。
    /// </summary>
    public sealed class RoomPlayer
    {
        /// <summary>玩家唯一 ID。</summary>
        public int PlayerId { get; }

        /// <summary>玩家显示名称。</summary>
        public string DisplayName { get; set; } = "Player";

        /// <summary>关联的网络连接 ID。</summary>
        public int ConnectionId { get; }

        /// <summary>所属团队 (-1 = 未分配)。</summary>
        public int TeamId { get; set; } = -1;

        /// <summary>是否已准备。</summary>
        public bool IsReady { get; set; }

        /// <summary>玩家角色/职业。</summary>
        public string Role { get; set; } = "Default";

        /// <summary>是否是房主。</summary>
        public bool IsHost { get; set; }

        /// <summary>加入时间。</summary>
        public DateTime JoinTime { get; } = DateTime.UtcNow;

        /// <summary>自定义属性。</summary>
        public Dictionary<string, object> CustomProperties { get; } = new Dictionary<string, object>();

        /// <summary>技能评分 (用于匹配)。</summary>
        public float SkillRating { get; set; } = 1000f;

        /// <summary>延迟 (毫秒)。</summary>
        public float Latency { get; set; }

        /// <summary>区域 (用于区域匹配)。</summary>
        public string Region { get; set; } = "Default";

        public RoomPlayer(int playerId, int connectionId)
        {
            PlayerId = playerId;
            ConnectionId = connectionId;
        }
    }

    // ──────────────────────────────────────────────────────────────
    // 4. 聊天消息
    // ──────────────────────────────────────────────────────────────

    /// <summary>房间聊天消息。</summary>
    public sealed class ChatMessage
    {
        /// <summary>消息类型。</summary>
        public ChatMessageType Type { get; }

        /// <summary>发送者玩家 ID (0 = 系统)。</summary>
        public int SenderId { get; }

        /// <summary>发送者名称。</summary>
        public string SenderName { get; }

        /// <summary>接收者玩家 ID (0 = 全体)。</summary>
        public int ReceiverId { get; }

        /// <summary>消息内容。</summary>
        public string Content { get; }

        /// <summary>发送时间。</summary>
        public DateTime Timestamp { get; } = DateTime.UtcNow;

        public ChatMessage(ChatMessageType type, int senderId, string senderName,
            string content, int receiverId = 0)
        {
            Type = type;
            SenderId = senderId;
            SenderName = senderName;
            Content = content;
            ReceiverId = receiverId;
        }
    }

    // ──────────────────────────────────────────────────────────────
    // 5. 房间
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 房间实例。包含 ID、名称、玩家列表、设置、状态等完整房间信息。
    /// </summary>
    public sealed class Room
    {
        private static int _nextRoomId;

        /// <summary>房间唯一 ID。</summary>
        public int RoomId { get; }

        /// <summary>房间名称。</summary>
        public string Name { get; set; } = "New Room";

        /// <summary>房间密码 (空 = 无密码)。</summary>
        public string Password { get; set; } = string.Empty;

        /// <summary>最大玩家数。</summary>
        public int MaxPlayers { get; set; } = 16;

        /// <summary>当前玩家数。</summary>
        public int CurrentPlayerCount => _players.Count;

        /// <summary>当前状态。</summary>
        public RoomState State { get; private set; } = RoomState.Waiting;

        /// <summary>可见性。</summary>
        public RoomVisibility Visibility { get; set; } = RoomVisibility.Public;

        /// <summary>是否锁定。</summary>
        public bool IsLocked { get; set; }

        /// <summary>房间设置。</summary>
        public RoomSettings Settings { get; } = new RoomSettings();

        /// <summary>创建时间。</summary>
        public DateTime CreatedTime { get; } = DateTime.UtcNow;

        /// <summary>房主玩家 ID。</summary>
        public int HostPlayerId => _hostId;

        /// <summary>聊天消息历史。</summary>
        public List<ChatMessage> ChatHistory { get; } = new List<ChatMessage>();

        // ── 内部 ──────────────────────────────────────────────────

        private readonly Dictionary<int, RoomPlayer> _players
            = new Dictionary<int, RoomPlayer>();
        private int _hostId;
        private readonly object _lock = new object();

        // ── 事件 ──────────────────────────────────────────────────

        /// <summary>玩家加入事件。</summary>
        public event Action<Room, RoomPlayer>? OnPlayerJoined;
        /// <summary>玩家离开事件。</summary>
        public event Action<Room, RoomPlayer>? OnPlayerLeft;
        /// <summary>设置变更事件。</summary>
        public event Action<Room>? OnSettingsChanged;
        /// <summary>游戏开始事件。</summary>
        public event Action<Room>? OnGameStarted;
        /// <summary>玩家准备状态变更。</summary>
        public event Action<Room, RoomPlayer>? OnPlayerReadyChanged;
        /// <summary>收到聊天消息。</summary>
        public event Action<Room, ChatMessage>? OnChatMessage;

        public Room()
        {
            RoomId = Interlocked.Increment(ref _nextRoomId);
        }

        public Room(int roomId)
        {
            RoomId = roomId;
            if (roomId >= _nextRoomId)
                Interlocked.CompareExchange(ref _nextRoomId, roomId + 1, _nextRoomId);
        }

        // ── 玩家管理 ────────────────────────────────────────────

        /// <summary>添加玩家到房间。</summary>
        public bool AddPlayer(RoomPlayer player)
        {
            lock (_lock)
            {
                if (State == RoomState.Playing) return false;
                if (_players.Count >= MaxPlayers)
                {
                    State = RoomState.Full;
                    return false;
                }
                if (IsLocked) return false;
                if (!string.IsNullOrEmpty(Password)) return false; // 需单独验证密码

                _players[player.PlayerId] = player;
                if (_players.Count == 1)
                {
                    _hostId = player.PlayerId;
                    player.IsHost = true;
                }

                if (_players.Count >= MaxPlayers)
                    State = RoomState.Full;
                else if (State == RoomState.Full)
                    State = RoomState.Waiting;

                OnPlayerJoined?.Invoke(this, player);
                SendSystemMessage($"{player.DisplayName} 加入了房间");
                return true;
            }
        }

        /// <summary>使用密码加入房间。</summary>
        public bool AddPlayerWithPassword(RoomPlayer player, string password)
        {
            if (Password != password) return false;
            return AddPlayer(player);
        }

        /// <summary>移除玩家。</summary>
        public bool RemovePlayer(int playerId, string reason = "Left")
        {
            lock (_lock)
            {
                if (!_players.TryGetValue(playerId, out var player)) return false;

                _players.Remove(playerId);

                // 如果房主离开, 转移房主
                if (playerId == _hostId && _players.Count > 0)
                {
                    var newHost = _players.Values.First();
                    newHost.IsHost = true;
                    _hostId = newHost.PlayerId;
                    SendSystemMessage($"{newHost.DisplayName} 成为了新房主");
                }

                if (State == RoomState.Full)
                    State = RoomState.Waiting;

                OnPlayerLeft?.Invoke(this, player);
                SendSystemMessage($"{player.DisplayName} 离开了房间 ({reason})");
                return true;
            }
        }

        /// <summary>踢出玩家 (仅房主)。</summary>
        public bool KickPlayer(int requesterId, int targetPlayerId)
        {
            lock (_lock)
            {
                if (requesterId != _hostId) return false;
                return RemovePlayer(targetPlayerId, "Kicked");
            }
        }

        /// <summary>获取所有玩家。</summary>
        public List<RoomPlayer> GetPlayers()
        {
            lock (_lock) { return _players.Values.ToList(); }
        }

        /// <summary>获取指定玩家。</summary>
        public RoomPlayer? GetPlayer(int playerId)
        {
            lock (_lock) { return _players.TryGetValue(playerId, out var p) ? p : null; }
        }

        /// <summary>检查是否所有玩家都已准备。</summary>
        public bool AreAllPlayersReady()
        {
            lock (_lock)
            {
                return _players.Values.All(p => p.IsReady || p.IsHost);
            }
        }

        /// <summary>设置玩家准备状态。</summary>
        public void SetPlayerReady(int playerId, bool ready)
        {
            lock (_lock)
            {
                if (_players.TryGetValue(playerId, out var player))
                {
                    player.IsReady = ready;
                    OnPlayerReadyChanged?.Invoke(this, player);
                }
            }
        }

        // ── 房间操作 ────────────────────────────────────────────

        /// <summary>变更设置。</summary>
        public void ChangeSettings(RoomSettings newSettings)
        {
            Settings.GameMode = newSettings.GameMode;
            Settings.MapName = newSettings.MapName;
            Settings.MaxRounds = newSettings.MaxRounds;
            Settings.RoundTimeLimit = newSettings.RoundTimeLimit;
            Settings.TeamCount = newSettings.TeamCount;
            Settings.MaxTeamSize = newSettings.MaxTeamSize;
            Settings.FriendlyFire = newSettings.FriendlyFire;
            OnSettingsChanged?.Invoke(this);
        }

        /// <summary>锁定房间。</summary>
        public void Lock() { IsLocked = true; }
        /// <summary>解锁房间。</summary>
        public void Unlock() { IsLocked = false; }

        /// <summary>变更密码。</summary>
        public void ChangePassword(string newPassword)
        {
            Password = newPassword;
            if (!string.IsNullOrEmpty(Password))
                Visibility = RoomVisibility.Private;
        }

        /// <summary>开始游戏。</summary>
        public void StartGame()
        {
            if (State != RoomState.Waiting) return;
            State = RoomState.Starting;
            OnGameStarted?.Invoke(this);
        }

        /// <summary>结束游戏。</summary>
        public void EndGame() { State = RoomState.Ending; }

        /// <summary>暂停游戏。</summary>
        public void PauseGame() { State = RoomState.Paused; }

        /// <summary>恢复游戏。</summary>
        public void ResumeGame() { State = RoomState.Playing; }

        // ── 聊天 ──────────────────────────────────────────────────

        /// <summary>发送文本消息。</summary>
        public void SendChatMessage(int senderId, string senderName, string content,
            ChatMessageType type = ChatMessageType.Text, int receiverId = 0)
        {
            var msg = new ChatMessage(type, senderId, senderName, content, receiverId);
            ChatHistory.Add(msg);
            OnChatMessage?.Invoke(this, msg);
        }

        /// <summary>发送系统消息。</summary>
        private void SendSystemMessage(string content)
        {
            SendChatMessage(0, "System", content, ChatMessageType.System);
        }

        /// <summary>获取房间描述。</summary>
        public override string ToString() =>
            $"Room[{RoomId}] \"{Name}\" {State} Players={CurrentPlayerCount}/{MaxPlayers} Vis={Visibility}";
    }

    // ──────────────────────────────────────────────────────────────
    // 6. 房间管理器
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 房间管理器。全局单例, 管理所有房间的创建/加入/离开/搜索/匹配。
    /// </summary>
    public sealed class RoomManager
    {
        private static readonly Lazy<RoomManager> _instance
            = new Lazy<RoomManager>(() => new RoomManager());

        /// <summary>单例。</summary>
        public static RoomManager Instance => _instance.Value;

        private readonly ConcurrentDictionary<int, Room> _rooms
            = new ConcurrentDictionary<int, Room>();
        private readonly ConcurrentDictionary<int, int> _playerToRoom
            = new ConcurrentDictionary<int, int>();

        // ── 统计 ──────────────────────────────────────────────────

        /// <summary>房间总数。</summary>
        public int RoomCount => _rooms.Count;

        /// <summary>事件。</summary>
        public event Action<Room>? OnRoomCreated;
        public event Action<Room>? OnRoomDestroyed;
        public event Action<Room, RoomPlayer>? OnPlayerJoinedRoom;
        public event Action<Room, RoomPlayer>? OnPlayerLeftRoom;

        private RoomManager() { }

        // ── 房间创建 ────────────────────────────────────────────

        /// <summary>创建房间。</summary>
        public Room CreateRoom(string name, int maxPlayers = 16,
            RoomVisibility visibility = RoomVisibility.Public,
            string password = "")
        {
            var room = new Room
            {
                Name = name,
                MaxPlayers = maxPlayers,
                Visibility = visibility,
                Password = password
            };
            _rooms[room.RoomId] = room;
            OnRoomCreated?.Invoke(room);
            return room;
        }

        /// <summary>创建带预设设置的房间。</summary>
        public Room CreateRoom(string name, RoomSettings settings,
            RoomVisibility visibility = RoomVisibility.Public,
            string password = "")
        {
            var room = CreateRoom(name, settings.MaxTeamSize * settings.TeamCount, visibility, password);
            room.ChangeSettings(settings);
            return room;
        }

        // ── 玩家操作 ──────────────────────────────────────────────

        /// <summary>玩家加入房间。</summary>
        public bool JoinRoom(int roomId, RoomPlayer player, string? password = null)
        {
            if (!_rooms.TryGetValue(roomId, out var room)) return false;

            bool joined;
            if (!string.IsNullOrEmpty(password))
                joined = room.AddPlayerWithPassword(player, password);
            else
                joined = room.AddPlayer(player);

            if (joined)
            {
                _playerToRoom[player.PlayerId] = roomId;
                OnPlayerJoinedRoom?.Invoke(room, player);
            }
            return joined;
        }

        /// <summary>玩家离开房间。</summary>
        public bool LeaveRoom(int playerId, string reason = "Left")
        {
            if (!_playerToRoom.TryRemove(playerId, out var roomId)) return false;
            if (!_rooms.TryGetValue(roomId, out var room)) return false;

            bool left = room.RemovePlayer(playerId, reason);
            if (left)
            {
                OnPlayerLeftRoom?.Invoke(room,
                    room.GetPlayer(playerId) ?? new RoomPlayer(playerId, 0));
            }

            // 如果房间空了, 自动关闭
            if (room.CurrentPlayerCount == 0)
            {
                _rooms.TryRemove(roomId, out _);
                OnRoomDestroyed?.Invoke(room);
            }

            return left;
        }

        /// <summary>踢出玩家。</summary>
        public bool KickPlayer(int roomId, int requesterId, int targetPlayerId)
        {
            if (!_rooms.TryGetValue(roomId, out var room)) return false;
            bool kicked = room.KickPlayer(requesterId, targetPlayerId);
            if (kicked) _playerToRoom.TryRemove(targetPlayerId, out _);
            return kicked;
        }

        // ── 房间搜索 ──────────────────────────────────────────────

        /// <summary>搜索房间 (按条件过滤)。</summary>
        public List<Room> SearchRooms(
            string? gameMode = null,
            string? mapName = null,
            RoomVisibility? visibility = null,
            bool? hasSpace = null,
            int? maxPlayers = null)
        {
            var query = _rooms.Values.AsEnumerable();

            if (visibility.HasValue)
                query = query.Where(r => r.Visibility == visibility.Value);

            if (visibility == null)
                query = query.Where(r => r.Visibility != RoomVisibility.Invisible);

            if (hasSpace == true)
                query = query.Where(r => r.CurrentPlayerCount < r.MaxPlayers);

            if (gameMode != null)
                query = query.Where(r => r.Settings.GameMode == gameMode);

            if (mapName != null)
                query = query.Where(r => r.Settings.MapName == mapName);

            if (maxPlayers.HasValue)
                query = query.Where(r => r.MaxPlayers >= maxPlayers.Value);

            return query.ToList();
        }

        /// <summary>获取所有公开房间列表。</summary>
        public List<Room> GetPublicRooms()
        {
            return SearchRooms(visibility: RoomVisibility.Public, hasSpace: true);
        }

        /// <summary>获取玩家所在房间。</summary>
        public Room? GetPlayerRoom(int playerId)
        {
            if (_playerToRoom.TryGetValue(playerId, out var roomId))
                return _rooms.TryGetValue(roomId, out var room) ? room : null;
            return null;
        }

        /// <summary>根据 ID 获取房间。</summary>
        public Room? GetRoom(int roomId)
        {
            return _rooms.TryGetValue(roomId, out var room) ? room : null;
        }

        // ── 房间管理 ────────────────────────────────────────────

        /// <summary>关闭房间。</summary>
        public void CloseRoom(int roomId)
        {
            if (!_rooms.TryRemove(roomId, out var room)) return;

            // 通知所有玩家
            foreach (var player in room.GetPlayers())
                _playerToRoom.TryRemove(player.PlayerId, out _);

            OnRoomDestroyed?.Invoke(room);
        }

        // ── 匹配系统 ──────────────────────────────────────────────

        /// <summary>
        /// 简单匹配: 找到一个有空间的房间。
        /// </summary>
        public Room? QuickMatch(RoomPlayer player, string? gameMode = null)
        {
            var rooms = SearchRooms(gameMode: gameMode, hasSpace: true);
            if (rooms.Count == 0) return null;

            // 优先选择玩家最多的房间
            return rooms.OrderByDescending(r => r.CurrentPlayerCount).First();
        }

        /// <summary>
        /// 技能匹配: 找到与玩家技能评分相近的房间。
        /// </summary>
        public Room? SkillMatch(RoomPlayer player, string? gameMode = null,
            float maxSkillDiff = 200f)
        {
            var rooms = SearchRooms(gameMode: gameMode, hasSpace: true);
            if (rooms.Count == 0) return null;

            return rooms
                .Select(r => new
                {
                    Room = r,
                    AvgSkill = r.GetPlayers().DefaultIfEmpty().Average(p => p.SkillRating),
                    SkillDiff = Math.Abs(
                        r.GetPlayers().DefaultIfEmpty(new RoomPlayer(0, 0) { SkillRating = player.SkillRating })
                            .Average(p => p.SkillRating) - player.SkillRating)
                })
                .Where(x => x.SkillDiff <= maxSkillDiff)
                .OrderBy(x => x.SkillDiff)
                .Select(x => x.Room)
                .FirstOrDefault();
        }

        /// <summary>
        /// 区域匹配: 优先匹配同区域的房间。
        /// </summary>
        public Room? RegionMatch(RoomPlayer player, string? gameMode = null)
        {
            var rooms = SearchRooms(gameMode: gameMode, hasSpace: true);
            if (rooms.Count == 0) return null;

            // 优先同区域
            var sameRegion = rooms.Where(r =>
                r.GetPlayers().Any(p => p.Region == player.Region)).ToList();
            if (sameRegion.Count > 0)
                return sameRegion.OrderByDescending(r => r.CurrentPlayerCount).First();

            return rooms.OrderByDescending(r => r.CurrentPlayerCount).First();
        }

        /// <summary>
        /// 自定义匹配规则。
        /// </summary>
        public Room? CustomMatch(RoomPlayer player, Func<Room, bool> matchRule)
        {
            return _rooms.Values.Where(matchRule).OrderByDescending(r => r.CurrentPlayerCount).FirstOrDefault();
        }

        /// <summary>获取所有房间。</summary>
        public IEnumerable<Room> GetAllRooms() => _rooms.Values;
    }
}
