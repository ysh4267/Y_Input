using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace YInput.Host;

/// <summary>대상 창 후보(프로세스명 + 대표 창 제목).</summary>
public sealed record OverlayWindowInfo(string Process, string Title);

/// <summary>
/// 디스코드 인게임 오버레이 스타일의 창. 안에 WebView2로 <c>/overlay.html</c>을 띄운다.
/// 레이어드 창 + 컬러키(마젠타)로 투명을 낸다: 페이지가 마젠타로 둔 영역은 투명 + <b>클릭/마우스 완전 통과</b>,
/// pill(불투명)만 보인다. <c>WS_EX_TRANSPARENT</c>까지 더해 창 전체가 마우스를 인식하지 않는다.
///
/// 표시 조건: (1) 웹이 무장(<c>SetArmed</c>: 활성화 매크로 있음) &amp;&amp; (2) 지정한 대상 창(프로세스)이 포그라운드일 때.
/// 대상이 비어 있으면 아무 포그라운드 창(우리 프로세스 제외)의 왼쪽 중앙에 뜬다.
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
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOACTIVATE = 0x10;
    private const int LeftMargin = 8; // 대상 창 왼쪽에서 살짝 띄움(px)
    private const uint LWA_COLORKEY = 0x1;
    private const uint ColorKey = 0x00FF00FF;              // COLORREF 마젠타(0x00bbggrr)
    private static readonly Color KeyColor = Color.Magenta; // 이 색 픽셀 = 투명 + 클릭통과

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool SetLayeredWindowAttributes(IntPtr hWnd, uint crKey, byte alpha, uint flags);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hWnd, int attr, out RECT val, int size);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, StringBuilder s, int max);
    [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int idx);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr hMon, ref MONITORINFO mi);
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int left, top, right, bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }

    private readonly WebView2 _web;
    private readonly string _url;
    private readonly string _userDataFolder;
    private readonly Action<string>? _onError;
    private readonly Action<string>? _onGameDetected; // auto 모드에서 감지한 게임 프로세스명 보고
    private readonly uint _selfPid = (uint)Environment.ProcessId;

    private bool _armed;                  // 웹이 표시를 원함(활성화 매크로 있음)
    private readonly HashSet<string> _white = new(); // 표시할 프로세스명(소문자)
    private readonly HashSet<string> _black = new(); // 제외할 프로세스명(소문자)
    private int _contentW = 210, _contentH = 60;
    private uint _fgPidCache; private string _fgProcCache = "";
    private string _lastGameReport = "";
    private readonly System.Windows.Forms.Timer _poll;

    public OverlayWindow(string url, string userDataFolder, Action<string>? onError, Action<string>? onGameDetected)
    {
        _url = url; _userDataFolder = userDataFolder; _onError = onError; _onGameDetected = onGameDetected;

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        ClientSize = new Size(_contentW, _contentH);
        BackColor = KeyColor; // 컬러키 = 창 배경(가려지지 않은 곳은 투명)
        Text = "Y Input Overlay";

        _web = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = KeyColor }; // 투명 대신 키색 → 컬러키로 통과
        Controls.Add(_web);
        InitAsync();

        _poll = new System.Windows.Forms.Timer { Interval = 250 };
        _poll.Tick += (_, _) => Evaluate();
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            // 레이어드(컬러키 투명) + 클릭/마우스 완전 통과 + 툴윈도우 + 비활성
            cp.ExStyle |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_LAYERED | WS_EX_TRANSPARENT;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // 마젠타 픽셀을 투명+클릭통과로. (레이어드 창을 보이게 만드는 호출이기도 함)
        try { SetLayeredWindowAttributes(Handle, ColorKey, 0, LWA_COLORKEY); } catch { /* 무시 */ }
    }

    // ---------- 컨트롤러가 호출(모두 UI 스레드) ----------
    public void SetArmed(bool armed)
    {
        if (_armed == armed) return;
        _armed = armed;
        if (armed) { _poll.Start(); Evaluate(); }
        else { _poll.Stop(); if (Visible) Hide(); }
    }

    /// <summary>표시(화이트)·제외(블랙) 프로세스 목록을 갱신한다.</summary>
    public void SetLists(IEnumerable<string> white, IEnumerable<string> black)
    {
        _white.Clear(); foreach (var w in white) { var n = Normalize(w); if (n.Length > 0) _white.Add(n); }
        _black.Clear(); foreach (var b in black) { var n = Normalize(b); if (n.Length > 0) _black.Add(n); }
        if (_armed) Evaluate();
    }

    /// <summary>포그라운드 창의 프로세스가 화이트리스트(또는 자동 감지된 게임)면 그 창 왼쪽 중앙에 표시, 아니면 숨김.</summary>
    private void Evaluate()
    {
        if (!IsHandleCreated || !_armed) return;
        IntPtr fg = GetForegroundWindow();
        if (fg == IntPtr.Zero) { if (Visible) Hide(); return; }
        if (fg == Handle) return; // 우리 창이 포그라운드면 상태 유지

        var info = ForegroundInfo(fg, out uint pid);
        if (pid == _selfPid) return; // 우리 프로세스 창 → 상태 유지
        string proc = info.proc;
        if (proc.Length == 0) { if (Visible) Hide(); return; }

        bool show;
        if (_black.Contains(proc)) show = false;              // 제외됨
        else if (_white.Contains(proc)) show = true;          // 표시 목록
        else if (info.isGame)                                 // 미지정인데 게임 같으면 → 화이트리스트에 자동 추가
        {
            show = true;
            if (proc != _lastGameReport) { _lastGameReport = proc; _onGameDetected?.Invoke(proc); }
        }
        else show = false;

        if (!show) { if (Visible) Hide(); return; }

        if (DwmGetWindowAttribute(fg, DWMWA_EXTENDED_FRAME_BOUNDS, out var r, Marshal.SizeOf<RECT>()) != 0)
        {
            if (!GetWindowRect(fg, out r)) return;
        }
        int gh = r.bottom - r.top;
        if (r.right - r.left <= 0 || gh <= 0) return;
        int x = r.left + LeftMargin;
        int y = r.top + (gh - _contentH) / 2;
        if (!Visible) Show();
        SetWindowPos(Handle, HWND_TOPMOST, x, y, _contentW, _contentH, SWP_NOACTIVATE);
    }

    // 포그라운드 프로세스명 + 게임 여부. pid가 바뀔 때만 계산해 캐시(모듈 검사 비용 절감).
    private uint _fgIsGamePid; private bool _fgIsGameCache;
    private (string proc, bool isGame) ForegroundInfo(IntPtr fg, out uint pid)
    {
        GetWindowThreadProcessId(fg, out pid);
        if (pid == _fgPidCache && pid == _fgIsGamePid) return (_fgProcCache, _fgIsGameCache);
        string name = "";
        try { using var p = System.Diagnostics.Process.GetProcessById((int)pid); name = p.ProcessName.ToLowerInvariant(); }
        catch { /* 접근 불가 등 */ }
        bool isGame = name.Length > 0 && !NonGame.Contains(name) && (IsFullscreen(fg) || HasGraphicsModule(pid));
        _fgPidCache = pid; _fgProcCache = name;
        _fgIsGamePid = pid; _fgIsGameCache = isGame;
        return (name, isGame);
    }

    // 풀스크린/보더리스: 캡션 없이 모니터 전체를 덮음.
    private static bool IsFullscreen(IntPtr hWnd)
    {
        if (!GetWindowRect(hWnd, out var wr)) return false;
        IntPtr mon = MonitorFromWindow(hWnd, MONITOR_DEFAULTTONEAREST);
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(mon, ref mi)) return false;
        var m = mi.rcMonitor;
        bool coversMonitor = wr.left <= m.left && wr.top <= m.top && wr.right >= m.right && wr.bottom >= m.bottom;
        if (!coversMonitor) return false;
        int style = GetWindowLong(hWnd, GWL_STYLE);
        return (style & WS_CAPTION) != WS_CAPTION; // 캡션 없음 = 보더리스/풀스크린
    }

    // 게임이 아닌 흔한 GPU 가속 앱(브라우저·Electron·셸 등) — 그래픽 DLL을 써도 게임으로 보지 않음.
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

    // 창모드 게임 감지: 대상 프로세스가 그래픽 API DLL을 로드했는지(로드 모듈 검사).
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
        catch { /* 32/64 불일치·접근 불가 → 풀스크린 판정으로만 */ }
        return false;
    }

    private static string Normalize(string? t)
    {
        t = (t ?? "").Trim().ToLowerInvariant();
        if (t.EndsWith(".exe", StringComparison.Ordinal)) t = t[..^4];
        return t;
    }

    private async void InitAsync()
    {
        try
        {
            // --disable-gpu: GPU/DirectComposition 대신 소프트웨어 합성 → 레이어드 창 컬러키(마젠타 투명)가 먹게 함.
            var opts = new CoreWebView2EnvironmentOptions { AdditionalBrowserArguments = "--disable-gpu" };
            var env = await CoreWebView2Environment.CreateAsync(null, _userDataFolder, opts);
            await _web.EnsureCoreWebView2Async(env);
            var s = _web.CoreWebView2.Settings;
            s.AreDefaultContextMenusEnabled = false;
            s.IsZoomControlEnabled = false;
            s.AreBrowserAcceleratorKeysEnabled = false;
            s.IsStatusBarEnabled = false;
            s.AreDevToolsEnabled = false;
            _web.CoreWebView2.WebMessageReceived += OnWebMessage;
            _web.CoreWebView2.Navigate(_url);
        }
        catch (Exception ex)
        {
            _onError?.Invoke("오버레이 WebView2 초기화 실패(런타임 확인): " + ex.Message);
        }
    }

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string msg; try { msg = e.TryGetWebMessageAsString() ?? ""; } catch { return; }
        if (msg == "overlay:show") SetArmed(true);
        else if (msg == "overlay:hide") SetArmed(false);
        else if (msg.StartsWith("size:", StringComparison.Ordinal)) ApplySize(msg.AsSpan(5));
    }

    private void ApplySize(ReadOnlySpan<char> wh)
    {
        int xi = wh.IndexOf('x');
        if (xi <= 0) return;
        if (!int.TryParse(wh[..xi], out int w) || !int.TryParse(wh[(xi + 1)..], out int h)) return;
        w = Math.Clamp(w, 90, 560);
        h = Math.Clamp(h, 34, 1000);
        if (w == _contentW && h == _contentH) return;
        _contentW = w; _contentH = h;
        ClientSize = new Size(w, h);
        if (_armed) Evaluate();
    }

    // ---------- 대상 창 후보 열거(설정 UI용) ----------
    public static List<OverlayWindowInfo> EnumerateWindows()
    {
        uint self = (uint)Environment.ProcessId;
        var byProc = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // proc -> 대표 제목(가장 긴)
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
        if (disposing) { try { _poll.Dispose(); } catch { } try { _web.Dispose(); } catch { } }
        base.Dispose(disposing);
    }
}
