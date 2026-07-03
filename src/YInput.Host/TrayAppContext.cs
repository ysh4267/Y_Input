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
    private readonly ToolStripMenuItem _versionItem;   // 앱 이름 밑: 현재 버전 · 업데이트 여부
    private string _verText = "버전 확인 중…";
    private DateTime _verAt = DateTime.MinValue;        // 마지막 확인 시각(캐시)
    private volatile bool _verBusy;

    public TrayAppContext(MacroService service)
    {
        _service = service;
        _ui = new WindowsFormsSynchronizationContext();

        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem("Y Input") { Enabled = false }); // 앱 이름(헤더)
        _versionItem = new ToolStripMenuItem(_verText) { Enabled = false };    // 앱 이름 밑: 버전 · 업데이트 여부
        menu.Items.Add(_versionItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("열기", null, (_, _) => OpenUi());
        menu.Items.Add("끄기", null, (_, _) => ExitThread());
        menu.Opening += (_, _) => RefreshVersion(false); // 메뉴 열 때마다 갱신(5분 캐시)

        _icon = new NotifyIcon
        {
            Icon = LoadAppIcon(),
            Visible = true,
            Text = "Y Input",
            ContextMenuStrip = menu,
        };
        _icon.DoubleClick += (_, _) => OpenUi();
        RefreshVersion(true); // 시작 시 미리 한 번 확인
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
        // 이미 열린 웹 UI가 있으면 새 탭을 열지 않고 그 창을 앞으로 가져온다(단일 개체).
        // 창을 못 찾으면(백그라운드 탭 등) 폴백으로 새로 연다.
        if (_service.HasWebUiClient && BrowserFocus.BringToFront()) return;
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

    /// <summary>앱 이름 밑 '버전 · 업데이트 여부' 표시를 갱신한다. 백그라운드로 GitHub 최신 릴리즈를 확인(5분 캐시)하고
    /// "v0.3.1 · 최신" / "v0.3.1 · 새 버전 v0.3.2 있음" 형태로 메뉴 텍스트를 바꾼다.</summary>
    private void RefreshVersion(bool force)
    {
        if (_verBusy) return;
        if (!force && (DateTime.UtcNow - _verAt) < TimeSpan.FromMinutes(5)) return;
        _verBusy = true;
        Task.Run(() =>
        {
            string text;
            try
            {
                var c = AppUpdater.Check();
                var cur = string.IsNullOrEmpty(c.Current) ? "개발 빌드" : c.Current;
                if (!c.Ok) text = $"{cur} · 업데이트 확인 실패";
                else if (c.UpdateAvailable) text = $"{cur} · 새 버전 {c.Latest} 있음";
                else text = $"{cur} · 최신";
            }
            catch { text = "버전 확인 실패"; }
            _verText = text;
            _verAt = DateTime.UtcNow;
            _ui.Post(_ => _versionItem.Text = text, null);
            _verBusy = false;
        });
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
