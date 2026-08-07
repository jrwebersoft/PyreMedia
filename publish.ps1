# Builds PyreMedia as a single .exe.
#
#   .\publish.ps1           64.5 MB, runs anywhere - the .NET runtime is inside
#   .\publish.ps1 -Small     7.2 MB, needs .NET 10 installed on the machine
#
# Trimming would cut the big one further but is not an option: the SDK refuses
# it outright for WPF (NETSDK1168). 64.5 MB is the floor for a standalone build,
# down from 140 MB uncompressed.
#
# ffmpeg/ffprobe and mkvmerge are deliberately NOT bundled: they are separate
# projects under their own licences, and the app locates them on PATH (About
# shows whether it found them).

param([switch]$Small)

$ErrorActionPreference = 'Stop'

$proj = Join-Path $PSScriptRoot 'PyreMedia.NET\src\PyreMedia.App\PyreMedia.App.csproj'
$out  = Join-Path $PSScriptRoot ($(if ($Small) { 'PyreMedia-Release-Small' } else { 'PyreMedia-Release' }))

if (-not (Test-Path $proj)) { throw "project not found: $proj" }

# Close a running copy first - publish cannot overwrite a locked exe.
Get-Process PyreMedia -ErrorAction SilentlyContinue | Stop-Process -Force

if ($Small) {
    dotnet publish $proj -c Release -r win-x64 --self-contained false -o $out --nologo `
        -p:PublishSingleFile=true `
        -p:DebugType=none
}
else {
    dotnet publish $proj -c Release -r win-x64 --self-contained true -o $out --nologo `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:IncludeAllContentForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true `
        -p:DebugType=none
}

if ($LASTEXITCODE -ne 0) { throw "publish failed ($LASTEXITCODE)" }

$exe = Join-Path $out 'PyreMedia.exe'
$mb  = [math]::Round((Get-Item $exe).Length / 1MB, 1)

Write-Host ""
Write-Host "Built $exe  ($mb MB)" -ForegroundColor Green

# Keep the build under its own version number, and never write over one that is
# already there. A kept release is the thing you fall back to when a new version
# turns out to be wrong, so it must not be quietly replaced by the build that
# went wrong.
$version = (Get-Item $exe).VersionInfo.FileVersion -replace '\.0$', ''
$keepDir = Join-Path $PSScriptRoot "releases\v$version"
$keep    = Join-Path $keepDir "PyreMedia-$version.exe"

if (Test-Path $keep) {
    $existing = (Get-FileHash $keep -Algorithm SHA256).Hash
    $fresh    = (Get-FileHash $exe  -Algorithm SHA256).Hash

    if ($existing -eq $fresh) {
        Write-Host "releases\v$version already holds this exact build - left alone." -ForegroundColor DarkGray
    }
    else {
        Write-Host ""
        Write-Host "NOT kept: releases\v$version already holds a different build." -ForegroundColor Yellow
        Write-Host "Bump <Version> in PyreMedia.App.csproj, or remove that folder by hand" -ForegroundColor Yellow
        Write-Host "if you really mean to replace a released build." -ForegroundColor Yellow
    }
}
else {
    New-Item -ItemType Directory -Force $keepDir | Out-Null
    Copy-Item $exe $keep
    (Get-FileHash $keep -Algorithm SHA256).Hash |
        Out-File "$keep.sha256" -Encoding ascii -NoNewline
    Write-Host "Kept as releases\v$version\PyreMedia-$version.exe" -ForegroundColor Green
}

if ($Small) {
    Write-Host "Needs .NET 10 Desktop Runtime on the machine that runs it."
    Write-Host "For a copy that runs anywhere, build without -Small (64.5 MB)."
}
else {
    Write-Host "Single file - copy it anywhere and run it. No .NET install required."
}

Write-Host "ffmpeg/ffprobe and mkvmerge are still needed for remuxing; see About."
