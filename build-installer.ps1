# build-installer.ps1
# Publishes League Login as a self-contained single EXE, then builds installers.
#
# Usage:
#   .\build-installer.ps1              - publish + MSI (+ Inno Setup if ISCC on PATH)
#   .\build-installer.ps1 -ExeOnly     - publish only
#   .\build-installer.ps1 -NoInno      - publish + MSI only
#   .\build-installer.ps1 -NoMsi       - publish + Inno Setup only
#
# One-time setup:
#   MSI   : dotnet tool install --global wix
#           wix extension add --global WixToolset.UI.wixext/6.0.2
#   Inno  : install Inno Setup 6 from https://jrsoftware.org/isinfo.php
#           and ensure ISCC.exe is on PATH (or in the default Program Files path)

param(
    [switch]$ExeOnly,
    [switch]$NoInno,
    [switch]$NoMsi
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# Single source of truth for the version string. Update when bumping.
$Version   = "1.1.0"
$Project   = "LeagueLogin.csproj"
$OutDir    = "publish"
$InnoDir   = "installer-output"

$MsiName   = "LeagueLogin-$Version-x64.msi"

function Write-Step([string]$Message) {
    Write-Host ""
    Write-Host "==> $Message" -ForegroundColor Cyan
}

function Write-OK([string]$Message) {
    Write-Host "    $Message" -ForegroundColor Green
}

function Write-Skip([string]$Message) {
    Write-Host "    $Message" -ForegroundColor DarkGray
}

$results = @()

# --- Step 1: Clean previous publish output ---

if (Test-Path $OutDir) {
    Write-Step "Cleaning previous publish output"
    Remove-Item -Recurse -Force $OutDir
}

# --- Step 2: Publish ---

Write-Step "Publishing self-contained exe (version $Version)"

dotnet publish $Project `
    --configuration Release `
    --runtime win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:EnableCompressionInSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=false `
    -p:DebugType=none `
    -p:DebugSymbols=false `
    --output $OutDir

if ($LASTEXITCODE -ne 0) {
    Write-Error "dotnet publish failed."
    exit 1
}

$exe = Join-Path $OutDir "LeagueLogin.exe"
if (-not (Test-Path $exe)) {
    Write-Error "Expected exe not found: $exe"
    exit 1
}

$exeSizeMB = [math]::Round((Get-Item $exe).Length / 1MB, 1)
Write-OK "Published: $exe ($exeSizeMB MB)"
$results += [pscustomobject]@{ Kind = "EXE"; Path = $exe; SizeMB = $exeSizeMB }

# --- Optional: sign the EXE (requires a code-signing certificate) ---
# Uncomment and fill in your certificate thumbprint or PFX path.
# See SIGNING.md for full instructions.
#
# $thumb = "YOUR_CERT_THUMBPRINT_HERE"
# signtool sign /sha1 $thumb /tr http://timestamp.digicert.com /td sha256 /fd sha256 $exe
# if ($LASTEXITCODE -ne 0) { Write-Error "signtool failed."; exit 1 }
# Write-OK "EXE signed."

if ($ExeOnly) {
    Write-Host ""
    Write-Host "Done. Skipping installers (-ExeOnly)." -ForegroundColor Yellow
    exit 0
}

# --- Step 3: Build MSI with WiX v4 ---

if (-not $NoMsi) {
    Write-Step "Building MSI with WiX v4"

    $wix = Get-Command "wix" -ErrorAction SilentlyContinue
    if (-not $wix) {
        Write-Skip "WiX v4 not found. Install with: dotnet tool install --global wix"
        Write-Skip "Skipping MSI build."
    }
    else {
        wix build installer.wxs -ext WixToolset.UI.wixext -arch x64 -o $MsiName

        if ($LASTEXITCODE -ne 0) {
            Write-Error "WiX build failed."
            exit 1
        }

        if (Test-Path $MsiName) {
            $msiMB = [math]::Round((Get-Item $MsiName).Length / 1MB, 1)
            Write-OK "MSI ready: $MsiName ($msiMB MB)"
            $results += [pscustomobject]@{ Kind = "MSI"; Path = (Resolve-Path $MsiName).Path; SizeMB = $msiMB }

            # --- Optional: sign the MSI ---
            # signtool sign /sha1 $thumb /tr http://timestamp.digicert.com /td sha256 /fd sha256 $MsiName
        }
    }
}
else {
    Write-Step "MSI build skipped (-NoMsi)"
}

# --- Step 4: Build Inno Setup installer (optional) ---

if (-not $NoInno) {
    Write-Step "Building Inno Setup installer"

    # Look for ISCC.exe on PATH first, then fall back to the standard install paths.
    $iscc = Get-Command "ISCC.exe" -ErrorAction SilentlyContinue
    if (-not $iscc) {
        $candidates = @(
            "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
            "${env:ProgramFiles}\Inno Setup 6\ISCC.exe"
        )
        foreach ($c in $candidates) {
            if (Test-Path $c) { $iscc = Get-Item $c; break }
        }
    }

    if (-not $iscc) {
        Write-Skip "Inno Setup compiler (ISCC.exe) not found. Skipping."
        Write-Skip "Install from https://jrsoftware.org/isinfo.php to enable."
    }
    else {
        & $iscc.Source /Q installer.iss
        if ($LASTEXITCODE -ne 0) {
            Write-Error "Inno Setup compile failed."
            exit 1
        }

        $innoOutput = Join-Path $InnoDir "LeagueLogin-Setup-$Version.exe"
        if (Test-Path $innoOutput) {
            $innoMB = [math]::Round((Get-Item $innoOutput).Length / 1MB, 1)
            Write-OK "Inno installer ready: $innoOutput ($innoMB MB)"
            $results += [pscustomobject]@{ Kind = "Inno"; Path = (Resolve-Path $innoOutput).Path; SizeMB = $innoMB }
        }
        else {
            Write-Skip "Inno Setup reported success but output not found at $innoOutput."
        }
    }
}
else {
    Write-Step "Inno Setup build skipped (-NoInno)"
}

# --- Summary ---

Write-Host ""
Write-Host "═══════════════════════════════════════════════════════════" -ForegroundColor Cyan
Write-Host "  Build summary — League Login v$Version" -ForegroundColor Cyan
Write-Host "═══════════════════════════════════════════════════════════" -ForegroundColor Cyan
$results | Format-Table -AutoSize Kind, SizeMB, Path
