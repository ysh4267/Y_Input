using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using YInput.Core.Persistence;

namespace YInput.Host.Services;

/// <summary>연결된 WebSocket 클라이언트들에게 상태/로그/진행 메시지를 브로드캐스트한다.</summary>
public sealed class SocketHub
{
    private readonly ConcurrentDictionary<Guid, WebSocket> _sockets = new();

    public async Task HandleAsync(WebSocket socket, CancellationToken appStopping)
    {
        var id = Guid.NewGuid();
        _sockets[id] = socket;
        try
        {
            // 수신은 사용하지 않지만, 연결 유지/종료 감지를 위해 읽어준다.
            var buffer = new byte[1024];
            while (socket.State == WebSocketState.Open && !appStopping.IsCancellationRequested)
            {
                var result = await socket.ReceiveAsync(buffer, appStopping);
                if (result.MessageType == WebSocketMessageType.Close) break;
            }
        }
        catch
        {
            // 연결 끊김 등 — 정리만 하고 무시
        }
        finally
        {
            _sockets.TryRemove(id, out _);
        }
    }

    /// <summary>type+data 봉투로 감싸 모든 클라이언트에 전송(논블로킹).</summary>
    public void Broadcast(string type, object data)
    {
        var envelope = new { type, data };
        var json = JsonSerializer.Serialize(envelope, MacroStore.Options);
        var bytes = Encoding.UTF8.GetBytes(json);
        _ = BroadcastBytesAsync(bytes);
    }

    private async Task BroadcastBytesAsync(byte[] bytes)
    {
        foreach (var (id, socket) in _sockets)
        {
            if (socket.State != WebSocketState.Open) continue;
            try
            {
                await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);
            }
            catch
            {
                _sockets.TryRemove(id, out _);
            }
        }
    }
}
