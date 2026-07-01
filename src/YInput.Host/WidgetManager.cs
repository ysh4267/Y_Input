using System.Drawing;
using System.Text.Json;
using System.Windows.Forms;
using YInput.Host.Services;

namespace YInput.Host;

/// <summary>
/// 고정(핀)된 매크로를 보더리스 위젯 창(<see cref="WidgetWindow"/>)으로 띄우고 수명을 관리한다.
/// WebView2 창은 UI(메시지 루프) 스레드에서만 만들 수 있어 <see cref="SynchronizationContext"/>로 마셜한다.
/// 열린 위젯 id는 <c>widgets.json</c>에 저장돼 다음 실행에 자동 복원된다.
/// </summary>
public sealed class WidgetManager
{
    private readonly SynchronizationContext _ui;
    private readonly string _baseUrl;
    private readonly string _userDataFolder;
    private readonly string _statePath;
    private readonly MacroService _service;
    private readonly Dictionary<string, WidgetWindow> _windows = new();
    private readonly object _gate = new();
    private int _cascade;

    // 위젯 모양(대기 상태 배경색 + 불투명도). 켜짐=파랑/재생=녹색은 위젯 페이지가 상태로 결정.
    private readonly string _appearancePath;
    private string _idleColor = "#1f232c"; private int _idleAlpha = 72; // 대기: 회색(card2)
    private string _onColor = "#243650"; private int _onAlpha = 72;     // 켜짐: 어두운 파랑
    private string _playColor = "#1f3d34"; private int _playAlpha = 72; // 재생: 어두운 녹색
    private int _blur = 1; // 블러 강도: 1=약함(BlurBehind) / 2=강함(Acrylic)

    public WidgetManager(SynchronizationContext ui, string baseUrl, string dataRoot, MacroService service)
    {
        _ui = ui;
        _baseUrl = baseUrl.TrimEnd('/');
        _userDataFolder = Path.Combine(dataRoot, "webview2");
        _statePath = Path.Combine(dataRoot, "widgets.json");
        _appearancePath = Path.Combine(dataRoot, "widget-config.json");
        _service = service;
        LoadAppearance();
    }

    // ---- 모양 설정: 상태별(대기/켜짐/재생) 배경색 + 알파(0=완전 투명=블러만) ----
    public object Appearance() => new
    {
        idleColor = _idleColor, idleAlpha = _idleAlpha,
        onColor = _onColor, onAlpha = _onAlpha,
        playColor = _playColor, playAlpha = _playAlpha,
        blur = _blur,
    };

    public void SetAppearance(string? idleColor, int? idleAlpha, string? onColor, int? onAlpha, string? playColor, int? playAlpha, int? blur)
    {
        if (!string.IsNullOrWhiteSpace(idleColor)) _idleColor = idleColor!.Trim();
        if (idleAlpha is int ia) _idleAlpha = Math.Clamp(ia, 0, 100);
        if (!string.IsNullOrWhiteSpace(onColor)) _onColor = onColor!.Trim();
        if (onAlpha is int oa) _onAlpha = Math.Clamp(oa, 0, 100);
        if (!string.IsNullOrWhiteSpace(playColor)) _playColor = playColor!.Trim();
        if (playAlpha is int pa) _playAlpha = Math.Clamp(pa, 0, 100);
        if (blur is int bl) _blur = bl == 2 ? 2 : 1;
        try { File.WriteAllText(_appearancePath, JsonSerializer.Serialize(Appearance())); } catch { /* 무시 */ }
        _service.BroadcastWidgetConfig(Appearance()); // 열린 위젯들 실시간 갱신
    }

    private void LoadAppearance()
    {
        try
        {
            if (!File.Exists(_appearancePath)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(_appearancePath));
            var r = doc.RootElement;
            string Str(string k, string d) => r.TryGetProperty(k, out var v) && v.GetString() is { Length: > 0 } s ? s : d;
            int Int(string k, int d) => r.TryGetProperty(k, out var v) && v.TryGetInt32(out var n) ? Math.Clamp(n, 0, 100) : d;
            _idleColor = Str("idleColor", _idleColor); _idleAlpha = Int("idleAlpha", _idleAlpha);
            _onColor = Str("onColor", _onColor); _onAlpha = Int("onAlpha", _onAlpha);
            _playColor = Str("playColor", _playColor); _playAlpha = Int("playAlpha", _playAlpha);
            if (r.TryGetProperty("blur", out var bv) && bv.TryGetInt32(out var bn)) _blur = bn == 2 ? 2 : 1;
        }
        catch { /* 기본값 유지 */ }
    }

    public IReadOnlyList<string> OpenIds() { lock (_gate) return _windows.Keys.ToArray(); }

    public void Open(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return;
        _ui.Post(_ => OpenOnUi(id), null);
    }

    public void Close(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return;
        _ui.Post(_ =>
        {
            WidgetWindow? w; lock (_gate) _windows.TryGetValue(id, out w);
            try { w?.Close(); } catch { /* 무시 */ }
        }, null);
    }

    public void CloseAll()
    {
        try
        {
            _ui.Send(_ =>
            {
                WidgetWindow[] all; lock (_gate) all = _windows.Values.ToArray();
                foreach (var w in all) try { w.Close(); } catch { /* 무시 */ }
            }, null);
        }
        catch { /* 종료 중 컨텍스트 없음 등 무시 */ }
    }

    /// <summary>지난 세션에 열려 있던 위젯 복원(메시지 루프 시작 후 생성됨).</summary>
    public void RestoreSaved()
    {
        List<string> ids;
        try { ids = File.Exists(_statePath) ? (JsonSerializer.Deserialize<List<string>>(File.ReadAllText(_statePath)) ?? new()) : new(); }
        catch { ids = new(); }
        foreach (var id in ids.Distinct()) Open(id);
    }

    // ---- UI 스레드에서만 ----
    private void OpenOnUi(string id)
    {
        lock (_gate) { if (_windows.TryGetValue(id, out var exists)) { try { exists.Activate(); } catch { } return; } }
        try
        {
            var loc = new Point(90 + (_cascade % 6) * 30, 90 + (_cascade % 6) * 26); _cascade++;
            var url = $"{_baseUrl}/widget.html?id={Uri.EscapeDataString(id)}";
            var w = new WidgetWindow(id, url, _userDataFolder, loc, msg => _service.Log("error", msg));
            w.FormClosed += (_, _) => { lock (_gate) _windows.Remove(id); Persist(); Broadcast(); };
            lock (_gate) _windows[id] = w;
            w.Show();
            Persist();
            Broadcast();
        }
        catch (Exception ex)
        {
            _service.Log("error", "위젯 창 열기 실패(WebView2 런타임을 확인하세요): " + ex.Message);
        }
    }

    private void Persist()
    {
        try { List<string> ids; lock (_gate) ids = _windows.Keys.ToList(); File.WriteAllText(_statePath, JsonSerializer.Serialize(ids)); }
        catch { /* 무시 */ }
    }

    private void Broadcast() => _service.BroadcastWidgets(OpenIds());
}
