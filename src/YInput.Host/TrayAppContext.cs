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
            // 기본 브라우저(보통 크롬) 일반 창/탭으로 연다.
            Process.Start(new ProcessStartInfo(_service.Url) { UseShellExecute = true });
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
        _icon.Visible = false;
        _icon.Dispose();
        base.ExitThreadCore();
    }
}
