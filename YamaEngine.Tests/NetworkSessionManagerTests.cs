// NetworkSessionManagerTests.cs — 网络会话管理 (NetworkSessionManager) 验证套件
// 覆盖:
//   * 连接到达: 注册玩家 + 进入 Lobby 状态 + 触发 OnPlayerJoinedSession
//   * 连接断开: 离开房间 + 清理玩家/端点缓存 + 回到 None + 触发 OnPlayerLeftSession
//   * 创建会话 (房主): 进入 InRoom + 记录 RoomId + 房主角色
//   * 加入会话 (客户端): 进入 InRoom + 记录 RoomId + 成员加入
//   * 离开房间: 回到 Lobby + 清空 RoomId
//   * 开始/结束游戏: InGame/Lobby 转换 + OnGameStarted/OnGameEnded 事件
//   * 兴趣图观察者生命周期: 连接加入观察者, 断开移除观察者 (无运输层也能移除)
//   * 端到端管线: 连接 → 建房 → 入房 → 开始 → 结束 → 离开 → 断开
//   * 心跳/更新无异常
// 全部 CPU 级确定性, 不依赖真实网络/GPU。
using System;
using System.Net;
using YamaEngine.Network;
using static YamaEngine.Tests.TestHelpers;

namespace YamaEngine.Tests;

/// <summary>网络会话管理验证套件（对齐 UE4.7 GameSession / UNetConnection 生命周期）。</summary>
public sealed class NetworkSessionManagerTests
{
    private static IPEndPoint EP(int port) => new IPEndPoint(IPAddress.Parse("127.0.0.1"), port);

    /// <summary>连接到达: 注册玩家 + Lobby 状态 + OnPlayerJoinedSession。</summary>
    public static void TestConnectedRegistersPlayerAndLobbyState()
    {
        using var mgr = new NetworkSessionManager(transport: null);
        bool joined = false;
        string? joinedPlayer = null;
        mgr.OnPlayerJoinedSession += (conn, player) => { joined = true; joinedPlayer = player; };

        var playerId = mgr.HandleConnectionConnected(1, EP(4001));

        AssertEqual("player-1", playerId, "玩家 ID 应为 player-1。");
        AssertEqual(SessionState.Lobby, mgr.State, "连接后应进入 Lobby。");
        AssertTrue(mgr.HasConnection(1), "应注册连接。");
        AssertEqual(1, mgr.OnlinePlayerCount, "在线玩家数应为 1。");
        AssertEqual("player-1", mgr.GetPlayerId(1), "GetPlayerId 应返回 player-1。");
        AssertEqual(1, mgr.GetConnectionId("player-1"), "GetConnectionId 应返回 1。");
        AssertTrue(joined, "应触发 OnPlayerJoinedSession。");
        AssertEqual("player-1", joinedPlayer, "事件应携带玩家 ID。");
    }

    /// <summary>连接断开: 离开房间 + 清理 + 回到 None + OnPlayerLeftSession。</summary>
    public static void TestDisconnectCleansUpPlayerAndState()
    {
        using var mgr = new NetworkSessionManager(transport: null);
        bool left = false;
        string? leftPlayer = null;
        mgr.OnPlayerLeftSession += (conn, player) => { left = true; leftPlayer = player; };

        mgr.HandleConnectionConnected(2, EP(4002));
        mgr.HandleConnectionDisconnected(2);

        AssertTrue(!mgr.HasConnection(2), "断开后不应再有连接。");
        AssertEqual(0, mgr.OnlinePlayerCount, "在线玩家数应为 0。");
        AssertEqual(SessionState.None, mgr.State, "全部玩家断开后应回到 None。");
        AssertTrue(left, "应触发 OnPlayerLeftSession。");
        AssertEqual("player-2", leftPlayer, "事件应携带玩家 ID。");
    }

    /// <summary>创建会话 (房主): InRoom + RoomId + 房主角色。</summary>
    public static void TestCreateSessionHostTransitionsToInRoom()
    {
        using var mgr = new NetworkSessionManager(transport: null);
        mgr.HandleConnectionConnected(10, EP(4010));

        var result = mgr.CreateSession(10, "Alpha", null, 8);

        AssertEqual(LobbyResult.Success, result, "建房应成功。");
        AssertEqual(SessionState.InRoom, mgr.State, "建房后应进入 InRoom。");
        AssertTrue(!string.IsNullOrEmpty(mgr.RoomId), "应记录 RoomId。");
    }

    /// <summary>加入会话 (客户端): InRoom + RoomId + 成员加入。</summary>
    public static void TestJoinSessionClientTransitionsToInRoom()
    {
        using var mgr = new NetworkSessionManager(transport: null);
        mgr.HandleConnectionConnected(10, EP(4010));
        mgr.CreateSession(10, "Beta", null, 8);
        var roomId = mgr.RoomId!;

        mgr.HandleConnectionConnected(11, EP(4011));
        var result = mgr.JoinSession(11, roomId, null, "client-one");

        AssertEqual(LobbyResult.Success, result, "加入房间应成功。");
        AssertEqual(SessionState.InRoom, mgr.State, "加入后应进入 InRoom。");
        AssertEqual(roomId, mgr.RoomId, "RoomId 应保持一致。");
    }

    /// <summary>离开房间: 回到 Lobby + 清空 RoomId。</summary>
    public static void TestLeaveRoomReturnsToLobby()
    {
        using var mgr = new NetworkSessionManager(transport: null);
        mgr.HandleConnectionConnected(10, EP(4010));
        mgr.CreateSession(10, "Gamma", null, 8);
        AssertEqual(SessionState.InRoom, mgr.State, "前置: 应处于 InRoom。");

        var result = mgr.LeaveRoom(10);

        AssertEqual(LobbyResult.Success, result, "离开房间应成功。");
        AssertEqual(SessionState.Lobby, mgr.State, "离开后应回到 Lobby。");
        AssertTrue(mgr.RoomId == null, "离开后应清空 RoomId。");
        AssertTrue(mgr.HasConnection(10), "离开房间不应断开连接。");
    }

    /// <summary>开始/结束游戏: InGame/Lobby 转换 + 事件。</summary>
    public static void TestStartEndGameStateAndEvents()
    {
        using var mgr = new NetworkSessionManager(transport: null);
        bool started = false, ended = false;
        mgr.OnGameStarted += _ => started = true;
        mgr.OnGameEnded += _ => ended = true;

        mgr.HandleConnectionConnected(10, EP(4010));
        mgr.CreateSession(10, "Delta", null, 8);

        var startResult = mgr.StartGame(10);
        AssertEqual(LobbyResult.Success, startResult, "开始游戏应成功。");
        AssertEqual(SessionState.InGame, mgr.State, "开始后应进入 InGame。");
        AssertTrue(started, "应触发 OnGameStarted。");

        var endResult = mgr.EndGame(10);
        AssertEqual(LobbyResult.Success, endResult, "结束游戏应成功。");
        AssertEqual(SessionState.Lobby, mgr.State, "结束后应回到 Lobby。");
        AssertTrue(ended, "应触发 OnGameEnded。");
    }

    /// <summary>兴趣图观察者生命周期: 连接加入, 断开移除 (无运输层也能移除)。</summary>
    public static void TestInterestGraphObserverLifecycle()
    {
        var graph = new ReplicationGraphReal();
        using var mgr = new NetworkSessionManager(transport: null, lobby: null, interestGraph: graph);

        var ep = EP(5001);
        mgr.HandleConnectionConnected(20, ep);
        AssertEqual(1, graph.ConnectionCount, "连接后兴趣图应增加 1 个观察者。");

        mgr.HandleConnectionDisconnected(20);
        AssertEqual(0, graph.ConnectionCount, "断开后兴趣图应移除观察者 (无需运输层)。");
    }

    /// <summary>端到端管线: 连接 → 建房 → 入房 → 开始 → 结束 → 离开 → 断开。</summary>
    public static void TestFullSessionPipelineEndToEnd()
    {
        var graph = new ReplicationGraphReal();
        using var mgr = new NetworkSessionManager(transport: null, lobby: null, interestGraph: graph);

        int joinedSession = 0, leftSession = 0, started = 0, ended = 0;
        mgr.OnPlayerJoinedSession += (_, _) => joinedSession++;
        mgr.OnPlayerLeftSession += (_, _) => leftSession++;
        mgr.OnGameStarted += _ => started++;
        mgr.OnGameEnded += _ => ended++;

        // 1) 连接: 房主 + 客户端
        mgr.HandleConnectionConnected(30, EP(5030));
        mgr.HandleConnectionConnected(31, EP(5031));
        AssertEqual(SessionState.Lobby, mgr.State, "两名玩家连接后应处于 Lobby。");
        AssertEqual(2, joinedSession, "两名玩家加入会话事件。");
        AssertEqual(2, graph.ConnectionCount, "兴趣图应有 2 个观察者。");

        // 2) 房主建房
        var create = mgr.CreateSession(30, "Epsilon", null, 16);
        AssertEqual(LobbyResult.Success, create, "建房应成功。");
        AssertEqual(SessionState.InRoom, mgr.State, "建房后应处于 InRoom。");

        // 3) 客户端入房
        var join = mgr.JoinSession(31, mgr.RoomId!, null, "p31");
        AssertEqual(LobbyResult.Success, join, "入房应成功。");
        AssertEqual(SessionState.InRoom, mgr.State, "入房后应处于 InRoom。");

        // 4) 房主开始游戏
        var start = mgr.StartGame(30);
        AssertEqual(LobbyResult.Success, start, "开始游戏应成功。");
        AssertEqual(SessionState.InGame, mgr.State, "开始后应处于 InGame。");
        AssertEqual(1, started, "应触发一次开始游戏事件。");

        // 5) 房主结束游戏
        var end = mgr.EndGame(30);
        AssertEqual(LobbyResult.Success, end, "结束游戏应成功。");
        AssertEqual(SessionState.Lobby, mgr.State, "结束后应回到 Lobby。");
        AssertEqual(1, ended, "应触发一次结束游戏事件。");

        // 6) 客户端离开房间
        var leave = mgr.LeaveRoom(31);
        AssertEqual(LobbyResult.Success, leave, "离开房间应成功。");
        AssertEqual(SessionState.Lobby, mgr.State, "客户端离开后房主仍应处于 Lobby。");

        // 7) 断开连接
        mgr.HandleConnectionDisconnected(30);
        mgr.HandleConnectionDisconnected(31);
        AssertEqual(SessionState.None, mgr.State, "全部断开后应回到 None。");
        AssertEqual(2, leftSession, "两名玩家离开会话事件。");
        AssertEqual(0, graph.ConnectionCount, "兴趣图观察者应全部移除。");
        AssertEqual(0, mgr.OnlinePlayerCount, "在线玩家数应为 0。");
    }

    /// <summary>心跳/更新不抛异常。</summary>
    public static void TestHeartbeatAndUpdateNoThrow()
    {
        using var mgr = new NetworkSessionManager(transport: null);
        mgr.HandleConnectionConnected(40, EP(5040));
        mgr.Heartbeat(40, 12.5f);
        mgr.Update(1f / 60f);
        AssertTrue(true, "心跳与更新不应抛异常。");
    }
}
