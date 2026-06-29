using System.Diagnostics;

namespace YInput.Host.Services;

/// <summary>
/// Git 기반 업데이트 동기화(개발 PC 전용). 소스 트리 + dotnet SDK + git 가 있어야 한다.
/// Check: 원격과 비교해 몇 커밋 뒤처졌는지. Start: tools/update.ps1 을 분리 실행(앱이 종료돼도 진행).
/// </summary>
public static class AppUpdater
{
    // deploy.ps1 과 동일하게 소스 트리 절대경로를 사용한다.
    private const string RepoDir = @"N:\Projects\Y_Input";

    public readonly record struct CheckResult(bool Ok, int Behind, string Current, string Message);
    public readonly record struct StartResult(bool Ok, string Message);

    public static CheckResult Check()
    {
        if (!Directory.Exists(Path.Combine(RepoDir, ".git")))
            return new CheckResult(false, 0, "", "git 저장소가 아닙니다: " + RepoDir);
        try
        {
            Run("git", new[] { "-C", RepoDir, "fetch", "--quiet" }, out _, out var fErr, 60000);
            Run("git", new[] { "-C", RepoDir, "rev-parse", "--short", "HEAD" }, out var cur, out _, 15000);
            var ok = Run("git", new[] { "-C", RepoDir, "rev-list", "--count", "HEAD..@{u}" }, out var behindStr, out var bErr, 15000);
            if (!ok) return new CheckResult(false, 0, cur.Trim(), "원격 비교 실패: " + (bErr.Trim() + " " + fErr.Trim()).Trim());
            int.TryParse(behindStr.Trim(), out var behind);
            return new CheckResult(true, behind, cur.Trim(),
                behind > 0 ? $"{behind}개 커밋 뒤처짐 — 업데이트 가능" : "최신 상태");
        }
        catch (Exception ex)
        {
            return new CheckResult(false, 0, "", ex.Message);
        }
    }

    public static StartResult Start()
    {
        var script = Path.Combine(RepoDir, "tools", "update.ps1");
        if (!File.Exists(script)) return new StartResult(false, "update.ps1 없음: " + script);
        try
        {
            // 분리(detached) 실행 — update.ps1 이 앱을 종료/교체/재실행한다.
            Process.Start(new ProcessStartInfo("powershell.exe",
                $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{script}\"")
            {
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = RepoDir,
            });
            return new StartResult(true, "업데이트 시작");
        }
        catch (Exception ex)
        {
            return new StartResult(false, ex.Message);
        }
    }

    private static bool Run(string file, string[] args, out string stdout, out string stderr, int timeoutMs)
    {
        var psi = new ProcessStartInfo(file)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("프로세스를 시작할 수 없습니다: " + file);
        stdout = p.StandardOutput.ReadToEnd();
        stderr = p.StandardError.ReadToEnd();
        if (!p.WaitForExit(timeoutMs)) { try { p.Kill(true); } catch { } return false; }
        return p.ExitCode == 0;
    }
}
