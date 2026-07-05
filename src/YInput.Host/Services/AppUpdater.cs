using System.Diagnostics;
using System.Net.Http.Headers;
using System.Reflection;
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
    private const string DefaultAssetName = "YInput.exe";           // 설치본
    private const string PortableAssetName = "YInput-Portable.exe"; // 포터블본
    private static readonly string LatestApi = $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest";

    /// <summary>현재 실행 파일이 포터블(파일명에 'portable')인가 — 다운로드할 릴리즈 자산 선택에 사용.</summary>
    private static bool IsPortable()
    {
        var p = Environment.ProcessPath;
        return !string.IsNullOrEmpty(p) && Path.GetFileNameWithoutExtension(p).Contains("portable", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>내려받을 자산 이름 — 포터블이면 YInput-Portable.exe, 아니면 YInput.exe. 같은 종류로 자기 자신을 교체한다.</summary>
    private static string CurrentAssetName() => IsPortable() ? PortableAssetName : DefaultAssetName;

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
            return new CheckResult(false, false, cur, latest.Tag, $"{latest.Tag} 릴리즈에 {CurrentAssetName()} 파일이 없습니다.", "", latest.HtmlUrl);

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

            // 종류(포터블/설치본)에 맞는 자산을 우선 선택하고, 없으면 기본 YInput.exe로 폴백(구 릴리즈 호환).
            var want = CurrentAssetName();
            string wantUrl = "", fallbackUrl = "";
            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in assets.EnumerateArray())
                {
                    if (!a.TryGetProperty("name", out var n) || !a.TryGetProperty("browser_download_url", out var u)) continue;
                    var name = n.GetString() ?? "";
                    var url = u.GetString() ?? "";
                    if (string.Equals(name, want, StringComparison.OrdinalIgnoreCase)) wantUrl = url;
                    else if (string.Equals(name, DefaultAssetName, StringComparison.OrdinalIgnoreCase)) fallbackUrl = url;
                }
            }
            var assetUrl = wantUrl.Length > 0 ? wantUrl : fallbackUrl;
            return (true, new LatestRelease(tag, date, assetUrl, htmlUrl), "");
        }
        catch (Exception ex)
        {
            return (false, default, ex.Message);
        }
    }

    /// <summary>
    /// 최신 릴리즈 exe를 실행 파일 옆에 <c>YInput.stage.exe</c>로 내려받고, 그 <b>새 exe를 곧바로 실행</b>한다
    /// (<c>--apply-update &lt;내PID&gt; "&lt;정식경로&gt;"</c>). 그러면 호출측(옛 프로세스)이 스스로 종료해 비켜주고,
    /// 새 프로세스가 옛 프로세스 종료를 기다렸다 실행 파일을 교체하고 정식 이름으로 재실행한다(<see cref="UpdateFinalizer"/>).
    /// 성공(Ok=true)하면 호출측이 앱을 <b>종료</b>해야 교체가 마무리된다. 관리자 권한은 자식 프로세스로 상속된다.
    /// </summary>
    public static UpdateStart StartSelfUpdate()
    {
        var chk = Check();
        if (!chk.Ok) return new(false, chk.Message);
        if (!chk.UpdateAvailable) return new(false, "이미 최신 버전입니다.");
        if (string.IsNullOrEmpty(chk.DownloadUrl)) return new(false, $"{chk.Latest} 릴리즈에 {CurrentAssetName()}이(가) 없습니다.");

        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe) || !File.Exists(exe)) return new(false, "현재 실행 파일 경로를 확인할 수 없습니다.");
        var dir = Path.GetDirectoryName(exe)!;
        var stage = Path.Combine(dir, "YInput.stage.exe");

        // 1) 새 exe 스트리밍 다운로드(실행 파일과 같은 폴더 — 관리자 권한이라 Program Files라도 쓰기 가능)
        try
        {
            using var resp = Http.GetAsync(chk.DownloadUrl, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
            if (!resp.IsSuccessStatusCode) { return new(false, $"다운로드 실패(HTTP {(int)resp.StatusCode})"); }
            using var fs = new FileStream(stage, FileMode.Create, FileAccess.Write, FileShare.None);
            resp.Content.CopyToAsync(fs).GetAwaiter().GetResult();
        }
        catch (Exception ex) { TryDelete(stage); return new(false, "다운로드 오류: " + ex.Message); }

        // 2) 손상 방지 — 단일 파일 exe는 수십 MB. 비정상적으로 작으면 중단.
        try { if (new FileInfo(stage).Length < 1_000_000) { TryDelete(stage); return new(false, "받은 파일이 비정상적으로 작습니다(손상)."); } }
        catch { /* 크기 확인 실패는 무시 */ }

        // 3) 내려받은 새 exe를 '업데이트 마무리' 모드로 실행 → 옛 프로세스(이 프로세스)가 종료되면 교체·정식 실행.
        try
        {
            UpdateFinalizer.Log($"start-self-update: {chk.Latest} 다운로드 완료, 스테이지 실행(--apply-update pid={Environment.ProcessId})");
            Process.Start(new ProcessStartInfo
            {
                FileName = stage,
                Arguments = $"--apply-update {Environment.ProcessId} \"{exe}\"",
                UseShellExecute = false, // 관리자 토큰 상속(UAC 재확인 없음)
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            });
        }
        catch (Exception ex) { return new(false, "업데이트 실행 오류: " + ex.Message); }

        return new(true, $"{chk.Latest} 내려받음 — 새 버전으로 교체·재시작합니다.");
    }

    private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { /* 무시 */ } }
}
