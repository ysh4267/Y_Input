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

    public WidgetManager(SynchronizationContext ui, string baseUrl, string dataRoot, MacroService service)
    {
        _ui = ui;
        _baseUrl = baseUrl.TrimEnd('/');
        _userDataFolder = Path.Combine(dataRoot, "webview2");
        _statePath = Path.Combine(dataRoot, "widgets.json");
        _service = service;
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
