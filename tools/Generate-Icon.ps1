Add-Type -AssemblyName System.Drawing
$iconPath = Join-Path $PSScriptRoot '..\DesktopNotes\Assets\Note.ico'
$frames = @()
foreach ($size in @(16, 24, 32, 48, 64, 256)) {
    $bitmap = [System.Drawing.Bitmap]::new($size, $size)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.Clear([System.Drawing.Color]::Transparent)
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.ScaleTransform($size / 32.0, $size / 32.0)
    $paper = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 235, 156))
    $ink = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(149, 120, 50), 2)
    $graphics.FillRectangle($paper, 3, 2, 26, 28)
    $graphics.DrawLine($ink, 8, 11, 24, 11)
    $graphics.DrawLine($ink, 8, 17, 24, 17)
    $graphics.DrawLine($ink, 8, 23, 18, 23)
    $buffer = [System.IO.MemoryStream]::new()
    $bitmap.Save($buffer, [System.Drawing.Imaging.ImageFormat]::Png)
    $frames += ,@{ Size = $size; Data = $buffer.ToArray() }
    $buffer.Dispose()
    $ink.Dispose()
    $paper.Dispose()
    $graphics.Dispose()
    $bitmap.Dispose()
}
$output = [System.IO.File]::Create($iconPath)
$writer = [System.IO.BinaryWriter]::new($output)
try {
    $writer.Write([uint16]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]$frames.Count)
    $offset = 6 + 16 * $frames.Count
    foreach ($frame in $frames) {
        $dimension = if ($frame.Size -eq 256) { 0 } else { $frame.Size }
        $writer.Write([byte]$dimension)
        $writer.Write([byte]$dimension)
        $writer.Write([byte]0)
        $writer.Write([byte]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]32)
        $writer.Write([uint32]$frame.Data.Length)
        $writer.Write([uint32]$offset)
        $offset += $frame.Data.Length
    }
    foreach ($frame in $frames) { $writer.Write([byte[]]$frame.Data) }
}
finally { $writer.Dispose(); $output.Dispose() }
