# Draws FeatherCAD.ico, the app icon, at every size Windows uses: a white feather on the brand-red tile,
# drawn with the round-capped strokes of the toolbar icons (src/FeatherCad.App/UI/Icons.cs).
# Windows only (System.Drawing). Run from the repo root:
#   build\make-icon.ps1 -Out src\FeatherCad.App\Assets\FeatherCAD.ico [-PreviewDir <folder for PNGs>]
param([string]$Out = "src\FeatherCad.App\Assets\FeatherCAD.ico", [string]$PreviewDir)
Add-Type -AssemblyName System.Drawing
$Out = [System.IO.Path]::GetFullPath($Out)

function RoundRect($x, $y, $w, $h, $r) {
  $p = New-Object System.Drawing.Drawing2D.GraphicsPath
  $d = 2 * $r
  $p.AddArc($x, $y, $d, $d, 180, 90); $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
  $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90); $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
  $p.CloseFigure(); $p
}
function Color($hex) { [System.Drawing.ColorTranslator]::FromHtml($hex) }
function RoundPen($hex, $width) {
  $pen = New-Object System.Drawing.Pen (Color $hex), $width
  $pen.StartCap = 'Round'; $pen.EndCap = 'Round'; $pen.LineJoin = 'Round'; $pen
}

$red = '#B4232A'      # the "FeatherCAD" wordmark in the menu bar
$redDark = '#8F1B21'
$white = '#FFFFFF'

# Drawn on a 128 grid. The feather runs from the quill at bottom left to the tip at top right.
function Draw($g, $size) {
  # Small sizes get heavier strokes so the feather survives being shrunk to 16 px.
  $k = if ($size -le 24) { 1.6 } elseif ($size -le 48) { 1.25 } else { 1 }

  $tile = RoundRect 4 4 120 120 26
  $fill = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.PointF 0, 4), (New-Object System.Drawing.PointF 0, 124), (Color '#C42A31'), (Color '#A81F26')
  $g.FillPath($fill, $tile)

  # Vane: a leaf shape either side of the shaft, with a V notch cut into the lower edge (in the tile's own fill).
  $vane = New-Object System.Drawing.Drawing2D.GraphicsPath
  $vane.AddBezier(38, 92, 28, 58, 62, 22, 106, 18)     # upper edge, base to tip
  $vane.AddBezier(106, 18, 106, 52, 78, 86, 38, 92)    # lower edge, tip to base
  $vane.CloseFigure()
  $g.FillPath((New-Object System.Drawing.SolidBrush (Color $white)), $vane)
  if ($size -ge 24) {
    $notch = New-Object System.Drawing.Drawing2D.GraphicsPath
    $notch.AddPolygon([System.Drawing.PointF[]]@((New-Object System.Drawing.PointF 75, 65), (New-Object System.Drawing.PointF 92, 72), (New-Object System.Drawing.PointF 80, 84)))
    $g.FillPath($fill, $notch)
  }

  # Shaft through the vane (darker red), then the quill below it (white).
  if ($size -ge 32) { $g.DrawLine((RoundPen $redDark (3 * $k)), 44, 86, 98, 28) }
  $g.DrawLine((RoundPen $white (7 * $k)), 22, 108, 42, 88)
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$frames = @()
foreach ($size in $sizes) {
  $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.SmoothingMode = 'AntiAlias'; $g.PixelOffsetMode = 'HighQuality'
  $g.ScaleTransform($size / 128, $size / 128)
  Draw $g $size
  $g.Dispose()

  $ms = New-Object System.IO.MemoryStream
  $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
  if ($PreviewDir) {
    $dir = [System.IO.Path]::GetFullPath($PreviewDir)
    [System.IO.Directory]::CreateDirectory($dir) | Out-Null
    [System.IO.File]::WriteAllBytes((Join-Path $dir "FeatherCAD-$size.png"), $ms.ToArray())
  }
  $bmp.Dispose()
  $frames += , $ms.ToArray()
}

# ICO with PNG frames
[System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($Out)) | Out-Null
$fs = [System.IO.File]::Create($Out)
$w = New-Object System.IO.BinaryWriter $fs
$w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
  $s = $sizes[$i]; $dim = if ($s -ge 256) { 0 } else { $s }
  $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
  $w.Write([UInt16]1); $w.Write([UInt16]32); $w.Write([UInt32]$frames[$i].Length); $w.Write([UInt32]$offset)
  $offset += $frames[$i].Length
}
foreach ($f in $frames) { $w.Write($f) }
$w.Close()
