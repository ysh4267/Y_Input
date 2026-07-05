using System.Diagnostics;
using System.Windows.Forms;

namespace YInput.Host;

/// <summary>
/// 업데이트 마무리 역할. 새로 내려받은 exe가 <c>--apply-update &lt;oldPid&gt; "&lt;정식경로&gt;"</c> 인자로 실행되면
/// 이 모드로 진입한다(정식 앱·단일 인스턴스 뮤텍스는 건드리지 않음). 옛 프로세스가 스스로 꺼지길 기다렸다가
/// 자기 자신을 정식 경로(YInput.exe)로 덮어쓰고, 정식 이름으로 새 버전을 실행한 뒤 종료한다.
/// 즉 "새 프로세스가 뜨면 옛 프로세스가 비켜주고, 새 것이 정식 자리로 들어앉는다".
/// </summary>
internal static class UpdateFinalizer
{
    /// <summary>업데이트 진행 로그(%APPDATA%\YInput\update.log). 실패 원인 추적용.</summary>
    public static void Log(string msg)
    {
        try { File.AppendAllText(LogPath(), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {msg}{Environment.NewLine}"); }
        catch { /* 로그 실패는 무시 */ }
    }

    // cmdArgs = Environment.GetCommandLineArgs(); idx = "--apply-update"의 위치. 이후 [idx+1]=oldPid, [idx+2]=정식경로
    public static void Run(string[] cmdArgs, int idx)
    {
        try
        {
            int oldPid = (idx + 1 < cmdArgs.Length && int.TryParse(cmdArgs[idx + 1], out var p)) ? p : 0;
            string canonical = idx + 2 < cmdArgs.Length ? cmdArgs[idx + 2] : "";
            string stage = Environment.ProcessPath ?? "";
            Log($"apply-update 시작: oldPid={oldPid} 정식='{canonical}' 스테이지='{stage}'");

            if (string.IsNullOrEmpty(canonical) || string.IsNullOrEmpty(stage))
            {
                Log("인자 부족 — 중단");
                return;
            }

            // 1) 옛 프로세스가 스스로 종료(비켜주기)하길 기다림 — 안 나가면 강제 종료해 실행 파일 잠금을 푼다.
            if (oldPid > 0)
            {
                try
                {
                    using var op = Process.GetProcessById(oldPid);
                    if (!op.WaitForExit(15000))
                    {
                        Log("옛 프로세스가 15초 후에도 살아있음 → 강제 종료");
                        try { op.Kill(); op.WaitForExit(4000); } catch (Exception ex) { Log("강제 종료 실패: " + ex.Message); }
                    }
                }
                catch { /* 이미 종료됨 */ }
            }
            Thread.Sleep(400); // 이미지 잠금/뮤텍스가 완전히 풀리도록 약간 더 대기

            // 2) 자기 자신(새 버전)을 정식 경로로 덮어쓰기 — 아직 잠겨 있을 수 있어 재시도(약 7.5초).
            bool copied = false; Exception? lastErr = null;
            for (int i = 0; i < 25 && !copied; i++)
            {
                try { File.Copy(stage, canonical, overwrite: true); copied = true; }
                catch (Exception ex) { lastErr = ex; Thread.Sleep(300); }
            }
            if (!copied)
            {
                Log("교체 실패: " + (lastErr?.Message ?? "알 수 없음"));
                ShowError("업데이트 교체에 실패했습니다: " + (lastErr?.Message ?? "실행 파일 잠김") + "\n앱을 완전히 종료한 뒤 다시 시도해 주세요.");
                return;
            }
            Log("교체 완료(스테이지 → 정식)");

            // 3) 정식 이름으로 새 버전 실행(--updated: 열린 탭이 재연결하므로 새 탭을 열지 않음). 관리자 권한은 상속됨.
            Process.Start(new ProcessStartInfo { FileName = canonical, Arguments = "--updated", UseShellExecute = true });
            Log("정식 실행(--updated) — 완료");
        }
        catch (Exception ex)
        {
            Log("apply-update 예외: " + ex);
            ShowError("업데이트 마무리 중 오류: " + ex.Message);
        }
    }

    private static string LogPath()
    {
        try
        {
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "YInput");
            Directory.CreateDirectory(root);
            return Path.Combine(root, "update.log");
        }
        catch { return Path.Combine(Path.GetTempPath(), "yinput-update.log"); }
    }

    private static void ShowError(string msg)
    {
        try { MessageBox.Show(msg, "Y Input 업데이트", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        catch { /* 무시 */ }
    }
}
