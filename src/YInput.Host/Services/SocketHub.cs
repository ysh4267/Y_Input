using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using YInput.Core.Persistence;

namespace YInput.Host.Services;

/// <summary>연결된 WebSocket 클라이언트들에게 상태/로그/진행 메시지를 브로드캐스트한다.
/// 각 연결은 전용 송신 큐(Channel) + 펌프 Task 1개를 가져 SendAsync가 절대 겹치지 않는다
/// (WebSocket은 같은 소켓에 동시 SendAsync를 허용하지 않음). 느린/죽은 클라이언트는
/// 자기 버퍼에서 DropOldest로만 영향받고 다른 클라이언트·브로드캐스트 스레드를 막지 못한다.</summary>
public sealed class SocketHub
{
    private readonly ConcurrentDictionary<Guid, Client> _clients = new();
    private int _mainClients; // 위젯이 아닌 웹 UI(브라우저 탭) 연결 수 — 단일 개체(중복 탭 방지) 판단용

    /// <summary>브라우저 웹 UI(위젯 제외)가 하나라도 연결돼 있는가.</summary>
    public bool HasMainClient => Volatile.Read(ref _mainClients) > 0;

    /// <summary>한 WebSocket 연결: 소켓 + 송신 채널. 송신은 펌프 1개가 전담한다.</summary>
    private sealed class Client
    {
        public WebSocket Socket { get; }
        public Channel<byte[]> Outbox { get; }

        public Client(WebSocket socket)
        {
            Socket = socket;
            // 용량 256, 가득 차면 가장 오래된 프레임을 버린다(느린 클라이언트가 막지 못하게).
            // 단일 리더(펌프 1개) → SendAsync 직렬화. 다중 라이터(여러 스레드 Broadcast).
            Outbox = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(256)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false,
            });
        }
    }

    public async Task HandleAsync(WebSocket socket, CancellationToken appStopping, bool isWidget = false)
    {
        var id = Guid.NewGuid();
        var client = new Client(socket);
        _clients[id] = client;
        if (!isWidget) Interlocked.Increment(ref _mainClients);

        // 송신 펌프: 이 소켓에서 SendAsync를 호출하는 유일한 경로.
        var pump = Task.Run(() => PumpAsync(client, appStopping));
        try
        {
            // 수신은 사용하지 않지만, 연결 유지/종료(브라우저 close, TCP 끊김) 감지를 위해 읽어준다.
            var buffer = new byte[1024];
            while (socket.State == WebSocketState.Open && !appStopping.IsCancellationRequested)
            {
                var result = await socket.ReceiveAsync(buffer, appStopping);
                if (result.MessageType == WebSocketMessageType.Close) break;
            }
        }
        catch
        {
            // 연결 끊김/Abort 등 — 정리만 하고 무시(펌프 실패 시 Abort()가 여기서 ReceiveAsync를 깨운다).
        }
        finally
        {
            _clients.TryRemove(id, out _);
            if (!isWidget) Interlocked.Decrement(ref _mainClients);
            client.Outbox.Writer.TryComplete();                  // 펌프 ReadAllAsync 종료 → 펌프 자연 종료
            try { await pump.ConfigureAwait(false); } catch { }   // 펌프 완료까지 대기(소켓이 펌프보다 먼저 dispose되지 않게)
            // socket.Dispose()는 호출하지 않는다 — /ws 엔드포인트의 `using var socket`이 소유(이중 Dispose 회피).
        }
    }

    /// <summary>채널에서 순차적으로 꺼내 SendAsync. 단일 펌프이므로 송신이 겹치지 않는다.
    /// 송신 실패 시 소켓을 Abort()해 수신 루프를 깨우고(→ 정리·브라우저 재연결) 펌프를 끝낸다.</summary>
    private static async Task PumpAsync(Client client, CancellationToken appStopping)
    {
        try
        {
            await foreach (var bytes in client.Outbox.Reader.ReadAllAsync(appStopping).ConfigureAwait(false))
            {
                if (client.Socket.State != WebSocketState.Open) break;
                await client.Socket.SendAsync(
                    bytes, WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }
        catch
        {
            // 송신 오류/취소 → 아래 finally에서 Abort()로 수신 루프를 깨운다.
        }
        finally
        {
            // 수신 루프(블로킹 ReceiveAsync)를 즉시 깨워 HandleAsync.finally가 정리하게 한다.
            // → TCP가 reset되어 브라우저가 close를 감지하고 재연결한다(반열림 방지).
            try { client.Socket.Abort(); } catch { /* ignore */ }
        }
    }

    /// <summary>type+data 봉투로 감싸 모든 클라이언트의 송신 큐에 넣는다(논블로킹, 절대 throw 안 함).</summary>
    public void Broadcast(string type, object data)
    {
        var envelope = new { type, data };
        var json = JsonSerializer.Serialize(envelope, MacroStore.Options);
        var bytes = Encoding.UTF8.GetBytes(json);

        foreach (var client in _clients.Values)
            client.Outbox.Writer.TryWrite(bytes); // 가득 차면 DropOldest로 가장 오래된 프레임을 버린다 — caller는 절대 안 막힘
    }
}
