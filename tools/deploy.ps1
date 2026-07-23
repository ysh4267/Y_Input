# Post-build redeploy: stop running YInput -> install new build to the installed location -> launch it.
# App manifest is asInvoker, so launching does NOT show a UAC prompt.
# Usage: powershell -File tools\deploy.ps1   (after publish creates artifacts\single\YInput.exe)
param(
  [string]$Src = "N:\Projects\Y_Input\artifacts\single\YInput.exe",
  [string]$InstallDir = "$env:LOCALAPPDATA\Programs\YInput",
  [switch]$NoLaunch
)

if (-not (Test-Path $Src)) { Write-Output "No new build: $Src (publish first)"; exit 1 }

$Dst = Join-Path $InstallDir 'YInput.exe'
$BuildExe = 'N:\Projects\Y_Input\Build\YInput.exe'

# 1) Graceful quit request - app exits itself
$proc = Get-Process YInput -ErrorAction SilentlyContinue
if ($proc) {
  $ports = @()
  try { $ports = Get-NetTCPConnection -OwningProcess $proc.Id -State Listen -ErrorAction Stop | Select-Object -ExpandProperty LocalPort -Unique } catch {}
  if (-not $ports) { $ports = @(48710) }
  foreach ($p in $ports) {
    try { Invoke-RestMethod -Method Post -Uri "http://127.0.0.1:$p/api/app/quit" -TimeoutSec 3 | Out-Null; break } catch {}
  }
  for ($i = 0; $i -lt 40 -and -not $proc.HasExited; $i++) { Start-Sleep -Milliseconds 200; $proc.Refresh() }
  if (-not $proc.HasExited) { try { Stop-Process -Id $proc.Id -Force -ErrorAction Stop; Start-Sleep -Milliseconds 400 } catch { Write-Output "force kill failed" } }
}

# 2) Install new build to the installed location (+ refresh Build copy). Keep the install marker so it runs in place.
New-Item -ItemType Directory -Force $InstallDir | Out-Null
foreach ($t in @($Dst, $BuildExe)) {
  try { Copy-Item $Src $t -Force -ErrorAction Stop }
  catch {
    $bak = [IO.Path]::ChangeExtension($t, "old_$(Get-Date -Format yyyyMMddHHmmss).exe")
    Rename-Item -LiteralPath $t -NewName (Split-Path $bak -Leaf)
    Copy-Item $Src $t -Force
  }
}
$marker = Join-Path $InstallDir '.yinput_install'
if (-not (Test-Path $marker)) { Set-Content -Path $marker -Value 'Y Input install marker' -Encoding utf8 }
Write-Output ("installed: " + $Dst + " (" + (Get-Item $Dst).Length + " bytes)")

# 3) Launch installed build (asInvoker -> no UAC prompt)
if (-not $NoLaunch) {
  Start-Process $Dst
  Write-Output "launched installed build"
}
