using System.Drawing;
using System.Text.Json;
using YInput.Core.Models;
using YInput.Engine;
using YInput.Host.Services;
using YInput.Input;

namespace YInput.Host;

/// <summary>오버레이 설정(파일 영속 대상).</summary>
public sealed class OverlaySettings
{
    public bool Enabled { get; set; } = true;
    public bool Debug { get; set; } = true;              // 디버그 섹션(진행 단계·송출 키, 좌하단) 표시
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
    private readonly Dictionary<string, double> _outerMono = new(); // 외부 원: 단조 증가(뒤로 안 감)
    private readonly Dictionary<string, int> _lastLoop = new();     // 상위 루프 추적 → 루프 바뀌면 싱크 재정렬
    private readonly System.Threading.Timer _pump; // 재생 중 ~30fps 렌더 펌프(이벤트 코얼레싱)
    private volatile bool _pumpOn;

    private readonly InputBackend? _backend;
    // 디버그 — 매크로가 송출한 최근 키 입력(오버레이 디버그 섹션 표시용)
    private readonly LinkedList<string> _sentKeys = new();
    private const int MaxSentKeys = 10;
    // 디버그 — 위치 보정·룬 사용 진행 단계/결과(PositionWatcher.StatusChanged 구독)
    private readonly LinkedList<string> _stageLines = new();
    private const int MaxStageLines = 6;

    /// <summary>위치 보정·룬 사용의 진행 단계·결과를 디버그 섹션에 표시(Program이 연결).</summary>
    public void OnWatcherStatus(string state, string message)
    {
        if (message.Length > 52) message = message[..52] + "…";
        string line = $"{DateTime.Now:HH:mm:ss.f} [{state}] {message}";
        bool dbg;
        lock (_gate)
        {
            _stageLines.AddLast(line);
            while (_stageLines.Count > MaxStageLines) _stageLines.RemoveFirst();
            dbg = _settings.Debug;
        }
        if (dbg && !_pumpOn) PushRows();
    }

    public OverlayController(SynchronizationContext ui, string dataRoot, SocketHub hub, MacroService service, ProgressBroadcaster progress, InputBackend? backend = null)
    {
        _ui = ui;
        _statePath = Path.Combine(dataRoot, "overlay.json");
        _hub = hub;
        _service = service;
        _progress = progress;
        _backend = backend;
        _settings = Load();

        _progress.Progressed += OnProgress;
        _progress.Ended += OnEnded;
        _service.StatusChanged += OnStatus;
        if (_backend is not null) _backend.Sent += OnInputSent;
        _pump = new System.Threading.Timer(_ => PumpTick(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>매크로가 송출한 키를 디버그 목록에 기록 — 룬 퍼즐 오입력 등 원인 추적용.
    /// 렌더는 펌프(~30fps)가 코얼레싱하고, 펌프가 꺼져 있으면 즉시 1회 반영한다.</summary>
    private void OnInputSent(object? sender, InputEvent e)
    {
        if (e is not KeyboardEvent ke) return; // 키보드만 — 마우스 이동 등은 노이즈
        string line = $"{DateTime.Now:HH:mm:ss.fff}  {KeyLabel(ke.Code, (ke.State & 0x02) != 0)} {(ke.IsKeyUp ? "뗌" : "누름")}";
        bool dbg;
        lock (_gate)
        {
            _sentKeys.AddLast(line);
            while (_sentKeys.Count > MaxSentKeys) _sentKeys.RemoveFirst();
            dbg = _settings.Debug;
        }
        if (dbg && !_pumpOn) PushRows();
    }

    /// <summary>스캔코드(set 1) → 표시 이름. 흔한 키만 이름, 나머지는 SC 표기.</summary>
    private static string KeyLabel(ushort sc, bool e0)
    {
        if (e0) return sc switch
        {
            0x48 => "↑", 0x50 => "↓", 0x4B => "←", 0x4D => "→",
            0x1C => "NumEnter", 0x1D => "RCtrl", 0x38 => "RAlt",
            0x52 => "Ins", 0x53 => "Del", 0x47 => "Home", 0x4F => "End", 0x49 => "PgUp", 0x51 => "PgDn",
            _ => $"E0-{sc:X2}",
        };
        return sc switch
        {
            0x01 => "Esc", 0x0E => "BS", 0x0F => "Tab", 0x1C => "Enter", 0x1D => "Ctrl",
            0x2A => "Shift", 0x36 => "RShift", 0x38 => "Alt", 0x39 => "Space", 0x3A => "Caps",
            >= 0x3B and <= 0x44 => "F" + (sc - 0x3A), 0x57 => "F11", 0x58 => "F12",
            0x02 => "1", 0x03 => "2", 0x04 => "3", 0x05 => "4", 0x06 => "5",
            0x07 => "6", 0x08 => "7", 0x09 => "8", 0x0A => "9", 0x0B => "0",
            0x10 => "Q", 0x11 => "W", 0x12 => "E", 0x13 => "R", 0x14 => "T",
            0x15 => "Y", 0x16 => "U", 0x17 => "I", 0x18 => "O", 0x19 => "P",
            0x1E => "A", 0x1F => "S", 0x20 => "D", 0x21 => "F", 0x22 => "G",
            0x23 => "H", 0x24 => "J", 0x25 => "K", 0x26 => "L",
            0x2C => "Z", 0x2D => "X", 0x2E => "C", 0x2F => "V", 0x30 => "B", 0x31 => "N", 0x32 => "M",
            _ => $"SC{sc:X2}",
        };
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
                var w = new OverlayWindow() { Location = new Point(-10000, -10000) };
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
        try { if (_backend is not null) _backend.Sent -= OnInputSent; } catch { }
        try { _pump.Dispose(); } catch { }
        try { _ui.Send(_ => { try { _window?.Close(); } catch { } _window = null; }, null); } catch { }
    }

    public OverlaySettings SetEnabled(bool enabled)
    {
        OverlaySettings snap;
        lock (_gate) { _settings.Enabled = enabled; snap = Clone(_settings); }
        Save(snap); Broadcast(snap); ApplyArm();
        return snap;
    }

    /// <summary>디버그 섹션(진행 단계·송출 키) 표시 토글 — 꺼면 즉시 패널을 지우고 본문만 남긴다.
    /// 기록(링 버퍼)은 계속 쌓이므로 다시 켜면 최근 내역이 바로 보인다.</summary>
    public OverlaySettings SetDebug(bool debug)
    {
        OverlaySettings snap;
        lock (_gate) { _settings.Debug = debug; snap = Clone(_settings); }
        Save(snap); Broadcast(snap); PushRows();
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

    // ---------- 데이터 이벤트 ----------
    private void OnStatus()
    {
        RefreshMacros();
        var playing = new HashSet<string>(_service.PlayingIds());
        lock (_gate)
        {
            var old = _playing;
            _playing = playing;
            foreach (var id in playing) if (!old.Contains(id)) { _outerMono[id] = 0; _lastLoop.Remove(id); } // 새로 시작 → 리셋
            foreach (var id in _prog.Keys.ToList()) if (!playing.Contains(id)) _prog.Remove(id);
            foreach (var id in _delay.Keys.ToList()) if (!playing.Contains(id)) _delay.Remove(id);
            foreach (var id in _outerMono.Keys.ToList()) if (!playing.Contains(id)) _outerMono.Remove(id);
            foreach (var id in _lastLoop.Keys.ToList()) if (!playing.Contains(id)) _lastLoop.Remove(id);
        }
        ApplyArm();
        EnsurePump();
        PushRows(); // 상태 변화(재생 시작/정지)는 즉시 1회 반영
    }

    private void OnProgress(string id, PlaybackProgress p)
    {
        lock (_gate)
        {
            _prog[id] = (p.StepIndex, p.StepCount, p.Loop);
            if (p.DelayMs > 0) _delay[id] = (Environment.TickCount64, p.DelayMs);
            else _delay.Remove(id);

            // 외부 원 = 진행도 타이머(앞으로만). 유한 반복이면 (loop+스텝비율)/loopCount, 무한이면 한 바퀴 기준.
            // 상위 루프가 바뀌면 싱크를 재정렬한다(유한: 그 루프 시작값으로, 무한: 0으로) — 반복이 쌓여 100%에
            // 붙어버리는 desync를 막는다. 같은 루프 안에서는 최댓값 유지로 인라인 반복에도 뒤로 가지 않는다.
            int lc = 0;
            foreach (var e in _enabled) if (e.id == id) { lc = e.loopCount; break; }
            if (_lastLoop.TryGetValue(id, out var ll) && ll != p.Loop)
                _outerMono[id] = lc > 0 ? Math.Clamp((double)p.Loop / lc, 0, 1) : 0;
            _lastLoop[id] = p.Loop;
            double within = p.StepCount > 0 ? (double)p.StepIndex / p.StepCount : 0;
            double raw = Math.Clamp(lc > 0 ? (p.Loop + within) / lc : within, 0, 1);
            _outerMono[id] = Math.Max(_outerMono.TryGetValue(id, out var prev) ? prev : 0, raw);
        }
        EnsurePump(); // 렌더는 펌프(~30fps)가 코얼레싱 — 진행 이벤트마다 그리지 않음
    }

    private void OnEnded(string id)
    {
        lock (_gate) { _prog.Remove(id); _delay.Remove(id); _outerMono.Remove(id); _lastLoop.Remove(id); }
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

    // ---------- 렌더 펌프(재생 중 ~30fps로 코얼레싱) ----------
    private void EnsurePump()
    {
        if (_pumpOn) return;
        bool playing; lock (_gate) playing = _playing.Count > 0;
        if (!playing) return;
        _pumpOn = true;
        try { _pump.Change(0, 33); } catch { }
    }

    private void PumpTick()
    {
        bool playing; lock (_gate) playing = _playing.Count > 0;
        if (!playing)
        {
            _pumpOn = false;
            try { _pump.Change(Timeout.Infinite, Timeout.Infinite); } catch { }
            PushRows(); // 마지막 프레임 반영
            return;
        }
        PushRows(); // 창이 동일 프레임이면 알아서 스킵
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
        List<string> keys, stages;
        lock (_gate)
        {
            // 디버그 꺼짐 → 빈 목록 전달(창은 디버그 패널을 아예 그리지 않고 본문을 다시 세로 중앙 정렬)
            bool dbg = _settings.Debug;
            keys = dbg ? _sentKeys.ToList() : new();
            stages = dbg ? _stageLines.ToList() : new();
        }
        _ui.Post(_ => { if (_window is null) return; _window.SetDebugInfo(stages, keys); _window.SetRows(rows); }, null);
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
                    outer = _outerMono.TryGetValue(id, out var mo) ? mo : (pr.stepCount > 0 ? (double)pr.stepIndex / pr.stepCount : 0);
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
        _hub.Broadcast("overlaySettings", new { enabled = s.Enabled, debug = s.Debug, whitelist = s.Whitelist, blacklist = s.Blacklist });

    private static string Normalize(string? t)
    {
        t = (t ?? "").Trim().ToLowerInvariant();
        if (t.EndsWith(".exe", StringComparison.Ordinal)) t = t[..^4];
        return t;
    }

    private static OverlaySettings Clone(OverlaySettings s) =>
        new() { Enabled = s.Enabled, Debug = s.Debug, Whitelist = new(s.Whitelist), Blacklist = new(s.Blacklist) };
}
