using System.Drawing;
using System.Drawing.Imaging;

namespace YInput.Host.Vision;

/// <summary>미니맵에서 찾은 노란 점 후보 하나 — 중심(서브픽셀), 픽셀 수, 바운딩박스 크기.</summary>
internal readonly record struct DotCandidate(PointF Center, int Area, int BoxW, int BoxH);

/// <summary>
/// 미니맵에서 플레이어 노란 점을 찾는다. 노란 픽셀 전체의 평균이 아니라 <b>연결된 덩어리(블롭)</b>
/// 단위로 분리한 뒤 '점처럼 생긴' 블롭만 후보로 남긴다 — 미니맵에 다른 노란 요소(아이콘·장식)가
/// 있어도 그 사이 엉뚱한 위치로 계산되지 않는다. 여러 후보 중에서는 직전 위치에 가장 가까운 것
/// (추적) 또는 플레이어 점 크기(~3×3)에 가장 가까운 것을 고른다.
/// </summary>
internal static class MinimapDetector
{
    private const int MinBlobArea = 2;    // 1px 노이즈 제외
    private const int MaxBlobArea = 150;  // 큰 노란 UI 덩어리 제외
    private const int MaxBlobBox = 14;    // 점은 작다 — 넓게 퍼진 장식·텍스트 제외
    private const int TypicalDotArea = 9; // 플레이어 점 ≈ 3×3

    /// <summary>minimapRect 안의 점 후보 블롭 목록(중심은 minimapRect 상대, 서브픽셀).</summary>
    public static List<DotCandidate> FindDots(Bitmap frame, Rectangle minimapRect,
                                              int minR = 200, int minG = 180, int maxB = 120)
    {
        var list = new List<DotCandidate>();
        var area = Rectangle.Intersect(minimapRect, new Rectangle(0, 0, frame.Width, frame.Height));
        if (area.Width <= 0 || area.Height <= 0) return list;

        int w = area.Width, h = area.Height;
        var mask = new bool[w * h];
        var data = frame.LockBits(area, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
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
                        mask[o + x] = r >= minR && g >= minG && b <= maxB;
                    }
                }
            }
        }
        finally { frame.UnlockBits(data); }

        // 연결 요소(4방향) 분리 — 블롭별 centroid/면적/바운딩박스
        var seen = new bool[w * h];
        var stack = new Stack<int>();
        float ox = area.X - minimapRect.X, oy = area.Y - minimapRect.Y;
        for (int i = 0; i < mask.Length; i++)
        {
            if (!mask[i] || seen[i]) continue;
            long sumX = 0, sumY = 0; int count = 0;
            int minX = w, maxX = -1, minY = h, maxY = -1;
            stack.Push(i); seen[i] = true;
            while (stack.Count > 0)
            {
                int p = stack.Pop();
                int px = p % w, py = p / w;
                sumX += px; sumY += py; count++;
                if (px < minX) minX = px; if (px > maxX) maxX = px;
                if (py < minY) minY = py; if (py > maxY) maxY = py;
                if (px > 0 && mask[p - 1] && !seen[p - 1]) { seen[p - 1] = true; stack.Push(p - 1); }
                if (px < w - 1 && mask[p + 1] && !seen[p + 1]) { seen[p + 1] = true; stack.Push(p + 1); }
                if (py > 0 && mask[p - w] && !seen[p - w]) { seen[p - w] = true; stack.Push(p - w); }
                if (py < h - 1 && mask[p + w] && !seen[p + w]) { seen[p + w] = true; stack.Push(p + w); }
            }
            int bw = maxX - minX + 1, bh = maxY - minY + 1;
            if (count >= MinBlobArea && count <= MaxBlobArea && bw <= MaxBlobBox && bh <= MaxBlobBox)
                list.Add(new DotCandidate(new PointF((float)sumX / count + ox, (float)sumY / count + oy), count, bw, bh));
        }
        return list;
    }

    /// <summary>후보 중 플레이어 점 선택 — near(직전 위치)가 있으면 가장 가까운 것(추적),
    /// 없으면 점 크기(~3×3)에 가장 가까운 것.</summary>
    public static DotCandidate Pick(List<DotCandidate> cands, PointF? near = null)
    {
        if (near is { } n)
            return cands.MinBy(c => (c.Center.X - n.X) * (c.Center.X - n.X) + (c.Center.Y - n.Y) * (c.Center.Y - n.Y));
        return cands.MinBy(c => Math.Abs(c.Area - TypicalDotArea));
    }

    /// <summary>플레이어 점 탐지(블롭 기반). dot은 minimapRect 상대, 서브픽셀.
    /// near = 직전 측정 위치(보정 중 추적) — 다른 노란 점으로 튀는 것을 막는다.</summary>
    public static bool TryFindPlayerDot(Bitmap frame, Rectangle minimapRect, out PointF dot,
                                        int minR = 200, int minG = 180, int maxB = 120, PointF? near = null)
    {
        dot = PointF.Empty;
        var cands = FindDots(frame, minimapRect, minR, minG, maxB);
        if (cands.Count == 0) return false;
        dot = Pick(cands, near).Center;
        return true;
    }
}
