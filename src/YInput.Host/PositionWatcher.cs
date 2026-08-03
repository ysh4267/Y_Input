using System.Diagnostics;
using System.Drawing;
using System.Text.Json;
using YInput.Core.Models;
using YInput.Engine;
using YInput.Host.Services;
using YInput.Host.Vision;
using YInput.Input;

namespace YInput.Host;

/// <summary>위치 지킴이 설정(파일 영속 대상). 좌표는 모두 게임 창 상대(px).</summary>
public sealed class WatcherSettings
{
    public bool Enabled { get; set; }
    public string Process { get; set; } = "maplestory";

    // 미니맵(코스 보정): 영역 + 저장 시점 플레이어 점(미니맵 상대)
    public int MiniX { get; set; }
    public int MiniY { get; set; }
    public int MiniW { get; set; }
    public int MiniH { get; set; }
    public int DotX { get; set; }
    public int DotY { get; set; }
    public int MiniTolerancePx { get; set; } = 1;
    public double MsPerMiniPx { get; set; } = 120; // 미니맵 1px ≈ 실좌표 수~십수 px → 홀드 비율 큼

    // 플레이어 점 색 임계(노랑) — UI 미노출, watcher.json 직접 수정으로 조정 가능
    public int DotMinR { get; set; } = 200;
    public int DotMinG { get; set; } = 180;
    public int DotMaxB { get; set; } = 120;

    // 템플릿(파인 보정): 기준 패치 rect + 매칭/이동 파라미터
    public int PatchX { get; set; }
    public int PatchY { get; set; }
    public int PatchW { get; set; }
    public int PatchH { get; set; }
    public int TolerancePx { get; set; } = 4;
    public double MinScore { get; set; } = 0.60;
    public double MsPerPx { get; set; } = 12;
    /// <summary>파인 보정 방향 부호. 0=미학습(카메라-추적 가정 +1로 시작해 첫 탭 결과로 학습·영속).</summary>
    public int DirectionSign { get; set; }

    public int MaxHoldMs { get; set; } = 350;
    public int SettleMs { get; set; } = 150;
    public int MaxCorrectionMs { get; set; } = 6000;
}

/// <summary>
/// 위치 지킴이 — 매크로 반복 사이클 사이(<see cref="Services.MacroService.CycleHook"/>)에 캐릭터가
/// 저장된 자리에서 벗어났으면 방향키로 되돌린다. 2단계: ① 미니맵 노란 점으로 코스 복귀(절대 위치라
/// 방향 명확, 멀리 벗어나도 복귀), ② 화면 템플릿 매칭으로 파인 조정(미니맵 해상도 이하의 잔여 오차).
/// 설정은 <c>watcher.json</c>, 기준 패치는 <c>watcher_patch.png</c>에 저장.
/// </summary>
public sealed class PositionWatcher : IDisposable
{
    private const ushort ScLeft = 0x4B, ScRight = 0x4D;   // 방향키 스캔코드(E0 확장)
    private const ushort KeyDownE0 = 0x02, KeyUpE0 = 0x03;
    private const int SearchBandPx = 24;                  // 템플릿 탐색 Y 범위(저장 Y ± 이 값)
    private const double MinPatchStdDev = 8;              // 패치 대비 하한(단색·특징 부족 거부)

    private readonly string _statePath;
    private readonly string _patchPath;
    private readonly SocketHub _hub;
    private readonly InputBackend _backend;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _sem = new(1, 1); // 동시 재생 매크로 여러 개 → 보정은 한 번에 하나

    private WatcherSettings _settings;
    private GrayImage? _patchGray;   // 저장된 패치의 그레이 캐시(로드 시 1회 변환)
    private Bitmap? _lastFrame;      // 마지막 캡처 프레임 — 영역 지정은 사용자가 본 이 프레임에서 크롭

    public PositionWatcher(string dataRoot, SocketHub hub, InputBackend backend)
    {
        _statePath = Path.Combine(dataRoot, "watcher.json");
        _patchPath = Path.Combine(dataRoot, "watcher_patch.png");
        _hub = hub;
        _backend = backend;
        _settings = Load();
        LoadPatchCache();
    }

    // ---------- 조회/설정 ----------
    public object Get() { lock (_gate) return Snapshot(_settings); }

    public object Update(bool? enabled, string? process, int? tolerancePx, double? msPerPx,
                         int? maxCorrectionMs, double? minScore, int? miniTolerancePx, double? msPerMiniPx)
    {
        object snap;
        lock (_gate)
        {
            if (enabled is bool en) _settings.Enabled = en;
            if (!string.IsNullOrWhiteSpace(process)) _settings.Process = Normalize(process);
            if (tolerancePx is int t && t >= 0) _settings.TolerancePx = t;
            if (msPerPx is double m && m > 0) _settings.MsPerPx = m;
            if (maxCorrectionMs is int mc && mc > 0) _settings.MaxCorrectionMs = mc;
            if (minScore is double ms && ms is > 0 and <= 1) _settings.MinScore = ms;
            if (miniTolerancePx is int mt && mt >= 0) _settings.MiniTolerancePx = mt;
            if (msPerMiniPx is double mm && mm > 0) _settings.MsPerMiniPx = mm;
            Save(_settings);
            snap = Snapshot(_settings);
        }
        Broadcast();
        return snap;
    }

    // ---------- 위치 저장(웹 UI) ----------
    /// <summary>게임 창을 찾아 전체 프레임을 캡처하고 PNG로 반환. 프레임은 이후 영역 지정용으로 보관.</summary>
    public byte[] CaptureFrame()
    {
        string proc; lock (_gate) proc = _settings.Process;
        if (!WindowLocator.TryGetWindowRect(proc, out var rect))
            throw new InvalidOperationException($"'{proc}' 창을 찾을 수 없습니다. 게임이 실행 중인지 확인하세요.");
        var bmp = ScreenCapture.Capture(rect);
        lock (_gate) { _lastFrame?.Dispose(); _lastFrame = bmp; }
        return ScreenCapture.ToPng(bmp);
    }

    /// <summary>마지막 캡처 프레임에서 미니맵 영역을 지정한다. 노란 점이 안 보이면 거부.</summary>
    public object SetMinimapRegion(int x, int y, int w, int h)
    {
        lock (_gate)
        {
            var frame = _lastFrame ?? throw new InvalidOperationException("먼저 화면을 캡처하세요.");
            var rect = ClampRect(x, y, w, h, frame.Width, frame.Height);
            if (rect.Width < 8 || rect.Height < 8) throw new ArgumentException("미니맵 영역이 너무 작습니다.");
            if (!MinimapDetector.TryFindPlayerDot(frame, rect, out _, _settings.DotMinR, _settings.DotMinG, _settings.DotMaxB))
                throw new ArgumentException("선택한 영역에서 플레이어 노란 점을 찾지 못했습니다. 미니맵이 펼쳐져 있는지 확인하고 다시 드래그하세요.");
            _settings.MiniX = rect.X; _settings.MiniY = rect.Y; _settings.MiniW = rect.Width; _settings.MiniH = rect.Height;
            Save(_settings);
        }
        Broadcast();
        lock (_gate) return Snapshot(_settings);
    }

    /// <summary>마지막 캡처 프레임에서 기준 패치를 지정하고, 같은 프레임의 미니맵 점을 기준 위치로 저장한다.</summary>
    public object SetRegion(int x, int y, int w, int h)
    {
        lock (_gate)
        {
            var frame = _lastFrame ?? throw new InvalidOperationException("먼저 화면을 캡처하세요.");
            if (_settings.MiniW <= 0) throw new InvalidOperationException("먼저 미니맵 영역을 지정하세요.");
            var rect = ClampRect(x, y, w, h, frame.Width, frame.Height);
            if (rect.Width < 16 || rect.Height < 16) throw new ArgumentException("기준 영역이 너무 작습니다(최소 16×16px).");

            var mini = new Rectangle(_settings.MiniX, _settings.MiniY, _settings.MiniW, _settings.MiniH);
            if (!MinimapDetector.TryFindPlayerDot(frame, mini, out var dot, _settings.DotMinR, _settings.DotMinG, _settings.DotMaxB))
                throw new ArgumentException("미니맵에서 플레이어 노란 점을 찾지 못했습니다. 미니맵이 가려지지 않았는지 확인하세요.");

            using var patchBmp = frame.Clone(rect, frame.PixelFormat);
            var gray = TemplateMatcher.ToGray(patchBmp);
            if (TemplateMatcher.StdDev(gray) < MinPatchStdDev)
                throw new ArgumentException("선택한 영역의 특징이 부족합니다(거의 단색). 무늬가 있는 지형·배경을 선택하세요.");

            File.WriteAllBytes(_patchPath, ScreenCapture.ToPng(patchBmp));
            _patchGray = gray;
            _settings.PatchX = rect.X; _settings.PatchY = rect.Y; _settings.PatchW = rect.Width; _settings.PatchH = rect.Height;
            _settings.DotX = dot.X; _settings.DotY = dot.Y;
            _settings.DirectionSign = 0; // 새 자리 → 파인 방향 재학습
            Save(_settings);
        }
        Broadcast();
        lock (_gate) return Snapshot(_settings);
    }

    public byte[]? GetPatchPng() => File.Exists(_patchPath) ? File.ReadAllBytes(_patchPath) : null;

    public object ClearPatch()
    {
        lock (_gate)
        {
            try { File.Delete(_patchPath); } catch { }
            _patchGray = null;
            _settings.PatchW = _settings.PatchH = 0;
            _settings.DirectionSign = 0;
            Save(_settings);
        }
        Broadcast();
        lock (_gate) return Snapshot(_settings);
    }

    // ---------- 측정/보정 ----------
    /// <summary>키를 누르지 않고 현재 이탈량만 측정(설정 UI의 테스트 버튼).</summary>
    public object Test()
    {
        WatcherSettings s; GrayImage? patch;
        lock (_gate) { s = Clone(_settings); patch = _patchGray; }
        if (s.MiniW <= 0) return new { error = "미니맵 영역이 지정되지 않았습니다." };

        var dot = MeasureDot(s);
        int? miniDx = dot is { } d ? d.X - s.DotX : null;
        double? score = null; int? dx = null;
        if (patch is { } p && s.PatchW > 0)
        {
            var pm = MeasurePatch(s, p);
            if (pm is { } r) { dx = r.dx; score = r.score; }
        }
        return new { dotFound = dot is not null, miniDx, patchFound = score >= s.MinScore, dx, score };
    }

    /// <summary>
    /// 반복 사이클 사이 보정(Player.BeforeCycle → MacroService.CycleHook). 취소(정지)는 OCE로 전파해
    /// 재생을 즉시 멈추고, 그 외 오류는 삼켜 다음 사이클을 계속한다. 어떤 경로로도 방향키가 눌린 채 남지 않는다.
    /// </summary>
    public async Task CorrectAsync(CancellationToken ct)
    {
        WatcherSettings s; GrayImage? patch;
        lock (_gate) { s = Clone(_settings); patch = _patchGray; }
        if (!s.Enabled || s.MiniW <= 0 || patch is null || s.PatchW <= 0) return;
        if (!_sem.Wait(0)) return; // 다른 매크로가 이미 보정 중 → 스킵

        try
        {
            if (!WindowLocator.IsForeground(s.Process)) { Status("skip", "게임 창이 전면이 아니라 보정을 건너뜁니다."); return; }
            var sw = Stopwatch.StartNew();

            for (int pass = 0; pass < 2; pass++) // 파인 후 미니맵이 다시 벗어나 있으면 1회 한해 코스부터 재시도
            {
                // ── 1단계: 미니맵 코스 복귀 ──
                var dot = MeasureDot(s);
                if (dot is null) { Status("fail", "미니맵에서 플레이어 점을 찾지 못해 보정을 포기합니다."); return; }
                int miniDx = dot.Value.X - s.DotX;

                while (Math.Abs(miniDx) > s.MiniTolerancePx && sw.ElapsedMilliseconds < s.MaxCorrectionMs)
                {
                    ct.ThrowIfCancellationRequested();
                    if (!WindowLocator.IsForeground(s.Process)) { Status("skip", "게임 창이 전면에서 벗어나 보정을 중단합니다."); return; }
                    Status("coarse", $"미니맵 보정 중 (이탈 {miniDx:+0;-0}px)", miniDx: miniDx);
                    // 점이 저장 위치보다 오른쪽(+) = 캐릭터가 오른쪽에 있음 → 왼쪽 키
                    ushort key = miniDx > 0 ? ScLeft : ScRight;
                    await TapAsync(key, Math.Clamp(Math.Abs(miniDx) * s.MsPerMiniPx, 40, s.MaxHoldMs), ct).ConfigureAwait(false);
                    await PreciseDelay.WaitAsync(s.SettleMs, ct).ConfigureAwait(false);
                    dot = MeasureDot(s);
                    if (dot is null) { Status("fail", "보정 중 미니맵 점을 놓쳤습니다."); return; }
                    miniDx = dot.Value.X - s.DotX;
                }
                if (Math.Abs(miniDx) > s.MiniTolerancePx) { Status("fail", $"보정 시간 초과(미니맵 이탈 {miniDx:+0;-0}px 남음)."); return; }

                // ── 2단계: 템플릿 파인 조정 ──
                var pm = MeasurePatch(s, patch.Value);
                if (pm is not { } m || m.score < s.MinScore)
                { Status("done", "템플릿 매칭 실패 — 미니맵 보정까지만 수행했습니다.", miniDx: miniDx); return; }

                int sign = s.DirectionSign == 0 ? 1 : s.DirectionSign; // 기본: 카메라-추적 가정
                bool learning = s.DirectionSign == 0;
                int dx = m.dx;

                while (Math.Abs(dx) > s.TolerancePx && sw.ElapsedMilliseconds < s.MaxCorrectionMs)
                {
                    ct.ThrowIfCancellationRequested();
                    if (!WindowLocator.IsForeground(s.Process)) { Status("skip", "게임 창이 전면에서 벗어나 보정을 중단합니다."); return; }
                    Status("fine", $"미세 보정 중 (이탈 {dx:+0;-0}px)", dx: dx, score: m.score);
                    // 카메라-추적: 배경 패치가 오른쪽(+)에 보이면 캐릭터가 왼쪽으로 간 것 → 오른쪽 키
                    ushort key = dx * sign > 0 ? ScRight : ScLeft;
                    await TapAsync(key, Math.Clamp(Math.Abs(dx) * s.MsPerPx, 40, s.MaxHoldMs), ct).ConfigureAwait(false);
                    await PreciseDelay.WaitAsync(s.SettleMs, ct).ConfigureAwait(false);

                    int prevAbs = Math.Abs(dx);
                    pm = MeasurePatch(s, patch.Value);
                    if (pm is not { } m2 || m2.score < s.MinScore)
                    { Status("done", "미세 보정 중 매칭을 놓쳐 여기서 마칩니다."); return; }
                    m = m2; dx = m.dx;

                    if (learning)
                    {
                        // 첫 탭 결과로 부호 학습: 편차가 커졌으면 반대 방향(맵 가장자리 = 카메라 고정 케이스)
                        if (Math.Abs(dx) > prevAbs + 2) { sign = -sign; PersistSign(sign); learning = false; Status("fine", "이동 방향을 반대로 학습했습니다."); }
                        else if (Math.Abs(dx) < prevAbs) { PersistSign(sign); learning = false; }
                    }
                }

                if (Math.Abs(dx) > s.TolerancePx) { Status("fail", $"보정 시간 초과(잔여 이탈 {dx:+0;-0}px)."); return; }

                // 파인 탭 도중 크게 밀렸을 수 있으니 미니맵 재확인 — 벗어났으면 한 번만 처음부터
                var recheck = MeasureDot(s);
                if (pass == 0 && recheck is { } rd && Math.Abs(rd.X - s.DotX) > s.MiniTolerancePx) continue;

                Status("done", $"위치 보정 완료 (잔여 {dx:+0;-0}px)", dx: dx, score: m.score);
                return;
            }
            Status("done", "위치 보정 완료");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { try { Status("fail", "보정 오류: " + ex.Message); } catch { } }
        finally { _sem.Release(); }
    }

    public void Dispose()
    {
        lock (_gate) { _lastFrame?.Dispose(); _lastFrame = null; }
        _sem.Dispose();
    }

    // ---------- 내부 ----------
    /// <summary>미니맵 영역만 캡처해 플레이어 점(미니맵 상대)을 찾는다. 창/점 없으면 null.</summary>
    private Point? MeasureDot(WatcherSettings s)
    {
        if (!WindowLocator.TryGetWindowRect(s.Process, out var win)) return null;
        var rect = new Rectangle(win.X + s.MiniX, win.Y + s.MiniY, s.MiniW, s.MiniH);
        using var bmp = ScreenCapture.Capture(rect);
        return MinimapDetector.TryFindPlayerDot(bmp, new Rectangle(0, 0, s.MiniW, s.MiniH), out var dot,
                                                s.DotMinR, s.DotMinG, s.DotMaxB) ? dot : null;
    }

    /// <summary>패치 Y ± 밴드만 창 전폭으로 캡처해 템플릿 매칭. dx = 현재 매칭 X − 저장 X.</summary>
    private (int dx, double score)? MeasurePatch(WatcherSettings s, GrayImage patch)
    {
        if (!WindowLocator.TryGetWindowRect(s.Process, out var win)) return null;
        int y0 = Math.Max(0, s.PatchY - SearchBandPx);
        int y1 = Math.Min(win.Height, s.PatchY + s.PatchH + SearchBandPx);
        if (y1 - y0 < s.PatchH || win.Width < s.PatchW) return null;

        using var bmp = ScreenCapture.Capture(new Rectangle(win.X, win.Y + y0, win.Width, y1 - y0));
        var gray = TemplateMatcher.ToGray(bmp);
        var search = new Rectangle(0, 0, gray.Width - patch.Width + 1, gray.Height - patch.Height + 1);
        var m = TemplateMatcher.Match(gray, patch, search);
        return (m.X - s.PatchX, m.Score); // 밴드는 창 X=0부터라 X는 창 상대 그대로
    }

    private async Task TapAsync(ushort code, double holdMs, CancellationToken ct)
    {
        _backend.Send(new KeyboardEvent { Code = code, State = KeyDownE0 });
        try { await PreciseDelay.WaitAsync(holdMs, ct).ConfigureAwait(false); }
        finally { try { _backend.Send(new KeyboardEvent { Code = code, State = KeyUpE0 }); } catch { } }
    }

    private void PersistSign(int sign)
    {
        lock (_gate) { _settings.DirectionSign = sign; Save(_settings); }
        Broadcast();
    }

    private void LoadPatchCache()
    {
        try
        {
            if (_settings.PatchW > 0 && File.Exists(_patchPath))
            {
                using var bmp = new Bitmap(_patchPath);
                _patchGray = TemplateMatcher.ToGray(bmp);
            }
        }
        catch { _patchGray = null; }
    }

    private static Rectangle ClampRect(int x, int y, int w, int h, int maxW, int maxH)
    {
        var r = Rectangle.Intersect(new Rectangle(x, y, w, h), new Rectangle(0, 0, maxW, maxH));
        return r;
    }

    private void Status(string state, string message, int? miniDx = null, int? dx = null, double? score = null) =>
        _hub.Broadcast("watcherStatus", new { state, message, miniDx, dx, score });

    private void Broadcast() { lock (_gate) _hub.Broadcast("watcherSettings", Snapshot(_settings)); }

    private object Snapshot(WatcherSettings s) => new
    {
        enabled = s.Enabled,
        process = s.Process,
        hasMinimap = s.MiniW > 0,
        hasPatch = s.PatchW > 0 && File.Exists(_patchPath),
        miniX = s.MiniX, miniY = s.MiniY, miniW = s.MiniW, miniH = s.MiniH,
        dotX = s.DotX, dotY = s.DotY,
        patchX = s.PatchX, patchY = s.PatchY, patchW = s.PatchW, patchH = s.PatchH,
        tolerancePx = s.TolerancePx, minScore = s.MinScore, msPerPx = s.MsPerPx,
        miniTolerancePx = s.MiniTolerancePx, msPerMiniPx = s.MsPerMiniPx,
        maxHoldMs = s.MaxHoldMs, settleMs = s.SettleMs, maxCorrectionMs = s.MaxCorrectionMs,
        directionSign = s.DirectionSign,
    };

    // ---------- 영속 ----------
    private WatcherSettings Load()
    {
        try { return File.Exists(_statePath) ? (JsonSerializer.Deserialize<WatcherSettings>(File.ReadAllText(_statePath)) ?? new()) : new(); }
        catch { return new(); }
    }

    private void Save(WatcherSettings s)
    {
        try { File.WriteAllText(_statePath, JsonSerializer.Serialize(s)); } catch { }
    }

    private static string Normalize(string? t)
    {
        t = (t ?? "").Trim().ToLowerInvariant();
        if (t.EndsWith(".exe", StringComparison.Ordinal)) t = t[..^4];
        return t;
    }

    private static WatcherSettings Clone(WatcherSettings s) => new()
    {
        Enabled = s.Enabled, Process = s.Process,
        MiniX = s.MiniX, MiniY = s.MiniY, MiniW = s.MiniW, MiniH = s.MiniH,
        DotX = s.DotX, DotY = s.DotY, MiniTolerancePx = s.MiniTolerancePx, MsPerMiniPx = s.MsPerMiniPx,
        DotMinR = s.DotMinR, DotMinG = s.DotMinG, DotMaxB = s.DotMaxB,
        PatchX = s.PatchX, PatchY = s.PatchY, PatchW = s.PatchW, PatchH = s.PatchH,
        TolerancePx = s.TolerancePx, MinScore = s.MinScore, MsPerPx = s.MsPerPx, DirectionSign = s.DirectionSign,
        MaxHoldMs = s.MaxHoldMs, SettleMs = s.SettleMs, MaxCorrectionMs = s.MaxCorrectionMs,
    };
}
