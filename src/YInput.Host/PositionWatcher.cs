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
    /// <summary>미니맵 허용오차(px). 점은 서브픽셀 centroid로 재므로 1 미만도 의미 있다.</summary>
    public double MiniTolerancePx { get; set; } = 0.6;

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
    private const double MinPatchStdDev = 8;              // 패치 대비 하한(단색·특징 부족 거부)

    // 룬 사용 — 룬은 상호작용 범위가 넓어 위치 보정보다 허용오차를 느슨하게 잡는다.
    private const double RuneTolX = 2.0;   // 미니맵 px
    private const double RuneTolY = 6.0;   // 층(발판) 일치 — 다이아 아이콘 중심이 발판보다 몇 px 위에 그려진다
    // 도착 판정 범위 — 내 노란 점이 룬 아이콘에 겹칠(가릴) 정도로 가까우면 도착.
    // 룬 위치는 시작 시 1회만 측정하고 이후 갱신하지 않는다(룬은 능동적으로 움직이지 않는다) —
    // 도착 순간 내 점이 아이콘을 가리거나 다른 보라 마커(정예 등)가 있어도 목표가 흔들리지 않는다
    // (19:24 '아이콘 놓침' 실패, 19:48 목표 ±135px 널뜀 로그의 원인).
    private const double OccludeNearX = 5.0, OccludeNearY = 9.0;
    private const int RuneMaxMs = 30000;   // 수직 이동 포함 총 제한 — 위치 보정보다 길게
    private const int JumpSettleMs = 1000; // 점프(윗점프/아래점프) 후 착지·정지 대기

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
            var walk = await WalkToXAsync(s, mini, dot.Value, spot.DotX, s.MiniTolerancePx, sw, s.MaxCorrectionMs,
                                          "coarse", "미니맵 보정 중", ct).ConfigureAwait(false);
            if (walk.Result == Walk.NotForeground) { Status("skip", "게임 창이 전면에서 벗어나 보정을 중단합니다."); return; }
            if (walk.Result == Walk.LostDot) { Status("fail", "보정 중 미니맵 점을 놓쳤습니다."); return; }
            double miniDx = walk.Dot.X - spot.DotX;
            if (walk.Result == Walk.Timeout) { Status("fail", $"보정 시간 초과(미니맵 이탈 {miniDx:+0.0;-0.0}px 남음)."); return; }

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

    /// <summary>미니맵 X 목표까지 좌우 걷기 — 연속 홀드 + 홀드 중 폴링(도착 직전/지나침에 뗌) +
    /// 감쇠 리바운드(220→132→79→50ms) + 뗀 뒤 SettleMs 대기 후 재측정. 위치 보정·룬 이동 공용.</summary>
    private async Task<(Walk Result, PointF Dot)> WalkToXAsync(WatcherSettings s, Rectangle mini, PointF dot, double targetX,
        double tol, Stopwatch sw, long maxMs, string state, string label, CancellationToken ct)
    {
        double miniDx = dot.X - targetX;
        double releaseEarly = Math.Max(tol, 1.2); // 키를 뗀 뒤 관성 미끄러짐 여유분
        int rebounds = 0; // 도착/지나침 후 반대 방향 재보정 횟수 — 누를수록 짧게(최소 50ms)

        while (Math.Abs(miniDx) > tol && sw.ElapsedMilliseconds < maxMs)
        {
            ct.ThrowIfCancellationRequested();
            if (!WindowLocator.IsForeground(s.Process)) return (Walk.NotForeground, dot);
            Status(state, $"{label} (이탈 {miniDx:+0.0;-0.0}px)");
            // 점이 목표보다 오른쪽(+) = 캐릭터가 오른쪽에 있음 → 왼쪽 키
            ushort key = miniDx > 0 ? ScLeft : ScRight;
            int dirSign = Math.Sign(miniDx);
            // 첫 홀드는 도착할 때까지 무제한, 이후 되돌림은 220→132→79→50ms로 점점 짧게 눌러 미세 조정
            long holdCapMs = rebounds == 0 ? long.MaxValue
                                           : Math.Max(50, (long)(220 * Math.Pow(0.6, rebounds - 1)));
            var holdSw = Stopwatch.StartNew();
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
            // 룬 위치는 여기서 1회만 측정 — 이후 갱신하지 않는다(맵 이동·재접속 전까지 룬은 그대로).
            if (MeasureRune(s, mini) is not { } runeAt) { Status("skip", "미니맵에 룬(보라 다이아) 아이콘이 없어 건너뜁니다."); return; }
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

            // ── 2단계: 수평 먼저 정렬 → 수직 점프 1회 → 다시 수평 재확인 반복(점프로 X가 흐트러질 수 있음) ──
            while (sw.ElapsedMilliseconds < RuneMaxMs)
            {
                ct.ThrowIfCancellationRequested();
                if (!WindowLocator.IsForeground(s.Process)) { Status("skip", "게임 창이 전면에서 벗어나 룬 사용을 중단합니다."); return; }
                if (Math.Abs(dot.X - runeAt.X) <= OccludeNearX && Math.Abs(dot.Y - runeAt.Y) <= OccludeNearY)
                    break; // 도착 — 시작 시 측정한 룬 위치 기준(아이콘이 내 점에 가려져도 무관)
                var rune = runeAt;

                if (Math.Abs(dot.X - rune.X) > RuneTolX)
                {
                    var walk = await WalkToXAsync(s, mini, dot, rune.X, RuneTolX, sw, RuneMaxMs, "rune", "룬으로 이동 중", ct).ConfigureAwait(false);
                    if (walk.Result == Walk.NotForeground) { Status("skip", "게임 창이 전면에서 벗어나 룬 사용을 중단합니다."); return; }
                    if (walk.Result == Walk.LostDot) { Status("fail", "이동 중 미니맵 점을 놓쳤습니다."); return; }
                    dot = walk.Dot;
                    if (walk.Result == Walk.Timeout) break;
                    continue; // 수평 정렬됨 — 다음 회차에서 수직 재평가
                }

                double dyOff = dot.Y - rune.Y; // +: 캐릭터가 룬보다 아래(위로 가야 함)
                if (Math.Abs(dyOff) <= RuneTolY) break; // 도착

                if (dyOff > 0)
                {
                    Status("rune", $"윗점프(V)로 위층 이동 (높이차 {dyOff:+0.0;-0.0}px)");
                    await TapAsync(ScV, 120, ct, e0: false).ConfigureAwait(false);
                }
                else
                {
                    Status("rune", $"아래점프(↓+Alt)로 아래층 이동 (높이차 {dyOff:+0.0;-0.0}px)");
                    _backend.Send(new KeyboardEvent { Code = ScDown, State = KeyDownE0 });
                    try
                    {
                        await PreciseDelay.WaitAsync(60, ct).ConfigureAwait(false);
                        await TapAsync(ScLAlt, 90, ct, e0: false).ConfigureAwait(false);
                        await PreciseDelay.WaitAsync(60, ct).ConfigureAwait(false);
                    }
                    finally { try { _backend.Send(new KeyboardEvent { Code = ScDown, State = KeyUpE0 }); } catch { } }
                }
                await PreciseDelay.WaitAsync(JumpSettleMs, ct).ConfigureAwait(false); // 착지·정지 대기
                var d = MeasureDot(s, mini, dot);
                if (d is null) { Status("fail", "이동 중 미니맵 점을 놓쳤습니다."); return; }
                dot = d.Value;
            }

            // 최종 도착 확인 — 시작 시 측정한 룬 위치 기준(재측정 없음)
            if (Math.Abs(dot.X - runeAt.X) > OccludeNearX || Math.Abs(dot.Y - runeAt.Y) > OccludeNearY)
            {
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
                // 퍼즐은 스페이스 후 ~100ms 안에 열리고, '입력이 3초간 없으면' 자동 취소된다.
                // 주의: 퍼즐이 열린 동안 스페이스를 또 누르면 '오답 입력'으로 처리돼 실패한다 —
                // 발동당 스페이스는 딱 한 번, 재발동은 취소가 확실히 지난 뒤에만.
                List<RuneArrow>? arrows = null;
                for (int attempt = 0; attempt < 2 && arrows is null; attempt++)
                {
                    // 발동·캡처 중에도 게임이 전면이어야 한다 — 다른 창이 덮이면 스페이스가 그 창으로
                    // 들어가고 캡처에도 그 창이 찍힌다(20:37 실행: IDE가 덮여 인식 실패).
                    if (!WindowLocator.IsForeground(s.Process)) { Status("skip", "게임 창이 전면에서 벗어나 룬 발동을 중단합니다."); return; }
                    if (attempt > 0)
                    {
                        // 열린 퍼즐에 스페이스를 누르면 '오답 입력'이 되어 실패한다(21:57 실행) —
                        // 화면에서 배너가 실제로 사라진 것을 확인한 뒤에만 재발동한다(고정 시간 가정 금지).
                        Status("rune", "퍼즐 인식 실패 — 퍼즐이 닫히기를 기다렸다 다시 발동합니다");
                        bool open = true;
                        var closeSw = Stopwatch.StartNew();
                        while (open && closeSw.ElapsedMilliseconds < 8000)
                        {
                            await PreciseDelay.WaitAsync(400, ct).ConfigureAwait(false);
                            try
                            {
                                using var f = ScreenCapture.Capture(screenCrop);
                                open = RuneArrowDetector.PuzzlePresent(f, beforeCrop, precropped: true);
                            }
                            catch { /* 일시적 캡처 실패 — 다음 폴링 */ }
                        }
                        if (open) { Status("fail", "퍼즐이 닫히지 않아 재발동을 포기합니다."); return; }
                        await PreciseDelay.WaitAsync(300, ct).ConfigureAwait(false);
                    }
                    await TapAsync(ScSpace, 100, ct, e0: false).ConfigureAwait(false);
                    await PreciseDelay.WaitAsync(120, ct).ConfigureAwait(false);
                    var winSw = Stopwatch.StartNew();
                    while (arrows is null && winSw.ElapsedMilliseconds < 1500)
                        arrows = await DetectArrowsAsync(screenCrop, beforeCrop, ct).ConfigureAwait(false);
                }
                if (arrows is null)
                {
                    SaveRuneShots(); // 실패 재현용 — 시간 제약이 끝났으니 이제 저장
                    Status("fail", "룬 퍼즐 화살표를 인식하지 못했습니다 — 직접 입력해 주세요(logs\\rune-puzzle.png 확인).");
                    return;
                }

                // 취소 타이머(3초) 안에 입력이 시작돼야 한다 — 인식 즉시 입력부터, 로그·저장은 뒤로
                foreach (var a in arrows)
                {
                    ct.ThrowIfCancellationRequested();
                    ushort code = a.Dir switch { 'L' => ScLeft, 'R' => ScRight, 'U' => ScUp, _ => ScDown };
                    await TapAsync(code, 80, ct).ConfigureAwait(false);
                    await PreciseDelay.WaitAsync(120, ct).ConfigureAwait(false);
                }
                var seq = string.Join(" ", arrows.Select(a => a.Dir switch { 'L' => '←', 'R' => '→', 'U' => '↑', _ => '↓' }));
                Status("rune", $"퍼즐 인식: {seq} — 입력했습니다");
                SaveRuneShots(); // 판정에 쓴 버스트 저장(오답 재현용)

                // 입력 후 퍼즐이 사라졌는지 확인 — 남아 있으면 인식이 틀렸을 가능성
                await PreciseDelay.WaitAsync(900, ct).ConfigureAwait(false);
                if (await DetectArrowsAsync(screenCrop, beforeCrop, ct).ConfigureAwait(false) is not null)
                    Status("fail", "퍼즐 입력 후에도 화살표가 남아 있습니다 — 인식이 틀렸을 수 있어요(logs\\rune-puzzle.png 확인).");
                else
                    Status("done", $"룬 사용 완료 (퍼즐 {seq})");
            }
            finally { beforeFrame?.Dispose(); beforeCrop?.Dispose(); ClearRuneShots(); }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { try { Status("fail", "룬 사용 오류: " + ex.Message); } catch { } }
        finally { _sem.Release(); }
    }

    /// <summary>미니맵 영역에서 룬(보라 다이아) 아이콘 위치(미니맵 상대). 없으면 null.
    /// 룬 사용 시작 시 1회만 호출된다 — 이후에는 갱신하지 않는다.</summary>
    private PointF? MeasureRune(WatcherSettings s, Rectangle mini)
    {
        using var frame = CaptureGameFrame(s.Process, out _);
        if (frame is null) return null;
        return MinimapDetector.FindRuneIcon(frame, mini);
    }

    private readonly List<Bitmap> _runeShots = new(); // 마지막 판정 버스트 프레임 — 진단 저장은 판정 뒤로 미룬다

    /// <summary>화면에서 룬 퍼즐 화살표 4개 탐지. 주 경로: ~0.3초간 4프레임을 찍어 연속 차분의
    /// 합집합으로 '하이라이트가 쓸고 지나가는' 화살표 전체를 채운다(바·배경·배너는 정지라 배제).
    /// 애니메이션이 없으면 발동 전 프레임 차분으로 폴백.
    /// screenCrop = 퍼즐 영역의 화면 절대 좌표 — 전체 창 PrintWindow(~120ms/장) 대신 영역만
    /// 화면 복사(~15ms/장)해 취소 타이머(3초) 안에 끝낸다. beforeCrop = 발동 직전 프레임의 같은 영역.
    /// 디스크 저장 등 느린 작업은 하지 않는다 — 프레임은 _runeShots에 보관했다가
    /// 판정·입력이 끝난 뒤 <see cref="SaveRuneShots"/>로 저장.</summary>
    private async Task<List<RuneArrow>?> DetectArrowsAsync(Rectangle screenCrop, Bitmap? beforeCrop, CancellationToken ct)
    {
        var frames = new List<Bitmap>(4);
        for (int i = 0; i < 4; i++)
        {
            if (i > 0) await PreciseDelay.WaitAsync(90, ct).ConfigureAwait(false);
            try { frames.Add(ScreenCapture.Capture(screenCrop)); } catch { /* 일시적 캡처 실패 무시 */ }
        }
        List<RuneArrow>? res = null;
        if (frames.Count >= 2)
        {
            res = RuneArrowDetector.FindArrowsAnimated(frames, beforeCrop, precropped: true);
            if (res is not null) FileLog.Write("info", "[위치보정:rune] 퍼즐 인식 경로: 애니메이션 차분");
        }
        if (res is null && frames.Count > 0)
        {
            res = RuneArrowDetector.FindArrows(frames[^1], beforeCrop, beforeCrop, precropped: true);
            if (res is not null) FileLog.Write("info", "[위치보정:rune] 퍼즐 인식 경로: 발동 전 차분 폴백");
        }
        // 진단 보관은 '첫 버스트'(퍼즐이 확실히 열려 있던 순간) — 이후 버스트가 덮어쓰지 않는다
        if (_runeShots.Count == 0) _runeShots.AddRange(frames);
        else foreach (var f in frames) f.Dispose();
        return res;
    }

    /// <summary>마지막 판정 버스트를 logs\rune-frame-N.png·rune-puzzle.png로 저장(오답·실패 재현용,
    /// --rune-analyze 다중 입력). PNG 인코딩이 느려 반드시 판정·입력이 끝난 뒤에 호출한다.</summary>
    private void SaveRuneShots()
    {
        for (int i = 0; i < _runeShots.Count; i++)
            FileLog.SavePng($"rune-frame-{i}", ScreenCapture.ToPng(_runeShots[i]));
        if (_runeShots.Count > 0)
            FileLog.SavePng("rune-puzzle", ScreenCapture.ToPng(_runeShots[^1]));
    }

    private void ClearRuneShots()
    {
        foreach (var f in _runeShots) f.Dispose();
        _runeShots.Clear();
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

    private void Status(string state, string message, int? miniDx = null, int? dx = null, double? score = null)
    {
        _hub.Broadcast("watcherStatus", new { state, message, miniDx, dx, score });
        FileLog.Write(state is "fail" ? "warn" : "info", $"[위치보정:{state}] {message}");
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
