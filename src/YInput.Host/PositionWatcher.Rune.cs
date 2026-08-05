using System.Diagnostics;
using System.Drawing;
using YInput.Core.Models;
using YInput.Engine;
using YInput.Host.Vision;

namespace YInput.Host;

/// <summary>룬 미니게임 오케스트레이션(partial) — 미니맵 룬 감지→이동→발동→퍼즐 해석→입력→검증→증거.
/// 퍼즐 판정은 <see cref="RunePuzzleSolver"/>, 인식은 <see cref="RuneArrowDetector"/>,
/// 각도·반동은 <see cref="RuneAngleTracker"/> — 여기는 이동·키 입력·캡처 스케줄·증거 저장만.
/// 이동 헬퍼(WalkToXAsync·점프·착지 폴링)는 위치 보정과 공유라 PositionWatcher.cs에 있다.</summary>
public sealed partial class PositionWatcher
{
    private const double RuneIconYOffset = 0.5;           // 목표 Y를 아이콘보다 살짝 아래로. 실측(2026-08-04
                                                          // 17:10 카르시온 나무줄기3, 같은 층): 아이콘 중심 Y=152.0,
                                                          // 내 점 중심 Y=151.2 — 차이 1px 미만이라 오프셋은 최소만
    // 룬 사용 — 도착 판정은 X=RuneTolX(룬 전용, 빡빡), Y=CorrectTolY(위치 보정과 동일)를 쓴다
    // (사용자 지정 2026-08-04). 목표지점은 시작 시 1회만 측정해 고정하고 이후 갱신하지 않는다
    // (룬은 능동적으로 움직이지 않는다) — 도착 순간 내 점이 아이콘을 가리거나 다른 보라 마커(정예 등)가
    // 있어도 목표가 흔들리지 않는다(19:24 '아이콘 놓침' 실패, 19:48 목표 ±135px 널뜀 로그의 원인).
    private const double RuneTolX = 0.6;   // 룬 접근 X 허용오차 — 위치 보정(MiniTolerancePx=1.5)보다
                                           // 빡빡하게 유지(사용자 지정 2026-08-04): 발동 정확도 우선
    private const double RuneTolY = 6.0;   // '점프해도 층 불변' 예외 시에만 쓰는 Y 상한 —
                                           // RuneIconYOffset 추정이 어긋난 경우의 무한 점프 방지용
    private const int RuneMaxMs = 30000;   // 수직 이동 포함 총 제한 — 위치 보정보다 길게

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
            { Status("skip", $"미니맵 룬 안정 검출 실패({runeHits}/3연속·±2px) — 룬 부재 또는 가림·이펙트로 보고 이번 회차를 종료합니다."); return; }
            // 목표지점 지정(사용자 지정) — 아이콘은 발판보다 몇 px 위에 그려지므로 '아이콘보다 약간 아래'
            // 즉 실제 발판 높이를 목표로 잡는다. 이후 이동·도착 판정은 전부 이 지점 기준.
            runeAt.Y += (float)RuneIconYOffset;
            var sw = Stopwatch.StartNew();

            // ── 1단계: 내 캐릭터 점 식별(위치 보정과 동일 — 여러 개면 프로브 이동으로 확인) ──
            var dots0 = MeasureDots(s, mini);
            if (dots0 is null || dots0.Count == 0) { Status("fail", "미니맵에서 플레이어 점을 찾지 못해 룬 사용을 포기합니다(첫 측정)."); return; }
            PointF dot;
            if (dots0.Count == 1) dot = dots0[0].Center;
            else
            {
                Status("rune", $"노란 블롭 {dots0.Count}개 — 살짝 이동해 내 캐릭터를 식별합니다");
                await TapAsync(ScLeft, 90, ct).ConfigureAwait(false);
                await PreciseDelay.WaitAsync(s.SettleMs, ct).ConfigureAwait(false);
                var dots1 = MeasureDots(s, mini);
                if (dots1 is null || dots1.Count == 0) { Status("fail", "미니맵에서 플레이어 점을 찾지 못해 룬 사용을 포기합니다(프로브 이동 후 재측정)."); return; }
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
                // 옛 문구 "게임 창을 찾지 못해"는 창은 찾았는데 크롭이 극소인 경우까지 뭉뚱그렸다 —
                // 실측 폭을 박아 원인(창 미탐지/최소화/잘림)을 구분 가능하게(2026-08-05 로그 개편).
                if (screenCrop.Width < 100) { Status("fail", $"퍼즐 영역 확보 실패(크롭 폭 {screenCrop.Width}px < 100 — 창 미탐지·최소화·잘림) — 룬 발동을 중단합니다."); return; }
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
                    var failDir = FileLog.SnapshotRune(); // 다음 시도(성공 포함)가 고정 이름을 덮어써도 실패 증거는 폴더로 남긴다
                    Status("fail", $"룬 퍼즐 인식 실패 — 직접 입력해 주세요(증거 logs\\{failDir ?? "rune-fail-*"}). 이번 회차를 종료합니다.");
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
                // 입력까지 간 시도는 결과 무관하게 폴더로도 보존 — 오답 입력은 퍼즐이 닫혀 아래
                // 잔존 감지가 성공으로 오판하므로(2026-08-05 09:20 실전: R D L D 오답, 스냅샷 없음
                // → 다음 시도가 증거를 덮을 뻔) 고정 이름만으로는 증거가 못 산다. 14일 자동 정리.
                var attemptDir = FileLog.SnapshotRune("rune-attempt-");

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
                    var failDir = FileLog.SnapshotRune(); // 오답 의심 증거도 폴더로 보존 — 다음 시도가 고정 이름을 덮어쓴다
                    Status("fail", $"퍼즐 입력 후에도 배너+화살표가 남아 있습니다 — 인식이 틀렸을 수 있어요(증거 logs\\{failDir ?? "rune-fail-*"}).");
                }
                else
                    // '완료'는 '퍼즐 닫힘 확인'일 뿐 정답 보증이 아니다 — 오답 입력도 퍼즐이 닫힌다
                    // (2026-08-05 09:20 실전). 증거 폴더를 로그가 직접 안내한다(로그 개편).
                    Status("done", $"룬 사용 완료 — 입력 {seq} 후 퍼즐 닫힘 확인(오답도 닫히므로 정답 보증은 아님 · 증거 logs\\{attemptDir ?? "rune-attempt-*"})");
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
        // 시계 앵커 — 이 라인의 앱로그 시각이 rune-solve.txt의 t=0. 이후 솔버 노트·트레이스의
        // t(ms)를 앱로그 타임스탬프와 바로 대조할 수 있다(2026-08-05 로그 개편).
        Note($"퍼즐 관찰 시작 — 기본 예산 {budgetMs}ms, 이 시각이 판정 t=0");
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
}
