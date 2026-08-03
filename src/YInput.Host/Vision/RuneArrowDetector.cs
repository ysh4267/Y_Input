using System.Drawing;
using System.Drawing.Imaging;

namespace YInput.Host.Vision;

/// <summary>퍼즐 화살표 하나 — 화면 좌표(중심)와 방향('L','R','U','D').</summary>
internal readonly record struct RuneArrow(PointF Center, char Dir);

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
    private const int MergePx = 10;      // 같은 화살표로 병합하는 조각 간 거리 — 침식 후 본체는 한 덩어리라
                                         // 좁게 잡는다(넓으면 바 외곽선 조각과 이웃 장식이 도로 붙는다)
    private const int MinArrowArea = 50, MaxArrowArea = 2500;
    private const int MinArrowBox = 8, MaxArrowBox = 70;
    private const int RowBandPx = 22;    // 같은 줄(4개 나열) 판정 Y 허용폭

    private const int MinGapPx = 35, MaxGapPx = 280; // 화살표 이웃 간격 상식 범위 — 데미지 숫자(촘촘) 배제

    /// <summary>프레임에서 화살표 4개를 찾아 왼쪽부터 순서대로 반환. 4개를 못 찾으면 null.
    /// diffRef = 차분 기준 프레임 — '그 이후 변한' 픽셀만 화살표 후보로 삼는다.
    ///   퍼즐이 뜬 상태에서 ~150ms 전 프레임을 주면 그라데이션이 흐르는 화살표만 남고
    ///   바·배경(반투명 바 너머로 비치는 장식 포함)·배너는 정지라 배제된다.
    /// bannerRef = 발동 직전 프레임 — 안내 배너(가로로 넓게 변한 띠)를 찾아 탐색 밴드를 좁힌다.</summary>
    public static List<RuneArrow>? FindArrows(Bitmap frame, Bitmap? diffRef = null, Bitmap? bannerRef = null)
    {
        var region = new Rectangle(
            (int)(frame.Width * RegionX0), (int)(frame.Height * RegionY0),
            (int)(frame.Width * (RegionX1 - RegionX0)), (int)(frame.Height * (RegionY1 - RegionY0)));
        region = Rectangle.Intersect(region, new Rectangle(0, 0, frame.Width, frame.Height));
        if (region.Width < 100 || region.Height < 60) return null;
        if (diffRef is not null && (diffRef.Width != frame.Width || diffRef.Height != frame.Height)) diffRef = null;
        if (bannerRef is not null && (bannerRef.Width != frame.Width || bannerRef.Height != frame.Height)) bannerRef = null;

        int w = region.Width, h = region.Height;
        var mask = BuildMask(frame, diffRef, bannerRef, region, w, h, out int bandY0, out int bandY1);

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

        // 간격 상식 검사 — 너무 촘촘(데미지 숫자)하거나 너무 벌어진 묶음은 화살표 줄이 아니다
        for (int i = 1; i < row.Count; i++)
        {
            double gap = row[i].Cx - row[i - 1].Cx;
            if (gap < MinGapPx || gap > MaxGapPx) return null;
        }

        var result = new List<RuneArrow>(4);
        foreach (var b in row)
        {
            char dir = ClassifyByEdgeProfile(b, mask, w);
            result.Add(new RuneArrow(new PointF((float)(region.X + b.Cx), (float)(region.Y + b.Cy)), dir));
        }
        return result;
    }

    /// <summary>화살표 픽셀 마스크 — 채도 높은 픽셀(색상 불문)이고, diffRef가 있으면 '그 이후 변한' 픽셀만.
    /// bandY0/Y1 = 화살표 탐색 허용 Y 범위(영역 상대). bannerRef 차분으로 안내 배너(가로로 넓게 변한
    /// 띠)를 찾으면 그 상단부터 화면 높이 22% 아래까지로 제한 — 캐릭터 주변 데미지 숫자 배제.</summary>
    private static bool[] BuildMask(Bitmap frame, Bitmap? diffRef, Bitmap? bannerRef, Rectangle region, int w, int h,
                                    out int bandY0, out int bandY1)
    {
        var mask = new bool[w * h];
        var rowDiff = new int[h]; // bannerRef 기준 행별 '변한 픽셀' 수 — 배너 위치 추정용
        var data = frame.LockBits(region, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var dataD = diffRef?.LockBits(region, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var dataN = bannerRef?.LockBits(region, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            unsafe
            {
                for (int y = 0; y < h; y++)
                {
                    byte* row = (byte*)data.Scan0 + y * data.Stride;
                    byte* rowD = dataD is { } dd ? (byte*)dd.Scan0 + y * dd.Stride : null;
                    byte* rowN = dataN is { } dn ? (byte*)dn.Scan0 + y * dn.Stride : null;
                    int o = y * w;
                    for (int x = 0; x < w; x++)
                    {
                        byte b = row[x * 4], g = row[x * 4 + 1], r = row[x * 4 + 2];
                        bool changed = true;
                        if (rowD is not null)
                        {
                            int d = Math.Abs(r - rowD[x * 4 + 2]) + Math.Abs(g - rowD[x * 4 + 1]) + Math.Abs(b - rowD[x * 4]);
                            changed = d >= DiffMin;
                        }
                        if (rowN is not null)
                        {
                            int d = Math.Abs(r - rowN[x * 4 + 2]) + Math.Abs(g - rowN[x * 4 + 1]) + Math.Abs(b - rowN[x * 4]);
                            if (d >= DiffMin) rowDiff[y]++;
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
            if (dataD is { } dd2) diffRef!.UnlockBits(dd2);
            if (dataN is { } dn2) bannerRef!.UnlockBits(dn2);
        }

        // 얇은 구조 제거 — 반투명 바 외곽선·글로우 다리가 화살표 4개를 한 덩어리로 이어붙여
        // 크기 필터에서 통째로 탈락시키는 것을 방지(20:00 오답 프레임 분석).
        // 가로·세로 연속 길이가 모두 MinThick 이상인 픽셀만 유지 — 침식과 달리 본체 크기를 보존한다.
        const int MinThick = 6;
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

        // 안내 배너 탐색: 위에서부터 '가로로 넓게 변한' 행이 연속되는 첫 띠 — 화살표는 그 근처 아래.
        bandY0 = 0; bandY1 = h - 1;
        if (bannerRef is not null)
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

    /// <summary>진단 CLI(--rune-analyze) — 저장된 퍼즐 스크린샷으로 인식 과정을 재현해
    /// &lt;png&gt;.analysis.txt로 남긴다(블롭 목록·선택된 줄·화살표별 방향 점수).</summary>
    public static void AnalyzeToFile(string pngPath)
    {
        var sb = new System.Text.StringBuilder();
        try
        {
            using var frame = new Bitmap(pngPath);
            var region = new Rectangle(
                (int)(frame.Width * RegionX0), (int)(frame.Height * RegionY0),
                (int)(frame.Width * (RegionX1 - RegionX0)), (int)(frame.Height * (RegionY1 - RegionY0)));
            region = Rectangle.Intersect(region, new Rectangle(0, 0, frame.Width, frame.Height));
            int w = region.Width, h = region.Height;
            var mask = BuildMask(frame, null, null, region, w, h, out int bandY0, out int bandY1);
            sb.AppendLine($"frame {frame.Width}x{frame.Height} region {region} band y {region.Y + bandY0}..{region.Y + bandY1} (기준 프레임 없음 — 차분·배너 미적용)");

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
                b.W is >= MinArrowBox and <= MaxArrowBox && b.H is >= MinArrowBox and <= MaxArrowBox &&
                b.Cy >= bandY0 && b.Cy <= bandY1).ToList();
            sb.AppendLine($"후보(밴드 내) {cands.Count}개");
            if (cands.Count >= 4)
            {
                var best = cands
                    .Select(a => cands.Where(o => Math.Abs(o.Cy - a.Cy) <= RowBandPx).ToList())
                    .OrderByDescending(g => g.Count).First();
                var row = best.OrderByDescending(b => b.Area).Take(4).OrderBy(b => b.Cx).ToList();
                sb.AppendLine("선택된 줄:");
                foreach (var b in row)
                {
                    var (dir, up, down, left, right) = ClassifyScores(b, w);
                    sb.AppendLine($"  ({region.X + b.Cx:0},{region.Y + b.Cy:0}) a{b.Area} {b.W}x{b.H} → {dir}  U{up:0.000} D{down:0.000} L{left:0.000} R{right:0.000}");
                }
            }
        }
        catch (Exception ex) { sb.AppendLine("오류: " + ex); }
        File.WriteAllText(pngPath + ".analysis.txt", sb.ToString());
    }

    /// <summary>가장자리 프로파일 분류 — 화살표가 가리키는 쪽은 그 가장자리의 '가운데 1/3'이
    /// 바깥 1/3들보다 볼록하다(셰브론·삼각형·축 있는 화살표 모두 해당). 점수 최대 방향 선택.</summary>
    private static char ClassifyByEdgeProfile(Blob b, bool[] mask, int w) => ClassifyScores(b, w).Dir;

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
