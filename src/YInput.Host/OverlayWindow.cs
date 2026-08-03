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
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr h);
    [DllImport("user32.dll")] private static extern bool UpdateLayeredWindow(IntPtr hWnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize, IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, int flags);

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int left, top, right, bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int x, y; public POINT(int a, int b) { x = a; y = b; } }
    [StructLayout(LayoutKind.Sequential)] private struct SIZE { public int cx, cy; public SIZE(int a, int b) { cx = a; cy = b; } }
    [StructLayout(LayoutKind.Sequential)] private struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

    private readonly uint _selfPid = (uint)Environment.ProcessId;

    private bool _armed;
    private readonly HashSet<string> _white = new(); // 표시할 프로세스명(수동 지정만)
    private readonly HashSet<string> _black = new();
    private List<OverlayRow> _rows = new();
    private Bitmap? _bmp; private bool _bmpDirty = true; // 내용 바뀔 때만 재생성, 아니면 캐시 재사용(재배치용)
    private uint _fgPidCache; private string _fgProcCache = "";
    private readonly System.Windows.Forms.Timer _poll;

    // 폰트(픽셀 단위로 DPI 영향 최소화)
    private static readonly Font NameFont = new("Segoe UI", 12.5f, FontStyle.Bold, GraphicsUnit.Pixel);
    private static readonly Font LoopFont = new("Segoe UI", 11f, FontStyle.Regular, GraphicsUnit.Pixel);
    private static readonly Font DebugTitleFont = new("Segoe UI", 10.5f, FontStyle.Bold, GraphicsUnit.Pixel);
    private static readonly Font DebugFont = new("Consolas", 11f, FontStyle.Regular, GraphicsUnit.Pixel);

    private List<string> _debug = new(); // 디버그 섹션 — 매크로가 송출한 최근 키 입력

    public OverlayWindow()
    {
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
        rows ??= new();
        if (rows.SequenceEqual(_rows)) return; // 동일 프레임 → 스킵(위치 갱신은 250ms poll이 처리)
        _rows = rows;
        _bmpDirty = true;
        if (_armed) Refresh2();
    }

    /// <summary>디버그 섹션 — 매크로가 송출한 최근 키 목록(룬 퍼즐 오입력 등 원인 추적용).</summary>
    public void SetDebugKeys(List<string> lines)
    {
        lines ??= new();
        if (lines.SequenceEqual(_debug)) return;
        _debug = lines;
        _bmpDirty = true;
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

        string proc = ForegroundProc(fg, out uint pid);
        if (pid == _selfPid) return;
        if (proc.Length == 0) { if (Visible) Hide(); return; }

        // 수동 지정(화이트리스트)한 프로세스에서만 표시. 블랙리스트는 제외.
        bool show = _white.Contains(proc) && !_black.Contains(proc);
        if (!show || _rows.Count == 0) { if (Visible) Hide(); return; }

        if (DwmGetWindowAttribute(fg, DWMWA_EXTENDED_FRAME_BOUNDS, out var r, Marshal.SizeOf<RECT>()) != 0)
        {
            if (!GetWindowRect(fg, out r)) return;
        }
        int gh = r.bottom - r.top;
        if (r.right - r.left <= 0 || gh <= 0) return;

        if (_bmpDirty || _bmp == null) { _bmp?.Dispose(); _bmp = BuildBitmap(); _bmpDirty = false; }
        int x = r.left + LeftMargin;
        int y = r.top + (gh - _bmp.Height) / 2;
        if (!Visible) Show();
        PushBitmap(_bmp, x, y);
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
        // 디버그(최근 송출 키) 패널 크기
        int dbgW = 0, dbgLineH = 0, dbgTitleH = 0, dbgH = 0;
        const int DbgPad = 10;
        if (_debug.Count > 0)
        {
            using var gd = Graphics.FromImage(probe);
            dbgTitleH = (int)Math.Ceiling(DebugTitleFont.GetHeight(gd));
            dbgLineH = (int)Math.Ceiling(DebugFont.GetHeight(gd));
            dbgW = (int)Math.Ceiling(gd.MeasureString("디버그 — 최근 송출 키", DebugTitleFont).Width);
            foreach (var l in _debug)
                dbgW = Math.Max(dbgW, (int)Math.Ceiling(gd.MeasureString(l, DebugFont).Width));
            dbgW += DbgPad * 2;
            dbgH = DbgPad * 2 + dbgTitleH + 4 + _debug.Count * dbgLineH;
        }

        int W = Math.Max(maxW, dbgW) + M * 2;
        int H = _rows.Count * pillH + Math.Max(0, _rows.Count - 1) * RowGap
                + (dbgH > 0 ? (_rows.Count > 0 ? RowGap : 0) + dbgH : 0) + M * 2;
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

        // 디버그 섹션 — 매크로가 송출한 최근 키(위=과거, 아래=최신)
        if (dbgH > 0)
        {
            var panel = new Rectangle(M, y, Math.Max(maxW, dbgW), dbgH);
            using (var path = Rounded(panel, 10))
            {
                using var fill = new SolidBrush(Color.FromArgb(165, 10, 13, 20));
                g.FillPath(fill, path);
                using var border = new Pen(Color.FromArgb(36, 255, 255, 255), 1f);
                g.DrawPath(border, path);
            }
            float dy = y + DbgPad;
            using (var titleBrush = new SolidBrush(Color.FromArgb(235, 192, 132, 252)))
                g.DrawString("디버그 — 최근 송출 키", DebugTitleFont, titleBrush, M + DbgPad, dy);
            dy += dbgTitleH + 4;
            using var lineBrush = new SolidBrush(Color.FromArgb(225, 214, 220, 230));
            foreach (var l in _debug)
            {
                g.DrawString(l, DebugFont, lineBrush, M + DbgPad, dy);
                dy += dbgLineH;
            }
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

    // ---------- 포그라운드 프로세스명 ----------
    private string ForegroundProc(IntPtr fg, out uint pid)
    {
        GetWindowThreadProcessId(fg, out pid);
        if (pid == _fgPidCache) return _fgProcCache;
        string name = "";
        try { using var p = System.Diagnostics.Process.GetProcessById((int)pid); name = p.ProcessName.ToLowerInvariant(); }
        catch { }
        _fgPidCache = pid; _fgProcCache = name;
        return name;
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
        if (disposing) { try { _poll.Dispose(); } catch { } try { _bmp?.Dispose(); } catch { } }
        base.Dispose(disposing);
    }
}
