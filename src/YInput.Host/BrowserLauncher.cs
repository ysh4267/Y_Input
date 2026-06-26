using System.Diagnostics;

namespace YInput.Host;

/// <summary>
/// 웹 UI를 띄운다. 가능하면 Chromium(Edge/Chrome)을 <c>--app</c> 모드 + 전용 프로필로 띄워,
/// 트레이 종료 시 그 프로세스를 강제 종료해 창까지 닫을 수 있게 한다.
/// Chromium이 없으면 기본 브라우저로 폴백(이 경우 창은 WS 'shutdown' 신호로 닫힘 처리).
/// </summary>
internal static class BrowserLauncher
{
    /// <summary>앱 모드로 실행. 성공 시 전용 브라우저 프로세스, 실패(미설치)면 null.</summary>
    public static Process? LaunchApp(string url)
    {
        string? exe = FindChromium();
        if (exe == null) return null;

        var profile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YInput", "browser");
        try { Directory.CreateDirectory(profile); } catch { /* ignore */ }

        var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
        psi.ArgumentList.Add($"--app={url}");
        psi.ArgumentList.Add($"--user-data-dir={profile}"); // 전용 인스턴스 → 종료 시 트리 kill로 창 닫힘
        psi.ArgumentList.Add("--no-first-run");
        psi.ArgumentList.Add("--no-default-browser-check");
        psi.ArgumentList.Add("--window-size=1280,860");
        try { return Process.Start(psi); }
        catch { return null; }
    }

    /// <summary>기본 브라우저로 열기(폴백).</summary>
    public static void ShellOpen(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* ignore */ }
    }

    private static string? FindChromium()
    {
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var pfx86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string[] candidates =
        {
            Path.Combine(pfx86, @"Microsoft\Edge\Application\msedge.exe"),
            Path.Combine(pf, @"Microsoft\Edge\Application\msedge.exe"),
            Path.Combine(pf, @"Google\Chrome\Application\chrome.exe"),
            Path.Combine(pfx86, @"Google\Chrome\Application\chrome.exe"),
            Path.Combine(local, @"Google\Chrome\Application\chrome.exe"),
        };
        foreach (var c in candidates)
            if (File.Exists(c)) return c;
        return null;
    }
}
