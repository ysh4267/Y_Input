using System.Drawing;
using System.Text.Json;
using System.Windows.Forms;
using YInput.Host.Services;

namespace YInput.Host;

/// <summary>오버레이 설정(파일 영속 대상).</summary>
public sealed class OverlaySettings
{
    public bool Enabled { get; set; } = true;
    public List<string> Whitelist { get; set; } = new(); // 표시할 프로세스명(게임 감지 시 자동 추가)
    public List<string> Blacklist { get; set; } = new(); // 제외할 프로세스명(목록에서 빼면 자동 추가)
}

/// <summary>
/// 인게임 오버레이 창(<see cref="OverlayWindow"/>)의 수명·설정·대상 창(화이트/블랙리스트)을 관리한다.
/// WebView2 창은 UI(메시지 루프) 스레드에서만 다룰 수 있어 <see cref="SynchronizationContext"/>로 마셜한다.
/// 설정은 <c>overlay.json</c>에 저장되고 변경 시 <c>overlaySettings</c> 이벤트로 방송한다.
///
/// 표시 여부: (웹 무장 = 활성화 매크로 있음) &amp;&amp; (포그라운드 프로세스가 화이트리스트 or 자동감지 게임, 블랙 제외).
/// 게임 자동감지 시 그 프로세스를 화이트리스트에 자동 추가한다.
/// </summary>
public sealed class OverlayController
{
    private readonly SynchronizationContext _ui;
    private readonly string _url;
    private readonly string _userDataFolder;
    private readonly string _statePath;
    private readonly SocketHub _hub;
    private readonly object _gate = new();

    private OverlaySettings _settings;
    private OverlayWindow? _window;

    public OverlayController(SynchronizationContext ui, string baseUrl, string dataRoot, SocketHub hub)
    {
        _ui = ui;
        _url = baseUrl.TrimEnd('/') + "/overlay.html";
        _userDataFolder = Path.Combine(dataRoot, "webview2"); // 위젯과 동일 폴더 공유
        _statePath = Path.Combine(dataRoot, "overlay.json");
        _hub = hub;
        _settings = Load();
    }

    public OverlaySettings Get() { lock (_gate) return Clone(_settings); }

    /// <summary>대상으로 고를 수 있는 실행 중 창(프로세스) 목록.</summary>
    public IReadOnlyList<OverlayWindowInfo> ListWindows()
    {
        try { return OverlayWindow.EnumerateWindows(); } catch { return Array.Empty<OverlayWindowInfo>(); }
    }

    public void Start()
    {
        _ui.Post(_ =>
        {
            if (_window is not null) return;
            try
            {
                var w = new OverlayWindow(_url, _userDataFolder, OnError, OnGameDetected)
                {
                    Location = new Point(-10000, -10000),
                };
                _window = w;
                w.Show();  // WebView2 초기화 트리거
                w.Hide();  // 무장(웹 신호) 전까지 숨김
                PushListsToWindow();
            }
            catch (Exception ex) { OnError("오버레이 창 생성 실패: " + ex.Message); }
        }, null);
    }

    public void Close()
    {
        try { _ui.Send(_ => { try { _window?.Close(); } catch { } _window = null; }, null); }
        catch { /* 종료 중 무시 */ }
    }

    public OverlaySettings SetEnabled(bool enabled)
    {
        OverlaySettings snap;
        lock (_gate) { _settings.Enabled = enabled; snap = Clone(_settings); }
        Save(snap); Broadcast(snap);
        // Enabled는 웹(overlay.js)이 overlaySettings로 받아 표시 의도를 다시 계산해 창을 무장/해제한다.
        return snap;
    }

    /// <summary>프로세스를 화이트리스트에 추가(블랙에서 제거).</summary>
    public OverlaySettings WhitelistAdd(string process) => Mutate(process, add: true);
    /// <summary>프로세스를 화이트리스트에서 제거 → 블랙리스트에 추가(자동감지가 다시 안 넣게).</summary>
    public OverlaySettings WhitelistRemove(string process) => Mutate(process, add: false);

    private OverlaySettings Mutate(string process, bool add)
    {
        var p = Normalize(process);
        OverlaySettings snap;
        lock (_gate)
        {
            _settings.Whitelist.RemoveAll(x => Normalize(x) == p);
            _settings.Blacklist.RemoveAll(x => Normalize(x) == p);
            if (add) _settings.Whitelist.Add(p); else _settings.Blacklist.Add(p);
            snap = Clone(_settings);
        }
        if (p.Length > 0) { Save(snap); PushListsToWindow(); Broadcast(snap); }
        return snap;
    }

    // 창이 게임을 자동 감지 → 화이트리스트에 자동 추가(블랙/화이트에 없을 때만).
    private void OnGameDetected(string process)
    {
        var p = Normalize(process);
        if (p.Length == 0) return;
        OverlaySettings snap;
        lock (_gate)
        {
            if (_settings.Blacklist.Any(x => Normalize(x) == p) || _settings.Whitelist.Any(x => Normalize(x) == p)) return;
            _settings.Whitelist.Add(p);
            snap = Clone(_settings);
        }
        Save(snap); PushListsToWindow(); Broadcast(snap);
    }

    private void PushListsToWindow()
    {
        OverlaySettings s; lock (_gate) s = Clone(_settings);
        _ui.Post(_ => _window?.SetLists(s.Whitelist, s.Blacklist), null);
    }

    private void OnError(string msg) => _hub.Broadcast("log", new { level = "error", message = msg, time = DateTime.Now.ToString("HH:mm:ss") });

    // ---------- 영속 ----------
    private OverlaySettings Load()
    {
        try { return File.Exists(_statePath) ? (JsonSerializer.Deserialize<OverlaySettings>(File.ReadAllText(_statePath)) ?? new()) : new(); }
        catch { return new(); }
    }

    private void Save(OverlaySettings s)
    {
        try { File.WriteAllText(_statePath, JsonSerializer.Serialize(s)); } catch { /* 무시 */ }
    }

    private void Broadcast(OverlaySettings s) =>
        _hub.Broadcast("overlaySettings", new { enabled = s.Enabled, whitelist = s.Whitelist, blacklist = s.Blacklist });

    private static string Normalize(string? t)
    {
        t = (t ?? "").Trim().ToLowerInvariant();
        if (t.EndsWith(".exe", StringComparison.Ordinal)) t = t[..^4];
        return t;
    }

    private static OverlaySettings Clone(OverlaySettings s) =>
        new() { Enabled = s.Enabled, Whitelist = new(s.Whitelist), Blacklist = new(s.Blacklist) };
}
