using System.Drawing;
using System.Text.Json;
using System.Windows.Forms;
using YInput.Host.Services;

namespace YInput.Host;

/// <summary>오버레이 설정(파일 영속 대상).</summary>
public sealed class OverlaySettings
{
    public bool Enabled { get; set; } = true;
}

/// <summary>
/// 인게임 오버레이 창(<see cref="OverlayWindow"/>) 하나의 수명·설정·가시성을 관리한다.
/// WebView2 창은 UI(메시지 루프) 스레드에서만 다룰 수 있어 <see cref="SynchronizationContext"/>로 마셜한다.
/// 설정은 <c>overlay.json</c>에 저장되고 변경 시 <c>overlaySettings</c> 이벤트로 방송한다.
///
/// 실효 표시 = 사용(Enabled) 중이고, 페이지가 계산한 표시 의도(<c>_webWantsShow</c> = 재생 중)일 때.
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
    private bool _webWantsShow;

    public OverlayController(SynchronizationContext ui, string baseUrl, string dataRoot, SocketHub hub)
    {
        _ui = ui;
        _url = baseUrl.TrimEnd('/') + "/overlay.html";
        _userDataFolder = Path.Combine(dataRoot, "webview2"); // 위젯과 동일 폴더 공유
        _statePath = Path.Combine(dataRoot, "overlay.json");
        _hub = hub;
        _settings = Load();
    }

    public OverlaySettings Get() { lock (_gate) return new OverlaySettings { Enabled = _settings.Enabled }; }

    /// <summary>메시지 루프 시작 후 호출 — 창을 만들어 초기화(숨김)한다.</summary>
    public void Start()
    {
        _ui.Post(_ =>
        {
            if (_window is not null) return;
            try
            {
                var w = new OverlayWindow(_url, _userDataFolder, OnError, OnWebShow)
                {
                    Location = new Point(-10000, -10000), // 초기화용 오프스크린(플래시 방지)
                };
                _window = w;
                w.Show();               // WebView2 초기화 트리거
                ApplyVisibilityOnUi();  // 초기 상태 반영(대개 숨김)
            }
            catch (Exception ex) { OnError("오버레이 창 생성 실패: " + ex.Message); }
        }, null);
    }

    public void Close()
    {
        try { _ui.Send(_ => { try { _window?.Close(); } catch { } _window = null; }, null); }
        catch { /* 종료 중 컨텍스트 없음 등 무시 */ }
    }

    /// <summary>켜기/끄기 설정. 저장·방송하고 가시성을 갱신한다.</summary>
    public OverlaySettings Set(bool? enabled)
    {
        OverlaySettings snap;
        lock (_gate) { if (enabled.HasValue) _settings.Enabled = enabled.Value; snap = new OverlaySettings { Enabled = _settings.Enabled }; }
        Save(snap);
        Broadcast(snap);
        _ui.Post(_ => ApplyVisibilityOnUi(), null);
        return snap;
    }

    // ---------- 창 콜백 ----------
    private void OnWebShow(bool show)
    {
        _webWantsShow = show;
        _ui.Post(_ => ApplyVisibilityOnUi(), null);
    }

    private void OnError(string msg) => _hub.Broadcast("log", new { level = "error", message = msg, time = DateTime.Now.ToString("HH:mm:ss") });

    // ---------- UI 스레드 ----------
    private void ApplyVisibilityOnUi()
    {
        if (_window is null) return;
        bool show;
        lock (_gate) show = _settings.Enabled && _webWantsShow;
        if (show) _window.ShowOverlay(); else _window.HideOverlay();
    }

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

    private void Broadcast(OverlaySettings s) => _hub.Broadcast("overlaySettings", new { enabled = s.Enabled });
}
