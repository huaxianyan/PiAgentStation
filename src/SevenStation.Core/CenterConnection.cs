using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using SevenStation.Shared;

namespace SevenStation.Core;

/// <summary>一条客户端连接。命令的应答回到这条连接，会话事件广播给所有连接。</summary>
public sealed class CenterConnection
{
    private const int MaxMessageBytes = 8 * 1024 * 1024;

    private readonly WebSocket _socket;
    private readonly CenterHub _hub;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly ConcurrentDictionary<string, byte> _inflight = new();
    private int _closed;

    public string Id { get; } = "c-" + Guid.NewGuid().ToString("N")[..8];

    public CenterConnection(WebSocket socket, CenterHub hub)
    {
        _socket = socket;
        _hub = hub;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        _hub.Join(this);
        Log.Write($"客户端连上 {Id}");
        try
        {
            var buffer = new byte[64 * 1024];
            using var message = new MemoryStream();

            while (_socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                var result = await _socket.ReceiveAsync(buffer, cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }

                message.Write(buffer, 0, result.Count);
                if (message.Length > MaxMessageBytes)
                {
                    Log.Write($"客户端 {Id} 单条消息超过 {MaxMessageBytes} 字节，断开");
                    break;
                }

                if (!result.EndOfMessage)
                {
                    continue;
                }

                var json = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
                message.SetLength(0);

                JsonObject? command;
                try
                {
                    command = JsonNode.Parse(json) as JsonObject;
                }
                catch (Exception ex)
                {
                    Log.Write($"客户端 {Id} 发来无法解析的消息", ex);
                    continue;
                }

                if (command is not null)
                {
                    // 每条命令独立跑，慢命令不挡住后面的消息。
                    _ = Task.Run(() => _hub.HandleAsync(command, this), CancellationToken.None);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 服务停机。
        }
        catch (WebSocketException ex)
        {
            Log.Write($"客户端 {Id} 连接中断：{ex.Message}");
        }
        finally
        {
            _hub.Leave(this);
            await CloseQuietlyAsync();
            Log.Write($"客户端断开 {Id}");
        }
    }

    /// <summary>发出即可，不等待。</summary>
    public void Send(JsonObject payload)
    {
        _ = SendAsync(payload);
    }

    public async Task SendAsync(JsonObject payload)
    {
        if (Volatile.Read(ref _closed) == 1 || _socket.State != WebSocketState.Open)
        {
            return;
        }

        var text = payload.ToJsonString(CenterHub.SerializerOptions);
        await _sendLock.WaitAsync();
        try
        {
            if (_socket.State != WebSocketState.Open)
            {
                return;
            }

            await _socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, CancellationToken.None);
        }
        catch (WebSocketException)
        {
            // 对端已经走了，RunAsync 的收循环会收尾。
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task CloseQuietlyAsync()
    {
        if (Interlocked.Exchange(ref _closed, 1) == 1)
        {
            return;
        }

        try
        {
            if (_socket.State == WebSocketState.Open || _socket.State == WebSocketState.CloseReceived)
            {
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
            }
        }
        catch
        {
            // 已经断了。
        }
        finally
        {
            _socket.Dispose();
        }
    }
}
