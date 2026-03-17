# build-installer.ps1
# Publishes League Login as a self-contained single EXE, then builds the MSI.
#
# Usage:
#   .\build-installer.ps1            - publish + MSI
#   .\build-installer.ps1 -ExeOnly   - publish only, skip MSI
#
# One-time setup to enable MSI builds:
#   dotnet tool install --global wix
#   wix extension add --global WixToolset.UI.wixext/6.0.2

param(
    [switch]$ExeOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$project = "LeagueLogin.csproj"
$outDir  = "publish"
$version = "1.0.3"

# --- Step 1: Publish ---

Write-Host "Publishing self-contained exe..."

dotnet publish $project `
    --configuration Release `
    --runtime win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none `
    -p:DebugSymbols=false `
    --output $outDir

if ($LASTEXITCODE -ne 0) {
    Write-Error "dotnet publish failed."
    exit 1
}

$exe = Join-Path $outDir "LeagueLogin.exe"
if (-not (Test-Path $exe)) {
    Write-Error "Expected exe not found: $exe"
    exit 1
}

$sizeMB = [math]::Round((Get-Item $exe).Length / 1MB, 1)
Write-Host "Published: $exe ($sizeMB MB)"
Write-Host "No .NET runtime needed on target machines - it is bundled inside the exe."

# --- Optional: sign the EXE (requires a code-signing certificate) ---
# Uncomment and fill in your certificate thumbprint or PFX path.
# See SIGNING.md for full instructions.
#
# $thumb = "YOUR_CERT_THUMBPRINT_HERE"
# signtool sign /sha1 $thumb /tr http://timestamp.digicert.com /td sha256 /fd sha256 $exe
# if ($LASTEXITCODE -ne 0) { Write-Error "signtool failed."; exit 1 }
# Write-Host "EXE signed."

if ($ExeOnly) {
    Write-Host "Done. Skipping MSI (-ExeOnly was set)."
    exit 0
}

# --- Step 2: Build MSI with WiX v4 ---

$wix = Get-Command "wix" -ErrorAction SilentlyContinue
if (-not $wix) {
    Write-Host ""
    Write-Host "WiX v4 not found. Install it once with:"
    Write-Host "  dotnet tool install --global wix"
    Write-Host ""
    Write-Host "Then re-run this script to produce the MSI."
    Write-Host "The published exe is already ready in: $outDir"
    exit 0
}

$msiName = "LeagueLogin-$version-x64.msi"
Write-Host "Building MSI: $msiName"

wix build installer.wxs -ext WixToolset.UI.wixext -o $msiName

if ($LASTEXITCODE -ne 0) {
    Write-Error "WiX build failed."
    exit 1
}

# --- Optional: sign the MSI too ---
# signtool sign /sha1 $thumb /tr http://timestamp.digicert.com /td sha256 /fd sha256 $msiName

if (Test-Path $msiName) {
    $msiMB = [math]::Round((Get-Item $msiName).Length / 1MB, 1)
    Write-Host "MSI ready: $msiName ($msiMB MB)"
} else {
    Write-Host "MSI build completed."
}
