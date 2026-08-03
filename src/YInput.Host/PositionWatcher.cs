using System.Diagnostics;
using System.Drawing;
using System.Text.Json;
using YInput.Core.Models;
using YInput.Engine;
using YInput.Host.Services;
using YInput.Host.Vision;
using YInput.Input;

namespace YInput.Host;

/// <summary>위치 지킴이 공통 설정(파일 영속 대상). 좌표는 게임 창 상대(px). 기준 위치 자체는
/// 블록(스팟)별로 <c>spots\{id}.json/png</c>에 따로 저장된다.</summary>
public sealed class WatcherSettings
{
    public string Process { get; set; } = "maplestory";

    // 미니맵 영역(전역 — 같은 게임 창이면 스팟이 달라도 동일)
    public int MiniX { get; set; }
    public int MiniY { get; set; }
    public int MiniW { get; set; }
    public int MiniH { get; set; }
    public int MiniTolerancePx { get; set; } = 1;
    public double MsPerMiniPx { get; set; } = 120; // 미니맵 1px ≈ 실좌표 수~십수 px → 홀드 비율 큼

    // 플레이어 점 색 임계(노랑) — UI 미노출, watcher.json 직접 수정으로 조정 가능
    public int DotMinR { get; set; } = 200;
    public int DotMinG { get; set; } = 180;
    public int DotMaxB { get; set; } = 120;

    // 템플릿(파인 보정) 공통 파라미터
    public int TolerancePx { get; set; } = 4;
    public double MinScore { get; set; } = 0.60;
    public double MsPerPx { get; set; } = 12;

    // 자동 기준 패치 크기/위치 — 창 가로 중앙, 세로 중앙에서 아래로 오프셋(캐릭터 발판 부근).
    // 카메라-추적 시 캐릭터가 화면 중앙에 오므로 그 발밑 지형이 기준이 된다. UI 미노출.
    public int PatchW { get; set; } = 120;
    public int PatchH { get; set; } = 64;
    public int PatchOffsetY { get; set; } = 24;

    public int MaxHoldMs { get; set; } = 350;
    public int SettleMs { get; set; } = 150;
    public int MaxCorrectionMs { get; set; } = 6000;
}

/// <summary>블록(스팟)별 기준 위치 — 저장 시점의 미니맵 점 + 기준 화면 패치 rect + 학습된 방향 부호.</summary>
public sealed class SpotData
{
    public int DotX { get; set; }
    public int DotY { get; set; }
    public int PatchX { get; set; }
    public int PatchY { get; set; }
    public int PatchW { get; set; }
    public int PatchH { get; set; }
    /// <summary>파인 보정 방향 부호. 0=미학습(카메라-추적 가정 +1로 시작해 첫 탭 결과로 학습·영속).</summary>
    public int DirectionSign { get; set; }
}

/// <summary>
/// 위치 지킴이 — '위치 보정' 스텝(<see cref="PositionCorrectEvent"/>)이 실행될 때 캐릭터가 그 블록에
/// 지정된 자리(스팟)에서 벗어났으면 방향키로 되돌린다. 2단계: ① 미니맵 노란 점으로 코스 복귀(절대 위치라
/// 방향 명확, 멀리 벗어나도 복귀), ② 화면 템플릿 매칭으로 파인 조정. 블록마다 서로 다른 스팟을 가진다.
/// 공통 설정은 <c>watcher.json</c>, 스팟은 <c>spots\{id}.json</c> + <c>spots\{id}.png</c>에 저장.
/// </summary>
public sealed class PositionWatcher : IDisposable
{
    private const ushort ScLeft = 0x4B, ScRight = 0x4D;   // 방향키 스캔코드(E0 확장)
    private const ushort KeyDownE0 = 0x02, KeyUpE0 = 0x03;
    private const int SearchBandPx = 24;                  // 템플릿 탐색 Y 범위(저장 Y ± 이 값)
    private const double MinPatchStdDev = 8;              // 패치 대비 하한(단색·특징 부족 거부)

    private readonly string _statePath;
    private readonly string _spotsDir;
    private readonly SocketHub _hub;
    private readonly InputBackend _backend;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _sem = new(1, 1); // 동시 재생 매크로 여러 개 → 보정은 한 번에 하나

    private WatcherSettings _settings;
    private readonly Dictionary<string, (SpotData Data, GrayImage Gray)> _spotCache = new();
    private Bitmap? _lastFrame; // 마지막 캡처 프레임 — 영역 지정은 사용자가 본 이 프레임에서 크롭

    public PositionWatcher(string dataRoot, SocketHub hub, InputBackend backend)
    {
        _statePath = Path.Combine(dataRoot, "watcher.json");
        _spotsDir = Path.Combine(dataRoot, "spots");
        _hub = hub;
        _backend = backend;
        _settings = Load();
    }

    // ---------- 조회/설정 ----------
    public object Get() { lock (_gate) return Snapshot(_settings); }

    public object Update(string? process, int? tolerancePx, double? msPerPx,
                         int? maxCorrectionMs, double? minScore, int? miniTolerancePx, double? msPerMiniPx)
    {
        object snap;
        lock (_gate)
        {
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

    // ---------- 캡처/미니맵(전역) ----------
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

    // ---------- 실시간 미리보기(블록 확장 카드) ----------
    private Bitmap? _liveFrame; // 마지막 Live() 캡처 — live/minimap·live/patch 크롭이 같은 프레임을 사용

    /// <summary>현재 게임 화면을 캡처해 미니맵 점·자동 패치 rect를 계산한다(키 입력 없음).
    /// 이어지는 <see cref="LiveCrop"/>이 이 프레임에서 미리보기 이미지를 잘라낸다.</summary>
    public object Live()
    {
        lock (_gate)
        {
            var s = _settings;
            if (s.MiniW <= 0) return new { ok = false, needMinimap = true, error = "미니맵 영역이 지정되지 않았습니다." };
            if (!WindowLocator.TryGetWindowRect(s.Process, out var win))
                return new { ok = false, needMinimap = false, error = $"'{s.Process}' 창을 찾을 수 없습니다." };

            _liveFrame?.Dispose();
            _liveFrame = ScreenCapture.Capture(win);

            var mini = new Rectangle(s.MiniX, s.MiniY, s.MiniW, s.MiniH);
            bool dotFound = MinimapDetector.TryFindPlayerDot(_liveFrame, mini, out var dot, s.DotMinR, s.DotMinG, s.DotMaxB);
            var patch = ClampRect(AutoPatchRect(s, _liveFrame.Width, _liveFrame.Height), _liveFrame.Width, _liveFrame.Height);
            return new
            {
                ok = true,
                dotFound, dotX = dot.X, dotY = dot.Y,
                miniW = s.MiniW, miniH = s.MiniH,
                patchW = patch.Width, patchH = patch.Height,
                foreground = WindowLocator.IsForeground(s.Process),
            };
        }
    }

    /// <summary>마지막 Live() 프레임에서 미리보기 크롭 PNG. what = "minimap" | "patch".</summary>
    public byte[]? LiveCrop(string what)
    {
        lock (_gate)
        {
            if (_liveFrame is null) return null;
            var s = _settings;
            var r = what == "minimap"
                ? new Rectangle(s.MiniX, s.MiniY, s.MiniW, s.MiniH)
                : AutoPatchRect(s, _liveFrame.Width, _liveFrame.Height);
            r = ClampRect(r, _liveFrame.Width, _liveFrame.Height);
            if (r.Width <= 0 || r.Height <= 0) return null;
            using var crop = _liveFrame.Clone(r, _liveFrame.PixelFormat);
            return ScreenCapture.ToPng(crop);
        }
    }

    // ---------- 스팟(블록별 기준 위치) ----------
    /// <summary>확정 — 지금 이 순간의 화면을 새로 캡처해 미니맵 점 + 자동 패치 영역을 스팟으로 저장한다.
    /// (블록 확장 카드에서 실시간 미리보기를 보다가 [확정]을 눌렀을 때)</summary>
    public object CaptureSpot(string id)
    {
        RequireValidId(id);
        lock (_gate)
        {
            var s = _settings;
            if (s.MiniW <= 0) throw new InvalidOperationException("먼저 미니맵 영역을 지정하세요.");
            if (!WindowLocator.TryGetWindowRect(s.Process, out var win))
                throw new InvalidOperationException($"'{s.Process}' 창을 찾을 수 없습니다. 게임이 실행 중인지 확인하세요.");

            using var frame = ScreenCapture.Capture(win);
            var mini = new Rectangle(s.MiniX, s.MiniY, s.MiniW, s.MiniH);
            if (!MinimapDetector.TryFindPlayerDot(frame, mini, out var dot, s.DotMinR, s.DotMinG, s.DotMaxB))
                throw new ArgumentException("미니맵에서 플레이어 노란 점을 찾지 못했습니다. 미니맵이 펼쳐져 있고 가려지지 않았는지 확인하세요.");

            var rect = ClampRect(AutoPatchRect(s, frame.Width, frame.Height), frame.Width, frame.Height);
            if (rect.Width < 16 || rect.Height < 16) throw new ArgumentException("기준 영역을 잡을 수 없습니다(창이 너무 작음).");

            using var patchBmp = frame.Clone(rect, frame.PixelFormat);
            var gray = TemplateMatcher.ToGray(patchBmp);
            if (TemplateMatcher.StdDev(gray) < MinPatchStdDev)
                throw new ArgumentException("캐릭터 발밑 배경의 특징이 부족합니다(거의 단색). 무늬 있는 지형 위에서 확정하세요.");

            var spot = new SpotData
            {
                DotX = dot.X, DotY = dot.Y,
                PatchX = rect.X, PatchY = rect.Y, PatchW = rect.Width, PatchH = rect.Height,
                DirectionSign = 0, // 새 자리 → 파인 방향 재학습
            };
            Directory.CreateDirectory(_spotsDir);
            File.WriteAllBytes(SpotPng(id), ScreenCapture.ToPng(patchBmp));
            File.WriteAllText(SpotJson(id), JsonSerializer.Serialize(spot));
            _spotCache[id] = (spot, gray);
        }
        return GetSpot(id);
    }

    /// <summary>자동 기준 패치 rect — 창 가로 중앙, 세로 중앙 + 오프셋(캐릭터 발판 부근).</summary>
    private static Rectangle AutoPatchRect(WatcherSettings s, int w, int h) =>
        new(w / 2 - s.PatchW / 2, h / 2 + s.PatchOffsetY, s.PatchW, s.PatchH);

    /// <summary>스팟 정보(블록 카드 표시용). 없으면 exists=false.</summary>
    public object GetSpot(string id)
    {
        RequireValidId(id);
        lock (_gate)
        {
            var r = ResolveSpot(id);
            if (r is not { } s) return new { exists = false };
            return new
            {
                exists = true,
                dotX = s.Data.DotX, dotY = s.Data.DotY,
                patchX = s.Data.PatchX, patchY = s.Data.PatchY, patchW = s.Data.PatchW, patchH = s.Data.PatchH,
                directionSign = s.Data.DirectionSign,
            };
        }
    }

    public byte[]? GetSpotPatch(string id)
    {
        RequireValidId(id);
        var p = SpotPng(id);
        return File.Exists(p) ? File.ReadAllBytes(p) : null;
    }

    /// <summary>키를 누르지 않고 스팟 기준 현재 이탈량만 측정(블록 카드의 테스트 버튼).</summary>
    public object TestSpot(string id)
    {
        RequireValidId(id);
        WatcherSettings s; (SpotData Data, GrayImage Gray)? spot;
        lock (_gate) { s = Clone(_settings); spot = ResolveSpot(id); }
        if (s.MiniW <= 0) return new { error = "미니맵 영역이 지정되지 않았습니다." };
        if (spot is not { } sp) return new { error = "지정된 위치가 없습니다. [지정하기]로 저장하세요." };

        var dot = MeasureDot(s);
        int? miniDx = dot is { } d ? d.X - sp.Data.DotX : null;
        double? score = null; int? dx = null;
        var pm = MeasurePatch(s, sp.Data, sp.Gray);
        if (pm is { } r) { dx = r.dx; score = r.score; }
        return new { dotFound = dot is not null, miniDx, patchFound = score >= s.MinScore, dx, score };
    }

    // ---------- 보정(재생 훅) ----------
    /// <summary>
    /// '위치 보정' 스텝의 실제 수행(Player.PositionCorrect 훅). 스텝의 spotId에 지정된 자리로 되돌린다.
    /// 취소(정지)는 OCE로 전파해 재생을 즉시 멈추고, 그 외 오류는 삼켜 재생을 계속한다.
    /// 어떤 경로로도 방향키가 눌린 채 남지 않는다. 스팟 미지정이면 no-op(상태 방송만).
    /// </summary>
    public async Task CorrectAsync(string? spotId, CancellationToken ct)
    {
        WatcherSettings s; (SpotData Data, GrayImage Gray)? resolved;
        lock (_gate)
        {
            s = Clone(_settings);
            resolved = string.IsNullOrEmpty(spotId) || !IsValidId(spotId) ? null : ResolveSpot(spotId);
        }
        if (s.MiniW <= 0) { Status("skip", "미니맵 영역이 지정되지 않아 위치 보정을 건너뜁니다."); return; }
        if (resolved is not { } sp) { Status("skip", "이 블록에 지정된 위치가 없어 보정을 건너뜁니다. 편집기에서 [지정하기]로 저장하세요."); return; }
        if (!_sem.Wait(0)) return; // 다른 매크로가 이미 보정 중 → 스킵

        try
        {
            var spot = sp.Data; var patch = sp.Gray;
            if (!WindowLocator.IsForeground(s.Process)) { Status("skip", "게임 창이 전면이 아니라 보정을 건너뜁니다."); return; }
            var sw = Stopwatch.StartNew();

            for (int pass = 0; pass < 2; pass++) // 파인 후 미니맵이 다시 벗어나 있으면 1회 한해 코스부터 재시도
            {
                // ── 1단계: 미니맵 코스 복귀 ──
                var dot = MeasureDot(s);
                if (dot is null) { Status("fail", "미니맵에서 플레이어 점을 찾지 못해 보정을 포기합니다."); return; }
                int miniDx = dot.Value.X - spot.DotX;

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
                    miniDx = dot.Value.X - spot.DotX;
                }
                if (Math.Abs(miniDx) > s.MiniTolerancePx) { Status("fail", $"보정 시간 초과(미니맵 이탈 {miniDx:+0;-0}px 남음)."); return; }

                // ── 2단계: 템플릿 파인 조정 ──
                var pm = MeasurePatch(s, spot, patch);
                if (pm is not { } m || m.score < s.MinScore)
                { Status("done", "템플릿 매칭 실패 — 미니맵 보정까지만 수행했습니다.", miniDx: miniDx); return; }

                int sign = spot.DirectionSign == 0 ? 1 : spot.DirectionSign; // 기본: 카메라-추적 가정
                bool learning = spot.DirectionSign == 0;
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
                    pm = MeasurePatch(s, spot, patch);
                    if (pm is not { } m2 || m2.score < s.MinScore)
                    { Status("done", "미세 보정 중 매칭을 놓쳐 여기서 마칩니다."); return; }
                    m = m2; dx = m.dx;

                    if (learning)
                    {
                        // 첫 탭 결과로 부호 학습: 편차가 커졌으면 반대 방향(맵 가장자리 = 카메라 고정 케이스)
                        if (Math.Abs(dx) > prevAbs + 2) { sign = -sign; PersistSign(spotId!, spot, sign); learning = false; Status("fine", "이동 방향을 반대로 학습했습니다."); }
                        else if (Math.Abs(dx) < prevAbs) { PersistSign(spotId!, spot, sign); learning = false; }
                    }
                }

                if (Math.Abs(dx) > s.TolerancePx) { Status("fail", $"보정 시간 초과(잔여 이탈 {dx:+0;-0}px)."); return; }

                // 파인 탭 도중 크게 밀렸을 수 있으니 미니맵 재확인 — 벗어났으면 한 번만 처음부터
                var recheck = MeasureDot(s);
                if (pass == 0 && recheck is { } rd && Math.Abs(rd.X - spot.DotX) > s.MiniTolerancePx) continue;

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
        lock (_gate)
        {
            _lastFrame?.Dispose(); _lastFrame = null;
            _liveFrame?.Dispose(); _liveFrame = null;
        }
        _sem.Dispose();
    }

    // ---------- 내부 ----------
    /// <summary>스팟을 캐시→디스크 순으로 해석. 반드시 _gate 안에서 호출.</summary>
    private (SpotData Data, GrayImage Gray)? ResolveSpot(string id)
    {
        if (_spotCache.TryGetValue(id, out var c)) return c;
        try
        {
            var json = SpotJson(id); var png = SpotPng(id);
            if (!File.Exists(json) || !File.Exists(png)) return null;
            var data = JsonSerializer.Deserialize<SpotData>(File.ReadAllText(json));
            if (data is null || data.PatchW <= 0) return null;
            using var bmp = new Bitmap(png);
            var gray = TemplateMatcher.ToGray(bmp);
            var entry = (data, gray);
            _spotCache[id] = entry;
            return entry;
        }
        catch { return null; }
    }

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
    private (int dx, double score)? MeasurePatch(WatcherSettings s, SpotData spot, GrayImage patch)
    {
        if (!WindowLocator.TryGetWindowRect(s.Process, out var win)) return null;
        int y0 = Math.Max(0, spot.PatchY - SearchBandPx);
        int y1 = Math.Min(win.Height, spot.PatchY + spot.PatchH + SearchBandPx);
        if (y1 - y0 < spot.PatchH || win.Width < spot.PatchW) return null;

        using var bmp = ScreenCapture.Capture(new Rectangle(win.X, win.Y + y0, win.Width, y1 - y0));
        var gray = TemplateMatcher.ToGray(bmp);
        var search = new Rectangle(0, 0, gray.Width - patch.Width + 1, gray.Height - patch.Height + 1);
        var m = TemplateMatcher.Match(gray, patch, search);
        return (m.X - spot.PatchX, m.Score); // 밴드는 창 X=0부터라 X는 창 상대 그대로
    }

    private async Task TapAsync(ushort code, double holdMs, CancellationToken ct)
    {
        _backend.Send(new KeyboardEvent { Code = code, State = KeyDownE0 });
        try { await PreciseDelay.WaitAsync(holdMs, ct).ConfigureAwait(false); }
        finally { try { _backend.Send(new KeyboardEvent { Code = code, State = KeyUpE0 }); } catch { } }
    }

    private void PersistSign(string id, SpotData spot, int sign)
    {
        lock (_gate)
        {
            spot.DirectionSign = sign;
            try { File.WriteAllText(SpotJson(id), JsonSerializer.Serialize(spot)); } catch { }
            if (_spotCache.TryGetValue(id, out var c)) _spotCache[id] = (spot, c.Gray);
        }
    }

    // 스팟 id는 클라이언트가 만든 UUID(하이픈 제거 가능) — 경로 조작 방지를 위해 엄격 검증.
    private static bool IsValidId(string id) =>
        id.Length is >= 8 and <= 64 && id.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');
    private static void RequireValidId(string id)
    { if (string.IsNullOrEmpty(id) || !IsValidId(id)) throw new ArgumentException("잘못된 위치 id입니다."); }

    private string SpotJson(string id) => Path.Combine(_spotsDir, id + ".json");
    private string SpotPng(string id) => Path.Combine(_spotsDir, id + ".png");

    private static Rectangle ClampRect(int x, int y, int w, int h, int maxW, int maxH) =>
        Rectangle.Intersect(new Rectangle(x, y, w, h), new Rectangle(0, 0, maxW, maxH));
    private static Rectangle ClampRect(Rectangle r, int maxW, int maxH) =>
        Rectangle.Intersect(r, new Rectangle(0, 0, maxW, maxH));

    private void Status(string state, string message, int? miniDx = null, int? dx = null, double? score = null) =>
        _hub.Broadcast("watcherStatus", new { state, message, miniDx, dx, score });

    private void Broadcast() { lock (_gate) _hub.Broadcast("watcherSettings", Snapshot(_settings)); }

    private object Snapshot(WatcherSettings s) => new
    {
        process = s.Process,
        hasMinimap = s.MiniW > 0,
        miniX = s.MiniX, miniY = s.MiniY, miniW = s.MiniW, miniH = s.MiniH,
        tolerancePx = s.TolerancePx, minScore = s.MinScore, msPerPx = s.MsPerPx,
        miniTolerancePx = s.MiniTolerancePx, msPerMiniPx = s.MsPerMiniPx,
        maxHoldMs = s.MaxHoldMs, settleMs = s.SettleMs, maxCorrectionMs = s.MaxCorrectionMs,
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
        Process = s.Process,
        MiniX = s.MiniX, MiniY = s.MiniY, MiniW = s.MiniW, MiniH = s.MiniH,
        MiniTolerancePx = s.MiniTolerancePx, MsPerMiniPx = s.MsPerMiniPx,
        DotMinR = s.DotMinR, DotMinG = s.DotMinG, DotMaxB = s.DotMaxB,
        TolerancePx = s.TolerancePx, MinScore = s.MinScore, MsPerPx = s.MsPerPx,
        PatchW = s.PatchW, PatchH = s.PatchH, PatchOffsetY = s.PatchOffsetY,
        MaxHoldMs = s.MaxHoldMs, SettleMs = s.SettleMs, MaxCorrectionMs = s.MaxCorrectionMs,
    };
}
