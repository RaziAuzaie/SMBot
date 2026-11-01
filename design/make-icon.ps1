# Renders the SMBot head (smbot-sprites.html?frame=icon) and packs it into src\SMBot\Assets\smbot.ico
# with the sizes Windows uses (16 to 256 px). Each size is stored as a PNG inside the .ico.
Add-Type -AssemblyName System.Drawing
$edge = @("${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe", "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe") |
    Where-Object { Test-Path $_ } | Select-Object -First 1
$page = Join-Path $PSScriptRoot "smbot-sprites.html"
$url = "file:///" + ($page -replace '\\', '/') + "?frame=icon"
$png = Join-Path $PSScriptRoot "smbot-icon.png"
$ico = Join-Path $PSScriptRoot "..\src\SMBot\Assets\smbot.ico"
$headPng = Join-Path $PSScriptRoot "..\src\SMBot\Assets\smbot-head.png"   # full-size copy for pictures inside the app

& $edge --headless=new --use-angle=swiftshader --enable-unsafe-swiftshader --hide-scrollbars `
    --default-background-color=00000000 --user-data-dir="$env:TEMP\smbot-sprite-render" --window-size=256,256 --force-device-scale-factor=2 `
    --virtual-time-budget=8000 --screenshot="$png" "$url" 2>$null | Out-Null

Copy-Item $png $headPng -Force
$source = [System.Drawing.Image]::FromFile($png)
$sizes = 256, 64, 48, 32, 24, 16
$images = foreach ($size in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = "HighQualityBicubic"; $g.SmoothingMode = "HighQuality"; $g.PixelOffsetMode = "HighQuality"
    $g.DrawImage($source, 0, 0, $size, $size)
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
    , $ms.ToArray()
}
$source.Dispose()

# ICO file: 6-byte header, a 16-byte directory entry per image, then the PNG data
$out = New-Object System.IO.BinaryWriter ([System.IO.File]::Create($ico))
$out.Write([uint16]0); $out.Write([uint16]1); $out.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }   # 0 means 256
    $out.Write([byte]$s); $out.Write([byte]$s); $out.Write([byte]0); $out.Write([byte]0)
    $out.Write([uint16]1); $out.Write([uint16]32)
    $out.Write([uint32]$images[$i].Length); $out.Write([uint32]$offset)
    $offset += $images[$i].Length
}
foreach ($data in $images) { $out.Write($data) }
$out.Close()
Write-Host "wrote $ico"
