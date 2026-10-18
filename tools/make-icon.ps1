# Renders the VibePlayer app icon (gradient circle + white play triangle) into a multi-size .ico
param(
    [Parameter(Mandatory = $true)][string]$OutFile
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$sizes = 16, 20, 24, 32, 40, 48, 64, 96, 128, 256
$frames = @()

foreach ($s in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.Clear([System.Drawing.Color]::Transparent)

    $pad = [Math]::Max(0.5, $s * 0.02)
    $d = $s - 2 * $pad
    $rect = New-Object System.Drawing.RectangleF ([single]$pad), ([single]$pad), ([single]$d), ([single]$d)

    # Background disc: warm red -> neon pink
    $c1 = [System.Drawing.Color]::FromArgb(255, 255, 45, 70)
    $c2 = [System.Drawing.Color]::FromArgb(255, 222, 20, 120)
    $bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, $c1, $c2, ([single]50)
    $g.FillEllipse($bg, $rect)

    # Soft top highlight
    if ($s -ge 32) {
        $hl = New-Object System.Drawing.RectangleF ([single]($pad + $d * 0.12)), ([single]($pad + $d * 0.04)), ([single]($d * 0.76)), ([single]($d * 0.5))
        $hlBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush $hl, ([System.Drawing.Color]::FromArgb(70, 255, 255, 255)), ([System.Drawing.Color]::FromArgb(0, 255, 255, 255)), ([single]90)
        $g.FillEllipse($hlBrush, $hl)
        $hlBrush.Dispose()
    }

    # Play triangle with rounded corners
    $cx = $s / 2 + $s * 0.045
    $cy = $s / 2
    $r = $s * 0.24
    $pts = @(
        (New-Object System.Drawing.PointF ([single]($cx - $r * 0.82)), ([single]($cy - $r))),
        (New-Object System.Drawing.PointF ([single]($cx - $r * 0.82)), ([single]($cy + $r))),
        (New-Object System.Drawing.PointF ([single]($cx + $r)), ([single]$cy))
    )
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddPolygon([System.Drawing.PointF[]]$pts)
    $white = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::White)
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), ([single][Math]::Max(1, $s * 0.07))
    $pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
    $g.FillPath($white, $path)
    $g.DrawPath($pen, $path)

    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    $frames += , @{ Size = $s; Bytes = $ms.ToArray() }
}

# Assemble ICO (PNG-compressed frames, supported on Windows Vista and later)
$out = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $out
$w.Write([UInt16]0)
$w.Write([UInt16]1)
$w.Write([UInt16]$frames.Count)
$offset = 6 + 16 * $frames.Count
foreach ($f in $frames) {
    $dim = if ($f.Size -ge 256) { 0 } else { $f.Size }
    $w.Write([byte]$dim)
    $w.Write([byte]$dim)
    $w.Write([byte]0)
    $w.Write([byte]0)
    $w.Write([UInt16]1)
    $w.Write([UInt16]32)
    $w.Write([UInt32]$f.Bytes.Length)
    $w.Write([UInt32]$offset)
    $offset += $f.Bytes.Length
}
foreach ($f in $frames) { $w.Write([byte[]]$f.Bytes) }
$w.Flush()

$dir = Split-Path -Parent $OutFile
if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Force $dir | Out-Null }
[System.IO.File]::WriteAllBytes($OutFile, $out.ToArray())
Write-Host "Icon written to $OutFile"
