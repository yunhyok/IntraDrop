# IntraDrop 아이콘(app.ico) 생성 스크립트
# PNG 압축 항목을 담은 다중 크기 ICO 파일을 만든다.
param(
    [string]$OutPath = "$PSScriptRoot\..\src\IntraDrop\app.ico"
)

Add-Type -AssemblyName System.Drawing

$sizes = 16, 24, 32, 48, 64, 128, 256
$pngs = @()

foreach ($s in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap($s, $s)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)

    # 둥근 사각형 파란 배경
    $m = [Math]::Max(1, [int]($s * 0.04))          # margin
    $r = [Math]::Max(2, [int]($s * 0.22))          # corner radius
    $w = $s - 2 * $m
    $rect = New-Object System.Drawing.Rectangle($m, $m, $w, $w)
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $path.AddArc($rect.X, $rect.Y, $d, $d, 180, 90)
    $path.AddArc($rect.Right - $d, $rect.Y, $d, $d, 270, 90)
    $path.AddArc($rect.Right - $d, $rect.Bottom - $d, $d, $d, 0, 90)
    $path.AddArc($rect.X, $rect.Bottom - $d, $d, $d, 90, 90)
    $path.CloseFigure()

    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        $rect,
        [System.Drawing.Color]::FromArgb(255, 59, 130, 246),
        [System.Drawing.Color]::FromArgb(255, 29, 78, 216),
        [System.Drawing.Drawing2D.LinearGradientMode]::Vertical)
    $g.FillPath($brush, $path)

    # 흰색 아래 화살표 (파일이 내려오는 모양)
    $white = [System.Drawing.Brushes]::White
    $cx = $s / 2.0
    $shaftW = $s * 0.14
    $headW = $s * 0.34
    $top = $s * 0.20
    $headTop = $s * 0.46
    $tip = $s * 0.64
    $arrow = New-Object System.Drawing.Drawing2D.GraphicsPath
    $pts = @(
        (New-Object System.Drawing.PointF(($cx - $shaftW), $top)),
        (New-Object System.Drawing.PointF(($cx + $shaftW), $top)),
        (New-Object System.Drawing.PointF(($cx + $shaftW), $headTop)),
        (New-Object System.Drawing.PointF(($cx + $headW), $headTop)),
        (New-Object System.Drawing.PointF($cx, $tip)),
        (New-Object System.Drawing.PointF(($cx - $headW), $headTop)),
        (New-Object System.Drawing.PointF(($cx - $shaftW), $headTop))
    )
    $arrow.AddPolygon($pts)
    $g.FillPath($white, $arrow)

    # 받침(트레이) 막대
    $barH = [Math]::Max(1.5, $s * 0.09)
    $barY = $s * 0.72
    $barX = $s * 0.24
    $barRect = New-Object System.Drawing.RectangleF($barX, $barY, ($s - 2 * $barX), $barH)
    $g.FillRectangle($white, $barRect)

    $g.Dispose()

    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngs += , ($ms.ToArray())
    $ms.Dispose()
    $bmp.Dispose()
}

# ICO 조립 (PNG 항목 방식, Vista 이상 지원)
$outFull = [System.IO.Path]::GetFullPath($OutPath)
$fs = [System.IO.File]::Create($outFull)
$bw = New-Object System.IO.BinaryWriter($fs)

$bw.Write([UInt16]0)                # reserved
$bw.Write([UInt16]1)                # type: icon
$bw.Write([UInt16]$sizes.Count)     # image count

$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]
    $len = $pngs[$i].Length
    $dim = if ($s -ge 256) { 0 } else { $s }
    $bw.Write([Byte]$dim)           # width
    $bw.Write([Byte]$dim)           # height
    $bw.Write([Byte]0)              # palette colors
    $bw.Write([Byte]0)              # reserved
    $bw.Write([UInt16]1)            # color planes
    $bw.Write([UInt16]32)           # bits per pixel
    $bw.Write([UInt32]$len)         # data length
    $bw.Write([UInt32]$offset)      # data offset
    $offset += $len
}
foreach ($png in $pngs) {
    $bw.Write($png)
}
$bw.Flush()
$bw.Close()

Write-Output "아이콘 생성 완료: $outFull"
