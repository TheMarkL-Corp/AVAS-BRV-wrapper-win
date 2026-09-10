<#
.SYNOPSIS
    Builds and packages the complete offline package installer for AVAS Routing Software.
.DESCRIPTION
    1. Compiles and publishes latest application payload (AVAS Routing SW)
    2. Packages standalone portable zip (AVAS-Routing-SW-vX.X.X-win-x64.zip)
    3. Validates offline dependencies (windowsdesktop-runtime-8.0.30-win-x64.exe, MicrosoftEdgeWebView2RuntimeInstallerX64.exe)
    4. Stages Advantech White-Label branding assets
    5. Builds self-contained single-file AvasRoutingSetup.exe
    6. Assembles the complete zero-internet installer distribution bundle and zip archive
#>

param(
    [string]$Version = "1.2.0",
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$RepoRoot = Split-Path -Parent $ScriptDir
$ReleasesDir = Join-Path $RepoRoot "releases"
$AppReleaseDir = Join-Path $ReleasesDir "AVAS-Routing-SW-v$Version-win-x64"
$AppZipPath = Join-Path $ReleasesDir "AVAS-Routing-SW-v$Version-win-x64.zip"
$InstallerDistDir = Join-Path $ReleasesDir "AVAS-Routing-SW-Installer-v$Version"
$InstallerZipPath = Join-Path $ReleasesDir "AVAS-Routing-SW-Installer-v$Version.zip"

Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host " Building AVAS Routing Software v$Version Release Package " -ForegroundColor Cyan
Write-Host "==========================================================" -ForegroundColor Cyan

# 1. Build and Publish Application Payload
Write-Host "`n[1/7] Compiling AVAS Routing Software v$Version..." -ForegroundColor Yellow
$AppProj = Join-Path $RepoRoot "src\AvasRoutingApp\AvasRoutingApp.csproj"

if (Test-Path $AppReleaseDir) {
    Remove-Item $AppReleaseDir -Recurse -Force
}

dotnet publish $AppProj `
    -c $Configuration `
    -r win-x64 `
    --no-self-contained `
    -o $AppReleaseDir

# Copy documentation and icons into app release
Copy-Item (Join-Path $RepoRoot "QUICKSTART.md") "$AppReleaseDir\" -Force -ErrorAction SilentlyContinue
Copy-Item (Join-Path $RepoRoot "USER_MANUAL.md") "$AppReleaseDir\" -Force -ErrorAction SilentlyContinue
Copy-Item (Join-Path $RepoRoot "VERIFICATION_GUIDE.md") "$AppReleaseDir\" -Force -ErrorAction SilentlyContinue
Copy-Item (Join-Path $RepoRoot "LOOP_TEST_RESULTS.md") "$AppReleaseDir\" -Force -ErrorAction SilentlyContinue
if (Test-Path (Join-Path $RepoRoot "src\AvasRoutingApp\logo.ico")) {
    Copy-Item (Join-Path $RepoRoot "src\AvasRoutingApp\logo.ico") "$AppReleaseDir\" -Force
}

# Create portable application zip
Write-Host "  -> Compressing portable application zip: $AppZipPath..." -ForegroundColor Green
if (Test-Path $AppZipPath) {
    Remove-Item $AppZipPath -Force
}
Compress-Archive -Path "$AppReleaseDir\*" -DestinationPath $AppZipPath -CompressionLevel Optimal
Write-Host "  -> Portable ZIP: $AppZipPath ($([math]::Round((Get-Item $AppZipPath).Length / 1KB, 1)) KB)" -ForegroundColor Green

# 2. Prepare Installer Directories
Write-Host "`n[2/7] Preparing installer directories..." -ForegroundColor Yellow
if (Test-Path $InstallerDistDir) {
    Remove-Item $InstallerDistDir -Recurse -Force
}
New-Item -ItemType Directory -Path "$InstallerDistDir\redist", "$InstallerDistDir\payload", "$InstallerDistDir\whitelabel" -Force | Out-Null

# 3. Stage Offline Redistributables (Zero-Internet Prerequisites)
Write-Host "`n[3/7] Staging offline dependencies..." -ForegroundColor Yellow
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

# 4. Stage Application Payload into Installer
Write-Host "`n[4/7] Staging application payload into installer..." -ForegroundColor Yellow
Copy-Item -Path "$AppReleaseDir\*" -Destination "$InstallerDistDir\payload\" -Recurse -Force
Write-Host "  -> Staged $(@(Get-ChildItem "$InstallerDistDir\payload" -Recurse -File).Count) application payload files" -ForegroundColor Green

# 5. Stage Advantech White-Labeling Assets
Write-Host "`n[5/7] Staging Advantech White-Labeling assets..." -ForegroundColor Yellow
$WlSourceDir = Join-Path $ScriptDir "whitelabel"
if (Test-Path "$WlSourceDir\logo.svg") {
    Copy-Item -Path "$WlSourceDir\*" -Destination "$InstallerDistDir\whitelabel\" -Force
    Write-Host "  -> Staged Advantech logo.svg and index.js white-label configuration" -ForegroundColor Green
} else {
    $PatchDir = Join-Path $DownloadsDir "Advantech BR Patch"
    if (Test-Path "$PatchDir\image\logo.svg") {
        Copy-Item -Path "$PatchDir\image\logo.svg" -Destination "$InstallerDistDir\whitelabel\" -Force
        Copy-Item -Path "$PatchDir\source\index.js" -Destination "$InstallerDistDir\whitelabel\" -Force
        Write-Host "  -> Staged white-label assets from Downloads/Advantech BR Patch" -ForegroundColor Green
    }
}

# 6. Build Self-Contained Installer Executable
Write-Host "`n[6/7] Compiling self-contained single-file AvasRoutingSetup.exe..." -ForegroundColor Yellow
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

# 7. Create Convenience Launcher, Documentation & ZIP Archive
Write-Host "`n[7/7] Generating Install.bat launcher, README, and distribution archive..." -ForegroundColor Yellow

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
# AVAS Routing Software — Package Installer (v$Version)

This is the offline (zero-internet) installer bundle for **AVAS Routing Software v$Version**.

> **IMPORTANT PREREQUISITE NOTICE**:
> This package installer does NOT include the BlueRiver AV Manager installer. In order for this application and the Advantech white-labeling feature to work properly, **BlueRiver AV Manager must already be installed** on the target machine (or accessible over the local network).

## How to Install
Double-click `AvasRoutingSetup.exe` (or run `Install.bat`).

## What this Installer does:
1. **Zero-Internet Dependency Installation**:
   - Automatically detects if .NET 8 Windows Desktop Runtime (x64) is installed. If missing, silently installs from local package (`redist/windowsdesktop-runtime-8.0.30-win-x64.exe`).
   - Automatically detects if Microsoft Edge WebView2 Runtime is installed. If missing, silently installs from local package (`redist/MicrosoftEdgeWebView2RuntimeInstallerX64.exe`).
2. **Shortcuts**:
   - Allows user to create a Desktop icon and Start Menu shortcut under `Advantech`.
3. **Advantech White-Labeling (4-Step Pipeline)**:
   - Step 1: Detects and stops `bavm` Windows service (if missing, notifies user and continues without failing).
   - Step 2: Backs up original `logo.svg` and deploys Advantech corporate `logo.svg`.
   - Step 3: Backs up `index.js` and applies Advantech branding (`APP_TITLE`, `APP_HEADER`, `THEME_PRIMARY_COLOR: #0055afff`).
   - Step 4: Restarts `bavm` service to apply changes.
4. **Windows Programs & Features**:
   - Registers entry and provides uninstaller script in application directory.
"@
Set-Content -Path "$InstallerDistDir\README.md" -Value $ReadmeContent -Encoding UTF8

Write-Host "  -> Compressing distribution archive: $InstallerZipPath..." -ForegroundColor Green
if (Test-Path $InstallerZipPath) {
    Remove-Item $InstallerZipPath -Force
}
Compress-Archive -Path "$InstallerDistDir\*" -DestinationPath $InstallerZipPath -CompressionLevel Optimal
Write-Host "  -> Created archive: $InstallerZipPath ($([math]::Round((Get-Item $InstallerZipPath).Length / 1MB, 1)) MB)" -ForegroundColor Green

Write-Host "`n==========================================================" -ForegroundColor Green
Write-Host " Release Packaging Complete for v$Version!                " -ForegroundColor Green
Write-Host " Portable App Bundle: $AppReleaseDir" -ForegroundColor Green
Write-Host " Portable App Zip:    $AppZipPath" -ForegroundColor Green
Write-Host " Installer Bundle:    $InstallerDistDir" -ForegroundColor Green
Write-Host " Installer Zip:       $InstallerZipPath" -ForegroundColor Green
Write-Host "==========================================================" -ForegroundColor Green
