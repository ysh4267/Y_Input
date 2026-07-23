using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace YInput.Host;

/// <summary>
/// 디스코드 인게임 오버레이 스타일의 창. 안에 WebView2로 <c>/overlay.html</c>을 띄운다.
/// 배경은 완전 투명(per-pixel alpha): 페이지가 그린 개별 pill만 보이고 그 사이는 게임이 그대로 비친다.
/// <c>WS_EX_LAYERED</c>(WebView2 DirectComposition 합성) + WebView2 <c>DefaultBackgroundColor=Transparent</c>로
/// 투명을, <c>WS_EX_TRANSPARENT</c>로 클릭 통과(입력 완전 무시)를 낸다.
///
/// 현재 포그라운드(게임) 창을 따라다니며 그 창의 <b>왼쪽 중앙</b>에 고정으로 뜬다.
/// 크기는 페이지가 <c>size:WxH</c>로 알려주면 맞춘다(콘텐츠 fit).
/// </summary>
internal sealed class OverlayWindow : Form
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x20;
    private const int WS_EX_TOOLWINDOW = 0x80;
    private const int WS_EX_LAYERED = 0x00080000;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOACTIVATE = 0x10;
    private const int LeftMargin = 8; // 게임 창 왼쪽에서 살짝 띄움(px)

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hWnd, int attr, out RECT val, int size);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int left, top, right, bottom; }

    private readonly WebView2 _web;
    private readonly string _url;
    private readonly string _userDataFolder;
    private readonly Action<string>? _onError;
    private readonly Action<bool>? _onWebShow;             // 페이지의 표시 의도(overlay:show/hide)
    private readonly uint _selfPid = (uint)Environment.ProcessId;

    private int _contentW = 210, _contentH = 60;
    private RECT _lastGame;               // 마지막으로 본 게임(포그라운드) 창 bounds
    private bool _hasGame;
    private readonly System.Windows.Forms.Timer _follow;

    public OverlayWindow(string url, string userDataFolder, Action<string>? onError, Action<bool>? onWebShow)
    {
        _url = url; _userDataFolder = userDataFolder;
        _onError = onError; _onWebShow = onWebShow;

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        ClientSize = new Size(_contentW, _contentH);
        BackColor = Color.Black; // 칠하지 않음(OnPaintBackground skip) — 레이어드+WebView2 투명이 실제 표시
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
            // 항상 클릭 통과(입력 완전 무시) + 툴윈도우 + 비활성 + 레이어드(투명 합성)
            cp.ExStyle |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_LAYERED | WS_EX_TRANSPARENT;
            return cp;
        }
    }

    // 폼 배경 미도색 → WebView2가 투명하게 둔 영역이 그대로 뒤(게임)로 뚫림
    protected override void OnPaintBackground(PaintEventArgs e) { /* skip */ }

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

    // ---------- 내부 ----------
    /// <summary>현재 포그라운드(게임) 창 bounds를 잡아 그 창의 왼쪽 중앙에 창을 맞춘다.</summary>
    private void Reposition()
    {
        if (!IsHandleCreated) return;
        TryUpdateGameBounds();
        if (!_hasGame) return;

        int gh = _lastGame.bottom - _lastGame.top;
        if (gh <= 0) return;
        int x = _lastGame.left + LeftMargin;
        int y = _lastGame.top + (gh - _contentH) / 2; // 세로 중앙
        SetWindowPos(Handle, HWND_TOPMOST, x, y, _contentW, _contentH, SWP_NOACTIVATE);
    }

    private void TryUpdateGameBounds()
    {
        IntPtr fg = GetForegroundWindow();
        if (fg == IntPtr.Zero || fg == Handle) return;
        GetWindowThreadProcessId(fg, out uint pid);
        if (pid == _selfPid) return; // 우리 프로세스 창은 제외 → 이전 앵커 유지
        if (DwmGetWindowAttribute(fg, DWMWA_EXTENDED_FRAME_BOUNDS, out var r, Marshal.SizeOf<RECT>()) != 0)
        {
            if (!GetWindowRect(fg, out r)) return;
        }
        if (r.right - r.left <= 0 || r.bottom - r.top <= 0) return;
        _lastGame = r; _hasGame = true;
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
        Reposition();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { try { _follow.Dispose(); } catch { } try { _web.Dispose(); } catch { } }
        base.Dispose(disposing);
    }
}
