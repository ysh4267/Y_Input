using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace YInput.Host.Vision;

/// <summary>화면/창 캡처(GDI). 위치 지킴이가 게임 화면을 찍는 데 사용.</summary>
internal static class ScreenCapture
{
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);
    private const uint PW_RENDERFULLCONTENT = 2; // DWM 합성본 렌더 — 다른 창에 가려져 있어도 창 내용이 찍힘

    /// <summary>화면 영역 복사 캡처 — 창이 다른 창에 가려져 있으면 가린 내용이 찍힌다(폴백용).</summary>
    public static Bitmap Capture(Rectangle screenRect)
    {
        var bmp = new Bitmap(screenRect.Width, screenRect.Height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.CopyFromScreen(screenRect.Left, screenRect.Top, 0, 0, bmp.Size, CopyPixelOperation.SourceCopy);
        return bmp;
    }

    /// <summary>창 내용 직접 캡처(PrintWindow + PW_RENDERFULLCONTENT) — 브라우저 등 다른 창이
    /// 게임을 가리고 있어도 게임 화면이 찍힌다. 결과는 DWM 확장 프레임 영역으로 잘라
    /// <see cref="Capture"/>와 좌표계가 동일하다. 실패(미지원 창)면 null.</summary>
    public static Bitmap? CaptureWindow(IntPtr hWnd, Rectangle winRect, Rectangle dwmRect)
    {
        if (winRect.Width <= 0 || winRect.Height <= 0) return null;
        var full = new Bitmap(winRect.Width, winRect.Height, PixelFormat.Format32bppArgb);
        bool ok;
        using (var g = Graphics.FromImage(full))
        {
            IntPtr hdc = g.GetHdc();
            try { ok = PrintWindow(hWnd, hdc, PW_RENDERFULLCONTENT); }
            finally { g.ReleaseHdc(hdc); }
        }
        if (!ok) { full.Dispose(); return null; }

        var crop = Rectangle.Intersect(
            new Rectangle(dwmRect.X - winRect.X, dwmRect.Y - winRect.Y, dwmRect.Width, dwmRect.Height),
            new Rectangle(0, 0, full.Width, full.Height));
        if (crop.Width <= 0 || crop.Height <= 0) { full.Dispose(); return null; }
        if (crop == new Rectangle(0, 0, full.Width, full.Height)) return full;
        try { var res = full.Clone(crop, full.PixelFormat); full.Dispose(); return res; }
        catch { full.Dispose(); return null; }
    }

    /// <summary>거의 검은 화면인지(일부 DX 창은 PrintWindow가 성공을 반환하며 검은 프레임을 준다) —
    /// 픽셀을 성기게 샘플링해 판정. true면 화면 복사 폴백을 쓴다.</summary>
    public static bool IsMostlyBlack(Bitmap bmp)
    {
        var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            unsafe
            {
                int step = Math.Max(1, bmp.Width / 48);
                long lit = 0, total = 0;
                for (int y = 0; y < bmp.Height; y += step)
                {
                    byte* row = (byte*)data.Scan0 + y * data.Stride;
                    for (int x = 0; x < bmp.Width; x += step)
                    {
                        byte b = row[x * 4], g = row[x * 4 + 1], r = row[x * 4 + 2];
                        if (r > 12 || g > 12 || b > 12) lit++;
                        total++;
                    }
                }
                return total > 0 && lit * 100 / total < 2; // 밝은 픽셀 2% 미만 = 검은 프레임
            }
        }
        finally { bmp.UnlockBits(data); }
    }

    public static byte[] ToPng(Bitmap bmp)
    {
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }
}
