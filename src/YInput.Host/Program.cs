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
        // 비주얼 스타일은 어떤 창(설치 대화상자·메시지박스)보다 먼저 설정해야 한다.
        System.Windows.Forms.Application.EnableVisualStyles();
        System.Windows.Forms.Application.SetCompatibleTextRenderingDefault(false);

        // 업데이트 마무리 역할: 새로 내려받은 exe가 --apply-update 로 실행되면 정식 앱을 띄우지 않고(뮤텍스도 안 잡음)
        // 옛 프로세스 종료를 기다렸다 실행 파일을 교체하고 정식 이름으로 재실행한다.
        var cmdArgs = Environment.GetCommandLineArgs();
        int applyIdx = Array.IndexOf(cmdArgs, "--apply-update");
        if (applyIdx >= 0) { UpdateFinalizer.Run(cmdArgs, applyIdx); return; }

        // 자체 설치: 설치 폴더가 아닌 곳에서 실행되면 설치 폴더로 복사 후 그쪽을 실행하고 이 인스턴스는 종료(뮤텍스 잡기 전).
        if (Installer.EnsureInstalled(cmdArgs)) return;

        using var mutex = new Mutex(true, "Global\\YInput_SingleInstance_2F1B", out bool created);
        if (!created)
        {
            // 이미 실행 중 — 기존 프로세스를 종료시키고 이어받는다(새로 실행한 쪽이 이김).
            TakeOverRunningInstance();
            bool acquired;
            try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(8)); }
            catch (AbandonedMutexException) { acquired = true; } // 이전 소유자가 죽으며 버려짐 → 우리가 획득
            if (!acquired)
            {
                System.Windows.Forms.MessageBox.Show(
                    "실행 중인 Y_Input을 종료하지 못했습니다. 잠시 후 다시 시도하세요.",
                    "Y_Input", System.Windows.Forms.MessageBoxButtons.OK,
                    System.Windows.Forms.MessageBoxIcon.Warning);
                return;
            }
        }

        TryCleanStage(); // 지난 업데이트에서 남은 스테이지 exe(YInput.stage.exe) 정리

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
        using var sync = new GitHubSync(library, service, dataRoot); // GitHub 비공개 저장소 동기화
        // 로컬 매크로 변경 → 위젯/웹 UI 실시간 방송(위젯이 편집 즉시 갱신) + 동기화 푸시 예약(디바운스)
        service.MacrosChanged = () => { service.BroadcastMacrosChanged(); sync.SchedulePush(); };

        // 로컬 웹서버 (127.0.0.1 전용)
        int port = FindFreePort(PreferredPort);
        string url = $"http://127.0.0.1:{port}";
        service.Url = url;

        // 위젯(보더리스 WebView2 창) 매니저 — WebView2 창은 UI(메시지 루프) 스레드에서만 생성 가능.
        // 이 STA 스레드에 바인딩된 SynchronizationContext로 마셜(Application.Run이 교체하지 않도록 현재로 지정).
        var uiSync = new System.Windows.Forms.WindowsFormsSynchronizationContext();
        SynchronizationContext.SetSynchronizationContext(uiSync);
        var widgets = new WidgetManager(uiSync, url, dataRoot, service);
        var overlay = new OverlayController(uiSync, dataRoot, hub, service, progress); // 인게임 오버레이(GDI+ 레이어드)

        var app = BuildWebApp(service, hub, sync, widgets, overlay, url);
        app.StartAsync().GetAwaiter().GetResult();

        sync.Start(); // 시작 시 원격에서 내려받기(설정돼 있으면) + 주기 동기화 타이머

        // 트레이 + 드라이버 부트스트랩(백그라운드)
        var tray = new TrayAppContext(service);
        service.QuitRequested = tray.RequestExit; // /api/app/quit → 그레이스풀 종료
        // 업데이트 재시작(--updated)이면 기존 탭이 새 인스턴스로 재연결하므로 새 탭을 열지 않는다.
        var isUpdated = cmdArgs.Contains("--updated");
        if (!isUpdated) tray.OpenUi(); // 실행 즉시 기본 브라우저로 편집 UI 열기
        RunBootstrap(service, tray);
        widgets.RestoreSaved(); // 지난 세션에 열린 위젯 복원(메시지 루프 시작되면 생성)
        overlay.Start();        // 인게임 오버레이 창 생성(숨김 상태로 대기)

        // 메시지 루프(블로킹) — 종료 시까지
        System.Windows.Forms.Application.Run(tray);

        try { widgets.CloseAll(); } catch { /* ignore */ } // 위젯 창 정리
        try { overlay.Close(); } catch { /* ignore */ }   // 오버레이 창 정리

        // 기본 브라우저로 열린 페이지에 종료 신호 → 페이지가 스스로 닫힘 처리.
        // 단, 업데이트 재시작 중이면 방송하지 않는다(열린 탭이 재연결을 포기하지 않고 새 인스턴스로 붙게).
        if (!service.IsUpdating)
            try { hub.Broadcast("shutdown", new { }); Thread.Sleep(150); } catch { /* ignore */ }

        // 정리
        try { app.StopAsync().Wait(3000); } catch { /* ignore */ }
        try { (app as IDisposable)?.Dispose(); } catch { /* ignore */ }
    }

    /// <summary>이미 실행 중인 Y_Input을 종료시킨다 — 먼저 그레이스풀 종료(/api/app/quit, 눌린 입력 떼기)를 요청하고,
    /// 그래도 남아 있으면 강제 종료. 스테이지(업데이트 마무리) 프로세스는 건드리지 않는다.</summary>
    private static void TakeOverRunningInstance()
    {
        int self = Environment.ProcessId;

        // 1) 그레이스풀 종료 요청(기본 포트) — 실패해도 아래 강제 종료로 처리
        try
        {
            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            http.PostAsync($"http://127.0.0.1:{PreferredPort}/api/app/quit", null).GetAwaiter().GetResult();
        }
        catch { /* 포트 다름/무응답 — 강제 종료로 */ }

        // 2) 남은 YInput 프로세스 종료(자기 자신·업데이트 스테이지 제외)
        foreach (var p in System.Diagnostics.Process.GetProcesses())
        {
            try
            {
                var name = p.ProcessName;
                if (p.Id == self
                    || !name.StartsWith("YInput", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("stage", StringComparison.OrdinalIgnoreCase)) // 업데이트 마무리 프로세스는 보호
                { p.Dispose(); continue; }
                if (!p.WaitForExit(3000)) { try { p.Kill(); } catch { /* 이미 종료 등 */ } }
                p.Dispose();
            }
            catch { try { p.Dispose(); } catch { /* 무시 */ } }
        }
    }

    /// <summary>지난 업데이트에서 남은 스테이지 exe(YInput.stage.exe)를 정리(있으면). 잠겨 있으면 다음 실행에 다시 시도.</summary>
    private static void TryCleanStage()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return;
            var stage = Path.Combine(Path.GetDirectoryName(exe)!, "YInput.stage.exe");
            if (File.Exists(stage)) File.Delete(stage);
        }
        catch { /* 잠김 등 — 무시(다음 실행/업데이트에서 정리) */ }
    }

    private static WebApplication BuildWebApp(MacroService service, SocketHub hub, GitHubSync sync, WidgetManager widgets, OverlayController overlay, string url)
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

        app.MapApi(service, hub, sync, widgets, overlay);
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
