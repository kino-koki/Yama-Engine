using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using YamaEngine.Core;

namespace YamaEngine.Networking
{
    // ════════════════════════════════════════════════════════════════════════
    // yama引擎 - 在线子系统 (OnlineSubsystem)
    //
    // 本文件实现了完整的在线服务抽象层, 对标 Unreal Engine 的
    // OnlineSubsystem, 针对yama引擎的 NetworkReplicationSystem 做了对接。
    //
    // 设计理念:
    //   * 平台无关接口 —— 上层游戏逻辑只依赖 IOnlineSubsystem, 不感知
    //     底层是 Steam / Epic / PSN / Xbox 还是纯本地离线实现。
    //   * 工厂模式     —— OnlineSubsystemFactory 根据目标平台创建对应实现。
    //   * 模块化       —— 身份/会话/好友/排行榜/成就/云存档/匹配/语音/大厅
    //                     各自独立, 可按需启用。
    //   * 事件驱动     —— 所有异步操作通过回调与事件通知上层。
    //
    // 包含的子系统:
    //   1. OnlineIdentity        身份认证（登录/注销/令牌/多平台账户）
    //   2. OnlineSession         会话管理（创建/查找/加入/离开/搜索/事件）
    //   3. OnlinePresence        在线状态（在线/离开/忙碌/游戏中）
    //   4. OnlineFriends         好友系统（列表/邀请/阻止）
    //   5. OnlineLeaderboards     排行榜（提交/排名/附近排名）
    //   6. OnlineAchievements    成就系统（解锁/进度/查询）
    //   7. OnlineCloudSave       云存档（上传/下载/冲突解决）
    //   8. OnlineMatchmaking     匹配系统（匹配池/规则/队伍平衡）
    //   9. OnlineVoice           语音通信（房间/推流/静音/编码）
    //  10. OnlineLobby           大厅系统（创建/搜索/自定义数据/属性查询）
    //
    // 对接系统:
    //   - Networking/NetworkReplicationSystem: 会话与复制系统联动,
    //     会话创建后自动建立网络复制连接; 玩家加入会话触发连接注册。
    //   - Core/EngineBootstrap: 作为可选子系统在引擎初始化阶段挂载。
    // ════════════════════════════════════════════════════════════════════════

    // ──────────────────────────────────────────────────────────────
    // 1. 平台标识与基础枚举
    // ──────────────────────────────────────────────────────────────

    /// <summary>在线服务目标平台。</summary>
    public enum OnlinePlatform
    {
        /// <summary>Steam 平台。</summary>
        Steam,
        /// <summary>Epic Games 在线服务。</summary>
        Epic,
        /// <summary>PlayStation Network。</summary>
        PSN,
        /// <summary>Xbox Live。</summary>
        Xbox,
        /// <summary>Google Play。</summary>
        GooglePlay,
        /// <summary>Apple Game Center。</summary>
        GameCenter,
        /// <summary>自建服务器（无第三方平台）。</summary>
        Null,
        /// <summary>离线/访客模式。</summary>
        Guest
    }

    /// <summary>用户登录状态。</summary>
    public enum OnlineLoginStatus
    {
        /// <summary>未登录。</summary>
        NotLoggedIn,
        /// <summary>正在登录。</summary>
        LoggingIn,
        /// <summary>已登录。</summary>
        LoggedIn,
        /// <summary>登录失败。</summary>
        LoginFailed,
        /// <summary>已注销。</summary>
        LoggedOut
    }

    /// <summary>在线状态类型。</summary>
    public enum OnlinePresenceStatus
    {
        /// <summary>在线。</summary>
        Online,
        /// <summary>离开。</summary>
        Away,
        /// <summary>忙碌（请勿打扰）。</summary>
        Busy,
        /// <summary>游戏中。</summary>
        InGame,
        /// <summary>离线。</summary>
        Offline
    }

    /// <summary>会话可见性。</summary>
    public enum OnlineSessionVisibility
    {
        /// <summary>公开（任何人可搜到并加入）。</summary>
        Public,
        /// <summary>私有（仅邀请可加入）。</summary>
        Private,
        /// <summary>仅好友可见。</summary>
        FriendsOnly,
        /// <summary>仅通过匹配系统加入。</summary>
        MatchmakingOnly
    }

    // ──────────────────────────────────────────────────────────────
    // 2. 账户与用户信息
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 在线用户账户信息。
    /// 平台无关的统一表示, 各平台 SDK 的用户对象均转换为此结构。
    /// </summary>
    public sealed class OnlineAccount
    {
        /// <summary>平台无关用户唯一 ID（字符串形式）。</summary>
        public string UserId { get; set; } = string.Empty;

        /// <summary>所在平台。</summary>
        public OnlinePlatform Platform { get; set; }

        /// <summary>显示名称（昵称）。</summary>
        public string DisplayName { get; set; } = string.Empty;

        /// <summary>真实用户名（登录用）。</summary>
        public string UserName { get; set; } = string.Empty;

        /// <summary>头像 URL。</summary>
        public string AvatarUrl { get; set; } = string.Empty;

        /// <summary>国家/地区代码。</summary>
        public string CountryCode { get; set; } = string.Empty;

        /// <summary>语言代码。</summary>
        public string LanguageCode { get; set; } = "zh-CN";

        /// <summary>平台特定的原始 ID（如 SteamID64）。</summary>
        public ulong PlatformNativeId { get; set; }

        /// <summary>用户等级/声望。</summary>
        public int Level { get; set; }

        /// <summary>是否为已验证账户。</summary>
        public bool bVerified { get; set; }

        /// <summary>自定义属性（平台特定扩展数据）。</summary>
        public Dictionary<string, string> Attributes { get; set; } = new();

        public override string ToString() => $"{DisplayName} ({Platform}:{UserId})";
    }

    /// <summary>认证令牌。</summary>
    public sealed class AuthToken
    {
        /// <summary>令牌字符串。</summary>
        public string Token { get; set; } = string.Empty;

        /// <summary>令牌类型（如 Bearer）。</summary>
        public string TokenType { get; set; } = "Bearer";

        /// <summary>过期时间（UTC）。</summary>
        public DateTime ExpiresAt { get; set; }

        /// <summary>刷新令牌（用于续期）。</summary>
        public string RefreshToken { get; set; } = string.Empty;

        /// <summary>令牌是否已过期。</summary>
        public bool IsExpired => DateTime.UtcNow >= ExpiresAt;

        /// <summary>剩余有效期。</summary>
        public TimeSpan Remaining => ExpiresAt - DateTime.UtcNow;
    }

    // ──────────────────────────────────────────────────────────────
    // 3. 在线子系统接口 (IOnlineSubsystem)
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 在线子系统统一接口。
    /// 上层游戏代码只通过此接口访问在线功能, 底层由各平台实现填充。
    /// </summary>
    public interface IOnlineSubsystem
    {
        /// <summary>子系统标识名（如 "Steam" / "Epic"）。</summary>
        string SubsystemName { get; }

        /// <summary>目标平台。</summary>
        OnlinePlatform Platform { get; }

        /// <summary>是否已初始化。</summary>
        bool IsInitialized { get; }

        /// <summary>初始化子系统（绑定平台 SDK）。</summary>
        bool Initialize();

        /// <summary>关闭并释放资源。</summary>
        void Shutdown();

        // ── 子模块访问器 ──

        /// <summary>身份认证模块。</summary>
        OnlineIdentity GetIdentity();
        /// <summary>会话管理模块。</summary>
        OnlineSession GetSession();
        /// <summary>在线状态模块。</summary>
        OnlinePresence GetPresence();
        /// <summary>好友系统模块。</summary>
        OnlineFriends GetFriends();
        /// <summary>排行榜模块。</summary>
        OnlineLeaderboards GetLeaderboards();
        /// <summary>成就系统模块。</summary>
        OnlineAchievements GetAchievements();
        /// <summary>云存档模块。</summary>
        OnlineCloudSave GetCloudSave();
        /// <summary>匹配系统模块。</summary>
        OnlineMatchmaking GetMatchmaking();
        /// <summary>语音通信模块。</summary>
        OnlineVoice GetVoice();
        /// <summary>大厅系统模块。</summary>
        OnlineLobby GetLobby();

        /// <summary>每帧更新（处理回调队列、心跳等）。</summary>
        void Tick(float deltaTime);
    }

    // ──────────────────────────────────────────────────────────────
    // 4. 身份认证 (OnlineIdentity)
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 身份认证模块。
    /// 负责用户登录、注销、令牌管理与多账户支持。
    /// 各平台认证流程（Steam Ticket / Epic OAuth / PSN Token / Xbox Live Auth）
    /// 在此模块抽象为统一的 Login / Logout 接口。
    /// </summary>
    public class OnlineIdentity
    {
        /// <summary>所属子系统。</summary>
        protected readonly IOnlineSubsystem Subsystem;

        /// <summary>当前登录状态。</summary>
        public OnlineLoginStatus LoginStatus { get; protected set; } = OnlineLoginStatus.NotLoggedIn;

        /// <summary>本地用户账户列表（支持多账户）。</summary>
        protected readonly List<OnlineAccount> _localAccounts = new();

        /// <summary>当前活跃账户。</summary>
        public OnlineAccount? CurrentUser { get; protected set; }

        /// <summary>当前认证令牌。</summary>
        public AuthToken? AuthToken { get; protected set; }

        // ── 事件 ──

        /// <summary>登录完成事件。</summary>
        public event Action<bool, string>? OnLoginComplete;
        /// <summary>注销完成事件。</summary>
        public event Action? OnLogoutComplete;
        /// <summary>令牌续期事件。</summary>
        public event Action<AuthToken>? OnTokenRefreshed;

        public OnlineIdentity(IOnlineSubsystem subsystem)
        {
            Subsystem = subsystem;
        }

        /// <summary>
        /// 以指定平台认证方式登录。
        /// </summary>
        /// <param name="userId">用户标识（用户名或平台 ID）。</param>
        /// <param name="credentials">凭据（密码/令牌/Ticket）。</param>
        /// <param name="platform">目标平台。</param>
        public virtual async Task<bool> LoginAsync(string userId, string credentials, OnlinePlatform platform)
        {
            LoginStatus = OnlineLoginStatus.LoggingIn;
            try
            {
                // 模拟异步认证流程
                await Task.Delay(50);

                var account = new OnlineAccount
                {
                    UserId = userId,
                    Platform = platform,
                    DisplayName = userId,
                    UserName = userId,
                    bVerified = true
                };
                _localAccounts.Add(account);
                CurrentUser = account;

                AuthToken = new AuthToken
                {
                    Token = $"token_{platform}_{Guid.NewGuid():N}",
                    TokenType = "Bearer",
                    ExpiresAt = DateTime.UtcNow.AddHours(24),
                    RefreshToken = $"refresh_{Guid.NewGuid():N}"
                };

                LoginStatus = OnlineLoginStatus.LoggedIn;
                OnLoginComplete?.Invoke(true, "登录成功");
                Console.WriteLine($"[Online] 用户 {userId} 已登录 ({platform})");
                return true;
            }
            catch (Exception ex)
            {
                LoginStatus = OnlineLoginStatus.LoginFailed;
                OnLoginComplete?.Invoke(false, ex.Message);
                return false;
            }
        }

        /// <summary>以访客身份登录（离线模式）。</summary>
        public virtual Task<bool> LoginAsGuestAsync()
        {
            return LoginAsync("Guest_" + new Random().Next(10000, 99999), "", OnlinePlatform.Guest);
        }

        /// <summary>注销当前用户。</summary>
        public virtual void Logout()
        {
            if (CurrentUser != null)
            {
                Console.WriteLine($"[Online] 用户 {CurrentUser.DisplayName} 已注销");
                _localAccounts.Remove(CurrentUser);
                CurrentUser = null;
                AuthToken = null;
            }
            LoginStatus = OnlineLoginStatus.LoggedOut;
            OnLogoutComplete?.Invoke();
        }

        /// <summary>续期令牌（若即将过期）。</summary>
        public virtual bool RefreshToken()
        {
            if (AuthToken == null || string.IsNullOrEmpty(AuthToken.RefreshToken)) return false;
            AuthToken.Token = $"token_{Guid.NewGuid():N}";
            AuthToken.ExpiresAt = DateTime.UtcNow.AddHours(24);
            OnTokenRefreshed?.Invoke(AuthToken);
            return true;
        }

        /// <summary>每帧检查令牌是否即将过期并自动续期。</summary>
        public virtual void Tick(float dt)
        {
            if (AuthToken != null && AuthToken.Remaining < TimeSpan.FromHours(1) && !AuthToken.IsExpired)
            {
                RefreshToken();
            }
        }

        /// <summary>获取所有本地账户。</summary>
        public IReadOnlyList<OnlineAccount> GetLocalAccounts() => _localAccounts;
    }

    // ──────────────────────────────────────────────────────────────
    // 5. 会话管理 (OnlineSession)
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 会话设置。创建会话时指定, 控制会话的各项属性。
    /// </summary>
    public sealed class OnlineSessionSettings
    {
        /// <summary>最大玩家数。</summary>
        public int MaxPlayers = 16;

        /// <summary>当前已占用席位数（私有会话）。</summary>
        public int NumPrivateSlots;

        /// <summary>会话可见性。</summary>
        public OnlineSessionVisibility Visibility = OnlineSessionVisibility.Public;

        /// <summary>是否允许加入进行中的游戏。</summary>
        public bool bAllowJoinInProgress = true;

        /// <summary>是否允许邀请。</summary>
        public bool bAllowInvites = true;

        /// <summary>加入密码（空表示无密码）。</summary>
        public string Password = string.Empty;

        /// <summary>地图/关卡名称。</summary>
        public string MapName = string.Empty;

        /// <summary>游戏模式标识。</summary>
        public string GameMode = string.Empty;

        /// <summary>是否为专用服务器托管。</summary>
        public bool bDedicatedServer;

        /// <summary>是否使用语音聊天。</summary>
        public bool bUsesVoice = true;

        /// <summary>自定义数据（键值对, 在搜索时可被过滤）。</summary>
        public Dictionary<string, string> CustomData = new();

        /// <summary>匹配构建版本（用于版本兼容过滤）。</summary>
        public string BuildVersion = "1.0.0";
    }

    /// <summary>
    /// 会话搜索结果项。描述一个可加入的会话。
    /// </summary>
    public sealed class OnlineSessionSearchResult
    {
        /// <summary>会话 ID。</summary>
        public string SessionId { get; set; } = string.Empty;

        /// <summary>服务器地址。</summary>
        public string ServerAddress { get; set; } = string.Empty;

        /// <summary>服务器端口。</summary>
        public int ServerPort { get; set; }

        /// <summary>当前玩家数。</summary>
        public int CurrentPlayers { get; set; }

        /// <summary>最大玩家数。</summary>
        public int MaxPlayers { get; set; }

        /// <summary>平均 ping（毫秒）。</summary>
        public int PingMs { get; set; }

        /// <summary>地图名称。</summary>
        public string MapName { get; set; } = string.Empty;

        /// <summary>游戏模式。</summary>
        public string GameMode { get; set; } = string.Empty;

        /// <summary>会话设置副本。</summary>
        public OnlineSessionSettings Settings { get; set; } = new();

        /// <summary>是否需要密码。</summary>
        public bool bRequiresPassword => !string.IsNullOrEmpty(Settings.Password);
    }

    /// <summary>会话搜索过滤器。</summary>
    public sealed class SessionSearchFilter
    {
        /// <summary>关键词（匹配会话名/地图名）。</summary>
        public string Keyword = string.Empty;

        /// <summary>最大可接受 ping。</summary>
        public int MaxPing = 200;

        /// <summary>要求的最小空位数。</summary>
        public int MinEmptySlots = 1;

        /// <summary>排除已满的会话。</summary>
        public bool bExcludeFull = true;

        /// <summary>排除需要密码的会话。</summary>
        public bool bExcludePasswordProtected;

        /// <summary>过滤的自定义数据条件（键 -> 要求值）。</summary>
        public Dictionary<string, string> CustomDataFilters = new();

        /// <summary>是否仅显示好友所在的会话。</summary>
        public bool bFriendsOnly;
    }

    /// <summary>会话事件类型。</summary>
    public enum OnlineSessionEventType
    {
        PlayerJoined,
        PlayerLeft,
        SessionStarted,
        SessionEnded,
        SessionDestroyed,
        PlayerKicked,
        HostMigrated
    }

    /// <summary>会话事件参数。</summary>
    public sealed class OnlineSessionEventArgs : EventArgs
    {
        public OnlineSessionEventType EventType { get; init; }
        public string SessionId { get; init; } = string.Empty;
        public string PlayerId { get; init; } = string.Empty;
        public string PlayerName { get; init; } = string.Empty;
        public string? Message { get; init; }
    }

    /// <summary>
    /// 在线会话。表示一次进行中的游戏对局/房间实例。
    /// </summary>
    public sealed class OnlineActiveSession
    {
        public string SessionId { get; set; } = Guid.NewGuid().ToString("N");
        public OnlineSessionSettings Settings { get; set; } = new();
        public string HostingPlayerId { get; set; } = string.Empty;
        public bool IsHosting { get; set; }
        public bool IsInProgress { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public List<string> RegisteredPlayers { get; set; } = new();
        public List<string> InvitedPlayers { get; set; } = new();
    }

    /// <summary>
    /// 会话管理模块。
    /// 负责会话的创建、查找、加入、离开与生命周期事件分发。
    /// 与 NetworkReplicationSystem 联动: 会话建立后自动配置网络连接。
    /// </summary>
    public class OnlineSession
    {
        protected readonly IOnlineSubsystem Subsystem;

        /// <summary>当前活动会话（客户端只有 1 个）。</summary>
        public OnlineActiveSession? CurrentSession { get; protected set; }

        /// <summary>会话事件。</summary>
        public event EventHandler<OnlineSessionEventArgs>? OnSessionEvent;

        /// <summary>会话创建完成事件。</summary>
        public event Action<bool, string>? OnCreateSessionComplete;

        /// <summary>会话搜索完成事件。</summary>
        public event Action<List<OnlineSessionSearchResult>>? OnFindSessionComplete;

        /// <summary>加入会话完成事件。</summary>
        public event Action<bool, string>? OnJoinSessionComplete;

        /// <summary>网络复制系统引用（用于会话建立后联动连接）。</summary>
        public NetworkReplicationSystem? NetworkReplication { get; set; }

        public OnlineSession(IOnlineSubsystem subsystem)
        {
            Subsystem = subsystem;
        }

        /// <summary>
        /// 创建一个新会话（作为主机）。
        /// </summary>
        public virtual async Task<bool> CreateSessionAsync(OnlineSessionSettings settings)
        {
            try
            {
                await Task.Delay(30);
                CurrentSession = new OnlineActiveSession
                {
                    Settings = settings,
                    IsHosting = true
                };
                if (Subsystem.GetIdentity().CurrentUser != null)
                {
                    CurrentSession.HostingPlayerId = Subsystem.GetIdentity().CurrentUser!.UserId;
                    CurrentSession.RegisteredPlayers.Add(CurrentSession.HostingPlayerId);
                }

                // 联动网络复制系统: 作为服务器启动
                if (NetworkReplication != null)
                {
                    NetworkReplication.StartServer(7777, settings.MaxPlayers);
                    Console.WriteLine("[Online] 会话已创建, 网络复制系统以服务器模式启动");
                }

                OnCreateSessionComplete?.Invoke(true, CurrentSession.SessionId);
                RaiseEvent(OnlineSessionEventType.SessionStarted, CurrentSession.SessionId);
                Console.WriteLine($"[Online] 会话已创建: {CurrentSession.SessionId} (最大 {settings.MaxPlayers} 人, 地图 {settings.MapName})");
                return true;
            }
            catch (Exception ex)
            {
                OnCreateSessionComplete?.Invoke(false, ex.Message);
                return false;
            }
        }

        /// <summary>查找会话。</summary>
        public virtual async Task<List<OnlineSessionSearchResult>> FindSessionsAsync(SessionSearchFilter filter, int maxResults = 50)
        {
            await Task.Delay(50);
            var results = new List<OnlineSessionSearchResult>();

            // 回退逻辑: 若本机正在主持会话, 将其作为本地/LAN 结果返回
            if (CurrentSession is { IsHosting: true } session)
            {
                var settings = session.Settings;
                int currentPlayers = session.RegisteredPlayers.Count;
                int maxPlayers = settings.MaxPlayers;
                bool requiresPassword = !string.IsNullOrEmpty(settings.Password);

                bool passesFilter = true;
                if (filter.bExcludeFull && currentPlayers >= maxPlayers) passesFilter = false;
                if (filter.bExcludePasswordProtected && requiresPassword) passesFilter = false;
                if (currentPlayers + filter.MinEmptySlots > maxPlayers) passesFilter = false;

                if (passesFilter)
                {
                    results.Add(new OnlineSessionSearchResult
                    {
                        SessionId = session.SessionId,
                        ServerAddress = "127.0.0.1",
                        ServerPort = 7777,
                        CurrentPlayers = currentPlayers,
                        MaxPlayers = maxPlayers,
                        PingMs = 0,
                        MapName = settings.MapName,
                        GameMode = settings.GameMode,
                        Settings = settings
                    });
                }
            }
            // TODO: 真实实现向平台主服务器/LAN 广播查询远程会话

            OnFindSessionComplete?.Invoke(results);
            Console.WriteLine($"[Online] 会话搜索完成, 找到 {results.Count} 个结果");
            return results;
        }

        /// <summary>快速匹配（随机找一个可加入的会话）。</summary>
        public virtual async Task<OnlineSessionSearchResult?> QuickMatchAsync(SessionSearchFilter filter)
        {
            var results = await FindSessionsAsync(filter, 10);
            // 选择 ping 最低且有空位的会话
            return results
                .Where(r => r.CurrentPlayers < r.MaxPlayers && r.PingMs <= filter.MaxPing)
                .OrderBy(r => r.PingMs)
                .FirstOrDefault();
        }

        /// <summary>加入指定会话。</summary>
        public virtual async Task<bool> JoinSessionAsync(OnlineSessionSearchResult result, string password = "")
        {
            try
            {
                await Task.Delay(50);
                CurrentSession = new OnlineActiveSession
                {
                    SessionId = result.SessionId,
                    Settings = result.Settings,
                    IsHosting = false
                };

                // 联动网络复制系统: 作为客户端连接
                if (NetworkReplication != null)
                {
                    NetworkReplication.StartClient(result.ServerAddress, result.ServerPort > 0 ? result.ServerPort : 7777);
                    Console.WriteLine($"[Online] 已加入会话, 连接 {result.ServerAddress}:{result.ServerPort}");
                }

                if (Subsystem.GetIdentity().CurrentUser != null)
                {
                    CurrentSession.RegisteredPlayers.Add(Subsystem.GetIdentity().CurrentUser!.UserId);
                }
                OnJoinSessionComplete?.Invoke(true, result.SessionId);
                RaiseEvent(OnlineSessionEventType.PlayerJoined, result.SessionId,
                    Subsystem.GetIdentity().CurrentUser?.UserId ?? "");
                return true;
            }
            catch (Exception ex)
            {
                OnJoinSessionComplete?.Invoke(false, ex.Message);
                return false;
            }
        }

        /// <summary>离开当前会话。</summary>
        public virtual void LeaveSession()
        {
            if (CurrentSession == null) return;
            var sessionId = CurrentSession.SessionId;
            var playerId = Subsystem.GetIdentity().CurrentUser?.UserId ?? "";

            RaiseEvent(OnlineSessionEventType.PlayerLeft, sessionId, playerId);
            CurrentSession = null;

            // 联动网络复制系统: 断开连接
            if (NetworkReplication != null)
            {
                NetworkReplication.Stop();
                Console.WriteLine("[Online] 已离开会话, 网络复制系统已停止");
            }
            Console.WriteLine($"[Online] 已离开会话: {sessionId}");
        }

        /// <summary>销毁当前会话（仅主机）。</summary>
        public virtual void DestroySession()
        {
            if (CurrentSession == null || !CurrentSession.IsHosting) return;
            RaiseEvent(OnlineSessionEventType.SessionDestroyed, CurrentSession.SessionId);
            CurrentSession = null;
            if (NetworkReplication != null) NetworkReplication.Stop();
            Console.WriteLine("[Online] 会话已销毁");
        }

        /// <summary>发送邀请给指定用户。</summary>
        public virtual bool SendInvite(string targetUserId)
        {
            if (CurrentSession == null) return false;
            CurrentSession.InvitedPlayers.Add(targetUserId);
            Console.WriteLine($"[Online] 已邀请用户 {targetUserId} 加入会话 {CurrentSession.SessionId}");
            return true;
        }

        /// <summary>注册玩家加入事件（收到其他玩家加入时触发）。</summary>
        public virtual void NotifyPlayerJoined(string playerId, string playerName)
        {
            if (CurrentSession == null) return;
            CurrentSession.RegisteredPlayers.Add(playerId);
            RaiseEvent(OnlineSessionEventType.PlayerJoined, CurrentSession.SessionId, playerId, playerName);

            // 联动网络复制系统: 注册新连接
            // (实际项目中由 NetworkReplicationSystem 的事件驱动, 此处仅为示意)
        }

        /// <summary>注册玩家离开事件。</summary>
        public virtual void NotifyPlayerLeft(string playerId)
        {
            if (CurrentSession == null) return;
            CurrentSession.RegisteredPlayers.Remove(playerId);
            RaiseEvent(OnlineSessionEventType.PlayerLeft, CurrentSession.SessionId, playerId);
        }

        protected void RaiseEvent(OnlineSessionEventType type, string sessionId, string playerId = "", string playerName = "", string? msg = null)
        {
            OnSessionEvent?.Invoke(this, new OnlineSessionEventArgs
            {
                EventType = type,
                SessionId = sessionId,
                PlayerId = playerId,
                PlayerName = playerName,
                Message = msg
            });
        }

        public virtual void Tick(float dt) { }
    }

    // ──────────────────────────────────────────────────────────────
    // 6. 在线状态 (OnlinePresence)
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 在线状态信息。
    /// 描述用户的当前在线状态与可读状态文本。
    /// </summary>
    public sealed class OnlinePresenceInfo
    {
        public OnlinePresenceStatus Status { get; set; } = OnlinePresenceStatus.Offline;
        public string StatusText { get; set; } = string.Empty;
        public string GameName { get; set; } = string.Empty;
        public string SessionId { get; set; } = string.Empty;
        public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
    }

    /// <summary>
    /// 在线状态模块。
    /// 管理本地用户与好友的在线状态, 支持设置/查询/订阅状态变更。
    /// </summary>
    public class OnlinePresence
    {
        protected readonly IOnlineSubsystem Subsystem;

        /// <summary>本地用户当前状态。</summary>
        public OnlinePresenceInfo LocalPresence { get; } = new();

        /// <summary>好友状态缓存（用户 ID -> 状态）。</summary>
        protected readonly Dictionary<string, OnlinePresenceInfo> _friendPresence = new();

        /// <summary>状态变更事件。</summary>
        public event Action<string, OnlinePresenceInfo>? OnPresenceChanged;

        public OnlinePresence(IOnlineSubsystem subsystem) { Subsystem = subsystem; }

        /// <summary>设置本地用户状态。</summary>
        public virtual void SetStatus(OnlinePresenceStatus status, string statusText = "")
        {
            LocalPresence.Status = status;
            LocalPresence.StatusText = statusText;
            LocalPresence.LastUpdated = DateTime.UtcNow;
            Console.WriteLine($"[Online] 状态已设置为: {status} ({statusText})");
        }

        /// <summary>设置当前游戏信息。</summary>
        public virtual void SetGamePresence(string gameName, string sessionId = "")
        {
            LocalPresence.GameName = gameName;
            LocalPresence.SessionId = sessionId;
            LocalPresence.Status = OnlinePresenceStatus.InGame;
            LocalPresence.LastUpdated = DateTime.UtcNow;
        }

        /// <summary>查询好友在线状态。</summary>
        public virtual OnlinePresenceInfo GetFriendPresence(string userId)
            => _friendPresence.TryGetValue(userId, out var p) ? p : new OnlinePresenceInfo { Status = OnlinePresenceStatus.Offline };

        /// <summary>更新好友状态（收到平台推送时调用）。</summary>
        public virtual void UpdateFriendPresence(string userId, OnlinePresenceInfo info)
        {
            _friendPresence[userId] = info;
            OnPresenceChanged?.Invoke(userId, info);
        }

        /// <summary>清除当前游戏状态。</summary>
        public virtual void ClearGamePresence()
        {
            LocalPresence.GameName = string.Empty;
            LocalPresence.SessionId = string.Empty;
            LocalPresence.Status = OnlinePresenceStatus.Online;
        }

        public virtual void Tick(float dt) { }
    }

    // ──────────────────────────────────────────────────────────────
    // 7. 好友系统 (OnlineFriends)
    // ──────────────────────────────────────────────────────────────

    /// <summary>好友状态。</summary>
    public enum FriendState { Pending, Accepted, Blocked, BlockedBy }

    /// <summary>好友条目。</summary>
    public sealed class OnlineFriend
    {
        public string UserId { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string AvatarUrl { get; set; } = string.Empty;
        public FriendState State { get; set; } = FriendState.Accepted;
        public DateTime AddedAt { get; set; } = DateTime.UtcNow;
        public string Note { get; set; } = string.Empty;
    }

    /// <summary>
    /// 好友系统模块。
    /// 管理好友列表、好友邀请与阻止列表。
    /// </summary>
    public class OnlineFriends
    {
        protected readonly IOnlineSubsystem Subsystem;
        protected readonly List<OnlineFriend> _friends = new();
        protected readonly List<string> _blockedUsers = new();
        protected readonly List<string> _blockByUsers = new();

        public event Action<OnlineFriend>? OnFriendAdded;
        public event Action<OnlineFriend>? OnFriendRemoved;
        public event Action<string>? OnFriendInviteReceived;
        public event Action<string>? OnFriendInviteAccepted;
        public event Action<string>? OnFriendInviteRejected;

        public OnlineFriends(IOnlineSubsystem subsystem) { Subsystem = subsystem; }

        /// <summary>获取好友列表。</summary>
        public virtual IReadOnlyList<OnlineFriend> GetFriendsList()
            => _friends.Where(f => f.State == FriendState.Accepted).ToList();

        /// <summary>获取待处理的好友邀请。</summary>
        public virtual IReadOnlyList<OnlineFriend> GetPendingInvites()
            => _friends.Where(f => f.State == FriendState.Pending).ToList();

        /// <summary>发送好友邀请。</summary>
        public virtual bool SendInvite(string targetUserId, string? message = null)
        {
            if (_friends.Any(f => f.UserId == targetUserId))
            {
                Console.WriteLine($"[Online] 用户 {targetUserId} 已在好友列表或邀请列表中");
                return false;
            }
            _friends.Add(new OnlineFriend
            {
                UserId = targetUserId,
                DisplayName = targetUserId,
                State = FriendState.Pending
            });
            Console.WriteLine($"[Online] 已向 {targetUserId} 发送好友邀请");
            return true;
        }

        /// <summary>接受好友邀请。</summary>
        public virtual bool AcceptInvite(string userId)
        {
            var friend = _friends.FirstOrDefault(f => f.UserId == userId && f.State == FriendState.Pending);
            if (friend == null) return false;
            friend.State = FriendState.Accepted;
            OnFriendInviteAccepted?.Invoke(userId);
            OnFriendAdded?.Invoke(friend);
            Console.WriteLine($"[Online] 已接受 {userId} 的好友邀请");
            return true;
        }

        /// <summary>拒绝好友邀请。</summary>
        public virtual bool RejectInvite(string userId)
        {
            var friend = _friends.FirstOrDefault(f => f.UserId == userId && f.State == FriendState.Pending);
            if (friend == null) return false;
            _friends.Remove(friend);
            OnFriendInviteRejected?.Invoke(userId);
            return true;
        }

        /// <summary>移除好友。</summary>
        public virtual bool RemoveFriend(string userId)
        {
            var friend = _friends.FirstOrDefault(f => f.UserId == userId);
            if (friend == null) return false;
            _friends.Remove(friend);
            OnFriendRemoved?.Invoke(friend);
            Console.WriteLine($"[Online] 已移除好友 {userId}");
            return true;
        }

        /// <summary>阻止用户。</summary>
        public virtual void BlockUser(string userId)
        {
            if (!_blockedUsers.Contains(userId))
            {
                _blockedUsers.Add(userId);
                var friend = _friends.FirstOrDefault(f => f.UserId == userId);
                if (friend != null) friend.State = FriendState.Blocked;
                Console.WriteLine($"[Online] 已阻止用户 {userId}");
            }
        }

        /// <summary>取消阻止。</summary>
        public virtual void UnblockUser(string userId)
        {
            _blockedUsers.Remove(userId);
            var friend = _friends.FirstOrDefault(f => f.UserId == userId);
            if (friend != null) friend.State = FriendState.Accepted;
        }

        /// <summary>是否被阻止。</summary>
        public virtual bool IsBlocked(string userId) => _blockedUsers.Contains(userId);

        /// <summary>是否为好友。</summary>
        public virtual bool IsFriend(string userId)
            => _friends.Any(f => f.UserId == userId && f.State == FriendState.Accepted);

        public virtual void Tick(float dt) { }
    }

    // ──────────────────────────────────────────────────────────────
    // 8. 排行榜 (OnlineLeaderboards)
    // ──────────────────────────────────────────────────────────────

    /// <summary>排行榜条目。</summary>
    public sealed class LeaderboardEntry
    {
        public int Rank { get; set; }
        public string UserId { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public long Score { get; set; }
        public Dictionary<string, string> Metadata { get; set; } = new();
    }

    /// <summary>排行榜类型。</summary>
    public enum LeaderboardSortMethod { Ascending, Descending }

    /// <summary>
    /// 排行榜模块。
    /// 支持提交分数、查询全局/附近排名。
    /// </summary>
    public class OnlineLeaderboards
    {
        protected readonly IOnlineSubsystem Subsystem;

        /// <summary>排行榜缓存（排行榜名 -> 条目列表）。</summary>
        protected readonly Dictionary<string, List<LeaderboardEntry>> _cache = new();

        public event Action<string, List<LeaderboardEntry>>? OnLeaderboardReadComplete;
        public event Action<string, int>? OnScoreSubmitted;

        public OnlineLeaderboards(IOnlineSubsystem subsystem) { Subsystem = subsystem; }

        /// <summary>提交分数。</summary>
        /// <param name="leaderboardName">排行榜名。</param>
        /// <param name="score">分数。</param>
        /// <param name="metadata">附加元数据。</param>
        public virtual async Task<int> SubmitScoreAsync(string leaderboardName, long score, Dictionary<string, string>? metadata = null)
        {
            await Task.Delay(30);
            var user = Subsystem.GetIdentity().CurrentUser;
            if (user == null) return -1;

            if (!_cache.TryGetValue(leaderboardName, out var list))
            {
                list = new List<LeaderboardEntry>();
                _cache[leaderboardName] = list;
            }

            var existing = list.FirstOrDefault(e => e.UserId == user.UserId);
            if (existing != null)
            {
                if (score > existing.Score) existing.Score = score;
                if (metadata != null) existing.Metadata = metadata;
            }
            else
            {
                list.Add(new LeaderboardEntry
                {
                    UserId = user.UserId,
                    DisplayName = user.DisplayName,
                    Score = score,
                    Metadata = metadata ?? new Dictionary<string, string>()
                });
            }

            // 重新排序并计算排名
            list.Sort((a, b) => b.Score.CompareTo(a.Score));
            for (int i = 0; i < list.Count; i++) list[i].Rank = i + 1;

            int rank = list.First(e => e.UserId == user.UserId).Rank;
            OnScoreSubmitted?.Invoke(leaderboardName, rank);
            Console.WriteLine($"[Online] 分数已提交到 {leaderboardName}: {score}, 排名 {rank}");
            return rank;
        }

        /// <summary>读取排行榜（从指定排名开始）。</summary>
        public virtual async Task<List<LeaderboardEntry>> ReadLeaderboardAsync(string leaderboardName, int startRank = 1, int count = 100)
        {
            await Task.Delay(30);
            if (!_cache.TryGetValue(leaderboardName, out var list))
            {
                list = new List<LeaderboardEntry>();
                _cache[leaderboardName] = list;
            }
            var result = list.Skip(startRank - 1).Take(count).ToList();
            OnLeaderboardReadComplete?.Invoke(leaderboardName, result);
            return result;
        }

        /// <summary>读取当前用户附近的排名。</summary>
        public virtual async Task<List<LeaderboardEntry>> ReadLeaderboardAroundUserAsync(string leaderboardName, int range = 10)
        {
            var user = Subsystem.GetIdentity().CurrentUser;
            if (user == null) return new List<LeaderboardEntry>();

            var all = await ReadLeaderboardAsync(leaderboardName, 1, int.MaxValue);
            var idx = all.FindIndex(e => e.UserId == user.UserId);
            if (idx < 0) return new List<LeaderboardEntry>();

            int start = Math.Max(0, idx - range / 2);
            return all.Skip(start).Take(range).ToList();
        }

        /// <summary>获取当前用户在指定排行榜的排名。</summary>
        public virtual int GetRank(string leaderboardName)
        {
            var user = Subsystem.GetIdentity().CurrentUser;
            if (user == null) return -1;
            if (!_cache.TryGetValue(leaderboardName, out var list)) return -1;
            var entry = list.FirstOrDefault(e => e.UserId == user.UserId);
            return entry?.Rank ?? -1;
        }

        public virtual void Tick(float dt) { }
    }

    // ──────────────────────────────────────────────────────────────
    // 9. 成就系统 (OnlineAchievements)
    // ──────────────────────────────────────────────────────────────

    /// <summary>成就定义。</summary>
    public sealed class AchievementDefinition
    {
        public string Id { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string IconUrl { get; set; } = string.Empty;
        public bool bHidden { get; set; }
        public int ProgressTarget = 100;
        public int ScoreReward;
    }

    /// <summary>成就进度。</summary>
    public sealed class AchievementProgress
    {
        public string AchievementId { get; set; } = string.Empty;
        public int CurrentProgress { get; set; }
        public int TargetProgress { get; set; } = 100;
        public float Progress => TargetProgress > 0 ? Math.Clamp(CurrentProgress / (float)TargetProgress, 0f, 1f) : 0f;
        public bool bUnlocked { get; set; }
        public DateTime? UnlockedAt { get; set; }
    }

    /// <summary>
    /// 成就系统模块。
    /// 支持成就解锁、进度更新与批量查询。
    /// </summary>
    public class OnlineAchievements
    {
        protected readonly IOnlineSubsystem Subsystem;
        protected readonly Dictionary<string, AchievementDefinition> _definitions = new();
        protected readonly Dictionary<string, AchievementProgress> _progress = new();

        public event Action<string>? OnAchievementUnlocked;
        public event Action<string, int>? OnAchievementProgressUpdated;

        public OnlineAchievements(IOnlineSubsystem subsystem) { Subsystem = subsystem; }

        /// <summary>注册成就定义。</summary>
        public virtual void RegisterAchievement(AchievementDefinition def)
        {
            _definitions[def.Id] = def;
            if (!_progress.ContainsKey(def.Id))
            {
                _progress[def.Id] = new AchievementProgress
                {
                    AchievementId = def.Id,
                    TargetProgress = def.ProgressTarget
                };
            }
        }

        /// <summary>解锁成就。</summary>
        public virtual bool Unlock(string achievementId)
        {
            if (!_progress.TryGetValue(achievementId, out var progress)) return false;
            if (progress.bUnlocked) return true;

            progress.bUnlocked = true;
            progress.CurrentProgress = progress.TargetProgress;
            progress.UnlockedAt = DateTime.UtcNow;
            OnAchievementUnlocked?.Invoke(achievementId);

            var def = _definitions.TryGetValue(achievementId, out var d) ? d.DisplayName : achievementId;
            Console.WriteLine($"[Online] 成就解锁: {def}");
            return true;
        }

        /// <summary>更新成就进度。</summary>
        public virtual void UpdateProgress(string achievementId, int newProgress)
        {
            if (!_progress.TryGetValue(achievementId, out var progress)) return;
            if (progress.bUnlocked) return;

            progress.CurrentProgress = Math.Max(progress.CurrentProgress, newProgress);
            OnAchievementProgressUpdated?.Invoke(achievementId, progress.CurrentProgress);

            if (progress.CurrentProgress >= progress.TargetProgress)
                Unlock(achievementId);
        }

        /// <summary>增量更新进度。</summary>
        public virtual void AddProgress(string achievementId, int delta)
        {
            if (!_progress.TryGetValue(achievementId, out var progress)) return;
            UpdateProgress(achievementId, progress.CurrentProgress + delta);
        }

        /// <summary>查询成就进度。</summary>
        public virtual AchievementProgress? GetProgress(string achievementId)
            => _progress.TryGetValue(achievementId, out var p) ? p : null;

        /// <summary>查询所有成就进度。</summary>
        public virtual IReadOnlyList<AchievementProgress> GetAllProgress()
            => _progress.Values.ToList();

        /// <summary>获取已解锁成就数。</summary>
        public virtual int GetUnlockedCount()
            => _progress.Values.Count(p => p.bUnlocked);

        /// <summary>获取成就完成率（0~1）。</summary>
        public virtual float GetCompletionRatio()
            => _progress.Count > 0 ? GetUnlockedCount() / (float)_progress.Count : 0f;

        public virtual void Tick(float dt) { }
    }

    // ──────────────────────────────────────────────────────────────
    // 10. 云存档 (OnlineCloudSave)
    // ──────────────────────────────────────────────────────────────

    /// <summary>云存档冲突解决策略。</summary>
    public enum CloudSaveConflictResolution
    {
        /// <summary>使用云端版本。</summary>
        UseCloud,
        /// <summary>使用本地版本。</summary>
        UseLocal,
        /// <summary>使用较新的版本（按时间戳）。</summary>
        UseNewest,
        /// <summary>合并（保留两者）。</summary>
        Merge,
        /// <summary>手动解决（回调上层）。</summary>
        Manual
    }

    /// <summary>云存档条目。</summary>
    public sealed class CloudSaveEntry
    {
        public string Key { get; set; } = string.Empty;
        public byte[] Data { get; set; } = Array.Empty<byte>();
        public DateTime LastModifiedUtc { get; set; }
        public long SizeBytes { get; set; }
        public string DeviceId { get; set; } = string.Empty;
    }

    /// <summary>
    /// 云存档模块。
    /// 支持将本地存档上传到云端、从云端下载, 以及冲突检测与解决。
    /// 与 Core/SaveSystem 联动: SaveSystem 保存时可选同步到云。
    /// </summary>
    public class OnlineCloudSave
    {
        protected readonly IOnlineSubsystem Subsystem;
        protected readonly Dictionary<string, CloudSaveEntry> _localCache = new();
        protected readonly Dictionary<string, CloudSaveEntry> _cloudCache = new();

        public event Action<string, bool>? OnUploadComplete;
        public event Action<string, CloudSaveEntry?, bool>? OnDownloadComplete;
        public event Action<string, CloudSaveEntry, CloudSaveEntry>? OnConflictDetected;

        public OnlineCloudSave(IOnlineSubsystem subsystem) { Subsystem = subsystem; }

        /// <summary>上传存档到云端。</summary>
        public virtual async Task<bool> UploadAsync(string key, byte[] data)
        {
            try
            {
                await Task.Delay(50);
                var entry = new CloudSaveEntry
                {
                    Key = key,
                    Data = data,
                    LastModifiedUtc = DateTime.UtcNow,
                    SizeBytes = data.Length,
                    DeviceId = Environment.MachineName
                };
                _localCache[key] = entry;
                _cloudCache[key] = entry;
                OnUploadComplete?.Invoke(key, true);
                Console.WriteLine($"[Online] 云存档已上传: {key} ({data.Length} 字节)");
                return true;
            }
            catch (Exception ex)
            {
                OnUploadComplete?.Invoke(key, false);
                Console.WriteLine($"[Online] 云存档上传失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>从云端下载存档。</summary>
        public virtual async Task<CloudSaveEntry?> DownloadAsync(string key)
        {
            try
            {
                await Task.Delay(50);
                if (!_cloudCache.TryGetValue(key, out var entry))
                {
                    OnDownloadComplete?.Invoke(key, null, false);
                    return null;
                }
                OnDownloadComplete?.Invoke(key, entry, true);
                Console.WriteLine($"[Online] 云存档已下载: {key} ({entry.SizeBytes} 字节)");
                return entry;
            }
            catch (Exception ex)
            {
                OnDownloadComplete?.Invoke(key, null, false);
                Console.WriteLine($"[Online] 云存档下载失败: {ex.Message}");
                return null;
            }
        }

        /// <summary>检测本地与云端是否冲突（同一 key 的本地修改时间晚于云端且不同）。</summary>
        public virtual bool DetectConflict(string key)
        {
            if (!_localCache.TryGetValue(key, out var local)) return false;
            if (!_cloudCache.TryGetValue(key, out var cloud)) return false;
            // 数据不同且时间戳接近视为冲突
            if (!local.Data.SequenceEqual(cloud.Data))
            {
                OnConflictDetected?.Invoke(key, local, cloud);
                return true;
            }
            return false;
        }

        /// <summary>按策略解决冲突。</summary>
        public virtual CloudSaveEntry? ResolveConflict(string key, CloudSaveConflictResolution strategy)
        {
            if (!_localCache.TryGetValue(key, out var local)) return null;
            if (!_cloudCache.TryGetValue(key, out var cloud)) return local;

            CloudSaveEntry? resolved = strategy switch
            {
                CloudSaveConflictResolution.UseCloud => cloud,
                CloudSaveConflictResolution.UseLocal => local,
                CloudSaveConflictResolution.UseNewest => local.LastModifiedUtc >= cloud.LastModifiedUtc ? local : cloud,
                CloudSaveConflictResolution.Merge => new CloudSaveEntry
                {
                    Key = key,
                    Data = local.Data.Concat(cloud.Data).ToArray(),
                    LastModifiedUtc = DateTime.UtcNow,
                    SizeBytes = local.SizeBytes + cloud.SizeBytes,
                    DeviceId = local.DeviceId
                },
                _ => null // Manual 由上层处理
            };

            if (resolved != null)
            {
                _localCache[key] = resolved;
                _cloudCache[key] = resolved;
                Console.WriteLine($"[Online] 云存档冲突已解决 ({strategy}): {key}");
            }
            return resolved;
        }

        /// <summary>删除云端存档。</summary>
        public virtual async Task<bool> DeleteAsync(string key)
        {
            await Task.Delay(20);
            _cloudCache.Remove(key);
            _localCache.Remove(key);
            Console.WriteLine($"[Online] 云存档已删除: {key}");
            return true;
        }

        /// <summary>列出所有云端存档键。</summary>
        public virtual IReadOnlyList<string> ListKeys() => _cloudCache.Keys.ToList();

        public virtual void Tick(float dt) { }
    }

    // ──────────────────────────────────────────────────────────────
    // 11. 匹配系统 (OnlineMatchmaking)
    // ──────────────────────────────────────────────────────────────

    /// <summary>匹配规则。</summary>
    public sealed class MatchmakingRule
    {
        /// <summary>规则名。</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>所需队伍数。</summary>
        public int TeamCount = 2;

        /// <summary>每队人数。</summary>
        public int PlayersPerTeam = 5;

        /// <summary>技能差距容忍度（MMR 差距）。</summary>
        public float SkillTolerance = 200f;

        /// <summary>最大等待时间（秒）, 超过后放宽条件。</summary>
        public float MaxWaitTime = 60f;

        /// <summary>每级放宽降低的技能要求。</summary>
        public float SkillRelaxPerLevel = 50f;

        /// <summary>放宽级别间隔时间（秒）。</summary>
        public float RelaxInterval = 10f;

        /// <summary>最低延迟要求（毫秒）。</summary>
        public int MaxPing = 150;

        /// <summary>区域限制（空为不限）。</summary>
        public string Region = string.Empty;
    }

    /// <summary>匹配池中的玩家。</summary>
    public sealed class MatchmakingPlayer
    {
        public string UserId { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public float SkillRating { get; set; }
        public int PingMs { get; set; }
        public string Region { get; set; } = string.Empty;
        public DateTime JoinedAt { get; set; } = DateTime.UtcNow;
        public int RelaxLevel { get; set; }
    }

    /// <summary>匹配结果。</summary>
    public sealed class MatchmakingResult
    {
        public string MatchId { get; set; } = Guid.NewGuid().ToString("N");
        public List<List<MatchmakingPlayer>> Teams { get; set; } = new();
        public string ServerAddress { get; set; } = string.Empty;
        public int ServerPort { get; set; }
        public DateTime MatchedAt { get; set; } = DateTime.UtcNow;
        public float TotalWaitTime { get; set; }
    }

    /// <summary>
    /// 匹配系统模块。
    /// 维护匹配池, 按规则匹配玩家, 平衡队伍, 匹配成功后通知上层加入会话。
    /// </summary>
    public class OnlineMatchmaking
    {
        protected readonly IOnlineSubsystem Subsystem;
        protected readonly List<MatchmakingPlayer> _pool = new();
        protected readonly Dictionary<string, MatchmakingRule> _rules = new();
        protected MatchmakingRule? _activeRule;

        public event Action<MatchmakingResult>? OnMatchFound;
        public event Action<string>? OnMatchmakingStarted;
        public event Action<string>? OnMatchmakingCanceled;
        public event Action<MatchmakingPlayer, int>? OnWaitTimeUpdate;

        public bool IsMatchmaking { get; protected set; }
        public int PoolSize => _pool.Count;

        public OnlineMatchmaking(IOnlineSubsystem subsystem) { Subsystem = subsystem; }

        /// <summary>注册匹配规则。</summary>
        public virtual void RegisterRule(MatchmakingRule rule) => _rules[rule.Name] = rule;

        /// <summary>开始匹配。</summary>
        public virtual void StartMatchmaking(string ruleName)
        {
            if (!_rules.TryGetValue(ruleName, out var rule))
            {
                Console.WriteLine($"[Online] 未找到匹配规则: {ruleName}");
                return;
            }
            _activeRule = rule;
            IsMatchmaking = true;

            var user = Subsystem.GetIdentity().CurrentUser;
            if (user != null)
            {
                _pool.Add(new MatchmakingPlayer
                {
                    UserId = user.UserId,
                    DisplayName = user.DisplayName,
                    SkillRating = 1000f,
                    Region = "Asia"
                });
            }
            OnMatchmakingStarted?.Invoke(ruleName);
            Console.WriteLine($"[Online] 开始匹配 (规则: {ruleName}, 需要 {rule.TeamCount}x{rule.PlayersPerTeam})");
        }

        /// <summary>取消匹配。</summary>
        public virtual void CancelMatchmaking()
        {
            if (!IsMatchmaking) return;
            var user = Subsystem.GetIdentity().CurrentUser;
            if (user != null)
                _pool.RemoveAll(p => p.UserId == user.UserId);
            IsMatchmaking = false;
            OnMatchmakingCanceled?.Invoke(_activeRule?.Name ?? "");
            Console.WriteLine("[Online] 匹配已取消");
        }

        /// <summary>
        /// 每帧更新: 检查匹配池是否满足成局条件, 计算等待时间与条件放宽。
        /// </summary>
        public virtual void Tick(float dt)
        {
            if (!IsMatchmaking || _activeRule == null) return;

            int requiredPlayers = _activeRule.TeamCount * _activeRule.PlayersPerTeam;
            var now = DateTime.UtcNow;

            // 更新等待时间与放宽级别
            foreach (var player in _pool)
            {
                float waitTime = (float)(now - player.JoinedAt).TotalSeconds;
                int newLevel = Math.Min((int)(waitTime / _activeRule.RelaxInterval), 5);
                if (newLevel != player.RelaxLevel)
                {
                    player.RelaxLevel = newLevel;
                    OnWaitTimeUpdate?.Invoke(player, (int)waitTime);
                }
            }

            // 检查是否有足够玩家
            if (_pool.Count >= requiredPlayers)
            {
                TryFormMatch(_activeRule, requiredPlayers);
            }
        }

        /// <summary>尝试从匹配池中组建一场对局。</summary>
        protected virtual void TryFormMatch(MatchmakingRule rule, int requiredPlayers)
        {
            // 按技能排序, 取前 N 个, 均分到各队以平衡
            var candidates = _pool
                .OrderBy(p => p.SkillRating)
                .Take(requiredPlayers)
                .ToList();

            if (candidates.Count < requiredPlayers) return;

            // 蛇形分配以平衡队伍总技能
            var teams = new List<List<MatchmakingPlayer>>();
            for (int i = 0; i < rule.TeamCount; i++)
                teams.Add(new List<MatchmakingPlayer>());

            for (int i = 0; i < candidates.Count; i++)
            {
                int round = i / rule.TeamCount;
                int idx = round % 2 == 0 ? i % rule.TeamCount : rule.TeamCount - 1 - (i % rule.TeamCount);
                teams[idx].Add(candidates[i]);
            }

            // 从池中移除已匹配玩家
            foreach (var p in candidates) _pool.Remove(p);

            var earliestJoin = candidates.Min(p => p.JoinedAt);
            var result = new MatchmakingResult
            {
                Teams = teams,
                TotalWaitTime = (float)(DateTime.UtcNow - earliestJoin).TotalSeconds
            };

            IsMatchmaking = false;
            OnMatchFound?.Invoke(result);
            Console.WriteLine($"[Online] 匹配成功! {rule.TeamCount} 队各 {rule.PlayersPerTeam} 人, 等待 {result.TotalWaitTime:F1}s");
        }

        /// <summary>向匹配池添加一个玩家（服务器侧）。</summary>
        public virtual void AddPlayerToPool(MatchmakingPlayer player)
        {
            _pool.Add(player);
            Console.WriteLine($"[Online] 玩家 {player.DisplayName} 进入匹配池 (技能 {player.SkillRating})");
        }

        /// <summary>从匹配池移除玩家。</summary>
        public virtual void RemovePlayerFromPool(string userId)
        {
            _pool.RemoveAll(p => p.UserId == userId);
        }
    }

    // ──────────────────────────────────────────────────────────────
    // 12. 语音通信 (OnlineVoice)
    // ──────────────────────────────────────────────────────────────

    /// <summary>语音编码格式。</summary>
    public enum VoiceCodec { Opus, PCM, Speex, AAC }

    /// <summary>语音房间成员状态。</summary>
    public sealed class VoiceParticipant
    {
        public string UserId { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public bool bIsMuted { get; set; }
        public bool bIsSpeaking { get; set; }
        public float Volume { get; set; } = 1.0f;
        public float AudioLevel { get; set; }
    }

    /// <summary>
    /// 语音通信模块。
    /// 管理语音房间、麦克风推流、静音控制与音频编码。
    /// </summary>
    public class OnlineVoice
    {
        protected readonly IOnlineSubsystem Subsystem;
        protected readonly Dictionary<string, List<VoiceParticipant>> _rooms = new();
        protected VoiceCodec _codec = VoiceCodec.Opus;
        protected int _sampleRate = 48000;
        protected int _bitrate = 64000;

        /// <summary>本地用户是否静音。</summary>
        public bool bLocalMuted { get; protected set; }

        /// <summary>语音采集是否启用。</summary>
        public bool bCaptureEnabled { get; protected set; }

        public event Action<string, VoiceParticipant>? OnParticipantSpeaking;
        public event Action<string, VoiceParticipant>? OnParticipantStoppedSpeaking;
        public event Action<string, VoiceParticipant>? OnParticipantJoined;
        public event Action<string, string>? OnParticipantLeft;

        public OnlineVoice(IOnlineSubsystem subsystem) { Subsystem = subsystem; }

        /// <summary>配置编码参数。</summary>
        public virtual void Configure(VoiceCodec codec, int sampleRate, int bitrate)
        {
            _codec = codec;
            _sampleRate = sampleRate;
            _bitrate = bitrate;
            Console.WriteLine($"[Online] 语音编码配置: {codec} {sampleRate}Hz {bitrate}bps");
        }

        /// <summary>加入语音房间。</summary>
        public virtual bool JoinVoiceRoom(string roomId)
        {
            if (!_rooms.ContainsKey(roomId))
                _rooms[roomId] = new List<VoiceParticipant>();

            var user = Subsystem.GetIdentity().CurrentUser;
            if (user != null)
            {
                var participant = new VoiceParticipant
                {
                    UserId = user.UserId,
                    DisplayName = user.DisplayName
                };
                _rooms[roomId].Add(participant);
                OnParticipantJoined?.Invoke(roomId, participant);
                Console.WriteLine($"[Online] 已加入语音房间: {roomId}");
            }
            return true;
        }

        /// <summary>离开语音房间。</summary>
        public virtual void LeaveVoiceRoom(string roomId)
        {
            if (!_rooms.TryGetValue(roomId, out var participants)) return;
            var user = Subsystem.GetIdentity().CurrentUser;
            if (user != null)
            {
                participants.RemoveAll(p => p.UserId == user.UserId);
                OnParticipantLeft?.Invoke(roomId, user.UserId);
            }
            if (participants.Count == 0) _rooms.Remove(roomId);
            Console.WriteLine($"[Online] 已离开语音房间: {roomId}");
        }

        /// <summary>开始麦克风采集与推流。</summary>
        public virtual void StartCapture()
        {
            bCaptureEnabled = true;
            Console.WriteLine("[Online] 语音采集已启动");
        }

        /// <summary>停止麦克风采集。</summary>
        public virtual void StopCapture()
        {
            bCaptureEnabled = false;
            Console.WriteLine("[Online] 语音采集已停止");
        }

        /// <summary>静音/取消静音本地麦克风。</summary>
        public virtual void ToggleMute()
        {
            bLocalMuted = !bLocalMuted;
            Console.WriteLine($"[Online] 本地麦克风 {(bLocalMuted ? "已静音" : "已取消静音")}");
        }

        /// <summary>静音指定参与者。</summary>
        public virtual void MuteParticipant(string roomId, string userId)
        {
            if (!_rooms.TryGetValue(roomId, out var participants)) return;
            var p = participants.FirstOrDefault(x => x.UserId == userId);
            if (p != null)
            {
                p.bIsMuted = true;
                Console.WriteLine($"[Online] 已静音 {userId} (房间 {roomId})");
            }
        }

        /// <summary>设置参与者音量。</summary>
        public virtual void SetParticipantVolume(string roomId, string userId, float volume)
        {
            if (!_rooms.TryGetValue(roomId, out var participants)) return;
            var p = participants.FirstOrDefault(x => x.UserId == userId);
            if (p != null) p.Volume = Math.Clamp(volume, 0f, 2f);
        }

        /// <summary>更新参与者说话状态（由音频引擎回调）。</summary>
        public virtual void UpdateSpeakingState(string roomId, string userId, float audioLevel)
        {
            if (!_rooms.TryGetValue(roomId, out var participants)) return;
            var p = participants.FirstOrDefault(x => x.UserId == userId);
            if (p == null) return;
            bool wasSpeaking = p.bIsSpeaking;
            p.AudioLevel = audioLevel;
            p.bIsSpeaking = audioLevel > 0.01f && !p.bIsMuted;
            if (!wasSpeaking && p.bIsSpeaking)
                OnParticipantSpeaking?.Invoke(roomId, p);
            else if (wasSpeaking && !p.bIsSpeaking)
                OnParticipantStoppedSpeaking?.Invoke(roomId, p);
        }

        /// <summary>获取房间参与者列表。</summary>
        public virtual IReadOnlyList<VoiceParticipant> GetParticipants(string roomId)
            => _rooms.TryGetValue(roomId, out var list) ? list.ToList() : new List<VoiceParticipant>();

        public virtual void Tick(float dt) { }
    }

    // ──────────────────────────────────────────────────────────────
    // 13. 大厅系统 (OnlineLobby)
    // ──────────────────────────────────────────────────────────────

    /// <summary>大厅成员。</summary>
    public sealed class LobbyMember
    {
        public string UserId { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public bool bIsOwner { get; set; }
        public Dictionary<string, string> Attributes { get; set; } = new();
        public DateTime JoinedAt { get; set; } = DateTime.UtcNow;
    }

    /// <summary>在线大厅。</summary>
    public sealed class OnlineLobbyData
    {
        public string LobbyId { get; set; } = Guid.NewGuid().ToString("N");
        public string Name { get; set; } = string.Empty;
        public int MaxMembers { get; set; } = 16;
        public OnlineSessionVisibility Visibility { get; set; } = OnlineSessionVisibility.Public;
        public string OwnerId { get; set; } = string.Empty;
        public Dictionary<string, string> LobbyAttributes { get; set; } = new();
        public List<LobbyMember> Members { get; set; } = new();
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        public int MemberCount => Members.Count;
        public bool IsFull => Members.Count >= MaxMembers;
    }

    /// <summary>大厅搜索结果。</summary>
    public sealed class LobbySearchResult
    {
        public string LobbyId { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public int MemberCount { get; set; }
        public int MaxMembers { get; set; }
        public OnlineSessionVisibility Visibility { get; set; }
        public Dictionary<string, string> Attributes { get; set; } = new();
    }

    /// <summary>大厅事件类型。</summary>
    public enum LobbyEventType { MemberJoined, MemberLeft, AttributeChanged, OwnerChanged, LobbyDestroyed }

    /// <summary>
    /// 大厅系统模块。
    /// 大厅比会话更轻量, 适用于大厅/房间阶段（非正式对局中）,
    /// 支持自定义属性查询, 可在成员间同步数据。
    /// 与 OnlineSession 互补: 大厅阶段 -> 匹配 -> 会话阶段。
    /// </summary>
    public class OnlineLobby
    {
        protected readonly IOnlineSubsystem Subsystem;
        protected readonly Dictionary<string, OnlineLobbyData> _lobbies = new();

        public OnlineLobbyData? CurrentLobby { get; protected set; }

        public event Action<LobbyEventType, string, string?>? OnLobbyEvent;
        public event Action<OnlineLobbyData>? OnLobbyJoined;
        public event Action? OnLobbyLeft;

        public OnlineLobby(IOnlineSubsystem subsystem) { Subsystem = subsystem; }

        /// <summary>创建大厅。</summary>
        public virtual OnlineLobbyData CreateLobby(string name, int maxMembers, OnlineSessionVisibility visibility = OnlineSessionVisibility.Public)
        {
            var lobby = new OnlineLobbyData
            {
                Name = name,
                MaxMembers = maxMembers,
                Visibility = visibility
            };
            var user = Subsystem.GetIdentity().CurrentUser;
            if (user != null)
            {
                lobby.OwnerId = user.UserId;
                lobby.Members.Add(new LobbyMember
                {
                    UserId = user.UserId,
                    DisplayName = user.DisplayName,
                    bIsOwner = true
                });
            }
            _lobbies[lobby.LobbyId] = lobby;
            CurrentLobby = lobby;
            OnLobbyJoined?.Invoke(lobby);
            Console.WriteLine($"[Online] 大厅已创建: {name} ({lobby.LobbyId})");
            return lobby;
        }

        /// <summary>搜索大厅。</summary>
        public virtual async Task<List<LobbySearchResult>> SearchLobbiesAsync(Dictionary<string, string>? attributeFilters = null, int maxResults = 50)
        {
            await Task.Delay(30);
            var results = new List<LobbySearchResult>();
            foreach (var lobby in _lobbies.Values)
            {
                if (lobby.IsFull) continue;
                if (lobby.Visibility == OnlineSessionVisibility.Private) continue;
                // 属性过滤
                if (attributeFilters != null)
                {
                    bool match = attributeFilters.All(kv =>
                        lobby.LobbyAttributes.TryGetValue(kv.Key, out var v) && v == kv.Value);
                    if (!match) continue;
                }
                results.Add(new LobbySearchResult
                {
                    LobbyId = lobby.LobbyId,
                    Name = lobby.Name,
                    MemberCount = lobby.MemberCount,
                    MaxMembers = lobby.MaxMembers,
                    Visibility = lobby.Visibility,
                    Attributes = new Dictionary<string, string>(lobby.LobbyAttributes)
                });
            }
            return results.Take(maxResults).ToList();
        }

        /// <summary>加入大厅。</summary>
        public virtual bool JoinLobby(string lobbyId)
        {
            if (!_lobbies.TryGetValue(lobbyId, out var lobby)) return false;
            if (lobby.IsFull) return false;
            var user = Subsystem.GetIdentity().CurrentUser;
            if (user == null) return false;
            lobby.Members.Add(new LobbyMember
            {
                UserId = user.UserId,
                DisplayName = user.DisplayName
            });
            CurrentLobby = lobby;
            OnLobbyJoined?.Invoke(lobby);
            OnLobbyEvent?.Invoke(LobbyEventType.MemberJoined, lobbyId, user.UserId);
            Console.WriteLine($"[Online] 已加入大厅: {lobby.Name}");
            return true;
        }

        /// <summary>离开当前大厅。</summary>
        public virtual void LeaveLobby()
        {
            if (CurrentLobby == null) return;
            var user = Subsystem.GetIdentity().CurrentUser;
            if (user != null)
            {
                CurrentLobby.Members.RemoveAll(m => m.UserId == user.UserId);
                OnLobbyEvent?.Invoke(LobbyEventType.MemberLeft, CurrentLobby.LobbyId, user.UserId);
                // 若房主离开, 转移房主
                if (CurrentLobby.OwnerId == user.UserId && CurrentLobby.Members.Count > 0)
                {
                    var newOwner = CurrentLobby.Members[0];
                    newOwner.bIsOwner = true;
                    CurrentLobby.OwnerId = newOwner.UserId;
                    OnLobbyEvent?.Invoke(LobbyEventType.OwnerChanged, CurrentLobby.LobbyId, newOwner.UserId);
                }
                if (CurrentLobby.Members.Count == 0)
                {
                    _lobbies.Remove(CurrentLobby.LobbyId);
                    OnLobbyEvent?.Invoke(LobbyEventType.LobbyDestroyed, CurrentLobby.LobbyId, null);
                }
            }
            CurrentLobby = null;
            OnLobbyLeft?.Invoke();
            Console.WriteLine("[Online] 已离开大厅");
        }

        /// <summary>设置大厅属性（仅房主）。</summary>
        public virtual void SetLobbyAttribute(string key, string value)
        {
            if (CurrentLobby == null) return;
            var user = Subsystem.GetIdentity().CurrentUser;
            if (user == null || CurrentLobby.OwnerId != user.UserId) return;
            CurrentLobby.LobbyAttributes[key] = value;
            OnLobbyEvent?.Invoke(LobbyEventType.AttributeChanged, CurrentLobby.LobbyId, null);
        }

        /// <summary>设置成员属性。</summary>
        public virtual void SetMemberAttribute(string key, string value)
        {
            if (CurrentLobby == null) return;
            var user = Subsystem.GetIdentity().CurrentUser;
            if (user == null) return;
            var member = CurrentLobby.Members.FirstOrDefault(m => m.UserId == user.UserId);
            if (member != null) member.Attributes[key] = value;
        }

        /// <summary>按属性查询成员。</summary>
        public virtual List<LobbyMember> QueryMembersByAttribute(string key, string value)
        {
            if (CurrentLobby == null) return new List<LobbyMember>();
            return CurrentLobby.Members
                .Where(m => m.Attributes.TryGetValue(key, out var v) && v == value)
                .ToList();
        }

        public virtual void Tick(float dt) { }
    }

    // ──────────────────────────────────────────────────────────────
    // 14. 在线子系统基类实现 (OnlineSubsystemBase)
    //     提供各模块的默认实现, 平台特定子类可覆盖个别模块。
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 在线子系统基类实现。
    /// 子类（如 SteamOnlineSubsystem）继承此类并按需覆盖具体模块。
    /// </summary>
    public abstract class OnlineSubsystemBase : IOnlineSubsystem
    {
        /// <summary>子系统标识名。</summary>
        public abstract string SubsystemName { get; }

        /// <summary>目标平台。</summary>
        public abstract OnlinePlatform Platform { get; }

        /// <summary>是否已初始化。</summary>
        public bool IsInitialized { get; protected set; }

        // ── 各子模块实例 ──
        protected OnlineIdentity? _identity;
        protected OnlineSession? _session;
        protected OnlinePresence? _presence;
        protected OnlineFriends? _friends;
        protected OnlineLeaderboards? _leaderboards;
        protected OnlineAchievements? _achievements;
        protected OnlineCloudSave? _cloudSave;
        protected OnlineMatchmaking? _matchmaking;
        protected OnlineVoice? _voice;
        protected OnlineLobby? _lobby;

        /// <summary>网络复制系统引用（用于会话联动）。</summary>
        public NetworkReplicationSystem? NetworkReplication { get; set; }

        public virtual bool Initialize()
        {
            _identity = CreateIdentity();
            _session = CreateSession();
            _session.NetworkReplication = NetworkReplication;
            _presence = CreatePresence();
            _friends = CreateFriends();
            _leaderboards = CreateLeaderboards();
            _achievements = CreateAchievements();
            _cloudSave = CreateCloudSave();
            _matchmaking = CreateMatchmaking();
            _voice = CreateVoice();
            _lobby = CreateLobby();
            IsInitialized = true;
            Console.WriteLine($"[Online] 在线子系统已初始化: {SubsystemName} ({Platform})");
            return true;
        }

        public virtual void Shutdown()
        {
            _session?.LeaveSession();
            _voice?.StopCapture();
            IsInitialized = false;
            Console.WriteLine($"[Online] 在线子系统已关闭: {SubsystemName}");
        }

        // ── 工厂方法（子类可覆盖以提供平台特定实现） ──

        protected virtual OnlineIdentity CreateIdentity() => new(this);
        protected virtual OnlineSession CreateSession() => new(this);
        protected virtual OnlinePresence CreatePresence() => new(this);
        protected virtual OnlineFriends CreateFriends() => new(this);
        protected virtual OnlineLeaderboards CreateLeaderboards() => new(this);
        protected virtual OnlineAchievements CreateAchievements() => new(this);
        protected virtual OnlineCloudSave CreateCloudSave() => new(this);
        protected virtual OnlineMatchmaking CreateMatchmaking() => new(this);
        protected virtual OnlineVoice CreateVoice() => new(this);
        protected virtual OnlineLobby CreateLobby() => new(this);

        // ── 子模块访问器 ──

        public OnlineIdentity GetIdentity() => _identity!;
        public OnlineSession GetSession() => _session!;
        public OnlinePresence GetPresence() => _presence!;
        public OnlineFriends GetFriends() => _friends!;
        public OnlineLeaderboards GetLeaderboards() => _leaderboards!;
        public OnlineAchievements GetAchievements() => _achievements!;
        public OnlineCloudSave GetCloudSave() => _cloudSave!;
        public OnlineMatchmaking GetMatchmaking() => _matchmaking!;
        public OnlineVoice GetVoice() => _voice!;
        public OnlineLobby GetLobby() => _lobby!;

        /// <summary>每帧更新所有子模块。</summary>
        public virtual void Tick(float deltaTime)
        {
            _identity?.Tick(deltaTime);
            _session?.Tick(deltaTime);
            _presence?.Tick(deltaTime);
            _friends?.Tick(deltaTime);
            _leaderboards?.Tick(deltaTime);
            _achievements?.Tick(deltaTime);
            _cloudSave?.Tick(deltaTime);
            _matchmaking?.Tick(deltaTime);
            _voice?.Tick(deltaTime);
            _lobby?.Tick(deltaTime);
        }
    }

    // ──────────────────────────────────────────────────────────────
    // 15. 平台特定子实现
    // ──────────────────────────────────────────────────────────────

    /// <summary>Steam 平台在线子系统。</summary>
    public sealed class SteamOnlineSubsystem : OnlineSubsystemBase
    {
        public override string SubsystemName => "Steam";
        public override OnlinePlatform Platform => OnlinePlatform.Steam;

        protected override OnlineIdentity CreateIdentity()
            => new SteamIdentity(this);

        public override bool Initialize()
        {
            Console.WriteLine("[Online] 初始化 Steam SDK...");
            return base.Initialize();
        }
    }

    /// <summary>Steam 身份认证（使用 Steam Auth Ticket）。</summary>
    public sealed class SteamIdentity : OnlineIdentity
    {
        public SteamIdentity(IOnlineSubsystem subsystem) : base(subsystem) { }

        public override async Task<bool> LoginAsync(string userId, string credentials, OnlinePlatform platform)
        {
            // Steam 平台使用 Auth Ticket 认证, 这里模拟获取 Ticket
            Console.WriteLine("[Online] Steam: 正在获取 Auth Ticket...");
            string ticket = "steam_ticket_" + Guid.NewGuid().ToString("N");
            return await base.LoginAsync(userId, ticket, OnlinePlatform.Steam);
        }
    }

    /// <summary>Epic Games 在线服务子系统。</summary>
    public sealed class EpicOnlineSubsystem : OnlineSubsystemBase
    {
        public override string SubsystemName => "Epic";
        public override OnlinePlatform Platform => OnlinePlatform.Epic;

        public override bool Initialize()
        {
            Console.WriteLine("[Online] 初始化 Epic Online Services SDK...");
            return base.Initialize();
        }
    }

    /// <summary>PlayStation Network 子系统。</summary>
    public sealed class PSNOnlineSubsystem : OnlineSubsystemBase
    {
        public override string SubsystemName => "PSN";
        public override OnlinePlatform Platform => OnlinePlatform.PSN;
        public override bool Initialize()
        {
            Console.WriteLine("[Online] 初始化 PSN SDK...");
            return base.Initialize();
        }
    }

    /// <summary>Xbox Live 子系统。</summary>
    public sealed class XboxOnlineSubsystem : OnlineSubsystemBase
    {
        public override string SubsystemName => "Xbox";
        public override OnlinePlatform Platform => OnlinePlatform.Xbox;
        public override bool Initialize()
        {
            Console.WriteLine("[Online] 初始化 Xbox Live GDK...");
            return base.Initialize();
        }
    }

    /// <summary>Null（自建服务器）子系统, 不依赖第三方平台。</summary>
    public sealed class NullOnlineSubsystem : OnlineSubsystemBase
    {
        public override string SubsystemName => "Null";
        public override OnlinePlatform Platform => OnlinePlatform.Null;
    }

    /// <summary>离线/访客子系统。</summary>
    public sealed class GuestOnlineSubsystem : OnlineSubsystemBase
    {
        public override string SubsystemName => "Guest";
        public override OnlinePlatform Platform => OnlinePlatform.Guest;

        public override bool Initialize()
        {
            base.Initialize();
            // 自动以访客身份登录
            _identity?.LoginAsGuestAsync();
            return true;
        }
    }

    // ──────────────────────────────────────────────────────────────
    // 16. 子系统工厂 (OnlineSubsystemFactory)
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 在线子系统工厂。
    /// 根据目标平台创建对应的子系统实现。
    /// 支持运行时切换平台（如从离线切换到 Steam）。
    /// </summary>
    public static class OnlineSubsystemFactory
    {
        /// <summary>当前活动的子系统实例。</summary>
        public static IOnlineSubsystem? ActiveSubsystem { get; private set; }

        /// <summary>根据平台创建在线子系统。</summary>
        public static IOnlineSubsystem Create(OnlinePlatform platform, NetworkReplicationSystem? networkReplication = null)
        {
            OnlineSubsystemBase subsystem = platform switch
            {
                OnlinePlatform.Steam => new SteamOnlineSubsystem(),
                OnlinePlatform.Epic => new EpicOnlineSubsystem(),
                OnlinePlatform.PSN => new PSNOnlineSubsystem(),
                OnlinePlatform.Xbox => new XboxOnlineSubsystem(),
                OnlinePlatform.Null => new NullOnlineSubsystem(),
                OnlinePlatform.Guest => new GuestOnlineSubsystem(),
                _ => new NullOnlineSubsystem()
            };
            subsystem.NetworkReplication = networkReplication;
            return subsystem;
        }

        /// <summary>
        /// 创建并初始化为活动子系统, 替换之前的实例。
        /// </summary>
        public static IOnlineSubsystem CreateAndActivate(OnlinePlatform platform, NetworkReplicationSystem? networkReplication = null)
        {
            // 关闭旧实例
            ActiveSubsystem?.Shutdown();

            var subsystem = Create(platform, networkReplication);
            subsystem.Initialize();
            ActiveSubsystem = subsystem;
            Console.WriteLine($"[Online] 活动在线子系统已切换为: {subsystem.SubsystemName}");
            return subsystem;
        }

        /// <summary>获取当前活动子系统。</summary>
        public static IOnlineSubsystem? GetActive() => ActiveSubsystem;

        /// <summary>关闭并清除活动子系统。</summary>
        public static void ShutdownActive()
        {
            ActiveSubsystem?.Shutdown();
            ActiveSubsystem = null;
        }

        /// <summary>每帧更新活动子系统（由引擎主循环调用）。</summary>
        public static void TickActive(float dt)
        {
            ActiveSubsystem?.Tick(dt);
        }
    }

    // ──────────────────────────────────────────────────────────────
    // 17. 在线子系统配置（与 EngineBootstrap 对接）
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 在线子系统配置。
    /// 在 EngineConfig 中声明, 引擎初始化时据此创建在线子系统。
    /// </summary>
    public sealed class OnlineSubsystemConfig
    {
        /// <summary>目标平台。</summary>
        public OnlinePlatform Platform = OnlinePlatform.Null;

        /// <summary>是否在引擎启动时自动初始化。</summary>
        public bool bAutoInitialize = false;

        /// <summary>应用 ID（Steam App ID / Epic Product ID 等）。</summary>
        public string AppId = string.Empty;

        /// <summary>API Key。</summary>
        public string ApiKey = string.Empty;

        /// <summary>是否启用语音。</summary>
        public bool bEnableVoice = true;

        /// <summary>是否启用云存档。</summary>
        public bool bEnableCloudSave = true;

        /// <summary>是否启用匹配。</summary>
        public bool bEnableMatchmaking = true;
    }
}
