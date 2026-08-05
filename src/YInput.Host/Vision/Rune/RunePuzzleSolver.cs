using System.Drawing;

namespace YInput.Host.Vision;

/// <summary>룬 퍼즐 4화살표 확정 솔버 — 프레임 구동형 상태기계. 캡처·대기·취소·비트맵 수명은
/// 호출자(실전 루프 또는 오프라인 재현기) 소유이고, 솔버는 주입받은 프레임과 시각(tMs)으로
/// 판정만 한다. 벽시계를 직접 읽지 않으므로 같은 프레임 시퀀스는 항상 같은 판정을 낸다 —
/// 실전과 오프라인 리플레이가 이 한 클래스를 공유한다(미러 손복제 제거, 2026-08-04 모듈화).
/// <b>시도당 1인스턴스</b> — 상태 리셋 누락을 원천 차단(재사용 금지).
///
/// 판정 구조(원 PositionWatcher.SolvePuzzleAsync 903–1298에서 이동): 매 프레임
/// ① 검증된 줄 인식(교집합)으로 정지 화살표 시그니처 락, ② 줄이 안 잡히면 합집합 마스크로
/// 위치만 획득, ③ 위치 확보 후 화살표별 로컬 분석 — 정지는 로컬 시그니처 런, 회전은 각도
/// 시계열 반동(스텝 급감·역행) 방위 투표. 4개 모두 확정되어야 입력한다(회전 중 표본 다수결은
/// 오답 — 00:41 ↑↑↑← 실입력 사례).</summary>
internal sealed class RunePuzzleSolver
{
    // 퍼즐 확정 파라미터. 두 종류의 화살표(사용자 확인: 색·배경 완전 랜덤, 회전 개수 0~4 랜덤):
    //  · 정지 화살표 — 모양·방향이 유지되는 시그니처 런으로 확정(검증된 경로)
    //  · 회전 화살표 — 절대 멈추지 않는다. 정답 방향을 지날 때마다 '격발 반동'(순간 딸깍)이
    //    한 번씩 있을 뿐 → 각도를 연속 추적해 각속도 이상(스텝 급감·역행)이 생기는 방향을
    //    여러 바퀴에 걸쳐 모아 최빈 방위로 확정한다.
    internal const int PuzzleBudgetMs = 2800;    // 정지형 기본 예산 — 보통 0.5초 안에 4/4 확정된다
    internal const int RotatingBudgetMs = 4000;  // 회전 감지 시 연장 — 반동 2회 관찰에 충분(사용자 지정 4초)
    internal const int PuzzleSampleGapMs = 70;   // 프레임 간격(캡처+분석 포함 실효 ~110-150ms)
    internal const int LockRun = 3;              // 정지 확정 최소 연속 프레임
    internal const int LockSpanMs = 250;         // 정지 확정 최소 지속시간
    internal const int FastTickMs = 50;          // 회전 판정 후 고속 관찰 주기(사용자 지정 — 1바퀴 <1초라 촘촘히)
    internal const int MinSlotSepPx = 24;        // 슬롯 관측점 최소 이격 — 미만이면 같은 화살표 중복 관측(실측:
                                                 // 20:38 오답 입력 때 2·3번이 15px, 진짜 이웃 화살표는 ≥48px)
    internal const int PosBox = 64;              // 슬롯 로컬 관측 박스 한 변

    /// <summary>StepLocal 한 회의 슬롯별 판독 — 오프라인 재현기의 f-라인 출력용(실전은 무시).
    /// Margin = 방향 분류 마진(합의 잠금 캘리브레이션 창구 — f-라인 M토큰).</summary>
    internal readonly record struct SlotReading(
        bool WasLocked, char LockedDir, bool Analyzed, char Dir, double AngleDeg, int Area, int MovingPx,
        bool RotActive, bool NewlyLocked, double Margin = 0);

    private readonly Bitmap? _beforeRef;   // 발동 직전 기준 프레임(차분 배제용) — 수명은 호출자 소유
    private readonly bool _precropped;     // 입력이 이미 PuzzleRegion 크롭인가(실전 true)
    private readonly Action<string>? _note; // 사용자 가시 진행 노트(실전 Note / 오프라인 sb)
    private readonly Action<string>? _diag; // 세부 진단(반동표·기각 — 실전 null / 오프라인 sb)

    // ── 슬롯 상태 (원 SolvePuzzleAsync 클로저 변수 1:1) ──
    private readonly char?[] _locked = new char?[4];
    private int _lockedCount;
    private readonly PointF[] _centers = new PointF[4];
    // 무거운 줄 경로 시그니처 런
    private readonly char[] _runDir = new char[4];
    private readonly int[] _runLen = new int[4];
    private readonly long[] _runStart = new long[4];
    private readonly bool[][] _runSig = new bool[4][];
    private bool _rowSeen, _spinNoted;
    // 위치 컨센서스 — 줄 인식(교집합)이 두 번 연속 일치하면 고정. 회전 화살표의 교집합 핵은
    // 실제 중심에서 ±30px까지 어긋날 수 있어 로컬 박스를 넉넉히 잡고, 이후 로컬 블롭
    // 중심으로 서서히 자기 보정한다(EMA).
    private PointF[]? _pos;
    private PointF[]? _posAnchor; // pos 확정 시점의 원본 — EMA 미끄러짐 클램프 기준
    // 회전 추적 — 화살표별 (시각 ms, 각도°) 시계열과 반동 방위 투표
    private readonly List<double>[] _angleT = [new(), new(), new(), new()];
    private readonly List<double>[] _angleV = [new(), new(), new(), new()];
    private readonly int[,] _votes = new int[4, 4]; // [화살표, 방위 R U L D]
    private bool _fastMode, _rotatingSeen;
    private readonly int[] _lastRotAt = [-999, -999, -999, -999]; // 화살표별 마지막 '회전 중' 표본 번호
    // 로컬 정지 확정용 런 상태 — 무거운 줄 경로의 런과 소스가 달라(교집합 마스크 vs 로컬 글리프)
    // 같은 배열을 쓰면 서로 리셋만 반복한다. 독립 이중화: 둘 중 먼저 안정되는 쪽이 확정.
    private readonly char[] _lRunDir = new char[4];
    private readonly int[] _lRunLen = new int[4];
    private readonly long[] _lRunStart = new long[4];
    private readonly bool[][] _lRunSig = new bool[4][];
    // 무거운 줄 경로의 슬롯별 최근 판독 — 로컬 정지 잠금과 충돌하면 잠금을 보류한다(사용자
    // 검수 2026-08-04). 20:38 실전: 관측점이 정확히 → 화살표 위였는데 로컬 sat140 추출이
    // 글리프 일부만 잡아(a273/a420) 주축 244°로 일관 왜곡 → 축 안정까지 통과해 D 오답 잠금.
    // 무거운 경로는 같은 블롭을 매 프레임 R로 정확히 분류하고 있었다 — 옳은 경로가 지는
    // 경쟁을 막는다. 줄이 안 잡히는 맵(10:39 로컬 단독)은 판독이 낡아(600ms↑) 영향 없음.
    private readonly char[] _heavyDir = new char[4];
    private readonly long[] _heavyDirAt = [-9999, -9999, -9999, -9999];
    private readonly float[] _heavyX = new float[4];
    // 시그니처 최근 이력 — 반짝임(스파클) 이펙트가 각도를 흔들어 정지 화살표가 회전으로
    // 오인되는 것 방지(2026-08-04 위아래위위 룬: 정지 ↑가 가짜 반동 D 2표로 오답 확정).
    // 3연속 동일 모양 = 정지. 회전 글리프는 매 프레임 모양이 변하고, 반동 멈칫은 2~3프레임이라
    // '직전 3개 전부 유사'에 도달하기 전에 투표가 끝난다.
    private readonly List<bool[]>[] _sigHist = [new(), new(), new(), new()];
    private bool _relocated;
    private double _adoptedRowArea;
    private string? _fusedContrib; // 융합 줄 채택 시 소스 기여 요약 — BuildTrace 말미에 기록
    // 로그 자기설명용 추적(판정에는 미사용) — 2026-08-05 로그 개편: 위치가 어느 경로(④/⑦/⑦w/⑧·
    // 교체/보정/재선출/재배치)로 언제 확보됐는지, 슬롯이 언제 잠겼는지 트레이스에 남긴다.
    private string? _posSource;
    private long _posAt = -1;
    private readonly long[] _lockedAt = new long[4];
    // 3소스 합의 잠금용 판독 저장(2026-08-05 14:01 오답 입력 — ④ heavy가 ←를 R로 오분류해
    // 단독 잠금한 사건의 재설계) — heavy(④줄)·로컬(sat체인)·웜(웜차분)의 슬롯별 최신
    // (방향, 마진, 시각[, X]). 이 커밋(C1)은 수집 배관만, 판정 결합(ConsensusAllows)은 C3.
    private readonly double[] _heavyMargin = new double[4];
    private readonly char[] _localDir = new char[4];
    private readonly long[] _localDirAt = [-9999, -9999, -9999, -9999];
    private readonly double[] _localMargin = new double[4];
    private readonly char[] _warmDir = new char[4];
    private readonly long[] _warmDirAt = [-9999, -9999, -9999, -9999];
    private readonly float[] _warmX = new float[4];
    private readonly double[] _warmMargin = new double[4];

    internal RunePuzzleSolver(Bitmap? beforeRef, int initialBudgetMs, bool precropped,
                              Action<string>? note = null, Action<string>? diag = null)
    {
        _beforeRef = beforeRef;
        BudgetMs = initialBudgetMs;
        _precropped = precropped;
        _note = note;
        _diag = diag;
    }

    internal int LockedCount => _lockedCount;
    internal bool FastMode => _fastMode;
    internal bool RowSeen => _rowSeen;
    internal bool PosAcquired => _pos is not null;
    internal int BudgetMs { get; private set; }
    internal char? GetLocked(int j) => _locked[j];
    internal PointF GetPos(int j) => _pos![j];
    internal int GetVote(int j, int cardinal) => _votes[j, cardinal];

    private static bool SigStable(List<bool[]> hist) => hist.Count >= 3
        && RuneArrowDetector.SigSimilar(hist[^1], hist[^2]) && RuneArrowDetector.SigSimilar(hist[^2], hist[^3]);

    /// <summary>간격 균일비 — X 정렬 후 이웃 간격 최대/최소. 진짜 화살표 줄이 잡줄보다 균일하다는
    /// 실측(2026-08-05: 진짜 1.11~1.49, 잡 1.63~1.79)이 웜 교차 검증의 판별 근거.</summary>
    private static double GapRatio(IEnumerable<float> xs)
    {
        var s = xs.OrderBy(x => x).ToArray();
        double mx = 0, mn = double.MaxValue;
        for (int i = 1; i < s.Length; i++) { double g = s[i] - s[i - 1]; mx = Math.Max(mx, g); mn = Math.Min(mn, g); }
        return mn <= 0 ? double.MaxValue : mx / mn;
    }

    /// <summary>오프라인 재현기 전용 — 위치 획득/pos= 인자 결과를 관측 위치로 시드한다.</summary>
    internal void AdoptPositions(PointF[] pos)
    {
        _pos = (PointF[])pos.Clone();
        _posAnchor = (PointF[])pos.Clone();
        _posSource = "시드(오프라인/pos=)"; _posAt = 0;
    }

    /// <summary>일반 모드 한 프레임 전체 판정 — 무거운 줄 인식→heavy 시그니처 런→줄 채택/재선출
    /// →부분 줄 보완→로컬 추적→슬롯 재배치→회전/예산 판정. recent = 직전 프레임 창(오래된 순,
    /// 최대 3장) — 채도 교집합·정지 게이트 기준. 프레임 소유권은 호출자에 남는다.
    /// clock: 경과 ms 판독자 — 무거운 분석(~25-65ms)이 끝난 <b>뒤</b> 읽어야 원본 타이밍과 같다
    /// (실전은 Stopwatch, 오프라인은 고정값 반환 → 결정성 유지).</summary>
    internal void StepFrame(Bitmap frame, IReadOnlyList<Bitmap> recent, Func<long> clock)
    {
        // 융합 풀 — 위치 미확보 프레임에서만 수집(확보 후엔 융합 폴백이 안 도니 비용 낭비).
        // 프레임 단위 생성·폐기: 프레임 간 누적은 카메라 이동 시 어긋난 후보를 섞는다.
        var pool = _pos is null ? new RuneArrowDetector.FusionPool() : null;
        var row = RuneArrowDetector.AnalyzeFrame(frame, recent, _beforeRef, _precropped, pool);
        long tMs = clock(); // 분석 소요 시간 반영 — 각속도 정규화·시간 게이트의 기준 시각

        // ④ 줄 최초 채택 전 웜 줄 교차 검증 — 한색 광류 부류 맵에서 no-warm 마스크의 진짜 줄이
        // 파편화되면 잡 4조합이 게이트를 전부 통과해 ④가 잡줄을 "성공" 채택한다(2026-08-05
        // 10:32 실전: ⓑ 잡줄 면적 1039 채택·초반 오잠금으로 재선출 봉쇄 → 정답을 아는 웜 줄
        // (면적 1737)이 폴백이라 돌 기회가 없어 2/4 실패). 웜 줄이 존재하고 ④ 줄과 2슬롯 이상
        // 위치가 다르면 아래 균일성 비교로 어느 쪽이 진짜인지 판별해 교체한다(위치 전용 —
        // 방향은 로컬 관찰). 정상 맵은 웜 줄과 ④ 줄이 같은 물체라 불일치가 없어 발동 안 함.
        if (row is not null && _pos is null
            && RuneArrowDetector.TryWarmRow(frame, _beforeRef, _precropped, pool, out double warmArea, out var warmDirs)
               is { Length: 4 } wcheck)
        {
            for (int j = 0; j < 4; j++) // 합의용 웜 판독 저장 — 위치 대응은 소비 시점에 검사
            { _warmDir[j] = warmDirs![j].Dir; _warmMargin[j] = warmDirs[j].Margin; _warmDirAt[j] = tMs; _warmX[j] = wcheck[j].X; }
            int mismatch = Enumerable.Range(0, 4).Count(j => Math.Abs(wcheck[j].X - row[j].Center.X) > MinSlotSepPx);
            double rowArea = row.Sum(r => (double)r.Area);
            double rowGapRatio = GapRatio(row.Select(r => r.Center.X));
            double warmGapRatio = GapRatio(wcheck.Select(p => p.X));
            // 전체 교체 판별자 = 간격 균일성 비교(2026-08-05 12:16 재설계, 사용자 검수).
            // 기각된 판별자(실측): 면적 우세(구 1.4×) — 12:16 진짜 웜이 1.16×라 미달(3/4 실패),
            // pillhigh는 잡 웜이 1.65×로 통과해 진짜 줄을 뺏을 위험(방향이 반대로도 나옴).
            // 중심 근접 — pillhigh 잡 웜 이탈 25px < 진짜 ④ 46px(역전). 균일성 비교는 실측 전
            // 케이스 정답: 12:16 웜 1.25 vs ④ 1.66, 10:32 웜 1.11 vs ④ 1.79 → 교체 / pillhigh
            // 웜 1.63 vs ④ 1.49 → 차단. 마진 0.15는 pillhigh 역차 0.14 배제의 최소값. 면적 0.8×
            // 하한은 파편 웜 줄의 탈취 방지(UUUR 웜 0.75×). 웜이 틀려도 위치가 틀리면 잠금
            // 실패 → 안전 종료(fail-closed)라 오답 위험은 없다.
            if (mismatch >= 2 && warmArea >= rowArea * 0.8 && warmGapRatio <= rowGapRatio - 0.15)
            {
                _pos = wcheck; _posAnchor = (PointF[])wcheck.Clone(); _adoptedRowArea = warmArea;
                _posSource = "④→⑦w 교체(웜 교차 검증)"; _posAt = tMs;
                _note?.Invoke($"위치 교체(④→⑦w 웜 교차 검증) — {mismatch}슬롯 불일치, 간격 균일비 웜 {warmGapRatio:0.00} vs ④ {rowGapRatio:0.00}, 면적 {warmArea:0}/{rowArea:0}, 웜 채택: "
                              + string.Join(" ", wcheck.Select(p => $"({p.X:0},{p.Y:0})")));
            }
            // 슬롯 단위 보정 — 3슬롯 일치 + 1슬롯 불일치면 일치한 3개가 두 줄의 정렬을 상호
            // 검증하므로, 불일치 슬롯만 웜 위치로 교체한다(면적 조건 불필요 — 2026-08-05 10:47
            // 실전: ④ 교집합이 진짜 1번(458← a368) 대신 잡블롭(533 a266)을 끼웠는데 불일치가
            // 1슬롯·면적비 1.13이라 전체 교체 게이트 미발동 → 그 슬롯만 굶주려 3/4 실패.
            // 웜이 틀린 최악의 경우에도 해당 슬롯이 잠기지 못해 3/4 안전 실패 — 더 나빠질 수 없다).
            else if (mismatch == 1)
            {
                int bad = Enumerable.Range(0, 4).First(j => Math.Abs(wcheck[j].X - row[j].Center.X) > MinSlotSepPx);
                var fixedPos = row.Select(r => r.Center).ToArray();
                fixedPos[bad] = wcheck[bad];
                _pos = fixedPos; _posAnchor = (PointF[])fixedPos.Clone();
                _adoptedRowArea = rowArea; // ④ 줄 기준 유지 — 더 강한 진짜 줄의 재선출 여지는 남긴다
                _posSource = $"④+⑦w 보정(슬롯{bad + 1} 교체)"; _posAt = tMs;
                _note?.Invoke($"위치 보정(④+⑦w 웜 슬롯 보정) — ④ 줄의 슬롯{bad + 1}(x{row[bad].Center.X:0})을 웜 위치({wcheck[bad].X:0},{wcheck[bad].Y:0})로 교체(나머지 3슬롯 일치)");
            }
        }

        if (row is not null)
        {
            _rowSeen = true;
            for (int j = 0; j < 4; j++)
            {
                // 슬롯 위치 대응 게이트 — ④ 줄의 j번 블롭이 현재 관측점과 크게 어긋나면 다른
                // 물체를 읽은 것이라 heavy 판독 갱신·잠금을 보류한다(웜 교차 채택 시 잡줄이
                // 슬롯 인덱스로 방향을 오염시키는 것 방지 — 2026-08-05 10:32). 정상 흐름은
                // 줄에서 채택한 위치 + EMA 클램프 ±22px라 이 게이트(±32px)에 안 걸린다.
                if (_pos is not null && Math.Abs(row[j].Center.X - _pos[j].X) > PosBox / 2.0) continue;
                _centers[j] = row[j].Center;
                _heavyDir[j] = row[j].Dir; _heavyDirAt[j] = tMs; _heavyX[j] = row[j].Center.X; // 로컬 잠금 충돌 보류 기준
                _heavyMargin[j] = row[j].Margin;
                if (_locked[j] is not null) continue;
                // 정지 확정: '런 시작' 모양·방향이 계속 같아야 함
                if (_runSig[j] is not null && _runDir[j] == row[j].Dir
                    && RuneArrowDetector.SigSimilar(_runSig[j], row[j].Sig))
                {
                    _runLen[j]++;
                    if (_runLen[j] >= LockRun && tMs - _runStart[j] >= LockSpanMs) { _locked[j] = _runDir[j]; _lockedCount++; _lockedAt[j] = tMs; }
                }
                else { _runSig[j] = row[j].Sig; _runDir[j] = row[j].Dir; _runLen[j] = 1; _runStart[j] = tMs; }
            }
        }

        // 위치 — 줄 게이트 통과한 첫 줄 즉시 채택(스페이스+100ms부터 화살표가 떠 있다는
        // 전제, 사용자 지정 2026-08-04) + 더 강한 줄이 보이면 재선출(TryAdoptRow 주석 참조).
        if (row is not null) TryAdoptRow(row, clock);
        // 부분 줄 보완 — 화살표 하나가 배경과 병합 소실되면 4개 줄이 영영 안 잡힌다
        // (14:46 보라맵). 0.9초까지 줄이 없으면 3개+외삽으로 위치만 확보하고,
        // 방향·잠금은 로컬 관찰에 맡긴다.
        if (_pos is null && row is null && clock() > 900)
        {
            var pp = RuneArrowDetector.TryPartialRow(frame, _beforeRef, _precropped);
            if (pp is { Length: 4 })
            {
                _pos = pp; _posAnchor = (PointF[])pp.Clone();
                _posSource = "⑦ 부분 줄(3개+외삽)"; _posAt = clock();
                _note?.Invoke($"위치 확보(⑦ 부분 줄 3개+외삽) — 방향은 로컬 관찰: {string.Join(" ", pp.Select(p => $"({p.X:0},{p.Y:0})"))}");
            }
            // 웜톤 줄(위치만) — 한색 광류 병합으로 ④·⑦이 전멸하는 맵의 구제(2026-08-05 09:20
            // 오답 실전 원인). 방향은 로컬 관찰이 판정. 융합보다 앞: 단일 웜 마스크의 완전한
            // 4블롭 줄이 다소스 짜깁기보다 강한 기하 증거다.
            else if (RuneArrowDetector.TryWarmRow(frame, _beforeRef, _precropped, pool, out _, out var wpDirs) is { Length: 4 } wp)
            {
                _pos = wp; _posAnchor = (PointF[])wp.Clone();
                long wpT = clock();
                for (int j = 0; j < 4; j++) // 합의용 웜 판독 저장
                { _warmDir[j] = wpDirs![j].Dir; _warmMargin[j] = wpDirs[j].Margin; _warmDirAt[j] = wpT; _warmX[j] = wp[j].X; }
                _posSource = "⑦w 웜톤 줄"; _posAt = wpT;
                _note?.Invoke($"위치 확보(⑦w 웜톤 줄·위치만) — 방향은 로컬 관찰: {string.Join(" ", wp.Select(p => $"({p.X:0},{p.Y:0})"))}");
            }
            // 소스 간 후보 융합 — 최후 폴백(④·⑦·웜톤 전부 실패한 프레임만). 단일 마스크가 4개를
            // 못 채워도 여러 마스크가 각자 본 후보를 슬롯 병합해 위치를 복원한다. 부분 줄이
            // 정답이고 융합이 잡줄인 케이스가 실측돼(DLUU 리플레이) 순서는 ⑦ 우선 고정.
            else if (pool is not null
                     && RuneArrowDetector.TryFusedRow(pool, frame, _beforeRef, out var contrib) is { Length: 4 } fp)
            {
                _pos = fp; _posAnchor = (PointF[])fp.Clone(); _fusedContrib = contrib;
                _posSource = "⑧ 소스 융합"; _posAt = clock();
                _note?.Invoke($"위치 확보(⑧ 소스 융합·최후 폴백) — 방향은 로컬 관찰: {string.Join(" ", fp.Select(p => $"({p.X:0},{p.Y:0})"))}");
            }
        }

        // 로컬 추적 — 위치가 고정된 뒤: 회전 화살표의 각도 시계열 + 반동 감지.
        // 로컬 블롭 중심으로 위치를 서서히 보정(회전 핵 어긋남 수렴).
        if (_pos is not null) StepLocal(frame, recent.Count > 0 ? recent[^1] : null, tMs);
        if (_pos is not null) TryRelocate(frame, tMs);

        // 회전 판정(초반 1초 내 각도 진행 감지) → 고속 모드 전환 + 예산 연장
        if (!_fastMode && _rotatingSeen && _pos is not null)
        {
            _fastMode = true; _spinNoted = true;
            BudgetMs = Math.Max(BudgetMs, RotatingBudgetMs);
            _note?.Invoke($"회전 관측 — 각도 진행 감지, {FastTickMs}ms 간격 고속 관찰로 반동 추적(예산 {BudgetMs}ms)");
        }
        else if (!_spinNoted && clock() > 800 && _lockedCount < 4)
        {
            _spinNoted = true;
            // 회전 관측이 없어도(위치 미확보·느린 잠금 등) 확정이 늦으면 예산만 연장한다.
            // 옛 문구 "화살표 회전 감지"는 정지 퍼즐의 지연 확정까지 회전으로 읽히게 해 폐기
            // (2026-08-05 11:30 실전 오독) — 회전의 진실은 _rotatingSeen(트레이스 헤더 회전관측=).
            BudgetMs = Math.Max(BudgetMs, RotatingBudgetMs);
            _note?.Invoke($"확정 지연 — 0.8초 경과 잠금 {_lockedCount}/4, 관찰 예산 {BudgetMs}ms로 연장(회전 관측과 무관)");
        }
    }

    /// <summary>고속 모드의 주기적 줄 재선출 체크 — 무거운 줄 인식+채택만(스케줄링은 호출자).
    /// 정지 퍼즐이 잡줄 관측점의 난수 각도로 '가짜 회전' 판정돼 고속 모드에 갇히면 무거운
    /// 경로가 영영 안 돌아 재선출 기회가 없던 문제(17:52 카르시온 3/4 실패)의 복구 창구.</summary>
    internal void StepHeavyRow(Bitmap frame, IReadOnlyList<Bitmap> recent, Func<long> clock)
    {
        var row = RuneArrowDetector.AnalyzeFrame(frame, recent, _beforeRef, _precropped);
        if (row is null) return;
        long tMs = clock();
        // 고속 모드에서도 heavy 판독은 갱신한다 — 로컬 안정 오독(10:47 실전: 730의 → 글리프를
        // 로컬 고채도 파편이 처음부터 끝까지 ←로 읽음)의 잠금을 막는 '경로 충돌 보류' 가드가
        // 600ms 신선도를 요구하는데, 이 갱신이 없으면 고속 전환 직후 만료돼 무력화된다
        // (10:47: 가드 만료 시점 t≈1301에 L 오잠금). 잠금은 추가하지 않는다(회전 퍼즐 최소 변경).
        for (int j = 0; j < 4; j++)
        {
            if (_pos is not null && Math.Abs(row[j].Center.X - _pos[j].X) > PosBox / 2.0) continue; // 슬롯 위치 대응 게이트
            _heavyDir[j] = row[j].Dir; _heavyDirAt[j] = tMs; _heavyX[j] = row[j].Center.X;
            _heavyMargin[j] = row[j].Margin;
        }
        TryAdoptRow(row, clock);
    }

    // 미확정 화살표들의 로컬 분석 한 회. 회전/정지 라우팅은 <b>글리프 각도 시계열</b>로만 판단 —
    // 박스 안 '움직임 픽셀 수'는 배경 애니메이션(불꽃·이펙트)에 오염돼 정지 화살표를 회전으로
    // 오분류했다(10:39 실행: 고속 모드에서 정지 화살표가 시그니처 락 경로를 영영 못 탐).
    //  · 회전 중(최근 3스텝 단조 ≥15°/100ms, 반동 딸깍 순간을 위해 3표본 히스테리시스) → 반동 감지
    //  · 정지 → 고속 모드에서는 로컬 시그니처 런으로 확정을 잇는다(무거운 경로가 멈추므로)
    internal SlotReading[] StepLocal(Bitmap frame, Bitmap? prev, long tMs)
    {
        var readings = new SlotReading[4];
        for (int j = 0; j < 4; j++)
        {
            if (_locked[j] is { } d0) { readings[j] = new SlotReading(true, d0, false, default, 0, 0, 0, false, false); continue; }
            var rect = new Rectangle((int)(_pos![j].X - PosBox / 2.0), (int)(_pos[j].Y - PosBox / 2.0), PosBox, PosBox);
            var la = RuneArrowDetector.AnalyzeArrowAt(frame, _beforeRef, prev, rect);
            if (la is not { } a) { readings[j] = new SlotReading(false, default, false, default, 0, 0, 0, false, false); continue; }
            if (a.Area >= 60)
            {
                // EMA 자기보정은 앵커(줄 인식 위치) ±22px로 클램프 — 자기 글리프가 픽에서
                // 빠지는 프레임이 이어지면 박스가 이웃 화살표로 미끄러져 남의 방향을 잠근다
                // (2026-08-04 위아래위위 룬: ↓화살표 관찰점이 옆 ↑화살표로 흘러 U 오답 락).
                float cx = (float)(_pos[j].X * 0.7 + a.Center.X * 0.3);
                float cy = (float)(_pos[j].Y * 0.7 + a.Center.Y * 0.3);
                _pos[j] = new PointF(
                    Math.Clamp(cx, _posAnchor![j].X - 22, _posAnchor[j].X + 22),
                    Math.Clamp(cy, _posAnchor[j].Y - 22, _posAnchor[j].Y + 22));
            }

            int before = _lockedCount;
            _angleT[j].Add(tMs); _angleV[j].Add(RuneAngleTracker.FixAngleFlip(_angleV[j], a.AngleDeg));
            // 플립 고착 리셋 — 교정이 교대 플립 난수에 빠지면 시계열을 비워 자가 복원.
            // 원시 각도는 매끈해서 새로 쌓으면 즉시 정상 회전으로 돌아온다(20:57 3번 ←).
            if (RuneAngleTracker.DerailedAngles(_angleT[j], _angleV[j]))
            {
                _angleT[j].Clear(); _angleV[j].Clear();
                _diag?.Invoke($"[화살표{j + 1} 플립 고착 → 시계열 리셋]"); // 실전 diag=null — 오프라인 출력 형식 그대로
            }
            _sigHist[j].Add(a.Sig); if (_sigHist[j].Count > 4) _sigHist[j].RemoveAt(0);
            int n = _angleV[j].Count;
            if (RuneAngleTracker.IsRotating(_angleT[j], _angleV[j])) _lastRotAt[j] = n;
            bool rotActive = n - _lastRotAt[j] <= 3 && !SigStable(_sigHist[j]); // 회전 중(반동 딸깍 포함) — 모양까지 변할 때만
            if (rotActive)
            {
                RuneAngleTracker.TryDetectRecoil(j, _angleT[j], _angleV[j], _votes, ref _lockedCount, _locked, _diag);
                _rotatingSeen = true;
            }
            else
            {
                // 합의용 로컬 판독 저장 — 비회전 판독만(회전 중 방향 분류는 무의미라 투표 금지)
                _localDir[j] = a.Dir; _localMargin[j] = a.Margin; _localDirAt[j] = tMs;
                // 정지 확정 — 로컬 시그니처 런. 무거운 줄 경로와 상시 병행(독립 런 상태) —
                // 줄 인식이 흔들리는 맵에서 정지 화살표가 굶는 것 방지(10:39 4정지 실패).
                if (_lRunSig[j] is not null && _lRunDir[j] == a.Dir && RuneArrowDetector.SigSimilar(_lRunSig[j], a.Sig))
                {
                    _lRunLen[j]++;
                    // '방향-주축 일치' 게이트는 시도 후 기각(2026-08-05) — 잡블롭의 안정된 오독
                    // (10:32 주축 318°에 D 잠금)을 막으려 했으나, 침식된 진짜 ↑/↓ 글리프는 가로
                    // 파편만 남아 주축이 2°/359°로 재면서도 방향 분류는 정확했다(같은 날 스트립
                    // 실측 — 게이트가 정답 잠금 2개를 막아 2/4 실패). 잡음 잠금은 웜 교차 검증·
                    // 슬롯 위치 대응 게이트(StepFrame)가 위치 단계에서 차단한다.
                    if (_lRunLen[j] >= LockRun && tMs - _lRunStart[j] >= LockSpanMs && RuneAngleTracker.AxisStable(_angleV[j])
                        // 경로 충돌 보류 — 무거운 줄 경로의 신선한(≤600ms)·같은 글리프(±24px)
                        // 판독과 방향이 다르면 이 프레임엔 잠그지 않는다. 일치하거나 무거운
                        // 경로가 먼저 잠그면 확정. 위치가 다르면 다른 걸 본 것이라 비교 무의미.
                        && !(tMs - _heavyDirAt[j] <= 600 && Math.Abs(_heavyX[j] - _pos[j].X) <= 24 && _heavyDir[j] != _lRunDir[j]))
                    { _locked[j] = _lRunDir[j]; _lockedCount++; }
                }
                else { _lRunSig[j] = a.Sig; _lRunDir[j] = a.Dir; _lRunLen[j] = 1; _lRunStart[j] = tMs; }
            }
            if (_lockedCount > before) _lockedAt[j] = tMs; // 반동표·로컬 시그니처 잠금 공통 — 트레이스용
            readings[j] = new SlotReading(false, default, true, a.Dir, a.AngleDeg, a.Area, a.MovingPx,
                rotActive, _lockedCount > before, a.Margin);
        }
        return readings;
    }

    /// <summary>3잠금 + 1잡 슬롯 재배치(사용자 검수 2026-08-04) — 22:34 실전: 1번 화살표가 폭포 빛줄기
    /// 위에서 30px 조각으로 침식돼 크기 게이트(≤5배)에 조합이 죽고, 우측 잡블롭이 낀 '한 칸
    /// 밀린 줄'(간격 균일이라 에지 보정 미발동)이 채택 → 관측점 3개는 진짜 화살표(정상 잠금),
    /// 1개는 잡영역(난수 미확정) → 3/4 실패. 잠긴 3개의 간격이 균일하면 남은 화살표는 그 줄의
    /// 왼쪽 또는 오른쪽 한 칸 외삽 위치에 있다 — 양쪽을 로컬 글리프로 탐침해 있는 쪽으로 관측점
    /// 을 옮기고 상태를 리셋해 재관찰한다. 실패해도 인식 실패 종료라 오답 위험은 없다.</summary>
    internal void TryRelocate(Bitmap frame, long tMs)
    {
        if (_relocated || _lockedCount != 3 || _pos is null || tMs < 2200) return;
        int starved = -1;
        for (int j = 0; j < 4; j++) if (_locked[j] is null) starved = j;
        // 반동 표가 쌓였거나 최근까지 회전 중이면 진짜 회전 화살표 관찰 중 — 옮기면 안 된다
        for (int c = 0; c < 4; c++) if (_votes[starved, c] > 0) return;
        if (_angleV[starved].Count > 0 && _angleV[starved].Count - _lastRotAt[starved] <= 3) return;
        var lx = Enumerable.Range(0, 4).Where(j => j != starved).Select(j => _pos[j].X).OrderBy(x => x).ToArray();
        double g1 = lx[1] - lx[0], g2 = lx[2] - lx[1];
        if (Math.Max(g1, g2) > Math.Min(g1, g2) * 1.35) return; // 잠긴 3개가 등간격일 때만
        float m = (float)((g1 + g2) / 2);
        if (m < 40 || m > 170) return; // 간격 상식 범위(실측 49~136px)
        float py = Enumerable.Range(0, 4).Where(j => j != starved).Average(j => _pos[j].Y);
        (PointF P, int Area) Probe(float px)
        {
            if (px < PosBox / 2f || px > frame.Width - PosBox / 2f) return (default, 0);
            var r = new Rectangle((int)(px - PosBox / 2.0), (int)(py - PosBox / 2.0), PosBox, PosBox);
            var la = RuneArrowDetector.AnalyzeArrowAt(frame, _beforeRef, null, r);
            return (new PointF(px, py), la?.Area ?? 0);
        }
        var left = Probe(lx[0] - m); var right = Probe(lx[2] + m);
        var pick = left.Area >= right.Area ? left : right;
        if (pick.Area < 40) return; // 침식 조각(실측 a30)도 로컬 추출로는 이보다 크게 잡힌다
        _note?.Invoke($"위치 재배치(3잠금 외삽) — 슬롯{starved + 1} 관측점({_pos[starved].X:F0},{_pos[starved].Y:F0})을 잠긴 줄 외삽 위치({pick.P.X:F0},{pick.P.Y:F0})로 이동해 재관찰");
        _posSource = $"{_posSource ?? "?"} → 재배치(슬롯{starved + 1})"; _posAt = tMs;
        _pos[starved] = pick.P; _posAnchor![starved] = pick.P;
        ResetSlot(starved, alsoVotes: false); // 투표는 위 가드로 항상 0 — 리셋 불요(원 코드와 동일)
        _heavyDirAt[starved] = -9999;
        _relocated = true;
        // 재관찰 시간 확보 — 정지 잠금은 3표본+250ms면 된다. 사용자 지정 4초에 구제 여유만
        // 최소로 얹는다(최대 5초).
        BudgetMs = Math.Min(5000, Math.Max(BudgetMs, (int)tMs + 1000));
    }

    // 줄 채택 + 재선출 — 게이트 통과 줄을 즉시 채택하되, 초반 2.5초 안에 '더 강한 줄'
    // (면적 합 1.4배↑, 위치 28px↑ 상이)이 보이면 관측 위치를 교체한다. 이 룬 UI는 글리프
    // 간격이 균일하지 않아(17:52 실측 97/136/49) 기하 게이트만으로 잡줄을 다 못 막는다 —
    // 몹 파편 줄이 먼저 잡혀 관측점이 몹 몸통에 앉는 사고(17:36·17:52 카르시온)를 복구.
    // 교체된 화살표는 각도·시그니처·투표·잠금을 전부 리셋(다른 지점의 기록은 무효).
    private void TryAdoptRow(List<ArrowSample> row, Func<long> clock)
    {
        double area = row.Sum(x => (double)x.Area);
        if (_pos is null)
        {
            _pos = row.Select(x => x.Center).ToArray(); _posAnchor = (PointF[])_pos.Clone();
            _adoptedRowArea = area;
            // 최초 채택도 노트를 남긴다(2026-08-05 로그 개편) — 이전엔 무음이라 시도마다
            // 위치를 어느 경로가 잡았는지 앱로그만으로는 알 수 없었다(폴백만 노트가 있었음).
            _posSource = "④ 줄 채택"; _posAt = clock();
            _note?.Invoke($"위치 확보(④ 줄 채택, 면적 {area:0}): {string.Join(" ", row.Select(p => $"({p.Center.X:0},{p.Center.Y:0})"))}");
            return;
        }
        // 잠금 보호 — 이미 잠긴 슬롯이 생겼으면 관측 위치를 옮기지 않는다(사용자 검수 2026-08-04).
        // 20:38 실전: 0.72초에 진짜 줄(476/594/682/752)을 잡았는데 0.18초 뒤 이펙트 병합
        // 비대 블롭(a1624)+이펙트 조각(a579) 잡줄이 면적 1.8배로 재선출을 통과해 줄을 뺏었고,
        // 2·3번 관측점이 같은 화살표로 미끄러져 오답(↓→→↓)을 입력했다. 잠금은 '그 자리가
        // 진짜 글리프'라는 가장 강한 증거다 — 잡 관측점은 난수 각도라 축 안정을 통과하지 못한다.
        if (_lockedCount > 0 || clock() >= 2500 || area < _adoptedRowArea * 1.4) return;
        bool Moved(int j) => Math.Abs(row[j].Center.X - _posAnchor![j].X) > 28
                          || Math.Abs(row[j].Center.Y - _posAnchor[j].Y) > 28;
        if (!Enumerable.Range(0, 4).Any(Moved)) { _adoptedRowArea = Math.Max(_adoptedRowArea, area); return; }
        for (int j = 0; j < 4; j++)
        {
            bool moved = Moved(j);
            _pos![j] = row[j].Center; _posAnchor![j] = row[j].Center;
            if (!moved) continue;
            if (_locked[j] is not null) { _locked[j] = null; _lockedCount--; _lockedAt[j] = 0; }
            ResetSlot(j, alsoVotes: true);
        }
        _adoptedRowArea = area;
        _posSource = $"{_posSource ?? "?"} → 재선출"; _posAt = clock();
        _note?.Invoke($"위치 재선출 — 더 강한 줄(면적 {area:0} ≥1.4×)로 관측점 교체: {string.Join(" ", row.Select(p => $"({p.Center.X:0},{p.Center.Y:0})"))}");
    }

    /// <summary>슬롯 관측 상태 리셋 — 재선출/재배치로 관측 지점이 바뀌면 다른 지점의 기록은 무효.
    /// alsoVotes: 재선출은 투표까지 지움, 재배치는 투표 0 가드를 이미 통과해 불요(동작 보존).</summary>
    private void ResetSlot(int j, bool alsoVotes)
    {
        _angleT[j].Clear(); _angleV[j].Clear(); _sigHist[j].Clear();
        _lRunSig[j] = null!; _lRunLen[j] = 0;
        _runSig[j] = null!; _runLen[j] = 0;
        if (alsoVotes) for (int c = 0; c < 4; c++) _votes[j, c] = 0;
        _lastRotAt[j] = -999;
        _localDirAt[j] = -9999; _warmDirAt[j] = -9999; // 다른 지점의 합의 판독도 무효(heavy는 호출자 소관)
    }

    /// <summary>종료 판정 — 실패 사유/사용자 노트 문구까지 확정해 돌려준다(저장·방송은 호출자).
    /// 4개 전부 확정될 때만 입력한다 — 회전 중 표본의 다수결은 추측이라 오답이 된다
    /// (00:41 실행: 멈춤 3/4 + 다수결 1 → ↑ ↑ ↑ ← 오답 입력). 미달이면 입력 없이 안전 종료
    /// (재발동 없음 — 사용자 지정 2026-08-04; 옛 문구 "재발동 대기"는 폐지된 정책의 잔재라 제거).</summary>
    internal List<RuneArrow>? Confirm(out string traceWhy, out string noteMsg)
    {
        if (!_rowSeen && _pos is null)
        {
            traceWhy = "줄 미인식"; noteMsg = "퍼즐 인식 실패 — 화살표 줄·위치 미확보(④·⑦·⑦w·⑧ 전부 실패)";
            return null;
        }
        if (_lockedCount < 4)
        {
            // 옛 사유 "반동 미관측"은 정지형 시그니처 실패·경로 충돌 보류 등 모든 미잠금을
            // 회전 반동 문제로 읽히게 했다 — 사실 그대로 '잠금 미달'로 기록(2026-08-05 개편).
            string open = string.Join("·", Enumerable.Range(0, 4).Where(j => _locked[j] is null).Select(j => $"슬롯{j + 1}"));
            traceWhy = $"잠금 미달 {_lockedCount}/4";
            noteMsg = $"퍼즐 확정 실패 — 잠금 {_lockedCount}/4({open} 미잠금), 오답 방지 안전 종료(재발동 없음)";
            return null;
        }

        // 중복 관측 안전장치(사용자 검수 2026-08-04) — 두 슬롯이 같은 화살표를 읽으면 4/4여도
        // 오답이다(20:38 실전: 잡줄 재선출로 2·3번 관측점이 15px 간격 → 같은 →를 두 번 입력,
        // 4번째 화살표는 미관측). 오답 입력은 룬 소실+쿨다운이라 인식 실패 종료가 항상 낫다.
        if (_pos is not null)
            for (int i = 0; i < 4; i++)
                for (int j = i + 1; j < 4; j++)
                    if (Math.Abs(_pos[i].X - _pos[j].X) < MinSlotSepPx && Math.Abs(_pos[i].Y - _pos[j].Y) < MinSlotSepPx)
                    {
                        traceWhy = $"중복 관측 슬롯{i + 1}·{j + 1}";
                        noteMsg = $"퍼즐 확정 무효 — 슬롯 {i + 1}·{j + 1} 관측점 겹침({_pos[i].X:F0},{_pos[i].Y:F0} vs {_pos[j].X:F0},{_pos[j].Y:F0}), 오답 입력 방지 종료";
                        return null;
                    }

        // 입력 순서는 관측점 X좌표 순 — 슬롯 재배치로 인덱스 순서와 좌우 순서가 어긋날 수 있다
        // (평상시엔 줄 채택이 좌→우 정렬이라 정렬해도 동일).
        var order = Enumerable.Range(0, 4).ToList();
        if (_pos is not null) order.Sort((i1, i2) => _pos[i1].X.CompareTo(_pos[i2].X));
        var result = new List<RuneArrow>(4);
        foreach (int j in order) result.Add(new RuneArrow(_centers[j], _locked[j]!.Value));
        traceWhy = "확정 4/4"; // 성공 판정도 트레이스를 남긴다 — 오확정 사후 분석용(사용자 지시 2026-08-04)
        // 회전 꼬리표는 실제 관측(_rotatingSeen)만 본다 — 옛 _spinNoted는 '0.8초 지연' 예산연장
        // 플래그라 정지 퍼즐에도 "(회전 포함)"이 붙었다(2026-08-05 11:30 실전 오독으로 교체).
        noteMsg = $"퍼즐 확정 — 4/4{(_rotatingSeen ? " (회전 관측 포함)" : " (전원 정지)")}";
        return result;
    }

    /// <summary>판정 트레이스(logs\rune-solve.txt, 저장은 호출자) — 헤더·위치 확보 경로·슬롯별
    /// 잠금 시각/반동표/각도 시계열·범례. 스트립 이미지 없이도 '잠금 미달'의 원인 — 투표 분산·
    /// 각도 노이즈·표본 공백 — 을 즉시 판독. 아무 도구도 이 파일을 파싱하지 않는다(사람 전용) —
    /// 2026-08-05 개편: 슬롯 1기반 통일(앱로그와 동일), 위치 경로 라인·범례 추가.</summary>
    internal string BuildTrace(string why, long tMs)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"{DateTime.Now:HH:mm:ss.fff} 퍼즐 판정({why}) t={tMs}ms lock={_lockedCount}/4 회전관측={_rotatingSeen} 고속={_fastMode}");
        sb.AppendLine(_pos is null
            ? "위치: 미확보 — ④ 줄·⑦ 부분·⑦w 웜톤·⑧ 융합 전부 실패"
            : $"위치: {_posSource ?? "?"} @{_posAt}ms → {string.Join(" ", _pos.Select(p => $"({p.X:F0},{p.Y:F0})"))}");
        for (int j = 0; j < 4; j++)
        {
            sb.Append($"[슬롯{j + 1}] lock={(_locked[j] is { } d ? $"{d}@{_lockedAt[j]}ms" : "미잠금")} 반동표 R:{_votes[j, 0]} U:{_votes[j, 1]} L:{_votes[j, 2]} D:{_votes[j, 3]}");
            if (_pos is not null) sb.Append($" pos=({_pos[j].X:F0},{_pos[j].Y:F0})");
            sb.AppendLine();
            var t = _angleT[j]; var v = _angleV[j];
            sb.Append("    ");
            for (int k = Math.Max(0, v.Count - 60); k < v.Count; k++) sb.Append($"{t[k]:F0}:{v[k]:F0}° ");
            sb.AppendLine();
        }
        if (_fusedContrib is not null) sb.AppendLine($"융합: {_fusedContrib}");
        sb.AppendLine("(범례) 슬롯=좌→우 1기반 · 반동표=회전 격발 투표(정지형은 전부 0이 정상) · 잠금 근거: 정지=시그니처 연속, 회전=반동표 다수결");
        sb.AppendLine("(범례) 각도열 t:주축각° = 참고용 — 방향 분류와 별개(언랩·최근 60표본·리셋 시 비움) · 회전 유무의 진실은 헤더 회전관측=");
        return sb.ToString();
    }
}
