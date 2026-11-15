# Genere Assets\lueur.ico (anneau lumineux violet -> rose sur fond sombre), tailles 16 a 256 px.
Add-Type -AssemblyName System.Drawing
$out = Join-Path $PSScriptRoot 'lueur.ico'
$sizes = 256, 64, 48, 32, 24, 16
$pngs = foreach ($s in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)

    # fond : carre arrondi sombre
    $r = [Math]::Max(3, $s * 0.22)
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc(0, 0, $r * 2, $r * 2, 180, 90)
    $path.AddArc($s - $r * 2 - 1, 0, $r * 2, $r * 2, 270, 90)
    $path.AddArc($s - $r * 2 - 1, $s - $r * 2 - 1, $r * 2, $r * 2, 0, 90)
    $path.AddArc(0, $s - $r * 2 - 1, $r * 2, $r * 2, 90, 90)
    $path.CloseFigure()
    $bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush ([System.Drawing.Point]::new(0, 0)), ([System.Drawing.Point]::new($s, $s)), ([System.Drawing.Color]::FromArgb(255, 24, 20, 38)), ([System.Drawing.Color]::FromArgb(255, 10, 10, 15))
    $g.FillPath($bg, $path)

    # halo diffus
    $m = $s * 0.18
    $glowPath = New-Object System.Drawing.Drawing2D.GraphicsPath
    $glowPath.AddEllipse($m, $m, $s - 2 * $m, $s - 2 * $m)
    $glow = New-Object System.Drawing.Drawing2D.PathGradientBrush $glowPath
    $glow.CenterColor = [System.Drawing.Color]::FromArgb(0, 139, 92, 246)
    $glow.SurroundColors = @([System.Drawing.Color]::FromArgb(110, 168, 85, 247))
    $glow.FocusScales = [System.Drawing.PointF]::new(0.55, 0.55)
    $g.FillPath($glow, $glowPath)

    # anneau
    $w = [Math]::Max(1.6, $s * 0.085)
    $ri = $s * 0.27
    $ring = New-Object System.Drawing.Drawing2D.LinearGradientBrush ([System.Drawing.Point]::new(0, 0)), ([System.Drawing.Point]::new($s, $s)), ([System.Drawing.Color]::FromArgb(255, 196, 181, 253)), ([System.Drawing.Color]::FromArgb(255, 236, 72, 153))
    $pen = New-Object System.Drawing.Pen $ring, $w
    $g.DrawEllipse($pen, $ri, $ri, $s - 2 * $ri, $s - 2 * $ri)

    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    , $ms.ToArray()
}

# Conteneur ICO avec images PNG
$fs = [System.IO.File]::Create($out)
$bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $len = $pngs[$i].Length
    $bw.Write([byte]($(if ($s -ge 256) { 0 } else { $s }))); $bw.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))
    $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([UInt16]1); $bw.Write([UInt16]32)
    $bw.Write([UInt32]$len); $bw.Write([UInt32]$offset)
    $offset += $len
}
foreach ($p in $pngs) { $bw.Write($p) }
$bw.Dispose()
"OK -> $out"
