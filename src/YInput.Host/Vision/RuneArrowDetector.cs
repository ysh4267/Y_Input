using System.Drawing;
using System.Drawing.Imaging;

namespace YInput.Host.Vision;

/// <summary>퍼즐 화살표 하나 — 화면 좌표(중심)와 방향('L','R','U','D').</summary>
internal readonly record struct RuneArrow(PointF Center, char Dir);

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
    private const double BannerWideFrac = 0.30; // 안내 배너 판정: 가로 이 비율 이상 변한 행
    private const int BannerMinRows = 6;        // 그런 행이 연속 이만큼 = 배너
    private const double ArrowBandFrac = 0.22;  // 배너 상단부터 화면 높이의 이 비율 안에서만 화살표 탐색
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
        return Detect(frame, bannerRef, mask, region, w, h, thinFilter: false);
    }

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
        return Detect(frame, bannerRef, mask, region, w, h, thinFilter: true);
    }

    /// <summary>퍼즐(안내 배너)이 지금 떠 있는지 — 열린 퍼즐에 스페이스를 다시 누르면 오답 입력이
    /// 되므로 재발동 전 확인용. 배너 = bannerRef(발동 전) 대비 '가로로 넓게 변한' 행이면서
    /// 지금 두 프레임(frameA→frameB, ~180ms 간격) 사이에는 '정지'인 행 — 몹·이펙트로 화면이
    /// 계속 변하는 맵에서 '아직 열려 있음' 오판을 막는다(정적 오버레이만 배너로 인정).</summary>
    public static bool PuzzlePresent(Bitmap frameA, Bitmap frameB, Bitmap? bannerRef, bool precropped = false)
    {
        if (bannerRef is null) return false;
        if (bannerRef.Width != frameB.Width || bannerRef.Height != frameB.Height) return false;
        if (frameA.Width != frameB.Width || frameA.Height != frameB.Height) return false;
        if (!TryRegion(frameB, precropped, out var region)) return false;
        int w = region.Width, h = region.Height;

        var vsBefore = RowDiffCounts(bannerRef, frameB, region, DiffMin); // 발동 전과 다른가
        var vsNow = RowDiffCounts(frameA, frameB, region, DiffMin);       // 지금 움직이는가
        int wide = (int)(w * BannerWideFrac), still = (int)(w * 0.05);
        int run = 0;
        for (int y = 0; y < h; y++)
        {
            if (vsBefore[y] >= wide && vsNow[y] <= still)
            {
                if (++run >= BannerMinRows) return true;
            }
            else run = 0;
        }
        return false;
    }

    /// <summary>행별 픽셀 차이 개수(|ΔR|+|ΔG|+|ΔB| ≥ min).</summary>
    private static int[] RowDiffCounts(Bitmap a, Bitmap b, Rectangle region, int min)
    {
        int w = region.Width, h = region.Height;
        var counts = new int[h];
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
                    int c = 0;
                    for (int x = 0; x < w; x++)
                    {
                        int i4 = x * 4;
                        int d = Math.Abs(ra[i4] - rb[i4]) + Math.Abs(ra[i4 + 1] - rb[i4 + 1]) + Math.Abs(ra[i4 + 2] - rb[i4 + 2]);
                        if (d >= min) c++;
                    }
                    counts[y] = c;
                }
            }
        }
        finally { a.UnlockBits(da); b.UnlockBits(db); }
        return counts;
    }

    // ---------- 공통 파이프라인 ----------
    private static bool TryRegion(Bitmap frame, bool precropped, out Rectangle region)
    {
        region = precropped
            ? new Rectangle(0, 0, frame.Width, frame.Height)
            : Rectangle.Intersect(PuzzleRegion(frame.Width, frame.Height), new Rectangle(0, 0, frame.Width, frame.Height));
        return region.Width >= 100 && region.Height >= 60;
    }

    private static List<RuneArrow>? Detect(Bitmap frame, Bitmap? bannerRef, bool[] mask, Rectangle region, int w, int h, bool thinFilter)
    {
        if (thinFilter) ThinFilter(mask, w, h);
        var (bandY0, bandY1, bannerCx) = BannerBand(frame, bannerRef, region, w, h);

        var cands = MergeNear(FindBlobs(mask, w, h)).Where(b =>
            b.Area is >= MinArrowArea and <= MaxArrowArea &&
            b.W is >= MinArrowBox and <= MaxArrowBox && b.H is >= MinArrowBox and <= MaxArrowBox &&
            b.Cy >= bandY0 && b.Cy <= bandY1).ToList();
        var row = PickRow(cands, bannerCx, frame.Width);
        if (row is null) return null;

        var result = new List<RuneArrow>(4);
        foreach (var b in row)
        {
            var (dir, _, _, _, _) = ClassifyScores(b, w);
            result.Add(new RuneArrow(new PointF((float)(region.X + b.Cx), (float)(region.Y + b.Cy)), dir));
        }
        return result;
    }

    /// <summary>후보들 중 '화살표 줄' 4개 선택 — 같은 높이(±RowBandPx), 이웃 간격 35~280px,
    /// 크기 유사(최대/최소 면적 ≤5배), 배너 중심 정렬(±12% 폭). 조건을 만족하는 조합 중
    /// 면적 합이 크고 배너 중심에 가까운 것. 없으면 null.</summary>
    private static List<Blob>? PickRow(List<Blob> cands, double bannerCx, int frameW)
    {
        if (cands.Count < 4) return null;
        var top = cands.OrderByDescending(b => b.Area).Take(14).OrderBy(b => b.Cx).ToList();
        List<Blob>? best = null; double bestScore = double.MinValue;
        int n = top.Count;
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
                        double score = combo.Sum(x => (double)x.Area) - centerPenalty;
                        if (score > bestScore) { bestScore = score; best = combo.ToList(); }
                    }
        return best;
    }

    /// <summary>밝고 채도 높은 '웜톤/초록' 픽셀 마스크. 화살표는 룬마다 색 배치가 달라도
    /// 빨강·주황·노랑·초록 무지개 그라데이션이라 차가운 색(파랑·청록)이 아니다 —
    /// 얼음 소용돌이·나뭇가지 등 파란 계열 배경 클러터를 픽셀 단계에서 배제한다.</summary>
    private static bool[] VividMask(Bitmap frame, Rectangle region, int w, int h)
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
                        mask[o + x] = max >= VividMax && max - min >= VividSat
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

    /// <summary>안내 배너("룬을 해방하려면…") 탐색 — bannerRef 대비 '가로로 넓게 변한' 행이 연속되는
    /// 첫 띠의 상단부터 화면 높이 22% 아래까지를 화살표 밴드로 반환. CenterX = 배너 가로 중심
    /// (화살표 줄은 배너와 같은 축에 놓인다 — 우측 팝업창 등 오탐 줄 배제용). 못 찾으면 (전체, -1).</summary>
    private static (int Y0, int Y1, double CenterX) BannerBand(Bitmap frame, Bitmap? bannerRef, Rectangle region, int w, int h)
    {
        if (bannerRef is null || bannerRef.Width != frame.Width || bannerRef.Height != frame.Height) return (0, h - 1, -1);
        var changed = new bool[w * h];
        AccumulateDiff(bannerRef, frame, region, w, h, DiffMin, changed);
        int wide = (int)(w * BannerWideFrac), run = 0;
        for (int y = 0; y < h; y++)
        {
            int cnt = 0, minX = w, maxX = -1;
            int o = y * w;
            for (int x = 0; x < w; x++)
                if (changed[o + x]) { cnt++; if (x < minX) minX = x; if (x > maxX) maxX = x; }
            if (cnt >= wide)
            {
                if (++run >= BannerMinRows)
                {
                    int y0 = y - run + 1;
                    return (y0, Math.Min(h - 1, y0 + (int)(frame.Height * ArrowBandFrac)), (minX + maxX) / 2.0);
                }
            }
            else run = 0;
        }
        return (0, h - 1, -1);
    }

    /// <summary>진단 CLI(--rune-analyze) — 저장된 퍼즐 스크린샷으로 인식 과정을 재현해
    /// 첫 파일 경로 + ".analysis.txt"로 남긴다. 파일 1개 = 채도 마스크만(크롭 검증),
    /// 여러 개(rune-frame-N 연속 캡처) = 실전과 같은 애니메이션 차분 합집합.</summary>
    public static void AnalyzeToFile(params string[] pngPaths)
    {
        var sb = new System.Text.StringBuilder();
        var frames = new List<Bitmap>();
        try
        {
            foreach (var p in pngPaths) frames.Add(new Bitmap(p));
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

    /// <summary>가장자리 프로파일 분류 — 화살표가 가리키는 쪽은 그 가장자리의 '가운데 1/3'이
    /// 바깥 1/3들보다 볼록하다(셰브론·삼각형·축 있는 화살표 모두 해당). 점수 최대 방향 선택.</summary>
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

        // 가운데 1/3 vs 바깥 1/3 평균(빈 열/행은 제외)
        double AvgRange(int[] arr, int from, int to, bool isMin)
        {
            double sum = 0; int n = 0;
            for (int i = Math.Max(0, from); i < Math.Min(arr.Length, to); i++)
            {
                if (arr[i] == int.MaxValue || arr[i] == int.MinValue) continue;
                sum += arr[i]; n++;
            }
            return n == 0 ? (isMin ? double.MaxValue : double.MinValue) : sum / n;
        }
        int cw = Math.Max(1, bw / 3), ch = Math.Max(1, bh / 3);
        double upScore = (Math.Min(AvgRange(minY, 0, cw, true), AvgRange(minY, bw - cw, bw, true))
                          - AvgRange(minY, (bw - cw) / 2, (bw + cw) / 2, true)) / bh;
        double downScore = (AvgRange(maxY, (bw - cw) / 2, (bw + cw) / 2, false)
                          - Math.Max(AvgRange(maxY, 0, cw, false), AvgRange(maxY, bw - cw, bw, false))) / bh;
        double leftScore = (Math.Min(AvgRange(minX, 0, ch, true), AvgRange(minX, bh - ch, bh, true))
                          - AvgRange(minX, (bh - ch) / 2, (bh + ch) / 2, true)) / bw;
        double rightScore = (AvgRange(maxX, (bh - ch) / 2, (bh + ch) / 2, false)
                          - Math.Max(AvgRange(maxX, 0, ch, false), AvgRange(maxX, bh - ch, bh, false))) / bw;

        double m = Math.Max(Math.Max(upScore, downScore), Math.Max(leftScore, rightScore));
        char dir = m == upScore ? 'U' : m == downScore ? 'D' : m == leftScore ? 'L' : 'R';
        return (dir, upScore, downScore, leftScore, rightScore);
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
