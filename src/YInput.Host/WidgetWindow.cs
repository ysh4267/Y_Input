using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace YInput.Host;

/// <summary>
/// 보더리스·항상 위·작업표시줄 미표시 위젯 창(둥근 모서리 + 아크릴 반투명 블러). 안에 WebView2로
/// <c>/widget.html?id=…</c>를 띄운다. 높이는 고정, 폭만 조절. 페이지가 상태(대기/켜짐/재생)에 맞는
/// 배경 틴트를 <c>window.chrome.webview.postMessage('tint:AABBGGRR')</c>로 보내면 창의 아크릴 색이 바뀐다.
/// 이동은 'drag', 크기조절(폭)은 'resize', 닫기는 'close' 메시지.
/// </summary>
internal sealed class WidgetWindow : Form
{
    private const int WM_NCLBUTTONDOWN = 0xA1;
    private const int HTCAPTION = 0x2;
    private const int HTRIGHT = 11;
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("gdi32.dll")] private static extern IntPtr CreateRoundRectRgn(int l, int t, int r, int b, int we, int he);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr h);

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy { public int AccentState; public int AccentFlags; public uint GradientColor; public int AnimationId; }
    [StructLayout(LayoutKind.Sequential)]
    private struct WinCompAttrData { public int Attribute; public IntPtr Data; public int SizeOfData; }
    [DllImport("user32.dll")] private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WinCompAttrData data);
    private const int WCA_ACCENT_POLICY = 19;
    private const int ACCENT_DISABLED = 0;
    private const int ACCENT_ENABLE_BLURBEHIND = 3; // 아크릴(4)은 이동/크기조절 시 심한 지연 → 가벼운 블러(3, 상시)
    private const int WM_EXITSIZEMOVE = 0x0232;

    private const int FixedHeight = 104;
    private const int CornerRadius = 9;
    private static readonly Color Bg = Color.FromArgb(21, 25, 33);

    private readonly WebView2 _web;
    private readonly string _url;
    private readonly string _userDataFolder;
    private readonly Action<string>? _onError;
    public string MacroId { get; }

    public WidgetWindow(string macroId, string url, string userDataFolder, Point location, Action<string>? onError = null)
    {
        MacroId = macroId; _url = url; _userDataFolder = userDataFolder; _onError = onError;

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        Location = location;
        ClientSize = new Size(340, FixedHeight);
        MinimumSize = new Size(180, Height);   // 높이 고정(최소=최대), 폭만 조절
        MaximumSize = new Size(1400, Height);
        BackColor = Bg;
        Text = "Y Input Widget";

        _web = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.Transparent };
        Controls.Add(_web);
        InitAsync();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try { SetBlur(true); } catch { /* 블러 미지원 시 무시(단색 배경으로 동작) */ }
        UpdateRegion();
    }

    // 크기가 바뀔 때마다 둥근 영역 갱신 + 블러 재적용.
    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        UpdateRegion();
        try { SetBlur(true); } catch { /* 무시 */ }
    }

    // 크기조절이 끝나면 블러를 껐다 켜서 DWM이 새(넓어진) 영역까지 다시 계산하게 한다
    // (재적용만으론 늘어난 부분에 블러가 안 입혀져 좌/우 배경이 달라 보이던 문제 해결).
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_EXITSIZEMOVE) { try { SetBlur(false); SetBlur(true); } catch { /* 무시 */ } }
        base.WndProc(ref m);
    }

    private void UpdateRegion()
    {
        if (Width <= 0 || Height <= 0) return;
        var h = CreateRoundRectRgn(0, 0, Width + 1, Height + 1, CornerRadius * 2, CornerRadius * 2);
        Region = System.Drawing.Region.FromHrgn(h);
        DeleteObject(h);
    }

    private void SetBlur(bool on)
    {
        if (!IsHandleCreated) return;
        var accent = new AccentPolicy { AccentState = on ? ACCENT_ENABLE_BLURBEHIND : ACCENT_DISABLED, GradientColor = 0 };
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
            _onError?.Invoke("위젯 WebView2 초기화 실패(런타임 확인): " + ex.Message);
            try { if (!IsDisposed) Close(); } catch { /* 무시 */ }
        }
    }

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string msg; try { msg = e.TryGetWebMessageAsString() ?? ""; } catch { return; }
        if (msg == "close") Close();
        else if (msg == "drag" && !IsDisposed) { ReleaseCapture(); SendMessage(Handle, WM_NCLBUTTONDOWN, HTCAPTION, IntPtr.Zero); }
        else if (msg == "resize" && !IsDisposed) { ReleaseCapture(); SendMessage(Handle, WM_NCLBUTTONDOWN, HTRIGHT, IntPtr.Zero); } // 폭만(색/불투명도는 페이지 CSS가 담당)
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { try { _web.Dispose(); } catch { /* 무시 */ } }
        base.Dispose(disposing);
    }
}
