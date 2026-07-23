# Register an elevated (highest-privilege) scheduled task that launches installed YInput WITHOUT a UAC prompt.
# Run this ONCE. It needs admin (accept the one UAC). After that, tools/deploy.ps1 launches via the task, prompt-free.
$ErrorActionPreference = 'Stop'
$exe = Join-Path $env:LOCALAPPDATA 'Programs\YInput\YInput.exe'

$action = New-ScheduledTaskAction -Execute $exe
$user = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name
$principal = New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Highest
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
$settings.ExecutionTimeLimit = 'PT0S'  # no time limit (app runs indefinitely)
$settings.MultipleInstances = 'IgnoreNew'

Register-ScheduledTask -TaskName 'YInputDevRun' -Action $action -Principal $principal -Settings $settings -Force | Out-Null
Write-Output "Registered scheduled task 'YInputDevRun' for: $exe"
