using System.Runtime.InteropServices;
using System.Text;

namespace YInput.Host;

/// <summary>
/// 기본 브라우저에 열린 웹 UI(창 제목에 "Y Input" 포함) 창을 찾아 앞으로 가져온다(단일 개체 + 포커스).
/// 우리 프로세스(위젯·트레이) 창은 제외한다. 단, Y Input이 여러 탭 중 <b>백그라운드 탭</b>이면 창 제목에
/// 안 잡혀(활성 탭 제목만 보임) 못 올릴 수 있다 — 이는 외부에서 특정 브라우저 탭을 전환할 수 없는 제약.
/// </summary>
internal static class BrowserFocus
{
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr h, StringBuilder s, int max);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr h);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr h);
    private const int SW_RESTORE = 9;

    /// <summary>제목에 <paramref name="titleContains"/>가 들어간(우리 프로세스가 아닌) 창을 찾아 앞으로 가져온다.
    /// 찾아서 올렸으면 true. (공백 포함 "Y Input" — 폴더명 "Y_Input" 같은 건 안 걸리게.)</summary>
    public static bool BringToFront(string titleContains = "Y Input")
    {
        uint self = (uint)Environment.ProcessId;
        IntPtr found = IntPtr.Zero;
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h)) return true;
            GetWindowThreadProcessId(h, out var pid);
            if (pid == self) return true;                 // 우리 위젯/트레이 창 제외
            int len = GetWindowTextLength(h);
            if (len <= 0) return true;
            var sb = new StringBuilder(len + 1);
            GetWindowText(h, sb, sb.Capacity);
            if (sb.ToString().IndexOf(titleContains, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                found = h; return false;                  // 첫 매칭에서 열거 중단
            }
            return true;
        }, IntPtr.Zero);

        if (found == IntPtr.Zero) return false;
        try
        {
            if (IsIconic(found)) ShowWindow(found, SW_RESTORE); // 최소화돼 있으면 복원
            BringWindowToTop(found);
            SetForegroundWindow(found);                          // 위젯 더블클릭 직후라 포그라운드 권한이 있어 대개 성공
            return true;
        }
        catch { return false; }
    }
}
