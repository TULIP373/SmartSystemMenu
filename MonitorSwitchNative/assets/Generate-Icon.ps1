$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

# Draw in a 32-unit grid. Supersampling keeps the small tray sizes crisp.
function New-RoundedRectangle([float]$x, [float]$y, [float]$width, [float]$height, [float]$radius) {
    $path = [Drawing.Drawing2D.GraphicsPath]::new()
    $diameter = $radius * 2
    $path.AddArc($x, $y, $diameter, $diameter, 180, 90)
    $path.AddArc($x + $width - $diameter, $y, $diameter, $diameter, 270, 90)
    $path.AddArc($x + $width - $diameter, $y + $height - $diameter, $diameter, $diameter, 0, 90)
    $path.AddArc($x, $y + $height - $diameter, $diameter, $diameter, 90, 90)
    $path.CloseFigure()
    return ,$path
}

function New-IconImage([int]$size) {
    $renderSize = $size * 4
    $canvas = [Drawing.Bitmap]::new($renderSize, $renderSize, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [Drawing.Graphics]::FromImage($canvas)
    $graphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.Clear([Drawing.Color]::Transparent)
    $graphics.ScaleTransform($renderSize / 32.0, $renderSize / 32.0)
    $ink = [Drawing.SolidBrush]::new([Drawing.ColorTranslator]::FromHtml('#101D32'))
    $rearInk = [Drawing.SolidBrush]::new([Drawing.ColorTranslator]::FromHtml('#172B48'))
    $blue = [Drawing.Pen]::new([Drawing.ColorTranslator]::FromHtml('#5797FF'), 2.2)
    $cyan = [Drawing.Pen]::new([Drawing.ColorTranslator]::FromHtml('#48D9E9'), 2.2)
    $white = [Drawing.Pen]::new([Drawing.ColorTranslator]::FromHtml('#F2FAFF'), 1.65)
    foreach ($pen in @($blue, $cyan, $white)) {
        $pen.StartCap = [Drawing.Drawing2D.LineCap]::Round
        $pen.EndCap = [Drawing.Drawing2D.LineCap]::Round
        $pen.LineJoin = [Drawing.Drawing2D.LineJoin]::Round
    }
    $rear = New-RoundedRectangle 3 4 18 13.5 2
    $front = New-RoundedRectangle 11 11 18 13.5 2
    try {
        $graphics.DrawLine($blue, [float]12, [float]17.5, [float]12, [float]21)
        $graphics.DrawLine($blue, [float]8, [float]21, [float]15, [float]21)
        $graphics.FillPath($rearInk, $rear)
        $graphics.DrawPath($blue, $rear)
        $graphics.DrawLine($cyan, [float]20, [float]24.5, [float]20, [float]28)
        $graphics.DrawLine($cyan, [float]15.5, [float]28, [float]24.5, [float]28)
        $graphics.FillPath($ink, $front)
        $graphics.DrawPath($cyan, $front)
        # Opposed arrows express moving a window between the two monitors.
        $graphics.DrawLines($white, [Drawing.PointF[]]@([Drawing.PointF]::new(15, 16), [Drawing.PointF]::new(24, 16), [Drawing.PointF]::new(21.8, 13.9)))
        $graphics.DrawLines($white, [Drawing.PointF[]]@([Drawing.PointF]::new(25, 20), [Drawing.PointF]::new(16, 20), [Drawing.PointF]::new(18.2, 22.1)))
        $image = [Drawing.Bitmap]::new($size, $size, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $downsample = [Drawing.Graphics]::FromImage($image)
        try {
            $downsample.CompositingMode = [Drawing.Drawing2D.CompositingMode]::SourceCopy
            $downsample.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $downsample.DrawImage($canvas, 0, 0, $size, $size)
        } finally { $downsample.Dispose() }
        return ,$image
    } finally {
        $rear.Dispose(); $front.Dispose()
        $ink.Dispose(); $rearInk.Dispose(); $blue.Dispose(); $cyan.Dispose(); $white.Dispose()
        $graphics.Dispose(); $canvas.Dispose()
    }
}

$frames = @()
foreach ($size in @(16, 20, 24, 32, 40, 48, 64, 128, 256)) {
    $image = New-IconImage $size
    $buffer = [IO.MemoryStream]::new()
    try {
        $image.Save($buffer, [Drawing.Imaging.ImageFormat]::Png)
        $frames += [pscustomobject]@{ Size=$size; Bytes=$buffer.ToArray() }
        if ($size -eq 256) { $image.Save((Join-Path $PSScriptRoot 'MonitorSwitch.png'), [Drawing.Imaging.ImageFormat]::Png) }
    } finally { $image.Dispose(); $buffer.Dispose() }
}
$stream = [IO.File]::Create((Join-Path $PSScriptRoot 'MonitorSwitch.ico'))
$writer = [IO.BinaryWriter]::new($stream)
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$frames.Count)
    $offset = 6 + 16 * $frames.Count
    foreach ($frame in $frames) {
        $dimension = if ($frame.Size -eq 256) { 0 } else { $frame.Size }
        $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
        $writer.Write([byte]0); $writer.Write([byte]0)
        $writer.Write([uint16]1); $writer.Write([uint16]32)
        $writer.Write([uint32]$frame.Bytes.Length); $writer.Write([uint32]$offset)
        $offset += $frame.Bytes.Length
    }
    foreach ($frame in $frames) { $writer.Write([byte[]]$frame.Bytes) }
} finally { $writer.Dispose() }
Write-Output 'Generated MonitorSwitch.ico (16–256 px) and transparent PNG preview.'
