using System.Drawing;
using System.Text.Json;
using System.Windows.Forms;
using YInput.Host.Services;

namespace YInput.Host;

/// <summary>오버레이 설정(파일 영속 대상). 값은 창 대비 좌상단 비율.</summary>
public sealed class OverlaySettings
{
    public bool Enabled { get; set; } = true;
    public bool HandleOn { get; set; }
    public double X { get; set; } = 0.03;
    public double Y { get; set; } = 0.35;
}

/// <summary>
/// 인게임 오버레이 창(<see cref="OverlayWindow"/>) 하나의 수명·설정·가시성을 관리한다.
/// WebView2 창은 UI(메시지 루프) 스레드에서만 다룰 수 있어 <see cref="SynchronizationContext"/>로 마셜한다.
/// 설정은 <c>overlay.json</c>에 저장되고 변경 시 <c>overlaySettings</c> 이벤트로 방송한다.
///
/// 실효 표시 = (페이지가 계산한) 표시 의도 <c>_webWantsShow</c>(= 사용 중 &amp;&amp; 재생 중) 또는 핸들 모드.
/// </summary>
public sealed class OverlayController
{
    private readonly SynchronizationContext _ui;
    private readonly string _url;
    private readonly string _userDataFolder;
    private readonly string _statePath;
    private readonly SocketHub _hub;
    private readonly object _gate = new();

    private OverlaySettings _settings = new();
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

    public OverlaySettings Get() { lock (_gate) return Clone(_settings); }

    /// <summary>메시지 루프 시작 후 호출 — 창을 만들어 초기화(숨김)한다.</summary>
    public void Start()
    {
        _ui.Post(_ =>
        {
            if (_window is not null) return;
            try
            {
                var w = new OverlayWindow(_url, _userDataFolder, OnError, OnDragSaved, OnWebShow)
                {
                    Location = new Point(-10000, -10000), // 초기화용 오프스크린(플래시 방지)
                };
                _window = w;
                w.Show();               // WebView2 초기화 트리거
                w.SetRatios(_settings.X, _settings.Y);
                ApplyVisibilityOnUi();  // 초기 상태 반영(대개 숨김)
                if (_settings.HandleOn) w.SetHandleMode(true);
            }
            catch (Exception ex) { OnError("오버레이 창 생성 실패: " + ex.Message); }
        }, null);
    }

    public void Close()
    {
        try { _ui.Send(_ => { try { _window?.Close(); } catch { } _window = null; }, null); }
        catch { /* 종료 중 컨텍스트 없음 등 무시 */ }
    }

    /// <summary>설정 일부 갱신(null이면 유지). 저장·방송하고 창에 반영한다.</summary>
    public OverlaySettings Set(bool? enabled, bool? handleOn, double? x, double? y)
    {
        OverlaySettings snap; bool handleChanged, ratioChanged;
        lock (_gate)
        {
            handleChanged = handleOn.HasValue && handleOn.Value != _settings.HandleOn;
            ratioChanged = (x.HasValue && x.Value != _settings.X) || (y.HasValue && y.Value != _settings.Y);
            if (enabled.HasValue) _settings.Enabled = enabled.Value;
            if (handleOn.HasValue) _settings.HandleOn = handleOn.Value;
            if (x.HasValue) _settings.X = Math.Clamp(x.Value, 0, 1);
            if (y.HasValue) _settings.Y = Math.Clamp(y.Value, 0, 1);
            snap = Clone(_settings);
        }
        Save(snap);
        Broadcast(snap);

        _ui.Post(_ =>
        {
            if (_window is null) return;
            if (ratioChanged) _window.SetRatios(snap.X, snap.Y);
            if (handleChanged) _window.SetHandleMode(snap.HandleOn);
            ApplyVisibilityOnUi();
        }, null);
        return snap;
    }

    // ---------- 창 콜백 ----------
    private void OnWebShow(bool show)
    {
        _webWantsShow = show;
        _ui.Post(_ => ApplyVisibilityOnUi(), null);
    }

    private void OnDragSaved(double x, double y)
    {
        OverlaySettings snap;
        lock (_gate) { _settings.X = x; _settings.Y = y; snap = Clone(_settings); }
        Save(snap);
        Broadcast(snap); // 브라우저 UI가 알 수 있게(위치 자체 컨트롤은 없지만 일관성 위해)
    }

    private void OnError(string msg) => _hub.Broadcast("log", new { level = "error", message = msg, time = DateTime.Now.ToString("HH:mm:ss") });

    // ---------- UI 스레드 ----------
    private void ApplyVisibilityOnUi()
    {
        if (_window is null) return;
        bool handle = _settings.HandleOn;
        bool show = handle || (_settings.Enabled && _webWantsShow);
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

    private void Broadcast(OverlaySettings s) =>
        _hub.Broadcast("overlaySettings", new { enabled = s.Enabled, handleOn = s.HandleOn, x = s.X, y = s.Y });

    private static OverlaySettings Clone(OverlaySettings s) =>
        new() { Enabled = s.Enabled, HandleOn = s.HandleOn, X = s.X, Y = s.Y };
}
