# Renders every SMBot frame from smbot-sprites.html into src\SMBot\Assets\Bot as transparent PNGs.
# Uses headless Microsoft Edge (software WebGL). Run again after changing the 3D design.
$edge = @("${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe", "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe") |
    Where-Object { Test-Path $_ } | Select-Object -First 1
$page = Join-Path $PSScriptRoot "smbot-sprites.html"
$url = "file:///" + ($page -replace '\\', '/')
$out = Join-Path $PSScriptRoot "..\src\SMBot\Assets\Bot"
$profileDir = Join-Path $env:TEMP "smbot-sprite-render"
New-Item -ItemType Directory -Force $out | Out-Null

$frames = @("neutral", "blink", "happy", "curious", "surprised", "sleepy", "wave1", "wave2", "wave3", "wave4") + (0..11 | ForEach-Object { "spin$_" })
foreach ($frame in $frames) {
    # 460x520 is twice the 230x260 size the bot is shown at, so it stays sharp on high-DPI screens
    & $edge --headless=new --use-angle=swiftshader --enable-unsafe-swiftshader --hide-scrollbars `
        --default-background-color=00000000 --user-data-dir="$profileDir" --window-size=460,520 `
        --virtual-time-budget=8000 --screenshot="$out\$frame.png" "$url`?frame=$frame" 2>$null | Out-Null
    Write-Host "rendered $frame"
}
