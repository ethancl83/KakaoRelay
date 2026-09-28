# Render the project's simple SVG paths directly at every Windows icon size.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore, WindowsBase
$assetRoot = Join-Path (Split-Path -Parent $PSScriptRoot) 'src\KakaoRelay.App\Assets'
[xml]$svg = [IO.File]::ReadAllText((Join-Path $assetRoot 'KakaoRelay.svg'))
$frames = [Collections.Generic.List[byte[]]]::new()
$sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
foreach ($size in ($sizes + 512)) {
    $visual = [Windows.Media.DrawingVisual]::new()
    $drawing = $visual.RenderOpen()
    $drawing.PushTransform([Windows.Media.ScaleTransform]::new($size / 64.0, $size / 64.0))
    foreach ($path in $svg.svg.path) {
        $fill = $null
        $pen = $null
        if ($path.fill -and $path.fill -ne 'none') {
            $fill = [Windows.Media.BrushConverter]::new().ConvertFromInvariantString($path.fill)
        }
        if ($path.stroke) {
            $brush = [Windows.Media.BrushConverter]::new().ConvertFromInvariantString($path.stroke)
            $pen = [Windows.Media.Pen]::new($brush, [double]$path.'stroke-width')
            $pen.StartLineCap = $pen.EndLineCap = [Windows.Media.PenLineCap]::Round
            $pen.LineJoin = [Windows.Media.PenLineJoin]::Round
        }
        $drawing.DrawGeometry($fill, $pen, [Windows.Media.Geometry]::Parse($path.d))
    }
    $drawing.Pop()
    $drawing.Close()
    $bitmap = [Windows.Media.Imaging.RenderTargetBitmap]::new($size, $size, 96, 96, [Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    $encoder = [Windows.Media.Imaging.PngBitmapEncoder]::new()
    $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $stream = [IO.MemoryStream]::new()
    try {
        $encoder.Save($stream)
        if ($size -eq 512) { [IO.File]::WriteAllBytes((Join-Path $assetRoot 'KakaoRelay.png'), $stream.ToArray()) }
        else { $frames.Add($stream.ToArray()) }
    } finally { $stream.Dispose() }
}
$writer = [IO.BinaryWriter]::new([IO.File]::Create((Join-Path $assetRoot 'KakaoRelay.ico')))
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
    foreach ($frame in $frames) { $writer.Write($frame) }
} finally { $writer.Dispose() }
