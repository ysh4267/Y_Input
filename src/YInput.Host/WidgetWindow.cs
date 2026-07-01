using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace YInput.Host;

/// <summary>
/// 보더리스·항상 위·작업표시줄 미표시 위젯 창. 안에 WebView2로 <c>/widget.html?id=…</c>를 띄운다.
/// 드래그(이동)와 닫기는 위젯 페이지가 <c>window.chrome.webview.postMessage('drag'|'close')</c>로 요청한다.
/// </summary>
internal sealed class WidgetWindow : Form
{
    private const int WM_NCLBUTTONDOWN = 0xA1;
    private const int HTCAPTION = 0x2;
    private const int HTBOTTOMRIGHT = 17;
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    private static readonly Color Bg = Color.FromArgb(21, 25, 33); // --card, 로딩 중 깜빡임 방지
    private readonly WebView2 _web;
    private readonly string _url;
    private readonly string _userDataFolder;
    private readonly Action<string>? _onError;
    public string MacroId { get; }

    public WidgetWindow(string macroId, string url, string userDataFolder, Point location, Action<string>? onError = null)
    {
        MacroId = macroId;
        _url = url;
        _userDataFolder = userDataFolder;
        _onError = onError;

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        Location = location;
        ClientSize = new Size(340, 82);
        MinimumSize = new Size(170, 54);
        MaximumSize = new Size(1400, 320);
        BackColor = Bg;
        Text = "Y Input Widget";

        _web = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = Bg };
        Controls.Add(_web);
        InitAsync();
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
            try { if (!IsDisposed) Close(); } catch { /* 무시 */ } // 빈 보더리스 창이 남지 않게
        }
    }

    // 위젯 페이지 → 네이티브: 'drag'(창 이동 시작) / 'close'(창 닫기)
    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string msg;
        try { msg = e.TryGetWebMessageAsString() ?? ""; } catch { return; }
        if (msg == "close") Close();
        else if (msg == "drag" && !IsDisposed)
        {
            // 보더리스 창을 캡션처럼 잡아 OS 이동 루프 시작(부드럽고 안정적).
            ReleaseCapture();
            SendMessage(Handle, WM_NCLBUTTONDOWN, HTCAPTION, IntPtr.Zero);
        }
        else if (msg == "resize" && !IsDisposed)
        {
            // 우하단 그립 → OS 크기조절 루프 시작(Min/MaximumSize를 OS가 존중).
            ReleaseCapture();
            SendMessage(Handle, WM_NCLBUTTONDOWN, HTBOTTOMRIGHT, IntPtr.Zero);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { try { _web.Dispose(); } catch { /* 무시 */ } }
        base.Dispose(disposing);
    }
}
