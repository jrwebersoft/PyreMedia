# Builds PyreMedia as a single .exe - twice.
#
#   .\publish.ps1           64.5 MB each, run anywhere - the .NET runtime is inside
#   .\publish.ps1 -Small     7.2 MB each, needs .NET 10 installed on the machine
#   .\publish.ps1 -PrivateOnly   skip the shareable build
#
# Two builds from identical source:
#
#   PyreMedia-Release         yours. Carries the API keys from keys.local.json,
#                             so a fresh machine needs nothing typed in.
#   PyreMedia-Release-Clean   shareable. Built with EmbedKeys=false, so the same
#                             code without the keys. This is the one that gets
#                             kept under releases\ and the one to hand to anybody.
#
# One command for both, because a shareable build made separately drifts from the
# one actually being used, and the drift is only noticed by whoever it fails for.
#
# The clean build is checked for every key before it is offered as clean. A build
# labelled safe that is not is worse than no label - the label is what stops
# anybody looking again.
#
# Trimming would cut the big ones further but is not an option: the SDK refuses
# it outright for WPF (NETSDK1168). 64.5 MB is the floor for a standalone build,
# down from 140 MB uncompressed.
#
# ffmpeg/ffprobe and mkvmerge are deliberately NOT bundled: they are separate
# projects under their own licences, and the app locates them on PATH (About
# shows whether it found them).

param([switch]$Small, [switch]$PrivateOnly)

$ErrorActionPreference = 'Stop'

$proj = Join-Path $PSScriptRoot 'PyreMedia.NET\src\PyreMedia.App\PyreMedia.App.csproj'
$keys = Join-Path $PSScriptRoot 'PyreMedia.NET\src\PyreMedia.App\keys.local.json'

if (-not (Test-Path $proj)) { throw "project not found: $proj" }

# Close a running copy first - publish cannot overwrite a locked exe.
Get-Process PyreMedia -ErrorAction SilentlyContinue | Stop-Process -Force

function Build([string]$outDir, [bool]$withKeys) {
    $args = @(
        'publish', $proj, '-c', 'Release', '-r', 'win-x64', '-o', $outDir, '--nologo',
        '-p:PublishSingleFile=true', '-p:DebugType=none'
    )

    if ($Small) { $args += '--self-contained'; $args += 'false' }
    else {
        $args += @('--self-contained', 'true',
                   '-p:IncludeNativeLibrariesForSelfExtract=true',
                   '-p:IncludeAllContentForSelfExtract=true',
                   '-p:EnableCompressionInSingleFile=true')
    }

    if (-not $withKeys) { $args += '-p:EmbedKeys=false' }

    & dotnet @args
    if ($LASTEXITCODE -ne 0) { throw "publish failed ($LASTEXITCODE)" }
}

# Every key that must not appear in the shareable build. Read from the file
# rather than named here, so a key added later is covered without this script
# being remembered.
function KeyValues {
    if (-not (Test-Path $keys)) { return @() }

    $json = Get-Content $keys -Raw | ConvertFrom-Json
    return $json.PSObject.Properties |
        Where-Object { $_.Value -is [string] -and $_.Value.Trim().Length -ge 8 } |
        ForEach-Object { $_.Value.Trim() }
}

# Whether a key appears in a binary. Read as raw bytes and searched as
# ISO-8859-1, which maps every byte to the character of the same value, so the
# bytes survive the round trip - reading 64 MB as text and searching for a
# substring is the obvious way and mangles anything non-textual.
#
# Codepage 28591 rather than [Text.Encoding]::Latin1: this runs under Windows
# PowerShell 5.1, where that property does not exist and resolves to null,
# which fails as "cannot call a method on a null-valued expression" two frames
# away from the cause.
function ContainsKey([string]$path, [string]$value) {
    $bytes = [IO.File]::ReadAllBytes($path)
    $text  = [Text.Encoding]::GetEncoding(28591).GetString($bytes)
    return $text.Contains($value)
}

# Every file that would be handed over, not just the exe.
#
# Checking only PyreMedia.exe was a hole big enough to drive the keys through.
# The keys reach the exe by way of IncludeAllContentForSelfExtract, which is
# passed for the standalone build and not for -Small; in -Small the csproj's
# CopyToOutputDirectory puts keys.local.json beside the exe as plain readable
# JSON instead. So the script was reporting "0 of 2 key(s) embedded" and
# "none present" about a folder holding both keys in cleartext - the exact
# false safe-label this file's own header warns about.
function FilesCarryingKey([string]$dir, [string]$value) {
    Get-ChildItem $dir -Recurse -File | Where-Object { ContainsKey $_.FullName $value }
}

$suffix    = if ($Small) { '-Small' } else { '' }
$privDir   = Join-Path $PSScriptRoot "PyreMedia-Release$suffix"
$cleanDir  = Join-Path $PSScriptRoot "PyreMedia-Release-Clean$suffix"

$keyValues = @(KeyValues)

# ---- yours -------------------------------------------------------------
Write-Host ""
Write-Host "Building your copy (keys embedded)..." -ForegroundColor Cyan
Build $privDir $true

$privExe = Join-Path $privDir 'PyreMedia.exe'
$privMb  = [math]::Round((Get-Item $privExe).Length / 1MB, 1)

if ($keyValues.Count -eq 0) {
    Write-Host "  no keys.local.json - this build carries no keys" -ForegroundColor Yellow
} else {
    $found = @($keyValues | Where-Object { ContainsKey $privExe $_ }).Count
    Write-Host "  $found of $($keyValues.Count) key(s) embedded" -ForegroundColor Green

    # Say plainly when a key is readable beside the exe rather than inside it.
    # This folder is never meant to be handed out either way, but "do not hand
    # this out" means something different when the keys are in a text file
    # anybody can open.
    $loose = @($keyValues | ForEach-Object { FilesCarryingKey $privDir $_ } |
               Where-Object { $_.FullName -ne $privExe } |
               Select-Object -ExpandProperty Name -Unique)

    if ($loose.Count -gt 0) {
        Write-Host "  keys also sit in the open beside the exe: $($loose -join ', ')" -ForegroundColor Yellow
    }
}

Write-Host "Built $privExe  ($privMb MB)" -ForegroundColor Green

if ($PrivateOnly) {
    Write-Host ""
    Write-Host "Shareable build skipped (-PrivateOnly). Nothing kept under releases\." -ForegroundColor Yellow
    return
}

# ---- shareable ---------------------------------------------------------
Write-Host ""
Write-Host "Building the shareable copy (no keys)..." -ForegroundColor Cyan
Build $cleanDir $false

$cleanExe = Join-Path $cleanDir 'PyreMedia.exe'
$cleanMb  = [math]::Round((Get-Item $cleanExe).Length / 1MB, 1)

# Every file in the folder, because the folder is what gets copied. A key in
# a loose keys.local.json is handed over exactly as completely as one compiled
# into the exe, and for longer - it is readable without any tooling at all.
$leaked = @()

foreach ($k in $keyValues) {
    $leaked += @(FilesCarryingKey $cleanDir $k)
}

if ($leaked.Count -gt 0) {
    $names = ($leaked | Select-Object -ExpandProperty FullName -Unique)

    # Take the whole folder, not just the exe. Deleting one file out of a
    # directory that still holds the key would leave the leak behind.
    Remove-Item $cleanDir -Recurse -Force

    throw ("the shareable build carried keys in: " + ($names -join ', ') +
           ". Deleted the whole folder rather than leave it looking safe.")
}

$fileCount = @(Get-ChildItem $cleanDir -Recurse -File).Count
Write-Host "  checked $fileCount file(s) against $($keyValues.Count) key(s) - none present" -ForegroundColor Green
Write-Host "Built $cleanExe  ($cleanMb MB)" -ForegroundColor Green

# ---- keep the shareable one -------------------------------------------
#
# The clean build is what gets kept, not yours. A kept release is meant to be
# rebuildable from its tag and checked against the .sha256 beside it, and a
# binary carrying a private key can never be reproduced by anybody else - so
# keeping that one would put a hash in the record that nothing can verify.
$version = (Get-Item $cleanExe).VersionInfo.FileVersion -replace '\.0$', ''
$keepDir = Join-Path $PSScriptRoot "releases\v$version"
$keep    = Join-Path $keepDir "PyreMedia-$version.exe"

if (Test-Path $keep) {
    $existing = (Get-FileHash $keep -Algorithm SHA256).Hash
    $fresh    = (Get-FileHash $cleanExe -Algorithm SHA256).Hash

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
    Copy-Item $cleanExe $keep
    (Get-FileHash $keep -Algorithm SHA256).Hash |
        Out-File "$keep.sha256" -Encoding ascii -NoNewline
    Write-Host "Kept the shareable build as releases\v$version\PyreMedia-$version.exe" -ForegroundColor Green
}

Write-Host ""
Write-Host "Yours:     $privDir\PyreMedia.exe   (has your keys - do not hand this out)"
Write-Host "Shareable: $cleanDir\PyreMedia.exe   (asks for keys on first run)"

if ($Small) {
    Write-Host "Both need the .NET 10 Desktop Runtime on the machine that runs them."
}

Write-Host "ffmpeg/ffprobe and mkvmerge are still needed for remuxing; see About."
