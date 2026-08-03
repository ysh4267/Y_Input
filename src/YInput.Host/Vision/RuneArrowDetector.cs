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
        int count = 0;
        for (int i = 0; i < vivid.Length; i++)
            if (vivid[i] && fresh[i] && !moving[i]) count++;
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

        var vivid = VividMask(frame, region, w, h);
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
                var mi = VividMask(frame, region, w, h, sat);
                foreach (var p in prevs)
                {
                    var vp = VividMask(p, region, w, h, sat);
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
            var (dir, _, _, _, _) = ClassifyScores(b, w);
            result.Add(new ArrowSample(new PointF((float)(region.X + b.Cx), (float)(region.Y + b.Cy)), dir, Signature(b, w)));
        }
        return result;
    }

    private static List<RuneArrow>? Detect(Bitmap frame, Bitmap? bannerRef, bool[] mask, Rectangle region, int w, int h, bool thinFilter, int fullFrameH)
    {
        var row = DetectRow(frame, bannerRef, mask, region, w, h, thinFilter, fullFrameH);
        if (row is null) return null;
        var result = new List<RuneArrow>(4);
        foreach (var b in row)
        {
            var (dir, _, _, _, _) = ClassifyScores(b, w);
            result.Add(new RuneArrow(new PointF((float)(region.X + b.Cx), (float)(region.Y + b.Cy)), dir));
        }
        return result;
    }

    /// <summary>진단 CLI에서만 설정 — DetectRow가 밴드·후보·선택 결과를 이 콜백으로 알린다.</summary>
    internal static Action<string>? DiagLog;

    /// <summary>마스크에서 '화살표 줄' 블롭 4개 선택(왼쪽부터). fullArea = 밴드·중심 제약 없이
    /// 전 영역 탐색(진단 전용 — 밴드 기하가 틀리는 창 크기를 확인할 때). 실패 시 null.</summary>
    private static List<Blob>? DetectRow(Bitmap frame, Bitmap? bannerRef, bool[] mask, Rectangle region, int w, int h, bool thinFilter, int fullFrameH, bool fullArea = false)
    {
        if (thinFilter) ThinFilter(mask, w, h);
        var (bandY0, bandY1, bannerCx) = fullArea ? (0, h - 1, -1.0) : BannerBand(w, h, fullFrameH);

        var cands = MergeNear(FindBlobs(mask, w, h)).Where(b =>
            b.Area is >= MinArrowArea and <= MaxArrowArea &&
            b.W is >= MinArrowBox and <= MaxArrowBox && b.H is >= MinArrowBox and <= MaxArrowBox &&
            b.Cy >= bandY0 && b.Cy <= bandY1).ToList();
        var row = PickRow(cands, bannerCx, frame.Width);
        if (DiagLog is not null)
        {
            DiagLog($"밴드 y{region.Y + bandY0}..{region.Y + bandY1} 중심X {(bannerCx >= 0 ? (region.X + bannerCx).ToString("0") : "-")} 후보 {cands.Count}"
                + (row is null ? " → 줄 없음" : " → " + string.Join(" ", row.Select(b => $"({region.X + b.Cx:0},{region.Y + b.Cy:0})a{b.Area}"))));
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
                        for (int i = 1; i < 4; i++)
                        {
                            double gap = combo[i].Cx - combo[i - 1].Cx;
                            if (gap < MinGapPx || gap > MaxGapPx) { gapsOk = false; break; }
                        }
                        if (!gapsOk) continue;
                        int aMin = combo.Min(x => x.Area), aMax = combo.Max(x => x.Area);
                        if (aMax > aMin * 5) continue; // 크기가 제각각인 묶음은 화살표 줄이 아니다
                        double avgX = combo.Average(x => x.Cx);
                        double centerPenalty = 0;
                        if (bannerCx >= 0)
                        {
                            double off = Math.Abs(avgX - bannerCx);
                            if (off > frameW * 0.12) continue; // 배너 축에서 벗어난 줄(우측 팝업창 등) 배제
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

    /// <summary>밝고 채도 높은 '웜톤/초록' 픽셀 마스크. 화살표는 룬마다 색 배치가 달라도
    /// 빨강·주황·노랑·초록 무지개 그라데이션이라 차가운 색(파랑·청록)이 아니다 —
    /// 얼음 소용돌이·나뭇가지 등 파란 계열 배경 클러터를 픽셀 단계에서 배제한다.</summary>
    private static bool[] VividMask(Bitmap frame, Rectangle region, int w, int h, int satMin = VividSat)
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
                        mask[o + x] = max >= VividMax && max - min >= satMin
                                      && (r >= b + 30 || g >= b + 30); // 파랑·청록 우세 픽셀 배제
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

    /// <summary>진단 CLI(--rune-analyze) — 저장된 퍼즐 스크린샷으로 인식 과정을 재현해
    /// 첫 파일 경로 + ".analysis.txt"로 남긴다. 파일 1개 = 채도 마스크만(크롭 검증),
    /// 여러 개(rune-frame-N 연속 캡처) = 실전과 같은 애니메이션 차분 합집합.</summary>
    public static void AnalyzeToFile(params string[] pngPaths)
    {
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
                        // ⑤ 실험: 채도 교집합 — 모든 프레임에서 '계속 채도 높음'(정적 UI) && 발동 전과 다름.
                        //    흔들리는 불꽃은 프레임마다 위치가 바뀌어 교집합에서 탈락한다.
                        if (frames.Count >= 2)
                        {
                            foreach (int sat in new[] { VividSat, 80 })
                            {
                                var mi = VividMask(frames[0], region, w, h, sat);
                                for (int k = 1; k < frames.Count; k++)
                                {
                                    var vk = VividMask(frames[k], region, w, h, sat);
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
    private static (char Dir, double Up, double Down, double Left, double Right) ClassifyScores(Blob b, int w)
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
