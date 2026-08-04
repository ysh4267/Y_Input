using System.Drawing;
using System.Drawing.Imaging;

namespace YInput.Host.Vision;

/// <summary>퍼즐 화살표 하나 — 화면 좌표(중심)와 방향('L','R','U','D').</summary>
internal readonly record struct RuneArrow(PointF Center, char Dir);

/// <summary>한 프레임에서 분석한 화살표 하나 — 방향 + 모양 시그니처(회전 정지 판별용).</summary>
internal readonly record struct ArrowSample(PointF Center, char Dir, bool[] Sig);

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
internal static class RuneArrowDetector
{
    // 퍼즐 UI가 뜨는 탐색 범위(화면 비율) — 상단 중앙 넓게
    private const double RegionX0 = 0.08, RegionX1 = 0.92;
    private const double RegionY0 = 0.02, RegionY1 = 0.60;

    private const int DiffMin = 60;      // 발동 전후 차분(폴백) 임계
    private const int AnimDiffMin = 40;  // 시간차(애니메이션) 차분 임계 — 하이라이트는 변화가 은은할 수 있다
    private const int VividMax = 120;    // 채도 판정: 최대 채널 밝기 하한
    private const int VividSat = 45;     // 채도 판정: (최대-최소) 하한 — 반투명 합성으로 채도가 깎이므로 느슨하게
    private const int VividSatStrict = 80; // 교집합 경로용 고채도 — 필 너머로 비치는 불꽃 잔광(둔탁한 웜톤)은
                                           // 지우고 화살표(선명한 무지개)만 남긴다. 00:45 프레임 검증값
    private const int VividSatUltra = 140; // 로컬 글리프 분리용 초고채도 — 애니메이션 배경 장식(수정 반짝임,
                                           // sat 80~120대)까지 지우고 순수 무지개 글리프(sat 150+)만 남긴다.
                                           // 10:14 실행: sat80으로는 수정 홍수가 회전 글리프를 삼켰다
    private const double BannerWideFrac = 0.30; // 안내 배너 판정: 가로 이 비율 이상 변한 행
    private const int BannerMinRows = 6;        // 그런 행이 연속 이만큼 = 배너
    private const int MinThick = 4;      // 얇은 구조 제거: 가로·세로 연속 두께 하한 — 화살표 코어(반투명
                                         // 합성으로 작아짐)는 살리고 1~3px 외곽선·궤적만 지운다
    private const int MinPieceArea = 4;  // 블롭 조각 최소 픽셀 — 반투명 합성으로 화살표가 점묘처럼
                                         // 흩어지므로 작은 조각도 살려 병합 단계에서 모은다
    private const int MergePx = 12;      // 같은 화살표로 병합하는 조각 간 거리(넓으면 외곽선 조각이 붙는다)
    private const int MinArrowArea = 30, MaxArrowArea = 2500; // 하한은 반투명 합성으로 작아진 코어 기준
    private const int MinArrowBox = 8, MaxArrowBox = 70;
    private const int RowBandPx = 22;    // 같은 줄(4개 나열) 판정 Y 허용폭
    private const int MinGapPx = 50, MaxGapPx = 280; // 화살표 이웃 간격 상식 범위(실측 85~125px) —
                                                     // 데미지 숫자·배너 글자 조각(13~35px) 배제

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

    /// <summary>행별 픽셀 차이 개수(|ΔR|+|ΔG|+|ΔB| ≥ min), 행 평균 밝기, 행 밝기 표준편차(a·b 각각).
    /// 표준편차는 배너 판정용 — 어두운 반투명 띠가 덮이면 행의 명암 대비가 눌려 표준편차가 떨어진다.</summary>
    private static (int[] Diff, double[] LumA, double[] LumB, double[] StdA, double[] StdB) RowStats(Bitmap a, Bitmap b, Rectangle region, int min)
    {
        int w = region.Width, h = region.Height;
        var counts = new int[h];
        var lumA = new double[h];
        var lumB = new double[h];
        var stdA = new double[h];
        var stdB = new double[h];
        var da = a.LockBits(region, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var db = b.LockBits(region, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            unsafe
            {
                for (int y = 0; y < h; y++)
                {
                    byte* ra = (byte*)da.Scan0 + y * da.Stride;
                    byte* rb = (byte*)db.Scan0 + y * db.Stride;
                    int c = 0; long sa = 0, sb2 = 0, qa = 0, qb = 0;
                    for (int x = 0; x < w; x++)
                    {
                        int i4 = x * 4;
                        int d = Math.Abs(ra[i4] - rb[i4]) + Math.Abs(ra[i4 + 1] - rb[i4 + 1]) + Math.Abs(ra[i4 + 2] - rb[i4 + 2]);
                        if (d >= min) c++;
                        int la = ra[i4] + ra[i4 + 1] + ra[i4 + 2];
                        int lb2 = rb[i4] + rb[i4 + 1] + rb[i4 + 2];
                        sa += la; sb2 += lb2;
                        qa += (long)la * la; qb += (long)lb2 * lb2;
                    }
                    counts[y] = c;
                    double ma = sa / (double)w, mb = sb2 / (double)w;
                    lumA[y] = ma / 3.0;
                    lumB[y] = mb / 3.0;
                    stdA[y] = Math.Sqrt(Math.Max(0, qa / (double)w - ma * ma)) / 3.0;
                    stdB[y] = Math.Sqrt(Math.Max(0, qb / (double)w - mb * mb)) / 3.0;
                }
            }
        }
        finally { a.UnlockBits(da); b.UnlockBits(db); }
        return (counts, lumA, lumB, stdA, stdB);
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
            result.Add(new ArrowSample(new PointF((float)(region.X + b.Cx), (float)(region.Y + b.Cy)), dir, Signature(b, w)));
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
        var (bandY0, bandY1, _) = BannerBand(w, h, (int)(h / (RegionY1 - RegionY0)));
        double rowY0 = bandY0 + 0.45 * (bandY1 - bandY0), rowY1 = bandY0 + 0.80 * (bandY1 - bandY0);
        var cands = MergeNear(FindBlobs(mask, w, h)).Where(b =>
            b.Area is >= MinArrowArea and <= MaxArrowArea &&
            b.W is >= MinArrowBox and <= MaxArrowBox && b.H is >= MinArrowBox and <= MaxArrowBox &&
            b.Cy >= rowY0 && b.Cy <= rowY1).OrderBy(b => b.Cx).ToList();
        double gLo = frame.Width * 0.055, gHi = frame.Width * 0.135;
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
        double bg1 = best[1].Cx - best[0].Cx, bg2 = best[2].Cx - best[1].Cx;
        double yAvg = best.Average(x => x.Cy);
        var xs = new List<double> { best[0].Cx, best[1].Cx, best[2].Cx };
        if (bg1 > gHi) xs.Insert(1, best[0].Cx + bg1 / 2);          // 내부 누락(왼쪽 간격이 2슬롯)
        else if (bg2 > gHi) xs.Insert(2, best[1].Cx + bg2 / 2);     // 내부 누락(오른쪽 간격이 2슬롯)
        else
        {
            // 끝 슬롯 누락 — 좌/우 외삽 중 로컬 글리프가 실제로 잡히는 쪽을 채택
            double gapAvg = (bg1 + bg2) / 2;
            double leftX = best[0].Cx - gapAvg, rightX = best[2].Cx + gapAvg;
            bool leftOk = leftX - 20 >= 0, rightOk = rightX + 20 < w;
            bool leftHit = false, rightHit = false;
            if (leftOk)
                leftHit = AnalyzeArrowAt(frame, beforeRef, null,
                    new Rectangle(region.X + (int)leftX - 32, region.Y + (int)yAvg - 32, 64, 64)) is not null;
            if (rightOk)
                rightHit = AnalyzeArrowAt(frame, beforeRef, null,
                    new Rectangle(region.X + (int)rightX - 32, region.Y + (int)yAvg - 32, 64, 64)) is not null;
            if (leftHit || (leftOk && !rightHit)) xs.Insert(0, leftX);
            else if (rightOk) xs.Add(rightX);
            else return null;
        }
        return xs.Select(x => new PointF((float)(region.X + x), (float)(region.Y + yAvg))).ToArray();
    }

    /// <summary>진단 CLI에서만 설정 — DetectRow가 밴드·후보·선택 결과를 이 콜백으로 알린다.</summary>
    internal static Action<string>? DiagLog;

    /// <summary>진단 CLI에서만 설정 — AnalyzeArrowAt이 로컬 마스크 PNG를 이 폴더에 덤프한다.</summary>
    internal static string? DiagMaskDir;

    /// <summary>마스크에서 '화살표 줄' 블롭 4개 선택(왼쪽부터). fullArea = 밴드·중심 제약 없이
    /// 전 영역 탐색(진단 전용 — 밴드 기하가 틀리는 창 크기를 확인할 때). 실패 시 null.</summary>
    private static List<Blob>? DetectRow(Bitmap frame, Bitmap? bannerRef, bool[] mask, Rectangle region, int w, int h, bool thinFilter, int fullFrameH, bool fullArea = false)
    {
        if (thinFilter) ThinFilter(mask, w, h);
        var (bandY0, bandY1, bannerCx) = fullArea ? (0, h - 1, -1.0) : BannerBand(w, h, fullFrameH);

        // 화살표 줄은 밴드 세로 45~80% 구간에 고정(실측: 두 맵·세 룬 모두 62% 부근, 스트립 55~70%).
        // 밴드 상단 가장자리 잡블롭 줄(y 0~18% — 배너 하단 반짝이)이 등간격으로 뭉치면
        // ① 경로가 그 줄을 먼저 반환해 이긴다(2026-08-04 14:13 룬: y197~214 잡줄 → 2번 누락).
        double rowY0 = bandY0 + 0.45 * (bandY1 - bandY0), rowY1 = bandY0 + 0.80 * (bandY1 - bandY0);
        var cands = MergeNear(FindBlobs(mask, w, h)).Where(b =>
            b.Area is >= MinArrowArea and <= MaxArrowArea &&
            b.W is >= MinArrowBox and <= MaxArrowBox && b.H is >= MinArrowBox and <= MaxArrowBox &&
            b.Cy >= rowY0 && b.Cy <= rowY1).ToList();
        var row = PickRow(cands, bannerCx, frame.Width);
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

    /// <summary>후보들 중 '화살표 줄' 4개 선택 — 같은 높이(±RowBandPx), 이웃 간격 35~280px,
    /// 크기 유사(최대/최소 면적 ≤5배), 배너 중심 정렬(±12% 폭). 조건을 만족하는 조합 중
    /// '면적 합 − 박스 불균일 벌점 − 중심 이탈'이 최대인 것. 화살표 4개는 글리프 크기가 같아
    /// 바운딩 박스가 거의 동일(22~30px)하고, 불꽃 잔광·이펙트 블롭은 박스가 튄다(34x28, 49x57 —
    /// 01:16·00:45 실행에서 면적만으로는 정크가 진짜 화살표를 밀어냈다). 모양 비대칭은 판별자로
    /// 못 쓴다 — 진짜 ←도 침식되면 |0.06|까지 떨어진다(00:45 sat80). 없으면 null.</summary>
    private static List<Blob>? PickRow(List<Blob> cands, double bannerCx, int frameW)
    {
        if (cands.Count < 4) return null;
        // 상위 20개 — 불타는 맵은 노이즈 블롭이 커서 14개 컷으로는 작은 화살표가 밀려났다
        var top = cands.OrderByDescending(b => b.Area).Take(20).OrderBy(b => b.Cx).ToList();
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
                        // 간격은 창폭 비례 절대 범위(1076px 창 실측 85~125 → 0.055~0.135W) + 균일성(비 ≤1.5).
                        // 개별 50~280 검사만으로는 잡줄이 통과했다(2026-08-04 14:04 실행: x173 잡블롭 줄
                        // 간격 226/88/191 → 3/4 실패·오답 입력, 리플레이에선 186~247 '균일' 잡줄까지 통과).
                        double gLo = frameW * 0.055, gHi = frameW * 0.135;
                        for (int i = 1; i < 4; i++)
                        {
                            double gap = combo[i].Cx - combo[i - 1].Cx;
                            if (gap < gLo || gap > gHi) { gapsOk = false; break; }
                            gMin = Math.Min(gMin, gap); gMax = Math.Max(gMax, gap);
                        }
                        // 균일비 1.6 — 고전 정지형 룬은 간격이 78~115로 불균일하다(2026-08-04 14:46
                        // 보라맵: 115/78/109 비 1.47이 1.5 게이트에 턱걸이). 잡줄 방어는 나머지
                        // 게이트(절대 범위·중앙·y구간·크기 균일)가 겹으로 담당.
                        if (!gapsOk || gMax > gMin * 1.6) continue;
                        int aMin = combo.Min(x => x.Area), aMax = combo.Max(x => x.Area);
                        if (aMax > aMin * 5) continue; // 크기가 제각각인 묶음은 화살표 줄이 아니다
                        double avgX = combo.Average(x => x.Cx);
                        double centerPenalty = 0;
                        if (bannerCx >= 0)
                        {
                            double off = Math.Abs(avgX - bannerCx);
                            // 화살표 줄은 필 중앙에 정확히 대칭(실측 이탈 ≤5px). 한 칸 밀린 줄(화살표
                            // 하나가 에지온으로 빠지고 잡블롭이 반대쪽에 끼는 조합)은 중심이 간격만큼
                            // (~90px) 이탈한다 — 0.12W(129px)로는 통과했다(2026-08-04 14:04 룬).
                            if (off > frameW * 0.05) continue;
                            centerPenalty = off * 2;
                        }
                        double wRatio = combo.Max(x => x.W) / (double)combo.Min(x => x.W);
                        double hRatio = combo.Max(x => x.H) / (double)combo.Min(x => x.H);
                        double boxPenalty = 300 * (wRatio - 1 + hRatio - 1);
                        double score = combo.Sum(x => (double)x.Area) - centerPenalty - boxPenalty;
                        if (score > bestScore) { bestScore = score; best = combo.ToList(); }
                    }
        return best;
    }

    /// <summary>밝고 채도 높은 픽셀 마스크. requireWarm = 웜톤/초록만 허용(파랑·청록 우세 배제).
    /// <b>화살표 색은 완전 랜덤이라(사용자 확인) 화살표 탐색 경로는 전부 requireWarm=false</b> —
    /// 파란 배경 클러터는 '발동 전과 다름'(diffBefore)·밴드·줄 조합 제약이 걸러낸다.
    /// requireWarm=true는 색이 고정인 배너 안내 텍스트 신호(PuzzlePresent)에만 쓴다.</summary>
    private static bool[] VividMask(Bitmap frame, Rectangle region, int w, int h, int satMin = VividSat, bool requireWarm = true, int maxMin = VividMax)
    {
        var mask = new bool[w * h];
        var data = frame.LockBits(region, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            unsafe
            {
                for (int y = 0; y < h; y++)
                {
                    byte* row = (byte*)data.Scan0 + y * data.Stride;
                    int o = y * w;
                    for (int x = 0; x < w; x++)
                    {
                        byte b = row[x * 4], g = row[x * 4 + 1], r = row[x * 4 + 2];
                        int max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
                        // 웜톤(노랑·빨강) 외에 보라·자홍(r가 g보다 우세)도 화살표 색 — 정지 상태의
                        // 보라 화살표가 여기서 걸러져 4개 줄 구성이 실패했다(2026-08-04 위아래위위 룬).
                        // 하늘·파랑 배경은 g가 높아 r>=g+30을 통과하지 못한다. 파랑 우세인 머리끝은
                        // 여기선 여전히 잘리지만(초고채도 일괄 통과는 반짝이·UI 파랑까지 흡수해 블롭
                        // 크기 초과 탈락 유발 — 실측 확인) 줄 선택 후 GrowGlyph가 복원한다.
                        mask[o + x] = max >= maxMin && max - min >= satMin
                                      && (!requireWarm || r >= b + 30 || g >= b + 30 || r >= g + 30);
                    }
                }
            }
        }
        finally { frame.UnlockBits(data); }
        return mask;
    }

    /// <summary>두 프레임의 픽셀 차이(|ΔR|+|ΔG|+|ΔB| ≥ min)를 changed에 OR 누적.</summary>
    private static void AccumulateDiff(Bitmap a, Bitmap b, Rectangle region, int w, int h, int min, bool[] changed)
    {
        var da = a.LockBits(region, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var db = b.LockBits(region, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            unsafe
            {
                for (int y = 0; y < h; y++)
                {
                    byte* ra = (byte*)da.Scan0 + y * da.Stride;
                    byte* rb = (byte*)db.Scan0 + y * db.Stride;
                    int o = y * w;
                    for (int x = 0; x < w; x++)
                    {
                        if (changed[o + x]) continue;
                        int i4 = x * 4;
                        int d = Math.Abs(ra[i4] - rb[i4]) + Math.Abs(ra[i4 + 1] - rb[i4 + 1]) + Math.Abs(ra[i4 + 2] - rb[i4 + 2]);
                        if (d >= min) changed[o + x] = true;
                    }
                }
            }
        }
        finally { a.UnlockBits(da); b.UnlockBits(db); }
    }

    /// <summary>얇은 구조 제거 — 가로·세로 연속 길이가 모두 MinThick 이상인 픽셀만 유지.
    /// 바 외곽선·글로우 다리·눈송이 궤적은 한 방향이 얇아 지워지고, 화살표 본체는 보존된다.</summary>
    private static void ThinFilter(bool[] mask, int w, int h)
    {
        var hRun = new short[w * h];
        var vRun = new short[w * h];
        for (int y = 0; y < h; y++)
        {
            int run = 0;
            for (int x = 0; x < w; x++) { int i = y * w + x; run = mask[i] ? run + 1 : 0; hRun[i] = (short)run; }
            run = 0;
            for (int x = w - 1; x >= 0; x--)
            {
                int i = y * w + x;
                if (mask[i]) { run = Math.Max(run, hRun[i]); hRun[i] = (short)run; } else run = 0;
            }
        }
        for (int x = 0; x < w; x++)
        {
            int run = 0;
            for (int y = 0; y < h; y++) { int i = y * w + x; run = mask[i] ? run + 1 : 0; vRun[i] = (short)run; }
            run = 0;
            for (int y = h - 1; y >= 0; y--)
            {
                int i = y * w + x;
                if (mask[i]) { run = Math.Max(run, vRun[i]); vRun[i] = (short)run; } else run = 0;
            }
        }
        for (int i = 0; i < mask.Length; i++)
            mask[i] = mask[i] && hRun[i] >= MinThick && vRun[i] >= MinThick;
    }

    /// <summary>화살표 탐색 밴드 — <b>창 기준 고정 기하</b>. 룬 퍼즐 UI는 창 기준 고정 위치다
    /// (실측 두 맵 동일: 배너 띠 상단 ≈ 창높이 20%, 안내 텍스트 ≈ 23~27%, 화살표 줄 ≈ 29.5~33%).
    /// 발동 전 차분·어두워짐으로 배너를 '탐지'하는 방식은 불타는 맵에서 세 번 다르게 실패했다
    /// (첫 행 오검출 → 불꽃 노이즈 / 피크 앵커 → 불꽃이 사라진 자리 어두워짐이 배너보다 큼) —
    /// 배경과 무관한 고정 밴드가 유일하게 안정적이다. 텍스트 글리프(≤27%)는 밴드 밖.
    /// CenterX = 탐색 영역 가로 중앙(퍼즐 UI는 창 중앙 정렬 — 우측 팝업 줄 배제용).</summary>
    private const double ArrowBandTopFrac = 0.28, ArrowBandBotFrac = 0.42;

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

    /// <summary>진단 CLI(--rune-analyze) — 저장된 퍼즐 스크린샷으로 인식 과정을 재현해
    /// 첫 파일 경로 + ".analysis.txt"로 남긴다. 파일 1개 = 채도 마스크만(크롭 검증),
    /// 여러 개(rune-frame-N 연속 캡처) = 실전과 같은 애니메이션 차분 합집합.
    /// 스트립 모드에서 "pos=x,y;x,y;x,y;x,y" 인자를 주면 위치 획득을 건너뛰고 그 좌표를
    /// 관찰한다(프레임 절대 y는 밴드 기준으로 자동 변환) — 반동 감지 단독 검증용.</summary>
    public static void AnalyzeToFile(params string[] pngPaths)
    {
        // 스트립(rune-strip-NN) 입력이면 회전 반동 파형 재현 모드로 — 각 스트립의 화살표별
        // 로컬 방향·각도 시계열과, 실전 반동 감지가 각 표본에서 내렸을 판정을 그대로 찍는다.
        if (pngPaths.Any(p => Path.GetFileName(p).Contains("rune-strip", StringComparison.OrdinalIgnoreCase)))
        {
            AnalyzeStripsToFile(pngPaths);
            return;
        }
        var sb = new System.Text.StringBuilder();
        var frames = new List<Bitmap>();
        Bitmap? beforeRef = null;
        try
        {
            // 'rune-before'가 포함된 파일은 발동 전 기준 프레임으로 사용(실전 경로 재현)
            foreach (var p in pngPaths)
            {
                if (Path.GetFileName(p).Contains("rune-before", StringComparison.OrdinalIgnoreCase))
                    beforeRef = new Bitmap(p);
                else frames.Add(new Bitmap(p));
            }
            // 크기가 다른 프레임(다른 실행의 잔재)은 차분에 못 쓴다 — 첫 프레임 크기 기준으로 필터
            int skipped = frames.RemoveAll(f => f.Width != frames[0].Width || f.Height != frames[0].Height);
            if (skipped > 0) sb.AppendLine($"크기 불일치 프레임 {skipped}개 제외");
            var frame = frames[^1];
            // 작은 이미지(퍼즐 영역 크롭·화살표 크롭)는 전체를 분석, 전체 창 캡처는 비율 영역 적용
            var region = frame.Width < 700 || frame.Height < 500
                ? new Rectangle(0, 0, frame.Width, frame.Height)
                : default;
            if (region == default && !TryRegion(frame, false, out region)) { sb.AppendLine("영역 계산 실패"); }
            else
            {
                int w = region.Width, h = region.Height;
                var mask = VividMask(frame, region, w, h);
                if (frames.Count >= 2)
                {
                    var changed = new bool[w * h];
                    for (int k = 1; k < frames.Count; k++)
                        AccumulateDiff(frames[k - 1], frames[k], region, w, h, AnimDiffMin, changed);
                    for (int i = 0; i < mask.Length; i++) mask[i] &= changed[i];
                }
                SaveMaskPng(mask, w, h, pngPaths[0] + ".mask-pre.png");
                if (frames.Count == 1) ThinFilter(mask, w, h); // 실전과 동일: 애니메이션 경로는 두께 필터 없음
                SaveMaskPng(mask, w, h, pngPaths[0] + ".mask-post.png");
                sb.AppendLine($"frame {frame.Width}x{frame.Height} region {region} 입력 {frames.Count}프레임"
                              + (frames.Count >= 2 ? " (애니메이션 차분 합집합)" : " (채도 마스크만)"));

                var merged = MergeNear(FindBlobs(mask, w, h));
                sb.AppendLine($"병합 블롭 {merged.Count}개 (면적순 상위 30, 좌표는 프레임 절대):");
                foreach (var b in merged.OrderByDescending(x => x.Area).Take(30))
                {
                    bool sizeOk = b.Area is >= MinArrowArea and <= MaxArrowArea
                                  && b.W is >= MinArrowBox and <= MaxArrowBox && b.H is >= MinArrowBox and <= MaxArrowBox;
                    sb.AppendLine($"  ({region.X + b.Cx:0},{region.Y + b.Cy:0}) a{b.Area} {b.W}x{b.H}{(sizeOk ? " [후보]" : "")}");
                }

                var cands = merged.Where(b =>
                    b.Area is >= MinArrowArea and <= MaxArrowArea &&
                    b.W is >= MinArrowBox and <= MaxArrowBox && b.H is >= MinArrowBox and <= MaxArrowBox).ToList();
                sb.AppendLine($"후보 {cands.Count}개");
                var row = PickRow(cands, -1, frame.Width);
                if (row is null) sb.AppendLine("선택된 줄 없음(조건 만족 조합 없음)");
                else
                {
                    sb.AppendLine("선택된 줄:");
                    foreach (var b in row)
                    {
                        var (dir, up, down, left, right) = ClassifyScores(b, w);
                        sb.AppendLine($"  ({region.X + b.Cx:0},{region.Y + b.Cy:0}) a{b.Area} {b.W}x{b.H} → {dir}  U{up:0.000} D{down:0.000} L{left:0.000} R{right:0.000}");
                    }
                }

                // 발동 전 프레임이 있으면 실전 3경로(애니메이션/발동 전 차분/채도 단독+배너)를 그대로 재현
                if (beforeRef is not null)
                {
                    bool pre = frame.Width < 700 || frame.Height < 500;
                    // 행별 배너 신호 프로파일 — '넓게 변한' 행 연속 구간과 어두워짐 정도
                    if (beforeRef.Width == frame.Width && beforeRef.Height == frame.Height)
                    {
                        var (dRows, lb, ln, sdBefore, sdNow) = RowStats(beforeRef, frame, region, DiffMin);
                        int wideThr = (int)(w * BannerWideFrac);
                        sb.AppendLine($"— 행별 배너 신호 (폭변화 ≥{wideThr}px + 어두워짐 ≥8 구간, 평탄화=표준편차 전→후) —");
                        for (int y = 0; y < h;)
                        {
                            if (dRows[y] < wideThr || lb[y] - ln[y] < 8) { y++; continue; }
                            int y0 = y; double dkSum = 0, dkMax = 0, sdbSum = 0, sdnSum = 0;
                            while (y < h && dRows[y] >= wideThr && lb[y] - ln[y] >= 8)
                            {
                                double dk = lb[y] - ln[y];
                                dkSum += dk; dkMax = Math.Max(dkMax, dk);
                                sdbSum += sdBefore[y]; sdnSum += sdNow[y]; y++;
                            }
                            int n2 = y - y0;
                            sb.AppendLine($"  y {region.Y + y0}..{region.Y + y - 1} ({n2}행) 어두워짐 평균 {dkSum / n2:0.0} 최대 {dkMax:0.0} 표준편차 {sdbSum / n2:0.0}→{sdnSum / n2:0.0}");
                        }
                    }
                    string Dirs(List<RuneArrow>? a) => a is null ? "실패"
                        : string.Join(" ", a.Select(x => x.Dir switch { 'L' => '←', 'R' => '→', 'U' => '↑', _ => '↓' }));
                    // ②경로 마스크(채도+발동 전 차분, 두께 필터 후)도 덤프 — 블롭 오염 원인 확인용
                    {
                        var m2 = VividMask(frame, region, w, h);
                        var ch2 = new bool[w * h];
                        AccumulateDiff(beforeRef, frame, region, w, h, DiffMin, ch2);
                        for (int i = 0; i < m2.Length; i++) m2[i] &= ch2[i];
                        ThinFilter(m2, w, h);
                        SaveMaskPng(m2, w, h, pngPaths[0] + ".mask-diffbefore.png");
                    }
                    if (frames.Count >= 2)
                        sb.AppendLine($"배너 텍스트 신호 = {PresentPixelCount(frames[^2], frames[^1], beforeRef, pre)}px (임계 {PresentMinPx} — PuzzlePresent)");
                    var band = BannerBand(w, h, FullFrameH(frame, pre));
                    sb.AppendLine($"— 실전 경로 재현 (배너 밴드 y {region.Y + band.Y0}..{region.Y + band.Y1}, 중심X {(band.CenterX >= 0 ? (region.X + band.CenterX).ToString("0") : "미탐지")}) —");
                    DiagLog = s => sb.AppendLine($"      [{s}]");
                    try
                    {
                        sb.AppendLine($"  ① 애니메이션 차분: {(frames.Count >= 2 ? Dirs(FindArrowsAnimated(frames, beforeRef, pre)) : "프레임 부족")}");
                        sb.AppendLine($"  ② 발동 전 차분:   {Dirs(FindArrows(frame, beforeRef, beforeRef, pre))}");
                        sb.AppendLine($"  ③ 채도 단독:      {Dirs(FindArrows(frame, null, beforeRef, pre))}");
                        var af = AnalyzeFrame(frame, frames.GetRange(0, frames.Count - 1), beforeRef, pre);
                        sb.AppendLine($"  ④ 프레임 분석(교집합→정지 게이트, 실전 경로): {(af is null ? "실패" : string.Join(" ", af.Select(x => x.Dir switch { 'L' => '←', 'R' => '→', 'U' => '↑', _ => '↓' })))}");
                        var pr = TryPartialRow(frame, beforeRef, pre);
                        sb.AppendLine($"  ⑦ 부분 줄 보완(3개+외삽): {(pr is null ? "실패" : string.Join(" ", pr.Select(p => $"({p.X:0},{p.Y:0})")))}");
                        // ⑤ 실험: 채도 교집합 — 모든 프레임에서 '계속 채도 높음'(정적 UI) && 발동 전과 다름.
                        //    흔들리는 불꽃은 프레임마다 위치가 바뀌어 교집합에서 탈락한다.
                        if (frames.Count >= 2)
                        {
                            foreach (int sat in new[] { VividSat, 80 })
                            {
                                var mi = VividMask(frames[0], region, w, h, sat, requireWarm: false);
                                for (int k = 1; k < frames.Count; k++)
                                {
                                    var vk = VividMask(frames[k], region, w, h, sat, requireWarm: false);
                                    for (int i = 0; i < mi.Length; i++) mi[i] &= vk[i];
                                }
                                var chI = new bool[w * h];
                                AccumulateDiff(beforeRef, frame, region, w, h, DiffMin, chI);
                                for (int i = 0; i < mi.Length; i++) mi[i] &= chI[i];
                                ThinFilter(mi, w, h);
                                SaveMaskPng(mi, w, h, pngPaths[0] + $".mask-vivid-and-{sat}.png");
                                var r5 = DetectRow(frame, beforeRef, mi, region, w, h, thinFilter: false, FullFrameH(frame, pre));
                                sb.AppendLine($"  ⑤ 채도 교집합(sat{sat}): {(r5 is null ? "실패" : string.Join(" ", r5.Select(b => ClassifyScores(b, w).Dir switch { 'L' => '←', 'R' => '→', 'U' => '↑', _ => '↓' })))}");
                                // ⑥ 같은 마스크를 배너 밴드 없이 전 영역에서 — 밴드 기하가 틀리는 창 크기 대비
                                var r6 = DetectRow(frame, beforeRef, mi, region, w, h, thinFilter: false, FullFrameH(frame, pre), fullArea: true);
                                sb.AppendLine($"  ⑥ 교집합 전영역(sat{sat}): {(r6 is null ? "실패" : string.Join(" ", r6.Select(b => ClassifyScores(b, w).Dir switch { 'L' => '←', 'R' => '→', 'U' => '↑', _ => '↓' })))}");
                            }
                            // ⑦ ④의 줄 위치를 기준으로 프레임별 로컬 방향·각도 — 회전 변형(반동 추적) 재현
                            if (af is null) sb.AppendLine("  ⑦ 위치 없음(④ 줄 실패)");
                            else
                            {
                                sb.AppendLine("  ⑦ 위치: " + string.Join(" ", af.Select(p => $"({p.Center.X:0},{p.Center.Y:0})")));
                                const int box7 = 64;
                                for (int fi = 0; fi < frames.Count; fi++)
                                {
                                    var parts = new List<string>();
                                    foreach (var p in af)
                                    {
                                        var rect = new Rectangle((int)(p.Center.X - box7 / 2.0), (int)(p.Center.Y - box7 / 2.0), box7, box7);
                                        var la = AnalyzeArrowAt(frames[fi], beforeRef, fi > 0 ? frames[fi - 1] : null, rect);
                                        parts.Add(la is { } a
                                            ? $"{(a.MovingPx >= 40 ? "회" : "정")}{(a.Dir switch { 'L' => '←', 'R' => '→', 'U' => '↑', _ => '↓' })}{a.AngleDeg:000}°a{a.Area}"
                                            : "×");
                                    }
                                    sb.AppendLine($"     f{fi}: {string.Join("  ", parts)}");
                                }
                            }
                        }
                    }
                    finally { DiagLog = null; }
                }
            }
        }
        catch (Exception ex) { sb.AppendLine("오류: " + ex); }
        finally { foreach (var f in frames) f.Dispose(); }
        File.WriteAllText(pngPaths[0] + ".analysis.txt", sb.ToString());
    }

    /// <summary>스트립 녹화(밴드 크롭, ~50ms 간격) 재현 — 위치를 잡고 스트립마다 화살표별
    /// 로컬 방향·각도를 찍으며, 실전 반동 감지(PositionWatcher.TryDetectRecoil)를 그대로 돌려
    /// 어느 표본에서 락이 걸렸을지 재현한다. 출력: 첫 스트립 경로 + ".analysis.txt".</summary>
    private static void AnalyzeStripsToFile(string[] pngPaths)
    {
        var sb = new System.Text.StringBuilder();
        Bitmap? before = null, beforeStrip = null;
        var strips = new List<(string Name, Bitmap Bmp)>();
        PointF[]? fixedPos = null; // "pos=x,y;…" 인자 — 위치 획득 생략, 반동 감지 단독 검증
        try
        {
            foreach (var p in pngPaths)
            {
                if (p.StartsWith("pos=", StringComparison.OrdinalIgnoreCase))
                {
                    fixedPos = p[4..].Split(';', StringSplitOptions.RemoveEmptyEntries)
                        .Select(t => t.Split(','))
                        .Select(a => new PointF(float.Parse(a[0]), float.Parse(a[1]))).ToArray();
                    continue;
                }
                if (Path.GetFileName(p).Contains("rune-before", StringComparison.OrdinalIgnoreCase)) before = new Bitmap(p);
                else strips.Add((Path.GetFileName(p), new Bitmap(p)));
            }
            if (before is null) { sb.AppendLine("rune-before가 필요합니다(밴드 기준 크롭용)"); return; }
            var bandRect = ArrowBandRect(before.Width, before.Height);
            beforeStrip = before.Clone(bandRect, before.PixelFormat);
            int skipped = strips.RemoveAll(s => s.Bmp.Width != beforeStrip.Width || s.Bmp.Height != beforeStrip.Height);
            if (skipped > 0) sb.AppendLine($"크기 불일치 스트립 {skipped}장 제외");
            if (strips.Count == 0) { sb.AppendLine("스트립 없음"); return; }
            int w = beforeStrip.Width, h = beforeStrip.Height;
            var region = new Rectangle(0, 0, w, h);
            sb.AppendLine($"스트립 {strips.Count}장 ({w}x{h}), 기준 밴드 {bandRect}");

            // 위치 획득 — 기준 후보를 차례로 시도: ① rune-before의 밴드 크롭, ② 첫 스트립
            // (녹화 초반은 퍼즐이 뜨기 전이라 같은 카메라의 깨끗한 기준이 된다 — rune-before는
            // 시도 사이 카메라 밀림으로 낡을 수 있다). 차분 없이 채도 단독은 배경 장식(수정 등)이
            // 홍수를 일으켜 회전 화살표를 삼키므로 쓰지 않는다.
            PointF[]? pos = null;
            Bitmap activeRef = beforeStrip;
            if (fixedPos is { Length: 4 })
            {
                // 프레임 절대 y(밴드 높이 이상)는 밴드 기준으로 변환
                pos = fixedPos.Select(p => new PointF(p.X - bandRect.X,
                    p.Y >= h ? p.Y - bandRect.Y : p.Y)).ToArray();
                sb.AppendLine("위치(pos= 지정): " + string.Join(" ", pos.Select(p => $"({p.X:0},{p.Y:0})")));
            }
            foreach (var (refName, refBmp) in new (string, Bitmap)[] { ("before", beforeStrip), ("strip0", strips[0].Bmp) })
            {
                for (int si = 0; si < strips.Count && pos is null; si++)
                {
                    var (name, s) = strips[si];
                    if (ReferenceEquals(s, refBmp)) continue;
                    // 고채도(80) ∧ 기준 차분, 두께 필터 없음 — 회전형 외곽선 글리프를 살리면서
                    // 배경 장식(수정 등)은 차분이, 필 테두리 링은 박스 크기 제한이 걸러준다.
                    // 채도를 45로 낮추면 필 내부의 어두워진 하늘(sat≈70)까지 통과해 밴드 전체가
                    // 홍수가 된다(2026-08-04 마스크 덤프 확인) — 45 금지.
                    var mask = VividMask(s, region, w, h, VividSatStrict, requireWarm: false);
                    var fresh = new bool[w * h];
                    AccumulateDiff(refBmp, s, region, w, h, DiffMin, fresh);
                    for (int i = 0; i < mask.Length; i++) mask[i] &= fresh[i];
                    if (si == 45) SaveMaskPng((bool[])mask.Clone(), w, h, pngPaths[0] + $".mask-{refName}-s45.png");
                    DiagLog = si is < 2 or (>= 44 and < 47) ? line => sb.AppendLine($"  [{refName}/{name}] {line}") : null;
                    List<Blob>? row;
                    try { row = DetectRow(s, refBmp, mask, region, w, h, thinFilter: false, h, fullArea: true); }
                    finally { DiagLog = null; }
                    // 화살표 줄은 밴드 세로 중앙 부근 + 필 가로 중앙 대칭(실측 이탈 ≤5px, 0.05W) —
                    // 가장자리 잡블롭 줄·한 칸 밀린 줄(2026-08-04: x22~191 줄, x287 밀림 줄)은 기각
                    if (row is not null && (Math.Abs(row.Average(b => b.Cy) - h / 2.0) > h * 0.2
                                            || Math.Abs(row.Average(b => b.Cx) - w / 2.0) > w * 0.05))
                    {
                        sb.AppendLine($"  [{refName}/{name}] 줄 기각(중앙 이탈): "
                                      + string.Join(" ", row.Select(b => $"({b.Cx:0},{b.Cy:0})")));
                        row = null;
                    }
                    if (row is not null)
                    {
                        pos = row.Select(b => new PointF((float)b.Cx, (float)b.Cy)).ToArray();
                        activeRef = refBmp;
                        sb.AppendLine($"위치({refName}/{name}): " + string.Join(" ", pos.Select(p => $"({p.X:0},{p.Y:0})")));
                    }
                }
                if (pos is not null) break;
            }
            if (pos is null)
            {
                // 폴백: 부분 줄로 4슬롯 격자 추정 — 회전 글리프가 배경 홍수(수정 반짝임 등)에 묻혀
                // 줄 4개가 안 채워지는 맵 대비. 같은 y(±10)에서 간격 70~110의 이웃 쌍을 찾고,
                // 격자 중심이 밴드 중앙(≈필 중앙)에 가장 가까운 배치를 택한다. EMA가 잔차를 수렴시킨다.
                for (int si = 10; si < strips.Count && pos is null; si += 5)
                {
                    var (name, s) = strips[si];
                    var mask = VividMask(s, region, w, h, VividSatStrict, requireWarm: false);
                    var fresh = new bool[w * h];
                    AccumulateDiff(strips[0].Bmp, s, region, w, h, DiffMin, fresh);
                    for (int i = 0; i < mask.Length; i++) mask[i] &= fresh[i];
                    var cands = MergeNear(FindBlobs(mask, w, h))
                        .Where(x => x.Area is >= 100 and <= 1200 && x.W is >= 12 and <= 60 && x.H is >= 12 and <= 60)
                        .OrderBy(x => x.Cx).ToList();
                    for (int a1 = 0; a1 < cands.Count - 1 && pos is null; a1++)
                        for (int b1 = a1 + 1; b1 < cands.Count && pos is null; b1++)
                        {
                            double gap = cands[b1].Cx - cands[a1].Cx;
                            if (gap < 70 || gap > 110 || Math.Abs(cands[b1].Cy - cands[a1].Cy) > 10) continue;
                            double bestOff = double.MaxValue; PointF[]? bestLat = null;
                            for (int shift = 0; shift < 4; shift++)
                            {
                                double x0 = cands[a1].Cx - shift * gap;
                                if (x0 < 30 || x0 + 3 * gap > w - 30) continue;
                                double centerOff = Math.Abs(x0 + 1.5 * gap - w / 2.0);
                                if (centerOff < bestOff)
                                {
                                    bestOff = centerOff;
                                    bestLat = Enumerable.Range(0, 4)
                                        .Select(k => new PointF((float)(x0 + k * gap), (float)cands[a1].Cy)).ToArray();
                                }
                            }
                            if (bestLat is not null)
                            {
                                pos = bestLat;
                                activeRef = strips[0].Bmp;
                                sb.AppendLine($"위치(격자 추정 {name}, 쌍 ({cands[a1].Cx:0},{cands[a1].Cy:0})+({cands[b1].Cx:0},{cands[b1].Cy:0})): "
                                              + string.Join(" ", pos.Select(p => $"({p.X:0},{p.Y:0})")));
                            }
                        }
                }
            }
            if (pos is null) { sb.AppendLine("위치 획득 실패 — 어떤 스트립에서도 줄을 못 찾음"); return; }
            var posAnchor = (PointF[])pos.Clone(); // EMA 클램프 기준

            const int box = 64;
            var locked = new char?[4];
            int lockedCount = 0;
            var votes = new int[4, 4];
            var angleT = new List<double>[4]; var angleV = new List<double>[4];
            var lastRotAt = new int[4];
            for (int j = 0; j < 4; j++) { angleT[j] = new List<double>(); angleV[j] = new List<double>(); lastRotAt[j] = -999; }
            // 실전 LocalPass 미러: 시그니처 안정 가드(반짝임 정지 화살표의 가짜 반동 차단) + 정지 시그니처 락
            var sigHist = new List<bool[]>[4]; for (int j = 0; j < 4; j++) sigHist[j] = new List<bool[]>();
            bool SigStable(List<bool[]> hist) => hist.Count >= 3
                && SigSimilar(hist[^1], hist[^2]) && SigSimilar(hist[^2], hist[^3]);
            var lRunDir = new char[4]; var lRunLen = new int[4]; var lRunStart = new double[4]; var lRunSig = new bool[4][];
            for (int fi = 0; fi < strips.Count; fi++)
            {
                double t = fi * 50.0; // 명목 50ms 간격
                var parts = new List<string>();
                for (int j = 0; j < 4; j++)
                {
                    if (locked[j] is { } d0) { parts.Add($"[확정{d0}]"); continue; }
                    var rect = new Rectangle((int)(pos[j].X - box / 2.0), (int)(pos[j].Y - box / 2.0), box, box);
                    DiagLog = fi is 30 or 40 ? m => sb.AppendLine($"      [f{fi} 화살표{j + 1}] {m}") : null;
                    DiagMaskDir = fi == 30 && j == 1 ? Path.GetDirectoryName(pngPaths[0]) : null;
                    var la = AnalyzeArrowAt(strips[fi].Bmp, activeRef, fi > 0 ? strips[fi - 1].Bmp : null, rect);
                    DiagLog = null; DiagMaskDir = null;
                    if (la is not { } a) { parts.Add("×"); continue; }
                    if (a.Area >= 60)
                    {
                        // 실전 LocalPass 미러: EMA 자기보정을 앵커 ±22px로 클램프(이웃 화살표 미끄러짐 방지)
                        float cx = (float)(pos[j].X * 0.7 + a.Center.X * 0.3);
                        float cy = (float)(pos[j].Y * 0.7 + a.Center.Y * 0.3);
                        pos[j] = new PointF(
                            Math.Clamp(cx, posAnchor[j].X - 22, posAnchor[j].X + 22),
                            Math.Clamp(cy, posAnchor[j].Y - 22, posAnchor[j].Y + 22));
                    }
                    int before1 = lockedCount;
                    angleT[j].Add(t); angleV[j].Add(PositionWatcher.FixAngleFlip(angleV[j], a.AngleDeg));
                    sigHist[j].Add(a.Sig); if (sigHist[j].Count > 4) sigHist[j].RemoveAt(0);
                    int n1 = angleV[j].Count;
                    if (PositionWatcher.IsRotating(angleT[j], angleV[j])) lastRotAt[j] = n1;
                    bool rotActive = n1 - lastRotAt[j] <= 3 && !SigStable(sigHist[j]);
                    if (rotActive)
                        PositionWatcher.TryDetectRecoil(j, angleT[j], angleV[j], votes, ref lockedCount, locked,
                            m => sb.AppendLine($"      {m}")); // 투표 이벤트(직전각→착지각·피벗·방위)를 해당 스트립 아래 기록
                    else
                    {
                        // 정지 시그니처 런(실전 LocalPass 미러: 3연속 + 250ms)
                        if (lRunSig[j] is not null && lRunDir[j] == a.Dir && SigSimilar(lRunSig[j], a.Sig))
                        {
                            lRunLen[j]++;
                            if (lRunLen[j] >= 3 && t - lRunStart[j] >= 250) { locked[j] = lRunDir[j]; lockedCount++; }
                        }
                        else { lRunSig[j] = a.Sig; lRunDir[j] = a.Dir; lRunLen[j] = 1; lRunStart[j] = t; }
                    }
                    parts.Add($"{(rotActive ? "회" : "정")}{(a.Dir switch { 'L' => '←', 'R' => '→', 'U' => '↑', _ => '↓' })}{a.AngleDeg:000}°a{a.Area}m{a.MovingPx}{(lockedCount > before1 ? "★락" : "")}");
                }
                sb.AppendLine($"f{fi:00} {t,5:0}ms  {string.Join("  ", parts)}");
            }
            sb.AppendLine("반동 투표 [R U L D]:");
            for (int j = 0; j < 4; j++)
                sb.AppendLine($"  화살표{j + 1}: {votes[j, 0]} {votes[j, 1]} {votes[j, 2]} {votes[j, 3]}  확정 {(locked[j] is { } dd ? dd.ToString() : "-")}");
        }
        catch (Exception ex) { sb.AppendLine("오류: " + ex); }
        finally
        {
            before?.Dispose(); beforeStrip?.Dispose();
            foreach (var (_, b) in strips) b.Dispose();
            File.WriteAllText(pngPaths[0] + ".analysis.txt", sb.ToString());
        }
    }

    /// <summary>진단용 — 마스크를 흑백 PNG로 저장(어느 단계에서 화살표가 지워지는지 확인).</summary>
    private static void SaveMaskPng(bool[] mask, int w, int h, string path)
    {
        try
        {
            using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                unsafe
                {
                    for (int y = 0; y < h; y++)
                    {
                        byte* row = (byte*)data.Scan0 + y * data.Stride;
                        for (int x = 0; x < w; x++)
                        {
                            byte v = mask[y * w + x] ? (byte)255 : (byte)0;
                            row[x * 4] = v; row[x * 4 + 1] = v; row[x * 4 + 2] = v; row[x * 4 + 3] = 255;
                        }
                    }
                }
            }
            finally { bmp.UnlockBits(data); }
            bmp.Save(path, ImageFormat.Png);
        }
        catch { /* 진단 저장 실패 무시 */ }
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

    private sealed record Blob(double Cx, double Cy, int Area, int MinX, int MinY, int MaxX, int MaxY, List<int> Pixels)
    {
        public int W => MaxX - MinX + 1;
        public int H => MaxY - MinY + 1;
    }

    private static List<Blob> FindBlobs(bool[] mask, int w, int h)
    {
        var list = new List<Blob>();
        var seen = new bool[w * h];
        var stack = new Stack<int>();
        for (int i = 0; i < mask.Length; i++)
        {
            if (!mask[i] || seen[i]) continue;
            long sumX = 0, sumY = 0;
            int minX = w, maxX = -1, minY = h, maxY = -1;
            var px = new List<int>();
            stack.Push(i); seen[i] = true;
            while (stack.Count > 0)
            {
                int p = stack.Pop();
                int x = p % w, y = p / w;
                sumX += x; sumY += y; px.Add(p);
                if (x < minX) minX = x; if (x > maxX) maxX = x;
                if (y < minY) minY = y; if (y > maxY) maxY = y;
                if (x > 0 && mask[p - 1] && !seen[p - 1]) { seen[p - 1] = true; stack.Push(p - 1); }
                if (x < w - 1 && mask[p + 1] && !seen[p + 1]) { seen[p + 1] = true; stack.Push(p + 1); }
                if (y > 0 && mask[p - w] && !seen[p - w]) { seen[p - w] = true; stack.Push(p - w); }
                if (y < h - 1 && mask[p + w] && !seen[p + w]) { seen[p + w] = true; stack.Push(p + w); }
            }
            if (px.Count >= MinPieceArea)
                list.Add(new Blob((double)sumX / px.Count, (double)sumY / px.Count, px.Count, minX, minY, maxX, maxY, px));
        }
        return list;
    }

    /// <summary>가까운 조각 병합 — 그라데이션·외곽선 때문에 한 화살표가 여러 조각으로 나뉘는 것을 흡수.</summary>
    private static List<Blob> MergeNear(List<Blob> blobs)
    {
        var merged = new List<Blob>();
        var used = new bool[blobs.Count];
        for (int i = 0; i < blobs.Count; i++)
        {
            if (used[i]) continue;
            used[i] = true;
            var group = new List<Blob> { blobs[i] };
            bool grew = true;
            while (grew) // 체인 병합(a-b 가깝고 b-c 가까우면 a·b·c 모두 한 화살표)
            {
                grew = false;
                for (int j = 0; j < blobs.Count; j++)
                {
                    if (used[j]) continue;
                    foreach (var g in group)
                    {
                        double dx = blobs[j].Cx - g.Cx, dy = blobs[j].Cy - g.Cy;
                        if (dx * dx + dy * dy > MergePx * (double)MergePx) continue;
                        used[j] = true; group.Add(blobs[j]); grew = true; break;
                    }
                }
            }
            double cx = 0, cy = 0; int area = 0;
            int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
            var pixels = new List<int>();
            foreach (var g in group)
            {
                cx += g.Cx * g.Area; cy += g.Cy * g.Area; area += g.Area;
                minX = Math.Min(minX, g.MinX); minY = Math.Min(minY, g.MinY);
                maxX = Math.Max(maxX, g.MaxX); maxY = Math.Max(maxY, g.MaxY);
                pixels.AddRange(g.Pixels);
            }
            merged.Add(new Blob(cx / area, cy / area, area, minX, minY, maxX, maxY, pixels));
        }
        return merged;
    }
}
