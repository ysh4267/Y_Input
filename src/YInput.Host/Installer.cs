using System.Diagnostics;

namespace YInput.Host;

/// <summary>
/// 간단한 "앱 자체 설치". 설치 폴더(<see cref="InstallExe"/>)가 아닌 곳에서 실행되면, 자기 자신을 설치 폴더로
/// 복사하고 시작 메뉴 바로가기를 만든 뒤 그쪽을 실행하고 이 인스턴스는 종료한다. 설치 폴더에서 실행 중이거나
/// 업데이트/포터블 모드(<c>--portable</c> 또는 파일명에 'portable')면 아무것도 하지 않는다(제자리 실행).
/// 즉 릴리즈의 <c>YInput.exe</c>=설치본, <c>YInput-Portable.exe</c>=포터블. 이미 관리자 권한이라 자식 실행이 권한을 물려받아 UAC 재확인이 없다.
/// 설치 뒤에는 실행 파일이 설치 폴더에 있으므로 인앱 업데이트도 자동으로 그 폴더에서 이뤄진다.
/// </summary>
internal static class Installer
{
    /// <summary>설치 폴더: %LOCALAPPDATA%\Programs\YInput (사용자별, 관리자여도 같은 사용자 프로필).</summary>
    public static string InstallDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "YInput");
    public static string InstallExe => Path.Combine(InstallDir, "YInput.exe");

    /// <summary>설치가 필요하면 설치 후 설치본을 실행한다. 이 인스턴스가 종료해야 하면 true(호출측이 return).
    /// 반드시 단일 인스턴스 뮤텍스를 잡기 <b>전에</b> 호출할 것(설치본이 뮤텍스를 잡아야 하므로).</summary>
    public static bool EnsureInstalled(string[] args)
    {
        try
        {
            var cur = Environment.ProcessPath;
            if (string.IsNullOrEmpty(cur)) return false;

            // 이미 설치 위치이거나, 업데이트 재시작/포터블(개발)이면 설치하지 않는다.
            if (PathEquals(cur, InstallExe)) return false;
            if (args.Contains("--updated") || args.Contains("--apply-update") || args.Contains("--portable")) return false;
            // 파일명에 'portable'이 들어간 배포본(YInput-Portable.exe)은 제자리 실행 — 설치 안 함.
            if (Path.GetFileNameWithoutExtension(cur).Contains("portable", StringComparison.OrdinalIgnoreCase)) return false;

            bool installed;
            try
            {
                Directory.CreateDirectory(InstallDir);
                File.Copy(cur, InstallExe, overwrite: true); // 실행 중 exe도 읽기 복사는 허용됨
                CopyDriversFolder(Path.GetDirectoryName(cur)!);
                CreateStartMenuShortcut();
                installed = true;
            }
            catch
            {
                installed = File.Exists(InstallExe); // 잠김 등(설치본이 이미 실행 중) — 있으면 그걸 실행
            }
            if (!installed) return false; // 설치 실패 + 설치본 없음 → 제자리 실행(폴백)

            try { Process.Start(new ProcessStartInfo(InstallExe) { UseShellExecute = true }); }
            catch { return false; } // 실행 실패 → 제자리 실행 폴백

            return true; // 설치본을 실행했으니 이 인스턴스는 종료
        }
        catch { return false; } // 설치는 어디까지나 선택적 — 어떤 문제든 제자리 실행으로 폴백
    }

    private static bool PathEquals(string a, string b)
    {
        try { return string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    /// <summary>실행 파일 옆 drivers\ 폴더(ViGEmBus 설치 파일 등)가 있으면 함께 복사.</summary>
    private static void CopyDriversFolder(string srcDir)
    {
        try
        {
            var src = Path.Combine(srcDir, "drivers");
            if (!Directory.Exists(src)) return;
            var dst = Path.Combine(InstallDir, "drivers");
            Directory.CreateDirectory(dst);
            foreach (var f in Directory.EnumerateFiles(src))
                File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), overwrite: true);
        }
        catch { /* drivers 폴더 없거나 복사 실패는 무시 */ }
    }

    private static void CreateStartMenuShortcut()
    {
        try
        {
            var programs = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Microsoft", "Windows", "Start Menu", "Programs");
            Directory.CreateDirectory(programs);
            var lnk = Path.Combine(programs, "Y Input.lnk");
            static string Q(string s) => s.Replace("'", "''"); // PowerShell 단일따옴표 이스케이프
            var script =
                $"$s=(New-Object -ComObject WScript.Shell).CreateShortcut('{Q(lnk)}');" +
                $"$s.TargetPath='{Q(InstallExe)}';$s.WorkingDirectory='{Q(InstallDir)}';" +
                $"$s.Description='Y Input';$s.Save()";
            using var p = Process.Start(new ProcessStartInfo("powershell.exe",
                $"-NoProfile -NonInteractive -Command \"{script}\"")
            { UseShellExecute = false, CreateNoWindow = true });
            p?.WaitForExit(10000);
        }
        catch { /* 바로가기 생성 실패는 치명적 아님 */ }
    }
}
