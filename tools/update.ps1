# Git update: pull -> publish single-file -> deploy (stop old, swap, relaunch).
# Launched detached by the app (POST /api/app/update). ASCII only (PS 5.1 cp949 safe).
# Requires: source tree + dotnet SDK + git on this PC.

$repo = "N:\Projects\Y_Input"
$logFile = Join-Path $repo "artifacts\update.log"
function Note($m) { Add-Content -Path $logFile -Value ("{0} {1}" -f (Get-Date -Format 'HH:mm:ss'), $m) }

Note "update start"

git -C $repo pull
if ($LASTEXITCODE -ne 0) { Note "git pull FAILED"; exit 1 }

dotnet publish "$repo\src\YInput.Host\YInput.Host.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -o "$repo\artifacts\single" --nologo -v minimal
if ($LASTEXITCODE -ne 0) { Note "publish FAILED"; exit 1 }

powershell -ExecutionPolicy Bypass -File "$repo\tools\deploy.ps1"
Note "update done"
