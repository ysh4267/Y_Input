using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace YInput.Host;

/// <summary>대상 창 후보(프로세스명 + 대표 창 제목).</summary>
public sealed record OverlayWindowInfo(string Process, string Title);

/// <summary>오버레이에 그릴 한 매크로 행.</summary>
public sealed record OverlayRow(string Name, string Loop, double Outer, double Inner, bool Playing);

/// <summary>
/// 디스코드 인게임 오버레이 스타일 창 — <b>GDI+ 레이어드 창</b>(UpdateLayeredWindow, 픽셀 단위 알파).
/// 배경은 완전 투명, 개별 pill(2중 원 + 이름/루프)만 보이고 그 사이는 게임이 그대로 비친다.
/// <c>WS_EX_TRANSPARENT|WS_EX_LAYERED</c>로 <b>클릭·마우스를 완전히 무시</b>(뒤 게임으로 통과)한다.
///
/// 표시 조건: (1) 무장(<see cref="SetArmed"/>: 활성화 매크로 있음) &amp;&amp; (2) 대상 창(화이트리스트 or 자동감지 게임,
/// 블랙 제외)이 포그라운드일 때. 그 창의 왼쪽 중앙에 뜬다. 게임 자동감지 시 그 프로세스를 보고(자동 화이트 추가).
/// </summary>
internal sealed class OverlayWindow : Form
{
    private const int WS_EX_TRANSPARENT = 0x20;
    private const int WS_EX_TOOLWINDOW = 0x80;
    private const int WS_EX_LAYERED = 0x00080000;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    private const int GWL_STYLE = -16;
    private const int WS_CAPTION = 0x00C00000;
    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const int LeftMargin = 8;
    private const byte AC_SRC_OVER = 0, AC_SRC_ALPHA = 1;
    private const int ULW_ALPHA = 2;

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hWnd, int attr, out RECT val, int size);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, StringBuilder s, int max);
    [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int idx);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr hMon, ref MONITORINFO mi);
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr h);
    [DllImport("user32.dll")] private static extern bool UpdateLayeredWindow(IntPtr hWnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize, IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, int flags);

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int left, top, right, bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int x, y; public POINT(int a, int b) { x = a; y = b; } }
    [StructLayout(LayoutKind.Sequential)] private struct SIZE { public int cx, cy; public SIZE(int a, int b) { cx = a; cy = b; } }
    [StructLayout(LayoutKind.Sequential)] private struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

    private readonly Action<string>? _onGameDetected;
    private readonly uint _selfPid = (uint)Environment.ProcessId;

    private bool _armed;
    private readonly HashSet<string> _white = new();
    private readonly HashSet<string> _black = new();
    private List<OverlayRow> _rows = new();
    private uint _fgPidCache; private string _fgProcCache = "";
    private uint _fgGamePid; private bool _fgGameCache;
    private string _lastGameReport = "";
    private readonly System.Windows.Forms.Timer _poll;

    // 폰트(픽셀 단위로 DPI 영향 최소화)
    private static readonly Font NameFont = new("Segoe UI", 12.5f, FontStyle.Bold, GraphicsUnit.Pixel);
    private static readonly Font LoopFont = new("Segoe UI", 11f, FontStyle.Regular, GraphicsUnit.Pixel);

    public OverlayWindow(Action<string>? onGameDetected)
    {
        _onGameDetected = onGameDetected;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        Text = "Y Input Overlay";
        _poll = new System.Windows.Forms.Timer { Interval = 250 };
        _poll.Tick += (_, _) => Refresh2();
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_LAYERED | WS_EX_TRANSPARENT;
            return cp;
        }
    }

    // ---------- 컨트롤러가 호출(모두 UI 스레드) ----------
    public void SetArmed(bool armed)
    {
        if (_armed == armed) return;
        _armed = armed;
        if (armed) { _poll.Start(); Refresh2(); }
        else { _poll.Stop(); if (Visible) Hide(); }
    }

    public void SetRows(List<OverlayRow> rows)
    {
        _rows = rows ?? new();
        if (_armed) Refresh2();
    }

    public void SetLists(IEnumerable<string> white, IEnumerable<string> black)
    {
        _white.Clear(); foreach (var w in white) { var n = Normalize(w); if (n.Length > 0) _white.Add(n); }
        _black.Clear(); foreach (var b in black) { var n = Normalize(b); if (n.Length > 0) _black.Add(n); }
        if (_armed) Refresh2();
    }

    // ---------- 표시 판단 + 렌더 ----------
    private void Refresh2()
    {
        if (!IsHandleCreated || !_armed) return;
        IntPtr fg = GetForegroundWindow();
        if (fg == IntPtr.Zero || fg == Handle) return;

        var info = ForegroundInfo(fg, out uint pid);
        if (pid == _selfPid) return;
        string proc = info.proc;
        if (proc.Length == 0) { if (Visible) Hide(); return; }

        bool show;
        if (_black.Contains(proc)) show = false;
        else if (_white.Contains(proc)) show = true;
        else if (info.isGame) { show = true; if (proc != _lastGameReport) { _lastGameReport = proc; _onGameDetected?.Invoke(proc); } }
        else show = false;

        if (!show || _rows.Count == 0) { if (Visible) Hide(); return; }

        if (DwmGetWindowAttribute(fg, DWMWA_EXTENDED_FRAME_BOUNDS, out var r, Marshal.SizeOf<RECT>()) != 0)
        {
            if (!GetWindowRect(fg, out r)) return;
        }
        int gh = r.bottom - r.top;
        if (r.right - r.left <= 0 || gh <= 0) return;

        using var bmp = BuildBitmap();
        int x = r.left + LeftMargin;
        int y = r.top + (gh - bmp.Height) / 2;
        if (!Visible) Show();
        PushBitmap(bmp, x, y);
    }

    private void PushBitmap(Bitmap bmp, int x, int y)
    {
        IntPtr screen = GetDC(IntPtr.Zero);
        IntPtr mem = CreateCompatibleDC(screen);
        IntPtr hbmp = bmp.GetHbitmap(Color.FromArgb(0));
        IntPtr old = SelectObject(mem, hbmp);
        var size = new SIZE(bmp.Width, bmp.Height);
        var src = new POINT(0, 0);
        var dst = new POINT(x, y);
        var blend = new BLENDFUNCTION { BlendOp = AC_SRC_OVER, BlendFlags = 0, SourceConstantAlpha = 255, AlphaFormat = AC_SRC_ALPHA };
        UpdateLayeredWindow(Handle, screen, ref dst, ref size, mem, ref src, 0, ref blend, ULW_ALPHA);
        SelectObject(mem, old);
        DeleteObject(hbmp);
        DeleteDC(mem);
        ReleaseDC(IntPtr.Zero, screen);
    }

    // ---------- 레이아웃/그리기 ----------
    private const int M = 3;            // 바깥 여백
    private const int RingD = 30, RingStroke = 3, InnerStroke = 3;
    private const int PadL = 6, PadR = 13, PadV = 6, Gap = 9, RowGap = 7;

    private Bitmap BuildBitmap()
    {
        using var probe = new Bitmap(1, 1);
        using (var pg = Graphics.FromImage(probe)) { pg.TextRenderingHint = TextRenderingHint.AntiAlias; }
        int pillH = RingD + PadV * 2;

        // 각 행 폭 계산
        var widths = new int[_rows.Count];
        int maxW = 0;
        using (var g0 = Graphics.FromImage(probe))
        {
            foreach (var (row, i) in _rows.Select((r, i) => (r, i)))
            {
                int tw = (int)Math.Ceiling(Math.Max(
                    g0.MeasureString(row.Name, NameFont).Width,
                    g0.MeasureString(row.Loop, LoopFont).Width));
                int w = PadL + RingD + Gap + tw + PadR;
                widths[i] = w; if (w > maxW) maxW = w;
            }
        }
        int W = maxW + M * 2;
        int H = _rows.Count * pillH + Math.Max(0, _rows.Count - 1) * RowGap + M * 2;
        W = Math.Max(W, 40); H = Math.Max(H, 40);

        var bmp = new Bitmap(W, H, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAlias;
        g.Clear(Color.Transparent);

        int y = M;
        for (int i = 0; i < _rows.Count; i++)
        {
            var row = _rows[i];
            bool on = row.Playing;
            var pill = new Rectangle(M, y, widths[i], pillH);
            using (var path = Rounded(pill, pillH / 2))
            {
                using var fill = new SolidBrush(Color.FromArgb(on ? 175 : 120, 10, 13, 20));
                g.FillPath(fill, path);
                using var border = new Pen(Color.FromArgb(on ? 46 : 30, 255, 255, 255), 1f);
                g.DrawPath(border, path);
            }
            int cx = M + PadL + RingD / 2;
            int cy = y + pillH / 2;
            DrawRing(g, cx, cy, RingD / 2, RingStroke, Color.FromArgb(52, 211, 153), row.Outer, on);           // 외부 = 전체 진행
            DrawRing(g, cx, cy, RingD / 2 - 5, InnerStroke, Color.FromArgb(79, 140, 255), row.Inner, on);       // 내부 = 딜레이 진행

            int tx = M + PadL + RingD + Gap;
            float nameH = NameFont.GetHeight(g), loopH = LoopFont.GetHeight(g);
            float th = nameH + loopH;
            float ty = y + (pillH - th) / 2f;
            using var nameBrush = new SolidBrush(Color.FromArgb(on ? 255 : 190, 238, 242, 248));
            using var loopBrush = new SolidBrush(Color.FromArgb(on ? 235 : 160, 181, 190, 205));
            g.DrawString(row.Name, NameFont, nameBrush, tx, ty);
            g.DrawString(row.Loop, LoopFont, loopBrush, tx, ty + nameH);

            y += pillH + RowGap;
        }
        return bmp;
    }

    private static void DrawRing(Graphics g, int cx, int cy, int r, int stroke, Color prog, double frac, bool on)
    {
        var rect = new Rectangle(cx - r, cy - r, r * 2, r * 2);
        using (var track = new Pen(Color.FromArgb(on ? 46 : 34, 255, 255, 255), stroke) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            g.DrawEllipse(track, rect);
        double f = Math.Max(0, Math.Min(1, frac));
        if (f > 0.001)
        {
            using var pen = new Pen(Color.FromArgb(on ? 255 : 150, prog), stroke) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawArc(pen, rect, -90f, (float)(f * 360));
        }
    }

    private static GraphicsPath Rounded(Rectangle r, int rad)
    {
        int d = rad * 2;
        var p = new GraphicsPath();
        if (d <= 0) { p.AddRectangle(r); return p; }
        d = Math.Min(d, Math.Min(r.Width, r.Height));
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    // ---------- 포그라운드/게임 판정 ----------
    private (string proc, bool isGame) ForegroundInfo(IntPtr fg, out uint pid)
    {
        GetWindowThreadProcessId(fg, out pid);
        if (pid == _fgPidCache && pid == _fgGamePid) return (_fgProcCache, _fgGameCache);
        string name = "";
        try { using var p = System.Diagnostics.Process.GetProcessById((int)pid); name = p.ProcessName.ToLowerInvariant(); }
        catch { }
        bool isGame = name.Length > 0 && !NonGame.Contains(name) && (IsFullscreen(fg) || HasGraphicsModule(pid));
        _fgPidCache = pid; _fgProcCache = name;
        _fgGamePid = pid; _fgGameCache = isGame;
        return (name, isGame);
    }

    private static bool IsFullscreen(IntPtr hWnd)
    {
        if (!GetWindowRect(hWnd, out var wr)) return false;
        IntPtr mon = MonitorFromWindow(hWnd, MONITOR_DEFAULTTONEAREST);
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(mon, ref mi)) return false;
        var m = mi.rcMonitor;
        bool covers = wr.left <= m.left && wr.top <= m.top && wr.right >= m.right && wr.bottom >= m.bottom;
        if (!covers) return false;
        return (GetWindowLong(hWnd, GWL_STYLE) & WS_CAPTION) != WS_CAPTION;
    }

    private static readonly HashSet<string> NonGame = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome", "msedge", "firefox", "opera", "opera_gx", "brave", "whale", "vivaldi", "iexplore",
        "code", "electron", "discord", "slack", "teams", "msteams", "spotify", "notion", "obsidian",
        "explorer", "dwm", "searchapp", "searchhost", "startmenuexperiencehost", "shellexperiencehost",
        "textinputhost", "applicationframehost", "systemsettings", "taskmgr", "mmc", "sihost", "ctfmon",
        "powershell", "pwsh", "cmd", "windowsterminal", "conhost", "notepad", "notepad++", "sublime_text",
        "devenv", "rider64", "idea64", "pycharm64", "webstorm64", "yinput", "widgets", "lockapp", "logonui",
        "nvcontainer", "onedrive", "acrobat", "acrord32", "winword", "excel", "powerpnt", "outlook",
    };
    private static readonly string[] GfxDlls = { "d3d9.dll", "d3d10.dll", "d3d11.dll", "d3d12.dll", "dxgi.dll", "opengl32.dll", "vulkan-1.dll", "d3d8.dll" };
    private static bool HasGraphicsModule(uint pid)
    {
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById((int)pid);
            foreach (System.Diagnostics.ProcessModule m in p.Modules)
            {
                var n = m.ModuleName?.ToLowerInvariant() ?? "";
                if (Array.IndexOf(GfxDlls, n) >= 0) return true;
            }
        }
        catch { }
        return false;
    }

    private static string Normalize(string? t)
    {
        t = (t ?? "").Trim().ToLowerInvariant();
        if (t.EndsWith(".exe", StringComparison.Ordinal)) t = t[..^4];
        return t;
    }

    // ---------- 대상 창 후보 열거(설정 UI용) ----------
    public static List<OverlayWindowInfo> EnumerateWindows()
    {
        uint self = (uint)Environment.ProcessId;
        var byProc = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h)) return true;
            int len = GetWindowTextLength(h);
            if (len <= 0) return true;
            var sb = new StringBuilder(len + 1);
            GetWindowText(h, sb, sb.Capacity);
            string title = sb.ToString().Trim();
            if (title.Length == 0) return true;
            GetWindowThreadProcessId(h, out uint pid);
            if (pid == self) return true;
            string proc = "";
            try { using var p = System.Diagnostics.Process.GetProcessById((int)pid); proc = p.ProcessName; } catch { return true; }
            if (proc.Length == 0) return true;
            if (!byProc.TryGetValue(proc, out var cur) || title.Length > cur.Length) byProc[proc] = title;
            return true;
        }, IntPtr.Zero);
        return byProc.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                     .Select(kv => new OverlayWindowInfo(kv.Key, kv.Value)).ToList();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { try { _poll.Dispose(); } catch { } }
        base.Dispose(disposing);
    }
}
