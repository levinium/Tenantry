# Renders the Tenantry app icon ("Midnight": a 3x3 grid of tenant tiles with the T lit) and writes
#   src/Tenantry.App/Assets/tenantry.ico       Windows executable and window icon (16-256 px)
#   src/Tenantry.App/Assets/tenantry-256.png   in-app logo
#   src/Tenantry.App/Assets/tenantry-1024.png  master for macOS/Linux packaging
# Run from any folder with PowerShell 7 on Windows (uses System.Drawing):
#   pwsh -File tools/icon/render-icon.ps1
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$assets = Join-Path $PSScriptRoot '..\..\src\Tenantry.App\Assets'
New-Item -ItemType Directory -Force $assets | Out-Null

function New-RoundRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = [Math]::Min($r * 2, [Math]::Min($w, $h))
    $path.AddArc($x, $y, $d, $d, 180, 90)
    $path.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $path.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $path.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    return $path
}

# Fills a rounded rect given in unit (0..1) coordinates.
function Add-UnitRect($graphics, $brush, $size, $x, $y, $w, $h, $r) {
    $path = New-RoundRect ($x * $size) ($y * $size) ($w * $size) ($h * $size) ($r * $size)
    $graphics.FillPath($brush, $path)
    $path.Dispose()
}

function New-IconBitmap([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bmp)
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.Clear([System.Drawing.Color]::Transparent)

    # Background: rounded tile, diagonal midnight gradient, soft inner rim that holds the edge
    # against dark taskbars.
    $tile = New-RoundRect 0 0 $size $size ($size * 0.22)
    $top = [System.Drawing.ColorTranslator]::FromHtml('#2A3350')
    $bottom = [System.Drawing.ColorTranslator]::FromHtml('#0F1424')
    $gradient = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.PointF 0, 0), (New-Object System.Drawing.PointF $size, $size), $top, $bottom
    $graphics.FillPath($gradient, $tile)
    $rimWidth = [Math]::Max(1, $size * 0.025)
    $inset = $rimWidth / 2 + $size * 0.01
    $rim = New-RoundRect $inset $inset ($size - 2 * $inset) ($size - 2 * $inset) ($size * 0.2)
    $rimPen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(70, 255, 255, 255)), $rimWidth
    $graphics.DrawPath($rimPen, $rim)

    # Grid: 3x3 tenant cells; the top row and the middle column (one bar) form the lit T.
    $white = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::White)
    $faint = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(45, 255, 255, 255))
    $cell = 0.17; $gap = 0.045; $origin = 0.2; $radius = $cell * 0.2
    foreach ($row in 1, 2) {
        foreach ($col in 0, 2) {
            Add-UnitRect $graphics $faint $size ($origin + $col * ($cell + $gap)) ($origin + $row * ($cell + $gap)) $cell $cell $radius
        }
    }
    foreach ($col in 0, 1, 2) {
        Add-UnitRect $graphics $white $size ($origin + $col * ($cell + $gap)) $origin $cell $cell $radius
    }
    Add-UnitRect $graphics $white $size ($origin + $cell + $gap) ($origin + $cell + $gap) $cell (2 * $cell + $gap) $radius

    $white.Dispose(); $faint.Dispose(); $rimPen.Dispose(); $rim.Dispose(); $gradient.Dispose(); $tile.Dispose()
    $graphics.Dispose()
    return $bmp
}

function Get-PngBytes([int]$size) {
    $bmp = New-IconBitmap $size
    $stream = New-Object System.IO.MemoryStream
    $bmp.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return , $stream.ToArray()
}

# .ico with PNG-compressed entries (supported since Windows Vista), one per size.
function Write-Ico([string]$path, [int[]]$sizes) {
    $images = foreach ($size in $sizes) { , (Get-PngBytes $size) }
    $file = [System.IO.File]::Create($path)
    $writer = New-Object System.IO.BinaryWriter $file
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $dimension = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
        $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
        $writer.Write([byte]0); $writer.Write([byte]0)
        $writer.Write([uint16]1); $writer.Write([uint16]32)
        $writer.Write([uint32]$images[$i].Length); $writer.Write([uint32]$offset)
        $offset += $images[$i].Length
    }
    foreach ($image in $images) { $writer.Write([byte[]]$image) }
    $writer.Dispose()
}

Write-Ico (Join-Path $assets 'tenantry.ico') @(16, 20, 24, 32, 40, 48, 64, 128, 256)
foreach ($size in 256, 1024) {
    [System.IO.File]::WriteAllBytes((Join-Path $assets "tenantry-$size.png"), (Get-PngBytes $size))
}
"wrote icon assets to $((Resolve-Path $assets).Path)"
