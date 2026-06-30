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

    public readonly record struct CheckResult(bool Ok, bool UpdateAvailable, string Current, string Latest, string Message);
    public readonly record struct StartResult(bool Ok, string Message);
    public readonly record struct VersionInfo(string Current, string CurrentDate, string Release, string ReleaseDate);

    private readonly record struct LatestRelease(string Tag, string Date, string AssetUrl);

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
        if (!ok) return new CheckResult(false, false, cur, "", "릴리즈 확인 실패: " + err);
        if (string.IsNullOrEmpty(latest.Tag)) return new CheckResult(false, false, cur, "", "릴리즈를 찾을 수 없습니다.");
        if (string.IsNullOrEmpty(latest.AssetUrl))
            return new CheckResult(false, false, cur, latest.Tag, $"{latest.Tag} 릴리즈에 {AssetName} 파일이 없습니다.");

        // 임베드 버전과 최신 태그가 정확히 같으면 최신. (개발 빌드 등 cur="" 이면 업데이트 가능으로 안내)
        var upToDate = !string.IsNullOrEmpty(cur) && string.Equals(cur, latest.Tag, StringComparison.OrdinalIgnoreCase);
        return new CheckResult(true, !upToDate, cur, latest.Tag,
            upToDate ? "최신 상태" : $"새 버전 {latest.Tag} 사용 가능");
    }

    public static StartResult Start()
    {
        var (ok, latest, err) = TryFetchLatest();
        if (!ok) return new StartResult(false, "릴리즈 확인 실패: " + err);
        if (string.IsNullOrEmpty(latest.AssetUrl)) return new StartResult(false, $"{AssetName} 에셋을 찾을 수 없습니다.");

        var target = Environment.ProcessPath;
        if (string.IsNullOrEmpty(target)) return new StartResult(false, "현재 실행 파일 경로를 알 수 없습니다.");

        try
        {
            var dir = Path.Combine(Path.GetTempPath(), "YInput.update");
            Directory.CreateDirectory(dir);
            var newExe = Path.Combine(dir, "YInput.new.exe");
            var script = Path.Combine(dir, "apply-update.ps1");

            // 1) 새 빌드 다운로드
            using (var resp = Http.GetAsync(latest.AssetUrl, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult())
            {
                if (!resp.IsSuccessStatusCode) return new StartResult(false, $"다운로드 실패({(int)resp.StatusCode})");
                using var s = resp.Content.ReadAsStream();
                using var f = File.Create(newExe);
                s.CopyTo(f);
            }
            if (new FileInfo(newExe).Length < 1_000_000)
                return new StartResult(false, "다운로드한 파일이 손상되었습니다.");

            // 2) 교체 스크립트 기록(ASCII, PS 5.1 안전) + 분리 실행
            File.WriteAllText(script, UpdaterScript, new UTF8Encoding(false));
            var procId = Environment.ProcessId;
            Process.Start(new ProcessStartInfo("powershell.exe",
                $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{script}\" " +
                $"-Target \"{target}\" -NewExe \"{newExe}\" -ProcId {procId}")
            {
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            });
            return new StartResult(true, $"{latest.Tag} 다운로드 완료 — 교체 후 재시작합니다.");
        }
        catch (Exception ex)
        {
            return new StartResult(false, ex.Message);
        }
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
            return (true, new LatestRelease(tag, date, assetUrl), "");
        }
        catch (Exception ex)
        {
            return (false, default, ex.Message);
        }
    }

    // 분리 실행되는 교체 스크립트. 이전 프로세스 종료를 기다린 뒤 exe 를 바꾸고 재실행한다.
    // ($PID 는 PowerShell 자동 변수이므로 파라미터는 -ProcId 를 사용한다. ASCII only.)
    private const string UpdaterScript = """
param([string]$Target, [string]$NewExe, [int]$ProcId)
$ErrorActionPreference = 'SilentlyContinue'

# 1) wait for the old app to exit (graceful), then force kill if still alive
for ($i = 0; $i -lt 50; $i++) {
  if (-not (Get-Process -Id $ProcId -ErrorAction SilentlyContinue)) { break }
  Start-Sleep -Milliseconds 200
}
if (Get-Process -Id $ProcId -ErrorAction SilentlyContinue) {
  Stop-Process -Id $ProcId -Force
  Start-Sleep -Milliseconds 500
}

# 2) swap the exe (if locked, rename the old one out of the way then copy)
$ok = $false
for ($i = 0; $i -lt 25 -and -not $ok; $i++) {
  try {
    Copy-Item -LiteralPath $NewExe -Destination $Target -Force -ErrorAction Stop
    $ok = $true
  } catch {
    try {
      $old = "$Target.old"
      Remove-Item -LiteralPath $old -Force -ErrorAction SilentlyContinue
      Rename-Item -LiteralPath $Target -NewName ([System.IO.Path]::GetFileName($old)) -ErrorAction Stop
      Copy-Item -LiteralPath $NewExe -Destination $Target -Force -ErrorAction Stop
      $ok = $true
    } catch { Start-Sleep -Milliseconds 300 }
  }
}

# 3) relaunch and clean up
if ($ok) { Start-Process -FilePath $Target }
Remove-Item -LiteralPath $NewExe -Force -ErrorAction SilentlyContinue
""";
}
