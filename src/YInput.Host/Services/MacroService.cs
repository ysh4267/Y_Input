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
    private readonly ProgressBroadcaster _progress;

    private readonly object _gate = new();
    private readonly List<(string macroId, GamepadControl control)> _gamepadTriggers = new();

    // 동시 재생: macroId → 그 매크로의 재생 Player. 여러 매크로가 동시에 재생될 수 있다.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Player> _running = new();
    private volatile bool _recording;
    private bool AnyPlaying => !_running.IsEmpty;
    private volatile bool _listenActive;
    private volatile bool _monitorActive;

    /// <summary>웹 UI 주소(상태에 포함).</summary>
    public string Url { get; set; } = "";

    /// <summary>앱 종료 요청 콜백(트레이가 설정). /api/app/quit에서 호출.</summary>
    public Action? QuitRequested { get; set; }

    /// <summary>업데이트 교체로 인한 재시작 중인지 — 종료 시 'shutdown' 방송을 억제해 열린 탭이 새 인스턴스로 재연결하게 한다.</summary>
    public bool IsUpdating { get; private set; }
    public void MarkUpdating() => IsUpdating = true;

    /// <summary>로컬 매크로 변경 후 호출(Program이 GitHubSync.SchedulePush로 연결) — 동기화 푸시 예약.</summary>
    public Action? MacrosChanged { get; set; }

    private void NotifyChanged() => MacrosChanged?.Invoke();

    /// <summary>동기화가 원격 변경을 로컬에 반영한 뒤 호출 — 핫키 재등록 + 상태/목록 새로고침 방송(푸시는 다시 트리거하지 않음).</summary>
    public void OnExternalMacrosChanged()
    {
        ReloadHotkeys();
        _hub.Broadcast("macrosChanged", new { });
        BroadcastStatus();
    }

    /// <summary>동기화 진행/결과를 웹 UI에 실시간 전달(설정 패널의 상태 줄 갱신용).</summary>
    public void BroadcastSyncStatus(object data) => _hub.Broadcast("syncStatus", data);

    /// <summary>현재 열린 위젯(핀) 창 목록을 웹 UI에 전달(목록의 핀 버튼 상태 동기화용).</summary>
    public void BroadcastWidgets(IReadOnlyList<string> ids) => _hub.Broadcast("widgets", new { ids });

    /// <summary>로컬 매크로 변경을 웹 UI·위젯 창에 방송(목록/위젯 실시간 새로고침). 핫키·동기화 푸시는 각 변경 지점이 처리.</summary>
    public void BroadcastMacrosChanged() => _hub.Broadcast("macrosChanged", new { });

    /// <summary>브라우저 웹 UI(위젯 제외)가 하나라도 연결돼 있는가 — 단일 개체(중복 탭 방지) 판단용.</summary>
    public bool HasWebUiClient => _hub.HasMainClient;

    /// <summary>열린 웹 UI에게 특정 매크로 편집을 열라고 알린다(새 탭 없이 기존 창 그 자리에서).</summary>
    public void BroadcastOpenEditor(string id) => _hub.Broadcast("openEditor", new { id });

    /// <summary>웹/스크립트에서 앱 종료를 요청한다(그레이스풀).</summary>
    public void RequestQuit()
    {
        Log("info", "종료 요청 수신 — 앱을 종료합니다.");
        QuitRequested?.Invoke();
    }

    public MacroService(InputBackend backend, MacroLibrary library, Player player,
                        Recorder recorder, HotkeyManager hotkeys, RawInputMonitor rawInput, SocketHub hub,
                        ProgressBroadcaster progress)
    {
        _backend = backend;
        _library = library;
        _player = player;
        _recorder = recorder;
        _hotkeys = hotkeys;
        _rawInput = rawInput;
        _hub = hub;
        _progress = progress;

        _backend.GamepadInput += OnGamepadInput;
        _rawInput.Detected += OnRawDetected;

        // 재생 Player는 매크로별로 Play()에서 생성·배선한다(동시 재생). 주입 _player 는 미사용.
        _recorder.StepRecorded += (_, step) =>
            // 실시간 카드 렌더용으로 전체 이벤트(@event)도 함께 보냄($type 포함 직렬화).
            _hub.Broadcast("recordedStep", new { summary = step.Event.Summary, delayBeforeMs = step.DelayBeforeMs, @event = step.Event });

        // 시작 시 재생 상태는 어차피 보존되지 않으므로 자동 재생되는 매크로는 없다.
        // 활성(enabled) 토글은 그대로 유지 → 다음 세션에도 트리거가 무장된 상태로 시작된다.
        ReloadHotkeys();
    }

    // ---------- 상태 ----------
    public object GetStatusData()
    {
        var ds = DriverProvisioner.QueryStatus();
        string state = _recording ? "recording" : (AnyPlaying ? "playing" : "idle");
        var playing = _running.Keys.ToArray();
        return new
        {
            url = Url,
            state,
            currentMacroId = playing.FirstOrDefault(), // 호환(단일 표시)
            playingIds = playing,                       // 동시 재생 중인 모든 매크로
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

    private readonly object _logGate = new();
    private readonly LinkedList<object> _recentLogs = new();

    public void Log(string level, string message)
    {
        var entry = new { level, message, time = DateTime.Now.ToString("HH:mm:ss") };
        lock (_logGate) { _recentLogs.AddLast(entry); while (_recentLogs.Count > 200) _recentLogs.RemoveFirst(); }
        _hub.Broadcast("log", entry);
    }

    /// <summary>최근 로그 스냅샷(진단용 GET /api/log/recent).</summary>
    public object[] RecentLogs() { lock (_logGate) return _recentLogs.ToArray(); }

    // ---------- 녹화 ----------
    public void StartRecording(RecordOptions options)
    {
        lock (_gate)
        {
            if (_recording || AnyPlaying)
                throw new InvalidOperationException("이미 녹화/재생 중입니다.");
            if (!_backend.InterceptionAvailable)
                throw new InvalidOperationException("Interception 드라이버가 준비되지 않았습니다(설치/재부팅 확인).");
            _recorder.Start(options);
            _recording = true;
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
            if (!_recording)
                throw new InvalidOperationException("녹화 중이 아닙니다.");
            var finalName = string.IsNullOrWhiteSpace(name) ? $"매크로 {DateTime.Now:MMdd-HHmmss}" : name!.Trim();
            macro = _recorder.Stop(finalName);
            if (persist) _library.Save(macro);
            _recording = false;
        }
        ReloadHotkeys();
        Log("info", persist ? $"녹화 저장: {macro.Name} ({macro.Steps.Count} 스텝)" : $"녹화 종료 ({macro.Steps.Count} 스텝)");
        BroadcastStatus();
        if (persist) NotifyChanged();
        return macro;
    }

    // ---------- 재생 ----------
    public void Play(string id)
    {
        if (_recording) throw new InvalidOperationException("녹화 중에는 재생할 수 없습니다.");
        // 이미 이 매크로가 재생 중이면 정지(재트리거 = 토글). 다른 매크로는 계속 재생됨(동시 재생).
        if (_running.TryRemove(id, out var existing)) { existing.Stop(); BroadcastStatus(); return; }

        var macro = _library.Load(id) ?? throw new FileNotFoundException("매크로를 찾을 수 없습니다: " + id);
        var player = new Player(_backend); // 매크로마다 독립 Player → 서로 비동기·동시 재생
        // 진행 보고는 스텝마다(최대 ~1000/s) 오므로, ProgressBroadcaster가 매크로별 최신값만 ~60Hz로 합쳐 전송한다.
        player.Progress += (_, p) => _progress.Report(id, p);
        player.Failed += (_, ex) => Log("error", $"재생 오류({macro.Name}): {ex.Message}");
        // 정지 시 최종 진행 프레임을 1회 강제 전송(코얼레싱으로 마지막 프레임이 누락되지 않게) 후 슬롯 제거.
        player.Stopped += (_, _) => { _running.TryRemove(id, out _); _progress.Complete(id); BroadcastStatus(); };
        _running[id] = player;
        Log("info", $"재생 시작: {macro.Name}");
        BroadcastStatus();
        _ = player.PlayAsync(ExpandMacro(macro)); // 완료 시 Stopped 핸들러가 _running에서 제거
    }

    /// <summary>모든 재생 중 매크로를 정지(킬 스위치).</summary>
    public void StopPlayback()
    {
        foreach (var p in _running.Values.ToArray()) p.Stop();
    }

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

    // ---------- 매크로 CRUD ----------
    public IReadOnlyList<Macro> ListMacros() => _library.LoadAll();

    public Macro? GetMacro(string id) => _library.Load(id);

    /// <summary>참조(MacroRef)를 인라인 전개한 매크로(재생되는 실제 스텝 시퀀스 = 진행 표시 기준). 없으면 null.</summary>
    public Macro? Expanded(string id)
    {
        var m = _library.Load(id);
        return m is null ? null : ExpandMacro(m);
    }

    /// <summary>우측 현황 패널용 — 참조(MacroRef)를 펼치되 계층(트리)을 유지한다. 각 step의 <c>i</c>는
    /// 재생 stepIndex와 동일(ExpandMacro와 같은 순서). ref 노드는 children(들여쓴 내용)과 그 노드가
    /// 덮는 flat 범위(from..to)를 가진다. 없으면 null.</summary>
    public object? StepTree(string id)
    {
        var top = _library.Load(id);
        if (top is null) return null;
        int flat = 0;
        var path = new HashSet<string> { top.Id };
        List<object> Walk(Macro m)
        {
            var nodes = new List<object>();
            foreach (var s in m.Steps)
            {
                if (s.Event is MacroRefEvent r)
                {
                    var sub = string.IsNullOrEmpty(r.MacroId) ? null : _library.Load(r.MacroId);
                    var name = string.IsNullOrWhiteSpace(r.Name) ? (sub?.Name ?? r.MacroId) : r.Name;
                    if (string.IsNullOrEmpty(r.MacroId) || sub is null)
                    { nodes.Add(new { kind = "ref", name, note = "대상 없음", children = new List<object>(), from = (int?)null, to = (int?)null }); continue; }
                    if (path.Contains(r.MacroId))
                    { nodes.Add(new { kind = "ref", name, note = "순환", children = new List<object>(), from = (int?)null, to = (int?)null }); continue; }
                    int from = flat;
                    var children = new List<object>();
                    if (s.DelayBeforeMs > 0) // ExpandMacro: 참조 블록 선지연 보존(자식 첫 스텝)
                    { children.Add(new { kind = "step", i = flat, delayBeforeMs = s.DelayBeforeMs, @event = (InputEvent)new DelayEvent() }); flat++; }
                    path.Add(r.MacroId);
                    children.AddRange(Walk(sub));
                    path.Remove(r.MacroId);
                    nodes.Add(new { kind = "ref", name, note = "", children, from = (int?)from, to = (int?)(flat - 1) });
                }
                else
                {
                    nodes.Add(new { kind = "step", i = flat, delayBeforeMs = s.DelayBeforeMs, @event = s.Event });
                    flat++;
                }
            }
            return nodes;
        }
        return new { id = top.Id, name = top.Name, nodes = Walk(top) };
    }

    public void SaveMacro(Macro macro)
    {
        _library.Save(macro);
        ReloadHotkeys();
        Log("info", $"매크로 저장: {macro.Name}");
        BroadcastStatus();
        NotifyChanged();
    }

    public void DeleteMacro(string id)
    {
        RecycleBin.Delete(_library.PathFor(id));
        ReloadHotkeys();
        Log("info", "매크로를 휴지통으로 보냈습니다.");
        BroadcastStatus();
        NotifyChanged();
    }

    /// <summary>모든 매크로를 휴지통으로 보낸다(전체 초기화). 재생 중이면 먼저 정지한다.</summary>
    public int DeleteAllMacros()
    {
        StopPlayback();
        var all = _library.LoadAll();
        foreach (var m in all)
            RecycleBin.Delete(_library.PathFor(m.Id));
        ReloadHotkeys();
        Log("warn", $"매크로 전체 초기화: {all.Count}개를 휴지통으로 보냈습니다.");
        BroadcastStatus();
        NotifyChanged();
        return all.Count;
    }

    /// <summary>매크로 묶음을 가져온다. (1) 같은 이름·같은 내용의 매크로가 이미 있으면 새로 만들지 않고 그것을 재사용한다.
    /// (2) 이름은 같지만 내용이 다르면 이름 뒤에 숫자를 붙여 유니크하게 만든다. (3) 내부 매크로 참조(MacroRefEvent)는
    /// 옛 Id/이름 → 최종 대상 Id 로 재연결한다(내보내기에 참조 대상까지 함께 묶여 오므로 끊기지 않는다).
    /// 새로 추가한(생성한) 매크로 개수를 반환.</summary>
    public int ImportMacros(IReadOnlyList<Macro> incoming)
    {
        if (incoming is null || incoming.Count == 0) return 0;

        var existing = _library.LoadAll();
        var existingById = existing.ToDictionary(m => m.Id, m => m);
        var existingSig = existing.ToDictionary(m => m.Id, m => ContentSignature(m));
        var existingByName = new Dictionary<string, List<Macro>>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in existing)
        {
            if (!existingByName.TryGetValue(m.Name, out var list)) existingByName[m.Name] = list = new List<Macro>();
            list.Add(m);
        }
        var usedNames = new HashSet<string>(existing.Select(m => m.Name), StringComparer.OrdinalIgnoreCase);

        var oldToNew = new Dictionary<string, string>(StringComparer.Ordinal);            // 옛 Id → 최종 대상 Id(재사용/신규)
        var nameToNew = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // 원래 이름 → 최종 대상 Id
        var toSave = new List<Macro>();
        int reused = 0, renamed = 0;
        var order = NextOrder();

        foreach (var m in incoming)
        {
            var oldId = m.Id ?? "";
            var origName = string.IsNullOrWhiteSpace(m.Name) ? "Untitled" : m.Name;
            var sig = ContentSignature(m);

            // (1) 같은 이름 + 같은 내용이면 기존 매크로 재사용(중복 생성 안 함)
            Macro? identical = null;
            if (existingByName.TryGetValue(origName, out var sameName))
                identical = sameName.FirstOrDefault(e => existingSig[e.Id] == sig);
            if (identical is not null)
            {
                if (!string.IsNullOrEmpty(oldId)) oldToNew[oldId] = identical.Id;
                nameToNew[origName] = identical.Id;
                reused++;
                continue;
            }

            // (2) 이름은 겹치는데 내용이 다르면 뒤에 숫자를 붙여 유니크하게
            var finalName = origName;
            if (usedNames.Contains(finalName))
            {
                int n = 1;
                while (usedNames.Contains(origName + n)) n++;
                finalName = origName + n;
                renamed++;
            }
            usedNames.Add(finalName);

            var newId = Guid.NewGuid().ToString("N");
            m.Id = newId;
            m.Name = finalName;
            m.Order = order++;
            if (!string.IsNullOrEmpty(oldId)) oldToNew[oldId] = newId;
            nameToNew[origName] = newId;
            toSave.Add(m);
        }

        // (3) 매크로 참조(macroRef) 재연결 — 옛 Id → 최종 Id, 없으면 원래 이름 → 기존 Id → 기존 이름 순
        var existingIds = new HashSet<string>(existing.Select(m => m.Id));
        string? Resolve(MacroRefEvent r)
        {
            if (!string.IsNullOrEmpty(r.MacroId) && oldToNew.TryGetValue(r.MacroId, out var a)) return a;
            if (!string.IsNullOrWhiteSpace(r.Name) && nameToNew.TryGetValue(r.Name, out var b)) return b;
            if (!string.IsNullOrEmpty(r.MacroId) && existingIds.Contains(r.MacroId)) return r.MacroId;
            if (!string.IsNullOrWhiteSpace(r.Name) && existingByName.TryGetValue(r.Name, out var lst) && lst.Count > 0) return lst[0].Id;
            return null;
        }
        int relinked = 0;
        foreach (var m in toSave)
        {
            if (m.Steps is null) continue;
            foreach (var s in m.Steps)
            {
                if (s.Event is not MacroRefEvent r) continue;
                var target = Resolve(r);
                if (target is null) continue;
                if (r.MacroId != target) relinked++;
                r.MacroId = target;
                if (existingById.TryGetValue(target, out var et)) r.Name = et.Name;            // 표시 이름 동기화
                else { var bt = toSave.FirstOrDefault(x => x.Id == target); if (bt is not null) r.Name = bt.Name; }
            }
        }

        foreach (var m in toSave) _library.Save(m);
        ReloadHotkeys();
        Log("info", $"가져오기 완료: 신규 {toSave.Count}개"
            + (reused > 0 ? $", 동일 재사용 {reused}개" : "")
            + (renamed > 0 ? $", 이름변경 {renamed}개" : "")
            + (relinked > 0 ? $", 참조 {relinked}건 재연결" : "") + ".");
        BroadcastStatus();
        NotifyChanged();
        return toSave.Count;
    }

    /// <summary>매크로의 "내용" 서명 — Id/이름/순서/수정시각/적용여부/트리거를 제외하고 스텝·반복·속도만으로
    /// 동일성 비교 문자열을 만든다. 매크로 참조(macroRef)는 대상 이름으로 정규화해 Id 차이를 무시한다.</summary>
    private static string ContentSignature(Macro m)
    {
        var c = MacroStore.Deserialize(MacroStore.Serialize(m)); // 깊은 복제
        c.Id = string.Empty; c.Name = string.Empty; c.Order = 0; c.Enabled = false; c.Trigger = null; c.ModifiedUtc = default;
        if (c.Steps is not null)
            foreach (var s in c.Steps)
                if (s.Event is MacroRefEvent r) r.MacroId = "ref:" + r.Name;
        return MacroStore.Serialize(c);
    }

    /// <summary>매크로의 적용(활성) 여부를 토글한다. ON이면 트리거 핫키 무장, OFF면 보관만.</summary>
    public void SetEnabled(string id, bool enabled)
    {
        var macro = _library.Load(id) ?? throw new FileNotFoundException("매크로를 찾을 수 없습니다: " + id);
        macro.Enabled = enabled;
        _library.Save(macro);
        // 끄면(비활성) 그 매크로가 재생 중일 때 즉시 정지(킬)
        if (!enabled && _running.TryGetValue(id, out var rp)) rp.Stop();
        ReloadHotkeys();
        Log("info", $"매크로 '{macro.Name}' 적용 {(enabled ? "켬" : "끔")}.");
        BroadcastStatus();
        NotifyChanged();
    }

    /// <summary>매크로의 반복 횟수를 설정한다(실행 페이지 목록에서 직접). 트리거에 영향 없음.</summary>
    public void SetPlayback(string id, int loopCount)
    {
        var macro = _library.Load(id) ?? throw new FileNotFoundException("매크로를 찾을 수 없습니다: " + id);
        macro.LoopCount = loopCount;
        _library.Save(macro);
        NotifyChanged();
    }

    /// <summary>새 매크로에 부여할 순서값(현재 최댓값+1 → 목록 맨 아래).</summary>
    public int NextOrder() { var all = _library.LoadAll(); return all.Count == 0 ? 0 : all.Max(m => m.Order) + 1; }

    /// <summary>매크로 목록 순서를 ids 순서대로 저장한다(드래그 재정렬). 트리거에 영향 없음.</summary>
    public void Reorder(IReadOnlyList<string> ids)
    {
        for (int i = 0; i < ids.Count; i++)
        {
            var m = _library.Load(ids[i]);
            if (m is not null && m.Order != i) { m.Order = i; _library.Save(m); }
        }
        BroadcastStatus();
        NotifyChanged();
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
        NotifyChanged();
    }

    // ---------- 핫키 ----------
    public void ReloadHotkeys()
    {
        _hotkeys.UnregisterAll();
        lock (_gate) _gamepadTriggers.Clear();
        // 같은 트리거(키/마우스)를 여러 매크로가 공유하면 하나의 그룹으로 묶어 한 번만 등록한다.
        // 각 매크로가 개별 토글되면 재생 상태가 어긋났을 때(한쪽만 먼저 끝난 경우 등)
        // 끄기 입력이 꺼진 쪽을 되살리므로, 그룹 단위로 함께 켜고 함께 끈다.
        var groups = new Dictionary<string, (Hotkey hk, List<string> ids, List<string> names)>();
        foreach (var m in _library.LoadAll())
        {
            if (!(m.Enabled && m.Trigger is { IsEmpty: false } hk)) continue;
            if (hk.IsGamepad)
            {
                lock (_gate) _gamepadTriggers.Add((m.Id, hk.Gamepad!.Value));
                continue;
            }
            var sig = TriggerSignature(hk);
            if (!groups.TryGetValue(sig, out var g))
                groups[sig] = g = (hk, new List<string>(), new List<string>());
            g.ids.Add(m.Id);
            g.names.Add(m.Name);
        }
        foreach (var (hk, ids, names) in groups.Values)
        {
            try
            {
                // 마우스는 마우스 훅, 키보드는 단일/조합 모두 저수준 키보드 훅으로 감지.
                // (RegisterHotKey는 게임/전체화면·풀스크린 포커스에서 누락되거나 다른 앱과 충돌해 등록 실패할 수 있어
                //  저수준 훅이 더 안정적이며, 트리거 키를 가로채지 않고 통과시킨다.)
                if (hk.IsMouse) _hotkeys.RegisterMouse(hk, () => OnHotkey(ids));
                else _hotkeys.RegisterKeyChord(hk, () => OnHotkey(ids));
            }
            catch (Exception ex)
            {
                Log("warn", $"핫키 등록 실패 ({string.Join(", ", names)} / {hk}): {ex.Message}");
            }
        }
    }

    /// <summary>트리거 동일성 비교용 시그니처 — 같은 키(조합·순서 무관)·모디파이어·마우스 버튼이면 같은 그룹.</summary>
    private static string TriggerSignature(Hotkey hk) =>
        $"{(hk.Ctrl ? 1 : 0)}{(hk.Alt ? 1 : 0)}{(hk.Shift ? 1 : 0)}{(hk.Win ? 1 : 0)}|"
        + (hk.IsMouse ? $"m:{hk.Mouse}" : "k:" + string.Join(",", hk.EffectiveKeys.OrderBy(k => k)));

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
            if (hits.Count > 0) OnHotkey(hits); // 같은 버튼을 공유하는 매크로는 그룹으로 함께 토글
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

    private void OnHotkey(IReadOnlyList<string> macroIds)
    {
        // 트리거/입력 캡처 중(listen)에는 매크로를 시작하지 않는다 — 캡처하려고 누른 키가 매크로를 켜지 않게.
        if (_listenActive) return;
        // 저수준 훅 콜백을 막지 않도록 백그라운드에서. Play()가 토글 처리:
        // 그 매크로가 재생 중이면 정지, 아니면 시작(다른 매크로와 동시 재생).
        _ = Task.Run(() =>
        {
            // 같은 트리거를 공유하는 매크로는 함께 켜고 함께 끈다 — 하나라도 재생 중이면
            // '끄기' 입력으로 보고 이미 꺼진(먼저 끝난) 매크로는 되살리지 않는다.
            bool anyRunning = macroIds.Any(_running.ContainsKey);
            foreach (var id in macroIds)
            {
                if (anyRunning && !_running.ContainsKey(id)) continue;
                try { Play(id); }
                catch (Exception ex) { Log("error", ex.Message); }
            }
        });
    }
}
