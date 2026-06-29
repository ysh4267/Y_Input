# Post-build redeploy: stop running YInput -> replace Build\YInput.exe -> launch new build.
# Usage: powershell -File tools\deploy.ps1   (after publish creates artifacts\single\YInput.exe)
param(
  [string]$Src = "N:\Projects\Y_Input\artifacts\single\YInput.exe",
  [string]$Dst = "N:\Projects\Y_Input\Build\YInput.exe",
  [switch]$NoLaunch
)

if (-not (Test-Path $Src)) { Write-Output "No new build: $Src (publish first)"; exit 1 }

$proc = Get-Process YInput -ErrorAction SilentlyContinue
if ($proc) {
  # 1) Graceful quit request - app exits itself (works even when elevated blocks external kill)
  $ports = @()
  try { $ports = Get-NetTCPConnection -OwningProcess $proc.Id -State Listen -ErrorAction Stop | Select-Object -ExpandProperty LocalPort -Unique } catch {}
  if (-not $ports) { $ports = @(48710) }
  $asked = $false
  foreach ($p in $ports) {
    try { Invoke-RestMethod -Method Post -Uri "http://127.0.0.1:$p/api/app/quit" -TimeoutSec 3 | Out-Null; $asked = $true; break } catch {}
  }
  Write-Output ("quit request: " + $(if ($asked) { "sent (port " + ($ports -join ',') + ")" } else { "no endpoint response" }))

  # 2) Wait for exit (~8s)
  for ($i = 0; $i -lt 40 -and -not $proc.HasExited; $i++) { Start-Sleep -Milliseconds 200; $proc.Refresh() }

  # 3) Still alive -> try force kill (may fail if elevated)
  if (-not $proc.HasExited) {
    try { Stop-Process -Id $proc.Id -Force -ErrorAction Stop; Start-Sleep -Milliseconds 400 } catch { Write-Output "force kill failed (needs admin) - using rename swap" }
  }
}

# 4) Replace (if locked, rename the running exe out of the way, then drop new one in)
try {
  Copy-Item $Src $Dst -Force -ErrorAction Stop
  Write-Output "swapped (direct copy)"
} catch {
  $bak = "YInput.prev_$(Get-Date -Format yyyyMMddHHmmss).exe"
  Rename-Item -LiteralPath $Dst -NewName $bak
  Copy-Item $Src $Dst -Force
  Write-Output "swapped (rename while running: $bak)"
}

$ok = (Get-FileHash $Src).Hash -eq (Get-FileHash $Dst).Hash
Write-Output ("Build up-to-date: " + $ok)

# 5) Launch new build (requireAdministrator manifest -> UAC consent dialog may appear)
if (-not $NoLaunch) {
  Start-Process $Dst
  Write-Output "launched new build"
}
