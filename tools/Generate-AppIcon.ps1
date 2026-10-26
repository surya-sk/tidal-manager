param(
    [string]$Destination = (Join-Path $PSScriptRoot '..\src\TidalManager.App\Assets')
)

Add-Type -AssemblyName System.Drawing
$destinationPath = [System.IO.Path]::GetFullPath($Destination)
[System.IO.Directory]::CreateDirectory($destinationPath) | Out-Null

function New-IconPng([int]$size) {
    $bitmap = [System.Drawing.Bitmap]::new($size, $size)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.Clear([System.Drawing.Color]::Transparent)
        $graphics.ScaleTransform($size / 256.0, $size / 256.0)

        $background = [System.Drawing.Drawing2D.GraphicsPath]::new()
        try {
            $background.AddArc(8, 8, 48, 48, 180, 90)
            $background.AddArc(200, 8, 48, 48, 270, 90)
            $background.AddArc(200, 200, 48, 48, 0, 90)
            $background.AddArc(8, 200, 48, 48, 90, 90)
            $background.CloseFigure()
            $navy = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 19, 31, 49))
            try { $graphics.FillPath($navy, $background) } finally { $navy.Dispose() }
        } finally { $background.Dispose() }

        $disc = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 66, 206, 204))
        $groove = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(110, 11, 66, 76), 3)
        $white = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::White)
        try {
            $graphics.FillEllipse($disc, 42, 42, 172, 172)
            $graphics.DrawEllipse($groove, 58, 58, 140, 140)
            $graphics.DrawEllipse($groove, 73, 73, 110, 110)
            $graphics.FillEllipse($white, 105, 105, 46, 46)
            $graphics.FillEllipse($disc, 119, 119, 18, 18)
        } finally {
            $disc.Dispose()
            $groove.Dispose()
            $white.Dispose()
        }

        $stream = [System.IO.MemoryStream]::new()
        try {
            $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
            return ,$stream.ToArray()
        } finally { $stream.Dispose() }
    } finally {
        $graphics.Dispose()
        $bitmap.Dispose()
    }
}

$sizes = @(16, 32, 48, 256)
$frames = @($sizes | ForEach-Object { New-IconPng $_ })
[System.IO.File]::WriteAllBytes((Join-Path $destinationPath 'AppIcon.png'), $frames[3])

$iconPath = Join-Path $destinationPath 'AppIcon.ico'
$stream = [System.IO.File]::Create($iconPath)
$writer = [System.IO.BinaryWriter]::new($stream)
try {
    $writer.Write([uint16]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]$frames.Count)
    $offset = 6 + 16 * $frames.Count
    for ($i = 0; $i -lt $frames.Count; $i++) {
        $writer.Write([byte]($sizes[$i] % 256))
        $writer.Write([byte]($sizes[$i] % 256))
        $writer.Write([byte]0)
        $writer.Write([byte]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]32)
        $writer.Write([uint32]$frames[$i].Length)
        $writer.Write([uint32]$offset)
        $offset += $frames[$i].Length
    }
    foreach ($frame in $frames) { $writer.Write([byte[]]$frame) }
} finally { $writer.Dispose() }

Write-Host "Generated $iconPath"
