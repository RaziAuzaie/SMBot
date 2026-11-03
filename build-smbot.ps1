# Builds the latest SMBot into src\SMBot\SMBot.exe (the only copy to run) and points the desktop shortcut at it.
# Run from anywhere:  powershell -ExecutionPolicy Bypass -File build-smbot.ps1
$ErrorActionPreference = "Stop"
$project = Join-Path $PSScriptRoot "src\SMBot"
$exe = Join-Path $project "SMBot.exe"

# A running SMBot locks its exe; close it first
Get-Process SMBot -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 2

# One self-contained-enough file next to the code (uses the installed .NET 10 runtime)
dotnet publish (Join-Path $project "SMBot.csproj") -c Release -r win-x64 --self-contained false `
    -p:PublishSingleFile=true -p:DebugType=none -o $project -nologo
if ($LASTEXITCODE -ne 0) { throw "Build failed" }

# The build leaves intermediate copies in bin and obj; remove them so there's only one SMBot.exe
Remove-Item (Join-Path $project "bin"), (Join-Path $project "obj") -Recurse -Force -ErrorAction SilentlyContinue

# Desktop shortcut to the one copy
$shortcut = (New-Object -ComObject WScript.Shell).CreateShortcut((Join-Path ([Environment]::GetFolderPath("Desktop")) "SMBot.lnk"))
$shortcut.TargetPath = $exe
$shortcut.WorkingDirectory = $project
$shortcut.IconLocation = "$exe,0"
$shortcut.Save()

Start-Process $exe
"SMBot built and started: $exe"
