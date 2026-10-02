$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
$frames = @()
foreach ($size in $sizes) {
    $bitmap = [System.Drawing.Bitmap]::new(1024, 1024)
    $g = [System.Drawing.Graphics]::FromImage($bitmap)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.ScaleTransform(4, 4)
    $navy = [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml('#102d40'))
    $teal = [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml('#54dfce'))
    $gold = [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml('#ffd275'))
    function Fill-Rounded($brush, $x, $y, $w, $h, $radius) {
        $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
        $d = $radius * 2
        $path.AddArc($x, $y, $d, $d, 180, 90)
        $path.AddArc(($x+$w-$d), $y, $d, $d, 270, 90)
        $path.AddArc(($x+$w-$d), ($y+$h-$d), $d, $d, 0, 90)
        $path.AddArc($x, ($y+$h-$d), $d, $d, 90, 90)
        $path.CloseFigure()
        $g.FillPath($brush, $path)
        $path.Dispose()
    }
    Fill-Rounded $navy 8 8 240 240 54
    Fill-Rounded $teal 39 53 178 131 18
    Fill-Rounded $navy 51 65 154 107 8
    $pen = [System.Drawing.Pen]::new($teal, 12)
    $pen.StartCap = $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $g.DrawLine($pen, 128, 182, 128, 206)
    $g.DrawLine($pen, 93, 206, 163, 206)
    $pen.Dispose()
    $pen = [System.Drawing.Pen]::new($gold, 8)
    $pen.StartCap = $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    foreach ($line in @(@(128,80,128,86),@(128,151,128,157),@(89,118,95,118),@(161,118,167,118),@(101,91,106,96),@(151,141,156,146),@(101,146,106,141),@(151,96,156,91))) {
        $g.DrawLine($pen, $line[0], $line[1], $line[2], $line[3])
    }
    $g.FillEllipse($gold, 105, 95, 46, 46)
    $small = [System.Drawing.Bitmap]::new($size, $size)
    $sg = [System.Drawing.Graphics]::FromImage($small)
    $sg.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $sg.DrawImage($bitmap, 0, 0, $size, $size)
    $stream = [System.IO.MemoryStream]::new()
    $small.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
    $frames += ,($stream.ToArray())
    if ($size -eq 256) { $small.Save((Join-Path $PSScriptRoot 'monitor-remote.png'), [System.Drawing.Imaging.ImageFormat]::Png) }
    $stream.Dispose(); $sg.Dispose(); $small.Dispose(); $pen.Dispose()
    $g.Dispose(); $bitmap.Dispose(); $navy.Dispose(); $teal.Dispose(); $gold.Dispose()
}
$output = [System.IO.File]::Create((Join-Path $PSScriptRoot 'monitor-remote.ico'))
$writer = [System.IO.BinaryWriter]::new($output)
$writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $dimension = if ($sizes[$i] -eq 256) { 0 } else { $sizes[$i] }
    $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
    $writer.Write([byte]0); $writer.Write([byte]0)
    $writer.Write([uint16]1); $writer.Write([uint16]32)
    $writer.Write([uint32]$frames[$i].Length); $writer.Write([uint32]$offset)
    $offset += $frames[$i].Length
}
foreach ($frame in $frames) { $writer.Write([byte[]]$frame) }
$writer.Dispose()
