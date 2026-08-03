using System.Drawing;
using System.Drawing.Imaging;

namespace YInput.Host.Vision;

/// <summary>그레이스케일 이미지(행 우선 바이트 배열).</summary>
internal readonly record struct GrayImage(byte[] Pixels, int Width, int Height)
{
    public byte At(int x, int y) => Pixels[y * Width + x];
}

/// <summary>템플릿 매칭 결과. X/Y는 frame 안에서 템플릿 좌상단이 매칭된 위치, Score는 NCC(-1..1).</summary>
internal readonly record struct MatchResult(int X, int Y, double Score);

/// <summary>
/// 제로민 NCC(정규화 상호상관) 템플릿 매칭 — OpenCV 없이 순수 구현(단일 파일 배포 유지).
/// 1/2 축소 코스 탐색으로 후보를 잡고 원본 해상도에서 ±3px 정밀화한다.
/// 제로민 정규화라 전체 밝기 변화(낮/밤 필터 등)에 강하다.
/// </summary>
internal static class TemplateMatcher
{
    /// <summary>Bitmap → 그레이스케일. LockBits(32bppArgb 변환 잠금) 후 (299R+587G+114B)/1000.</summary>
    public static GrayImage ToGray(Bitmap bmp)
    {
        var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
        var data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            int w = bmp.Width, h = bmp.Height;
            var gray = new byte[w * h];
            unsafe
            {
                for (int y = 0; y < h; y++)
                {
                    byte* row = (byte*)data.Scan0 + y * data.Stride;
                    int o = y * w;
                    for (int x = 0; x < w; x++)
                    {
                        byte b = row[x * 4], g = row[x * 4 + 1], r = row[x * 4 + 2];
                        gray[o + x] = (byte)((r * 299 + g * 587 + b * 114) / 1000);
                    }
                }
            }
            return new GrayImage(gray, w, h);
        }
        finally { bmp.UnlockBits(data); }
    }

    /// <summary>2×2 평균 다운스케일(코스 탐색용).</summary>
    public static GrayImage Downscale2(GrayImage src)
    {
        int w = Math.Max(1, src.Width / 2), h = Math.Max(1, src.Height / 2);
        var dst = new byte[w * h];
        for (int y = 0; y < h; y++)
        {
            int sy = y * 2;
            for (int x = 0; x < w; x++)
            {
                int sx = x * 2;
                int sum = src.At(sx, sy) + src.At(Math.Min(sx + 1, src.Width - 1), sy)
                        + src.At(sx, Math.Min(sy + 1, src.Height - 1))
                        + src.At(Math.Min(sx + 1, src.Width - 1), Math.Min(sy + 1, src.Height - 1));
                dst[y * w + x] = (byte)(sum / 4);
            }
        }
        return new GrayImage(dst, w, h);
    }

    /// <summary>템플릿 표준편차(0이면 단색 — 매칭 무의미, 저장 시 "특징 부족" 거부에 사용).</summary>
    public static double StdDev(GrayImage img)
    {
        long sum = 0, sum2 = 0;
        foreach (var p in img.Pixels) { sum += p; sum2 += (long)p * p; }
        double n = img.Pixels.Length;
        double mean = sum / n;
        return Math.Sqrt(Math.Max(0, sum2 / n - mean * mean));
    }

    /// <summary>
    /// frame의 search 영역(템플릿 좌상단 후보 범위)에서 NCC 최대점을 찾는다.
    /// 1/2 축소로 전 범위 코스 탐색 → 원본 해상도에서 최적점 주변 ±3px 정밀화.
    /// </summary>
    public static MatchResult Match(GrayImage frame, GrayImage tmpl, Rectangle search)
    {
        if (tmpl.Width > frame.Width || tmpl.Height > frame.Height) return new MatchResult(0, 0, 0);

        // 코스: 1/2 해상도 전역 탐색
        var f2 = Downscale2(frame);
        var t2 = Downscale2(tmpl);
        var s2 = new Rectangle(search.X / 2, search.Y / 2, Math.Max(1, search.Width / 2), Math.Max(1, search.Height / 2));
        var coarse = MatchExhaustive(f2, t2, s2);

        // 파인: 코스 최적점 ×2 주변 ±3px
        int cx = coarse.X * 2, cy = coarse.Y * 2;
        var fine = new Rectangle(cx - 3, cy - 3, 7, 7);
        fine.Intersect(search);
        if (fine.Width <= 0 || fine.Height <= 0) fine = new Rectangle(cx, cy, 1, 1);
        return MatchExhaustive(frame, tmpl, fine);
    }

    private static MatchResult MatchExhaustive(GrayImage frame, GrayImage tmpl, Rectangle search)
    {
        int tw = tmpl.Width, th = tmpl.Height, n = tw * th;

        // 템플릿 제로민 배열 — Σ(t-tm)=0 이므로 분자 = Σ f·(t-tm) 한 번의 곱합으로 끝난다.
        long tSum = 0, tSum2 = 0;
        foreach (var p in tmpl.Pixels) { tSum += p; tSum2 += (long)p * p; }
        double tMean = (double)tSum / n;
        double tVar = tSum2 - (double)tSum * tSum / n; // n·분산
        if (tVar <= 0) return new MatchResult(search.X, search.Y, 0);
        var tz = new double[n];
        for (int i = 0; i < n; i++) tz[i] = tmpl.Pixels[i] - tMean;

        int x0 = Math.Max(0, search.Left), y0 = Math.Max(0, search.Top);
        int x1 = Math.Min(frame.Width - tw, search.Right - 1);
        int y1 = Math.Min(frame.Height - th, search.Bottom - 1);

        var best = new MatchResult(x0, y0, double.MinValue);
        for (int y = y0; y <= y1; y++)
        {
            for (int x = x0; x <= x1; x++)
            {
                long fSum = 0, fSum2 = 0;
                double cross = 0;
                int ti = 0;
                for (int ty = 0; ty < th; ty++)
                {
                    int fo = (y + ty) * frame.Width + x;
                    for (int tx = 0; tx < tw; tx++, ti++)
                    {
                        int f = frame.Pixels[fo + tx];
                        fSum += f; fSum2 += (long)f * f;
                        cross += f * tz[ti];
                    }
                }
                double fVar = fSum2 - (double)fSum * fSum / n;
                double score = fVar <= 0 ? 0 : cross / Math.Sqrt(fVar * tVar);
                if (score > best.Score) best = new MatchResult(x, y, score);
            }
        }
        return best.Score == double.MinValue ? new MatchResult(x0, y0, 0) : best;
    }
}
