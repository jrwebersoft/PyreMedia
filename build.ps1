<#
    Rebuild MediaScout and deploy to MediaScout-App.

    Usage:   .\build.ps1            # Release build + deploy + launch
             .\build.ps1 -Debug     # Debug build (for attaching a debugger)
             .\build.ps1 -NoRun     # build + deploy, don't launch

    NOTE: must use the VS 2022 Build Tools MSBuild. MSBuild 18 (VS 2026) refuses
    .NET Framework 4.5.x-era projects outright, so we pin the 2022 toolchain.
#>
param(
    [switch]$Debug,
    [switch]$NoRun
)

$ErrorActionPreference = 'Stop'

$root   = $PSScriptRoot
$sln    = Join-Path $root 'MediaScout-v3\Decompile\MediaScout.sln'
$config = if ($Debug) { 'Debug' } else { 'Release' }
$outDir = Join-Path $root "MediaScout-v3\Decompile\MediaScoutGUI\bin\$config"
$appDir = Join-Path $root 'MediaScout-App'

$msbuild = 'C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe'
if (-not (Test-Path $msbuild)) {
    # fall back to whatever vswhere reports, preferring older instances
    $vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
    $msbuild = & $vswhere -products * -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' |
               Where-Object { $_ -match '2022|2019' } | Select-Object -First 1
}
if (-not $msbuild) { throw 'No suitable MSBuild found (need VS 2022 Build Tools or older).' }

Write-Host "Building $config ..." -ForegroundColor Cyan
$output = & $msbuild $sln /t:Build /p:Configuration=$config /p:Platform='Any CPU' /v:minimal /nologo /m 2>&1
$errors = $output | Select-String -Pattern ': error '

if ($errors) {
    Write-Host "BUILD FAILED ($($errors.Count) error(s))" -ForegroundColor Red
    $errors | ForEach-Object { Write-Host "  $($_.Line.Trim())" -ForegroundColor Red }
    exit 1
}
Write-Host 'Build succeeded.' -ForegroundColor Green

# Don't clobber a running instance
$running = Get-Process MediaScoutGUI -ErrorAction SilentlyContinue
if ($running) {
    Write-Host 'Closing running MediaScout ...' -ForegroundColor Yellow
    $running | Stop-Process -Force
    Start-Sleep -Milliseconds 700
}

New-Item -ItemType Directory -Force $appDir | Out-Null
Copy-Item "$outDir\*" $appDir -Force -Exclude *.pdb
Write-Host "Deployed to $appDir" -ForegroundColor Green

if (-not $NoRun) {
    Start-Process -FilePath (Join-Path $appDir 'MediaScoutGUI.exe') -WorkingDirectory $appDir
    Write-Host 'Launched.' -ForegroundColor Green
}
