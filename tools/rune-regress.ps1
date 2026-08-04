# Rune regression harness: replay all fixture sets through the offline CLI and
# compare judgment lines against each set's expected.txt directives.
# Usage: powershell -File tools\rune-regress.ps1 [-NoBuild] [-Set <name-filter>] [-Dll <path>]
# Exit code = number of failed sets (0 = all green).
#
# expected.txt directives (UTF-8, # comments allowed):
#   run: <cli args...>        starts a job. *.png globs expand (name-sorted) in the set dir.
#   out: <file>               output file of the current job (relative to set dir).
#   re: <regex>               file content must match.
#   forbid: <regex>           file content must NOT match.
#   dirs: <anchor> = <arrows> first line containing <anchor>: its arrow chars must equal sequence.
#   xs: <anchor> = x1 x2 x3 x4 ~tol   first line containing <anchor>: first 4 "(x," values within tol.
#   rowxs: <anchor> = x1 x2 x3 x4 ~tol  row-diagnostic line ("... -> (x,y)a...") within 4 lines after anchor.
param(
    [string]$Dll = "",
    [switch]$NoBuild,
    [string]$Set = "*"
)
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
if (-not $Dll) { $Dll = Join-Path $root "src\YInput.Host\bin\Release\net10.0-windows\YInput.dll" }
$dotnet = "dotnet"
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { $dotnet = Join-Path $env:USERPROFILE ".dotnet\dotnet.exe" }

if (-not $NoBuild) {
    & $dotnet build (Join-Path $root "src\YInput.Host\YInput.Host.csproj") -c Release --nologo -v q | Out-Null
    if ($LASTEXITCODE -ne 0) { Write-Output "BUILD FAILED"; exit 99 }
}
if (-not (Test-Path $Dll)) { Write-Output "DLL NOT FOUND: $Dll"; exit 99 }

function Get-Arrows([string]$line) {
    $m = [regex]::Matches($line, '[←↑→↓]')  # left/up/right/down arrows
    return @($m | ForEach-Object { $_.Value })
}
function Get-Xs([string]$line) {
    $m = [regex]::Matches($line, '\((-?\d+)')
    return @($m | ForEach-Object { [int]$_.Groups[1].Value })
}

$totalFail = 0
$setDirs = Get-ChildItem (Join-Path $root "tests\fixtures") -Directory |
    Where-Object { $_.Name -like $Set -and (Test-Path (Join-Path $_.FullName "expected.txt")) } |
    Sort-Object Name

foreach ($dir in $setDirs) {
    $lines = Get-Content (Join-Path $dir.FullName "expected.txt") -Encoding UTF8
    $problems = New-Object System.Collections.Generic.List[string]
    $jobArgs = $null; $outFile = $null; $checks = New-Object System.Collections.Generic.List[object]

    # Run one job (CLI invocation) and evaluate its queued checks.
    function Invoke-Job {
        if ($null -eq $jobArgs) { return }
        if (-not $outFile) { $script:problems.Add("expected.txt: job without out:"); return }
        $outPath = Join-Path $dir.FullName $outFile
        if (Test-Path $outPath) { Remove-Item $outPath -Force }   # stale output must not mask failures
        $resolved = New-Object System.Collections.Generic.List[string]
        foreach ($tok in ($jobArgs -split '\s+')) {
            if ($tok -eq "") { continue }
            if ($tok.Contains("*")) {
                Get-ChildItem (Join-Path $dir.FullName $tok) | Sort-Object Name | ForEach-Object { $resolved.Add($_.FullName) }
            } elseif (Test-Path (Join-Path $dir.FullName $tok)) {
                $resolved.Add((Join-Path $dir.FullName $tok))
            } else {
                $resolved.Add($tok)   # e.g. --rune-analyze, pos=...
            }
        }
        & $dotnet $Dll @resolved 2>$null | Out-Null
        if (-not (Test-Path $outPath)) { $problems.Add("no output: $outFile"); return }
        $content = Get-Content $outPath -Raw -Encoding UTF8
        $textLines = $content -split "`r?`n"
        foreach ($c in $checks) {
            switch ($c.Kind) {
                "re"     { if (-not [regex]::IsMatch($content, $c.Arg)) { $problems.Add("re missing: $($c.Arg)") } }
                "forbid" { if ([regex]::IsMatch($content, $c.Arg)) { $problems.Add("forbidden present: $($c.Arg)") } }
                "dirs" {
                    $anchor, $want = $c.Arg -split '\s*=\s*', 2
                    $line = $textLines | Where-Object { $_.Contains($anchor.Trim()) } | Select-Object -First 1
                    if (-not $line) { $problems.Add("dirs anchor missing: $anchor"); break }
                    # anchor label itself may contain arrows/parens - judge only the part after the last colon
                    if ($line.LastIndexOf(':') -ge 0) { $line = $line.Substring($line.LastIndexOf(':') + 1) }
                    $got = (Get-Arrows $line) -join " "
                    if ($got -ne $want.Trim()) { $problems.Add("dirs [$($anchor.Trim())]: got '$got' want '$($want.Trim())'") }
                }
                "xs" {
                    $anchor, $spec = $c.Arg -split '\s*=\s*', 2
                    $parts = $spec.Trim() -split '\s+'
                    $tol = 0; if ($parts[-1] -like "~*") { $tol = [int]$parts[-1].Substring(1); $parts = $parts[0..($parts.Count - 2)] }
                    $want = @($parts | ForEach-Object { [int]$_ })
                    $line = $textLines | Where-Object { $_.Contains($anchor.Trim()) } | Select-Object -First 1
                    if (-not $line) { $problems.Add("xs anchor missing: $anchor"); break }
                    # anchor label may contain "(3개+외삽)" 류 괄호 숫자 - coords live after the last colon
                    if ($line.LastIndexOf(':') -ge 0) { $line = $line.Substring($line.LastIndexOf(':') + 1) }
                    $got = Get-Xs $line
                    if ($got.Count -lt $want.Count) { $problems.Add("xs [$($anchor.Trim())]: only $($got.Count) coords"); break }
                    for ($i = 0; $i -lt $want.Count; $i++) {
                        if ([math]::Abs($got[$i] - $want[$i]) -gt $tol) { $problems.Add("xs [$($anchor.Trim())] #$($i+1): got $($got[$i]) want $($want[$i])+-$tol") }
                    }
                }
                "rowxs" {
                    $anchor, $spec = $c.Arg -split '\s*=\s*', 2
                    $parts = $spec.Trim() -split '\s+'
                    $tol = 0; if ($parts[-1] -like "~*") { $tol = [int]$parts[-1].Substring(1); $parts = $parts[0..($parts.Count - 2)] }
                    $want = @($parts | ForEach-Object { [int]$_ })
                    $idx = -1
                    for ($i = 0; $i -lt $textLines.Count; $i++) { if ($textLines[$i].Contains($anchor.Trim())) { $idx = $i; break } }
                    if ($idx -lt 0) { $problems.Add("rowxs anchor missing: $anchor"); break }
                    $rowLine = $null
                    for ($i = $idx; $i -lt [math]::Min($idx + 5, $textLines.Count); $i++) {
                        if ($textLines[$i] -match '→ \(') { $rowLine = $textLines[$i]; break }   # "-> (" row line
                    }
                    if (-not $rowLine) { $problems.Add("rowxs [$($anchor.Trim())]: no row line"); break }
                    $tail = $rowLine.Substring($rowLine.IndexOf([char]0x2192))
                    $got = Get-Xs $tail
                    if ($got.Count -lt $want.Count) { $problems.Add("rowxs [$($anchor.Trim())]: only $($got.Count) coords"); break }
                    for ($i = 0; $i -lt $want.Count; $i++) {
                        if ([math]::Abs($got[$i] - $want[$i]) -gt $tol) { $problems.Add("rowxs [$($anchor.Trim())] #$($i+1): got $($got[$i]) want $($want[$i])+-$tol") }
                    }
                }
            }
        }
    }

    foreach ($raw in $lines) {
        $line = $raw.Trim()
        if ($line -eq "" -or $line.StartsWith("#")) { continue }
        if ($line -match '^run:\s*(.+)$') { Invoke-Job; $jobArgs = $Matches[1]; $outFile = $null; $checks = New-Object System.Collections.Generic.List[object]; continue }
        if ($line -match '^out:\s*(.+)$') { $outFile = $Matches[1].Trim(); continue }
        if ($line -match '^(re|forbid|dirs|xs|rowxs):\s*(.+)$') { $checks.Add([pscustomobject]@{ Kind = $Matches[1]; Arg = $Matches[2] }); continue }
        $problems.Add("expected.txt: unknown directive: $line")
    }
    Invoke-Job

    if ($problems.Count -eq 0) {
        Write-Output "[PASS] $($dir.Name)"
    } else {
        $totalFail++
        Write-Output "[FAIL] $($dir.Name)"
        foreach ($p in $problems) { Write-Output "       $p" }
    }
}
Write-Output ("=" * 40)
Write-Output "sets: $($setDirs.Count)  failed: $totalFail"
exit $totalFail
