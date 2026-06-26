using System.Diagnostics;
using System.Security.Principal;
using InputInterceptorNS;
using Nefarius.ViGEm.Client;

namespace YInput.Input;

/// <summary>현재 드라이버 설치/권한 상태.</summary>
public sealed record DriverStatus(
    bool InterceptionInstalled,
    bool ViGEmInstalled,
    bool IsAdministrator);

/// <summary>자동 설치 시도 결과.</summary>
public sealed record ProvisionResult(
    bool InterceptionInstalled,
    bool ViGEmInstalled,
    bool RebootRequired,
    IReadOnlyList<string> Messages);

/// <summary>
/// 커널 드라이버(Interception, ViGEmBus)를 탐지하고, 미설치 시 자동(사일런트) 설치한다.
/// Interception은 NuGet에 드라이버가 내장돼 코드로 설치되며, ViGEmBus는 <see cref="DriversFolder"/>에
/// 동봉한 설치 파일을 실행한다. 둘 다 관리자 권한이 필요하다.
/// </summary>
public static class DriverProvisioner
{
    /// <summary>동봉 드라이버 설치 파일 위치(실행 폴더 하위 drivers/).</summary>
    public static string DriversFolder { get; } =
        Path.Combine(AppContext.BaseDirectory, "drivers");

    public static DriverStatus QueryStatus() =>
        new(IsInterceptionInstalled(), IsViGEmInstalled(), IsAdministrator());

    public static bool IsInterceptionInstalled()
    {
        try { return InputInterceptor.CheckDriverInstalled(); }
        catch { return false; }
    }

    /// <summary>ViGEmBus 존재 여부 — 버스에 실제로 연결해 본다(가장 신뢰도 높은 탐지).</summary>
    public static bool IsViGEmInstalled()
    {
        try
        {
            using var client = new ViGEmClient();
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool IsAdministrator()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>미설치 드라이버를 자동 설치한다(관리자 필요). 멱등 — 이미 있으면 건너뜀.</summary>
    public static ProvisionResult EnsureInstalled()
    {
        var messages = new List<string>();
        bool reboot = false;
        bool admin = IsAdministrator();

        // --- Interception (드라이버 NuGet 내장, 코드로 설치) ---
        bool interception = IsInterceptionInstalled();
        if (interception)
        {
            messages.Add("Interception: 이미 설치됨.");
        }
        else if (!admin)
        {
            messages.Add("Interception 미설치 — 관리자 권한으로 다시 실행해야 설치할 수 있습니다.");
        }
        else
        {
            try
            {
                if (InputInterceptor.InstallDriver())
                {
                    interception = true;
                    reboot = true; // 필터 드라이버는 적용에 재부팅 필요
                    messages.Add("Interception 드라이버 설치 완료 — 적용하려면 재부팅이 필요합니다.");
                }
                else
                {
                    messages.Add("Interception 드라이버 설치에 실패했습니다.");
                }
            }
            catch (Exception ex)
            {
                messages.Add("Interception 설치 오류: " + ex.Message);
            }
        }

        // --- ViGEmBus (동봉 설치 파일 사일런트 실행) ---
        bool vigem = IsViGEmInstalled();
        if (vigem)
        {
            messages.Add("ViGEmBus: 이미 설치됨.");
        }
        else if (!admin)
        {
            messages.Add("ViGEmBus 미설치 — 관리자 권한으로 다시 실행해야 설치할 수 있습니다.");
        }
        else
        {
            var installer = FindViGEmInstaller();
            if (installer is null)
            {
                messages.Add(
                    $"ViGEmBus 설치 파일을 찾지 못했습니다. '{DriversFolder}' 폴더에 " +
                    "ViGEmBus 설치 파일(ViGEmBus_*.exe 또는 .msi)을 두면 자동 설치됩니다.");
            }
            else
            {
                try
                {
                    var (ok, rebootNeeded, detail) = RunSilentInstaller(installer);
                    if (ok)
                    {
                        vigem = IsViGEmInstalled();
                        reboot |= rebootNeeded;
                        messages.Add($"ViGEmBus 설치 실행 완료({detail}).");
                    }
                    else
                    {
                        messages.Add($"ViGEmBus 설치 실패({detail}).");
                    }
                }
                catch (Exception ex)
                {
                    messages.Add("ViGEmBus 설치 오류: " + ex.Message);
                }
            }
        }

        return new ProvisionResult(interception, vigem, reboot, messages);
    }

    private static string? FindViGEmInstaller()
    {
        if (!Directory.Exists(DriversFolder)) return null;
        return Directory.EnumerateFiles(DriversFolder, "ViGEmBus*.exe").FirstOrDefault()
            ?? Directory.EnumerateFiles(DriversFolder, "ViGEmBus*.msi").FirstOrDefault();
    }

    private static (bool ok, bool reboot, string detail) RunSilentInstaller(string path)
    {
        ProcessStartInfo psi;
        if (path.EndsWith(".msi", StringComparison.OrdinalIgnoreCase))
        {
            psi = new ProcessStartInfo("msiexec.exe", $"/i \"{path}\" /qn /norestart");
        }
        else
        {
            // WiX/Inno 번들 — 일반적인 사일런트 플래그
            psi = new ProcessStartInfo(path, "/quiet /norestart");
        }
        psi.UseShellExecute = false; // 호스트가 이미 관리자 → 자식도 승격 상속
        psi.CreateNoWindow = true;

        using var proc = Process.Start(psi);
        if (proc is null) return (false, false, "프로세스 시작 실패");

        if (!proc.WaitForExit(180_000))
        {
            try { proc.Kill(true); } catch { /* ignore */ }
            return (false, false, "설치 시간 초과");
        }

        int code = proc.ExitCode;
        bool reboot = code == 3010;               // ERROR_SUCCESS_REBOOT_REQUIRED
        bool ok = code == 0 || code == 3010;
        return (ok, reboot, $"exit={code}");
    }
}
