# Publish bt NativeAOT binaries to a NuGet feed
# Run from repo root: tools\scripts\publish.ps1 [-RID <rid>] [-Commit <hash>]
#
# First run creates tools\scripts\feed.config with your feed details.
# feed.config is gitignored — no corp URLs in the repo.
#
# By default builds win-x64 and win-arm64. Pass -RID to build only one.
# Pass -PackOnly to build and pack without publishing to feed or GitHub.
# Pass -SkipFeed to skip NuGet feed push (build + GitHub release only).
# Pass -Commit to checkout and build a specific commit (requires clean tree).
# Set GITHUB_TOKEN env var to create a GitHub release with zip assets.
# Falls back to Git Credential Manager when GITHUB_TOKEN is unset.
param(
    [string[]]$RID = @('win-x64', 'win-arm64'),
    [switch]$PackOnly,
    [switch]$SkipFeed,
    [string]$Commit
)

$ErrorActionPreference = 'Stop'
$root = git rev-parse --show-toplevel
Push-Location $root

# --- Checkout specific commit if requested ---
$originalRef = $null
if ($Commit) {
    $dirty = git diff --name-only HEAD
    $staged = git diff --name-only --cached
    if ($dirty -or $staged) {
        Write-Host "Tracked files have uncommitted changes — commit or stash first." -ForegroundColor Red
        @($dirty) + @($staged) | Sort-Object -Unique |
            ForEach-Object { Write-Host "  $_" -ForegroundColor DarkGray }
        exit 1
    }
    $resolved = git rev-parse --verify "$Commit^{commit}" 2>$null
    if ($LASTEXITCODE -ne 0) {
        Write-Host "Not a valid commit: $Commit" -ForegroundColor Red
        exit 1
    }
    $originalRef = git symbolic-ref --short HEAD 2>$null
    if (-not $originalRef) { $originalRef = git rev-parse HEAD }
    Write-Host "Checking out $($resolved.Substring(0,7)) ..." -ForegroundColor Yellow
    git checkout --quiet $resolved
}

# --- Preflight: check HEAD is pushed to origin ---
if (-not $PackOnly) {
    git fetch origin --quiet 2>$null
    $onRemote = git merge-base --is-ancestor HEAD origin/main 2>$null
    if ($LASTEXITCODE -ne 0) {
        Write-Host "HEAD not pushed to origin — push first." -ForegroundColor Red
        exit 1
    }
}

# --- Preflight: check for cross-compile prerequisites ---
$hostArch = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLowerInvariant()
$needsCross = $RID | Where-Object { $_.Split('-')[1] -ne $hostArch }
if ($needsCross -and -not (Get-Command nuget -ErrorAction SilentlyContinue)) {
    $hostPkg = "runtime.win-$hostArch.microsoft.dotnet.ilcompiler"
    $hostPkgDir = Join-Path $env:USERPROFILE ".nuget\packages\$hostPkg"
    if (-not (Test-Path $hostPkgDir)) {
        throw "Cross-compile needs nuget.exe in PATH (winget install Microsoft.NuGet)"
    }
}

try {
    # --- Feed config (skip in pack-only or skip-feed mode) ---
    $feedName = $null
    if (-not $PackOnly -and -not $SkipFeed) {
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
    }

    # --- Version from git ---
    $count = git rev-list --count HEAD
    $hash = git rev-parse --short HEAD
    $version = "1.0.0-ci.$count.$hash"

    # --- Cross-compile setup ---
    # $hostArch already set in preflight
    $json = dotnet msbuild src\Bt\Bt.csproj -getItem:KnownILCompilerPack 2>$null | ConvertFrom-Json
    $ilcVersion = ($json.Items.KnownILCompilerPack |
        Where-Object { $_.TargetFramework -eq 'net8.0' }).ILCompilerPackVersion
    $hostPkg = "runtime.win-$hostArch.microsoft.dotnet.ilcompiler"
    $hostPkgPath = Join-Path $env:USERPROFILE ".nuget\packages\$hostPkg\$ilcVersion"

    if (-not (Test-Path $hostPkgPath)) {
        Write-Host "Downloading $hostPkg $ilcVersion ..." -ForegroundColor Yellow
        nuget install $hostPkg -Version $ilcVersion -OutputDirectory (
            Join-Path $env:USERPROFILE '.nuget\packages') -NonInteractive
        if ($LASTEXITCODE -ne 0) { throw "nuget install $hostPkg failed" }
    }

    # --- Publish NativeAOT for each RID ---
    $staging = Join-Path $root 'staging'
    if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }

    foreach ($r in $RID) {
        Write-Host "Publishing $r ..." -ForegroundColor Cyan
        $out = Join-Path $staging $r
        $targetArch = $r.Split('-')[1]
        # Clean intermediate state from prior RID
        $objDir = Join-Path $root 'src\Bt\obj'
        if (Test-Path $objDir) { Remove-Item $objDir -Recurse -Force }
        $publishArgs = @(
            'publish', 'src\Bt', '-c', 'Release', '-r', $r, '-o', $out,
            "-p:Version=$version", "-p:PlatformTarget=$targetArch",
            "-p:IlcHostPackagePath=$hostPkgPath"
        )
        dotnet @publishArgs
        if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $r" }
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

    # --- Zip per-RID binaries (after pack to avoid cleanup) ---
    foreach ($r in $RID) {
        $zip = Join-Path $staging "bt-$r.zip"
        Compress-Archive -Path (Join-Path $staging "$r\bt.exe") -DestinationPath $zip
    }

    Write-Host "Pushing $pkg ..." -ForegroundColor Cyan
    if ($PackOnly) {
        Write-Host "Pack-only mode — skipping feed push and GitHub release" -ForegroundColor Yellow
        Write-Host "`nPacked Bt $version ($($RID -join ', '))" -ForegroundColor Green
        Write-Host "Artifacts: $staging" -ForegroundColor DarkGray
        return
    }
    if (-not $SkipFeed) {
        dotnet nuget push $pkg --source $feedName --api-key az
        if ($LASTEXITCODE -ne 0) { throw "dotnet nuget push failed" }
    } else {
        Write-Host "Skipping feed push (-SkipFeed)" -ForegroundColor Yellow
    }

    # --- GitHub release (via REST API, no gh CLI needed) ---
    $ghToken = $env:GITHUB_TOKEN
    if (-not $ghToken) {
        # Fall back to Git Credential Manager (same store git push uses)
        $credOutput = "protocol=https`nhost=github.com" | git credential fill 2>$null
        $ghToken = ($credOutput | Select-String '^password=(.+)$').Matches |
            ForEach-Object { $_.Groups[1].Value }
    }
    if (-not $ghToken) {
        Write-Host "No GitHub token (set GITHUB_TOKEN or configure git credential manager)" -ForegroundColor Yellow
    } else {
        Write-Host "Creating GitHub release $version ..." -ForegroundColor Cyan
        $tag = "v$version"
        # Extract release notes from CHANGELOG at branch tip (not checked-out commit)
        $clRef = if ($originalRef) { $originalRef } else { 'HEAD' }
        $cl = git show "${clRef}:CHANGELOG.md" 2>$null
        if (-not $cl) { $cl = Get-Content (Join-Path $root 'CHANGELOG.md') -Raw }
        $commitHash = $hash
        $m = [regex]::Match($cl, "(?ms)^## \[$commitHash\]\s*\n(.*?)(?=^## \[|\z)")
        if (-not $m.Success) {
            $m = [regex]::Match($cl, '(?ms)^## \[.+?\]\s*\n(.*?)(?=^## \[|\z)')
        }
        $notes = if ($m.Success) { $m.Groups[1].Value.Trim() } else { '' }

        $headers = @{
            Authorization = "Bearer $ghToken"
            Accept        = 'application/vnd.github+json'
        }
        $owner = 'sundaramramaswamy'
        $repo  = 'bt'
        $body  = @{
            tag_name         = $tag
            target_commitish = (git rev-parse HEAD)
            name             = $tag
            body             = $notes
            prerelease       = $true
        } | ConvertTo-Json

        try {
            $release = Invoke-RestMethod `
                -Uri "https://api.github.com/repos/$owner/$repo/releases" `
                -Method Post -Headers $headers -Body $body `
                -ContentType 'application/json'

            # Upload per-RID zip assets
            $uploadBase = $release.upload_url -replace '\{.*\}', ''
            foreach ($r in $RID) {
                $zip = Join-Path $staging "bt-$r.zip"
                $name = "bt-$r.zip"
                $bytes = [System.IO.File]::ReadAllBytes($zip)
                Invoke-RestMethod `
                    -Uri "${uploadBase}?name=$name" `
                    -Method Post -Headers $headers `
                    -ContentType 'application/zip' `
                    -Body $bytes | Out-Null
            }
        } catch {
            Write-Host "GitHub release failed: $_" -ForegroundColor Yellow
            Write-Host "  (feed push succeeded)" -ForegroundColor Yellow
        }
    }

    Write-Host "`nPublished Bt $version ($($RID -join ', '))" -ForegroundColor Green
    Write-Host "Install:  nuget install Bt -Version $version -Source $feedName -OutputDirectory ~\tools" -ForegroundColor DarkGray
}
finally {
    if ($originalRef) {
        Write-Host "Restoring $originalRef ..." -ForegroundColor Yellow
        git checkout --quiet $originalRef
    }
    Pop-Location
    if (-not $PackOnly -and (Test-Path (Join-Path $root 'staging'))) {
        Remove-Item (Join-Path $root 'staging') -Recurse -Force
    }
}
