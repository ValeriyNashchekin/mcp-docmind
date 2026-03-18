<#
.SYNOPSIS
    Builds McpDocMind.Setup as a single-file self-contained installer.
.DESCRIPTION
    1. Publishes McpDocMind.Lite (embedded into Setup as a resource)
    2. Publishes McpDocMind.Setup (the installer exe)
    3. Copies the final installer to ./out/
#>
param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"

$root = $PSScriptRoot
$outDir = Join-Path $root "out"

Write-Host "=== Building McpDocMind Setup ===" -ForegroundColor Cyan
Write-Host ""

# Step 1: Publish Lite
Write-Host "[1/3] Publishing McpDocMind.Lite..." -ForegroundColor Yellow
dotnet publish "$root\McpDocMind.Lite\McpDocMind.Lite.csproj" -c $Configuration -r $Runtime
if ($LASTEXITCODE -ne 0) {
    Write-Host "FAILED: McpDocMind.Lite publish failed." -ForegroundColor Red
    exit 1
}
Write-Host "  OK" -ForegroundColor Green

# Step 2: Publish Setup (embeds Lite.exe as resource)
Write-Host "[2/3] Publishing McpDocMind.Setup..." -ForegroundColor Yellow
dotnet publish "$root\McpDocMind.Setup\McpDocMind.Setup.csproj" -c $Configuration -r $Runtime
if ($LASTEXITCODE -ne 0) {
    Write-Host "FAILED: McpDocMind.Setup publish failed." -ForegroundColor Red
    exit 1
}
Write-Host "  OK" -ForegroundColor Green

# Step 3: Copy to out/
Write-Host "[3/3] Copying installer to out/..." -ForegroundColor Yellow
if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir | Out-Null }

$publishDir = Join-Path $root "McpDocMind.Setup\bin\$Configuration\net10.0\$Runtime\publish"
$exe = Join-Path $publishDir "McpDocMind.Setup.exe"

if (-not (Test-Path $exe)) {
    Write-Host "FAILED: McpDocMind.Setup.exe not found at $publishDir" -ForegroundColor Red
    exit 1
}

Copy-Item $exe (Join-Path $outDir "McpDocMind.Setup.exe") -Force
$size = [math]::Round((Get-Item (Join-Path $outDir "McpDocMind.Setup.exe")).Length / 1MB, 1)
Write-Host "  OK ($size MB)" -ForegroundColor Green

Write-Host ""
Write-Host "=== Done! Installer: out\McpDocMind.Setup.exe ===" -ForegroundColor Cyan
