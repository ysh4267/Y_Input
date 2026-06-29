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
        menu.Items.Add(new ToolStripMenuItem("Y Input") { Enabled = false }); // 앱 이름(헤더)
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("열기", null, (_, _) => OpenUi());
        menu.Items.Add("업데이트", null, (_, _) => StartUpdate());
        menu.Items.Add("끄기", null, (_, _) => ExitThread());

        _icon = new NotifyIcon
        {
            Icon = LoadAppIcon(),
            Visible = true,
            Text = "Y Input",
            ContextMenuStrip = menu,
        };
        _icon.DoubleClick += (_, _) => OpenUi();
    }

    /// <summary>exe에 내장된 아이콘(ApplicationIcon)을 트레이 아이콘으로 사용. 실패 시 기본 아이콘.</summary>
    private static Icon LoadAppIcon()
    {
        try
        {
            var path = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(path))
            {
                var ico = Icon.ExtractAssociatedIcon(path);
                if (ico is not null) return ico;
            }
        }
        catch { /* 폴백 */ }
        return SystemIcons.Application;
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

    /// <summary>트레이 '업데이트' — Git 동기화 빌드를 분리 실행(웹 UI의 업데이트와 동일).</summary>
    private void StartUpdate()
    {
        try
        {
            var r = AppUpdater.Start();
            _service.Log(r.Ok ? "info" : "error",
                r.Ok ? "업데이트 시작 — 곧 재빌드/재시작됩니다." : ("업데이트 실패: " + r.Message));
            ShowBalloon(r.Ok ? "업데이트" : "업데이트 실패",
                r.Ok ? "업데이트를 시작했습니다. 곧 재시작됩니다." : r.Message);
        }
        catch (Exception ex)
        {
            _service.Log("error", "업데이트 오류: " + ex.Message);
        }
    }

    /// <summary>외부(웹/스크립트) 요청으로 그레이스풀 종료. UI 스레드에서 ExitThread 호출.</summary>
    public void RequestExit() => _ui.Post(_ => ExitThread(), null);

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
