namespace YInput.Host;

/// <summary>
/// 파일 로그 — 실행 파일 옆 <c>logs\</c> 폴더에 남긴다(설치본이면 설치 폴더 내부).
/// <c>app-날짜.log</c>: 모든 로그(정보 포함), <c>error-날짜.log</c>: 경고/오류 + 처리 안 된 예외만.
/// 문제 발생 시 이 파일을 열어 원인을 추적한다. 14일 지난 파일은 시작 시 정리.
/// 로그 실패는 앱 동작에 영향을 주지 않는다(전부 무시).
/// </summary>
public static class FileLog
{
    private static readonly object Gate = new();
    private static string _dir = "";

    /// <summary>로그 폴더 생성 + 오래된 로그 정리 + 전역 예외 후킹. Main 최상단에서 1회 호출.</summary>
    public static void Init()
    {
        try
        {
            // 단일 파일 게시에서 AppContext.BaseDirectory는 임시 추출 폴더라 실제 exe 위치를 쓴다.
            var exeDir = Path.GetDirectoryName(Environment.ProcessPath);
            _dir = Path.Combine(string.IsNullOrEmpty(exeDir) ? AppContext.BaseDirectory : exeDir, "logs");
            Directory.CreateDirectory(_dir);
            Cleanup();
        }
        catch { _dir = ""; }

        // 처리 안 된 예외 → error 로그(크래시 원인 추적)
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Write("error", "UnhandledException: " + (e.ExceptionObject as Exception)?.ToString() ?? e.ExceptionObject?.ToString() ?? "?");
        System.Windows.Forms.Application.ThreadException += (_, e) =>
            Write("error", "UIThreadException: " + e.Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        { Write("error", "UnobservedTaskException: " + e.Exception); e.SetObserved(); };
    }

    /// <summary>로그 한 줄. level: info/warn/error/monitor 등 — warn/error는 error 파일에도 복사.</summary>
    public static void Write(string level, string message)
    {
        if (_dir.Length == 0) return;
        try
        {
            var now = DateTime.Now;
            var line = $"{now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}";
            lock (Gate)
            {
                File.AppendAllText(Path.Combine(_dir, $"app-{now:yyyyMMdd}.log"), line);
                if (level is "warn" or "error")
                    File.AppendAllText(Path.Combine(_dir, $"error-{now:yyyyMMdd}.log"), line);
            }
        }
        catch { /* 로그 실패 무시 */ }
    }

    public static void Error(string context, Exception ex) => Write("error", context + ": " + ex);

    private static void Cleanup()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(_dir, "*.log"))
                if (DateTime.Now - File.GetLastWriteTime(f) > TimeSpan.FromDays(14))
                    try { File.Delete(f); } catch { /* 잠김 등 무시 */ }
            var shots = Path.Combine(_dir, "shots");
            if (Directory.Exists(shots)) // 구버전이 남긴 진단 스냅샷 폴더 정리
                try { Directory.Delete(shots, true); } catch { /* 무시 */ }
        }
        catch { /* 무시 */ }
    }
}
