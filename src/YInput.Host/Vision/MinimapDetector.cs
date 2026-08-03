using System.Drawing;
using System.Drawing.Imaging;

namespace YInput.Host.Vision;

/// <summary>
/// 미니맵에서 플레이어 노란 점을 색 임계로 찾는다. 점은 수 px 크기라 매칭 픽셀들의
/// 중심점(centroid)으로 안정화한다. 다른 유저(빨강)/NPC 점과는 색으로 구분된다.
/// </summary>
internal static class MinimapDetector
{
    /// <summary>frame의 minimapRect 영역에서 노란 점 중심을 찾는다. dot은 minimapRect 기준 상대 좌표 —
    /// 매칭 픽셀들의 <b>서브픽셀 centroid</b>(소수점)라 미니맵 1px 미만의 이동도 감지된다(1px ≈ 실좌표 수 px).
    /// 임계 기본값: R≥200, G≥180, B≤120 (메이플 플레이어 점 노랑).</summary>
    public static bool TryFindPlayerDot(Bitmap frame, Rectangle minimapRect, out PointF dot,
                                        int minR = 200, int minG = 180, int maxB = 120)
    {
        dot = PointF.Empty;
        var area = Rectangle.Intersect(minimapRect, new Rectangle(0, 0, frame.Width, frame.Height));
        if (area.Width <= 0 || area.Height <= 0) return false;

        long sumX = 0, sumY = 0; int count = 0;
        var data = frame.LockBits(area, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            unsafe
            {
                for (int y = 0; y < area.Height; y++)
                {
                    byte* row = (byte*)data.Scan0 + y * data.Stride;
                    for (int x = 0; x < area.Width; x++)
                    {
                        byte b = row[x * 4], g = row[x * 4 + 1], r = row[x * 4 + 2];
                        if (r >= minR && g >= minG && b <= maxB) { sumX += x; sumY += y; count++; }
                    }
                }
            }
        }
        finally { frame.UnlockBits(data); }

        if (count == 0) return false;
        // 넓은 노란 영역(UI 장식 등 오검출)이면 점이 아니다 — 플레이어 점은 수~수십 px.
        if (count > 400) return false;
        dot = new PointF((float)sumX / count + (area.X - minimapRect.X),
                         (float)sumY / count + (area.Y - minimapRect.Y));
        return true;
    }
}
