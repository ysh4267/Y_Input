# External macro tool (XML) -> Y Input macro (JSON) converter.
#   Reads ./macros/*.xml and writes %APPDATA%\YInput\macros\{guid}.json
#   Map: Type0=delay(sec), Type1=key(ScanCode + State 0/1/2/3),
#        Type2=mouse, Type6=loop(LoopEvent State0 start/Number count, State1 end),
#        Type7=run another macro (by guid). actionBar etc. ignored.
# Run: powershell -ExecutionPolicy Bypass -File tools\convert-macros.ps1
$ErrorActionPreference = 'Stop'
$inv = [Globalization.CultureInfo]::InvariantCulture
# Some KeyEvents only have <Makecode> (Win32 VK) with no <ScanCode>; derive scancode from VK.
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class VkMap { [DllImport("user32.dll")] public static extern uint MapVirtualKey(uint code, uint mapType); }
"@
$srcDir = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\macros'))
$outDir = Join-Path $env:APPDATA 'YInput\macros'
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

$files = Get-ChildItem -Path $srcDir -Filter *.xml
$stripGuid = { param($g) ($g -replace '[^0-9a-fA-F]', '').ToLower() }
$jsonStr = { param($s) if ($null -eq $s) { '""' } else { $s | ConvertTo-Json -Compress } }

# Pass 1: guid -> name (for macro-reference display labels)
$nameByGuid = @{}
foreach ($f in $files) {
    [xml]$doc = Get-Content -Raw -Encoding UTF8 $f.FullName
    $id = & $stripGuid $doc.Macro.Guid
    if ($id) { $nameByGuid[$id] = [string]$doc.Macro.Name }
}

# Pass 2: convert
$count = 0; $warns = @()
foreach ($f in $files) {
    [xml]$doc = Get-Content -Raw -Encoding UTF8 $f.FullName
    $name = [string]$doc.Macro.Name
    $id = & $stripGuid $doc.Macro.Guid
    if (-not $id) { $warns += "no guid: $($f.Name) (skipped)"; continue }

    $steps = New-Object System.Collections.Generic.List[string]
    foreach ($e in $doc.Macro.MacroEvents.MacroEvent) {
        switch ([string]$e.Type) {
            '0' {
                $ms = [int][math]::Round([double]::Parse([string]$e.Number, $inv) * 1000)
                $steps.Add("{`"delayBeforeMs`":$ms,`"event`":{`"`$type`":`"delay`",`"randomizePercent`":0}}")
            }
            '1' {
                if ($e.ScanCode -and [int]$e.ScanCode -ne 0) { $code = [int]$e.ScanCode }
                else { $code = [int][VkMap]::MapVirtualKey([uint32][int]$e.KeyEvent.Makecode, 0) }  # VK -> scancode
                $state = [int]$e.KeyEvent.State
                $steps.Add("{`"delayBeforeMs`":0,`"event`":{`"`$type`":`"keyboard`",`"code`":$code,`"state`":$state}}")
            }
            '2' {
                $btn = [int]$e.MouseEvent.MouseButton
                $st = [int]$e.MouseEvent.State
                $bs = switch ($btn) {
                    0 { if ($st -eq 0) { 1 } else { 2 } }
                    1 { if ($st -eq 0) { 4 } else { 8 } }
                    2 { if ($st -eq 0) { 16 } else { 32 } }
                    default { 0 }
                }
                $steps.Add("{`"delayBeforeMs`":0,`"event`":{`"`$type`":`"mouse`",`"buttonState`":$bs,`"flags`":0,`"rolling`":0,`"x`":0,`"y`":0}}")
            }
            '6' {
                $st = [int]$e.LoopEvent.State
                if ($st -eq 0) {
                    $n = 2; if ($e.Number) { $n = [int][math]::Round([double]::Parse([string]$e.Number, $inv)) }
                    if ($n -lt 1) { $n = 1 }
                    $steps.Add("{`"delayBeforeMs`":0,`"event`":{`"`$type`":`"loopStart`",`"count`":$n}}")
                } else {
                    $steps.Add("{`"delayBeforeMs`":0,`"event`":{`"`$type`":`"loopEnd`"}}")
                }
            }
            '7' {
                $ref = & $stripGuid $e.guid
                $rn = ''
                if ($nameByGuid.ContainsKey($ref)) { $rn = $nameByGuid[$ref] } else { $warns += "$name -> referenced macro missing (guid $ref)" }
                $steps.Add("{`"delayBeforeMs`":0,`"event`":{`"`$type`":`"macroRef`",`"macroId`":`"$ref`",`"name`":$(& $jsonStr $rn)}}")
            }
            default { }
        }
    }

    $json = "{`"id`":`"$id`",`"name`":$(& $jsonStr $name),`"steps`":[$([string]::Join(',', $steps))],`"loopCount`":1,`"speedMultiplier`":1.0,`"randomizeDelayPercent`":0,`"trigger`":null,`"enabled`":false}"
    $null = $json | ConvertFrom-Json   # validate JSON
    $path = Join-Path $outDir "$id.json"
    [System.IO.File]::WriteAllText($path, $json, (New-Object System.Text.UTF8Encoding $false))
    Write-Output ("OK  {0,-28} {1,3} steps -> {2}.json" -f $name, $steps.Count, $id.Substring(0, 8))
    $count++
}
Write-Output ""
Write-Output "Converted: $count -> $outDir"
if ($warns.Count) { Write-Output "Warnings:"; $warns | ForEach-Object { Write-Output "  - $_" } }
