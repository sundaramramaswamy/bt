# Publish bt NativeAOT binaries to a NuGet feed
# Run from repo root: tools\scripts\publish.ps1 [-RID <rid>]
#
# First run creates tools\scripts\feed.config with your feed details.
# feed.config is gitignored — no corp URLs in the repo.
#
# By default builds win-x64 and win-arm64. Pass -RID to build only one.
param(
    [string[]]$RID = @('win-x64', 'win-arm64')
)

$ErrorActionPreference = 'Stop'
$root = git rev-parse --show-toplevel
Push-Location $root

try {
    # --- Feed config ---
    $configPath = Join-Path $root 'tools\scripts\feed.config'
    if (-not (Test-Path $configPath)) {
        Write-Host "No feed configured. Let's set one up." -ForegroundColor Yellow
        $name = Read-Host "Feed name (e.g. my-tools)"
        $url  = Read-Host "Feed URL  (e.g. https://pkgs.dev.azure.com/org/project/_packaging/feed/nuget/v3/index.json)"
        @{ Name = $name; Url = $url } | ConvertTo-Json | Set-Content $configPath
        Write-Host "Saved to $configPath (gitignored)" -ForegroundColor Green
    }

    $config = Get-Content $configPath | ConvertFrom-Json
    $feedName = $config.Name
    $feedUrl  = $config.Url

    # Ensure feed is registered
    $sources = dotnet nuget list source 2>&1 | Out-String
    $namePattern = "\b$([regex]::Escape($feedName))\b"
    if ($sources -notmatch [regex]::Escape($feedUrl) -and $sources -notmatch $namePattern) {
        Write-Host "Adding NuGet source '$feedName' ..." -ForegroundColor Yellow
        dotnet nuget add source $feedUrl --name $feedName
    }

    # --- Version from git ---
    $count = git rev-list --count HEAD
    $hash = git rev-parse --short HEAD
    $version = "1.0.0-ci.$count.$hash"

    # --- Publish NativeAOT for each RID ---
    $staging = Join-Path $root 'staging'
    if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }

    foreach ($rid in $RID) {
        Write-Host "Publishing $rid ..." -ForegroundColor Cyan
        $out = Join-Path $staging $rid
        dotnet publish src\Bt -c Release -r $rid -o $out -p:Version=$version
        if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $rid" }
        Write-Host "  $out\bt.exe" -ForegroundColor Green
    }

    # --- Pack nupkg via dotnet pack with custom nuspec ---
    Write-Host "Packing Bt $version ..." -ForegroundColor Cyan
    $nuspecPath = Join-Path $root 'tools\scripts\bt.nuspec'
    # Restore without a RID so the assets file has a plain net8.0 target
    # (the publish steps restore with -r, leaving only RID-specific targets).
    dotnet restore src\Bt\Bt.csproj -v quiet
    dotnet pack src\Bt\Bt.csproj --no-build --no-restore -c Release `
        /p:NuspecFile="$nuspecPath" `
        /p:NuspecBasePath="$root" `
        /p:NuspecProperties="version=$version" `
        -o $staging
    if ($LASTEXITCODE -ne 0) { throw "dotnet pack failed" }
    $pkg = Join-Path $staging "Bt.$version.nupkg"

    Write-Host "Pushing $pkg ..." -ForegroundColor Cyan
    dotnet nuget push $pkg --source $feedName --api-key az
    if ($LASTEXITCODE -ne 0) { throw "dotnet nuget push failed" }

    Write-Host "`nPublished Bt $version ($($RID -join ', '))" -ForegroundColor Green
    Write-Host "Install:  nuget install Bt -Version $version -Source $feedName -OutputDirectory ~\tools" -ForegroundColor DarkGray
}
finally {
    Pop-Location
    if (Test-Path (Join-Path $root 'staging')) {
        Remove-Item (Join-Path $root 'staging') -Recurse -Force
    }
}
