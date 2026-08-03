using System.Runtime.InteropServices;

namespace YInput.Host;

/// <summary>
/// .NET 런타임 상태 점검 — 드라이버 상태처럼 앱 안에서 설치 여부와 설치 링크를 보여준다.
/// 릴리즈 배포본은 self-contained(런타임 내장)라 시스템 .NET 없이도 돌지만, 프레임워크 의존
/// 빌드(개발 bin 직접 실행 등)는 시스템 .NET이 없으면 앱이 뜨기도 전에 OS 설치 창으로 튕긴다 —
/// 그래서 앱이 떠 있는 동안 시스템 상태를 미리 알려준다.
/// </summary>
public static class DotnetProbe
{
    private static readonly string[] RequiredFrameworks =
        { "Microsoft.NETCore.App", "Microsoft.AspNetCore.App", "Microsoft.WindowsDesktop.App" };

    private static object? _cache;
    private static long _cacheAtMs;

    /// <summary>상태 스냅샷(60초 캐시 — 상태 방송이 잦아도 디스크 조회는 드물게).</summary>
    public static object Query()
    {
        var now = Environment.TickCount64;
        if (_cache is { } c && now - _cacheAtMs < 60_000) return c;

        int major = Environment.Version.Major;
        // 내장(self-contained) 실행 여부: 런타임 디렉터리가 공유 프레임워크(…\dotnet\shared\Microsoft.NETCore.App\…)가 아니면 내장.
        bool selfContained;
        try
        {
            selfContained = !RuntimeEnvironment.GetRuntimeDirectory()
                .Contains(Path.Combine("shared", "Microsoft.NETCore.App"), StringComparison.OrdinalIgnoreCase);
        }
        catch { selfContained = false; }

        // 시스템 설치 확인: 표준 위치(Program Files\dotnet)에 현재 메이저 버전의 세 공유 프레임워크가 있는가.
        // (exe를 더블클릭으로 실행할 때 apphost가 보는 위치)
        var missing = new List<string>();
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet");
        foreach (var fx in RequiredFrameworks)
        {
            bool ok = false;
            try
            {
                var dir = Path.Combine(root, "shared", fx);
                ok = Directory.Exists(dir) && Directory.EnumerateDirectories(dir, major + ".*").Any();
            }
            catch { /* 접근 불가 → 미설치 취급 */ }
            if (!ok) missing.Add(fx);
        }

        var result = new
        {
            major,
            selfContained,
            systemOk = missing.Count == 0,
            missing,
            link = $"https://dotnet.microsoft.com/download/dotnet/{major}.0",
        };
        _cache = result; _cacheAtMs = now;
        return result;
    }
}
