using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace YInput.Host;

/// <summary>
/// 디스코드 인게임 오버레이 스타일의 작은 반투명 패널 창. 안에 WebView2로 <c>/overlay.html</c>을 띄운다.
/// 위젯(<see cref="WidgetWindow"/>)과 같은 DWM 블러 + 다크 틴트 + 둥근 모서리로 반투명을 낸다(전체화면을
/// 덮지 않고 콘텐츠 크기에 맞춰 작게 유지 → 게임을 가리지 않음).
///
/// - 평소: <c>WS_EX_TRANSPARENT</c>로 입력을 완전히 무시하고 클릭이 뒤 게임 창으로 통과(click-through).
/// - 현재 포그라운드(게임) 창을 따라다니며, 창 전체 대비 비율(<c>_x</c>,<c>_y</c>) 위치에 좌상단을 맞춘다.
/// - 핸들 모드(설정 스위치): 클릭 통과를 풀고 드래그 핸들로 위치를 옮기며, 놓은 위치를 비율로 저장한다.
/// - 크기는 페이지가 <c>size:WxH</c>로 알려주면 맞춘다(콘텐츠 fit).
/// </summary>
internal sealed class OverlayWindow : Form
{
    // ---- 창 이동/클릭통과/포그라운드 추적 Win32 ----
    private const int WM_NCLBUTTONDOWN = 0xA1;
    private const int WM_EXITSIZEMOVE = 0x0232;
    private const int HTCAPTION = 0x2;
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x20;
    private const int WS_EX_TOOLWINDOW = 0x80;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOACTIVATE = 0x10;
    private const uint SWP_NOZORDER = 0x4;

    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int idx);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int idx, int val);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hWnd, int attr, out RECT val, int size);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int left, top, right, bottom; }

    // ---- 반투명(블러+틴트)·둥근 모서리: 위젯과 동일 기법 ----
    [DllImport("gdi32.dll")] private static extern IntPtr CreateRoundRectRgn(int l, int t, int r, int b, int we, int he);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr h);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int val, int size);
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;
    private static readonly bool IsWin11 = Environment.OSVersion.Version.Build >= 22000;

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy { public int AccentState; public int AccentFlags; public uint GradientColor; public int AnimationId; }
    [StructLayout(LayoutKind.Sequential)]
    private struct WinCompAttrData { public int Attribute; public IntPtr Data; public int SizeOfData; }
    [DllImport("user32.dll")] private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WinCompAttrData data);
    private const int WCA_ACCENT_POLICY = 19;
    private const int ACCENT_ENABLE_BLURBEHIND = 3;
    private const uint Tint = 0xC00A0D14; // 다크 반투명(ABGR) — 알파 0xC0으로 게임 위에서 은은하게

    private const int CornerRadius = 10;

    private readonly WebView2 _web;
    private readonly string _url;
    private readonly string _userDataFolder;
    private readonly Action<string>? _onError;
    private readonly Action<double, double>? _onDragSaved; // 핸들 드래그 후 저장된 비율(x,y)
    private readonly Action<bool>? _onWebShow;             // 페이지의 표시 의도(overlay:show/hide)
    private readonly uint _selfPid = (uint)Environment.ProcessId;

    private bool _clickThrough = true;   // 평소 true(입력 무시)
    private bool _handleMode;            // 핸들(이동) 모드
    private double _x = 0.03, _y = 0.35; // 게임 창 대비 좌상단 비율
    private int _contentW = 210, _contentH = 60;
    private RECT _lastGame;               // 마지막으로 본 게임(포그라운드) 창 bounds
    private bool _hasGame;
    private readonly System.Windows.Forms.Timer _follow;

    public OverlayWindow(string url, string userDataFolder,
        Action<string>? onError, Action<double, double>? onDragSaved, Action<bool>? onWebShow)
    {
        _url = url; _userDataFolder = userDataFolder;
        _onError = onError; _onDragSaved = onDragSaved; _onWebShow = onWebShow;

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        ClientSize = new Size(_contentW, _contentH);
        BackColor = Color.FromArgb(10, 13, 20);
        Text = "Y Input Overlay";

        _web = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.Transparent };
        Controls.Add(_web);
        InitAsync();

        _follow = new System.Windows.Forms.Timer { Interval = 250 };
        _follow.Tick += (_, _) => Reposition();
    }

    // 표시해도 포커스를 빼앗지 않음(게임이 계속 활성)
    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
            if (_clickThrough) cp.ExStyle |= WS_EX_TRANSPARENT;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try { SetBlur(); } catch { /* 블러 미지원 시 단색 배경 */ }
        UpdateRegion();
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        UpdateRegion();
        try { SetBlur(); } catch { /* 무시 */ }
    }

    // 폼 배경 미도색 → WebView2 미도달 영역까지 DWM 블러+틴트가 균일하게 보임
    protected override void OnPaintBackground(PaintEventArgs e) { /* skip */ }

    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        // 드래그 종료 시 현재 위치를 게임 대비 비율로 저장
        if (m.Msg == WM_EXITSIZEMOVE && _handleMode) SaveRatioFromPosition();
    }

    // ---------- 컨트롤러가 호출(모두 UI 스레드) ----------
    public void ShowOverlay()
    {
        if (!Visible) Show();
        _follow.Start();
        Reposition();
    }

    public void HideOverlay()
    {
        _follow.Stop();
        if (Visible) Hide();
    }

    public void SetRatios(double x, double y)
    {
        _x = Math.Clamp(x, 0, 1);
        _y = Math.Clamp(y, 0, 1);
        Reposition();
    }

    public void SetHandleMode(bool on)
    {
        if (_handleMode == on) return;
        _handleMode = on;
        SetClickThrough(!on);
        SendToWeb(on ? "handle:on" : "handle:off");
        if (on) _follow.Stop(); else _follow.Start(); // 핸들 중엔 위치 고정, 끝나면 다시 따라감
    }

    // ---------- 내부 ----------
    private void SetClickThrough(bool on)
    {
        _clickThrough = on;
        if (!IsHandleCreated) return;
        int ex = GetWindowLong(Handle, GWL_EXSTYLE);
        if (on) ex |= WS_EX_TRANSPARENT; else ex &= ~WS_EX_TRANSPARENT;
        SetWindowLong(Handle, GWL_EXSTYLE, ex);
    }

    /// <summary>현재 포그라운드(게임) 창 bounds를 잡아 캐시하고, 그 위 비율 위치/콘텐츠 크기로 창을 옮긴다.</summary>
    private void Reposition()
    {
        if (!IsHandleCreated) return;
        if (!_handleMode) TryUpdateGameBounds();
        if (!_hasGame) return;

        int gw = _lastGame.right - _lastGame.left, gh = _lastGame.bottom - _lastGame.top;
        if (gw <= 0 || gh <= 0) return;
        int x = _lastGame.left + (int)Math.Round(_x * gw);
        int y = _lastGame.top + (int)Math.Round(_y * gh);
        // 화면 밖으로 완전히 나가지 않게 살짝 보정
        x = Math.Min(x, _lastGame.right - 40);
        y = Math.Min(y, _lastGame.bottom - 30);
        SetWindowPos(Handle, HWND_TOPMOST, x, y, _contentW, _contentH, SWP_NOACTIVATE);
    }

    private void TryUpdateGameBounds()
    {
        IntPtr fg = GetForegroundWindow();
        if (fg == IntPtr.Zero || fg == Handle) return;
        GetWindowThreadProcessId(fg, out uint pid);
        if (pid == _selfPid) return; // 우리 프로세스(오버레이/위젯/설치창 등)는 제외 → 이전 앵커 유지
        if (DwmGetWindowAttribute(fg, DWMWA_EXTENDED_FRAME_BOUNDS, out var r, Marshal.SizeOf<RECT>()) != 0)
        {
            if (!GetWindowRect(fg, out r)) return;
        }
        if (r.right - r.left <= 0 || r.bottom - r.top <= 0) return;
        _lastGame = r; _hasGame = true;
    }

    private void SaveRatioFromPosition()
    {
        if (!_hasGame) return;
        int gw = _lastGame.right - _lastGame.left, gh = _lastGame.bottom - _lastGame.top;
        if (gw <= 0 || gh <= 0) return;
        if (!GetWindowRect(Handle, out var w)) return;
        double nx = Math.Clamp((w.left - _lastGame.left) / (double)gw, 0, 1);
        double ny = Math.Clamp((w.top - _lastGame.top) / (double)gh, 0, 1);
        _x = nx; _y = ny;
        _onDragSaved?.Invoke(nx, ny);
    }

    private void SendToWeb(string msg)
    {
        try { _web.CoreWebView2?.PostWebMessageAsString(msg); } catch { /* 초기화 전 무시 */ }
    }

    private void UpdateRegion()
    {
        if (Width <= 0 || Height <= 0) return;
        if (IsWin11)
        {
            Region = null;
            int pref = DWMWCP_ROUND;
            try { DwmSetWindowAttribute(Handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int)); } catch { /* 구버전 */ }
            return;
        }
        var h = CreateRoundRectRgn(0, 0, Width + 1, Height + 1, CornerRadius * 2, CornerRadius * 2);
        Region = System.Drawing.Region.FromHrgn(h);
        DeleteObject(h);
    }

    private void SetBlur()
    {
        if (!IsHandleCreated) return;
        var accent = new AccentPolicy { AccentState = ACCENT_ENABLE_BLURBEHIND, GradientColor = Tint };
        int size = Marshal.SizeOf(accent);
        IntPtr ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(accent, ptr, false);
            var data = new WinCompAttrData { Attribute = WCA_ACCENT_POLICY, Data = ptr, SizeOfData = size };
            SetWindowCompositionAttribute(Handle, ref data);
        }
        finally { Marshal.FreeHGlobal(ptr); }
    }

    private async void InitAsync()
    {
        try
        {
            var env = await CoreWebView2Environment.CreateAsync(null, _userDataFolder);
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
        if (msg == "overlay:show") _onWebShow?.Invoke(true);
        else if (msg == "overlay:hide") _onWebShow?.Invoke(false);
        else if (msg == "drag" && _handleMode && !IsDisposed) { ReleaseCapture(); SendMessage(Handle, WM_NCLBUTTONDOWN, HTCAPTION, IntPtr.Zero); }
        else if (msg.StartsWith("size:", StringComparison.Ordinal)) ApplySize(msg.AsSpan(5));
    }

    private void ApplySize(ReadOnlySpan<char> wh)
    {
        int xi = wh.IndexOf('x');
        if (xi <= 0) return;
        if (!int.TryParse(wh[..xi], out int w) || !int.TryParse(wh[(xi + 1)..], out int h)) return;
        w = Math.Clamp(w, 90, 560);
        h = Math.Clamp(h, 34, 900);
        if (w == _contentW && h == _contentH) return;
        _contentW = w; _contentH = h;
        ClientSize = new Size(w, h);
        Reposition();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { try { _follow.Dispose(); } catch { } try { _web.Dispose(); } catch { } }
        base.Dispose(disposing);
    }
}
