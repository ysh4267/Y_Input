using System.Drawing;
using System.Drawing.Imaging;

namespace YInput.Host.Vision;

/// <summary>줄 선택 — 후보 블롭들에서 화살표 4개 줄을 고르는 기하·점수 로직(partial).
/// 밴드 기하, 줄 조합 점수화(PickRow), 에지 슬롯 외삽 교체, 부분 줄 보완.</summary>
internal static partial class RuneArrowDetector
{
                                           // sat 80~120대)까지 지우고 순수 무지개 글리프(sat 150+)만 남긴다.
                                           // 10:14 실행: sat80으로는 수정 홍수가 회전 글리프를 삼켰다
    private const double BannerWideFrac = 0.30; // 안내 배너 판정: 가로 이 비율 이상 변한 행
    private const int BannerMinRows = 6;        // 그런 행이 연속 이만큼 = 배너

    private const int MinArrowArea = 30, MaxArrowArea = 2500; // 하한은 반투명 합성으로 작아진 코어 기준
    private const int MinArrowBox = 8, MaxArrowBox = 70;
    private const int RowBandPx = 22;    // 같은 줄(4개 나열) 판정 Y 허용폭
    private const int MinGapPx = 50, MaxGapPx = 280; // 화살표 이웃 간격 상식 범위(실측 85~125px) —

                                                     // 데미지 숫자·배너 글자 조각(13~35px) 배제
    // 줄 선택 게이트 공용 상수(PickRow·TryPartialRow) — 실측 근거는 사용처 주석 참조.
    private const double GapFracLo = 0.040, GapFracHi = 0.145; // 이웃 간격 창폭 비례 상식 범위(실측 49~136px = 0.046~0.126W)
    private const double GapRatioHardMax = 3.5;                // 간격 균일비 순수 상식 상한(실측 최대 2.78 — 카르시온 97/136/49)
    private const double GapRatioSoftW = 0.35;                 // 소프트 균일비 페널티 가중(면적 합 대비) —
                                                               // DDRD 잡줄(비 1.94, 면적 우세)은 지고
                                                               // 카르시온 진짜 줄(비 2.78, 면적 4배)은 이기는 창(0.3~0.4)
    private const double RowFracLo = 0.03, RowFracHi = 0.90;   // 후보 y 밴드 구간. 하한 0.20→0.03(22:19 실전):
                                                               // 필 y도 룬 월드 좌표를 따라 움직여 화살표가 창높이
                                                               // 29.3%(밴드 8.9%)까지 올라온다 — 0.20 컷이 진짜 줄을
                                                               // 후보에서 제거해 몹 잡줄(y~260) 채택 → 3/4 실패.
                                                               // 0.20의 원목적(14:13 배너 하단 반짝이 잡줄)은 현재
                                                               // 면적 경쟁·박스 벌점이 점수로 막는다(DDRD 재검증).
                                                               // 상한 0.85→0.90(2026-08-05 밴드 상단 확장 동반 보정):
                                                               // 밴드가 0.28~0.42→0.21~0.42로 1.5배 커지며 비율 상한의
                                                               // 절대 y가 딸려 올라가는 것을 상쇄 — 0.85×0.14 ≈ 0.90×0.21
                                                               // +상단차라 구 하단 절대 위치(창높이 ≈40%)가 그대로 보존된다.
    // 1패스(기존 구간) 상단 — 구 밴드(0.28~0.42)의 후보 창 상단(0.28+0.03×0.14 = 창높이 28.4%)을
    // 현행 밴드(0.21~0.42) 비율로 환산한 값. 확장 구간(21~28.4%, 안내 텍스트 23~27% 포함)은 잡
    // 재료가 실측돼(2026-08-05 회귀: LLDR x407·LUUR 웜 x353 진입으로 줄이 한 칸 밀림, LDUU ⑧
    // 융합 0좌표) 항상 참여시키지 않는다 — 기존 구간으로 줄이 안 나올 때만 2패스로 합류(19:13
    // 높은 필 구제). 융합 풀은 기존 구간만 수집(풀 오염 방지, ⑧ 동작 동결).
    private const double RowClassicTopFrac = ((0.28 - 0.21) + 0.03 * (0.42 - 0.28)) / (0.42 - 0.21);
    // 에지 슬롯 간격 외삽 교체(PickRow 후처리) — 20:19 실전: 침식 보라(a263)+병합 비대(a1347)가
    // 한 줄에 공존하면 크기 게이트·면적 경쟁이 진짜 줄에 불리해져 끝 슬롯을 잡블롭(버섯 a783,
    // 간격 161)이 차지했다. 나머지 두 간격이 균일(92/97)할 때만 외삽 위치의 실존 후보로 교체.
    private const double EdgeGapSuspectMin = 1.5; // 에지 간격 ≥ 내부 균일 간격 평균 × 이 값 → 의심(실측 161/94.5=1.70)
    private const double EdgeGapUniformMax = 1.3; // '나머지 두 간격 균일' 상한 — 카르시온2 97/49(비 1.98)는 미발동
    private const int EdgeRepairTolPx = 25;       // 외삽 위치 허용 오차(실측: 후보 711 vs 외삽 722.5 = 11.5px)
    // 줄 중심의 필 중심 정렬 허용(창폭 비례) — PickRow·부분 줄(⑦·융합) 공용. 근거 실측은
    // PickRow 사용처 주석(진짜 최대 이탈 0.058W < 0.065W < 잡줄 0.075W~) + 2026-08-05 09:20
    // 오답 실전(부분 경로에 이 게이트가 없어 0.26W 이탈 잡줄 채택 → R D L D 오답 입력).
    private const double RowCenterTolFrac = 0.065;

    /// <summary>부분 줄 보완 — 화살표 하나가 배경(몬스터 발광 등)과 병합되어 후보에서 사라지면
    /// 4개 줄이 영영 안 잡힌다(2026-08-04 14:46 보라맵: ↓화살표가 병합 소실 → 3회 시도 전부
    /// 줄 실패). 게이트를 통과하는 3개 조합을 찾아 빠진 슬롯을 간격 외삽으로 채워 '위치만'
    /// 반환한다 — 방향·잠금은 이후 로컬 관찰(AnalyzeArrowAt 폴백 체인)이 해결한다.
    /// 한쪽 간격이 다른 쪽의 ~2배면 내부 슬롯 누락, 아니면 좌·우 끝 중 로컬 블롭이 있는 쪽.</summary>
    internal static PointF[]? TryPartialRow(Bitmap frame, Bitmap? beforeRef, bool precropped)
    {
        if (!TryRegion(frame, precropped, out var region)) return null;
        int w = region.Width, h = region.Height;
        var mask = VividMask(frame, region, w, h, requireWarm: false);
        if (beforeRef is not null && beforeRef.Width == frame.Width && beforeRef.Height == frame.Height)
        {
            var fresh = new bool[w * h];
            AccumulateDiff(beforeRef, frame, region, w, h, DiffMin, fresh);
            for (int i = 0; i < mask.Length; i++) mask[i] &= fresh[i];
        }
        ThinFilter(mask, w, h);
        var (bandY0, bandY1, bannerCx) = BannerBand(w, h, (int)(h / (RegionY1 - RegionY0)));
        double rowY0 = bandY0 + RowFracLo * (bandY1 - bandY0), rowY1 = bandY0 + RowFracHi * (bandY1 - bandY0);
        var cands = FilterCands(mask, w, h, rowY0, rowY1);
        // 2패스(RowClassicTopFrac 주석 참조) — 기존 구간 우선, 실패 시에만 확장 구간 합류
        var classic = cands.Where(b => b.Cy >= bandY0 + RowClassicTopFrac * (bandY1 - bandY0)).ToList();
        var pr = PartialRowFromCands(classic, frame, beforeRef, region, w, frame.Width, bannerCx, verifyExtrap: false, out _);
        if (pr is null && classic.Count != cands.Count)
            pr = PartialRowFromCands(cands, frame, beforeRef, region, w, frame.Width, bannerCx, verifyExtrap: false, out _);
        return pr;
    }

    /// <summary>부분 줄 코어 — 후보 리스트에서 3개 조합 + 빠진 슬롯 외삽으로 위치 4개 복원.
    /// verifyExtrap: true면 내부 중점 삽입을 포함한 <b>모든</b> 외삽 슬롯이 AnalyzeArrowAt 탐침으로
    /// 글리프 실존을 확인해야 채택(소스 융합 폴백용 — 융합 풀은 단일 마스크보다 잡음이 많아
    /// 무검증 외삽은 잡음 관측점을 만든다). false = 현행 TryPartialRow 동작(끝 슬롯만 탐침).
    /// chosen = 채택된 실블롭 3개(융합 경로의 교차 확인 게이트용 — 반환이 null이 아닐 때만 유효).
    /// bannerCx = 필 중심(영역 상대, 음수면 게이트 생략) — 재구성 4슬롯 평균이 ±RowCenterTolFrac 안이어야 채택.</summary>
    private static PointF[]? PartialRowFromCands(List<Blob> candsIn, Bitmap frame, Bitmap? beforeRef,
        Rectangle region, int w, int frameW, double bannerCx, bool verifyExtrap, out List<Blob>? chosen)
    {
        chosen = null;
        var cands = candsIn.OrderBy(b => b.Cx).ToList();
        double gLo = frameW * GapFracLo, gHi = frameW * GapFracHi; // PickRow와 동일 상수
        List<Blob>? best = null; double bestScore = double.MinValue;
        for (int a = 0; a < cands.Count - 2; a++)
            for (int b2 = a + 1; b2 < cands.Count - 1; b2++)
                for (int c = b2 + 1; c < cands.Count; c++)
                {
                    var t3 = new[] { cands[a], cands[b2], cands[c] };
                    if (t3.Max(x => x.Cy) - t3.Min(x => x.Cy) > RowBandPx) continue;
                    double g1 = t3[1].Cx - t3[0].Cx, g2 = t3[2].Cx - t3[1].Cx;
                    // 각 간격은 1슬롯(범위 내) 또는 2슬롯(내부 누락, 범위의 2배) 허용
                    bool ok1 = g1 >= gLo && g1 <= gHi, ok2 = g2 >= gLo && g2 <= gHi;
                    bool dbl1 = g1 >= gLo * 2 && g1 <= gHi * 2, dbl2 = g2 >= gLo * 2 && g2 <= gHi * 2;
                    if (!((ok1 && ok2) || (ok1 && dbl2) || (dbl1 && ok2))) continue;
                    int aMin = t3.Min(x => x.Area), aMax = t3.Max(x => x.Area);
                    if (aMax > aMin * 5) continue;
                    double score = t3.Sum(x => (double)x.Area);
                    if (score > bestScore) { bestScore = score; best = t3.ToList(); }
                }
        if (best is null) return null;
        chosen = best;
        double bg1 = best[1].Cx - best[0].Cx, bg2 = best[2].Cx - best[1].Cx;
        double yAvg = best.Average(x => x.Cy);
        bool ProbeAt(double x) => AnalyzeArrowAt(frame, beforeRef, null,
            new Rectangle(region.X + (int)x - 32, region.Y + (int)yAvg - 32, 64, 64)) is not null;
        var xs = new List<double> { best[0].Cx, best[1].Cx, best[2].Cx };
        if (bg1 > gHi)                                              // 내부 누락(왼쪽 간격이 2슬롯)
        {
            double mx = best[0].Cx + bg1 / 2;
            if (verifyExtrap && !ProbeAt(mx)) return null;
            xs.Insert(1, mx);
        }
        else if (bg2 > gHi)                                         // 내부 누락(오른쪽 간격이 2슬롯)
        {
            double mx = best[1].Cx + bg2 / 2;
            if (verifyExtrap && !ProbeAt(mx)) return null;
            xs.Insert(2, mx);
        }
        else
        {
            // 끝 슬롯 누락 — 좌/우 외삽 중 로컬 글리프가 실제로 잡히는 쪽을 채택
            double gapAvg = (bg1 + bg2) / 2;
            double leftX = best[0].Cx - gapAvg, rightX = best[2].Cx + gapAvg;
            bool leftOk = leftX - 20 >= 0, rightOk = rightX + 20 < w;
            bool leftHit = leftOk && ProbeAt(leftX);
            bool rightHit = rightOk && ProbeAt(rightX);
            if (verifyExtrap)
            {
                // 융합 경로는 탐침 실증 없는 쪽을 '소거법'으로 채택하지 않는다
                if (leftHit) xs.Insert(0, leftX);
                else if (rightHit) xs.Add(rightX);
                else return null;
            }
            else if (leftHit || (leftOk && !rightHit)) xs.Insert(0, leftX);
            else if (rightOk) xs.Add(rightX);
            else return null;
        }
        // 중심 정렬 게이트 — 부분 줄도 재구성 4슬롯 평균이 필 중심에 정렬되어야 한다(PickRow와
        // 동일 기준). 2026-08-05 09:20 오답 실전: 이 게이트가 없어 융합 부분 경로가 필 중심에서
        // 0.26W 이탈한 우측 잡신호 군집(잎사귀·UI 보드·림라이트)을 채택 → 정지 잡음이 잠금
        // 게이트를 전부 통과 → R D L D 오답 입력. 같은 프레임 리플레이의 ⑦ 잡줄(0.16W 이탈)도
        // 차단. 진짜 줄 최대 이탈 실측 0.058W(22:08 필 이탈 맵)·DLUU 부분 줄 0.051W는 통과.
        // 트리오 평균이 아니라 외삽 포함 4슬롯 평균인 이유: 끝 슬롯 누락 시 트리오 평균은
        // 간격/4(~0.02W)만큼 치우쳐 진짜 줄이 억울하게 죽을 수 있다.
        if (bannerCx >= 0 && Math.Abs(xs.Average() - bannerCx) > frameW * RowCenterTolFrac) return null;
        return xs.Select(x => new PointF((float)(region.X + x), (float)(region.Y + yAvg))).ToArray();
    }

    /// <summary>웜톤 줄 — <b>위치 전용</b> 폴백(⑦ 부분 줄과 같은 계약). 한색(파랑·청록) 광류가
    /// '정적·고채도·발동 전과 다름'의 세 술어를 전부 통과하며 화살표 4개를 한 블롭으로 병합시키는
    /// 맵 대비(2026-08-05 09:20 오답 실측: warm무시 마스크는 병합 블롭 562x201/a40958로 후보 전멸,
    /// warm 게이트만 켜면 같은 프레임에서 4개 블롭 22~25px/a310~406으로 분리돼 전 게이트 통과).
    /// 반환 위치가 단독 근거고, 방향(slotDirs)은 <b>3소스 합의 잠금의 1표 전용</b> — 웜 마스크가
    /// 글리프 일부(파랑 머리끝)를 잘라 방향 분류가 뒤집힐 수 있어(실측: DLUU 735의 ↑이 →로
    /// 오분류 — ④ 정식 단계 편입을 기각한 근거) 단독 결정권을 절대 갖지 않는다. 잠금은 합의
    /// (heavy·로컬·웜 중 2표 동의)가 판정하므로 잘못된 줄/방향은 잠금 실패로 귀결(fail-closed).
    /// pool = 융합 풀 수집 싱크(웜 후보를 DiffWarm 소스로 기여 — 이 줄 자체가 실패해도 ⑧이 쓴다).
    /// areaSum = 채택 줄의 블롭 면적 합(④ 줄과의 교차 검증 비교용 — 실패 시 0).
    /// slotDirs = 슬롯별 (방향, 마진) — 실패 시 null. 마진 정의는 ArrowSample.Margin과 동일.</summary>
    internal static PointF[]? TryWarmRow(Bitmap frame, Bitmap? beforeRef, bool precropped, FusionPool? pool,
        out double areaSum, out (char Dir, double Margin)[]? slotDirs)
    {
        areaSum = 0; slotDirs = null;
        if (!TryRegion(frame, precropped, out var region)) return null;
        if (beforeRef is null || beforeRef.Width != frame.Width || beforeRef.Height != frame.Height) return null;
        int w = region.Width, h = region.Height;
        var mask = VividMask(frame, region, w, h); // requireWarm 기본 true — 한색 광류 절단이 목적
        var fresh = new bool[w * h];
        AccumulateDiff(beforeRef, frame, region, w, h, DiffMin, fresh);
        for (int i = 0; i < mask.Length; i++) mask[i] &= fresh[i];
        var cands = pool is null ? null : new List<Blob>();
        var row = DetectRow(frame, beforeRef, mask, region, w, h, thinFilter: true, FullFrameH(frame, precropped), candsOut: cands);
        if (cands is not null) pool!.Add(FuseSource.DiffWarm, cands);
        if (row is null) return null;
        areaSum = row.Sum(b => (double)b.Area);
        slotDirs = row.Select(b =>
        {
            var (dir, up, _, left, _) = ClassifyScores(b, w, frame, region); // 그라데이션 보정 포함
            return (dir, Math.Abs(Math.Abs(left) - Math.Abs(up)));
        }).ToArray();
        return row.Select(b => new PointF((float)(region.X + b.Cx), (float)(region.Y + b.Cy))).ToArray();
    }

    /// <summary>줄 후보 공용 필터 — 병합 블롭에서 화살표 크기·박스·y밴드 조건을 만족하는 후보만.
    /// (DetectRow·TryPartialRow가 같은 술어를 쓴다 — 게이트 수정 시 한 곳만 고치도록 통합)</summary>
    private static List<Blob> FilterCands(bool[] mask, int w, int h, double rowY0, double rowY1)
        => MergeNear(FindBlobs(mask, w, h)).Where(b =>
            b.Area is >= MinArrowArea and <= MaxArrowArea &&
            b.W is >= MinArrowBox and <= MaxArrowBox && b.H is >= MinArrowBox and <= MaxArrowBox &&
            b.Cy >= rowY0 && b.Cy <= rowY1).ToList();

    /// <summary>마스크에서 '화살표 줄' 블롭 4개 선택(왼쪽부터). fullArea = 밴드·중심 제약 없이
    /// 전 영역 탐색(진단 전용 — 밴드 기하가 틀리는 창 크기를 확인할 때). candsOut = 게이트 통과
    /// 후보의 수집 싱크(소스 융합용 — 줄 실패여도 후보는 남긴다). 실패 시 null.</summary>
    // extendPass=false: ④ 교집합 마스크 전용 — 교집합은 확장 구간(21~28%)에 잡 재료가 많아
    // 2패스가 잡줄을 만든다(DLUU 실측 2026-08-05: 확장 후보 96개로 (388…727) 잡줄 구성 →
    // 진짜 ⑦ 부분줄 455/548/633/735 선점). 높은 필 구제는 차분 기반 ⑦·⑦w의 2패스가 담당
    // (19:13 실측: ⑦w가 확장으로 진짜 웜 줄을 찾아 교차 검증이 ④ 파편 잡줄을 교체).
    private static List<Blob>? DetectRow(Bitmap frame, Bitmap? bannerRef, bool[] mask, Rectangle region, int w, int h, bool thinFilter, int fullFrameH, bool fullArea = false, List<Blob>? candsOut = null, bool extendPass = true)
    {
        if (thinFilter) ThinFilter(mask, w, h);
        var (bandY0, bandY1, bannerCx) = fullArea ? (0, h - 1, -1.0) : BannerBand(w, h, fullFrameH);

        // 화살표 줄의 밴드 세로 위치는 맵·창마다 다르다 — 실측: 카르시온 나무줄기3 29~40%(17:36 실전:
        // 45% 하한이 진짜 화살표 4개를 전부 걸러내 줄 구성 원천 실패), 이전 맵들 36~62%.
        // 하한 20%는 밴드 상단 가장자리 잡블롭 줄(y 0~18% — 배너 하단 반짝이, 14:13 룬)만 차단한다.
        double rowY0 = bandY0 + RowFracLo * (bandY1 - bandY0), rowY1 = bandY0 + RowFracHi * (bandY1 - bandY0);
        var cands = FilterCands(mask, w, h, rowY0, rowY1);
        // 2패스 줄 구성(RowClassicTopFrac 주석 참조) — 1패스는 기존 구간(구 밴드 창)만으로
        // 구 동작을 정확히 보존하고, 실패 시에만 확장 구간(높은 필 화살표)을 합류시킨다.
        // 융합 풀(candsOut)은 항상 기존 구간만 받는다.
        var classic = fullArea ? cands
            : cands.Where(b => b.Cy >= bandY0 + RowClassicTopFrac * (bandY1 - bandY0)).ToList();
        candsOut?.AddRange(classic);
        var row = PickRow(classic, bannerCx, frame.Width);
        if (row is null && extendPass && classic.Count != cands.Count)
        {
            row = PickRow(cands, bannerCx, frame.Width, take: cands.Count);
            if (row is not null) DiagLog?.Invoke("[확장 2패스] 기존 구간 줄 없음 — 확장 구간(상단 21%~) 포함 채택");
        }
        if (DiagLog is not null)
        {
            DiagLog($"밴드 y{region.Y + bandY0}..{region.Y + bandY1} 중심X {(bannerCx >= 0 ? (region.X + bannerCx).ToString("0") : "-")} 후보 {cands.Count}"
                + (row is null ? " → 줄 없음" : " → " + string.Join(" ", row.Select(b =>
                    $"({region.X + b.Cx:0},{region.Y + b.Cy:0})a{b.Area}{ClassifyScores(b, w, frame, region).Dir}"))));
            foreach (var b in cands.OrderBy(b => b.Cx))
            {
                var (dir, up, _, left, _) = ClassifyScores(b, w);
                DiagLog($"  후보 ({region.X + b.Cx:0},{region.Y + b.Cy:0}) a{b.Area} {b.W}x{b.H} {dir} |L{left:+0.00;-0.00}|U{up:+0.00;-0.00}|");
            }
        }
        return row;
    }

    /// <summary>후보들 중 '화살표 줄' 4개 선택 — 같은 높이(±RowBandPx), 이웃 간격 35~280px,
    /// 크기 유사(최대/최소 면적 ≤5배), 배너 중심 정렬(±12% 폭). 조건을 만족하는 조합 중
    /// '면적 합 − 박스 불균일 벌점 − 중심 이탈'이 최대인 것. 화살표 4개는 글리프 크기가 같아
    /// 바운딩 박스가 거의 동일(22~30px)하고, 불꽃 잔광·이펙트 블롭은 박스가 튄다(34x28, 49x57 —
    /// 01:16·00:45 실행에서 면적만으로는 정크가 진짜 화살표를 밀어냈다). 모양 비대칭은 판별자로
    /// 못 쓴다 — 진짜 ←도 침식되면 |0.06|까지 떨어진다(00:45 sat80). 없으면 null.
    /// srcBonus = 블롭별 가산점 훅(소스 융합 전용 — 교차 확인 후보 우대). null이면 동작 불변.</summary>
    private static List<Blob>? PickRow(List<Blob> cands, double bannerCx, int frameW, Func<Blob, double>? srcBonus = null, int take = 20)
    {
        if (cands.Count < 4) return null;
        // 상위 20개 — 불타는 맵은 노이즈 블롭이 커서 14개 컷으로는 작은 화살표가 밀려났다.
        // 2패스(확장 구간 합류)는 take=전체로 프루닝 해제 — 19:13 실측: 후보 38개 중 진짜
        // 슬롯1(a294)이 면적 순위 21위로 잘려 줄 구성 실패. 2패스는 구 코드가 무조건 실패하던
        // 프레임에서만 돌아 조합 수 증가(≤C(40,4))는 실전 주기 대비 무시 가능.
        var top = cands.OrderByDescending(b => b.Area).Take(take).OrderBy(b => b.Cx).ToList();
        int n = top.Count;
        List<Blob>? best = null; double bestScore = double.MinValue;
        for (int a = 0; a < n - 3; a++)
            for (int b2 = a + 1; b2 < n - 2; b2++)
                for (int c = b2 + 1; c < n - 1; c++)
                    for (int d = c + 1; d < n; d++)
                    {
                        var combo = new[] { top[a], top[b2], top[c], top[d] };
                        double yMin = combo.Min(x => x.Cy), yMax = combo.Max(x => x.Cy);
                        if (yMax - yMin > RowBandPx) continue;
                        bool gapsOk = true;
                        double gMin = double.MaxValue, gMax = 0;
                        // 간격은 '상식 범위'만 하드 게이트 — 이 룬 UI의 글리프 간격은 균일하지 않다.
                        // 실측(1076px 창): 카르시온 17:52 실전 97/136/49(비 2.78!) — ←·→가 49px로
                        // 붙어 렌더됐다. 좁은 범위·균일비 하드컷은 진짜 줄을 두 번 죽였다(17:36·17:52).
                        // 잡줄 방어는 점수 경쟁(면적 — 진짜 화살표 a300~460 vs 파편 a50~150)과
                        // 아래 소프트 균일비 페널티가 담당한다.
                        double gLo = frameW * GapFracLo, gHi = frameW * GapFracHi;
                        for (int i = 1; i < 4; i++)
                        {
                            double gap = combo[i].Cx - combo[i - 1].Cx;
                            if (gap < gLo || gap > gHi) { gapsOk = false; break; }
                            gMin = Math.Min(gMin, gap); gMax = Math.Max(gMax, gap);
                        }
                        if (!gapsOk || gMax > gMin * GapRatioHardMax) continue;
                        int aMin = combo.Min(x => x.Area), aMax = combo.Max(x => x.Area);
                        if (aMax > aMin * 5) continue; // 크기가 제각각인 묶음은 화살표 줄이 아니다
                        double avgX = combo.Average(x => x.Cx);
                        double centerPenalty = 0;
                        if (bannerCx >= 0)
                        {
                            double off = Math.Abs(avgX - bannerCx);
                            // 필은 창 중앙이 아니라 룬의 월드 좌표에 앵커된다 — 맵 가장자리 카메라
                            // 클램프 시 창 중앙에서 밀린다(실측: 20:19 +2 · 20:38 +51 · 22:08 +67px,
                            // 22:08은 0.05W=57px 게이트가 진짜 줄 ↓←→↓를 죽여 2/4 실패). 0.065W로
                            // 완화 — 상한 실측 창: 진짜 최대 이탈 67px=0.058W(1149) < 0.065W < DLUU
                            // 잡줄 80px=0.075W(1076)·한 칸 밀린 줄 ≈ 간격 90px=0.078W(14:04 룬).
                            // 0.075W를 시도했더니 DLUU 잡줄이 0.45px 차로 통과해 ④가 잡줄을 채택하는
                            // 회귀가 났다(부분 줄 보완 경로가 영영 안 돎). 중앙 근접 선호(이탈×2
                            // 페널티)는 계속 유지.
                            if (off > frameW * RowCenterTolFrac) continue;
                            centerPenalty = off * 2;
                        }
                        double wRatio = combo.Max(x => x.W) / (double)combo.Min(x => x.W);
                        double hRatio = combo.Max(x => x.H) / (double)combo.Min(x => x.H);
                        double boxPenalty = 300 * (wRatio - 1 + hRatio - 1);
                        double areaSum = combo.Sum(x => (double)x.Area);
                        // 소프트 균일비 페널티(면적 비례) — 균일한 줄을 선호하되 결격은 아니다.
                        // DDRD: 잡줄(비 1.67, 면적 우세)이 진짜 줄(비 1.08)을 하드컷 완화 때 밀어냈던
                        // 사례의 방어를 점수로 옮긴 것. 카르시온의 진짜 줄(비 2.78)은 면적 합이
                        // 파편 줄의 3배 이상이라 페널티를 감수하고도 이긴다.
                        double gapPenalty = areaSum * GapRatioSoftW * (gMax / gMin - 1);
                        double score = areaSum - centerPenalty - boxPenalty - gapPenalty;
                        if (srcBonus is not null) score += combo.Sum(srcBonus);
                        if (score > bestScore) { bestScore = score; best = combo.ToList(); }
                    }
        if (best is not null) RepairEdgeSlot(best, cands);
        return best;
    }

    /// <summary>에지 슬롯 간격 외삽 교체 — 선택된 줄의 끝 슬롯(1번/4번)이 잡블롭일 때 복구.
    /// 조건: 나머지 두 간격이 균일(비 ≤1.3)한데 에지 간격만 그 평균의 1.5배 이상 → 외삽 위치
    /// (±25px, 줄 y밴드 유지)에 실존하는 후보가 있으면 <b>면적과 무관하게</b> 그 후보로 교체.
    /// 근거(20:19 실전, D R D D): 진짜 줄 439/531/628/711(간격 92/97/83)의 4번 보라가 침식(a263)
    /// +3번이 몹과 병합(a1347)돼 크기 게이트(≤5배)에서 정답 조합이 탈락(비 5.12), 대신 버섯
    /// a783이 4번을 차지(간격 92/97/161) → 관측점 난수 각도 → 3/4 실패. 균일 간격 실존 후보는
    /// 잡블롭보다 강한 줄 증거다. 불균일 진짜 줄(카르시온2 97/136/49 — 나머지 비 1.98)과
    /// 정상 줄(LUUX 108/89/90 — 에지 1.2배)은 발동 조건에 걸리지 않음을 픽스처로 검증.</summary>
    private static void RepairEdgeSlot(List<Blob> row, List<Blob> cands)
    {
        for (int pass = 0; pass < 2; pass++)
        {
            bool lastEdge = pass == 0;
            double g1 = row[1].Cx - row[0].Cx, g2 = row[2].Cx - row[1].Cx, g3 = row[3].Cx - row[2].Cx;
            double edge = lastEdge ? g3 : g1;
            double oA = lastEdge ? g1 : g2, oB = lastEdge ? g2 : g3;
            if (Math.Max(oA, oB) > Math.Min(oA, oB) * EdgeGapUniformMax) continue;
            double m = (oA + oB) / 2;
            if (edge < m * EdgeGapSuspectMin) continue;
            int slot = lastEdge ? 3 : 0;
            double expX = lastEdge ? row[2].Cx + m : row[1].Cx - m;
            // 유지되는 3개와 y밴드를 이뤄야 한다 — 의심 에지 블롭의 y는 기준에서 제외
            var kept = row.Where((_, i) => i != slot).ToList();
            double kyMin = kept.Min(b => b.Cy), kyMax = kept.Max(b => b.Cy);
            Blob? swap = null;
            foreach (var c in cands)
            {
                if (row.Contains(c)) continue;
                if (Math.Abs(c.Cx - expX) > EdgeRepairTolPx) continue;
                if (Math.Max(kyMax, c.Cy) - Math.Min(kyMin, c.Cy) > RowBandPx) continue;
                if (swap is null || c.Area > swap.Area) swap = c;
            }
            if (swap is null) continue;
            DiagLog?.Invoke($"[에지 보정] 슬롯{slot + 1} ({row[slot].Cx:0},{row[slot].Cy:0})a{row[slot].Area} 간격 {edge:0} → "
                + $"외삽 {expX:0}±{EdgeRepairTolPx}의 ({swap.Cx:0},{swap.Cy:0})a{swap.Area}로 교체");
            row[slot] = swap;
        }
    }

    /// <summary>화살표 탐색 밴드 — <b>창 기준 고정 기하</b>. 룬 퍼즐 UI는 창 기준 고정 위치다
    /// (초기 실측 두 맵: 배너 띠 상단 ≈ 창높이 20%, 안내 텍스트 ≈ 23~27%, 화살표 줄 ≈ 29.5~33%).
    /// 발동 전 차분·어두워짐으로 배너를 '탐지'하는 방식은 불타는 맵에서 세 번 다르게 실패했다
    /// (첫 행 오검출 → 불꽃 노이즈 / 피크 앵커 → 불꽃이 사라진 자리 어두워짐이 배너보다 큼) —
    /// 배경과 무관한 고정 밴드가 유일하게 안정적이다.
    /// 상단 0.28→0.21(2026-08-05 사용자 지정 "세로 1.5배까지 위로"): 19:13 실전에서 화살표
    /// 중심이 27.6%(y205)에 떠 상단 컷(28%)에 걸려 전 소스 줄 실패 → 3/4 안전 종료.
    /// 텍스트 글리프(23~27%)가 밴드 안으로 들어오지만, 잡줄 방어는 컷이 아니라 줄 게이트·
    /// 면적 경쟁·웜 교차 검증·방향 협응·3소스 합의가 담당한다(회귀 전 세트로 검증).
    /// CenterX = 탐색 영역 가로 중앙(퍼즐 UI는 창 중앙 정렬 — 우측 팝업 줄 배제용).</summary>
    private const double ArrowBandTopFrac = 0.21, ArrowBandBotFrac = 0.42;

    private static (int Y0, int Y1, double CenterX) BannerBand(int w, int h, int fullFrameH)
    {
        int offset = (int)(RegionY0 * fullFrameH); // 탐색 영역 상단의 창 기준 y (크롭·전체 프레임 공통)
        int y0 = Math.Clamp((int)(ArrowBandTopFrac * fullFrameH) - offset, 0, h - 1);
        int y1 = Math.Clamp((int)(ArrowBandBotFrac * fullFrameH) - offset, y0, h - 1);
        return (y0, y1, w / 2.0);
    }

    /// <summary>퍼즐 영역 크롭 안에서 화살표 밴드가 차지하는 사각형 — 실패 진단 녹화(스트립)용.</summary>
    public static Rectangle ArrowBandRect(int cropW, int cropH)
    {
        int fullH = (int)(cropH / (RegionY1 - RegionY0));
        var (y0, y1, _) = BannerBand(cropW, cropH, fullH);
        return new Rectangle(0, y0, cropW, Math.Max(1, y1 - y0));
    }
}
