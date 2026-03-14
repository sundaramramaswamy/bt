# Pack and push bt to NuGet feed
# Run from repo root: tools\scripts\publish.ps1
#
# First run creates tools\scripts\feed.config with your feed details.
# feed.config is gitignored — no corp URLs in the repo.
$ErrorActionPreference = 'Stop'
$root = git rev-parse --show-toplevel
Push-Location $root

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

# Ensure feed is registered (check by name or URL)
$sources = dotnet nuget list source
if ($sources -notmatch [regex]::Escape($feedUrl) -and $sources -notmatch [regex]::Escape($feedName)) {
    Write-Host "Adding NuGet source '$feedName' ..." -ForegroundColor Yellow
    dotnet nuget add source $feedUrl --name $feedName
}

$count = git rev-list --count HEAD
$hash = git rev-parse --short HEAD
$version = "1.0.0-ci.$count.$hash"

Write-Host "Packing Bt $version ..." -ForegroundColor Cyan
dotnet pack src\Bt -c Release -p:Version=$version
if ($LASTEXITCODE -ne 0) { Pop-Location; exit 1 }

$pkg = "src\Bt\bin\Release\Bt.$version.nupkg"
Write-Host "Pushing $pkg ..." -ForegroundColor Cyan
dotnet nuget push $pkg --source $feedName --api-key az

Pop-Location
