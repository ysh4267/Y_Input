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
    private readonly SocketHub _hub;

    private readonly object _gate = new();
    private readonly Dictionary<int, string> _hotkeyToMacro = new();

    private AppState _state = AppState.Idle;
    private string? _currentMacroId;

    /// <summary>웹 UI 주소(상태에 포함).</summary>
    public string Url { get; set; } = "";

    public MacroService(InputBackend backend, MacroLibrary library, Player player,
                        Recorder recorder, HotkeyManager hotkeys, SocketHub hub)
    {
        _backend = backend;
        _library = library;
        _player = player;
        _recorder = recorder;
        _hotkeys = hotkeys;
        _hub = hub;

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
            _hub.Broadcast("recordedStep", new { summary = step.Event.Summary, delayBeforeMs = step.DelayBeforeMs });

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
            driver = new { interception = ds.InterceptionInstalled, vigem = ds.ViGEmInstalled, admin = ds.IsAdministrator },
            backend = new
            {
                interceptionAvailable = _backend.InterceptionAvailable,
                keyboardReady = _backend.KeyboardReady,
                mouseReady = _backend.MouseReady,
                gamepadAvailable = _backend.GamepadAvailable,
                gamepadConnected = _backend.GamepadConnected,
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

    public Macro StopRecording(string? name)
    {
        Macro macro;
        lock (_gate)
        {
            if (_state != AppState.Recording)
                throw new InvalidOperationException("녹화 중이 아닙니다.");
            var finalName = string.IsNullOrWhiteSpace(name) ? $"매크로 {DateTime.Now:MMdd-HHmmss}" : name!.Trim();
            macro = _recorder.Stop(finalName);
            _library.Save(macro);
            _state = AppState.Idle;
        }
        ReloadHotkeys();
        Log("info", $"녹화 저장: {macro.Name} ({macro.Steps.Count} 스텝)");
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
        _ = _player.PlayAsync(macro); // 완료 시 Stopped 핸들러에서 Idle 복귀
    }

    public void StopPlayback() => _player.Stop();

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

    // ---------- 핫키 ----------
    public void ReloadHotkeys()
    {
        _hotkeys.UnregisterAll();
        _hotkeyToMacro.Clear();
        foreach (var m in _library.LoadAll())
        {
            if (m.Trigger is { IsEmpty: false } hk)
            {
                try
                {
                    string macroId = m.Id;
                    int id = hk.IsMouse
                        ? _hotkeys.RegisterMouse(hk, () => OnHotkey(macroId))
                        : _hotkeys.Register(hk, () => OnHotkey(macroId));
                    _hotkeyToMacro[id] = macroId;
                }
                catch (Exception ex)
                {
                    Log("warn", $"핫키 등록 실패 ({m.Name} / {hk}): {ex.Message}");
                }
            }
        }
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
            try { Play(macroId); }
            catch (Exception ex) { Log("error", ex.Message); }
        }
    }
}
