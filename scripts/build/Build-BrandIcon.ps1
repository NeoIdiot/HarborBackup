# Original geometric anchor artwork. No downloaded image or external icon library.
# Regenerate the multi-resolution Windows icon used by the app and installer shortcut.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$IconPath = Join-Path $PSScriptRoot '../../src/HarborBackup.Desktop/Assets/HarborBackup.ico'
New-Item -ItemType Directory -Path (Split-Path $IconPath) -Force | Out-Null
$Frames = foreach ($Size in @(16, 24, 32, 48, 64, 256)) {
    $Bitmap = [Drawing.Bitmap]::new($Size, $Size)
    $Graphics = [Drawing.Graphics]::FromImage($Bitmap)
    $Graphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $Graphics.ScaleTransform($Size / 32.0, $Size / 32.0)
    $Path = [Drawing.Drawing2D.GraphicsPath]::new()
    foreach ($Arc in @(@(1,1,180), @(21,1,270), @(21,21,0), @(1,21,90))) {
        $Path.AddArc([single]$Arc[0], [single]$Arc[1], 10, 10, [single]$Arc[2], 90)
    }
    $Path.CloseFigure()
    $Brush = [Drawing.SolidBrush]::new([Drawing.Color]::FromArgb(0,103,176))
    $Graphics.FillPath($Brush, $Path)
    $Pen = [Drawing.Pen]::new([Drawing.Color]::White, 2)
    $Pen.StartCap = [Drawing.Drawing2D.LineCap]::Round
    $Pen.EndCap = [Drawing.Drawing2D.LineCap]::Round
    $Graphics.DrawEllipse($Pen, 13, 5, 6, 6)
    $Graphics.DrawLine($Pen, 16, 11, 16, 25)
    $Graphics.DrawLine($Pen, 11, 14, 21, 14)
    $Graphics.DrawBezier($Pen, 7, 18, 7, 27, 25, 27, 25, 18)
    $Graphics.DrawLine($Pen, 7, 18, 5, 21)
    $Graphics.DrawLine($Pen, 7, 18, 10, 20)
    $Graphics.DrawLine($Pen, 25, 18, 22, 20)
    $Graphics.DrawLine($Pen, 25, 18, 27, 21)
    $Buffer = [IO.MemoryStream]::new()
    $Bitmap.Save($Buffer, [Drawing.Imaging.ImageFormat]::Png)
    @{ Size = $Size; Data = $Buffer.ToArray() }
    $Buffer.Dispose(); $Pen.Dispose(); $Brush.Dispose(); $Path.Dispose(); $Graphics.Dispose(); $Bitmap.Dispose()
}
$Stream = [IO.File]::Create($IconPath)
$Writer = [IO.BinaryWriter]::new($Stream)
try {
    $Writer.Write([uint16]0); $Writer.Write([uint16]1); $Writer.Write([uint16]$Frames.Count)
    $Offset = 6 + 16 * $Frames.Count
    foreach ($Frame in $Frames) {
        $Dimension = if ($Frame.Size -eq 256) { 0 } else { $Frame.Size }
        $Writer.Write([byte]$Dimension); $Writer.Write([byte]$Dimension)
        $Writer.Write([byte]0); $Writer.Write([byte]0); $Writer.Write([uint16]1); $Writer.Write([uint16]32)
        $Writer.Write([uint32]$Frame.Data.Length); $Writer.Write([uint32]$Offset)
        $Offset += $Frame.Data.Length
    }
    foreach ($Frame in $Frames) { $Writer.Write([byte[]]$Frame.Data) }
} finally { $Writer.Dispose(); $Stream.Dispose() }
