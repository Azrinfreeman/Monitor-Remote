param([string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
# WPF parses the filled SVG paths; geometry is never redrawn in this exporter.
Add-Type -AssemblyName PresentationCore, WindowsBase
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$target = if ($OutputDirectory) { [IO.Path]::GetFullPath($OutputDirectory) } else { $root }
$assets = Join-Path $target 'desktop/assets'
$web = Join-Path $target 'wwwroot'
$logo = Join-Path $assets 'logo'
foreach ($directory in @($assets, $web, $logo)) { [IO.Directory]::CreateDirectory($directory) | Out-Null }
$culture = [Globalization.CultureInfo]::InvariantCulture
function Read-Master([string]$name) {
    [xml]$svg = Get-Content -LiteralPath (Join-Path $PSScriptRoot $name) -Raw
    if ($svg.svg.viewBox -ne '0 0 48 48') { throw "Unsupported viewBox in $name" }
    $paths = @()
    foreach ($element in $svg.svg.ChildNodes) {
        if ($element.NodeType -ne 'Element') { continue }
        if ($element.LocalName -ne 'path' -or $element.HasAttribute('transform') -or $element.HasAttribute('stroke')) {
            throw "Unsupported SVG element in $name; use untransformed filled paths."
        }
        $geometry = [Windows.Media.Geometry]::Parse($element.GetAttribute('d'))
        $geometry.Freeze(); $paths += $geometry
    }
    if (!$paths.Count) { throw "No paths in $name" }
    return ,$paths
}
$master = Read-Master 'monitor-remote.svg'
$smallMaster = Read-Master 'monitor-remote-small.svg'
$navy = [Windows.Media.BrushConverter]::new().ConvertFromString('#102d40')
$white = [Windows.Media.Brushes]::White
function Render-Mark($context, $paths, $brush) {
    foreach ($path in $paths) { $context.DrawGeometry($brush, $null, $path) }
}
function Save-Png($visual, [int]$width, [int]$height, [string]$path) {
    $bitmap = [Windows.Media.Imaging.RenderTargetBitmap]::new($width, $height, 96, 96, [Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    $encoder = [Windows.Media.Imaging.PngBitmapEncoder]::new()
    $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $stream = [IO.File]::Create($path)
    try { $encoder.Save($stream) } finally { $stream.Dispose() }
}
function Export-Icon([int]$size, [string]$path, [bool]$rounded) {
    $visual = [Windows.Media.DrawingVisual]::new(); $context = $visual.RenderOpen()
    $context.PushTransform([Windows.Media.ScaleTransform]::new($size / 48.0, $size / 48.0))
    $radius = if ($rounded) { 10 } else { 0 }
    $context.DrawRoundedRectangle($navy, $null, [Windows.Rect]::new(0, 0, 48, 48), $radius, $radius)
    $paths = if ($size -le 32) { $smallMaster } else { $master }
    Render-Mark $context $paths $white
    $context.Pop(); $context.Close()
    Save-Png $visual $size $size $path
}
function Write-Ico([int[]]$sizes, [string]$path) {
    $frames = @()
    foreach ($size in $sizes) {
        $framePath = Join-Path $env:TEMP ('monitor-remote-' + [Guid]::NewGuid().ToString('N') + '.png')
        try { Export-Icon $size $framePath $true; $frames += ,([IO.File]::ReadAllBytes($framePath)) }
        finally { if (Test-Path -LiteralPath $framePath) { Remove-Item -LiteralPath $framePath } }
    }
    $writer = [IO.BinaryWriter]::new([IO.File]::Create($path))
    try {
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
    } finally { $writer.Dispose() }
}
Write-Ico @(16,20,24,32,40,48,64,128,256) (Join-Path $assets 'monitor-remote.ico')
Write-Ico @(16,20,24,32,48) (Join-Path $web 'favicon.ico')
Export-Icon 256 (Join-Path $assets 'monitor-remote.png') $true
foreach ($size in @(180,192,512)) { Export-Icon $size (Join-Path $web "icon-$size.png") $false }
# SVG lockups retain editable text; PNGs freeze the installed typography.
# No font binaries or converted font outlines are distributed.
$typeface = [Windows.Media.Typeface]::new([Windows.Media.FontFamily]::new('Segoe UI Variable'), [Windows.FontStyles]::Normal, [Windows.FontWeights]::SemiBold, [Windows.FontStretches]::Normal)
$text = [Windows.Media.FormattedText]::new('Monitor Remote', $culture, [Windows.FlowDirection]::LeftToRight, $typeface, 32, $navy, 1.0)
$width = [int][Math]::Ceiling(64 + $text.WidthIncludingTrailingWhitespace + 8)
$textTop = (64 - $text.Height) / 2
$baseline = ($textTop + $text.Baseline).ToString('0.###', $culture)
[xml]$source = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'monitor-remote.svg') -Raw
$geometry = ($source.svg.ChildNodes | Where-Object NodeType -eq 'Element' | ForEach-Object OuterXml) -join ''
foreach ($variant in @('dark','white','black')) {
    $color = switch ($variant) { 'white' { '#ffffff' } 'black' { '#000000' } default { '#102d40' } }
    $symbol = '<svg xmlns="http://www.w3.org/2000/svg" width="48" height="48" viewBox="0 0 48 48" fill="{0}">{1}</svg>' -f $color, $geometry
    [IO.File]::WriteAllText((Join-Path $logo "symbol-$variant.svg"), $symbol + "`n")
}
foreach ($variant in @('dark','white')) {
    $color = if ($variant -eq 'white') { '#ffffff' } else { '#102d40' }
    $brush = if ($variant -eq 'white') { $white } else { $navy }
    $svg = '<svg xmlns="http://www.w3.org/2000/svg" width="{0}" height="64" viewBox="0 0 {0} 64" role="img" aria-label="Monitor Remote"><g transform="translate(0 8)" fill="{1}">{2}</g><text x="64" y="{3}" fill="{1}" font-family="Segoe UI Variable, system-ui, sans-serif" font-size="32" font-weight="600">Monitor Remote</text></svg>' -f $width, $color, $geometry, $baseline
    [IO.File]::WriteAllText((Join-Path $logo "monitor-remote-$variant.svg"), $svg + "`n")
    $visual = [Windows.Media.DrawingVisual]::new(); $context = $visual.RenderOpen()
    $context.PushTransform([Windows.Media.ScaleTransform]::new(2, 2))
    $context.PushTransform([Windows.Media.TranslateTransform]::new(0, 8))
    Render-Mark $context $master $brush
    $context.Pop(); $text.SetForegroundBrush($brush)
    $context.DrawText($text, [Windows.Point]::new(64, $textTop))
    $context.Pop(); $context.Close()
    Save-Png $visual ($width * 2) 128 (Join-Path $logo "monitor-remote-$variant.png")
}
Write-Output "Brand assets generated with Windows WPF ($([Environment]::Version)) in $target"
