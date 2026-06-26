using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using YInput.Host.Services;

namespace YInput.Host;

/// <summary>
/// 창 없는 트레이 상주 컨텍스트. NotifyIcon과 메뉴를 관리하고 'UI 열기' 시 기본 브라우저를 띄운다.
/// </summary>
internal sealed class TrayAppContext : ApplicationContext
{
    private readonly NotifyIcon _icon;
    private readonly MacroService _service;
    private readonly SynchronizationContext _ui;
    private Process? _browser; // 앱 모드로 띄운 전용 브라우저(종료 시 함께 닫음)

    public TrayAppContext(MacroService service)
    {
        _service = service;
        _ui = new WindowsFormsSynchronizationContext();

        var menu = new ContextMenuStrip();
        menu.Items.Add("UI 열기", null, (_, _) => OpenUi());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("재생/녹화 정지", null, (_, _) =>
        {
            try { _service.StopPlayback(); } catch { /* ignore */ }
        });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("종료", null, (_, _) => ExitThread());

        _icon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Visible = true,
            Text = "Y_Input",
            ContextMenuStrip = menu,
        };
        _icon.DoubleClick += (_, _) => OpenUi();
    }

    public void OpenUi()
    {
        try
        {
            if (_browser is { HasExited: false }) return; // 이미 앱 창이 열려 있음
            _browser = BrowserLauncher.LaunchApp(_service.Url);
            if (_browser == null) BrowserLauncher.ShellOpen(_service.Url); // 폴백: 기본 브라우저
        }
        catch (Exception ex)
        {
            _service.Log("error", "브라우저 열기 실패: " + ex.Message);
        }
    }

    public void ShowBalloon(string title, string text)
    {
        _ui.Post(_ =>
        {
            _icon.BalloonTipTitle = title;
            _icon.BalloonTipText = text;
            _icon.ShowBalloonTip(4000);
        }, null);
    }

    protected override void ExitThreadCore()
    {
        // 앱 모드로 띄운 브라우저 창도 함께 닫는다(프로세스 트리 종료).
        try { if (_browser is { HasExited: false }) _browser.Kill(entireProcessTree: true); }
        catch { /* ignore */ }
        _icon.Visible = false;
        _icon.Dispose();
        base.ExitThreadCore();
    }
}
