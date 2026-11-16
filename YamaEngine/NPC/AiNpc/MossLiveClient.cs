using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace YamaEngine.NPC.AiNpc;

/// <summary>MOSS-Live 传输抽象（默认 WebSocket，可替换为内存管道用于测试）。</summary>
public interface IMossLiveTransport : IAsyncDisposable
{
    bool IsConnected { get; }
    Task ConnectAsync(Uri uri, CancellationToken ct);
    Task SendAsync(string text, CancellationToken ct);
    /// <summary>返回 null 表示对端已关闭。</summary>
    Task<string?> ReceiveAsync(CancellationToken ct);
    Task CloseAsync(CancellationToken ct);
}

public sealed class WebSocketMossTransport : IMossLiveTransport
{
    private ClientWebSocket? _socket;

    public bool IsConnected => _socket?.State == WebSocketState.Open;

    public async Task ConnectAsync(Uri uri, CancellationToken ct)
    {
        await CloseAsync(CancellationToken.None).ConfigureAwait(false);
        var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
        _socket = socket;
        await socket.ConnectAsync(uri, ct).ConfigureAwait(false);
    }

    public async Task SendAsync(string text, CancellationToken ct)
    {
        var socket = _socket ?? throw new InvalidOperationException("MOSS-Live 尚未连接");
        var bytes = Encoding.UTF8.GetBytes(text);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
    }

    public async Task<string?> ReceiveAsync(CancellationToken ct)
    {
        var socket = _socket ?? throw new InvalidOperationException("MOSS-Live 尚未连接");
        var buffer = new byte[1024 * 64];
        using var ms = new System.IO.MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            ms.Write(buffer, 0, result.Count);
            if (result.EndOfMessage) return Encoding.UTF8.GetString(ms.ToArray());
            if (ms.Length > 8 * 1024 * 1024)
                throw new InvalidOperationException("MOSS-Live 单帧消息超过 8MB");
        }
    }

    public async Task CloseAsync(CancellationToken ct)
    {
        var socket = _socket;
        _socket = null;
        if (socket == null) return;
        try
        {
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "engine shutdown", ct).ConfigureAwait(false);
        }
        catch
        {
            // 关闭失败时直接释放。
        }
        finally
        {
            socket.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await CloseAsync(CancellationToken.None).ConfigureAwait(false);
    }
}

/// <summary>MOSS-Live 全双工客户端：连接、会话、音频/文本上行、事件下行、自动重连。</summary>
public sealed class MossLiveClient : IAsyncDisposable
{
    private static readonly JsonSerializerOptions TxOptions = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private readonly MossLiveConfig _config;
    private readonly IMossLiveTransport _transport;
    private readonly bool _ownsTransport;
    private readonly ConcurrentQueue<MossServerEvent> _events = new();
    private CancellationTokenSource? _sessionCts;
    private Task? _receiveLoop;
    private MossSessionInfo? _session;
    private int _audioSeq;
    private volatile MossConnectionState _state = MossConnectionState.Disconnected;

    public event Action<MossServerEvent>? EventReceived;
    public event Action<string>? ErrorReceived;

    public MossLiveClient(MossLiveConfig config, IMossLiveTransport? transport = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _ownsTransport = transport == null;
        _transport = transport ?? new WebSocketMossTransport();
    }

    public MossConnectionState State => _state;
    public string? LastError { get; private set; }
    public bool IsConnected => _state == MossConnectionState.Connected && _transport.IsConnected;

    /// <summary>连接并发送 session_start，随后启动接收循环（不阻塞）。</summary>
    public async Task StartAsync(MossSessionInfo session, CancellationToken ct = default)
    {
        if (_sessionCts != null) throw new InvalidOperationException("MOSS-Live 会话已启动");
        _sessionCts = new CancellationTokenSource();

        try
        {
            _state = MossConnectionState.Connecting;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, _sessionCts.Token);
            timeout.CancelAfter(TimeSpan.FromMilliseconds(Math.Max(1000, _config.ConnectTimeoutMs)));
            await _transport.ConnectAsync(new Uri(_config.Endpoint), timeout.Token).ConfigureAwait(false);
            _state = MossConnectionState.Connected;
            await SendSessionStartAsync(session, _sessionCts.Token).ConfigureAwait(false);
            _session = session;
            _receiveLoop = Task.Run(() => ReceiveLoopAsync(_sessionCts.Token));
        }
        catch (Exception ex)
        {
            _state = MossConnectionState.Disconnected;
            LastError = ex.Message;
            _sessionCts.Dispose();
            _sessionCts = null;
            throw;
        }
    }

    public async Task SendSessionStartAsync(MossSessionInfo session, CancellationToken ct = default)
    {
        var message = new MossClientMessage
        {
            Type = "session_start",
            Protocol = MossLiveProtocol.Version,
            NpcId = session.NpcId,
            Name = session.Name,
            Persona = session.Persona,
            Voice = session.Voice ?? _config.Voice,
            SampleRate = session.SampleRate > 0 ? session.SampleRate : _config.SampleRate,
            Channels = session.Channels > 0 ? session.Channels : _config.Channels,
            Capabilities = MossSessionInfo.DefaultCapabilities
        };
        await SendAsync(message, ct).ConfigureAwait(false);
    }

    /// <summary>上行麦克风 PCM16 帧（小端、单声道）。</summary>
    public Task SendAudioAsync(ReadOnlyMemory<byte> pcm16, CancellationToken ct = default)
    {
        var seq = Interlocked.Increment(ref _audioSeq);
        var message = new MossClientMessage
        {
            Type = "audio_in",
            Pcm16 = Convert.ToBase64String(pcm16.ToArray()),
            SampleRate = _config.SampleRate,
            Channels = _config.Channels,
            Seq = seq
        };
        return SendAsync(message, ct);
    }

    public Task SendTextAsync(string text, CancellationToken ct = default)
        => SendAsync(new MossClientMessage { Type = "text_in", Text = text }, ct);

    public Task SendInterruptAsync(CancellationToken ct = default)
        => SendAsync(new MossClientMessage { Type = "interrupt" }, ct);

    public Task SendActionResultAsync(string action, string status, float durationSeconds, string? message = null, CancellationToken ct = default)
        => SendAsync(new MossClientMessage
        {
            Type = "action_result",
            Action = action,
            Status = status,
            DurationSeconds = durationSeconds,
            Message = message
        }, ct);

    /// <summary>同步 NPC 世界状态（位置/情绪），供 MOSS-Live 场景理解。</summary>
    public Task SendContextAsync(string key, string value, CancellationToken ct = default)
        => SendAsync(new MossClientMessage { Type = "context", Text = key, Message = value }, ct);

    public bool TryDequeueEvent(out MossServerEvent evt)
    {
        if (_events.TryDequeue(out var item))
        {
            evt = item;
            return true;
        }
        evt = null!;
        return false;
    }

    private async Task SendAsync(MossClientMessage message, CancellationToken ct)
    {
        if (!IsConnected)
            throw new InvalidOperationException($"MOSS-Live 未连接（state={_state}）");
        var json = JsonSerializer.Serialize(message, TxOptions);
        await _transport.SendAsync(json, ct).ConfigureAwait(false);
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var payload = await _transport.ReceiveAsync(ct).ConfigureAwait(false);
                if (payload == null) break;

                MossServerEvent? evt;
                try
                {
                    evt = JsonSerializer.Deserialize<MossServerEvent>(payload, KimodoJson.Options);
                }
                catch (JsonException jx)
                {
                    LastError = $"MOSS-Live 事件解析失败: {jx.Message}";
                    ErrorReceived?.Invoke(LastError);
                    continue;
                }
                if (evt == null || string.IsNullOrEmpty(evt.Type)) continue;

                _events.Enqueue(evt);
                try { EventReceived?.Invoke(evt); } catch { /* 订阅者异常不得中断接收 */ }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                if (ct.IsCancellationRequested) break;
                LastError = ex.Message;
                ErrorReceived?.Invoke(ex.Message);
                if (!_config.AutoReconnect) break;

                _state = MossConnectionState.Reconnecting;
                try
                {
                    await Task.Delay(Math.Max(500, _config.ReconnectDelayMs), ct).ConfigureAwait(false);
                    await _transport.ConnectAsync(new Uri(_config.Endpoint), ct).ConfigureAwait(false);
                    _state = MossConnectionState.Connected;
                    if (_session != null)
                        await SendSessionStartAsync(_session, ct).ConfigureAwait(false);
                }
                catch (Exception rex)
                {
                    LastError = $"MOSS-Live 重连失败: {rex.Message}";
                    ErrorReceived?.Invoke(LastError);
                    break;
                }
            }
        }

        _state = MossConnectionState.Disconnected;
    }

    public async Task StopAsync()
    {
        var cts = _sessionCts;
        _sessionCts = null;
        _session = null;
        if (cts == null) return;
        cts.Cancel();
        await _transport.CloseAsync(CancellationToken.None).ConfigureAwait(false);
        if (_receiveLoop != null)
        {
            try { await _receiveLoop.ConfigureAwait(false); } catch { /* 忽略关闭异常 */ }
            _receiveLoop = null;
        }
        cts.Dispose();
        _state = MossConnectionState.Disconnected;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        if (_ownsTransport) await _transport.DisposeAsync().ConfigureAwait(false);
    }
}
