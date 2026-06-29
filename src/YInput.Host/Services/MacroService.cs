using YInput.Core.Models;
using YInput.Core.Persistence;
using YInput.Engine;
using YInput.Input;

namespace YInput.Host.Services;

public enum AppState { Idle, Recording, Playing }

/// <summary>
/// 녹화/재생/핫키/게임패드 상태를 묶어 조율하고, 변경 사항을 WebSocket으로 브로드캐스트한다.
/// 녹화와 재생은 상호 배타적(한 번에 하나).
/// </summary>
public sealed class MacroService
{
    private readonly InputBackend _backend;
    private readonly MacroLibrary _library;
    private readonly Player _player;
    private readonly Recorder _recorder;
    private readonly HotkeyManager _hotkeys;
    private readonly RawInputMonitor _rawInput;
    private readonly SocketHub _hub;

    private readonly object _gate = new();
    private readonly Dictionary<int, string> _hotkeyToMacro = new();
    private readonly List<(string macroId, GamepadControl control)> _gamepadTriggers = new();

    private AppState _state = AppState.Idle;
    private string? _currentMacroId;
    private volatile bool _listenActive;
    private volatile bool _monitorActive;

    /// <summary>웹 UI 주소(상태에 포함).</summary>
    public string Url { get; set; } = "";

    /// <summary>앱 종료 요청 콜백(트레이가 설정). /api/app/quit에서 호출.</summary>
    public Action? QuitRequested { get; set; }

    /// <summary>웹/스크립트에서 앱 종료를 요청한다(그레이스풀).</summary>
    public void RequestQuit()
    {
        Log("info", "종료 요청 수신 — 앱을 종료합니다.");
        QuitRequested?.Invoke();
    }

    public MacroService(InputBackend backend, MacroLibrary library, Player player,
                        Recorder recorder, HotkeyManager hotkeys, RawInputMonitor rawInput, SocketHub hub)
    {
        _backend = backend;
        _library = library;
        _player = player;
        _recorder = recorder;
        _hotkeys = hotkeys;
        _rawInput = rawInput;
        _hub = hub;

        _backend.GamepadInput += OnGamepadInput;
        _rawInput.Detected += OnRawDetected;

        _player.Stopped += (_, _) =>
        {
            lock (_gate)
            {
                if (_state == AppState.Playing) { _state = AppState.Idle; _currentMacroId = null; }
            }
            BroadcastStatus();
        };
        _player.Progress += (_, p) =>
            _hub.Broadcast("progress", new { loop = p.Loop, stepIndex = p.StepIndex, stepCount = p.StepCount });
        _player.Failed += (_, ex) => Log("error", "재생 오류: " + ex.Message);
        _recorder.StepRecorded += (_, step) =>
            // 실시간 카드 렌더용으로 전체 이벤트(@event)도 함께 보냄($type 포함 직렬화).
            _hub.Broadcast("recordedStep", new { summary = step.Event.Summary, delayBeforeMs = step.DelayBeforeMs, @event = step.Event });

        ReloadHotkeys();
    }

    // ---------- 상태 ----------
    public object GetStatusData()
    {
        var ds = DriverProvisioner.QueryStatus();
        string state = _state switch
        {
            AppState.Recording => "recording",
            AppState.Playing => "playing",
            _ => "idle",
        };
        return new
        {
            url = Url,
            state,
            currentMacroId = _currentMacroId,
            listening = _listenActive,
            monitoring = _monitorActive,
            driver = new { interception = ds.InterceptionInstalled, vigem = ds.ViGEmInstalled, admin = ds.IsAdministrator },
            backend = new
            {
                interceptionAvailable = _backend.InterceptionAvailable,
                keyboardReady = _backend.KeyboardReady,
                mouseReady = _backend.MouseReady,
                gamepadAvailable = _backend.GamepadAvailable,
                gamepadConnected = _backend.GamepadConnected,
                gamepadDevicePresent = _backend.GamepadDevicePresent,
            },
        };
    }

    public void BroadcastStatus() => _hub.Broadcast("status", GetStatusData());

    public void Log(string level, string message) =>
        _hub.Broadcast("log", new { level, message, time = DateTime.Now.ToString("HH:mm:ss") });

    // ---------- 녹화 ----------
    public void StartRecording(RecordOptions options)
    {
        lock (_gate)
        {
            if (_state != AppState.Idle)
                throw new InvalidOperationException("이미 녹화/재생 중입니다.");
            if (!_backend.InterceptionAvailable)
                throw new InvalidOperationException("Interception 드라이버가 준비되지 않았습니다(설치/재부팅 확인).");
            _recorder.Start(options);
            _state = AppState.Recording;
        }
        Log("info", "녹화를 시작했습니다. 입력을 기록합니다…");
        BroadcastStatus();
    }

    /// <summary>녹화 종료. persist=false면 라이브러리에 저장하지 않고 매크로만 반환(편집기 '녹화하기' 카드용).</summary>
    public Macro StopRecording(string? name, bool persist = true)
    {
        Macro macro;
        lock (_gate)
        {
            if (_state != AppState.Recording)
                throw new InvalidOperationException("녹화 중이 아닙니다.");
            var finalName = string.IsNullOrWhiteSpace(name) ? $"매크로 {DateTime.Now:MMdd-HHmmss}" : name!.Trim();
            macro = _recorder.Stop(finalName);
            if (persist) _library.Save(macro);
            _state = AppState.Idle;
        }
        ReloadHotkeys();
        Log("info", persist ? $"녹화 저장: {macro.Name} ({macro.Steps.Count} 스텝)" : $"녹화 종료 ({macro.Steps.Count} 스텝)");
        BroadcastStatus();
        return macro;
    }

    // ---------- 재생 ----------
    public void Play(string id)
    {
        Macro macro;
        lock (_gate)
        {
            if (_state != AppState.Idle)
                throw new InvalidOperationException("이미 녹화/재생 중입니다.");
            macro = _library.Load(id) ?? throw new FileNotFoundException("매크로를 찾을 수 없습니다: " + id);
            _state = AppState.Playing;
            _currentMacroId = id;
        }
        Log("info", $"재생 시작: {macro.Name}");
        BroadcastStatus();
        _ = _player.PlayAsync(ExpandMacro(macro)); // 매크로 참조를 평탄화한 뒤 재생. 완료 시 Stopped에서 Idle 복귀
    }

    public void StopPlayback() => _player.Stop();

    /// <summary>매크로 참조(MacroRefEvent)를 대상 매크로의 스텝으로 재귀 인라인 전개한다(순환/누락은 건너뜀).
    /// 반환 매크로는 최상위의 LoopCount/Speed를 유지하고 스텝만 평탄화된다(대상 매크로는 1사이클 실행).</summary>
    private Macro ExpandMacro(Macro top)
    {
        var flat = new List<MacroStep>();
        var path = new HashSet<string>();
        void Walk(Macro m)
        {
            foreach (var s in m.Steps)
            {
                if (s.Event is MacroRefEvent r)
                {
                    if (string.IsNullOrEmpty(r.MacroId)) continue;
                    if (path.Contains(r.MacroId)) { Log("warn", $"매크로 참조 순환 무시: {(string.IsNullOrEmpty(r.Name) ? r.MacroId : r.Name)}"); continue; }
                    var sub = _library.Load(r.MacroId);
                    if (sub is null) { Log("warn", $"참조 매크로 없음(건너뜀): {(string.IsNullOrEmpty(r.Name) ? r.MacroId : r.Name)}"); continue; }
                    if (s.DelayBeforeMs > 0) flat.Add(new MacroStep(new DelayEvent(), s.DelayBeforeMs)); // 참조 블록 선지연 보존
                    path.Add(r.MacroId);
                    Walk(sub);
                    path.Remove(r.MacroId);
                }
                else flat.Add(s);
            }
        }
        path.Add(top.Id);
        Walk(top);
        return new Macro
        {
            Id = top.Id, Name = top.Name, Steps = flat,
            LoopCount = top.LoopCount, SpeedMultiplier = top.SpeedMultiplier,
            RandomizeDelayPercent = top.RandomizeDelayPercent, Trigger = top.Trigger, Enabled = top.Enabled,
        };
    }

    /// <summary>주어진 매크로를 MacroRefEvent로 참조하는 다른 매크로들의 이름 목록(삭제 경고용).</summary>
    public IReadOnlyList<string> FindReferencing(string id)
    {
        var names = new List<string>();
        foreach (var m in _library.LoadAll())
        {
            if (m.Id == id) continue;
            if (m.Steps.Any(s => s.Event is MacroRefEvent r && r.MacroId == id))
                names.Add(m.Name);
        }
        return names;
    }

    // ---------- 게임패드 ----------
    public void ConnectGamepad()
    {
        _backend.ConnectGamepad();
        Log("info", "가상 게임패드를 연결했습니다.");
        BroadcastStatus();
    }

    public void DisconnectGamepad()
    {
        _backend.DisconnectGamepad();
        Log("info", "가상 게임패드를 해제했습니다.");
        BroadcastStatus();
    }

    /// <summary>게임패드 컨트롤 1개를 즉시 송출(테스트/수동 조작용).</summary>
    public void SendGamepad(GamepadControl control, int value) =>
        _backend.Send(new GamepadEvent { Control = control, Value = value });

    // ---------- 매크로 CRUD ----------
    public IReadOnlyList<Macro> ListMacros() => _library.LoadAll();

    public Macro? GetMacro(string id) => _library.Load(id);

    public void SaveMacro(Macro macro)
    {
        _library.Save(macro);
        ReloadHotkeys();
        Log("info", $"매크로 저장: {macro.Name}");
        BroadcastStatus();
    }

    public void DeleteMacro(string id)
    {
        RecycleBin.Delete(_library.PathFor(id));
        ReloadHotkeys();
        Log("info", "매크로를 휴지통으로 보냈습니다.");
        BroadcastStatus();
    }

    /// <summary>매크로의 적용(활성) 여부를 토글한다. ON이면 트리거 핫키 무장, OFF면 보관만.</summary>
    public void SetEnabled(string id, bool enabled)
    {
        var macro = _library.Load(id) ?? throw new FileNotFoundException("매크로를 찾을 수 없습니다: " + id);
        macro.Enabled = enabled;
        _library.Save(macro);
        ReloadHotkeys();
        Log("info", $"매크로 '{macro.Name}' 적용 {(enabled ? "켬" : "끔")}.");
        BroadcastStatus();
    }

    /// <summary>매크로의 반복 횟수를 설정한다(실행 페이지 목록에서 직접). 트리거에 영향 없음.</summary>
    public void SetPlayback(string id, int loopCount)
    {
        var macro = _library.Load(id) ?? throw new FileNotFoundException("매크로를 찾을 수 없습니다: " + id);
        macro.LoopCount = loopCount;
        _library.Save(macro);
    }

    /// <summary>매크로의 트리거 핫키를 설정/해제한다(실행 페이지 목록에서 직접 지정).</summary>
    public void SetTrigger(string id, Hotkey? trigger)
    {
        var macro = _library.Load(id) ?? throw new FileNotFoundException("매크로를 찾을 수 없습니다: " + id);
        macro.Trigger = trigger;
        _library.Save(macro);
        ReloadHotkeys();
        Log("info", $"매크로 '{macro.Name}' 트리거: {trigger?.ToString() ?? "없음"}");
        BroadcastStatus();
    }

    // ---------- 핫키 ----------
    public void ReloadHotkeys()
    {
        _hotkeys.UnregisterAll();
        _hotkeyToMacro.Clear();
        lock (_gate) _gamepadTriggers.Clear();
        foreach (var m in _library.LoadAll())
        {
            if (m.Enabled && m.Trigger is { IsEmpty: false } hk)
            {
                try
                {
                    string macroId = m.Id;
                    if (hk.IsGamepad)
                    {
                        lock (_gate) _gamepadTriggers.Add((macroId, hk.Gamepad!.Value));
                    }
                    else
                    {
                        // 마우스는 마우스 훅, 키보드는 단일/조합 모두 저수준 키보드 훅으로 감지.
                        // (RegisterHotKey는 게임/전체화면·풀스크린 포커스에서 누락되거나 다른 앱과 충돌해 등록 실패할 수 있어
                        //  저수준 훅이 더 안정적이며, 트리거 키를 가로채지 않고 통과시킨다.)
                        int id = hk.IsMouse
                            ? _hotkeys.RegisterMouse(hk, () => OnHotkey(macroId))
                            : _hotkeys.RegisterKeyChord(hk, () => OnHotkey(macroId));
                        _hotkeyToMacro[id] = macroId;
                    }
                }
                catch (Exception ex)
                {
                    Log("warn", $"핫키 등록 실패 ({m.Name} / {hk}): {ex.Message}");
                }
            }
        }
    }

    // ---------- 입력 감지(게임패드 트리거 / 아무 입력 잡기 / 입력 모니터) ----------
    private static bool IsPress(GamepadEvent e) => GamepadControls.KindOf(e.Control) switch
    {
        GamepadControlKind.Button => e.Value != 0,
        GamepadControlKind.Trigger => e.Value >= 128,
        _ => false, // 스틱 축은 트리거로 사용하지 않음
    };

    private void OnGamepadInput(object? sender, GamepadEvent e)
    {
        if (_monitorActive)
            _hub.Broadcast("inputMonitor", new { source = "gamepad", label = $"{e.Control}={e.Value}", time = DateTime.Now.ToString("HH:mm:ss") });

        if (_listenActive && IsPress(e))
        {
            if (TryConsumeListen())
                _hub.Broadcast("inputDetected", new { source = "gamepad", bindable = true, label = $"Pad {e.Control}", trigger = new { gamepad = e.Control.ToString() } });
            return;
        }

        if (IsPress(e))
        {
            List<string> hits;
            lock (_gate) hits = _gamepadTriggers.Where(t => t.control == e.Control).Select(t => t.macroId).ToList();
            foreach (var id in hits) OnHotkey(id);
        }
    }

    private void OnRawDetected(object? sender, DetectedInput d)
    {
        if (_monitorActive)
            _hub.Broadcast("inputMonitor", new { source = d.Kind.ToString().ToLowerInvariant(), label = d.Label, time = DateTime.Now.ToString("HH:mm:ss") });
    }

    /// <summary>게임패드 버튼을 트리거로 잡는 학습 모드 시작(다음 버튼 입력을 inputDetected로 전송).</summary>
    public void StartListen()
    {
        _listenActive = true;
        Log("info", "게임패드 버튼 입력 대기 중… 패드 버튼을 누르세요.");
        BroadcastStatus();
    }

    public void StopListen()
    {
        _listenActive = false;
        BroadcastStatus();
    }

    private bool TryConsumeListen()
    {
        lock (_gate)
        {
            if (!_listenActive) return false;
            _listenActive = false;
        }
        BroadcastStatus();
        return true;
    }

    /// <summary>입력 모니터 on/off — 켜면 키보드·마우스·게임패드·HID 입력을 인식해 스트림.</summary>
    public void SetMonitor(bool on)
    {
        _monitorActive = on;
        if (on) _rawInput.Enable(); else _rawInput.Disable();
        Log("info", on ? "입력 모니터 켜짐 — 들어오는 모든 입력을 인식해 표시합니다." : "입력 모니터 꺼짐.");
        BroadcastStatus();
    }

    private void OnHotkey(string macroId)
    {
        bool startPlay = false;
        lock (_gate)
        {
            if (_state == AppState.Playing && _currentMacroId == macroId) { _player.Stop(); return; }
            if (_state == AppState.Idle) startPlay = true;
        }
        if (startPlay)
        {
            // 저수준 훅 콜백을 막지 않도록 로드/전개/재생은 백그라운드에서(훅 타임아웃에 의한 트리거 누락 방지)
            _ = Task.Run(() =>
            {
                try { Play(macroId); }
                catch (Exception ex) { Log("error", ex.Message); }
            });
        }
    }
}
