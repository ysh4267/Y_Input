using System.Drawing;
using System.Drawing.Imaging;

namespace YInput.Host.Vision;

/// <summary>퍼즐 화살표 하나 — 화면 좌표(중심)와 방향('L','R','U','D').</summary>
internal readonly record struct RuneArrow(PointF Center, char Dir);

/// <summary>
/// 룬 발동(스페이스) 후 화면 상단 중앙에 뜨는 방향키 퍼즐(화살표 4개)을 인식한다.
/// 화살표는 색이 매번 랜덤이지만 <b>꼬리가 빨강 계열 → 머리가 초록 계열</b> 그라데이션이라,
/// 빨강 픽셀 덩어리와 그 주변 초록 픽셀 무게중심을 짝지어 '빨강→초록' 벡터로 방향을 판정한다.
/// (커뮤니티에서 검증된 휴리스틱 — maple-bot의 red→green 접근과 동일 원리)
/// </summary>
internal static class RuneArrowDetector
{
    // 퍼즐 UI가 뜨는 탐색 범위(화면 비율) — 상단 중앙 넓게
    private const double RegionX0 = 0.10, RegionX1 = 0.90;
    private const double RegionY0 = 0.03, RegionY1 = 0.60;

    private const int MinRedBlobArea = 12;    // 화살표 꼬리 빨강 덩어리 최소 픽셀
    private const int MaxRedBlobArea = 2000;
    private const int MergeDistPx = 22;       // 이 거리 안의 빨강 블롭은 같은 화살표로 병합
    private const int GreenSearchRadius = 60; // 빨강 중심에서 초록(머리)을 찾는 반경
    private const int MinGreenCount = 10;     // 초록 픽셀 최소 개수
    private const double MinVectorLen = 6;    // 빨강→초록 벡터 최소 길이(px)
    private const int RowBandPx = 30;         // 같은 줄(4개 나열) 판정 Y 허용폭

    /// <summary>프레임에서 화살표 4개를 찾아 왼쪽부터 순서대로 반환. 4개를 못 찾으면 null.</summary>
    public static List<RuneArrow>? FindArrows(Bitmap frame)
    {
        var region = new Rectangle(
            (int)(frame.Width * RegionX0), (int)(frame.Height * RegionY0),
            (int)(frame.Width * (RegionX1 - RegionX0)), (int)(frame.Height * (RegionY1 - RegionY0)));
        region = Rectangle.Intersect(region, new Rectangle(0, 0, frame.Width, frame.Height));
        if (region.Width < 100 || region.Height < 60) return null;

        int w = region.Width, h = region.Height;
        var red = new bool[w * h];
        var green = new bool[w * h];
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
                        // 반투명 합성으로 채도가 깎이므로 임계는 느슨하게 — 우세 채널 기준
                        red[o + x] = r >= 130 && r - g >= 45 && r - b >= 45;
                        green[o + x] = g >= 120 && g - r >= 40 && g - b >= 25;
                    }
                }
            }
        }
        finally { frame.UnlockBits(data); }

        // 빨강 블롭(화살표 꼬리) → 근접 병합
        var blobs = FindBlobs(red, w, h);
        var tails = MergeNear(blobs);
        if (tails.Count == 0) return null;

        // 각 꼬리마다 반경 안 초록(머리) 무게중심 → 빨강→초록 벡터로 방향
        var arrows = new List<(RuneArrow A, int Area)>();
        foreach (var t in tails)
        {
            double gx = 0, gy = 0; int gc = 0;
            int x0 = Math.Max(0, (int)t.Cx - GreenSearchRadius), x1 = Math.Min(w - 1, (int)t.Cx + GreenSearchRadius);
            int y0 = Math.Max(0, (int)t.Cy - GreenSearchRadius), y1 = Math.Min(h - 1, (int)t.Cy + GreenSearchRadius);
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    if (!green[y * w + x]) continue;
                    double dx0 = x - t.Cx, dy0 = y - t.Cy;
                    if (dx0 * dx0 + dy0 * dy0 > GreenSearchRadius * (double)GreenSearchRadius) continue;
                    gx += x; gy += y; gc++;
                }
            if (gc < MinGreenCount) continue;
            double dx = gx / gc - t.Cx, dy = gy / gc - t.Cy;
            if (dx * dx + dy * dy < MinVectorLen * MinVectorLen) continue;
            char dir = Math.Abs(dx) >= Math.Abs(dy) ? (dx > 0 ? 'R' : 'L') : (dy > 0 ? 'D' : 'U');
            var center = new PointF((float)(region.X + (t.Cx + gx / gc) / 2), (float)(region.Y + (t.Cy + gy / gc) / 2));
            arrows.Add((new RuneArrow(center, dir), t.Area));
        }
        if (arrows.Count < 4) return null;

        // 같은 줄(Y ±RowBandPx)에 나열된 묶음 중 가장 큰 것에서 4개 선택
        var best = arrows
            .Select(a => arrows.Where(o => Math.Abs(o.A.Center.Y - a.A.Center.Y) <= RowBandPx).ToList())
            .OrderByDescending(g => g.Count)
            .First();
        if (best.Count < 4) return null;
        // 4개 초과면(배경 오탐 섞임) 면적 큰 4개를 취하고 왼쪽부터 정렬
        return best.OrderByDescending(g => g.Area).Take(4)
                   .Select(g => g.A).OrderBy(a => a.Center.X).ToList();
    }

    private readonly record struct Blob(double Cx, double Cy, int Area);

    private static List<Blob> FindBlobs(bool[] mask, int w, int h)
    {
        var list = new List<Blob>();
        var seen = new bool[w * h];
        var stack = new Stack<int>();
        for (int i = 0; i < mask.Length; i++)
        {
            if (!mask[i] || seen[i]) continue;
            long sumX = 0, sumY = 0; int count = 0;
            stack.Push(i); seen[i] = true;
            while (stack.Count > 0)
            {
                int p = stack.Pop();
                int px = p % w, py = p / w;
                sumX += px; sumY += py; count++;
                if (px > 0 && mask[p - 1] && !seen[p - 1]) { seen[p - 1] = true; stack.Push(p - 1); }
                if (px < w - 1 && mask[p + 1] && !seen[p + 1]) { seen[p + 1] = true; stack.Push(p + 1); }
                if (py > 0 && mask[p - w] && !seen[p - w]) { seen[p - w] = true; stack.Push(p - w); }
                if (py < h - 1 && mask[p + w] && !seen[p + w]) { seen[p + w] = true; stack.Push(p + w); }
            }
            if (count is >= MinRedBlobArea and <= MaxRedBlobArea)
                list.Add(new Blob((double)sumX / count, (double)sumY / count, count));
        }
        return list;
    }

    /// <summary>가까운 블롭 병합(면적 가중 평균) — 화살표 꼬리가 그라데이션 때문에 조각나는 것을 흡수.</summary>
    private static List<Blob> MergeNear(List<Blob> blobs)
    {
        var merged = new List<Blob>();
        var used = new bool[blobs.Count];
        for (int i = 0; i < blobs.Count; i++)
        {
            if (used[i]) continue;
            double cx = blobs[i].Cx * blobs[i].Area, cy = blobs[i].Cy * blobs[i].Area;
            int area = blobs[i].Area;
            used[i] = true;
            for (int j = i + 1; j < blobs.Count; j++)
            {
                if (used[j]) continue;
                double dx = blobs[j].Cx - blobs[i].Cx, dy = blobs[j].Cy - blobs[i].Cy;
                if (dx * dx + dy * dy > MergeDistPx * (double)MergeDistPx) continue;
                cx += blobs[j].Cx * blobs[j].Area; cy += blobs[j].Cy * blobs[j].Area;
                area += blobs[j].Area;
                used[j] = true;
            }
            merged.Add(new Blob(cx / area, cy / area, area));
        }
        return merged;
    }
}
