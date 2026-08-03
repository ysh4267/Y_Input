using System.Drawing;
using System.Runtime.InteropServices;

namespace YInput.Host.Vision;

/// <summary>
/// 프로세스명으로 대상 창을 찾아 화면 좌표 rect를 얻는다. 위치 지킴이가 캡처 영역과
/// 보정 안전 게이트(포그라운드 확인)에 사용한다. 브라우저에서 버튼을 누르는 순간에는
/// 게임이 포그라운드가 아니므로 EnumWindows로 프로세스명 매칭해 찾는다(오버레이의
/// GetForegroundWindow 방식과 다른 이유).
/// </summary>
internal static class WindowLocator
{
    private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hWnd, int attr, out RECT val, int size);
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int left, top, right, bottom; }

    /// <summary>프로세스명(".exe" 유무 무관, 대소문자 무시)의 보이는 최상위 창 중 가장 큰 것의
    /// DWM 확장 프레임 rect(화면 좌표). 최소화된 창은 제외. 없으면 false.</summary>
    public static bool TryGetWindowRect(string process, out Rectangle rect)
    {
        rect = Rectangle.Empty;
        var target = Normalize(process);
        if (target.Length == 0) return false;

        var pidCache = new Dictionary<uint, string>();
        Rectangle best = Rectangle.Empty;
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h) || IsIconic(h)) return true;
            GetWindowThreadProcessId(h, out uint pid);
            if (!pidCache.TryGetValue(pid, out var name))
            {
                try { using var p = System.Diagnostics.Process.GetProcessById((int)pid); name = p.ProcessName.ToLowerInvariant(); }
                catch { name = ""; }
                pidCache[pid] = name;
            }
            if (name != target) return true;
            if (!TryRect(h, out var r)) return true;
            if ((long)r.Width * r.Height > (long)best.Width * best.Height) best = r;
            return true;
        }, IntPtr.Zero);

        if (best.Width <= 0 || best.Height <= 0) return false;
        rect = best;
        return true;
    }

    /// <summary>대상 프로세스가 현재 포그라운드인가 — 보정 직전 안전 게이트.
    /// Interception은 전역 송출이라 다른 앱에 방향키가 들어가는 사고를 막는다.</summary>
    public static bool IsForeground(string process)
    {
        var target = Normalize(process);
        if (target.Length == 0) return false;
        IntPtr fg = GetForegroundWindow();
        if (fg == IntPtr.Zero) return false;
        GetWindowThreadProcessId(fg, out uint pid);
        try { using var p = System.Diagnostics.Process.GetProcessById((int)pid); return p.ProcessName.ToLowerInvariant() == target; }
        catch { return false; }
    }

    private static bool TryRect(IntPtr hWnd, out Rectangle rect)
    {
        if (DwmGetWindowAttribute(hWnd, DWMWA_EXTENDED_FRAME_BOUNDS, out var r, Marshal.SizeOf<RECT>()) != 0)
        {
            if (!GetWindowRect(hWnd, out r)) { rect = Rectangle.Empty; return false; }
        }
        rect = Rectangle.FromLTRB(r.left, r.top, r.right, r.bottom);
        return rect.Width > 0 && rect.Height > 0;
    }

    private static string Normalize(string? t)
    {
        t = (t ?? "").Trim().ToLowerInvariant();
        if (t.EndsWith(".exe", StringComparison.Ordinal)) t = t[..^4];
        return t;
    }
}
