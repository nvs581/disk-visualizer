$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore, WindowsBase
$destination = Join-Path $PSScriptRoot 'assets'
[IO.Directory]::CreateDirectory($destination) | Out-Null
$culture = [Globalization.CultureInfo]::InvariantCulture
function Point-At([double]$radius, [double]$angle) {
    $radians = $angle * [Math]::PI / 180
    return ((64 + $radius * [Math]::Cos($radians)).ToString('0.###', $culture) + ',' + (64 + $radius * [Math]::Sin($radians)).ToString('0.###', $culture))
}
function Ring-Path([double]$outer, [double]$inner, [double]$start, [double]$end) {
    $large = [int](($end - $start) -gt 180)
    return "M $(Point-At $outer $start) A $outer,$outer 0 $large 1 $(Point-At $outer $end) L $(Point-At $inner $end) A $inner,$inner 0 $large 0 $(Point-At $inner $start) Z"
}
$segments = @(
    @{ Color = '#B59AFF'; Path = (Ring-Path 47 34 -90 28) },
    @{ Color = '#8F75DE'; Path = (Ring-Path 47 34 36 146) },
    @{ Color = '#65C8EC'; Path = (Ring-Path 47 34 154 262) },
    @{ Color = '#B59AFF'; Path = (Ring-Path 28 15 -90 146) },
    @{ Color = '#65C8EC'; Path = (Ring-Path 28 15 154 262) }
)
$svg = '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 128 128"><rect x="2" y="2" width="124" height="124" rx="28" fill="#172131"/>'
foreach ($segment in $segments) { $svg += '<path fill="' + $segment.Color + '" d="' + $segment.Path + '"/>' }
$svg += '</svg>'
[IO.File]::WriteAllText((Join-Path $destination 'disk-visualizer.svg'), $svg, (New-Object Text.UTF8Encoding($false)))
$frames = @()
foreach ($size in @(16, 20, 24, 32, 48, 64, 128, 256)) {
    $visual = New-Object Windows.Media.DrawingVisual
    $drawing = $visual.RenderOpen()
    $drawing.PushTransform((New-Object Windows.Media.ScaleTransform ($size / 128.0), ($size / 128.0)))
    $brush = [Windows.Media.BrushConverter]::new().ConvertFromString('#172131')
    $drawing.DrawRoundedRectangle($brush, $null, [Windows.Rect]::new(2, 2, 124, 124), 28, 28)
    foreach ($segment in $segments) {
        $brush = [Windows.Media.BrushConverter]::new().ConvertFromString($segment.Color)
        $drawing.DrawGeometry($brush, $null, [Windows.Media.Geometry]::Parse($segment.Path))
    }
    $drawing.Pop(); $drawing.Close()
    $bitmap = [Windows.Media.Imaging.RenderTargetBitmap]::new($size, $size, 96, 96, [Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    $encoder = New-Object Windows.Media.Imaging.PngBitmapEncoder
    $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $stream = New-Object IO.MemoryStream
    $encoder.Save($stream)
    $frames += ,$stream.ToArray()
    $stream.Dispose()
}
$output = [IO.File]::Create((Join-Path $destination 'disk-visualizer.ico'))
$writer = [IO.BinaryWriter]::new($output)
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$frames.Count)
    $offset = 6 + 16 * $frames.Count
    $sizes = @(16, 20, 24, 32, 48, 64, 128, 256)
    for ($i = 0; $i -lt $frames.Count; $i++) {
        $dimension = if ($sizes[$i] -eq 256) { 0 } else { $sizes[$i] }
        $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
        $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]32)
        $writer.Write([uint32]$frames[$i].Length); $writer.Write([uint32]$offset)
        $offset += $frames[$i].Length
    }
    foreach ($frame in $frames) { $writer.Write([byte[]]$frame) }
} finally { $writer.Dispose() }
Write-Output 'Generated SVG and multi-resolution Windows icon.'
