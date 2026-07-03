using System.Diagnostics;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace YInput.Host.Services;

/// <summary>
/// GitHub Releases 기반 업데이트. 소스 트리·dotnet SDK·git 가 전혀 필요 없어 어느 PC에서나 동작한다.
/// Check : 최신 릴리즈 태그를 현재 빌드(어셈블리에 임베드된 버전)와 비교.
/// Start : 최신 릴리즈의 YInput.exe 에셋을 내려받아 교체 스크립트를 분리 실행한 뒤 앱이 종료/재시작된다.
/// </summary>
public static class AppUpdater
{
    private const string Owner = "ysh4267";
    private const string Repo = "Y_Input";
    private const string AssetName = "YInput.exe";
    private static readonly string LatestApi = $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest";

    private static readonly HttpClient Http = CreateHttp();
    private static HttpClient CreateHttp()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        h.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("YInput-Updater", "1.0"));
        h.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return h;
    }

    public readonly record struct CheckResult(bool Ok, bool UpdateAvailable, string Current, string Latest, string Message, string DownloadUrl, string PageUrl);
    public readonly record struct VersionInfo(string Current, string CurrentDate, string Release, string ReleaseDate);
    public readonly record struct UpdateStart(bool Ok, string Message);

    private readonly record struct LatestRelease(string Tag, string Date, string AssetUrl, string HtmlUrl);

    /// <summary>현재 빌드 버전(빌드 시 어셈블리에 임베드). 예: "v0.3.1". 미임베드 개발 빌드면 "".</summary>
    public static string CurrentVersion()
    {
        var s = Assembly.GetEntryAssembly()?
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";
        var plus = s.IndexOf('+');           // "0.3.1+<commit>" → 커밋 해시 제거
        if (plus >= 0) s = s[..plus];
        s = s.Trim();
        if (string.IsNullOrEmpty(s) || s == "1.0.0") return "";   // 버전 미지정 빌드는 알 수 없음으로 취급
        return s.StartsWith('v') ? s : "v" + s;
    }

    /// <summary>표시용 버전 정보. 현재 빌드 + GitHub 최신 릴리즈(태그·날짜). 네트워크 실패 시 릴리즈는 빈 문자열.</summary>
    public static VersionInfo Version()
    {
        var cur = CurrentVersion();
        var curDate = "";
        try
        {
            var p = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(p) && File.Exists(p))
                curDate = File.GetLastWriteTime(p).ToString("yyyy-MM-dd");
        }
        catch { /* 무시 */ }

        var (ok, latest, _) = TryFetchLatest();
        return new VersionInfo(cur, curDate, ok ? latest.Tag : "", ok ? latest.Date : "");
    }

    public static CheckResult Check()
    {
        var cur = CurrentVersion();
        var (ok, latest, err) = TryFetchLatest();
        if (!ok) return new CheckResult(false, false, cur, "", "릴리즈 확인 실패: " + err, "", "");
        if (string.IsNullOrEmpty(latest.Tag)) return new CheckResult(false, false, cur, "", "릴리즈를 찾을 수 없습니다.", "", "");
        if (string.IsNullOrEmpty(latest.AssetUrl))
            return new CheckResult(false, false, cur, latest.Tag, $"{latest.Tag} 릴리즈에 {AssetName} 파일이 없습니다.", "", latest.HtmlUrl);

        // 임베드 버전과 최신 태그가 정확히 같으면 최신. (개발 빌드 등 cur="" 이면 업데이트 가능으로 안내)
        var upToDate = !string.IsNullOrEmpty(cur) && string.Equals(cur, latest.Tag, StringComparison.OrdinalIgnoreCase);
        return new CheckResult(true, !upToDate, cur, latest.Tag,
            upToDate ? "최신 상태" : $"새 버전 {latest.Tag} 사용 가능", latest.AssetUrl, latest.HtmlUrl);
    }

    private static (bool ok, LatestRelease latest, string error) TryFetchLatest()
    {
        try
        {
            using var resp = Http.GetAsync(LatestApi).GetAwaiter().GetResult();
            if (!resp.IsSuccessStatusCode) return (false, default, $"GitHub 응답 {(int)resp.StatusCode}");
            var json = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var tag = root.TryGetProperty("tag_name", out var t) ? (t.GetString() ?? "") : "";
            var htmlUrl = root.TryGetProperty("html_url", out var hu) ? (hu.GetString() ?? "") : "";
            var date = "";
            if (root.TryGetProperty("published_at", out var pa) && pa.GetString() is { Length: >= 10 } ds)
                date = ds[..10];

            var assetUrl = "";
            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in assets.EnumerateArray())
                {
                    if (a.TryGetProperty("name", out var n)
                        && string.Equals(n.GetString(), AssetName, StringComparison.OrdinalIgnoreCase)
                        && a.TryGetProperty("browser_download_url", out var u))
                    {
                        assetUrl = u.GetString() ?? "";
                        break;
                    }
                }
            }
            return (true, new LatestRelease(tag, date, assetUrl, htmlUrl), "");
        }
        catch (Exception ex)
        {
            return (false, default, ex.Message);
        }
    }

    /// <summary>
    /// 최신 릴리즈 exe를 실행 파일 옆에 내려받고, "이 프로세스가 죽으면 실행 파일을 새 것으로 교체하고 다시 실행"하는
    /// 교체 스크립트(.cmd)를 분리 실행한다. 성공(Ok=true)하면 호출측이 앱을 <b>종료</b>해야 교체가 진행된다.
    /// 앱이 관리자 권한이면 스크립트·새 프로세스도 권한을 물려받아 UAC 재확인 없이 매끄럽게 재시작된다.
    /// </summary>
    public static UpdateStart StartSelfUpdate()
    {
        var chk = Check();
        if (!chk.Ok) return new(false, chk.Message);
        if (!chk.UpdateAvailable) return new(false, "이미 최신 버전입니다.");
        if (string.IsNullOrEmpty(chk.DownloadUrl)) return new(false, $"{chk.Latest} 릴리즈에 {AssetName}이(가) 없습니다.");

        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe) || !File.Exists(exe)) return new(false, "현재 실행 파일 경로를 확인할 수 없습니다.");
        var dir = Path.GetDirectoryName(exe)!;
        var newExe = Path.Combine(dir, "YInput.update.exe");

        // 1) 새 exe 스트리밍 다운로드(실행 파일과 같은 폴더 — 관리자 권한이라 Program Files라도 쓰기 가능)
        try
        {
            using var resp = Http.GetAsync(chk.DownloadUrl, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
            if (!resp.IsSuccessStatusCode) { return new(false, $"다운로드 실패(HTTP {(int)resp.StatusCode})"); }
            using var fs = new FileStream(newExe, FileMode.Create, FileAccess.Write, FileShare.None);
            resp.Content.CopyToAsync(fs).GetAwaiter().GetResult();
        }
        catch (Exception ex) { TryDelete(newExe); return new(false, "다운로드 오류: " + ex.Message); }

        // 2) 손상 방지 — 단일 파일 exe는 수십 MB. 비정상적으로 작으면 중단.
        try { if (new FileInfo(newExe).Length < 1_000_000) { TryDelete(newExe); return new(false, "받은 파일이 비정상적으로 작습니다(손상)."); } }
        catch { /* 크기 확인 실패는 무시 */ }

        // 3) 교체 스크립트 작성 후 분리 실행(경로/PID는 스크립트에 그대로 박아 인자 따옴표 문제를 피함)
        try
        {
            var pid = Environment.ProcessId;
            var script = Path.Combine(Path.GetTempPath(), $"yinput-update-{pid}.cmd");
            File.WriteAllText(script, BuildSwapScript(pid, exe, newExe), new UTF8Encoding(false)); // ASCII/UTF-8 no BOM
            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c \"{script}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            });
        }
        catch (Exception ex) { return new(false, "업데이트 실행 오류: " + ex.Message); }

        return new(true, $"{chk.Latest} 내려받음 — 교체 후 재시작합니다.");
    }

    private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { /* 무시 */ } }

    /// <summary>PID 종료 대기 → 실행 파일 교체(잠겨 있으면 옆으로 밀어내고 교체) → 새 버전(--updated) 실행 → 임시 파일·자기 자신 삭제.</summary>
    private static string BuildSwapScript(int pid, string exe, string newExe)
    {
        static string E(string s) => s.Replace("%", "%%"); // 경로 내 % 방어(드묾)
        return string.Join("\r\n",
            "@echo off",
            "setlocal enableextensions",
            $"set \"PID={pid}\"",
            $"set \"EXE={E(exe)}\"",
            $"set \"NEW={E(newExe)}\"",
            ":wait",
            "tasklist /nh /fi \"PID eq %PID%\" | find \"%PID%\" >nul 2>nul",
            "if %errorlevel%==0 ( ping -n 2 127.0.0.1 >nul & goto wait )",
            "copy /y \"%NEW%\" \"%EXE%\" >nul 2>nul",
            "if errorlevel 1 ( move /y \"%EXE%\" \"%EXE%.old\" >nul 2>nul & copy /y \"%NEW%\" \"%EXE%\" >nul 2>nul )",
            "start \"\" \"%EXE%\" --updated",
            "del /q \"%NEW%\" >nul 2>nul",
            "del /q \"%EXE%.old\" >nul 2>nul",
            "del /q \"%~f0\" >nul 2>nul",
            "") ;
    }
}
