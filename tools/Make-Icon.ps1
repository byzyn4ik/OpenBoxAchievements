# Draws OpenBox Achievements' icon: a white trophy on a green circle, as a
# multi-size .ico (16-256 px, PNG frames), assets\openbox.ico by default.
param([string]$Out = (Join-Path $PSScriptRoot '..\assets\openbox.ico'))
Add-Type -AssemblyName System.Drawing
$sizes = 16, 24, 32, 48, 64, 128, 256
$frames = foreach ($size in $sizes) {
    $bitmap = New-Object Drawing.Bitmap $size, $size
    $g = [Drawing.Graphics]::FromImage($bitmap)
    $g.SmoothingMode = 'AntiAlias'; $g.PixelOffsetMode = 'HighQuality'; $g.Clear([Drawing.Color]::Transparent)
    $green = New-Object Drawing.Drawing2D.LinearGradientBrush (New-Object Drawing.RectangleF 0, 0, $size, $size), ([Drawing.Color]::FromArgb(255, 0x2C, 0x9A, 0x2C)), ([Drawing.Color]::FromArgb(255, 0x0E, 0x6B, 0x0E)), 90
    $g.FillEllipse($green, 0.5, 0.5, $size - 1, $size - 1)
    # Trophy on a 24-unit grid, centred.
    $u = $size / 24.0
    $white = New-Object Drawing.SolidBrush ([Drawing.Color]::White)
    $pen = New-Object Drawing.Pen ([Drawing.Color]::White), ([math]::Max(1, 1.6 * $u))
    $cup = New-Object Drawing.Drawing2D.GraphicsPath
    $cup.AddLine(7.2 * $u, 5.2 * $u, 16.8 * $u, 5.2 * $u)
    $cup.AddLine(16.8 * $u, 5.2 * $u, 16.8 * $u, 9.6 * $u)
    $cup.AddArc(7.2 * $u, 4.8 * $u, 9.6 * $u, 9.6 * $u, 0, 180)
    $cup.CloseFigure()
    $g.FillPath($white, $cup)
    $g.DrawArc($pen, 4.3 * $u, 6.0 * $u, 4.4 * $u, 4.6 * $u, 90, 180)   # left handle
    $g.DrawArc($pen, 15.3 * $u, 6.0 * $u, 4.4 * $u, 4.6 * $u, 270, 180) # right handle
    $g.FillRectangle($white, 11.0 * $u, 13.8 * $u, 2.0 * $u, 3.0 * $u)   # stem
    $g.FillRectangle($white, 8.4 * $u, 16.6 * $u, 7.2 * $u, 1.9 * $u)    # base
    $g.Dispose()
    $stream = New-Object IO.MemoryStream
    $bitmap.Save($stream, [Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose()
    , $stream.ToArray()
}
$file = [IO.File]::Create($Out)
$writer = New-Object IO.BinaryWriter $file
$writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]
    $writer.Write([byte]($(if ($s -ge 256) { 0 } else { $s }))); $writer.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))
    $writer.Write([byte]0); $writer.Write([byte]0); $writer.Write([uint16]1); $writer.Write([uint16]32)
    $writer.Write([uint32]$frames[$i].Length); $writer.Write([uint32]$offset)
    $offset += $frames[$i].Length
}
foreach ($frame in $frames) { $writer.Write($frame) }
$writer.Close()
"icon: $Out"
