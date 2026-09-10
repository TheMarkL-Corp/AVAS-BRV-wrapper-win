<#
.SYNOPSIS
    Builds and packages the complete offline package installer for AVAS Routing Software.
.DESCRIPTION
    1. Validates offline dependencies (windowsdesktop-runtime-8.0.30-win-x64.exe, MicrosoftEdgeWebView2RuntimeInstallerX64.exe)
    2. Packages the v1.1.0 application payload
    3. Stages Advantech White-Label branding assets
    4. Builds self-contained single-file AvasRoutingSetup.exe
    5. Assembles the complete zero-internet installer distribution bundle and zip archive
#>

param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$RepoRoot = Split-Path -Parent $ScriptDir
$ReleasesDir = Join-Path $RepoRoot "releases"
$InstallerDistDir = Join-Path $ReleasesDir "AVAS-Routing-SW-Installer-v1.1.0"
$InstallerZipPath = Join-Path $ReleasesDir "AVAS-Routing-SW-Installer-v1.1.0.zip"

Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host " Building AVAS Routing Software Offline Package Installer " -ForegroundColor Cyan
Write-Host "==========================================================" -ForegroundColor Cyan

# 1. Ensure Target Directories
Write-Host "`n[1/6] Preparing directories..." -ForegroundColor Yellow
if (Test-Path $InstallerDistDir) {
    Remove-Item $InstallerDistDir -Recurse -Force
}
New-Item -ItemType Directory -Path "$InstallerDistDir\redist", "$InstallerDistDir\payload", "$InstallerDistDir\whitelabel" -Force | Out-Null

# 2. Stage Offline Redistributables (Zero-Internet Prerequisites)
Write-Host "`n[2/6] Staging offline dependencies..." -ForegroundColor Yellow
$DownloadsDir = Join-Path $env:USERPROFILE "Downloads"
$RedistFiles = @(
    "windowsdesktop-runtime-8.0.30-win-x64.exe",
    "MicrosoftEdgeWebView2RuntimeInstallerX64.exe"
)

foreach ($file in $RedistFiles) {
    $src = Join-Path "$ScriptDir\redist" $file
    if (-not (Test-Path $src)) {
        $src = Join-Path $DownloadsDir $file
    }
    if (Test-Path $src) {
        Copy-Item -Path $src -Destination "$InstallerDistDir\redist\" -Force
        Write-Host "  -> Bundled: $file ($([math]::Round((Get-Item $src).Length / 1MB, 1)) MB)" -ForegroundColor Green
    } else {
        Write-Warning "Redistributable missing: $file. Checked $ScriptDir\redist and $DownloadsDir"
    }
}

# 3. Stage Application Payload (AVAS Routing SW v1.1.0)
Write-Host "`n[3/6] Staging application payload..." -ForegroundColor Yellow
$PayloadSrc = Join-Path $ReleasesDir "AVAS-Routing-SW-v1.1.0-win-x64"
if (Test-Path $PayloadSrc) {
    Copy-Item -Path "$PayloadSrc\*" -Destination "$InstallerDistDir\payload\" -Recurse -Force
    Write-Host "  -> Staged $(@(Get-ChildItem "$InstallerDistDir\payload" -Recurse -File).Count) application payload files from releases/AVAS-Routing-SW-v1.1.0-win-x64" -ForegroundColor Green
} else {
    throw "Application release folder not found at: $PayloadSrc"
}

# 4. Stage Advantech White-Labeling Assets
Write-Host "`n[4/6] Staging Advantech White-Labeling assets..." -ForegroundColor Yellow
$WlSourceDir = Join-Path $ScriptDir "whitelabel"
if (Test-Path "$WlSourceDir\logo.svg") {
    Copy-Item -Path "$WlSourceDir\*" -Destination "$InstallerDistDir\whitelabel\" -Force
    Write-Host "  -> Staged Advantech logo.svg and index.js white-label configuration" -ForegroundColor Green
} else {
    # Fallback to Downloads
    $PatchDir = Join-Path $DownloadsDir "Advantech BR Patch"
    if (Test-Path "$PatchDir\image\logo.svg") {
        Copy-Item -Path "$PatchDir\image\logo.svg" -Destination "$InstallerDistDir\whitelabel\" -Force
        Copy-Item -Path "$PatchDir\source\index.js" -Destination "$InstallerDistDir\whitelabel\" -Force
        Write-Host "  -> Staged white-label assets from Downloads/Advantech BR Patch" -ForegroundColor Green
    }
}

# 5. Build Self-Contained Installer Executable
Write-Host "`n[5/6] Compiling self-contained single-file AvasRoutingSetup.exe..." -ForegroundColor Yellow
$SetupProj = Join-Path $RepoRoot "src\AvasRoutingSetup\AvasRoutingSetup.csproj"
$PublishDir = Join-Path $ScriptDir "bin"

dotnet publish $SetupProj `
    -c $Configuration `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -o $PublishDir

if (-not (Test-Path "$PublishDir\AvasRoutingSetup.exe")) {
    throw "Failed to compile AvasRoutingSetup.exe"
}

Copy-Item -Path "$PublishDir\AvasRoutingSetup.exe" -Destination "$InstallerDistDir\" -Force
Write-Host "  -> Successfully built and copied AvasRoutingSetup.exe ($([math]::Round((Get-Item "$InstallerDistDir\AvasRoutingSetup.exe").Length / 1MB, 1)) MB)" -ForegroundColor Green

# 6. Create Convenience Launcher & Documentation
Write-Host "`n[6/6] Generating Install.bat launcher and README..." -ForegroundColor Yellow

$InstallBatContent = @"
@echo off
setlocal
cd /d "%~dp0"
echo Launching AVAS Routing Software Setup Wizard...
start "" "%~dp0AvasRoutingSetup.exe"
endlocal
"@
Set-Content -Path "$InstallerDistDir\Install.bat" -Value $InstallBatContent -Encoding ASCII

$ReadmeContent = @"
# AVAS Routing Software — Package Installer (v1.1.0)

This is the offline (zero-internet) installer bundle for **AVAS Routing Software v1.1.0**.

## How to Install
Double-click `AvasRoutingSetup.exe` (or run `Install.bat`).

## What this Installer does:
1. **Zero-Internet Dependency Installation**:
   - Checks if .NET 8 Windows Desktop Runtime (x64) is installed. If missing, automatically installs it from the local bundle (`redist/windowsdesktop-runtime-8.0.30-win-x64.exe`).
   - Checks if Microsoft Edge WebView2 Runtime is installed. If missing, automatically installs it from the local bundle (`redist/MicrosoftEdgeWebView2RuntimeInstallerX64.exe`).
2. **Shortcuts**:
   - Prompts the user to create a Desktop icon and Start Menu shortcuts with the official application logo.
3. **Advantech White-Labeling**:
   - Prompts the user to white-label BlueRiver AV Manager's UI and backend configuration to Advantech branding.
   - Deploys Advantech `logo.svg` and branded `index.js` into `%APPDATA%\Semtech\BlueRiver AV Manager\app`.
   - Automatically restarts the `bavm` Windows service if running.
4. **Windows Programs & Features**:
   - Registers an entry and provides an uninstaller script in the application folder.
"@
Set-Content -Path "$InstallerDistDir\README.md" -Value $ReadmeContent -Encoding UTF8

# 7. Create ZIP archive
Write-Host "`nCompressing distribution into: $InstallerZipPath..." -ForegroundColor Yellow
if (Test-Path $InstallerZipPath) {
    Remove-Item $InstallerZipPath -Force
}
Compress-Archive -Path "$InstallerDistDir\*" -DestinationPath $InstallerZipPath -CompressionLevel Optimal
Write-Host "  -> Created archive: $InstallerZipPath ($([math]::Round((Get-Item $InstallerZipPath).Length / 1MB, 1)) MB)" -ForegroundColor Green

Write-Host "`n==========================================================" -ForegroundColor Green
Write-Host " Installer Packaging Complete!                            " -ForegroundColor Green
Write-Host " Directory: $InstallerDistDir" -ForegroundColor Green
Write-Host " Archive:   $InstallerZipPath" -ForegroundColor Green
Write-Host "==========================================================" -ForegroundColor Green
