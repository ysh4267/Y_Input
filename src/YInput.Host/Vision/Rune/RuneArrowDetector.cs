using System.Drawing;
using System.Drawing.Imaging;

namespace YInput.Host.Vision;

/// <summary>퍼즐 화살표 하나 — 화면 좌표(중심)와 방향('L','R','U','D').</summary>
internal readonly record struct RuneArrow(PointF Center, char Dir);

/// <summary>한 프레임에서 분석한 화살표 하나 — 방향 + 모양 시그니처(회전 정지 판별용) + 블롭 면적
/// (줄 재선출 비교용 — 잡줄은 파편이라 면적 합이 진짜 줄의 1/3 수준).</summary>
internal readonly record struct ArrowSample(PointF Center, char Dir, bool[] Sig, int Area = 0);

/// <summary>
/// 룬 발동(스페이스) 후 화면 상단 배너 아래에 뜨는 방향키 퍼즐(화살표 4개)을 인식한다.
/// 화살표는 채도 높은 작은 글리프(~20~30px)인데 <b>색은 룬마다 다르고</b> 하이라이트가
/// 쓸고 지나가는 애니메이션이 있다. 인식 전략(20:00·20:16 실패 프레임 분석 반영):
///  ① 주 경로 — 퍼즐이 뜬 상태에서 여러 프레임(~0.6초)의 <b>연속 차분 합집합</b>:
///     하이라이트가 글리프 전체를 쓸고 지나가 화살표 픽셀만 채워지고,
///     바·배경(반투명 바 너머 장식 포함)·배너는 정지라 비어 있다.
///  ② 폴백 — 애니메이션이 없으면 발동 직전 프레임과의 차분(새로 나타난 픽셀).
///  ③ 얇은 구조 제거(가로·세로 두께 6px 미만) — 바 외곽선·글로우 다리·눈송이 궤적 절단.
///  ④ 안내 배너(가로로 넓게 변한 띠) 아래 밴드로 제한 — 데미지 숫자 등 배제.
///  ⑤ 방향은 색이 아니라 모양 — 가리키는 쪽 가장자리의 '가운데'가 볼록(꼭짓점).
/// </summary>
internal static partial class RuneArrowDetector
{
    // 퍼즐 UI가 뜨는 탐색 범위(화면 비율) — 상단 중앙 넓게
    private const double RegionX0 = 0.08, RegionX1 = 0.92;
    private const double RegionY0 = 0.02, RegionY1 = 0.60;

    private const int VividMax = 120;    // 채도 판정: 최대 채널 밝기 하한
    private const int VividSat = 45;     // 채도 판정: (최대-최소) 하한 — 반투명 합성으로 채도가 깎이므로 느슨하게
    private const int VividSatStrict = 80; // 교집합 경로용 고채도 — 필 너머로 비치는 불꽃 잔광(둔탁한 웜톤)은
                                           // 지우고 화살표(선명한 무지개)만 남긴다. 00:45 프레임 검증값
    private const int VividSatUltra = 140; // 로컬 글리프 분리용 초고채도 — 애니메이션 배경 장식(수정 반짝임,

    /// <summary>퍼즐 UI 탐색 영역(창 상대) — 캡처를 이 영역만 화면 복사로 뜨면 전체 창 캡처보다
    /// 훨씬 빠르다(취소 타이머 3초 안에 인식·입력을 끝내야 함).</summary>
    public static Rectangle PuzzleRegion(int frameW, int frameH) => new(
        (int)(frameW * RegionX0), (int)(frameH * RegionY0),
        (int)(frameW * (RegionX1 - RegionX0)), (int)(frameH * (RegionY1 - RegionY0)));

    /// <summary>주 경로 — 연속 캡처 프레임들(같은 크기, 시간순)의 차분 합집합으로 애니메이션
    /// 화살표만 분리해 인식. bannerRef = 발동 직전 프레임(탐색 밴드 제한).
    /// precropped = 입력이 이미 PuzzleRegion으로 잘려 있음(전체를 영역으로 사용). 실패 시 null.</summary>
    public static List<RuneArrow>? FindArrowsAnimated(IReadOnlyList<Bitmap> frames, Bitmap? bannerRef, bool precropped = false)
    {
        if (frames.Count < 2) return null;
        var frame = frames[^1];
        if (!TryRegion(frame, precropped, out var region)) return null;
        foreach (var f in frames)
            if (f.Width != frame.Width || f.Height != frame.Height) return null;

        int w = region.Width, h = region.Height;
        var changed = new bool[w * h];
        for (int k = 1; k < frames.Count; k++)
            AccumulateDiff(frames[k - 1], frames[k], region, w, h, AnimDiffMin, changed);
        var mask = VividMask(frame, region, w, h);
        for (int i = 0; i < mask.Length; i++) mask[i] &= changed[i];
        // 차분이 정지된 바 외곽선을 이미 지웠으므로 두께 필터는 생략 — 반투명 합성으로 작아진
        // 화살표 코어(2~3px 획)가 두께 필터에 지워지는 것이 20:24 인식 실패의 원인이었다.
        return Detect(frame, bannerRef, mask, region, w, h, thinFilter: false, FullFrameH(frame, precropped));
    }

    /// <summary>배너 밴드 길이 계산용 '원본 창 높이' — 입력이 퍼즐 영역 크롭이면 비율로 역산.
    /// (크롭 높이를 그대로 쓰면 밴드가 95px로 줄어 화살표가 밴드 밖으로 걸러졌다 — 23:39 실행)</summary>
    private static int FullFrameH(Bitmap frame, bool precropped) =>
        precropped ? (int)(frame.Height / (RegionY1 - RegionY0)) : frame.Height;

    /// <summary>폴백 — 단일 프레임 + 발동 직전 프레임 차분(새로 나타난 채도 높은 픽셀)으로 인식.
    /// diffRef 없으면 채도 마스크만 사용(진단용). bannerRef = 탐색 밴드 제한.</summary>
    public static List<RuneArrow>? FindArrows(Bitmap frame, Bitmap? diffRef = null, Bitmap? bannerRef = null, bool precropped = false)
    {
        if (!TryRegion(frame, precropped, out var region)) return null;
        if (diffRef is not null && (diffRef.Width != frame.Width || diffRef.Height != frame.Height)) diffRef = null;

        int w = region.Width, h = region.Height;
        var mask = VividMask(frame, region, w, h);
        if (diffRef is not null)
        {
            var changed = new bool[w * h];
            AccumulateDiff(diffRef, frame, region, w, h, DiffMin, changed);
            for (int i = 0; i < mask.Length; i++) mask[i] &= changed[i];
        }
        // 폴백 경로에는 바 외곽선이 남아 있어 두께 필터로 화살표와의 연결을 끊는다
        return Detect(frame, bannerRef, mask, region, w, h, thinFilter: true, FullFrameH(frame, precropped));
    }

    /// <summary>배너 띠(안내 텍스트 배경)의 창 기준 세로 구간 — 실측 두 맵 동일(상단 ≈ 20%H).</summary>
    private const double BannerStripTopFrac = 0.18, BannerStripBotFrac = 0.28;
    private const int PresentMinPx = 250; // 텍스트 신호 임계 — 안내 텍스트 글리프는 수백~수천 px

    /// <summary>퍼즐(안내 배너)이 지금 떠 있는지 — 열린 퍼즐에 스페이스를 다시 누르면 오답 입력이
    /// 되므로 재발동 전 확인용. 신호 = 배너 띠의 <b>창 기준 고정 구간</b>(18~28%H)에서
    /// '발동 전과 다름 + 지금 정지 + 채도 높음'인 픽셀 수 — 안내 텍스트("방향키" 주황 글리프)는
    /// 퍼즐이 떠 있는 동안(회전 중에도) 항상 정지·선명·새로 나타난 픽셀 수백 개를 만든다.
    /// 이전의 행 단위 어두워짐/정지/평탄화 판정은 불타는 맵에서 전부 오판했다(불꽃이 사라진
    /// 자리는 영구 어두움 → 01:17 재발동 포기, 반투명 띠 너머 고대비 배경은 평탄화 무효).</summary>
    public static bool PuzzlePresent(Bitmap frameA, Bitmap frameB, Bitmap? bannerRef, bool precropped = false)
        => PresentPixelCount(frameA, frameB, bannerRef, precropped) >= PresentMinPx;

    /// <summary>배너 텍스트 신호 픽셀 수 — <see cref="PuzzlePresent"/>의 원값(진단 출력용).</summary>
    internal static int PresentPixelCount(Bitmap frameA, Bitmap frameB, Bitmap? bannerRef, bool precropped = false)
    {
        if (bannerRef is null) return 0;
        if (bannerRef.Width != frameB.Width || bannerRef.Height != frameB.Height) return 0;
        if (frameA.Width != frameB.Width || frameA.Height != frameB.Height) return 0;
        if (!TryRegion(frameB, precropped, out var region)) return 0;
        int fullH = FullFrameH(frameB, precropped);
        int offset = (int)(RegionY0 * fullH);
        int sy0 = Math.Clamp((int)(BannerStripTopFrac * fullH) - offset, 0, region.Height - 1);
        int sy1 = Math.Clamp((int)(BannerStripBotFrac * fullH) - offset, sy0, region.Height - 1);
        var strip = new Rectangle(region.X, region.Y + sy0, region.Width, sy1 - sy0 + 1);
        int w = strip.Width, h = strip.Height;

        var vivid = VividMask(frameB, strip, w, h);
        var fresh = new bool[w * h];
        AccumulateDiff(bannerRef, frameB, strip, w, h, DiffMin, fresh);
        var moving = new bool[w * h];
        AccumulateDiff(frameA, frameB, strip, w, h, AnimDiffMin, moving);
        // 어두워진 행 게이트 — 배너 텍스트는 어두운 반투명 띠 '위'에 그려져 해당 행의 평균 밝기가
        // 발동 전보다 떨어진다(실측 27~38). 몹 넉백으로 카메라가 밀리면 금색 장식 등이 '새로 나타난
        // 정지 픽셀'로 찍혀 열림 오판을 일으키는데(10:51 실행), 그런 행은 어두워지지 않아 걸러진다.
        var (_, lumBefore, lumNow, _, _) = RowStats(bannerRef, frameB, strip, DiffMin);
        int count = 0;
        for (int y = 0; y < h; y++)
        {
            if (lumBefore[y] - lumNow[y] < 6) continue;
            int o = y * w;
            for (int x = 0; x < w; x++)
            {
                int i = o + x;
                if (vivid[i] && fresh[i] && !moving[i]) count++;
            }
        }
        return count;
    }

    // ---------- 공통 파이프라인 ----------
    private static bool TryRegion(Bitmap frame, bool precropped, out Rectangle region)
    {
        region = precropped
            ? new Rectangle(0, 0, frame.Width, frame.Height)
            : Rectangle.Intersect(PuzzleRegion(frame.Width, frame.Height), new Rectangle(0, 0, frame.Width, frame.Height));
        return region.Width >= 100 && region.Height >= 60;
    }

    /// <summary>단일 이전 프레임 편의 오버로드 — <see cref="AnalyzeFrame(Bitmap, IReadOnlyList{Bitmap}, Bitmap?, bool)"/> 참조.</summary>
    public static List<ArrowSample>? AnalyzeFrame(Bitmap frame, Bitmap? prevFrame, Bitmap? bannerRef, bool precropped = false)
        => AnalyzeFrame(frame, prevFrame is null ? [] : [prevFrame], bannerRef, precropped);

    /// <summary>한 프레임 분석 — 화살표 4개의 방향 + 모양 시그니처. recent = 직전 프레임들(오래된 순).
    /// 마스크를 단계별로 시도:
    ///  ⓪ 채도 <b>교집합</b>(고채도 80 → 완화 45) + '발동 전과 다름' — 화살표는 정적 UI라 모든
    ///     프레임에서 같은 자리가 계속 채도 높고, 흔들리는 불꽃·이펙트는 위치가 바뀌어 교집합에서
    ///     탈락한다(00:45 불타는 맵: 이 경로만 정답 → ↑ ← ↓ 복원, 나머지는 전부 오염)
    ///  ⓐ 채도 + '발동 전과 다름' + '직전 프레임과 정지' — 교집합이 실패한 경우(프레임 부족 등)
    ///  ⓑ 채도 + '발동 전과 다름' — 회전 중 화살표(정지 조건에 안 걸림) 대비
    ///  ⓒ 채도 단독 — 기준 프레임이 오염된 경우 최후 수단
    /// 회전형은 호출자가 연속 샘플링해 시그니처·방향이 유지되는 구간(멈춤)에서 확정한다.</summary>
    public static List<ArrowSample>? AnalyzeFrame(Bitmap frame, IReadOnlyList<Bitmap> recent, Bitmap? bannerRef, bool precropped = false)
    {
        if (!TryRegion(frame, precropped, out var region)) return null;
        var prevs = recent.Where(p => p.Width == frame.Width && p.Height == frame.Height).ToList();
        if (bannerRef is not null && (bannerRef.Width != frame.Width || bannerRef.Height != frame.Height)) bannerRef = null;
        int w = region.Width, h = region.Height;
        int fullH = FullFrameH(frame, precropped);

        var vivid = VividMask(frame, region, w, h, requireWarm: false);
        bool[]? diffBefore = null;
        if (bannerRef is not null)
        {
            diffBefore = new bool[w * h];
            AccumulateDiff(bannerRef, frame, region, w, h, DiffMin, diffBefore);
        }

        List<Blob>? row = null;
        if (diffBefore is not null && prevs.Count >= 1)
        {
            // 고채도(80)·완화(45) 두 마스크를 모두 시도해 '면적이 고른' 줄을 채택 — 어떤 맵은
            // 고채도에서 화살표가 침식돼 조각나고(면적 제각각), 어떤 맵은 완화에서 불꽃 잔광이
            // 화살표에 들러붙어 한 블롭만 커진다. 균일한 쪽이 깨끗한 분리다(동률이면 면적 합 큰 쪽).
            List<Blob>? best = null; double bestRatio = double.MaxValue; int bestArea = 0;
            foreach (int sat in (int[])[VividSatStrict, VividSat])
            {
                var mi = VividMask(frame, region, w, h, sat, requireWarm: false);
                foreach (var p in prevs)
                {
                    var vp = VividMask(p, region, w, h, sat, requireWarm: false);
                    for (int i = 0; i < mi.Length; i++) mi[i] &= vp[i];
                }
                for (int i = 0; i < mi.Length; i++) mi[i] &= diffBefore[i];
                var r = DetectRow(frame, bannerRef, mi, region, w, h, thinFilter: true, fullH);
                if (r is null) continue;
                double ratio = (double)r.Max(b => b.Area) / r.Min(b => b.Area);
                int area = r.Sum(b => b.Area);
                if (ratio < bestRatio - 0.01 || (Math.Abs(ratio - bestRatio) <= 0.01 && area > bestArea))
                { best = r; bestRatio = ratio; bestArea = area; }
            }
            row = best;
        }
        if (row is null && diffBefore is not null && prevs.Count >= 1)
        {
            var moving = new bool[w * h];
            AccumulateDiff(prevs[^1], frame, region, w, h, AnimDiffMin, moving);
            var maskA = new bool[w * h];
            for (int i = 0; i < maskA.Length; i++) maskA[i] = vivid[i] && diffBefore[i] && !moving[i];
            row = DetectRow(frame, bannerRef, maskA, region, w, h, thinFilter: true, fullH);
        }
        if (row is null && diffBefore is not null)
        {
            var maskB = new bool[w * h];
            for (int i = 0; i < maskB.Length; i++) maskB[i] = vivid[i] && diffBefore[i];
            row = DetectRow(frame, bannerRef, maskB, region, w, h, thinFilter: true, fullH);
        }
        row ??= DetectRow(frame, bannerRef, (bool[])vivid.Clone(), region, w, h, thinFilter: true, fullH);
        if (row is null) return null;

        var result = new List<ArrowSample>(4);
        foreach (var b in row)
        {
            var (dir, _, _, _, _) = ClassifyScores(b, w, frame, region);
            result.Add(new ArrowSample(new PointF((float)(region.X + b.Cx), (float)(region.Y + b.Cy)), dir, Signature(b, w), b.Area));
        }
        return result;
    }

    /// <summary>한 화살표의 로컬 분석 결과. Dir = 4방위 분류(정지 글리프용), Sig = 모양 시그니처,
    /// AngleDeg = 가리키는 각도(0=→, 90=↑, 반시계 양수; 회전 글리프 추적용), Area = 픽셀 수,
    /// Center = 블롭 중심(프레임 절대 좌표 — 회전 핵이 어긋난 위치 추정을 자기 보정하는 데 쓴다),
    /// MovingPx = 박스 안 '채도 높고 직전 프레임과 다른' 픽셀 수 — 회전 중 여부 판별용
    /// (움직임 마스크의 블롭은 글리프가 아니라 '변화 영역' 조각이라 각도에는 절대 쓰지 않는다).</summary>
    internal readonly record struct LocalArrow(char Dir, bool[] Sig, double AngleDeg, int Area, PointF Center, int MovingPx);

    /// <summary>프레임의 지정 사각형(한 화살표 주변)만 분석.
    /// 글리프 = 초고채도(140→80→45 체인) ∧ 발동 전 차분 — 애니메이션 배경 장식(수정 반짝임,
    /// sat 80~120)은 초고채도가 지우고, 순수 무지개 글리프만 남는다. 회전 여부는 별도로
    /// '채도 ∧ 직전 프레임 차분' 픽셀 수(MovingPx)로만 판별한다. '글리프 크기(60~1200px,
    /// 박스≤60) + 중심 근접' 블롭 선택, 두께 필터 없음(회전형은 속이 빈 외곽선이라 지워진다).
    /// 각도 = 주축(관성) + 머리쪽(픽셀 많은 반쪽). 실패 시 null.</summary>
    internal static LocalArrow? AnalyzeArrowAt(Bitmap frame, Bitmap? bannerRef, Bitmap? prevFrame, Rectangle localRect)
    {
        var bounds = new Rectangle(0, 0, frame.Width, frame.Height);
        var rect = Rectangle.Intersect(localRect, bounds);
        if (rect.Width < 12 || rect.Height < 12) return null;
        if (bannerRef is null || bannerRef.Width != frame.Width || bannerRef.Height != frame.Height) return null;
        if (ReferenceEquals(frame, bannerRef)) return null; // 자기 자신 차분 = 같은 비트맵 이중 잠금
        if (prevFrame is not null && (ReferenceEquals(frame, prevFrame)
            || prevFrame.Width != frame.Width || prevFrame.Height != frame.Height)) prevFrame = null;
        int w = rect.Width, h = rect.Height;

        Blob? Pick(bool[] mask) => MergeNear(FindBlobs(mask, w, h))
            .Where(x => x.Area is >= 60 and <= 1200 && x.W <= 60 && x.H <= 60)
            .OrderBy(x => Math.Pow(x.Cx - w / 2.0, 2) + Math.Pow(x.Cy - h / 2.0, 2))
            .FirstOrDefault();

        Blob? pick = null;
        var fresh = new bool[w * h];
        AccumulateDiff(bannerRef, frame, rect, w, h, DiffMin, fresh);
        // 2차 시도(thin=true) = 글로우 다리 절단 — 화살표가 인접 장식(선인장·게이지 바)과 안티앨리어싱
        // 다리로 연결되면 박스 전체가 한 블롭(면적 상한 초과)이 되어 픽 실패한다(2026-08-04 실측:
        // 회전 화살표1이 전 채도에서 a2145~3068 단일 덩어리 → 3초 내내 각도 표본 0개 → 반동 관측 불가).
        foreach (bool thin in (bool[])[false, true])
        {
            foreach (int sat in (int[])[VividSatUltra, VividSatStrict, VividSat])
            {
                var mask = VividMask(frame, rect, w, h, sat, requireWarm: false);
                for (int i = 0; i < mask.Length; i++) mask[i] &= fresh[i];
                if (thin) ThinFilter(mask, w, h);
                pick = Pick(mask);
                if (DiagLog is not null)
                {
                    var all = MergeNear(FindBlobs(mask, w, h)).OrderByDescending(x => x.Area).Take(4);
                    DiagLog($"[로컬 sat{sat}{(thin ? "+thin" : "")}] {(pick is null ? "실패" : $"pick a{pick.Area} {pick.W}x{pick.H}")} 블롭: "
                            + string.Join(" ", all.Select(x => $"a{x.Area}({x.W}x{x.H})")));
                    if (DiagMaskDir is not null)
                        SaveMaskPng((bool[])mask.Clone(), w, h, Path.Combine(DiagMaskDir, $"local-mask-sat{sat}{(thin ? "-thin" : "")}.png"));
                }
                if (pick is not null) break;
            }
            if (pick is not null) break;
        }
        // 최후 폴백: 움직임 차분 — 화살표가 인접 장식과 같은 채도 덩어리로 붙어 전 단계가 실패하면
        // (2026-08-04: 회전 화살표1이 선인장·게이지와 한 덩어리 a2145+ → 3초간 각도 표본 0),
        // 직전 프레임 차분으로 '움직이는 것'만 남긴다. 정지 장식은 지워지고 회전 글리프만 남는다.
        // 두 자세의 합집합이라 각도는 다소 흐리지만, 반동 감지는 스텝 이상만 보므로 감내 가능
        // (아래 '움직임 마스크 각도 금지' 주석은 정지 판별 얘기 — 여기는 회전 전용 최후 수단).
        if (pick is null && prevFrame is not null)
        {
            var mv = new bool[w * h];
            AccumulateDiff(prevFrame, frame, rect, w, h, AnimDiffMin, mv);
            foreach (int sat in (int[])[VividSatUltra, VividSatStrict])
            {
                var mask = VividMask(frame, rect, w, h, sat, requireWarm: false);
                for (int i = 0; i < mask.Length; i++) mask[i] &= mv[i] && fresh[i];
                pick = Pick(mask);
                if (DiagLog is not null)
                {
                    var all = MergeNear(FindBlobs(mask, w, h)).OrderByDescending(x => x.Area).Take(4);
                    DiagLog($"[로컬 sat{sat}+움직임] {(pick is null ? "실패" : $"pick a{pick.Area} {pick.W}x{pick.H}")} 블롭: "
                            + string.Join(" ", all.Select(x => $"a{x.Area}({x.W}x{x.H})")));
                }
                if (pick is not null) break;
            }
        }
        if (pick is null) return null;
        var b = pick;

        int movingPx = 0;
        if (prevFrame is not null)
        {
            var vv = VividMask(frame, rect, w, h, requireWarm: false);
            var mv = new bool[w * h];
            AccumulateDiff(prevFrame, frame, rect, w, h, AnimDiffMin, mv);
            for (int i = 0; i < vv.Length; i++) if (vv[i] && mv[i]) movingPx++;
        }

        var (dir, _, _, _, _) = ClassifyScores(b, w, frame, rect);
        var sig = Signature(b, w);

        // 주축 각도 — 화면 y는 아래가 양수이므로 수학 좌표로 뒤집어 계산(0=→, 90=↑)
        double sxx = 0, syy = 0, sxy = 0;
        foreach (var p in b.Pixels)
        {
            double ux = p % w - b.Cx, uy = -(p / w - b.Cy);
            sxx += ux * ux; syy += uy * uy; sxy += ux * uy;
        }
        double phi = 0.5 * Math.Atan2(2 * sxy, sxx - syy); // 라디안, (-90°, 90°]
        double ca = Math.Cos(phi), sa = Math.Sin(phi);
        int headPos = 0, headNeg = 0;
        foreach (var p in b.Pixels)
        {
            double proj = (p % w - b.Cx) * ca + -(p / w - b.Cy) * sa;
            if (proj > 0) headPos++; else if (proj < 0) headNeg++;
        }
        double deg = phi * 180 / Math.PI;
        if (headNeg > headPos) deg += 180;
        deg = (deg % 360 + 360) % 360;
        return new LocalArrow(dir, sig, deg, b.Area, new PointF((float)(rect.X + b.Cx), (float)(rect.Y + b.Cy)), movingPx);
    }

    private static List<RuneArrow>? Detect(Bitmap frame, Bitmap? bannerRef, bool[] mask, Rectangle region, int w, int h, bool thinFilter, int fullFrameH)
    {
        var row = DetectRow(frame, bannerRef, mask, region, w, h, thinFilter, fullFrameH);
        if (row is null) return null;
        var result = new List<RuneArrow>(4);
        foreach (var b in row)
        {
            var (dir, _, _, _, _) = ClassifyScores(b, w, frame, region);
            result.Add(new RuneArrow(new PointF((float)(region.X + b.Cx), (float)(region.Y + b.Cy)), dir));
        }
        return result;
    }

    /// <summary>진단 CLI에서만 설정 — DetectRow가 밴드·후보·선택 결과를 이 콜백으로 알린다.</summary>
    internal static Action<string>? DiagLog;

    /// <summary>진단 CLI에서만 설정 — AnalyzeArrowAt이 로컬 마스크 PNG를 이 폴더에 덤프한다.</summary>
    internal static string? DiagMaskDir;

    // ---------- 모양 시그니처(회전 정지 판별) ----------
    private const int SigN = 12; // 바운딩박스를 12×12 셀로 정규화

    /// <summary>블롭 모양을 바운딩박스 정규화 12×12 그리드로 요약 — 회전 중이면 프레임마다 달라지고,
    /// 멈춰 있으면(정답 표시 구간) 연속 프레임에서 같게 유지된다.</summary>
    private static bool[] Signature(Blob b, int w)
    {
        var sig = new bool[SigN * SigN];
        int bw = b.MaxX - b.MinX + 1, bh = b.MaxY - b.MinY + 1;
        foreach (var p in b.Pixels)
        {
            int cx = (p % w - b.MinX) * SigN / bw;
            int cy = (p / w - b.MinY) * SigN / bh;
            sig[cy * SigN + cx] = true;
        }
        return sig;
    }

    /// <summary>두 시그니처가 '같은 모양'인가 — 일치 셀 비율 기준.</summary>
    public static bool SigSimilar(bool[] a, bool[] b, double minMatch = 0.90)
    {
        if (a.Length != b.Length) return false;
        int same = 0;
        for (int i = 0; i < a.Length; i++) if (a[i] == b[i]) same++;
        return (double)same / a.Length >= minMatch;
    }

    /// <summary>모양 분류 — '양끝 폭 비교': 화살표 머리 끝은 뾰족해서 수직 폭이 좁고,
    /// 꼬리 끝은 축(shaft)·베이스라 넓다. 비대칭이 큰 축이 화살표 축이고 폭이 좁은 끝이 방향.
    /// (이전의 '가운데 볼록' 지표는 축 달린 화살표에서 꼬리 축이 가운데 행만 길게 튀어나와
    /// 4방향 모두 정반대로 뒤집혔다 — 22:44 오답 프레임으로 확인.)</summary>
    private static (char Dir, double Up, double Down, double Left, double Right) ClassifyScores(Blob b, int w, Bitmap? frame = null, Rectangle region = default)
    {
        int bw = b.MaxX - b.MinX + 1, bh = b.MaxY - b.MinY + 1;
        var minY = new int[bw]; var maxY = new int[bw];
        var minX = new int[bh]; var maxX = new int[bh];
        for (int i = 0; i < bw; i++) { minY[i] = int.MaxValue; maxY[i] = int.MinValue; }
        for (int i = 0; i < bh; i++) { minX[i] = int.MaxValue; maxX[i] = int.MinValue; }
        foreach (var p in b.Pixels)
        {
            int px = p % w - b.MinX, py = p / w - b.MinY;
            if (py < minY[px]) minY[px] = py;
            if (py > maxY[px]) maxY[px] = py;
            if (px < minX[py]) minX[py] = px;
            if (px > maxX[py]) maxX[py] = px;
        }

        const int EndSlice = 3; // 끝 폭은 바깥쪽 이 개수 열/행의 평균으로(노이즈 완화)
        double SpanAvg(int[] lo, int[] hi, int from, int to)
        {
            double sum = 0; int n = 0;
            for (int i = Math.Max(0, from); i < Math.Min(lo.Length, to); i++)
            {
                if (lo[i] == int.MaxValue) continue; // 빈 열/행
                sum += hi[i] - lo[i] + 1; n++;
            }
            return n == 0 ? 0 : sum / n;
        }
        double leftEnd = SpanAvg(minY, maxY, 0, EndSlice);
        double rightEnd = SpanAvg(minY, maxY, bw - EndSlice, bw);
        double topEnd = SpanAvg(minX, maxX, 0, EndSlice);
        double bottomEnd = SpanAvg(minX, maxX, bh - EndSlice, bh);

        double lScore = (rightEnd - leftEnd) / bh; // 양수 = 왼쪽 끝이 좁다 → 왼쪽이 머리
        double uScore = (bottomEnd - topEnd) / bw; // 양수 = 위쪽 끝이 좁다 → 위쪽이 머리
        char dir = Math.Abs(lScore) >= Math.Abs(uScore)
            ? (lScore >= 0 ? 'L' : 'R')
            : (uScore >= 0 ? 'U' : 'D');

        // 그라데이션 보정 — 분홍 꼬리→파랑 머리 그라데이션 화살표는 파랑 우세 머리끝이 웜톤
        // 마스크에서 잘려 '넓은 끝=꼬리' 판정이 정반대로 뒤집힌다(2026-08-04 위아래위위 룬:
        // 보라 ↓·↑를 U·D로 반전 판독). 양끝 (b−r) 색차가 크고(≥80) 넓은 끝이 최대 폭에
        // 근접(삼각 밑변 절단 흔적)할 때만, 파랑 우세 끝을 머리로 채택한다. 완형 화살표는
        // 끝 폭 < 최대 폭이라 발동하지 않는다.
        if (frame is not null)
        {
            bool vertical = dir is 'U' or 'D';
            double EndBr(bool nearMin)
            {
                double sum = 0; int n = 0;
                int lo = vertical ? b.MinY : b.MinX, hi = vertical ? b.MaxY : b.MaxX;
                foreach (var p in b.Pixels)
                {
                    int px = p % w, py = p / w;
                    int t = vertical ? py : px;
                    if (nearMin ? t > lo + EndSlice - 1 : t < hi - EndSlice + 1) continue;
                    var c = frame.GetPixel(region.X + px, region.Y + py);
                    sum += c.B - c.R; n++;
                }
                return n == 0 ? 0 : sum / n;
            }
            double minEndBr = EndBr(true), maxEndBr = EndBr(false);
            if (Math.Abs(minEndBr - maxEndBr) >= 80)
            {
                char gDir = vertical ? (minEndBr > maxEndBr ? 'U' : 'D') : (minEndBr > maxEndBr ? 'L' : 'R');
                if (gDir != dir)
                {
                    double wideEnd = vertical ? Math.Max(topEnd, bottomEnd) : Math.Max(leftEnd, rightEnd);
                    double maxSpan = 0;
                    if (vertical) { for (int i = 0; i < bh; i++) if (minX[i] != int.MaxValue) maxSpan = Math.Max(maxSpan, maxX[i] - minX[i] + 1); }
                    else { for (int i = 0; i < bw; i++) if (minY[i] != int.MaxValue) maxSpan = Math.Max(maxSpan, maxY[i] - minY[i] + 1); }
                    // 절단면은 안티앨리어싱으로 듬성해져 평균 폭이 실제보다 낮게 재진다
                    // (2026-08-04 실측: 진성 절단면이 0.63·0.97, 완형 회전 글리프는 0.50) → 0.6
                    DiagLog?.Invoke($"    [그라데이션 보정] ({b.Cx:0},{b.Cy:0}) {dir}→{gDir}? br {minEndBr:0}/{maxEndBr:0} 넓은끝 {wideEnd:0.0} 최대폭 {maxSpan:0} → {(wideEnd >= 0.6 * maxSpan ? "적용" : "기각")}");
                    if (wideEnd >= 0.6 * maxSpan) dir = gDir;
                }
            }
        }
        return (dir, uScore, -uScore, lScore, -lScore);
    }
}
