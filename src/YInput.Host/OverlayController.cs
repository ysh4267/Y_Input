using System.Drawing;
using System.Text.Json;
using YInput.Engine;
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
/// 인게임 오버레이(GDI+ 레이어드 <see cref="OverlayWindow"/>)의 수명·설정·데이터 공급을 관리한다.
/// 진행도/상태를 C#에서 직접 구독해(웹 불필요) 창에 그릴 행을 만들어 넘긴다. 창은 UI 스레드 전용이라
/// <see cref="SynchronizationContext"/>로 마셜한다. 설정은 <c>overlay.json</c>에 저장, 변경 시 <c>overlaySettings</c> 방송.
/// </summary>
public sealed class OverlayController : IDisposable
{
    private readonly SynchronizationContext _ui;
    private readonly string _statePath;
    private readonly SocketHub _hub;
    private readonly MacroService _service;
    private readonly ProgressBroadcaster _progress;
    private readonly object _gate = new();

    private OverlaySettings _settings;
    private OverlayWindow? _window;

    // 데이터 상태
    private List<(string id, string name, int loopCount)> _enabled = new();
    private HashSet<string> _playing = new();
    private readonly Dictionary<string, (int stepIndex, int stepCount, int loop)> _prog = new();
    private readonly Dictionary<string, (long startMs, double durMs)> _delay = new();
    private readonly System.Threading.Timer _anim;
    private volatile bool _animOn;

    public OverlayController(SynchronizationContext ui, string dataRoot, SocketHub hub, MacroService service, ProgressBroadcaster progress)
    {
        _ui = ui;
        _statePath = Path.Combine(dataRoot, "overlay.json");
        _hub = hub;
        _service = service;
        _progress = progress;
        _settings = Load();

        _progress.Progressed += OnProgress;
        _progress.Ended += OnEnded;
        _service.StatusChanged += OnStatus;
        _anim = new System.Threading.Timer(_ => AnimTick(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public OverlaySettings Get() { lock (_gate) return Clone(_settings); }

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
                var w = new OverlayWindow(OnGameDetected) { Location = new Point(-10000, -10000) };
                _window = w;
                w.Show(); w.Hide(); // 핸들 생성 후 숨김
            }
            catch (Exception ex) { OnError("오버레이 창 생성 실패: " + ex.Message); }
        }, null);
        RefreshMacros();
        PushLists();
        PushRows();
        ApplyArm();
    }

    public void Close() => Dispose();

    public void Dispose()
    {
        try { _progress.Progressed -= OnProgress; } catch { }
        try { _progress.Ended -= OnEnded; } catch { }
        try { _service.StatusChanged -= OnStatus; } catch { }
        try { _anim.Dispose(); } catch { }
        try { _ui.Send(_ => { try { _window?.Close(); } catch { } _window = null; }, null); } catch { }
    }

    public OverlaySettings SetEnabled(bool enabled)
    {
        OverlaySettings snap;
        lock (_gate) { _settings.Enabled = enabled; snap = Clone(_settings); }
        Save(snap); Broadcast(snap); ApplyArm();
        return snap;
    }

    public OverlaySettings WhitelistAdd(string process) => Mutate(process, add: true);
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
        if (p.Length > 0) { Save(snap); PushLists(); Broadcast(snap); }
        return snap;
    }

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
        Save(snap); PushLists(); Broadcast(snap);
    }

    // ---------- 데이터 이벤트 ----------
    private void OnStatus()
    {
        RefreshMacros();
        var playing = new HashSet<string>(_service.PlayingIds());
        lock (_gate)
        {
            _playing = playing;
            foreach (var id in _prog.Keys.ToList()) if (!playing.Contains(id)) _prog.Remove(id);
            foreach (var id in _delay.Keys.ToList()) if (!playing.Contains(id)) _delay.Remove(id);
        }
        ApplyArm();
        PushRows();
    }

    private void OnProgress(string id, PlaybackProgress p)
    {
        lock (_gate)
        {
            _prog[id] = (p.StepIndex, p.StepCount, p.Loop);
            if (p.DelayMs > 0) _delay[id] = (Environment.TickCount64, p.DelayMs);
            else _delay.Remove(id);
        }
        EnsureAnim();
        PushRows();
    }

    private void OnEnded(string id)
    {
        lock (_gate) { _prog.Remove(id); _delay.Remove(id); }
        PushRows();
    }

    private void RefreshMacros()
    {
        try
        {
            var list = _service.ListMacros()
                .Where(m => m.Enabled)
                .OrderBy(m => m.Order)
                .Select(m => (m.Id, m.Name, m.LoopCount))
                .ToList();
            lock (_gate) _enabled = list;
        }
        catch { /* 무시 */ }
    }

    // ---------- 애니메이션(딜레이 채움) ----------
    private void EnsureAnim()
    {
        if (_animOn) return;
        if (!HasActiveDelay()) return;
        _animOn = true;
        try { _anim.Change(0, 33); } catch { }
    }

    private void AnimTick()
    {
        if (HasActiveDelay()) { PushRows(); return; }
        _animOn = false;
        try { _anim.Change(Timeout.Infinite, Timeout.Infinite); } catch { }
        PushRows(); // 마지막 프레임(원 가득 참) 반영
    }

    private bool HasActiveDelay()
    {
        long now = Environment.TickCount64;
        lock (_gate) return _delay.Values.Any(d => d.durMs > 0 && now - d.startMs < d.durMs);
    }

    // ---------- 창에 반영 ----------
    private void ApplyArm()
    {
        bool armed; lock (_gate) armed = _settings.Enabled && _enabled.Count > 0;
        _ui.Post(_ => _window?.SetArmed(armed), null);
    }

    private void PushRows()
    {
        var rows = BuildRows();
        _ui.Post(_ => _window?.SetRows(rows), null);
    }

    private List<OverlayRow> BuildRows()
    {
        long now = Environment.TickCount64;
        var rows = new List<OverlayRow>();
        lock (_gate)
        {
            foreach (var (id, name, loopCount) in _enabled)
            {
                bool playing = _playing.Contains(id);
                double outer = 0, inner = 0; int loop = 0;
                if (playing && _prog.TryGetValue(id, out var pr))
                {
                    outer = pr.stepCount > 0 ? (double)pr.stepIndex / pr.stepCount : 0;
                    loop = pr.loop;
                }
                if (playing && _delay.TryGetValue(id, out var d) && d.durMs > 0)
                    inner = Math.Clamp((now - d.startMs) / d.durMs, 0, 1);
                rows.Add(new OverlayRow(name, LoopText(loopCount, loop, playing), outer, inner, playing));
            }
        }
        return rows;
    }

    private static string LoopText(int loopCount, int loop, bool playing)
    {
        if (!playing) return loopCount <= 0 ? "↻" : $"×{loopCount}";
        int cur = loop + 1;
        return loopCount <= 0 ? $"{cur} ↻" : $"{cur}/{loopCount}";
    }

    private void PushLists()
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
        try { File.WriteAllText(_statePath, JsonSerializer.Serialize(s)); } catch { }
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
