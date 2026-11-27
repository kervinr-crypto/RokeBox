# Renders the RokeBox icon (same design as the Android launcher icon) as a multi-size .ico.
# Usage: pwsh tools/make-icon.ps1  ->  agent/PhoneBox.Agent/phonebox.ico
Add-Type -AssemblyName System.Drawing
$outDir = Join-Path $PSScriptRoot '..\agent\PhoneBox.Agent'
$out = Join-Path (Resolve-Path -LiteralPath $outDir).Path 'phonebox.ico'

function New-RoundRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $p.AddArc($x, $y, $d, $d, 180, 90); $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90); $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

function New-Brush([string]$hex) { New-Object System.Drawing.SolidBrush ([System.Drawing.ColorTranslator]::FromHtml($hex)) }

function New-IconPng([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)
    $s = $size / 256.0

    # RokeBox mark (the same as the Android launcher icon and the web pages): a graphite plate, a display window cut
    # into it with a white R and a rose lamp, and the panel's page scale under it with the current page lit.
    $g.FillPath((New-Brush '#1F232A'), (New-RoundRect 0 0 ($size - 1) ($size - 1) (60 * $s)))
    $edge = New-Object System.Drawing.Pen ([System.Drawing.ColorTranslator]::FromHtml('#0C0E11')), ([Math]::Max(1, 6 * $s))
    $g.DrawPath($edge, (New-RoundRect (3 * $s) (3 * $s) ($size - 1 - 6 * $s) ($size - 1 - 6 * $s) (57 * $s)))
    $g.FillPath((New-Brush '#090B0E'), (New-RoundRect (44 * $s) (44 * $s) (168 * $s) (124 * $s) (24 * $s)))
    $white = [System.Drawing.ColorTranslator]::FromHtml('#EEF1F4')
    $g.FillRectangle((New-Object System.Drawing.SolidBrush $white), 84 * $s, 68 * $s, 18.4 * $s, 76 * $s)
    $stroke = New-Object System.Drawing.Pen $white, (18.4 * $s)
    $bowl = New-Object System.Drawing.Drawing2D.GraphicsPath
    $bowl.AddLine(93.2 * $s, 76.8 * $s, 123.6 * $s, 76.8 * $s)
    $bowl.AddArc(105.2 * $s, 76.8 * $s, 36.8 * $s, 36.8 * $s, -90, 180)
    $bowl.AddLine(123.6 * $s, 113.6 * $s, 93.2 * $s, 113.6 * $s)
    $g.DrawPath($stroke, $bowl)
    $stroke.StartCap = 'Round'; $stroke.EndCap = 'Round'
    $g.DrawLine($stroke, 120.8 * $s, 120 * $s, 145.6 * $s, 146.4 * $s)
    $g.FillEllipse((New-Brush '#FB7185'), 172.4 * $s, 62.4 * $s, 19.2 * $s, 19.2 * $s)
    if ($size -ge 32) {
        $lip = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(32, 255, 255, 255)), ([Math]::Max(1, 4 * $s))
        $g.DrawLine($lip, 56 * $s, 170 * $s, 200 * $s, 170 * $s)
        $tick = New-Object System.Drawing.Pen ([System.Drawing.ColorTranslator]::FromHtml('#5C636E')), (7.2 * $s)
        $tick.StartCap = 'Round'; $tick.EndCap = 'Round'
        foreach ($x in 88, 108, 148, 168) { $g.DrawLine($tick, $x * $s, 196 * $s, $x * $s, 212 * $s) }
        $lit = New-Object System.Drawing.Pen ([System.Drawing.ColorTranslator]::FromHtml('#FB7185')), (8.8 * $s)
        $lit.StartCap = 'Round'; $lit.EndCap = 'Round'
        $g.DrawLine($lit, 128 * $s, 190 * $s, 128 * $s, 218 * $s)
    }

    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return , $ms.ToArray()
}

$sizes = 16, 24, 32, 48, 64, 128, 256
$images = foreach ($sz in $sizes) { , (New-IconPng $sz) }

$fs = [System.IO.File]::Create($out)
$bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $sz = $sizes[$i]; $len = $images[$i].Length
    $dim = if ($sz -ge 256) { 0 } else { $sz }
    $bw.Write([byte]$dim); $bw.Write([byte]$dim)
    $bw.Write([byte]0); $bw.Write([byte]0); $bw.Write([uint16]1); $bw.Write([uint16]32)
    $bw.Write([uint32]$len); $bw.Write([uint32]$offset)
    $offset += $len
}
foreach ($img in $images) { $bw.Write([byte[]]$img) }
$bw.Close()
"icon written: $out ($((Get-Item $out).Length) bytes)"
