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

    // 미니맵 영역은 전역이 아니라 '매크로별'로 저장된다(Macro.MapleMinimap) — 메이플 블록이 든
    // 매크로마다 자기 미니맵 rect를 갖고, 재생 훅과 편집기 측정 API가 그 rect를 넘겨받는다.
    /// <summary>미니맵 X 허용오차(px). 0.6은 리바운드 미세조정이 잦아 1.5로 완화(사용자 지정 2026-08-04).</summary>
    public double MiniTolerancePx { get; set; } = 1.5;

    // 플레이어 점 색 임계(노랑) — UI 미노출, watcher.json 직접 수정으로 조정 가능
    public int DotMinR { get; set; } = 200;
    public int DotMinG { get; set; } = 180;
    public int DotMaxB { get; set; } = 120;
    /// <summary>미니맵 창(어두운 프레임) 판정 밝기 임계 — 이 이하 밝기 픽셀을 '어두움'으로 본다.</summary>
    public int PanelMaxLum { get; set; } = 70;

    // 기준 화면 패치(캐릭터 포함 지형) — 이동에는 쓰지 않고 '서있음' 일치율 참고 표시용.
    /// <summary>매칭/서있음 일치 임계 — 캐릭터 애니메이션·방향 변화 때문에 100%는 안 나온다.</summary>
    public double MinScore { get; set; } = 0.55;
    public int PatchW { get; set; } = 450;
    public int PatchH { get; set; } = 340;

    /// <summary>이동 후 재측정까지 대기(ms) — 키를 뗀 뒤 캐릭터가 관성으로 미끄러져 멈출 시간.</summary>
    public int SettleMs { get; set; } = 500;
    public int MaxCorrectionMs { get; set; } = 10000;
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
}

/// <summary>
/// 위치 지킴이 — '위치 보정' 스텝(<see cref="PositionCorrectEvent"/>)이 실행될 때 캐릭터가 그 블록에
/// 지정된 자리(스팟)에서 벗어났으면 방향키로 되돌린다. 이동은 미니맵 노란 점 기준으로만 하고(절대 위치라
/// 방향 명확, 멀리 벗어나도 복귀), 화면 패치는 이동 없이 '서있음' 일치율 참고 표시에만 쓴다. 블록마다 서로 다른 스팟.
/// 공통 설정은 <c>watcher.json</c>, 스팟은 <c>spots\{id}.json</c> + <c>spots\{id}.png</c>에 저장.
/// </summary>
public sealed class PositionWatcher : IDisposable
{
    private const ushort ScLeft = 0x4B, ScRight = 0x4D;   // 방향키 스캔코드(E0 확장)
    private const ushort ScUp = 0x48, ScDown = 0x50;      // 퍼즐 입력·아래점프용(E0 확장)
    private const ushort KeyDownE0 = 0x02, KeyUpE0 = 0x03;
    private const ushort ScSpace = 0x39, ScV = 0x2F, ScLAlt = 0x38; // 일반 키(E0 아님) — Down=0x00/Up=0x01
    private const int SearchBandPx = 24;                  // 템플릿 탐색 Y 범위(저장 Y ± 이 값)
    private const double CorrectTolY = 3.0;               // 위치 보정 층(Y) 일치 판정 — 같은 발판이면
                                                          // 점 Y가 거의 고정, 층간 간격(보통 5px+)보다 작게
    private const double FlashJumpMinDx = 20.0;           // 목표가 이보다 멀면 방향키+Alt '따닥' 더블점프로 도약
    private const int FlashJumpTapMs = 45, FlashJumpGapMs = 80; // Alt 두 번 '따닥' 탭 길이·간격
    private const int FlashJumpCooldownMs = 700;          // 도약 사이 최소 간격 — 착지·재가속 시간
    private const double RuneIconYOffset = 0.5;           // 목표 Y를 아이콘보다 살짝 아래로. 실측(2026-08-04
                                                          // 17:10 카르시온 나무줄기3, 같은 층): 아이콘 중심 Y=152.0,
                                                          // 내 점 중심 Y=151.2 — 차이 1px 미만이라 오프셋은 최소만
    private const double RopeStuckDy = 3.0;               // 아래점프 후 이만큼도 못 내려갔으면 로프·사다리에 걸림
    private const int RopeSlideRiseMs = 250, RopeSlideMaxMs = 4000; // 로프 회복(↓ 홀드) 착지 폴링
    private const double MinPatchStdDev = 8;              // 패치 대비 하한(단색·특징 부족 거부)

    // 룬 사용 — 도착 판정은 X=RuneTolX(룬 전용, 빡빡), Y=CorrectTolY(위치 보정과 동일)를 쓴다
    // (사용자 지정 2026-08-04). 목표지점은 시작 시 1회만 측정해 고정하고 이후 갱신하지 않는다
    // (룬은 능동적으로 움직이지 않는다) — 도착 순간 내 점이 아이콘을 가리거나 다른 보라 마커(정예 등)가
    // 있어도 목표가 흔들리지 않는다(19:24 '아이콘 놓침' 실패, 19:48 목표 ±135px 널뜀 로그의 원인).
    private const double RuneTolX = 0.6;   // 룬 접근 X 허용오차 — 위치 보정(MiniTolerancePx=1.5)보다
                                           // 빡빡하게 유지(사용자 지정 2026-08-04): 발동 정확도 우선
    private const double RuneTolY = 6.0;   // '점프해도 층 불변' 예외 시에만 쓰는 Y 상한 —
                                           // RuneIconYOffset 추정이 어긋난 경우의 무한 점프 방지용
    private const int RuneMaxMs = 30000;   // 수직 이동 포함 총 제한 — 위치 보정보다 길게
    // 점프 후 대기 일괄 절반 축소(사용자 지정 2026-08-04 "너무 기다린다") — 값은 이전의 1/2.
    private const int DownJumpRiseMs = 225;    // 아래점프 직후 낙하 진입 대기 — 이륙 전 '정지' 오판 방지
    private const int UpJumpRiseMs = 400;      // 윗점프(V) 상승+정점 통과 대기 — 정점의 순간 정지를 착지로 오판 방지
    private const int UpJumpSettleMaxMs = 1250;   // 윗점프 착지 폴링 상한 — 착지 순간 반동(튕김)이 있어 여유
    private const int DownJumpSettleMaxMs = 900;  // 아래점프 착지 폴링 상한
    private const int PostUpJumpMs = 750;      // 윗점프(V) → 거리 판단·스페이스 발동 최소 간격 —
                                               // 점프 궤적 중 룬과 순간 가까워졌다 멀어질 수 있고, 착지
                                               // 반동까지 끝난 '정착 후' 위치로만 판단·발동해야 한다

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

    public object Update(string? process, int? maxCorrectionMs, double? minScore, double? miniTolerancePx)
    {
        object snap;
        lock (_gate)
        {
            if (!string.IsNullOrWhiteSpace(process)) _settings.Process = Normalize(process);
            if (maxCorrectionMs is int mc && mc > 0) _settings.MaxCorrectionMs = mc;
            if (minScore is double ms && ms is > 0 and <= 1) _settings.MinScore = ms;
            if (miniTolerancePx is double mt && mt >= 0) _settings.MiniTolerancePx = mt;
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

    /// <summary>[미니맵 탐지] — 지금 이 순간 1회 자동 감지해 영역을 반환한다(저장은 편집기가
    /// 매크로에 한다). 흰 테두리 안 맵 영역을 우선, 없으면 검은 챠시. 실패 시 예외(수동 지정 안내).</summary>
    public object AutoDetectMinimap()
    {
        WatcherSettings s; lock (_gate) s = Clone(_settings);
        using var frame = CaptureGameFrame(s.Process, out _)
            ?? throw new InvalidOperationException($"'{s.Process}' 창을 찾을 수 없습니다. 게임이 실행 중인지 확인하세요.");
        if (!MinimapDetector.TryDetect(frame, out var panel, out var mapArea, out _, out _,
                                       s.DotMinR, s.DotMinG, s.DotMaxB, s.PanelMaxLum) || panel.IsEmpty)
            throw new ArgumentException("미니맵을 자동으로 찾지 못했습니다 — 미니맵이 펼쳐져 있는지 확인하거나 [수동 지정]을 사용하세요.");
        var r = mapArea.IsEmpty ? panel : mapArea;
        return new { x = r.X, y = r.Y, w = r.Width, h = r.Height };
    }

    /// <summary>[수동 지정] — 마지막 캡처 프레임에서 드래그한 영역을 검증해 반환(저장은 편집기가
    /// 매크로에 한다). 점이 안 보이면 거부.</summary>
    public object SetMinimapRegion(int x, int y, int w, int h)
    {
        lock (_gate)
        {
            var frame = _lastFrame ?? throw new InvalidOperationException("먼저 화면을 캡처하세요.");
            var rect = ClampRect(new Rectangle(x, y, w, h), frame.Width, frame.Height);
            if (rect.Width < 8 || rect.Height < 8) throw new ArgumentException("미니맵 영역이 너무 작습니다.");
            if (!MinimapDetector.TryFindPlayerDot(frame, rect, out _, _settings.DotMinR, _settings.DotMinG, _settings.DotMaxB))
                throw new ArgumentException("선택한 영역에서 플레이어 노란 점을 찾지 못했습니다. 미니맵 맵 영역을 감싸게 다시 드래그하세요.");
            return new { x = rect.X, y = rect.Y, w = rect.Width, h = rect.Height };
        }
    }

    /// <summary>미니맵 확인용 미리보기 — 새 프레임에서 주어진 영역(+여백)을 잘라 영역 테두리(초록)와
    /// 현재 캐릭터 점(노란 링)을 그려 반환. 실패 시 null. (편집기 1열 미니맵 카드 썸네일)</summary>
    public byte[]? MinimapPreview(int mx, int my, int mw, int mh)
    {
        WatcherSettings s; lock (_gate) s = Clone(_settings);
        if (mw <= 0 || mh <= 0) return null;
        using var frame = CaptureGameFrame(s.Process, out _);
        if (frame is null) return null;
        var mini = new Rectangle(mx, my, mw, mh);
        var r = ClampRect(new Rectangle(mini.X - 10, mini.Y - 10, mini.Width + 20, mini.Height + 20), frame.Width, frame.Height);
        if (r.Width <= 0 || r.Height <= 0) return null;
        var cands = MinimapDetector.FindDots(frame, mini, s.DotMinR, s.DotMinG, s.DotMaxB);
        using var crop = frame.Clone(r, frame.PixelFormat);
        using (var g = Graphics.FromImage(crop))
        {
            using var penMini = new Pen(Color.FromArgb(52, 211, 153), 1.6f);
            g.DrawRectangle(penMini, mini.X - r.X, mini.Y - r.Y, mini.Width - 1, mini.Height - 1);
            if (cands.Count > 0)
            {
                var picked = MinimapDetector.Pick(cands).Center;
                using var penOther = new Pen(Color.FromArgb(170, 255, 255, 255), 1.4f); // 흰 링 = 선택 안 된 다른 노란 블롭
                foreach (var c in cands)
                    if (c.Center != picked)
                        g.DrawEllipse(penOther, mini.X + c.Center.X - r.X - 5, mini.Y + c.Center.Y - r.Y - 5, 10, 10);
                using var penDot = new Pen(Color.FromArgb(255, 216, 59), 2f);           // 노란 링 = 내 캐릭터로 선택된 점
                g.DrawEllipse(penDot, mini.X + picked.X - r.X - 6, mini.Y + picked.Y - r.Y - 6, 12, 12);
            }
        }
        return ScreenCapture.ToPng(crop);
    }

    // ---------- 실시간 미리보기(캐릭터 위치 지정 팝업) ----------
    private Bitmap? _liveFrame;  // 마지막 Live() 캡처 — live/frame·live/mini가 같은 프레임을 사용
    private PointF? _liveDot;    // 마지막 Live()에서 감지된 캐릭터 점(창 상대)
    private List<DotCandidate>? _liveCands; // 마지막 Live()의 노란 블롭 후보 전체(창 상대)
    private Rectangle _liveMiniRect;        // 마지막 Live()에 쓴 미니맵 영역 — LiveMini 크롭용

    /// <summary>현재 게임 화면을 캡처해 미니맵 점·자동 패치 rect를 계산한다(키 입력 없음).
    /// mini = 편집 중 매크로의 미니맵 영역(창 상대). 이어지는 LiveFrame/LiveMini가 이 프레임을 사용.</summary>
    public object Live(int mx, int my, int mw, int mh)
    {
        lock (_gate)
        {
            var s = _settings;
            if (mw <= 0 || mh <= 0)
                return new { ok = false, needMinimap = true, error = "이 매크로에 미니맵이 지정되지 않았습니다." };
            var frame = CaptureGameFrame(s.Process, out _);
            if (frame is null)
                return new { ok = false, needMinimap = false, error = $"'{s.Process}' 창을 찾을 수 없습니다." };

            _liveFrame?.Dispose();
            _liveFrame = frame;

            // 매크로의 미니맵 영역 안에서만 캐릭터 점을 찾는다.
            var mini = new Rectangle(mx, my, mw, mh);
            _liveMiniRect = mini;
            var cands = MinimapDetector.FindDots(_liveFrame, mini, s.DotMinR, s.DotMinG, s.DotMaxB)
                .Select(c => c with { Center = new PointF(c.Center.X + mini.X, c.Center.Y + mini.Y) }).ToList();
            bool dotFound = cands.Count > 0;
            var dot = dotFound ? MinimapDetector.Pick(cands).Center : PointF.Empty;
            _liveDot = dotFound ? dot : null;
            _liveCands = cands;
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

    /// <summary>미니맵 미리보기(팝업용) — 마지막 Live()에 쓴 영역(+여백)을 잘라 영역 테두리(초록)와
    /// 내 캐릭터 점(노란 링)을 그려 반환. 점 미탐지/프레임 없음이면 null.</summary>
    public byte[]? LiveMini()
    {
        lock (_gate)
        {
            if (_liveFrame is null || _liveDot is not { } dot || _liveMiniRect.Width <= 0) return null;
            var mini = _liveMiniRect;
            var r = ClampRect(new Rectangle(mini.X - 8, mini.Y - 8, mini.Width + 16, mini.Height + 16),
                              _liveFrame.Width, _liveFrame.Height);
            if (r.Width <= 0 || r.Height <= 0) return null;
            using var crop = _liveFrame.Clone(r, _liveFrame.PixelFormat);
            using (var g = Graphics.FromImage(crop))
            {
                using var penMini = new Pen(Color.FromArgb(52, 211, 153), 1.6f); // 초록 = 고정된 미니맵 영역
                g.DrawRectangle(penMini, mini.X - r.X, mini.Y - r.Y, mini.Width - 1, mini.Height - 1);
                if (_liveCands is { } lc)
                {
                    using var penOther = new Pen(Color.FromArgb(170, 255, 255, 255), 1.4f); // 흰 링 = 다른 노란 블롭
                    foreach (var c in lc)
                        if (c.Center != dot)
                            g.DrawEllipse(penOther, c.Center.X - r.X - 5, c.Center.Y - r.Y - 5, 10, 10);
                }
                using var penDot = new Pen(Color.FromArgb(255, 216, 59), 2f);    // 노랑 = 내 캐릭터 점
                g.DrawEllipse(penDot, dot.X - r.X - 6, dot.Y - r.Y - 6, 12, 12);
            }
            return ScreenCapture.ToPng(crop);
        }
    }

    // ---------- 스팟(블록별 기준 위치) ----------
    /// <summary>확정 — 지금 이 순간의 화면을 새로 캡처해 미니맵 점 + 자동 패치 영역을 스팟으로 저장한다.
    /// (블록 확장 카드에서 실시간 미리보기를 보다가 [확정]을 눌렀을 때)</summary>
    public object CaptureSpot(string id, int? anchorX, int? anchorY, int mx, int my, int mw, int mh)
    {
        RequireValidId(id);
        lock (_gate)
        {
            var s = _settings;
            if (mw <= 0 || mh <= 0) throw new InvalidOperationException("이 매크로에 미니맵이 지정되지 않았습니다 — 편집기 1열의 [미니맵 위치] 카드에서 먼저 지정하세요.");
            using var frame = CaptureGameFrame(s.Process, out _)
                ?? throw new InvalidOperationException($"'{s.Process}' 창을 찾을 수 없습니다. 게임이 실행 중인지 확인하세요.");
            // 매크로의 미니맵 영역 안에서 점 탐지 — Live 미리보기와 같은 기준이라 마커와 일치.
            var mini = new Rectangle(mx, my, mw, mh);
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
    public object TestSpot(string id, int mx, int my, int mw, int mh)
    {
        RequireValidId(id);
        WatcherSettings s; (SpotData Data, GrayImage Gray)? spot;
        lock (_gate) { s = Clone(_settings); spot = ResolveSpot(id); }
        if (spot is not { } sp) return new { error = "지정된 위치가 없습니다. [지정하기]로 저장하세요." };
        if (mw <= 0 || mh <= 0) return new { error = "이 매크로에 미니맵이 지정되지 않았습니다 — 1열의 [미니맵 위치] 카드에서 지정하세요." };

        var cands = MeasureDots(s, new Rectangle(mx, my, mw, mh));
        int candCount = cands?.Count ?? 0;
        double? miniDx = null;
        // 미리보기(Live)와 같은 기준(점 크기 기반)으로 내 캐릭터 점을 고른다 —
        // '저장 위치 근접' 선택은 저장 지점 옆의 정지 블롭(마커 등)에 고정될 수 있다.
        if (cands is { Count: > 0 })
            miniDx = Math.Round(MinimapDetector.Pick(cands).Center.X - sp.Data.DotX, 1);
        double? score = MeasurePatch(s, sp.Data, sp.Gray)?.score;
        return new
        {
            dotFound = miniDx is not null, miniDx,
            inPlace = miniDx is { } m && Math.Abs(m) <= s.MiniTolerancePx, // 서있어야 할 위치에 있는가
            dotCandidates = candCount, // 2개 이상 = 오인 가능(재생 보정은 프로브 이동으로 자동 식별)
            patchFound = score >= s.MinScore, score, // 지형 일치율 — 참고용(이동에는 미사용)
        };
    }

    // ---------- 보정(재생 훅) ----------
    /// <summary>
    /// '위치 보정' 스텝의 실제 수행(Player.PositionCorrect 훅). 스텝의 spotId에 지정된 자리로 되돌린다.
    /// miniCfg = 재생 중 매크로의 미니맵 영역(Macro.MapleMinimap) — 없으면 no-op(상태 방송만).
    /// 취소(정지)는 OCE로 전파해 재생을 즉시 멈추고, 그 외 오류는 삼켜 재생을 계속한다.
    /// 어떤 경로로도 방향키가 눌린 채 남지 않는다. 스팟 미지정이면 no-op(상태 방송만).
    /// </summary>
    public async Task CorrectAsync(MapleMinimap? miniCfg, string? spotId, CancellationToken ct)
    {
        WatcherSettings s; (SpotData Data, GrayImage Gray)? resolved;
        lock (_gate)
        {
            s = Clone(_settings);
            resolved = string.IsNullOrEmpty(spotId) || !IsValidId(spotId) ? null : ResolveSpot(spotId);
        }
        if (resolved is not { } sp) { Status("skip", "이 블록에 지정된 위치가 없어 보정을 건너뜁니다. 편집기에서 [지정하기]로 저장하세요."); return; }
        if (miniCfg is not { W: > 0, H: > 0 }) { Status("skip", "이 매크로에 미니맵 정보가 없어 보정을 건너뜁니다 — 편집기에서 미니맵을 지정하세요."); return; }
        var mini = new Rectangle(miniCfg.X, miniCfg.Y, miniCfg.W, miniCfg.H);
        if (!_sem.Wait(0)) return; // 다른 매크로가 이미 보정 중 → 스킵

        try
        {
            var spot = sp.Data; var patch = sp.Gray;
            if (!WindowLocator.IsForeground(s.Process)) { Status("skip", "게임 창이 전면이 아니라 보정을 건너뜁니다."); return; }
            var sw = Stopwatch.StartNew();

            // ── 1단계: 내 캐릭터 점 식별 — 노란 블롭이 여러 개면(NPC 마커 등) 살짝 걸어보고
            //           '움직인' 블롭을 내 캐릭터로 잠근다(마커·타인 점은 안 움직임) ──
            var dots0 = MeasureDots(s, mini);
            if (dots0 is null || dots0.Count == 0) { Status("fail", "미니맵에서 플레이어 점을 찾지 못해 보정을 포기합니다."); return; }
            PointF? dot;
            if (dots0.Count == 1) dot = dots0[0].Center;
            else
            {
                Status("coarse", $"노란 블롭 {dots0.Count}개 — 살짝 이동해 내 캐릭터를 식별합니다");
                await TapAsync(ScLeft, 90, ct).ConfigureAwait(false); // 왼쪽으로 프로브(어차피 보정으로 되돌아옴)
                await PreciseDelay.WaitAsync(s.SettleMs, ct).ConfigureAwait(false);
                var dots1 = MeasureDots(s, mini);
                if (dots1 is null || dots1.Count == 0) { Status("fail", "미니맵에서 플레이어 점을 찾지 못해 보정을 포기합니다."); return; }
                // 폴백도 저장 위치 근접이 아닌 크기 기반 — 근접 선택은 정지 블롭에 고정될 수 있다.
                dot = IdentifyMovedLeft(dots0, dots1) ?? MinimapDetector.Pick(dots1).Center;
            }
            // ── 2단계: 수평(X) 정렬은 처음 한 번만 — 윗점프(V)·아래점프(↓+Alt)는 X를 옮기지
            //           않으므로(사용자 지정) 이후에는 층(Y)이 맞을 때까지 수직 이동만 반복한다. ──
            var walk = await WalkToXAsync(s, mini, dot.Value, spot.DotX, s.MiniTolerancePx, sw, s.MaxCorrectionMs,
                                          "coarse", "미니맵 보정 중", ct).ConfigureAwait(false);
            if (walk.Result == Walk.NotForeground) { Status("skip", "게임 창이 전면에서 벗어나 보정을 중단합니다."); return; }
            if (walk.Result == Walk.LostDot) { Status("fail", "보정 중 미니맵 점을 놓쳤습니다."); return; }
            var dotNow = walk.Dot;
            double miniDx = dotNow.X - spot.DotX;
            if (walk.Result == Walk.Timeout) { Status("fail", $"보정 시간 초과(미니맵 이탈 {miniDx:+0.0;-0.0}px 남음)."); return; }

            while (true)
            {
                ct.ThrowIfCancellationRequested();
                double miniDy = dotNow.Y - spot.DotY; // +: 캐릭터가 스팟보다 아래(위로 가야 함)
                if (Math.Abs(miniDy) <= CorrectTolY) break; // 층 일치 — 보정 완료
                if (sw.ElapsedMilliseconds >= s.MaxCorrectionMs) { Status("fail", $"보정 시간 초과(층차 {miniDy:+0.0;-0.0}px 남음)."); return; }
                if (!WindowLocator.IsForeground(s.Process)) { Status("skip", "게임 창이 전면에서 벗어나 보정을 중단합니다."); return; }

                if (miniDy > 0)
                {
                    Status("coarse", $"윗점프(V)로 위층 이동 (높이차 {miniDy:+0.0;-0.0}px)");
                    await TapAsync(ScV, 120, ct, e0: false).ConfigureAwait(false);
                }
                else
                {
                    Status("coarse", $"아래점프(↓+Alt)로 아래층 이동 (높이차 {miniDy:+0.0;-0.0}px)");
                    await DownJumpAsync(ct).ConfigureAwait(false);
                }
                var landed = miniDy > 0
                    ? await WaitLandedAsync(s, mini, dotNow, UpJumpRiseMs, UpJumpSettleMaxMs, ct).ConfigureAwait(false)
                    : await WaitDownLandedAsync(s, mini, dotNow, "coarse", ct).ConfigureAwait(false);
                if (landed is null) { Status("fail", "보정 중 미니맵 점을 놓쳤습니다."); return; }
                dotNow = landed.Value;
            }
            miniDx = dotNow.X - spot.DotX; // 완료 보고용 최종 잔차

            // 파인(화면 매칭) 이동은 쓰지 않는다 — 주변 유저·말풍선이 패치에 겹치거나 카메라
            // 레이지무브가 정착 중이면 매칭이 흔들려, 맞춰둔 자리를 오히려 이탈시켰다(18:05 실행 로그).
            // 지형 일치율은 이동 없이 참고용으로만 측정해 완료 메시지에 남긴다.
            var pm = MeasurePatch(s, spot, patch);
            Status("done",
                $"위치 보정 완료 — 미니맵 기준 (잔여 {miniDx:+0.0;-0.0}px){(pm is { } m ? $" · 지형 일치 {m.score * 100:0}%" : "")}",
                score: pm?.score);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { try { Status("fail", "보정 오류: " + ex.Message); } catch { } }
        finally { _sem.Release(); }
    }

    // ---------- 공용 수평 걷기 ----------
    private enum Walk { Arrived, Timeout, LostDot, NotForeground }

    /// <summary>미니맵 X 목표까지 좌우 이동 — 연속 홀드 + 홀드 중 폴링(도착 직전/지나침에 뗌) +
    /// 멀면(>FlashJumpMinDx) Alt 따닥 더블점프 도약 + 남은 거리 비례 5단계 리바운드
    /// (≥6px 280 / ≥4px 220 / ≥2.5px 160 / ≥1.5px 110 / 그 외 50ms) +
    /// 뗀 뒤 SettleMs 대기 후 재측정. 위치 보정·룬 이동 공용.</summary>
    private async Task<(Walk Result, PointF Dot)> WalkToXAsync(WatcherSettings s, Rectangle mini, PointF dot, double targetX,
        double tol, Stopwatch sw, long maxMs, string state, string label, CancellationToken ct)
    {
        double miniDx = dot.X - targetX;
        double releaseEarly = Math.Max(tol, 1.2); // 키를 뗀 뒤 관성 미끄러짐 여유분
        int rebounds = 0; // 도착/지나침 후 반대 방향 재보정 횟수 — 첫 홀드(무제한) 판별용

        while (Math.Abs(miniDx) > tol && sw.ElapsedMilliseconds < maxMs)
        {
            ct.ThrowIfCancellationRequested();
            if (!WindowLocator.IsForeground(s.Process)) return (Walk.NotForeground, dot);
            Status(state, $"{label} (이탈 {miniDx:+0.0;-0.0}px)");
            // 점이 목표보다 오른쪽(+) = 캐릭터가 오른쪽에 있음 → 왼쪽 키
            ushort key = miniDx > 0 ? ScLeft : ScRight;
            int dirSign = Math.Sign(miniDx);
            // 첫 홀드는 도착할 때까지 무제한. 되돌림(미세조정)은 '남은 거리' 기준 5단계(사용자 지정
            // 2026-08-04: 횟수 감쇠는 거리가 남았는데도 짧은 탭만 반복했다) — 멀면 조금 길게 누르고,
            // 가까울수록 짧게. 폴링이 도착 직전/지나침에 어차피 떼므로 상한이 길어도 과주행 위험은 낮다.
            double adx = Math.Abs(miniDx);
            long holdCapMs = rebounds == 0 ? long.MaxValue
                           : adx >= 6.0 ? 280
                           : adx >= 4.0 ? 220
                           : adx >= 2.5 ? 160
                           : adx >= 1.5 ? 110
                           : 50; // 최소 50ms — 진짜 미세조정용(사용자 지정)
            var holdSw = Stopwatch.StartNew();
            long lastFjAt = -FlashJumpCooldownMs; // 홀드 시작 즉시 1회 도약 가능
            _backend.Send(new KeyboardEvent { Code = key, State = KeyDownE0 });
            try
            {
                // 누른 채 연속 이동 — 걷는 동안 주기적으로 위치를 재며 도착 직전 또는 지나침에 뗀다
                while (sw.ElapsedMilliseconds < maxMs)
                {
                    long remain = holdCapMs - holdSw.ElapsedMilliseconds;
                    if (remain <= 0) break;                       // 되돌림 홀드 상한 도달
                    await PreciseDelay.WaitAsync((int)Math.Min(60, remain), ct).ConfigureAwait(false);
                    if (holdCapMs - holdSw.ElapsedMilliseconds <= 0) break;
                    var d2 = MeasureDot(s, mini, dot);
                    if (d2 is null) break; // 점 놓침 → 일단 멈추고 정지 후 재평가
                    dot = d2.Value;
                    miniDx = dot.X - targetX;
                    if (Math.Sign(miniDx) != dirSign) break;      // 목표를 지나침
                    if (Math.Abs(miniDx) <= releaseEarly) break;  // 도착 직전(관성 감안)
                    // 아직 멀면 방향키를 누른 채 Alt 두 번 '따닥' — 그 방향으로 더블점프 도약(사용자 지정).
                    // 가까워지면 걷기만으로 미세 접근한다.
                    if (Math.Abs(miniDx) > FlashJumpMinDx && holdSw.ElapsedMilliseconds - lastFjAt >= FlashJumpCooldownMs)
                    {
                        await TapAsync(ScLAlt, FlashJumpTapMs, ct, e0: false).ConfigureAwait(false);
                        await PreciseDelay.WaitAsync(FlashJumpGapMs, ct).ConfigureAwait(false);
                        await TapAsync(ScLAlt, FlashJumpTapMs, ct, e0: false).ConfigureAwait(false);
                        lastFjAt = holdSw.ElapsedMilliseconds;
                    }
                }
            }
            finally { try { _backend.Send(new KeyboardEvent { Code = key, State = KeyUpE0 }); } catch { } }
            rebounds++;

            // 관성·가속 때문에 키를 뗀 뒤에도 조금 미끄러진다 — 고정 시간만큼 기다렸다 측정
            await PreciseDelay.WaitAsync(s.SettleMs, ct).ConfigureAwait(false);
            var d3 = MeasureDot(s, mini, dot);
            if (d3 is null) return (Walk.LostDot, dot); // 홀드 중 일시 놓침은 허용, 정지 후에도 없으면 실패
            dot = d3.Value;
            miniDx = dot.X - targetX;
            // 아직 밖이면 새 방향으로 다시 홀드(지나쳤으면 자연히 반대 방향으로 짧게 되돌아온다)
        }
        return (Math.Abs(miniDx) <= tol ? Walk.Arrived : Walk.Timeout, dot);
    }

    // ---------- 룬 사용(재생 훅) ----------
    /// <summary>블록 카드 [테스트] — 키 입력 없이 미니맵의 룬 아이콘·내 점 위치와 상대 거리만 측정.</summary>
    public object RuneTest(int mx, int my, int mw, int mh)
    {
        WatcherSettings s; lock (_gate) s = Clone(_settings);
        if (mw <= 0 || mh <= 0) return new { error = "이 매크로에 미니맵이 지정되지 않았습니다 — 1열의 [미니맵 위치] 카드에서 지정하세요." };
        using var frame = CaptureGameFrame(s.Process, out _);
        if (frame is null) return new { error = $"'{s.Process}' 창을 찾을 수 없습니다." };
        var mini = new Rectangle(mx, my, mw, mh);
        var rune = MinimapDetector.FindRuneIcon(frame, mini);
        var cands = MinimapDetector.FindDots(frame, mini, s.DotMinR, s.DotMinG, s.DotMaxB);
        PointF? dot = cands.Count > 0 ? MinimapDetector.Pick(cands).Center : null;
        return new
        {
            runeFound = rune is not null,
            dotFound = dot is not null,
            dx = rune is { } r1 && dot is { } d1 ? Math.Round(d1.X - r1.X, 1) : (double?)null,
            dy = rune is { } r2 && dot is { } d2 ? Math.Round(d2.Y - r2.Y, 1) : (double?)null,
        };
    }

    /// <summary>
    /// '룬 사용' 스텝의 실제 수행(Player.RuneUse 훅). 미니맵의 룬(보라 다이아) 아이콘까지
    /// 수평(걷기) → 수직(윗점프 V / 아래점프 ↓+Alt) 순으로 이동해 스페이스로 발동하고,
    /// 화면에 뜬 방향키 퍼즐(화살표 4개)을 인식해 자동 입력한다.
    /// 미니맵에 룬이 없으면 건너뛰고 다음 스텝 진행. 취소는 OCE로 전파, 그 외 오류는 삼킨다.
    /// </summary>
    public async Task RuneUseAsync(MapleMinimap? miniCfg, CancellationToken ct)
    {
        WatcherSettings s; lock (_gate) s = Clone(_settings);
        if (miniCfg is not { W: > 0, H: > 0 }) { Status("skip", "이 매크로에 미니맵 정보가 없어 룬 사용을 건너뜁니다 — 편집기에서 미니맵을 지정하세요."); return; }
        var mini = new Rectangle(miniCfg.X, miniCfg.Y, miniCfg.W, miniCfg.H);
        if (!_sem.Wait(0)) return; // 다른 매크로가 보정/룬 수행 중 → 스킵

        try
        {
            if (!WindowLocator.IsForeground(s.Process)) { Status("skip", "게임 창이 전면이 아니라 룬 사용을 건너뜁니다."); return; }
            // 룬 존재 확인 — 매크로는 룬 유무와 무관하게 주기적으로 실행되는 전제라, 없으면 '룬 없음'으로
            // 결론 내고 즉시 끝낸다(있을 때만 가서 깐다). 단일 프레임 검출만 믿으면 보라 이펙트·마커
            // 오탐이 '유령 룬' 이동을 유발한다(사용자 관측 2026-08-04) — 룬은 움직이지 않으므로
            // 같은 자리(±2px)에서 3연속 검출될 때만 인정한다. 미탐 프레임이 섞여도 잡게 최대 6프레임 관찰.
            PointF? runeCand = null; int runeHits = 0;
            for (int i = 0; i < 6 && runeHits < 3; i++)
            {
                if (i > 0) await PreciseDelay.WaitAsync(120, ct).ConfigureAwait(false);
                var m = MeasureRune(s, mini);
                if (m is { } p && runeCand is { } c && Math.Abs(p.X - c.X) <= 2 && Math.Abs(p.Y - c.Y) <= 2) runeHits++;
                else if (m is { } p2) { runeCand = p2; runeHits = 1; } // 새 후보(첫 검출 또는 위치 불일치) — 다시 센다
                else { runeCand = null; runeHits = 0; }               // 검출 끊김 — 오탐/이펙트로 보고 리셋
            }
            if (runeHits < 3 || runeCand is not { } runeAt)
            { Status("skip", $"미니맵에 룬 없음(연속 검출 {runeHits}/3) — 이번 회차를 바로 종료합니다."); return; }
            // 목표지점 지정(사용자 지정) — 아이콘은 발판보다 몇 px 위에 그려지므로 '아이콘보다 약간 아래'
            // 즉 실제 발판 높이를 목표로 잡는다. 이후 이동·도착 판정은 전부 이 지점 기준.
            runeAt.Y += (float)RuneIconYOffset;
            var sw = Stopwatch.StartNew();

            // ── 1단계: 내 캐릭터 점 식별(위치 보정과 동일 — 여러 개면 프로브 이동으로 확인) ──
            var dots0 = MeasureDots(s, mini);
            if (dots0 is null || dots0.Count == 0) { Status("fail", "미니맵에서 플레이어 점을 찾지 못해 룬 사용을 포기합니다."); return; }
            PointF dot;
            if (dots0.Count == 1) dot = dots0[0].Center;
            else
            {
                Status("rune", $"노란 블롭 {dots0.Count}개 — 살짝 이동해 내 캐릭터를 식별합니다");
                await TapAsync(ScLeft, 90, ct).ConfigureAwait(false);
                await PreciseDelay.WaitAsync(s.SettleMs, ct).ConfigureAwait(false);
                var dots1 = MeasureDots(s, mini);
                if (dots1 is null || dots1.Count == 0) { Status("fail", "미니맵에서 플레이어 점을 찾지 못해 룬 사용을 포기합니다."); return; }
                dot = IdentifyMovedLeft(dots0, dots1) ?? MinimapDetector.Pick(dots1).Center;
            }

            // ── 2단계: 룬까지 이동(수평 정렬 → 수직 점프 반복) — 발동 직전 위치 복귀에도 재사용 ──
            long lastUpJumpAt = -1; // 마지막 윗점프(V) 시각 — 발동 전 최소 간격 보장용
            // 반환: 0=도착, 1=창 전면 아님, 2=점 놓침, 3=시간 초과. 도착 시 윗점프 최소 간격까지 보장.
            async Task<int> MoveToRuneAsync(long maxMs)
            {
                var swm = Stopwatch.StartNew();
                bool vStuck = false; // 점프로도 층이 안 바뀜 — 수직 이동의 물리적 한계 도달(무한 점프 방지)

                // 수평(X) 정렬은 처음 한 번만 — 윗점프(V)·아래점프(↓+Alt)는 X를 옮기지 않으므로
                // (사용자 지정) 이후에는 층(Y)이 맞을 때까지 수직 이동만 반복한다.
                if (Math.Abs(dot.X - runeAt.X) > RuneTolX)
                {
                    var walk = await WalkToXAsync(s, mini, dot, runeAt.X, RuneTolX, swm, maxMs, "rune", "룬으로 이동 중", ct).ConfigureAwait(false);
                    if (walk.Result == Walk.NotForeground) return 1;
                    if (walk.Result == Walk.LostDot) return 2;
                    dot = walk.Dot;
                }

                while (swm.ElapsedMilliseconds < maxMs)
                {
                    ct.ThrowIfCancellationRequested();
                    if (!WindowLocator.IsForeground(s.Process)) return 1;
                    // Y 허용오차는 위치 보정과 동일(사용자 지정): CorrectTolY. 단 룬 아이콘 오프셋
                    // 추정이 어긋나 점프로는 잔차를 더 못 줄이는 경우('점프해도 층 불변' vStuck)에는
                    // 기존 룬 허용오차(RuneTolY)까지만 인정한다.
                    double tolY = vStuck ? RuneTolY : CorrectTolY;
                    double dyOff = dot.Y - runeAt.Y; // +: 캐릭터가 룬보다 아래(위로 가야 함)
                    if (Math.Abs(dyOff) <= tolY) break; // 도착 — 시작 시 지정한 목표지점 기준

                    if (dyOff > 0)
                    {
                        Status("rune", $"윗점프(V)로 위층 이동 (높이차 {dyOff:+0.0;-0.0}px)");
                        await TapAsync(ScV, 120, ct, e0: false).ConfigureAwait(false);
                        lastUpJumpAt = sw.ElapsedMilliseconds;
                    }
                    else
                    {
                        Status("rune", $"아래점프(↓+Alt)로 아래층 이동 (높이차 {dyOff:+0.0;-0.0}px)");
                        await DownJumpAsync(ct).ConfigureAwait(false);
                    }
                    // 착지·정지 폴링 — 윗점프(V)는 착지 순간 반동(튕김)이 있어 고정 대기로는 이르다.
                    // 미니맵 점이 연속 3표본 정지할 때까지 본 뒤에 다음 판단으로 넘어간다.
                    var landed = dyOff > 0
                        ? await WaitLandedAsync(s, mini, dot, UpJumpRiseMs, UpJumpSettleMaxMs, ct).ConfigureAwait(false)
                        : await WaitDownLandedAsync(s, mini, dot, "rune", ct).ConfigureAwait(false);
                    if (landed is null) return 2;
                    // 점프했는데 Y가 그대로면 이 방향으로는 층 이동 불가(이미 룬 층이거나 맵 끝) —
                    // 반복해봐야 무한 점프라, 남은 잔차는 RuneTolY 한도 안에서 인정하고 진행한다.
                    if (Math.Abs(landed.Value.Y - dot.Y) < 1.0)
                    {
                        vStuck = true;
                        Status("rune", $"점프해도 층이 안 바뀜 — 현재 층에서 진행(높이 잔차 {landed.Value.Y - runeAt.Y:+0.0;-0.0}px)");
                    }
                    dot = landed.Value;

                    // 윗점프 후에는 거리 판단도 V로부터 최소 1.5초 뒤에(사용자 지정) — 점프 궤적 중
                    // 룬과 순간 가까워졌다 다시 멀어질 수 있어, 시간이 찬 뒤 위치를 재측정해 판단한다.
                    if (dyOff > 0)
                    {
                        long sinceV = sw.ElapsedMilliseconds - lastUpJumpAt;
                        if (sinceV < PostUpJumpMs)
                            await PreciseDelay.WaitAsync((int)(PostUpJumpMs - sinceV), ct).ConfigureAwait(false);
                        var settled = MeasureDot(s, mini, dot);
                        if (settled is null) return 2;
                        dot = settled.Value;
                    }
                }

                // 최종 도착 확인 — 시작 시 지정한 목표지점 기준(재측정 없음). X는 룬 전용(빡빡), Y는 보정과 동일
                if (Math.Abs(dot.X - runeAt.X) > RuneTolX || Math.Abs(dot.Y - runeAt.Y) > (vStuck ? RuneTolY : CorrectTolY))
                    return 3;

                // 윗점프 직후 곧장 발동하지 않는다(사용자 지정) — 착지 반동까지 완전히 끝난 뒤
                // 스페이스를 눌러야 상호작용이 씹히지 않는다. 마지막 V로부터 최소 간격을 보장.
                if (lastUpJumpAt >= 0)
                {
                    long sinceUpJump = sw.ElapsedMilliseconds - lastUpJumpAt;
                    if (sinceUpJump < PostUpJumpMs)
                        await PreciseDelay.WaitAsync((int)(PostUpJumpMs - sinceUpJump), ct).ConfigureAwait(false);
                }
                return 0;
            }

            switch (await MoveToRuneAsync(RuneMaxMs).ConfigureAwait(false))
            {
                case 1: Status("skip", "게임 창이 전면에서 벗어나 룬 사용을 중단합니다."); return;
                case 2: Status("fail", "이동 중 미니맵 점을 놓쳤습니다."); return;
                case 3:
                    Status("fail", $"룬 도달 시간 초과(잔여 dx {dot.X - runeAt.X:+0.0;-0.0}px · dy {dot.Y - runeAt.Y:+0.0;-0.0}px).");
                    return;
            }

            // ── 3단계: 발동 + 방향키 퍼즐 ──
            // 발동 직전 프레임을 보관 — 퍼즐 인식이 이 프레임과의 차분으로 배경(나무·이펙트)을 배제한다.
            // 취소 타이머(3초) 안에 끝내기 위해 판정 캡처는 '퍼즐 영역만 화면 복사'로 뜬다(전체 창
            // PrintWindow는 장당 ~120ms). 게임이 전면인 상태에서만 이 단계에 오므로 화면 복사가 유효하다.
            Status("rune", "룬 도착 — 스페이스로 발동합니다");
            var beforeFrame = CaptureGameFrame(s.Process, out var winRect);
            Bitmap? beforeCrop = null;
            var puzzleReg = Rectangle.Empty;   // 창 상대 퍼즐 영역
            var screenCrop = Rectangle.Empty;  // 화면 절대 좌표
            if (beforeFrame is not null && winRect.Width > 0)
            {
                puzzleReg = Rectangle.Intersect(
                    RuneArrowDetector.PuzzleRegion(beforeFrame.Width, beforeFrame.Height),
                    new Rectangle(0, 0, beforeFrame.Width, beforeFrame.Height));
                screenCrop = new Rectangle(winRect.X + puzzleReg.X, winRect.Y + puzzleReg.Y, puzzleReg.Width, puzzleReg.Height);
                beforeCrop = beforeFrame.Clone(puzzleReg, beforeFrame.PixelFormat);
            }
            try
            {
                if (screenCrop.Width < 100) { Status("fail", "게임 창을 찾지 못해 룬 발동을 중단합니다."); return; }
                // 발동은 스페이스 딱 한 번, 재발동·재시도 없음(사용자 지정 2026-08-04 18:13) —
                // 열린 퍼즐에 스페이스는 '오답 입력'이라 어떤 재발동도 퍼즐을 날릴 위험이 있다
                // (18:05 실행: 닫힘 오판 → 재발동 스페이스가 오답으로 들어가 1차 퍼즐 소실).
                // 스페이스 후에는 무조건 열렸다고 전제하고 인식 1회 — 실패하면 오류로 띄우고 끝.
                // 인식 실패는 재시도로 덮지 않고 증거(rune-*.png·rune-solve.txt)로 남겨 고친다.

                // 넉백 재확인·복귀 없음(사용자 지정 — 캐릭터는 몹에게 맞아도 밀려나지 않는다).
                // 도착 → 스페이스 → 인식, 끝.
                if (!WindowLocator.IsForeground(s.Process)) { Status("skip", "게임 창이 전면에서 벗어나 룬 발동을 중단합니다."); return; }
                await TapAsync(ScSpace, 100, ct, e0: false).ConfigureAwait(false);
                var spaceSw = Stopwatch.StartNew();
                await PreciseDelay.WaitAsync(120, ct).ConfigureAwait(false);

                int budget = Math.Max(900, RunePuzzleSolver.PuzzleBudgetMs - (int)spaceSw.ElapsedMilliseconds);
                ClearRuneShots();
                var arrows = await SolvePuzzleAsync(screenCrop, beforeCrop, budget, ct).ConfigureAwait(false);
                if (arrows is null)
                {
                    SaveRuneShots(beforeCrop, includeStrips: true); // 실패 증거 — 재시도 대신 이걸로 인식을 고친다
                    FileLog.SnapshotRune(); // 다음 시도(성공 포함)가 고정 이름을 덮어써도 실패 증거는 폴더로 남긴다
                    Status("fail", "룬 퍼즐 인식 실패 — 직접 입력해 주세요(logs\\rune-fail-* 폴더 확인). 이번 회차를 종료합니다.");
                    return;
                }

                // 취소 타이머(3초) 안에 입력이 시작돼야 한다 — 인식 즉시 입력부터, 로그·저장은 뒤로.
                // 입력 직전 화면 1장을 따로 캡처(사용자 지시 2026-08-04: 확정 방향과 입력 순간의
                // 실제 글리프 대조용) — 인코딩이 느려 캡처만 먼저, 저장은 입력 후.
                Bitmap? inputShot = null;
                try { inputShot = ScreenCapture.Capture(screenCrop); } catch { /* 캡처 실패 — 입력 우선 */ }
                foreach (var a in arrows)
                {
                    ct.ThrowIfCancellationRequested();
                    ushort code = a.Dir switch { 'L' => ScLeft, 'R' => ScRight, 'U' => ScUp, _ => ScDown };
                    await TapAsync(code, 80, ct).ConfigureAwait(false);
                    await PreciseDelay.WaitAsync(120, ct).ConfigureAwait(false);
                }
                var seq = string.Join(" ", arrows.Select(a => a.Dir switch { 'L' => '←', 'R' => '→', 'U' => '↑', _ => '↓' }));
                Status("rune", $"퍼즐 인식: {seq} — 입력했습니다");
                if (inputShot is not null)
                {
                    try { FileLog.SavePng("rune-input", ScreenCapture.ToPng(inputShot)); } catch { }
                    finally { inputShot.Dispose(); }
                }
                // 스페이스 발동 후에는 결과 무관하게 전부 저장(사용자 지시 2026-08-04) — 스트립 포함.
                // 14:32 실행: 오확정 입력이었는데 성공 경로라 스트립·트레이스가 없어 원인 추적 불가였다.
                SaveRuneShots(beforeCrop, includeStrips: true);

                // 입력 후 퍼즐(배너)이 사라졌는지 확인 — 화살표 재탐지는 룬 해제 이펙트를 오인할 수 있어
                // 배너 잔존 여부로 판정한다(23:48 실행: 성공인데 이펙트를 화살표로 재탐지해 실패 경고).
                // 성공 직후엔 '룬 해방' 보상 배너(진짜 어두운 띠+텍스트)가 떠서 배너 신호만으로는
                // 퍼즐 잔존과 구분이 안 된다(11:10 실행: 정답인데 경고) — '배너 + 화살표 줄' 둘 다
                // 남아 있어야 오답 잔존으로 판단하고, 이펙트가 가라앉을 시간을 두고 최대 3회 재확인.
                await PreciseDelay.WaitAsync(900, ct).ConfigureAwait(false);
                bool stillOpen = false;
                for (int chk = 0; chk < 3; chk++)
                {
                    try
                    {
                        using var va = ScreenCapture.Capture(screenCrop);
                        await PreciseDelay.WaitAsync(180, ct).ConfigureAwait(false);
                        using var vb = ScreenCapture.Capture(screenCrop);
                        stillOpen = RuneArrowDetector.PuzzlePresent(va, vb, beforeCrop, precropped: true)
                                    && RuneArrowDetector.AnalyzeFrame(vb, va, beforeCrop, precropped: true) is not null;
                    }
                    catch { stillOpen = false; /* 캡처 실패 — 검증 생략 */ }
                    if (!stillOpen) break;
                    await PreciseDelay.WaitAsync(800, ct).ConfigureAwait(false);
                }
                if (stillOpen)
                {
                    FileLog.SnapshotRune(); // 오답 의심 증거도 폴더로 보존 — 다음 시도가 고정 이름을 덮어쓴다
                    Status("fail", "퍼즐 입력 후에도 퍼즐이 남아 있습니다 — 인식이 틀렸을 수 있어요(logs\\rune-fail-* 폴더 확인).");
                }
                else
                    Status("done", $"룬 사용 완료 (퍼즐 {seq})");
            }
            catch (OperationCanceledException)
            {
                // 취소(매크로 중단)로 검증·저장 전에 끊겨도 증거는 남긴다(14:32 실행: 입력 후 무로그 종료).
                // 반드시 ClearRuneShots(아래 finally)보다 먼저 저장해야 한다.
                try { Status("rune", "룬 처리 취소(매크로 중단) — 증거 저장 후 종료"); SaveRuneShots(beforeCrop, includeStrips: true); } catch { }
                throw;
            }
            finally { beforeFrame?.Dispose(); beforeCrop?.Dispose(); ClearRuneShots(); }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { try { Status("fail", "룬 사용 오류: " + ex.Message); } catch { } }
        finally { _sem.Release(); }
    }

    /// <summary>미니맵 영역에서 룬(보라 다이아) 아이콘 위치(미니맵 상대). 없으면 null.
    /// 룬 사용 시작 시 1회만 호출된다 — 이후에는 갱신하지 않는다.
    /// 측정 시점의 미니맵 크롭을 logs\rune-minimap.png로 남긴다 — 오탐(색이 비슷한 NPC 마커를
    /// 룬으로 판정해 말을 걸었던 08-04 사례)·미탐 진단은 이 크롭으로만 가능하다.</summary>
    private PointF? MeasureRune(WatcherSettings s, Rectangle mini)
    {
        using var frame = CaptureGameFrame(s.Process, out _);
        if (frame is null) return null;
        var rune = MinimapDetector.FindRuneIcon(frame, mini);
        try
        {
            var crop = Rectangle.Intersect(mini, new Rectangle(0, 0, frame.Width, frame.Height));
            if (crop.Width > 0 && crop.Height > 0)
            {
                using var mc = frame.Clone(crop, frame.PixelFormat);
                FileLog.SavePng("rune-minimap", ScreenCapture.ToPng(mc));
            }
        }
        catch { /* 진단 저장 실패 무시 */ }
        return rune;
    }

    private readonly List<Bitmap> _runeShots = new(); // 마지막 판정 버스트 프레임 — 진단 저장은 판정 뒤로 미룬다
    private readonly List<Bitmap> _runeStrips = new(); // 화살표 밴드 스트립 연속 녹화(링) — 회전 반동 파형 재현용

    /// <summary>화살표 밴드만 잘라 링 버퍼에 녹화 — 실패 시 rune-strip-NN.png로 덤프해
    /// 회전 화살표의 반동(격발) 파형을 오프라인에서 재현·튜닝한다.</summary>
    private void RecordStrip(Bitmap f)
    {
        try
        {
            var r = RuneArrowDetector.ArrowBandRect(f.Width, f.Height);
            var s = f.Clone(r, f.PixelFormat);
            _runeStrips.Add(s);
            if (_runeStrips.Count > StripKeep) { _runeStrips[0].Dispose(); _runeStrips.RemoveAt(0); }
        }
        catch { /* 진단 녹화 실패 무시 */ }
    }

    // 퍼즐 확정 파라미터·판정 로직은 RunePuzzleSolver로 이동(2026-08-04 모듈화) — 여기는
    // 캡처·대기·취소·비트맵 수명(스케줄링)만 남는다.
    private const int StripKeep = 90;           // 실패 진단용 밴드 스트립 녹화 링 크기(고속 50ms 기준 ~4.5초)

    /// <summary>퍼즐 화살표 4개 확정 — 판정은 <see cref="RunePuzzleSolver"/>(실전·오프라인 공용),
    /// 이 메서드는 캡처·대기 산술·취소·비트맵 수명(스케줄링)만 담당한다. 고속 모드에서는 무거운
    /// 줄 인식을 끄고 로컬 분석만 50ms 주기로 돈다(캡처 ~15ms + 로컬 4개 ~8ms라 주기 유지 가능;
    /// 반동은 순간이라 촘촘함이 생명). 위치 고정 후에는 무거운 줄 인식 사이에 가벼운 로컬 샘플을
    /// 한 번 더 끼워 각도 샘플링을 ~2배 조밀하게 만든다(반동 포착률↑ — 1바퀴 <1초 사용자 확인).</summary>
    private async Task<List<RuneArrow>?> SolvePuzzleAsync(Rectangle screenCrop, Bitmap? beforeCrop, int budgetMs, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var solver = new RunePuzzleSolver(beforeCrop, budgetMs, precropped: true, note: Note);
        var win = new List<Bitmap>(4); // 직전 프레임 창(최대 3장, 오래된 순) — 채도 교집합·정지 게이트 기준
        Bitmap? prevFast = null;       // 고속 모드의 직전 틱 프레임(진단 MovingPx용)
        int fastTick = 0;              // 고속 모드 틱 카운터 — 주기적 줄 재선출 체크용
        try
        {
            while (sw.ElapsedMilliseconds < solver.BudgetMs && solver.LockedCount < 4)
            {
                ct.ThrowIfCancellationRequested();

                if (solver.FastMode)
                {
                    long tickStart = sw.ElapsedMilliseconds;
                    Bitmap? ff = null;
                    try { ff = ScreenCapture.Capture(screenCrop); } catch { /* 일시적 캡처 실패 */ }
                    if (ff is not null)
                    {
                        try
                        {
                            solver.StepLocal(ff, prevFast, sw.ElapsedMilliseconds);
                            solver.TryRelocate(ff, sw.ElapsedMilliseconds);
                            RecordStrip(ff);
                            if (_runeShots.Count < 4) _runeShots.Add(ff);
                            // 잘못 채택된 줄의 복구 기회 — 8틱(~0.4초)마다 무거운 줄 인식을 끼워
                            // 더 강한 줄이 보이면 재선출(솔버 StepHeavyRow 주석 참조).
                            if (++fastTick % 8 == 0 && sw.ElapsedMilliseconds < 2500)
                                solver.StepHeavyRow(ff, win, () => sw.ElapsedMilliseconds);
                        }
                        finally
                        {
                            // 직전 틱 프레임으로 보관(움직임 마스크 기준) — 이전 것은 정리
                            if (prevFast is not null && !_runeShots.Contains(prevFast)) prevFast.Dispose();
                            prevFast = ff;
                        }
                    }
                    long wait = RunePuzzleSolver.FastTickMs - (sw.ElapsedMilliseconds - tickStart);
                    if (solver.LockedCount < 4 && wait > 0) await PreciseDelay.WaitAsync((int)wait, ct).ConfigureAwait(false);
                    continue;
                }

                Bitmap? f = null;
                try { f = ScreenCapture.Capture(screenCrop); } catch { /* 일시적 캡처 실패 */ }
                if (f is not null)
                {
                    // finally로 f의 소유권을 win/_runeShots에 반드시 넘긴다 — 분석이 예외를 던져도
                    // (캡처 자원 고갈 등) f가 리스트 어딘가에 있어 정리 경로에서 dispose된다
                    try { solver.StepFrame(f, win, () => sw.ElapsedMilliseconds); }
                    finally
                    {
                        if (_runeShots.Count < 4) _runeShots.Add(f); // 진단 보관(첫 4프레임)
                        RecordStrip(f);                              // 실패 진단용 밴드 스트립 녹화(링)
                        win.Add(f);
                        while (win.Count > 3)
                        {
                            var old = win[0]; win.RemoveAt(0);
                            if (!_runeShots.Contains(old)) old.Dispose();
                        }
                    }
                }
                // 위치 고정 후 중간 로컬 표본 1회 — 각도 샘플링 ~2배 조밀화(반동 포착률↑)
                if (solver.PosAcquired && solver.LockedCount < 4 && sw.ElapsedMilliseconds < solver.BudgetMs)
                {
                    await PreciseDelay.WaitAsync(35, ct).ConfigureAwait(false);
                    Bitmap? f2 = null;
                    try { f2 = ScreenCapture.Capture(screenCrop); } catch { /* 일시적 캡처 실패 */ }
                    if (f2 is not null)
                    {
                        try { solver.StepLocal(f2, win.Count > 0 ? win[^1] : null, sw.ElapsedMilliseconds); RecordStrip(f2); }
                        finally { f2.Dispose(); }
                    }
                }
                if (solver.LockedCount < 4) await PreciseDelay.WaitAsync(RunePuzzleSolver.PuzzleSampleGapMs, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            foreach (var b in win) if (!_runeShots.Contains(b)) b.Dispose();
            if (prevFast is not null && !_runeShots.Contains(prevFast)) prevFast.Dispose();
        }
        var arrows = solver.Confirm(out string traceWhy, out string noteMsg);
        try { FileLog.SaveText("rune-solve", solver.BuildTrace(traceWhy, sw.ElapsedMilliseconds)); }
        catch { /* 진단 저장 실패 무시 */ }
        Note(noteMsg);
        return arrows;
    }

    /// <summary>마지막 판정 버스트를 logs\rune-frame-N.png·rune-puzzle.png로 저장(오답·실패 재현용,
    /// --rune-analyze 다중 입력). includeStrips = 밴드 스트립 녹화(rune-strip-NN)도 덤프 — 인코딩이
    /// 수 초 걸리므로 인식 실패 경로에서만 켠다. PNG 인코딩이 느려 반드시 판정·입력이 끝난 뒤에 호출.</summary>
    private void SaveRuneShots(Bitmap? beforeCrop = null, bool includeStrips = false)
    {
        if (_runeShots.Count == 0) return;
        FileLog.DeletePngs("rune-frame-"); // 이전 실행 잔재(프레임 수·크기가 다르면 재현 분석이 깨진다)
        for (int i = 0; i < _runeShots.Count; i++)
            FileLog.SavePng($"rune-frame-{i}", ScreenCapture.ToPng(_runeShots[i]));
        FileLog.SavePng("rune-puzzle", ScreenCapture.ToPng(_runeShots[^1]));
        if (beforeCrop is not null) FileLog.SavePng("rune-before", ScreenCapture.ToPng(beforeCrop)); // 차분 기준 재현용
        if (includeStrips)
        {
            FileLog.DeletePngs("rune-strip-");
            for (int i = 0; i < _runeStrips.Count; i++)
                FileLog.SavePng($"rune-strip-{i:00}", ScreenCapture.ToPng(_runeStrips[i]));
        }
    }

    private void ClearRuneShots()
    {
        foreach (var f in _runeShots) f.Dispose();
        _runeShots.Clear();
        foreach (var s in _runeStrips) s.Dispose();
        _runeStrips.Clear();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _liveFrame?.Dispose(); _liveFrame = null;
            _lastFrame?.Dispose(); _lastFrame = null;
        }
        ClearRuneShots();
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
            // 마이그레이션 → 현행: 점 좌표는 '미니맵 영역 상대'.
            // 창 상대(DotFrame)였으면 구버전 전역 미니맵 원점을 빼서 변환(없으면 재지정 필요).
            if (!data.DotRel)
            {
                if (data.DotFrame && LegacyMinimap is { } lm) { data.DotX -= lm.X; data.DotY -= lm.Y; }
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

    /// <summary>프레임을 찍어 매크로의 미니맵 영역 안에서 플레이어 점(미니맵 상대, 서브픽셀)을 찾는다.
    /// 창/점 미탐지면 null. near = 직전(또는 저장) 위치(미니맵 상대) — 추적으로 다른 점으로 튀는 것을 막는다.</summary>
    /// <summary>점프 후 착지·정지 폴링 — 상승 최소 대기(<see cref="JumpRiseMs"/>, 이륙 전 '정지'
    /// 오판 방지) 후 미니맵 점이 연속 3표본(±1px, ~240ms) 움직이지 않으면 착지로 판정하고 그
    /// 좌표를 돌려준다. 윗점프(V)는 착지 순간 반동으로 한 번 더 튀므로 고정 대기로는 이르다 —
    /// 반동이 끝나 완전히 정지해야 통과된다. 상한까지 안정되지 않으면 마지막 측정값으로 진행.</summary>
    private async Task<PointF?> WaitLandedAsync(WatcherSettings s, Rectangle mini, PointF last, int riseMs, int maxMs, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        await PreciseDelay.WaitAsync(riseMs, ct).ConfigureAwait(false);
        PointF? prev = null;
        int stable = 0;
        while (sw.ElapsedMilliseconds < maxMs)
        {
            ct.ThrowIfCancellationRequested();
            var d = MeasureDot(s, mini, last);
            if (d is not null)
            {
                if (prev is { } p && Math.Abs(d.Value.Y - p.Y) <= 1.0 && Math.Abs(d.Value.X - p.X) <= 1.5)
                {
                    if (++stable >= 2) return d; // 직전과 2연속 일치 = 3표본 정지
                }
                else stable = 0;
                prev = d;
                last = d.Value;
            }
            await PreciseDelay.WaitAsync(120, ct).ConfigureAwait(false);
        }
        return prev ?? MeasureDot(s, mini, last);
    }

    private PointF? MeasureDot(WatcherSettings s, Rectangle mini, PointF? near = null)
    {
        var cands = MeasureDots(s, mini);
        if (cands is null || cands.Count == 0) return null;
        return MinimapDetector.Pick(cands, near).Center; // 미니맵 상대 좌표
    }

    /// <summary>미니맵 영역 안의 점 후보 블롭 전체(미니맵 상대). 창 미탐지면 null.</summary>
    private List<DotCandidate>? MeasureDots(WatcherSettings s, Rectangle mini)
    {
        if (mini.Width <= 0 || mini.Height <= 0) return null;
        using var frame = CaptureGameFrame(s.Process, out _);
        if (frame is null) return null;
        return MinimapDetector.FindDots(frame, mini, s.DotMinR, s.DotMinG, s.DotMaxB);
    }

    /// <summary>프로브 이동(왼쪽 90ms) 전/후 블롭을 근접 매칭해 '왼쪽으로 움직인' 블롭을 찾는다 =
    /// 내 캐릭터. NPC 마커·다른 유저 점은 프로브에 반응하지 않는다. 없으면 null.</summary>
    private static PointF? IdentifyMovedLeft(List<DotCandidate> before, List<DotCandidate> after)
    {
        PointF? best = null; double bestDx = -0.6; // 이보다 더 왼쪽으로 움직인 블롭만 인정
        foreach (var b in after)
        {
            DotCandidate? nearest = null; double nd = double.MaxValue;
            foreach (var a in before)
            {
                double d = (a.Center.X - b.Center.X) * (a.Center.X - b.Center.X)
                         + (a.Center.Y - b.Center.Y) * (a.Center.Y - b.Center.Y);
                if (d < nd) { nd = d; nearest = a; }
            }
            if (nearest is not { } a2) continue;
            double dx = b.Center.X - a2.Center.X;
            double dy = Math.Abs(b.Center.Y - a2.Center.Y);
            if (dx <= bestDx && dy <= 2.0) { bestDx = dx; best = b.Center; }
        }
        return best;
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

    /// <summary>아래점프 — ↓를 누른 채 Alt 한 번 탭(꾹 유지 없음, 사용자 지정 2026-08-04).
    /// 낙하 중 로프·사다리를 잡아버리는 문제는 <see cref="WaitDownLandedAsync"/>의 예외처리가 맡는다.</summary>
    private async Task DownJumpAsync(CancellationToken ct)
    {
        _backend.Send(new KeyboardEvent { Code = ScDown, State = KeyDownE0 });
        try
        {
            await PreciseDelay.WaitAsync(60, ct).ConfigureAwait(false);
            await TapAsync(ScLAlt, 90, ct, e0: false).ConfigureAwait(false);
        }
        finally { try { _backend.Send(new KeyboardEvent { Code = ScDown, State = KeyUpE0 }); } catch { } }
    }

    /// <summary>아래점프 후 착지 대기 + 로프 예외처리 — 착지 Y가 출발과 거의 같으면 낙하 중
    /// 로프·사다리를 잡은 것으로 보고, ↓를 꾹 눌러(이 예외에서만 홀드) 바닥까지 타고 내려간다.</summary>
    private async Task<PointF?> WaitDownLandedAsync(WatcherSettings s, Rectangle mini, PointF from, string state, CancellationToken ct)
    {
        var landed = await WaitLandedAsync(s, mini, from, DownJumpRiseMs, DownJumpSettleMaxMs, ct).ConfigureAwait(false);
        if (landed is { } p && p.Y - from.Y < RopeStuckDy)
        {
            Status(state, "아래점프가 로프·사다리에 걸린 듯 — ↓를 눌러 끝까지 타고 내려갑니다");
            _backend.Send(new KeyboardEvent { Code = ScDown, State = KeyDownE0 });
            try { landed = await WaitLandedAsync(s, mini, p, RopeSlideRiseMs, RopeSlideMaxMs, ct).ConfigureAwait(false); }
            finally { try { _backend.Send(new KeyboardEvent { Code = ScDown, State = KeyUpE0 }); } catch { } }
        }
        return landed;
    }

    /// <summary>키 1회 탭(누르고 holdMs 뒤 뗌). e0=false면 일반 키(스페이스·V·Alt 등).</summary>
    private async Task TapAsync(ushort code, double holdMs, CancellationToken ct, bool e0 = true)
    {
        _backend.Send(new KeyboardEvent { Code = code, State = e0 ? KeyDownE0 : (ushort)0x00 });
        try { await PreciseDelay.WaitAsync(holdMs, ct).ConfigureAwait(false); }
        finally { try { _backend.Send(new KeyboardEvent { Code = code, State = e0 ? KeyUpE0 : (ushort)0x01 }); } catch { } }
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

    /// <summary>상태 변화의 C# 구독용(오버레이 디버그 '진행 단계' 표시) — (state, message).</summary>
    public event Action<string, string>? StatusChanged;

    private void Status(string state, string message, int? miniDx = null, int? dx = null, double? score = null)
    {
        _hub.Broadcast("watcherStatus", new { state, message, miniDx, dx, score });
        FileLog.Write(state is "fail" ? "warn" : "info", $"[위치보정:{state}] {message}");
        try { StatusChanged?.Invoke(state, message); } catch { /* 구독자 오류 무시 */ }
    }

    /// <summary>진행 노트(로그+오버레이 디버그) — 룬 퍼즐 인식 경로·결과 등 세부 단계.</summary>
    private void Note(string message)
    {
        FileLog.Write("info", "[위치보정:rune] " + message);
        try { StatusChanged?.Invoke("note", message); } catch { /* 구독자 오류 무시 */ }
    }

    private void Broadcast() { lock (_gate) _hub.Broadcast("watcherSettings", Snapshot(_settings)); }

    private object Snapshot(WatcherSettings s) => new
    {
        process = s.Process,
        minScore = s.MinScore, miniTolerancePx = s.MiniTolerancePx,
        settleMs = s.SettleMs, maxCorrectionMs = s.MaxCorrectionMs,
    };

    // ---------- 영속 ----------
    /// <summary>구버전 전역 미니맵 영역(watcher.json에 남아 있던 값). Program이 시작 시 메이플 블록이
    /// 있는 매크로들로 이관한 뒤 <see cref="ClearLegacyMinimap"/>를 호출해 정리한다.</summary>
    public (int X, int Y, int W, int H)? LegacyMinimap { get; private set; }

    /// <summary>레거시 전역 미니맵 정리 — watcher.json을 현행 스키마(미니맵 필드 없음)로 다시 쓴다.</summary>
    public void ClearLegacyMinimap()
    {
        LegacyMinimap = null;
        lock (_gate) Save(_settings);
    }

    private sealed class LegacyMiniFields
    {
        public int MiniX { get; set; }
        public int MiniY { get; set; }
        public int MiniW { get; set; }
        public int MiniH { get; set; }
    }

    private WatcherSettings Load()
    {
        WatcherSettings s;
        try
        {
            var json = File.Exists(_statePath) ? File.ReadAllText(_statePath) : null;
            s = json is null ? new() : JsonSerializer.Deserialize<WatcherSettings>(json) ?? new();
            // 구버전 전역 미니맵 필드가 남아 있으면 보관 — 시작 시 매크로별로 이관된다.
            if (json is not null)
            {
                var lm = JsonSerializer.Deserialize<LegacyMiniFields>(json);
                if (lm is { MiniW: > 0, MiniH: > 0 }) LegacyMinimap = (lm.MiniX, lm.MiniY, lm.MiniW, lm.MiniH);
            }
        }
        catch { return new(); }
        // 구버전 기본값 마이그레이션(사용자가 직접 조정한 값은 유지).
        if (s.MiniTolerancePx == 1) s.MiniTolerancePx = 0.6;
        if (s.SettleMs is 150 or 220 or 350) s.SettleMs = 500; // 관성·가속 정지 대기 확대
        // 패치를 '캐릭터 포함 창 중앙'으로 통합하면서 크기/임계 기본값 변경
        if (s.PatchW is 120 or 180) s.PatchW = 450; // 캐릭터 주변 범위 약 2.5배 확대
        if (s.PatchH is 64 or 140) s.PatchH = 340;
        if (s.MinScore == 0.60) s.MinScore = 0.55;
        if (s.MaxCorrectionMs is 6000 or 8000) s.MaxCorrectionMs = 10000; // 정지 대기 확대에 맞춰 여유 확대
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
        MiniTolerancePx = s.MiniTolerancePx,
        DotMinR = s.DotMinR, DotMinG = s.DotMinG, DotMaxB = s.DotMaxB, PanelMaxLum = s.PanelMaxLum,
        MinScore = s.MinScore, PatchW = s.PatchW, PatchH = s.PatchH,
        SettleMs = s.SettleMs, MaxCorrectionMs = s.MaxCorrectionMs,
    };
}
