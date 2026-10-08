# Draws the SpryCAD icons at every size Windows uses, each an ICO of PNG frames:
#   src\SpryCad.App\Assets\SpryCAD.ico           the app: a white S drawn as a sketch spline, grips at its ends,
#                                                   on the brand-teal tile
#   src\SpryCad.App\Assets\SpryCADPart.ico       .sprt parts: a page with a block, the app's tile as a badge
#   src\SpryCad.App\Assets\SpryCADAssembly.ico   .sasm assemblies: a page with two blocks put together (orange, blue)
#   (at 16 and 20 px no page, the blocks alone, so a part and an assembly still differ in Explorer's small views)
# and their 256 px frames for Linux (installer\linux\sprycad.png, application-x-sprycad-part.png, -assembly.png).
# Windows only (System.Drawing). Run from the repo root:
#   build\make-icon.ps1 [-PreviewDir <folder for PNGs of every frame>]
param([string]$PreviewDir)
Add-Type -AssemblyName System.Drawing
$root = Split-Path -Parent $PSScriptRoot

function RoundRect($x, $y, $w, $h, $r) {
  $p = New-Object System.Drawing.Drawing2D.GraphicsPath
  $d = 2 * $r
  $p.AddArc($x, $y, $d, $d, 180, 90); $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
  $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90); $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
  $p.CloseFigure(); $p
}
function Color($hex) { [System.Drawing.ColorTranslator]::FromHtml($hex) }
function Brush($hex) { New-Object System.Drawing.SolidBrush (Color $hex) }
function RoundPen($hex, $width) {
  $pen = New-Object System.Drawing.Pen (Color $hex), $width
  $pen.StartCap = 'Round'; $pen.EndCap = 'Round'; $pen.LineJoin = 'Round'; $pen
}
function Pt($x, $y) { New-Object System.Drawing.PointF $x, $y }

$teal = '#0F9A83'      # the "SpryCAD" wordmark in the menu bar
$tealLight = '#16B095'
$tealDark = '#0A7563'
$white = '#FFFFFF'

# The app's mark on a 128 grid, $px pixels to the unit (the frame's size / 128).
function Logo($g, $px) {
  # Small sizes get a heavier S so it survives being shrunk to 16 px; the grips only where they can be made out.
  $k = if ($px * 128 -le 24) { 1.45 } elseif ($px * 128 -le 48) { 1.2 } else { 1 }
  $tile = RoundRect 4 4 120 120 26
  $g.FillPath((New-Object System.Drawing.Drawing2D.LinearGradientBrush (Pt 0 4), (Pt 0 124), (Color $tealLight), (Color $tealDark)), $tile)

  # The S: a top bowl, the spine and a bottom bowl, three cubic spans joined smoothly (a sketch spline).
  $s = New-Object System.Drawing.Drawing2D.GraphicsPath
  $s.AddBezier((Pt 90 38), (Pt 80 18), (Pt 34 20), (Pt 38 44))
  $s.AddBezier((Pt 38 44), (Pt 41 62), (Pt 87 58), (Pt 90 80))
  $s.AddBezier((Pt 90 80), (Pt 94 106), (Pt 46 110), (Pt 36 88))
  $g.DrawPath((RoundPen $white (15 * $k)), $s)

  # Grips at the spline's ends: white squares with the tile's color inside, as the sketcher draws its points.
  if ($px * 128 -ge 32) {
    foreach ($e in @(@(90, 38), @(36, 88))) {
      $g.FillRectangle((Brush $white), $e[0] - 10, $e[1] - 10, 20, 20)
      $g.FillRectangle((Brush $tealDark), $e[0] - 5, $e[1] - 5, 10, 10)
    }
  }
}

# An isometric block centered at (cx, cy), r from its center to a corner, on the 128 grid.
function Block($g, $cx, $cy, $r, $top, $left, $right, $line) {
  $h = $r * 0.866
  $T = Pt $cx ($cy - $r); $UR = Pt ($cx + $h) ($cy - $r / 2); $LR = Pt ($cx + $h) ($cy + $r / 2)
  $B = Pt $cx ($cy + $r); $LL = Pt ($cx - $h) ($cy + $r / 2); $UL = Pt ($cx - $h) ($cy - $r / 2); $C = Pt $cx $cy
  $g.FillPolygon((Brush $top), [System.Drawing.PointF[]]@($T, $UR, $C, $UL))
  $g.FillPolygon((Brush $left), [System.Drawing.PointF[]]@($UL, $C, $B, $LL))
  $g.FillPolygon((Brush $right), [System.Drawing.PointF[]]@($C, $UR, $LR, $B))
  $pen = RoundPen '#1A2433' $line
  $g.DrawPolygon($pen, [System.Drawing.PointF[]]@($T, $UR, $LR, $B, $LL, $UL))
  $thin = RoundPen '#1A2433' ($line * 0.6)
  $g.DrawLine($thin, $C, $UR); $g.DrawLine($thin, $C, $UL); $g.DrawLine($thin, $C, $B)
}

# A file type's icon on the 128 grid: the page, its symbol, and the app's tile as a badge where it fits.
function FileIcon($g, $px, $kind) {
  $line = [Math]::Max(1.0, $px * 128 / 28) / $px     # at least a pixel
  # 16 and 20 px (Explorer's details and list views): no page, the blocks filling the square, so a part (one
  # blue block) and an assembly (an orange and a blue) differ by their shape as well as their colors
  if ($px * 128 -le 20) {
    if ($kind -eq 'part') { Block $g 64 66 58 '#5FA3E0' '#2F75B8' '#1F5590' $line }
    else {
      Block $g 84 44 40 '#F2B35C' '#D9822B' '#AD621B' $line
      Block $g 44 84 40 '#5FA3E0' '#2F75B8' '#1F5590' $line
    }
    return
  }
  $page = [System.Drawing.PointF[]]@((Pt 20 4), (Pt 82 4), (Pt 110 32), (Pt 110 124), (Pt 20 124))
  $g.FillPolygon((Brush '#FBFCFE'), $page)
  $g.DrawPolygon((RoundPen '#6B7686' $line), $page)
  $fold = [System.Drawing.PointF[]]@((Pt 82 4), (Pt 82 32), (Pt 110 32))
  $g.FillPolygon((Brush '#D9DFE8'), $fold)
  $g.DrawPolygon((RoundPen '#6B7686' $line), $fold)
  # 24 px: the blocks as big as the page allows, the assembly's two the same size (orange over blue)
  $small = $px * 128 -le 24
  if ($kind -eq 'part') { if ($small) { Block $g 65 80 42 '#5FA3E0' '#2F75B8' '#1F5590' $line } else { Block $g 65 81 37 '#5FA3E0' '#2F75B8' '#1F5590' $line } }
  elseif ($small) {
    Block $g 78 56 30 '#F2B35C' '#D9822B' '#AD621B' $line
    Block $g 50 92 30 '#5FA3E0' '#2F75B8' '#1F5590' $line
  }
  else {
    Block $g 78 61 27 '#F2B35C' '#D9822B' '#AD621B' $line
    Block $g 53 93 27 '#5FA3E0' '#2F75B8' '#1F5590' $line
  }
  if ($px * 128 -ge 32) {
    $state = $g.Save()
    $g.TranslateTransform(4, 4); $g.ScaleTransform(0.40, 0.40)
    Logo $g ($px * 0.40)
    $g.Restore($state)
  }
}

function Frames($draw) {
  $frames = @()
  foreach ($size in 16, 20, 24, 32, 40, 48, 64, 96, 128, 256) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.PixelOffsetMode = 'HighQuality'
    $g.ScaleTransform($size / 128, $size / 128)
    & $draw $g ($size / 128)
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    $frames += , @($size, $ms.ToArray())
  }
  , $frames
}

function WriteIcon($frames, $ico, $png, $name) {
  $fs = [System.IO.File]::Create($ico)
  $w = New-Object System.IO.BinaryWriter $fs
  $w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$frames.Count)
  $offset = 6 + 16 * $frames.Count
  foreach ($f in $frames) {
    $dim = if ($f[0] -ge 256) { 0 } else { $f[0] }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([UInt16]1); $w.Write([UInt16]32); $w.Write([UInt32]$f[1].Length); $w.Write([UInt32]$offset)
    $offset += $f[1].Length
  }
  foreach ($f in $frames) { $w.Write($f[1]) }
  $w.Close()
  [System.IO.File]::WriteAllBytes($png, $frames[-1][1])
  if ($PreviewDir) {
    $dir = [System.IO.Path]::GetFullPath($PreviewDir)
    [System.IO.Directory]::CreateDirectory($dir) | Out-Null
    foreach ($f in $frames) { [System.IO.File]::WriteAllBytes((Join-Path $dir "$name-$($f[0]).png"), $f[1]) }
  }
  Write-Host "$ico`n$png"
}

$assets = Join-Path $root 'src\SpryCad.App\Assets'
$linux = Join-Path $root 'installer\linux'
WriteIcon (Frames { param($g, $px) Logo $g $px }) (Join-Path $assets 'SpryCAD.ico') (Join-Path $linux 'sprycad.png') 'SpryCAD'
WriteIcon (Frames { param($g, $px) FileIcon $g $px 'part' }) (Join-Path $assets 'SpryCADPart.ico') (Join-Path $linux 'application-x-sprycad-part.png') 'SpryCADPart'
WriteIcon (Frames { param($g, $px) FileIcon $g $px 'assembly' }) (Join-Path $assets 'SpryCADAssembly.ico') (Join-Path $linux 'application-x-sprycad-assembly.png') 'SpryCADAssembly'

# The installer's wizard images (installer\wizard-*.bmp, 24-bit as Inno Setup wants), at each size it picks from for
# the screen's DPI: the side banner of its Welcome and Finished pages (the brand teal, the S, the name, and a part
# made in SpryCAD: the spur gear sample's own preview), and the small image at the top right of the other pages (the
# app's tile on the header's white).

# The spur gear sample's preview image (the thumbnail.png saved in the container; transparent around the part)
$gear = $null
try {
  Add-Type -AssemblyName System.IO.Compression.FileSystem
  $zip = [System.IO.Compression.ZipFile]::OpenRead((Join-Path $root 'samples\demos\spur-gear.sprt'))
  $entry = $zip.GetEntry('thumbnail.png')
  if ($entry) { $ms = New-Object System.IO.MemoryStream; $st = $entry.Open(); $st.CopyTo($ms); $st.Close(); $gear = [System.Drawing.Image]::FromStream($ms) }
  $zip.Dispose()
} catch { $gear = $null }

function Banner($g, $w, $h) {
  $u = $w / 164   # drawn on a 164 wide grid
  $g.FillRectangle((New-Object System.Drawing.Drawing2D.LinearGradientBrush (Pt 0 0), (Pt 0 $h), (Color $tealLight), (Color $tealDark)), 0, 0, $w, $h)
  $g.ScaleTransform($u, $u)
  $hh = $h / $u
  if ($gear) {
    # the gear in the lower part, on a soft shadow
    $size = 150; $x = (164 - $size) / 2; $y = [Math]::Max(178, $hh - $size - 6)
    $shadow = New-Object System.Drawing.Drawing2D.GraphicsPath
    $shadow.AddEllipse($x + 22, $y + $size * 0.80, $size - 44, $size * 0.13)
    $sb = New-Object System.Drawing.Drawing2D.PathGradientBrush $shadow
    $sb.CenterColor = [System.Drawing.Color]::FromArgb(70, 0, 40, 32); $sb.SurroundColors = @([System.Drawing.Color]::FromArgb(0, 0, 40, 32))
    $g.FillPath($sb, $shadow)
    $g.InterpolationMode = 'HighQualityBicubic'
    $g.DrawImage($gear, [single]$x, [single]$y, [single]$size, [single]$size)
  }
  else {
    # no sample to show: faint isometric blocks along the bottom, white lines only
    $faint = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(46, 255, 255, 255)), 1.4
    $faint.LineJoin = 'Round'
    foreach ($b in @(@(40, ($hh - 60), 30), @(98, ($hh - 92), 22), @(128, ($hh - 38), 34), @(20, ($hh - 130), 16))) {
      $cx, $cy, $r = $b; $k = $r * 0.866
      $pts = [System.Drawing.PointF[]]@((Pt $cx ($cy - $r)), (Pt ($cx + $k) ($cy - $r / 2)), (Pt ($cx + $k) ($cy + $r / 2)), (Pt $cx ($cy + $r)), (Pt ($cx - $k) ($cy + $r / 2)), (Pt ($cx - $k) ($cy - $r / 2)))
      $g.DrawPolygon($faint, $pts)
      $g.DrawLine($faint, (Pt $cx $cy), (Pt ($cx + $k) ($cy - $r / 2))); $g.DrawLine($faint, (Pt $cx $cy), (Pt ($cx - $k) ($cy - $r / 2))); $g.DrawLine($faint, (Pt $cx $cy), (Pt $cx ($cy + $r)))
    }
  }
  # the mark (its tile drawn too: a shade darker than the banner, it reads as the app's icon) and the name
  $state = $g.Save()
  $g.TranslateTransform(42, 34); $g.ScaleTransform(80 / 128, 80 / 128)
  Logo $g ($u * 80 / 128)
  $g.Restore($state)
  $fmt = New-Object System.Drawing.StringFormat; $fmt.Alignment = 'Center'
  $g.DrawString('SpryCAD', (New-Object System.Drawing.Font 'Segoe UI Semibold', 19, ([System.Drawing.FontStyle]::Bold), ([System.Drawing.GraphicsUnit]::Pixel)), (Brush $white), (New-Object System.Drawing.RectangleF 0, 124, 164, 30), $fmt)
  $g.DrawString('Parametric 3D CAD', (New-Object System.Drawing.Font 'Segoe UI', 11, ([System.Drawing.FontStyle]::Regular), ([System.Drawing.GraphicsUnit]::Pixel)), (New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(220, 255, 255, 255))), (New-Object System.Drawing.RectangleF 0, 152, 164, 20), $fmt)
}

function SmallImage($g, $w, $h) {
  $g.Clear([System.Drawing.Color]::White)
  $side = [Math]::Min($w, $h) * 0.86
  $g.TranslateTransform(($w - $side) / 2, ($h - $side) / 2); $g.ScaleTransform($side / 128, $side / 128)
  Logo $g ($side / 128)
}

function WizardImages($draw, $sizes, $name) {
  $files = @()
  foreach ($sz in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap $sz[0], $sz[1], ([System.Drawing.Imaging.PixelFormat]::Format24bppRgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.PixelOffsetMode = 'HighQuality'; $g.TextRenderingHint = 'AntiAliasGridFit'
    & $draw $g $sz[0] $sz[1]
    $g.Dispose()
    $file = Join-Path $root "installer\$name-$($sz[0])x$($sz[1]).bmp"
    $bmp.Save($file, [System.Drawing.Imaging.ImageFormat]::Bmp); $bmp.Dispose()
    $files += $file
  }
  Write-Host ($files -join "`n")
}

# the sizes Inno Setup chooses among (100% to 250% DPI)
WizardImages { param($g, $w, $h) Banner $g $w $h } @(@(164, 314), @(192, 386), @(246, 459), @(273, 556), @(328, 604), @(383, 707), @(410, 797)) 'wizard'
WizardImages { param($g, $w, $h) SmallImage $g $w $h } @(@(55, 55), @(64, 68), @(83, 80), @(92, 97), @(110, 106), @(119, 123), @(138, 140)) 'wizard-small'
