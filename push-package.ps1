#!/usr/bin/env pwsh
<#
    .SYNOPSIS
    Push AzureFunctions.Worker.Extensions.AppConfiguration.Host package to idun-corp

    .DESCRIPTION
    Builds the AppConfiguration.Host project and pushes the generated NuGet packages to idun-corp feed.
    Requires NuGet CLI to be installed and accessible in PATH.

    .PARAMETER SkipBuild
    Skip the build step and only push existing packages

    .PARAMETER ApiKey
    Azure DevOps Personal Access Token for authentication. If not provided, will use cached credentials.

    .EXAMPLE
    .\push-package.ps1

    .EXAMPLE
    .\push-package.ps1 -SkipBuild

    .EXAMPLE
    .\push-package.ps1 -ApiKey "your-pat-token"
#>

param(
    [switch]$SkipBuild,
    [string]$ApiKey
)

$ErrorActionPreference = "Stop"

# Configuration
$projectPath = "src/AzureFunctions.Worker.Extensions.AppConfiguration.Host"
$projectName = "AzureFunctions.Worker.Extensions.AppConfiguration.Host"
$buildConfig = "Release"
$feedUrl = "https://pkgs.dev.azure.com/idun-corp/_packaging/idun-corp/nuget/v3/index.json"

Write-Host "=== Azure Functions AppConfiguration.Host Package Push ===" -ForegroundColor Cyan

# Step 1: Build the project
if (-not $SkipBuild) {
    Write-Host "`n[1/3] Building project..." -ForegroundColor Yellow
    try {
        dotnet build "$projectPath/$projectName.csproj" -c $buildConfig
        Write-Host "✓ Build completed successfully" -ForegroundColor Green
    }
    catch {
        Write-Host "✗ Build failed: $_" -ForegroundColor Red
        exit 1
    }
}
else {
    Write-Host "`n[1/3] Skipping build..." -ForegroundColor Yellow
}

# Step 2: Locate the generated packages
Write-Host "`n[2/3] Locating generated packages..." -ForegroundColor Yellow
$packagePath = "$projectPath/bin/$buildConfig"

if (-not (Test-Path $packagePath)) {
    Write-Host "✗ Package directory not found: $packagePath" -ForegroundColor Red
    exit 1
}

$nupkg = Get-Item "$packagePath/$projectName.*.nupkg" -ErrorAction SilentlyContinue
$snupkg = Get-Item "$packagePath/$projectName.*.snupkg" -ErrorAction SilentlyContinue

if (-not $nupkg) {
    Write-Host "✗ No .nupkg file found in $packagePath" -ForegroundColor Red
    exit 1
}

Write-Host "Found packages:" -ForegroundColor Green
Write-Host "  - $($nupkg.Name)"
if ($snupkg) {
    Write-Host "  - $($snupkg.Name)"
}

# Step 3: Push packages
Write-Host "`n[3/3] Pushing packages to idun-corp..." -ForegroundColor Yellow

$pushArgs = @(
    "push",
    $nupkg.FullName,
    "-Source", $feedUrl,
    "-SkipDuplicate"
)

if ($ApiKey) {
    $pushArgs += @("-ApiKey", $ApiKey)
}

try {
    Write-Host "Pushing $($nupkg.Name)..."
    & nuget @pushArgs
    Write-Host "✓ Package pushed successfully" -ForegroundColor Green

    if ($snupkg) {
        Write-Host "`nPushing $($snupkg.Name)..."
        $pushArgs[1] = $snupkg.FullName
        & nuget @pushArgs
        Write-Host "✓ Symbol package pushed successfully" -ForegroundColor Green
    }
}
catch {
    Write-Host "✗ Push failed: $_" -ForegroundColor Red
    exit 1
}

Write-Host "`n=== Push completed successfully! ===" -ForegroundColor Cyan
Write-Host "Feed URL: $feedUrl" -ForegroundColor Gray
