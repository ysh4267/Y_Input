using System.Drawing;
using System.Drawing.Imaging;

namespace YInput.Host.Vision;

/// <summary>퍼즐 화살표 하나 — 화면 좌표(중심)와 방향('L','R','U','D').</summary>
internal readonly record struct RuneArrow(PointF Center, char Dir);

/// <summary>한 프레임에서 분석한 화살표 하나 — 방향 + 모양 시그니처(회전 정지 판별용).</summary>
internal readonly record struct ArrowSample(PointF Center, char Dir, bool[] Sig);

/// <summary>
/// 룬 발동(스페이스) 후 화면 상단 배너 아래에 뜨는 방향키 퍼즐(화살표 4개)을 인식한다.
/// 화살표는 채도 높은 작은 글리프(~20~30px)인데 <b>색은 룬마다 다르고</b> 그라데이션도
/// 애니메이션이라, 특정 색을 가정하지 않는다. 대신:
///  ① 발동 직전 프레임과의 차분으로 '새로 나타난' 픽셀만 남기고(배경·이펙트 배제)
///  ② 안내 배너("룬을 해방하려면…" — 가로로 넓게 변한 띠)를 찾아 그 아래 좁은 밴드로
///     탐색을 제한하고(캐릭터 주변 데미지 숫자 같은 '한 줄 4개' 오탐 배제)
///  ③ 채도 높은(색상 불문) 픽셀 블롭에서 한 줄 4개를 고른 뒤
///  ④ 블롭 가장자리 프로파일로 분류 — 화살표의 꼭짓점은 가리키는 쪽 가장자리의
///     '가운데'가 바깥보다 볼록하게 튀어나온다.
/// </summary>
internal static class RuneArrowDetector
{
    // 퍼즐 UI가 뜨는 탐색 범위(화면 비율) — 상단 중앙 넓게
    private const double RegionX0 = 0.08, RegionX1 = 0.92;
    private const double RegionY0 = 0.02, RegionY1 = 0.60;

    private const int DiffMin = 60;      // 발동 전후 프레임 차이(|ΔR|+|ΔG|+|ΔB|) 임계
    private const int VividMax = 130;    // 채도 판정: 최대 채널 밝기 하한
    private const int VividSat = 60;     // 채도 판정: (최대-최소) 하한 — 색상 자체는 특정하지 않는다
    private const double BannerWideFrac = 0.30; // 안내 배너 판정: 가로 이 비율 이상 변한 행
    private const int BannerMinRows = 6;        // 그런 행이 연속 이만큼 = 배너
    private const double ArrowBandFrac = 0.22;  // 배너 상단부터 화면 높이의 이 비율 안에서만 화살표 탐색
    private const int MinPieceArea = 12; // 블롭 조각 최소 픽셀(그라데이션·외곽선으로 쪼개짐 흡수)
    private const int MergePx = 18;      // 같은 화살표로 병합하는 조각 간 거리
    private const int MinArrowArea = 50, MaxArrowArea = 2500;
    private const int MinArrowBox = 8, MaxArrowBox = 70;
    private const int RowBandPx = 22;    // 같은 줄(4개 나열) 판정 Y 허용폭

    /// <summary>프레임에서 화살표 4개를 찾아 왼쪽부터 순서대로 반환(단발 판정 — 입력 후 잔존 확인용).</summary>
    public static List<RuneArrow>? FindArrows(Bitmap frame, Bitmap? before = null) =>
        Analyze(frame, before)?.Select(a => new RuneArrow(a.Center, a.Dir)).ToList();

    /// <summary>한 프레임 분석 — 화살표 4개의 방향 + 모양 시그니처. 회전형 퍼즐(돌다가 정답 방향에서
    /// 잠깐 멈춤)은 호출자가 여러 프레임을 샘플링해 시그니처가 안 변하는 구간(정지)의 방향을 확정한다.
    /// before = 스페이스 직전 프레임(있으면 차분으로 배경을 배제해 오탐이 크게 준다).</summary>
    public static List<ArrowSample>? Analyze(Bitmap frame, Bitmap? before = null)
    {
        var region = new Rectangle(
            (int)(frame.Width * RegionX0), (int)(frame.Height * RegionY0),
            (int)(frame.Width * (RegionX1 - RegionX0)), (int)(frame.Height * (RegionY1 - RegionY0)));
        region = Rectangle.Intersect(region, new Rectangle(0, 0, frame.Width, frame.Height));
        if (region.Width < 100 || region.Height < 60) return null;
        if (before is not null && (before.Width != frame.Width || before.Height != frame.Height)) before = null;

        int w = region.Width, h = region.Height;
        var mask = BuildMask(frame, before, region, w, h, out int bandY0, out int bandY1);

        // 조각 블롭 → 근접 병합 → 화살표 크기 필터 + 배너 아래 밴드 제한
        var pieces = FindBlobs(mask, w, h);
        var arrows0 = MergeNear(pieces);
        var cands = arrows0.Where(b =>
            b.Area is >= MinArrowArea and <= MaxArrowArea &&
            b.W is >= MinArrowBox and <= MaxArrowBox && b.H is >= MinArrowBox and <= MaxArrowBox &&
            b.Cy >= bandY0 && b.Cy <= bandY1).ToList();
        if (cands.Count < 4) return null;

        // 같은 줄(Y ±RowBandPx)에 나열된 묶음 중 가장 큰 것에서 4개 선택(초과분은 면적 큰 순)
        var best = cands
            .Select(a => cands.Where(o => Math.Abs(o.Cy - a.Cy) <= RowBandPx).ToList())
            .OrderByDescending(g => g.Count)
            .First();
        if (best.Count < 4) return null;
        var row = best.OrderByDescending(b => b.Area).Take(4).OrderBy(b => b.Cx).ToList();

        var result = new List<ArrowSample>(4);
        foreach (var b in row)
        {
            char dir = ClassifyByEdgeProfile(b, mask, w);
            result.Add(new ArrowSample(new PointF((float)(region.X + b.Cx), (float)(region.Y + b.Cy)), dir, Signature(b, w)));
        }
        return result;
    }

    // ---------- 모양 시그니처(회전 정지 판별) ----------
    private const int SigN = 12; // 바운딩박스를 12×12 셀로 정규화

    /// <summary>블롭 모양을 바운딩박스 정규화 12×12 그리드로 요약 — 회전 중이면 프레임마다 달라지고,
    /// 멈춰 있으면(정답 방향 표시 구간) 연속 프레임에서 같게 유지된다.</summary>
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
    public static bool SigSimilar(bool[] a, bool[] b, double minMatch = 0.92)
    {
        if (a.Length != b.Length) return false;
        int same = 0;
        for (int i = 0; i < a.Length; i++) if (a[i] == b[i]) same++;
        return (double)same / a.Length >= minMatch;
    }

    /// <summary>화살표 픽셀 마스크 — 채도 높은 픽셀(색상 불문)이고, before가 있으면 '변한' 픽셀만.
    /// bandY0/Y1 = 화살표 탐색 허용 Y 범위(영역 상대). before 차분으로 안내 배너(가로로 넓게 변한
    /// 띠)를 찾으면 그 상단부터 화면 높이 22% 아래까지로 제한 — 캐릭터 주변 데미지 숫자 배제.</summary>
    private static bool[] BuildMask(Bitmap frame, Bitmap? before, Rectangle region, int w, int h,
                                    out int bandY0, out int bandY1)
    {
        var mask = new bool[w * h];
        var rowDiff = new int[h]; // 행별 '변한 픽셀' 수 — 배너 위치 추정용
        var data = frame.LockBits(region, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var dataB = before?.LockBits(region, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            unsafe
            {
                for (int y = 0; y < h; y++)
                {
                    byte* row = (byte*)data.Scan0 + y * data.Stride;
                    byte* rowB = dataB is { } db ? (byte*)db.Scan0 + y * db.Stride : null;
                    int o = y * w;
                    for (int x = 0; x < w; x++)
                    {
                        byte b = row[x * 4], g = row[x * 4 + 1], r = row[x * 4 + 2];
                        bool changed = true;
                        if (rowB is not null)
                        {
                            int d = Math.Abs(r - rowB[x * 4 + 2]) + Math.Abs(g - rowB[x * 4 + 1]) + Math.Abs(b - rowB[x * 4]);
                            changed = d >= DiffMin;
                            if (changed) rowDiff[y]++;
                        }
                        // 색상은 룬마다 달라 특정하지 않는다 — 밝고 채도 높은 픽셀 전부
                        int max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
                        mask[o + x] = changed && max >= VividMax && max - min >= VividSat;
                    }
                }
            }
        }
        finally
        {
            frame.UnlockBits(data);
            if (dataB is { } db2) before!.UnlockBits(db2);
        }

        // 안내 배너 탐색: 위에서부터 '가로로 넓게 변한' 행이 연속되는 첫 띠 — 화살표는 그 근처 아래.
        bandY0 = 0; bandY1 = h - 1;
        if (before is not null)
        {
            int wide = (int)(w * BannerWideFrac), run = 0;
            for (int y = 0; y < h; y++)
            {
                if (rowDiff[y] >= wide) { if (++run >= BannerMinRows) { bandY0 = y - run + 1; bandY1 = Math.Min(h - 1, bandY0 + (int)(frame.Height * ArrowBandFrac)); break; } }
                else run = 0;
            }
        }
        return mask;
    }

    /// <summary>가장자리 프로파일 분류 — 화살표가 가리키는 쪽은 그 가장자리의 '가운데 1/3'이
    /// 바깥 1/3들보다 볼록하다(셰브론·삼각형·축 있는 화살표 모두 해당). 점수 최대 방향 선택.</summary>
    private static char ClassifyByEdgeProfile(Blob b, bool[] mask, int w)
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
        return m == upScore ? 'U' : m == downScore ? 'D' : m == leftScore ? 'L' : 'R';
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
