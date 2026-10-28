# app.ico を生成する（マウス + 歯車）。使い方: powershell -ExecutionPolicy Bypass -File make_icon.ps1
param([string]$PreviewDir = "")
Add-Type -AssemblyName System.Drawing

function RoundRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

function GearPath([float]$cx, [float]$cy, [float]$outer, [float]$root, [float]$hole) {
    $pts = New-Object System.Collections.Generic.List[System.Drawing.PointF]
    for ($i = 0; $i -lt 8; $i++) {
        $a = $i * 45
        foreach ($pair in @(@($root, -15), @($outer, -9), @($outer, 9), @($root, 15))) {
            $rad = ($a + $pair[1]) * [Math]::PI / 180
            $pts.Add((New-Object System.Drawing.PointF ($cx + $pair[0] * [Math]::Cos($rad)), ($cy + $pair[0] * [Math]::Sin($rad))))
        }
    }
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $p.AddPolygon($pts.ToArray())
    $p.AddEllipse($cx - $hole, $cy - $hole, $hole * 2, $hole * 2) # 交互塗りで穴になる
    return $p
}

function Draw([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.ScaleTransform($size / 64.0, $size / 64.0)   # 64x64 の座標系で描く

    $small = $size -lt 24   # 小さいサイズは歯車なしでマウスを中央に
    $ox = if ($small) { 0 } else { -5 }
    $line = [float][Math]::Max(3.0, 64.0 / $size * 1.1)  # 16px でも線が1px以上になるように

    $dark  = [System.Drawing.Color]::FromArgb(255, 38, 42, 50)
    $edge  = New-Object System.Drawing.Pen $dark, $line
    $edge.LineJoin = 'Round'

    # 本体
    $body = RoundRect (14 + $ox) 3 36 58 18
    $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.PointF 0, 3), (New-Object System.Drawing.PointF 0, 61), ([System.Drawing.Color]::FromArgb(255, 250, 251, 253)), ([System.Drawing.Color]::FromArgb(255, 200, 207, 217))
    $g.FillPath($grad, $body)

    # サイドボタン（オレンジ）
    $orange = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 255, 122, 26))
    foreach ($y in @(29, 40)) {
        $sb = RoundRect (9 + $ox) $y 9 9 3
        $g.FillPath($orange, $sb); $g.DrawPath($edge, $sb)
    }

    # ボタンの区切り線とホイール
    $g.SetClip($body)
    $g.DrawLine($edge, (14 + $ox), 25, (50 + $ox), 25)
    $g.DrawLine($edge, (32 + $ox), 3, (32 + $ox), 25)
    $g.ResetClip()
    $wheel = RoundRect (28.5 + $ox) 9 7 12 3.5
    $g.FillPath((New-Object System.Drawing.SolidBrush $dark), $wheel)
    $g.DrawPath($edge, $body)

    # 歯車（白い縁取りでマウスと分ける）
    if (-not $small) {
        $gear = GearPath 49 49 14 10.5 4.5
        $halo = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), 5
        $halo.LineJoin = 'Round'
        $g.DrawPath($halo, $gear)
        $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 59, 130, 246))), $gear)
    }

    $g.Dispose()
    return $bmp
}

# ICO（32bit DIB 形式）を書き出す
$sizes = @(16, 20, 24, 32, 40, 48, 64, 128)
$frames = @()
foreach ($s in $sizes) {
    $bmp = Draw $s
    if ($PreviewDir) { $bmp.Save((Join-Path $PreviewDir "icon_$s.png"), [System.Drawing.Imaging.ImageFormat]::Png) }
    $ms = New-Object System.IO.MemoryStream
    $w = New-Object System.IO.BinaryWriter $ms
    $maskRow = [int][Math]::Ceiling($s / 32.0) * 4
    $w.Write([int]40); $w.Write([int]$s); $w.Write([int]($s * 2)); $w.Write([int16]1); $w.Write([int16]32)
    $w.Write([int]0); $w.Write([int]($s * $s * 4 + $maskRow * $s)); $w.Write([int]0); $w.Write([int]0); $w.Write([int]0); $w.Write([int]0)
    for ($y = $s - 1; $y -ge 0; $y--) {   # 下の行から
        for ($x = 0; $x -lt $s; $x++) {
            $c = $bmp.GetPixel($x, $y)
            $w.Write([byte]$c.B); $w.Write([byte]$c.G); $w.Write([byte]$c.R); $w.Write([byte]$c.A)
        }
    }
    $w.Write((New-Object byte[] ($maskRow * $s)))
    $w.Flush()
    $frames += , @($s, $ms.ToArray())
    $bmp.Dispose()
}

$out = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $out
$w.Write([int16]0); $w.Write([int16]1); $w.Write([int16]$frames.Count)
$offset = 6 + 16 * $frames.Count
foreach ($f in $frames) {
    $s = $f[0]; $len = $f[1].Length
    $w.Write([byte]($s % 256)); $w.Write([byte]($s % 256)); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([int16]1); $w.Write([int16]32); $w.Write([int]$len); $w.Write([int]$offset)
    $offset += $len
}
foreach ($f in $frames) { $w.Write($f[1]) }
$w.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $PSScriptRoot "app.ico"), $out.ToArray())
"app.ico を作成しました"
