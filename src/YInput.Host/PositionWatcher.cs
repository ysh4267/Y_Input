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

    // 고정된 미니맵 영역(점 탐색 범위, 창 상대) — [미니맵 탐지] 버튼으로 1회 자동 감지해 고정하거나
    // 수동 드래그로 지정한다. 이후 모든 측정은 이 고정 영역 안에서만 점을 찾는다.
    public int MiniX { get; set; }
    public int MiniY { get; set; }
    public int MiniW { get; set; }
    public int MiniH { get; set; }
    /// <summary>미니맵 허용오차(px). 점은 서브픽셀 centroid로 재므로 1 미만도 의미 있다.</summary>
    public double MiniTolerancePx { get; set; } = 0.6;
    public double MsPerMiniPx { get; set; } = 120; // 미니맵 1px ≈ 실좌표 수~십수 px → 홀드 비율 큼

    // 플레이어 점 색 임계(노랑) — UI 미노출, watcher.json 직접 수정으로 조정 가능
    public int DotMinR { get; set; } = 200;
    public int DotMinG { get; set; } = 180;
    public int DotMaxB { get; set; } = 120;
    /// <summary>미니맵 창(어두운 프레임) 판정 밝기 임계 — 이 이하 밝기 픽셀을 '어두움'으로 본다.</summary>
    public int PanelMaxLum { get; set; } = 70;

    // 템플릿(파인 보정 + 서있음 검증) 공통 파라미터.
    // 패치는 '캐릭터가 포함된' 창 정중앙 영역 하나 — 미세 보정과 제자리 검증을 겸한다.
    public int TolerancePx { get; set; } = 2;
    /// <summary>매칭/서있음 일치 임계 — 캐릭터 애니메이션·방향 변화 때문에 100%는 안 나온다.</summary>
    public double MinScore { get; set; } = 0.55;
    public double MsPerPx { get; set; } = 12;
    public int PatchW { get; set; } = 450;
    public int PatchH { get; set; } = 340;

    public int MaxHoldMs { get; set; } = 350;
    /// <summary>탭 후 재측정까지 대기(ms) — 키를 뗀 뒤 캐릭터가 미끄러져 멈출 시간을 포함해야 정확히 잰다.</summary>
    public int SettleMs { get; set; } = 220;
    public int MaxCorrectionMs { get; set; } = 6000;
}

/// <summary>블록(스팟)별 기준 위치 — 저장 시점의 미니맵 점(서브픽셀) + 기준 화면 패치 rect + 학습된 방향 부호.</summary>
public sealed class SpotData
{
    public double DotX { get; set; }
    public double DotY { get; set; }
    /// <summary>(구버전 호환) 점 좌표가 창(프레임) 상대였는지 — DotRel 마이그레이션에 사용.</summary>
    public bool DotFrame { get; set; }
    /// <summary>점 좌표가 '고정된 미니맵 영역 상대'인가(현행) — 미니맵 창을 옮기거나 다시 고정해도
    /// 상대 좌표는 유효해서 이탈 방향이 뒤집히지 않는다. false면 로드 시 변환.</summary>
    public bool DotRel { get; set; }
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
    private const int MinTapMs = 28;                      // 최소 탭(이보다 짧으면 게임이 이동으로 안 받을 수 있음)
    private const int FineNudgePx = 6;                    // 이 이하 잔여 오차는 비례 대신 최소 탭으로 미세 이동(오버슈트 방지)

    private readonly string _statePath;
    private readonly string _spotsDir;
    private readonly SocketHub _hub;
    private readonly InputBackend _backend;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _sem = new(1, 1); // 동시 재생 매크로 여러 개 → 보정은 한 번에 하나

    private WatcherSettings _settings;
    private readonly Dictionary<string, (SpotData Data, GrayImage Gray)> _spotCache = new();

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
                         int? maxCorrectionMs, double? minScore, double? miniTolerancePx, double? msPerMiniPx)
    {
        object snap;
        lock (_gate)
        {
            if (!string.IsNullOrWhiteSpace(process)) _settings.Process = Normalize(process);
            if (tolerancePx is int t && t >= 0) _settings.TolerancePx = t;
            if (msPerPx is double m && m > 0) _settings.MsPerPx = m;
            if (maxCorrectionMs is int mc && mc > 0) _settings.MaxCorrectionMs = mc;
            if (minScore is double ms && ms is > 0 and <= 1) _settings.MinScore = ms;
            if (miniTolerancePx is double mt && mt >= 0) _settings.MiniTolerancePx = mt;
            if (msPerMiniPx is double mm && mm > 0) _settings.MsPerMiniPx = mm;
            Save(_settings);
            snap = Snapshot(_settings);
        }
        Broadcast();
        return snap;
    }

    // ---------- 캡처/미니맵(전역) ----------
    /// <summary>게임 창 프레임 캡처 — 브라우저 등 다른 창이 게임을 가리고 있어도 PrintWindow로
    /// 창 내용을 직접 찍는다. 실패·검은 프레임(일부 DX 창)이면 화면 복사로 폴백.
    /// 반환 프레임의 좌표계는 기존과 동일한 DWM rect(창 상대).</summary>
    private static Bitmap? CaptureGameFrame(string process, out Rectangle win)
    {
        win = Rectangle.Empty;
        if (!WindowLocator.TryGetWindow(process, out var hWnd, out var dwmRect, out var winRect)) return null;
        win = dwmRect;
        var bmp = ScreenCapture.CaptureWindow(hWnd, winRect, dwmRect);
        if (bmp is not null && !ScreenCapture.IsMostlyBlack(bmp)) return bmp;
        bmp?.Dispose();
        return ScreenCapture.Capture(dwmRect);
    }

    // ---------- 미니맵 고정(자동 탐지/수동 지정) ----------
    private Bitmap? _lastFrame; // 수동 지정 모달용 마지막 캡처 — 사용자가 본 그 프레임에서 좌표를 받는다

    /// <summary>수동 지정 모달용: 게임 창 전체 프레임을 캡처해 PNG 반환 + 보관.</summary>
    public byte[] CaptureFrame()
    {
        string proc; lock (_gate) proc = _settings.Process;
        var bmp = CaptureGameFrame(proc, out _)
            ?? throw new InvalidOperationException($"'{proc}' 창을 찾을 수 없습니다. 게임이 실행 중인지 확인하세요.");
        lock (_gate) { _lastFrame?.Dispose(); _lastFrame = bmp; }
        return ScreenCapture.ToPng(bmp);
    }

    /// <summary>[미니맵 탐지] — 지금 이 순간 1회 자동 감지해 미니맵 영역을 '고정'한다.
    /// 흰 테두리 안 맵 영역을 우선, 없으면 검은 챠시. 실패 시 예외(수동 지정 안내).</summary>
    public object AutoDetectMinimap()
    {
        lock (_gate)
        {
            var s = _settings;
            using var frame = CaptureGameFrame(s.Process, out _)
                ?? throw new InvalidOperationException($"'{s.Process}' 창을 찾을 수 없습니다. 게임이 실행 중인지 확인하세요.");
            if (!MinimapDetector.TryDetect(frame, out var panel, out var mapArea, out _, out _,
                                           s.DotMinR, s.DotMinG, s.DotMaxB, s.PanelMaxLum) || panel.IsEmpty)
                throw new ArgumentException("미니맵을 자동으로 찾지 못했습니다 — 미니맵이 펼쳐져 있는지 확인하거나 [수동 지정]을 사용하세요.");
            var r = mapArea.IsEmpty ? panel : mapArea;
            _settings.MiniX = r.X; _settings.MiniY = r.Y; _settings.MiniW = r.Width; _settings.MiniH = r.Height;
            Save(_settings);
        }
        Broadcast();
        lock (_gate) return Snapshot(_settings);
    }

    /// <summary>[수동 지정] — 마지막 캡처 프레임에서 드래그한 영역을 미니맵으로 고정. 점이 안 보이면 거부.</summary>
    public object SetMinimapRegion(int x, int y, int w, int h)
    {
        lock (_gate)
        {
            var frame = _lastFrame ?? throw new InvalidOperationException("먼저 화면을 캡처하세요.");
            var rect = ClampRect(new Rectangle(x, y, w, h), frame.Width, frame.Height);
            if (rect.Width < 8 || rect.Height < 8) throw new ArgumentException("미니맵 영역이 너무 작습니다.");
            if (!MinimapDetector.TryFindPlayerDot(frame, rect, out _, _settings.DotMinR, _settings.DotMinG, _settings.DotMaxB))
                throw new ArgumentException("선택한 영역에서 플레이어 노란 점을 찾지 못했습니다. 미니맵 맵 영역을 감싸게 다시 드래그하세요.");
            _settings.MiniX = rect.X; _settings.MiniY = rect.Y; _settings.MiniW = rect.Width; _settings.MiniH = rect.Height;
            Save(_settings);
        }
        Broadcast();
        lock (_gate) return Snapshot(_settings);
    }

    /// <summary>고정된 미니맵 확인용 미리보기 — 새 프레임에서 고정 영역(+여백)을 잘라 영역 테두리(초록)와
    /// 현재 캐릭터 점(노란 링)을 그려 반환. 미지정/실패 시 null.</summary>
    public byte[]? MinimapPreview()
    {
        WatcherSettings s; lock (_gate) s = Clone(_settings);
        if (s.MiniW <= 0) return null;
        using var frame = CaptureGameFrame(s.Process, out _);
        if (frame is null) return null;
        var mini = new Rectangle(s.MiniX, s.MiniY, s.MiniW, s.MiniH);
        var r = ClampRect(new Rectangle(mini.X - 10, mini.Y - 10, mini.Width + 20, mini.Height + 20), frame.Width, frame.Height);
        if (r.Width <= 0 || r.Height <= 0) return null;
        bool hasDot = MinimapDetector.TryFindPlayerDot(frame, mini, out var dotRel, s.DotMinR, s.DotMinG, s.DotMaxB);
        using var crop = frame.Clone(r, frame.PixelFormat);
        using (var g = Graphics.FromImage(crop))
        {
            using var penMini = new Pen(Color.FromArgb(52, 211, 153), 1.6f);
            g.DrawRectangle(penMini, mini.X - r.X, mini.Y - r.Y, mini.Width - 1, mini.Height - 1);
            if (hasDot)
            {
                using var penDot = new Pen(Color.FromArgb(255, 216, 59), 2f);
                g.DrawEllipse(penDot, mini.X + dotRel.X - r.X - 6, mini.Y + dotRel.Y - r.Y - 6, 12, 12);
            }
        }
        return ScreenCapture.ToPng(crop);
    }

    // ---------- 실시간 미리보기(캐릭터 위치 지정 팝업) ----------
    private Bitmap? _liveFrame;  // 마지막 Live() 캡처 — live/frame·live/mini가 같은 프레임을 사용
    private PointF? _liveDot;    // 마지막 Live()에서 감지된 캐릭터 점(창 상대)

    /// <summary>현재 게임 화면을 캡처해 미니맵 점·자동 패치 rect를 계산한다(키 입력 없음).
    /// 이어지는 <see cref="LiveCrop"/>이 이 프레임에서 미리보기 이미지를 잘라낸다.</summary>
    public object Live()
    {
        lock (_gate)
        {
            var s = _settings;
            if (s.MiniW <= 0)
                return new { ok = false, needMinimap = true, error = "미니맵이 지정되지 않았습니다." };
            var frame = CaptureGameFrame(s.Process, out _);
            if (frame is null)
                return new { ok = false, needMinimap = false, error = $"'{s.Process}' 창을 찾을 수 없습니다." };

            _liveFrame?.Dispose();
            _liveFrame = frame;

            // 고정된 미니맵 영역 안에서만 캐릭터 점을 찾는다.
            var mini = new Rectangle(s.MiniX, s.MiniY, s.MiniW, s.MiniH);
            var cands = MinimapDetector.FindDots(_liveFrame, mini, s.DotMinR, s.DotMinG, s.DotMaxB)
                .Select(c => c with { Center = new PointF(c.Center.X + mini.X, c.Center.Y + mini.Y) }).ToList();
            bool dotFound = cands.Count > 0;
            var dot = dotFound ? MinimapDetector.Pick(cands).Center : PointF.Empty;
            _liveDot = dotFound ? dot : null;
            return new
            {
                ok = true,
                dotFound, dotX = Math.Round(dot.X, 1), dotY = Math.Round(dot.Y, 1), // 창(프레임) 상대
                dotCandidates = cands.Count,        // 2개 이상이면 UI가 '마커가 내 캐릭터인지 확인' 경고
                frameW = _liveFrame.Width, frameH = _liveFrame.Height, // 클릭 좌표 환산·오버레이 배율용
                patchW = s.PatchW, patchH = s.PatchH,                  // 앵커 중심 저장 영역 오버레이 크기
                foreground = WindowLocator.IsForeground(s.Process),
            };
        }
    }

    /// <summary>마지막 Live() 프레임 전체 PNG(확장 카드의 실시간 뷰).</summary>
    public byte[]? LiveFrame()
    {
        lock (_gate) return _liveFrame is null ? null : ScreenCapture.ToPng(_liveFrame);
    }

    /// <summary>고정된 미니맵 미리보기(팝업용) — 고정 영역(+여백)을 잘라 영역 테두리(초록)와
    /// 내 캐릭터 점(노란 링)을 그려 반환. 점 미탐지/프레임 없음이면 null.</summary>
    public byte[]? LiveMini()
    {
        lock (_gate)
        {
            var s = _settings;
            if (_liveFrame is null || _liveDot is not { } dot || s.MiniW <= 0) return null;
            var mini = new Rectangle(s.MiniX, s.MiniY, s.MiniW, s.MiniH);
            var r = ClampRect(new Rectangle(mini.X - 8, mini.Y - 8, mini.Width + 16, mini.Height + 16),
                              _liveFrame.Width, _liveFrame.Height);
            if (r.Width <= 0 || r.Height <= 0) return null;
            using var crop = _liveFrame.Clone(r, _liveFrame.PixelFormat);
            using (var g = Graphics.FromImage(crop))
            {
                using var penMini = new Pen(Color.FromArgb(52, 211, 153), 1.6f); // 초록 = 고정된 미니맵 영역
                g.DrawRectangle(penMini, mini.X - r.X, mini.Y - r.Y, mini.Width - 1, mini.Height - 1);
                using var penDot = new Pen(Color.FromArgb(255, 216, 59), 2f);    // 노랑 = 내 캐릭터 점
                g.DrawEllipse(penDot, dot.X - r.X - 6, dot.Y - r.Y - 6, 12, 12);
            }
            return ScreenCapture.ToPng(crop);
        }
    }

    // ---------- 스팟(블록별 기준 위치) ----------
    /// <summary>확정 — 지금 이 순간의 화면을 새로 캡처해 미니맵 점 + 자동 패치 영역을 스팟으로 저장한다.
    /// (블록 확장 카드에서 실시간 미리보기를 보다가 [확정]을 눌렀을 때)</summary>
    public object CaptureSpot(string id, int? anchorX = null, int? anchorY = null)
    {
        RequireValidId(id);
        lock (_gate)
        {
            var s = _settings;
            if (s.MiniW <= 0) throw new InvalidOperationException("미니맵이 지정되지 않았습니다 — 설정에서 [미니맵 탐지] 또는 [수동 지정]을 먼저 하세요.");
            using var frame = CaptureGameFrame(s.Process, out _)
                ?? throw new InvalidOperationException($"'{s.Process}' 창을 찾을 수 없습니다. 게임이 실행 중인지 확인하세요.");
            // 고정된 미니맵 영역 안에서 점 탐지 — Live 미리보기와 같은 기준이라 마커와 일치.
            var mini = new Rectangle(s.MiniX, s.MiniY, s.MiniW, s.MiniH);
            if (!MinimapDetector.TryFindPlayerDot(frame, mini, out var dot, s.DotMinR, s.DotMinG, s.DotMaxB))
                throw new ArgumentException("고정된 미니맵 영역에서 플레이어 점을 찾지 못했습니다. 미니맵이 이동/접힘 상태인지 확인하세요.");
            // dot은 미니맵 영역 '상대' 좌표 그대로 저장 — 미니맵을 옮기거나 다시 고정해도 유효

            // 앵커 = 사용자가 지정 카드에서 클릭한 캐릭터 위치(창 상대). 카메라 레이지 무브 때문에
            // 캐릭터가 화면 중앙에 있다는 보장이 없어, 중앙 가정 대신 앵커 중심으로 자른다.
            var anchor = new Point(
                Math.Clamp(anchorX ?? frame.Width / 2, 0, frame.Width - 1),
                Math.Clamp(anchorY ?? frame.Height / 2, 0, frame.Height - 1));
            var rect = ClampRect(RectAround(s, anchor), frame.Width, frame.Height);
            if (rect.Width < 16 || rect.Height < 16) throw new ArgumentException("기준 영역을 잡을 수 없습니다(창 가장자리에 너무 가깝습니다).");

            using var patchBmp = frame.Clone(rect, frame.PixelFormat);
            var gray = TemplateMatcher.ToGray(patchBmp);
            if (TemplateMatcher.StdDev(gray) < MinPatchStdDev)
                throw new ArgumentException("선택 위치 주변의 특징이 부족합니다(거의 단색). 무늬 있는 지형이 함께 잡히게 캐릭터를 클릭하세요.");

            var spot = new SpotData
            {
                DotX = dot.X, DotY = dot.Y, DotFrame = true, DotRel = true, // 미니맵 영역 상대 좌표
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

    /// <summary>앵커(캐릭터 위치) 중심의 기준 패치 rect.</summary>
    private static Rectangle RectAround(WatcherSettings s, Point anchor) =>
        new(anchor.X - s.PatchW / 2, anchor.Y - s.PatchH / 2, s.PatchW, s.PatchH);

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
                dotX = Math.Round(s.Data.DotX, 1), dotY = Math.Round(s.Data.DotY, 1),
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
        if (spot is not { } sp) return new { error = "지정된 위치가 없습니다. [지정하기]로 저장하세요." };

        var dot = MeasureDot(s, new PointF((float)sp.Data.DotX, (float)sp.Data.DotY)); // 저장 위치 근처 우선(다른 블롭 배제)
        double? miniDx = dot is { } d ? Math.Round(d.X - sp.Data.DotX, 1) : null;
        double? score = null; int? dx = null;
        var pm = MeasurePatch(s, sp.Data, sp.Gray);
        if (pm is { } r) { dx = r.dx; score = r.score; }
        return new
        {
            dotFound = dot is not null, miniDx,
            inPlace = miniDx is { } m && Math.Abs(m) <= s.MiniTolerancePx, // 서있어야 할 위치에 있는가
            patchFound = score >= s.MinScore, dx, score,
        };
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
        if (resolved is not { } sp) { Status("skip", "이 블록에 지정된 위치가 없어 보정을 건너뜁니다. 편집기에서 [지정하기]로 저장하세요."); return; }
        if (!_sem.Wait(0)) return; // 다른 매크로가 이미 보정 중 → 스킵

        try
        {
            var spot = sp.Data; var patch = sp.Gray;
            if (!WindowLocator.IsForeground(s.Process)) { Status("skip", "게임 창이 전면이 아니라 보정을 건너뜁니다."); return; }
            var sw = Stopwatch.StartNew();

            for (int pass = 0; pass < 2; pass++) // 파인 후 미니맵이 다시 벗어나 있으면 1회 한해 코스부터 재시도
            {
                // ── 1단계: 미니맵 코스 복귀 — 방향키를 '누른 채' 걸으며 주기 측정, 도착 직전/지나침에 뗀다 ──
                var dot = MeasureDot(s, new PointF((float)spot.DotX, (float)spot.DotY));
                if (dot is null) { Status("fail", "미니맵에서 플레이어 점을 찾지 못해 보정을 포기합니다."); return; }
                double miniDx = dot.Value.X - spot.DotX;
                double releaseEarly = Math.Max(s.MiniTolerancePx, 1.2); // 키를 뗀 뒤 관성 미끄러짐 여유분

                while (Math.Abs(miniDx) > s.MiniTolerancePx && sw.ElapsedMilliseconds < s.MaxCorrectionMs)
                {
                    ct.ThrowIfCancellationRequested();
                    if (!WindowLocator.IsForeground(s.Process)) { Status("skip", "게임 창이 전면에서 벗어나 보정을 중단합니다."); return; }
                    Status("coarse", $"미니맵 보정 중 (이탈 {miniDx:+0.0;-0.0}px)");
                    // 점이 저장 위치보다 오른쪽(+) = 캐릭터가 오른쪽에 있음 → 왼쪽 키
                    ushort key = miniDx > 0 ? ScLeft : ScRight;
                    int dirSign = Math.Sign(miniDx);
                    bool lostDot = false;
                    _backend.Send(new KeyboardEvent { Code = key, State = KeyDownE0 });
                    try
                    {
                        // 누른 채 연속 이동 — 걷는 동안 주기적으로 위치를 재며 도착 직전 또는 지나침에 뗀다
                        while (sw.ElapsedMilliseconds < s.MaxCorrectionMs)
                        {
                            await PreciseDelay.WaitAsync(60, ct).ConfigureAwait(false);
                            var d2 = MeasureDot(s, dot);
                            if (d2 is null) { lostDot = true; break; } // 점 놓침 → 일단 멈추고 아래서 재평가
                            dot = d2;
                            miniDx = dot.Value.X - spot.DotX;
                            if (Math.Sign(miniDx) != dirSign) break;      // 목표를 지나침
                            if (Math.Abs(miniDx) <= releaseEarly) break;  // 도착 직전(관성 감안)
                        }
                    }
                    finally { try { _backend.Send(new KeyboardEvent { Code = key, State = KeyUpE0 }); } catch { } }

                    await PreciseDelay.WaitAsync(s.SettleMs, ct).ConfigureAwait(false); // 미끄러짐 정지 대기
                    var d3 = MeasureDot(s, dot);
                    if (d3 is null) { Status("fail", "보정 중 미니맵 점을 놓쳤습니다."); return; }
                    _ = lostDot; // 홀드 중 일시적으로 놓쳤어도 정지 후 다시 찾았으면 계속
                    dot = d3;
                    miniDx = dot.Value.X - spot.DotX;
                    // 아직 밖이면 새 방향으로 다시 홀드(지나쳤으면 자연히 반대 방향으로 짧게 되돌아온다)
                }
                if (Math.Abs(miniDx) > s.MiniTolerancePx) { Status("fail", $"보정 시간 초과(미니맵 이탈 {miniDx:+0.0;-0.0}px 남음)."); return; }

                // ── 2단계: 템플릿 파인 조정(캐릭터 포함 기준 화면 — 못 찾으면 다른 발판/맵 의심) ──
                var pm = MeasurePatch(s, spot, patch);
                if (pm is not { } m || m.score < s.MinScore)
                {
                    Status("fail", $"기준 지형(캐릭터 주변)을 찾지 못했습니다 — 다른 발판이나 다른 맵에 있을 수 있습니다{(pm is { } p0 ? $"(일치 {p0.score * 100:0}%)" : "")}.");
                    return;
                }

                int sign = spot.DirectionSign == 0 ? 1 : spot.DirectionSign; // 기본: 카메라-추적 가정
                bool learning = spot.DirectionSign == 0;
                int dx = m.dx;
                double msPerPx = s.MsPerPx;

                while (Math.Abs(dx) > s.TolerancePx && sw.ElapsedMilliseconds < s.MaxCorrectionMs)
                {
                    ct.ThrowIfCancellationRequested();
                    if (!WindowLocator.IsForeground(s.Process)) { Status("skip", "게임 창이 전면에서 벗어나 보정을 중단합니다."); return; }
                    Status("fine", $"미세 보정 중 (이탈 {dx:+0;-0}px)", dx: dx, score: m.score);
                    // 카메라-추적: 배경 패치가 오른쪽(+)에 보이면 캐릭터가 왼쪽으로 간 것 → 오른쪽 키
                    ushort key = dx * sign > 0 ? ScRight : ScLeft;
                    // 잔여 오차가 작으면 비례 홀드 대신 최소 탭으로 콕콕 이동 — 오버슈트 없이 허용오차 안까지
                    double hold = Math.Abs(dx) <= FineNudgePx
                        ? MinTapMs
                        : Math.Clamp(Math.Abs(dx) * msPerPx, MinTapMs, s.MaxHoldMs);
                    await TapAsync(key, hold, ct).ConfigureAwait(false);
                    await PreciseDelay.WaitAsync(s.SettleMs, ct).ConfigureAwait(false);

                    int prevDx = dx;
                    pm = MeasurePatch(s, spot, patch);
                    if (pm is not { } m2 || m2.score < s.MinScore)
                    { Status("done", "미세 보정 중 매칭을 놓쳐 여기서 마칩니다."); return; }
                    m = m2; dx = m.dx;

                    if (learning)
                    {
                        // 첫 탭 결과로 부호 학습: 편차가 커졌으면 반대 방향(맵 가장자리 = 카메라 고정 케이스)
                        if (Math.Abs(dx) > Math.Abs(prevDx) + 2) { sign = -sign; PersistSign(spotId!, spot, sign); learning = false; Status("fine", "이동 방향을 반대로 학습했습니다."); }
                        else if (Math.Abs(dx) < Math.Abs(prevDx)) { PersistSign(spotId!, spot, sign); learning = false; }
                    }
                    else if (dx * prevDx < 0) msPerPx *= 0.55; // 목표를 지나침 → 다음 탭 약하게(진동 방지)
                }

                if (Math.Abs(dx) > s.TolerancePx) { Status("fail", $"보정 시간 초과(잔여 이탈 {dx:+0;-0}px)."); return; }

                // 파인 탭 도중 크게 밀렸을 수 있으니 미니맵 재확인 — 벗어났으면 한 번만 처음부터
                var recheck = MeasureDot(s, new PointF((float)spot.DotX, (float)spot.DotY));
                if (pass == 0 && recheck is { } rd && Math.Abs(rd.X - spot.DotX) > s.MiniTolerancePx) continue;

                // 패치가 캐릭터 포함 지형이므로 최종 매칭 성공 = 그 자리에 제대로 서 있음
                Status("done", $"위치 보정 완료 — 서있음 확인 {m.score * 100:0}% (잔여 {dx:+0;-0}px)", dx: dx, score: m.score);
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
            _liveFrame?.Dispose(); _liveFrame = null;
            _lastFrame?.Dispose(); _lastFrame = null;
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
            // 마이그레이션 → 현행: 점 좌표는 '고정 미니맵 영역 상대'.
            // 창 상대(DotFrame)였으면 현재 고정 영역 원점을 빼서 변환(미니맵을 옮겼다면 재지정 권장).
            if (!data.DotRel)
            {
                if (data.DotFrame) { data.DotX -= _settings.MiniX; data.DotY -= _settings.MiniY; }
                data.DotRel = true; data.DotFrame = true;
                try { File.WriteAllText(json, JsonSerializer.Serialize(data)); } catch { /* 무시 */ }
            }
            GrayImage gray;
            using (var bmp = new Bitmap(png)) gray = TemplateMatcher.ToGray(bmp);
            var entry = (data, gray);
            _spotCache[id] = entry;
            return entry;
        }
        catch { return null; }
    }

    /// <summary>프레임을 찍어 '고정된 미니맵 영역' 안에서 플레이어 점(미니맵 상대, 서브픽셀)을 찾는다.
    /// 창/점/미니맵 미지정이면 null. near = 직전(또는 저장) 위치(미니맵 상대) — 추적으로 다른 점으로 튀는 것을 막는다.</summary>
    private PointF? MeasureDot(WatcherSettings s, PointF? near = null)
    {
        if (s.MiniW <= 0) return null;
        using var frame = CaptureGameFrame(s.Process, out _);
        if (frame is null) return null;
        var mini = new Rectangle(s.MiniX, s.MiniY, s.MiniW, s.MiniH);
        var cands = MinimapDetector.FindDots(frame, mini, s.DotMinR, s.DotMinG, s.DotMaxB);
        if (cands.Count == 0) return null;
        return MinimapDetector.Pick(cands, near).Center; // 미니맵 상대 좌표
    }

    /// <summary>프레임의 패치 Y ± 밴드에서 템플릿 매칭. dx = 현재 매칭 X − 저장 X.</summary>
    private (int dx, double score)? MeasurePatch(WatcherSettings s, SpotData spot, GrayImage patch)
    {
        using var frame = CaptureGameFrame(s.Process, out _);
        if (frame is null) return null;
        int y0 = Math.Max(0, spot.PatchY - SearchBandPx);
        int y1 = Math.Min(frame.Height, spot.PatchY + spot.PatchH + SearchBandPx);
        if (y1 - y0 < spot.PatchH || frame.Width < spot.PatchW) return null;

        using var band = frame.Clone(new Rectangle(0, y0, frame.Width, y1 - y0), frame.PixelFormat);
        var gray = TemplateMatcher.ToGray(band);
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

    private void Status(string state, string message, int? miniDx = null, int? dx = null, double? score = null)
    {
        _hub.Broadcast("watcherStatus", new { state, message, miniDx, dx, score });
        FileLog.Write(state is "fail" ? "warn" : "info", $"[위치보정:{state}] {message}");
    }

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
        WatcherSettings s;
        try { s = File.Exists(_statePath) ? (JsonSerializer.Deserialize<WatcherSettings>(File.ReadAllText(_statePath)) ?? new()) : new(); }
        catch { return new(); }
        // 구버전 기본값 마이그레이션(사용자가 직접 조정한 값은 유지).
        if (s.MiniTolerancePx == 1) s.MiniTolerancePx = 0.6;
        if (s.TolerancePx == 4) s.TolerancePx = 2;
        if (s.SettleMs == 150) s.SettleMs = 220;
        // 패치를 '캐릭터 포함 창 중앙'으로 통합하면서 크기/임계 기본값 변경
        if (s.PatchW is 120 or 180) s.PatchW = 450; // 캐릭터 주변 범위 약 2.5배 확대
        if (s.PatchH is 64 or 140) s.PatchH = 340;
        if (s.MinScore == 0.60) s.MinScore = 0.55;
        return s;
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
        DotMinR = s.DotMinR, DotMinG = s.DotMinG, DotMaxB = s.DotMaxB, PanelMaxLum = s.PanelMaxLum,
        TolerancePx = s.TolerancePx, MinScore = s.MinScore, MsPerPx = s.MsPerPx,
        PatchW = s.PatchW, PatchH = s.PatchH,
        MaxHoldMs = s.MaxHoldMs, SettleMs = s.SettleMs, MaxCorrectionMs = s.MaxCorrectionMs,
    };
}
