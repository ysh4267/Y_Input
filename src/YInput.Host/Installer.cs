using System.Diagnostics;
using System.Windows.Forms;

namespace YInput.Host;

/// <summary>
/// "앱 자체 설치". 설치본(파일명이 포터블이 아니고, 옆에 설치 표식이 없는 exe)이 처음 실행되면 설치 대화상자를 띄워
/// 설치 위치·바로가기를 정하고, 자기 자신을 그 위치로 복사한 뒤 그쪽을 실행하고 이 인스턴스는 종료한다.
/// '설치본'인지는 <see cref="MarkerName"/> 표식 파일이 exe 옆에 있는지로 판별하므로, 사용자가 임의 위치에 설치해도
/// 다시 설치를 묻지 않는다. 포터블(<c>YInput-Portable.exe</c>)·업데이트 재시작·<c>--portable</c>은 설치하지 않는다.
/// 이미 관리자 권한이라 자식 실행이 권한을 물려받아 UAC 재확인이 없다. 업데이트도 이 위치에서 자동으로 이뤄진다.
/// </summary>
internal static class Installer
{
    private const string MarkerName = ".yinput_install"; // exe 옆에 있으면 '설치된 복사본'
    private const string AppExeName = "YInput.exe";

    /// <summary>기본 설치 폴더: %LOCALAPPDATA%\Programs\YInput (사용자별, 관리자여도 같은 사용자 프로필).</summary>
    public static string DefaultInstallDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "YInput");

    /// <summary>설치가 필요하면 대화상자로 설치 후 설치본을 실행한다. 이 인스턴스가 종료해야 하면 true(호출측이 return).
    /// 반드시 단일 인스턴스 뮤텍스를 잡기 <b>전에</b> 호출할 것.</summary>
    public static bool EnsureInstalled(string[] args)
    {
        try
        {
            var cur = Environment.ProcessPath;
            if (string.IsNullOrEmpty(cur)) return false;
            var curDir = Path.GetDirectoryName(cur)!;

            // 이미 설치본(표식 존재)이거나, 포터블/업데이트/특수 모드면 설치하지 않는다.
            if (File.Exists(Path.Combine(curDir, MarkerName))) return false;
            if (args.Contains("--updated") || args.Contains("--apply-update") || args.Contains("--portable")) return false;
            if (Path.GetFileNameWithoutExtension(cur).Contains("portable", StringComparison.OrdinalIgnoreCase)) return false;

            // 최초 실행 — 설치 대화상자(위치 + 바로가기 선택)
            using var dlg = new InstallDialog(DefaultInstallDir);
            if (dlg.ShowDialog() != DialogResult.OK) return false; // 취소 → 이번엔 제자리 실행(설치 안 함)

            var installDir = string.IsNullOrWhiteSpace(dlg.InstallDir) ? DefaultInstallDir : dlg.InstallDir;
            var installExe = Path.Combine(installDir, AppExeName);

            bool installed;
            try
            {
                Directory.CreateDirectory(installDir);
                File.Copy(cur, installExe, overwrite: true); // 실행 중 exe도 읽기 복사는 허용됨
                CopyDriversFolder(curDir, installDir);
                File.WriteAllText(Path.Combine(installDir, MarkerName), "Y Input install marker"); // 설치본 표식
                if (dlg.CreateStartMenu) CreateShortcut(StartMenuLnk(), installExe, installDir);
                if (dlg.CreateDesktop) CreateShortcut(DesktopLnk(), installExe, installDir);
                installed = true;
            }
            catch
            {
                installed = File.Exists(installExe); // 잠김 등(이미 설치본 실행 중) — 있으면 그걸 실행
            }
            if (!installed) return false; // 설치 실패 + 설치본 없음 → 제자리 실행(폴백)

            try { Process.Start(new ProcessStartInfo(installExe) { UseShellExecute = true }); }
            catch { return false; } // 실행 실패 → 제자리 실행 폴백

            return true; // 설치본을 실행했으니 이 인스턴스는 종료
        }
        catch { return false; } // 설치는 선택적 — 어떤 문제든 제자리 실행으로 폴백
    }

    private static string StartMenuLnk() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Microsoft", "Windows", "Start Menu", "Programs", "Y Input.lnk");

    private static string DesktopLnk() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Y Input.lnk");

    /// <summary>실행 파일 옆 drivers\ 폴더(ViGEmBus 설치 파일 등)가 있으면 설치 위치로 함께 복사.</summary>
    private static void CopyDriversFolder(string srcDir, string dstDir)
    {
        try
        {
            var src = Path.Combine(srcDir, "drivers");
            if (!Directory.Exists(src)) return;
            var dst = Path.Combine(dstDir, "drivers");
            Directory.CreateDirectory(dst);
            foreach (var f in Directory.EnumerateFiles(src))
                File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), overwrite: true);
        }
        catch { /* drivers 폴더 없거나 복사 실패는 무시 */ }
    }

    private static void CreateShortcut(string lnk, string target, string workingDir)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(lnk)!);
            static string Q(string s) => s.Replace("'", "''"); // PowerShell 단일따옴표 이스케이프
            var script =
                $"$s=(New-Object -ComObject WScript.Shell).CreateShortcut('{Q(lnk)}');" +
                $"$s.TargetPath='{Q(target)}';$s.WorkingDirectory='{Q(workingDir)}';" +
                $"$s.Description='Y Input';$s.Save()";
            using var p = Process.Start(new ProcessStartInfo("powershell.exe",
                $"-NoProfile -NonInteractive -Command \"{script}\"")
            { UseShellExecute = false, CreateNoWindow = true });
            p?.WaitForExit(10000);
        }
        catch { /* 바로가기 생성 실패는 치명적 아님 */ }
    }
}
