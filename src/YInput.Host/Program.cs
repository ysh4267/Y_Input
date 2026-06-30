using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.FileProviders;
using YInput.Core.Persistence;
using YInput.Engine;
using YInput.Host.Services;
using YInput.Host.Web;
using YInput.Input;

namespace YInput.Host;

internal static class Program
{
    private const int PreferredPort = 48710;

    [STAThread]
    private static void Main()
    {
        using var mutex = new Mutex(true, "Global\\YInput_SingleInstance_2F1B", out bool created);
        if (!created)
        {
            System.Windows.Forms.MessageBox.Show(
                "Y_Input이 이미 실행 중입니다. 작업 표시줄 오른쪽 트레이 아이콘을 확인하세요.",
                "Y_Input", System.Windows.Forms.MessageBoxButtons.OK,
                System.Windows.Forms.MessageBoxIcon.Information);
            return;
        }

        System.Windows.Forms.Application.EnableVisualStyles();
        System.Windows.Forms.Application.SetCompatibleTextRenderingDefault(false);

        // 데이터 폴더: %APPDATA%\YInput\macros
        var dataRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "YInput");
        var macrosDir = Path.Combine(dataRoot, "macros");

        // 서비스 구성
        using var backend = new InputBackend();
        var library = new MacroLibrary(macrosDir);
        var player = new Player(backend);
        var recorder = new Recorder(backend);
        using var hotkeys = new HotkeyManager();
        using var rawInput = new RawInputMonitor();
        var hub = new SocketHub();
        using var progress = new ProgressBroadcaster(hub); // 진행 보고 ~60Hz 코얼레싱(종료 시 타이머 정리)
        var service = new MacroService(backend, library, player, recorder, hotkeys, rawInput, hub, progress);

        // 로컬 웹서버 (127.0.0.1 전용)
        int port = FindFreePort(PreferredPort);
        string url = $"http://127.0.0.1:{port}";
        service.Url = url;

        var app = BuildWebApp(service, hub, url);
        app.StartAsync().GetAwaiter().GetResult();

        // 트레이 + 드라이버 부트스트랩(백그라운드)
        var tray = new TrayAppContext(service);
        service.QuitRequested = tray.RequestExit; // /api/app/quit → 그레이스풀 종료
        tray.OpenUi(); // 실행 즉시 기본 브라우저로 편집 UI 열기
        RunBootstrap(service, tray);

        // 메시지 루프(블로킹) — 종료 시까지
        System.Windows.Forms.Application.Run(tray);

        // 기본 브라우저로 열린(앱 모드가 아닌) 페이지에 종료 신호 → 페이지가 스스로 닫힘 처리
        try { hub.Broadcast("shutdown", new { }); Thread.Sleep(150); } catch { /* ignore */ }

        // 정리
        try { app.StopAsync().Wait(3000); } catch { /* ignore */ }
        try { (app as IDisposable)?.Dispose(); } catch { /* ignore */ }
    }

    private static WebApplication BuildWebApp(MacroService service, SocketHub hub, string url)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = AppContext.BaseDirectory, // 게시 폴더의 wwwroot 보장
        });
        builder.Logging.ClearProviders(); // 콘솔 없는 트레이 앱
        builder.WebHost.UseUrls(url);

        var app = builder.Build();
        app.UseWebSockets();

        // 웹 UI는 어셈블리에 임베디드된 wwwroot에서 서빙(단일 exe에 포함됨)
        var webUi = new ManifestEmbeddedFileProvider(typeof(Program).Assembly, "wwwroot");
        var defaultFiles = new DefaultFilesOptions { FileProvider = webUi };
        defaultFiles.DefaultFileNames.Clear();
        defaultFiles.DefaultFileNames.Add("index.html");
        app.UseDefaultFiles(defaultFiles);
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = webUi,
            // 항상 ETag로 재검증 → 업데이트 후 브라우저가 옛 app.js/app.css를 캐시한 채 쓰지 않게(안 바뀌면 304, 바뀌면 새 파일)
            OnPrepareResponse = ctx => ctx.Context.Response.Headers["Cache-Control"] = "no-cache, must-revalidate",
        });

        app.MapApi(service, hub);
        return app;
    }

    private static void RunBootstrap(MacroService service, TrayAppContext tray)
    {
        Task.Run(() =>
        {
            try
            {
                var result = DriverProvisioner.EnsureInstalled();
                foreach (var m in result.Messages) service.Log("info", m);
                service.BroadcastStatus();

                if (result.RebootRequired)
                    tray.ShowBalloon("드라이버 설치 완료", "키보드·마우스 드라이버 적용을 위해 PC를 재부팅하세요.");
                else
                    tray.ShowBalloon("Y_Input 준비됨", "편집 UI가 브라우저에 열렸습니다. (트레이에서 다시 열 수 있어요)");
            }
            catch (Exception ex)
            {
                service.Log("error", "드라이버 부트스트랩 오류: " + ex.Message);
            }
        });
    }

    /// <summary>선호 포트가 비어 있으면 사용, 아니면 임의의 빈 포트.</summary>
    private static int FindFreePort(int preferred)
    {
        foreach (int candidate in new[] { preferred, 0 })
        {
            try
            {
                var listener = new TcpListener(IPAddress.Loopback, candidate);
                listener.Start();
                int actual = ((IPEndPoint)listener.LocalEndpoint).Port;
                listener.Stop();
                return actual;
            }
            catch (SocketException) { /* 다음 후보 */ }
        }
        return preferred;
    }
}
