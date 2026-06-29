# Y Input 아이콘 생성기: "Y" 로고를 여러 해상도로 렌더해 멀티사이즈 PNG-ICO로 저장.
# 결과: src\YInput.Host\app.ico (exe 아이콘 + 트레이 아이콘 공용)
# 실행: powershell -ExecutionPolicy Bypass -File tools\make-icon.ps1
Add-Type -AssemblyName System.Drawing

$out = Join-Path $PSScriptRoot '..\src\YInput.Host\app.ico'
$out = [System.IO.Path]::GetFullPath($out)
$sizes = 16, 24, 32, 48, 64, 128, 256

function New-YPng([int]$s) {
    $bmp = New-Object System.Drawing.Bitmap($s, $s, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)

    # 둥근 사각 배경(#181b22)
    $r = [float]($s * 0.22)
    $d = [float]($r * 2)
    $w = [float]($s - 1)
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc(0, 0, $d, $d, 180, 90)
    $path.AddArc($w - $d, 0, $d, $d, 270, 90)
    $path.AddArc($w - $d, $w - $d, $d, $d, 0, 90)
    $path.AddArc(0, $w - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    $bg = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 24, 27, 34))
    $g.FillPath($bg, $path)

    # "Y" — 좌우 대각선 + 아래 줄기 (그라디언트 #4f8cff -> #3b6fd6)
    $pw = [float][Math]::Max(2.0, $s * 0.13)
    $c1 = [System.Drawing.Color]::FromArgb(255, 79, 140, 255)
    $c2 = [System.Drawing.Color]::FromArgb(255, 59, 111, 214)
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.PointF(0, 0)),
        (New-Object System.Drawing.PointF($s, $s)), $c1, $c2)
    $pen = New-Object System.Drawing.Pen($brush, $pw)
    $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round

    $lx = [float]($s * 0.30); $rx = [float]($s * 0.70)
    $ty = [float]($s * 0.30); $mx = [float]($s * 0.50); $my = [float]($s * 0.56)
    $by = [float]($s * 0.74)
    $g.DrawLine($pen, $lx, $ty, $mx, $my)
    $g.DrawLine($pen, $rx, $ty, $mx, $my)
    $g.DrawLine($pen, $mx, $my, $mx, $by)

    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return , $ms.ToArray()
}

$pngs = foreach ($s in $sizes) { , (New-YPng $s) }

# ICO 컨테이너 조립(모든 항목 PNG; Windows Vista+ 지원)
$fs = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter($fs)
$bw.Write([uint16]0)            # reserved
$bw.Write([uint16]1)            # type = icon
$bw.Write([uint16]$sizes.Count) # count

$offset = 6 + (16 * $sizes.Count)
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $len = $pngs[$i].Length
    $dim = if ($s -ge 256) { 0 } else { $s }
    $bw.Write([byte]$dim)        # width  (0 = 256)
    $bw.Write([byte]$dim)        # height
    $bw.Write([byte]0)           # palette
    $bw.Write([byte]0)           # reserved
    $bw.Write([uint16]1)         # planes
    $bw.Write([uint16]32)        # bit count
    $bw.Write([uint32]$len)      # bytes in resource
    $bw.Write([uint32]$offset)   # image offset
    $offset += $len
}
foreach ($p in $pngs) { $bw.Write($p) }
$bw.Flush()
[System.IO.File]::WriteAllBytes($out, $fs.ToArray())
$bw.Dispose()
Write-Output ("Wrote " + $out + " (" + (Get-Item $out).Length + " bytes, sizes: " + ($sizes -join ',') + ")")
