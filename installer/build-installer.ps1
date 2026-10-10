<#
.SYNOPSIS
    Publishes OpenMouseConfig and compiles the Inno Setup installer.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File installer\build-installer.ps1
    powershell -ExecutionPolicy Bypass -File installer\build-installer.ps1 -Version 0.2.0
    powershell -ExecutionPolicy Bypass -File installer\build-installer.ps1 -SkipTests

.NOTES
    Requires the .NET 10 SDK and Inno Setup 6.3+ (https://jrsoftware.org/isdl.php).
    Output: artifacts\installer\OpenMouseConfig-Setup-<Version>-x64.exe
#>
[CmdletBinding()]
param(
    [string] $Version = "0.1.0",
    [string] $Runtime = "win-x64",
    [switch] $SkipTests
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$publishDir = Join-Path $root "artifacts\publish\$Runtime"

function Invoke-Step([string] $Title, [scriptblock] $Action) {
    Write-Host "==> $Title" -ForegroundColor Cyan
    & $Action
    if ($LASTEXITCODE -ne 0) { throw "$Title failed (exit code $LASTEXITCODE)." }
}

if (-not $SkipTests) {
    Invoke-Step "Running unit tests" {
        dotnet test (Join-Path $root "tests\OpenMouseConfig.Core.Tests\OpenMouseConfig.Core.Tests.csproj") -c Release
    }
}

if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }

# Self-contained: end users don't need to install the .NET Desktop Runtime.
Invoke-Step "Publishing $Runtime build $Version" {
    dotnet publish (Join-Path $root "src\OpenMouseConfig\OpenMouseConfig.csproj") `
        -c Release -r $Runtime --self-contained true `
        -p:Version=$Version -p:DebugType=none `
        -o $publishDir
}

# Locate the Inno Setup compiler (PATH, then the default install locations).
$iscc = (Get-Command "iscc.exe" -ErrorAction SilentlyContinue).Source
if (-not $iscc) {
    $iscc = @(
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
    ) | Where-Object { Test-Path $_ } | Select-Object -First 1
}
if (-not $iscc) {
    throw "ISCC.exe not found. Install Inno Setup 6.3+ (winget install JRSoftware.InnoSetup) or add it to PATH."
}

Invoke-Step "Compiling installer" {
    & $iscc "/DAppVersion=$Version" "/DPublishDir=$publishDir" (Join-Path $PSScriptRoot "OpenMouseConfig.iss")
}

Write-Host "Done: $(Join-Path $root 'artifacts\installer')" -ForegroundColor Green
