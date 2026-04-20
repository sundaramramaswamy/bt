# Install bt from GitHub Releases.
# Downloads the latest release for the current architecture and
# places bt.exe in ~/.bt/ (added to the current session's PATH).
# If bt.exe already exists on disk, just adds to PATH without downloading.
param(
    [string]$InstallDir = (Join-Path $HOME '.bt')
)

$ErrorActionPreference = 'Stop'
$btExe = Join-Path $InstallDir 'bt.exe'

# Already on disk — just add to PATH
if (Test-Path $btExe) {
    if ($env:PATH -notlike "*$InstallDir*") {
        $env:PATH = "$InstallDir;$env:PATH"
    }
    $version = & $btExe --version 2>&1
    Write-Host "bt $version (already installed at $InstallDir)" -ForegroundColor Green
    return
}

$owner = 'sundaramramaswamy'
$repo  = 'bt'
$apiUrl = "https://api.github.com/repos/$owner/$repo/releases"

# Detect architecture
$arch = [System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture
$rid = switch ($arch) {
    'X64'   { 'win-x64' }
    'Arm64' { 'win-arm64' }
    default { throw "Unsupported architecture: $arch" }
}
$assetName = "bt-$rid.zip"

# Fetch latest release
$headers = @{ Accept = 'application/vnd.github+json' }
$token = $env:GITHUB_TOKEN
if (-not $token) {
    $credOutput = "protocol=https`nhost=github.com" | git credential fill 2>$null
    $token = ($credOutput | Select-String '^password=(.+)$').Matches |
        ForEach-Object { $_.Groups[1].Value }
}
if ($token) { $headers['Authorization'] = "Bearer $token" }

Write-Host "Fetching latest bt release ..." -ForegroundColor Cyan
$releases = Invoke-RestMethod -Uri $apiUrl -Headers $headers
if (-not $releases -or $releases.Count -eq 0) {
    throw "No releases found at $apiUrl"
}
$release = $releases[0]

$asset = $release.assets | Where-Object { $_.name -eq $assetName }
if (-not $asset) {
    throw "Asset $assetName not found in release $($release.tag_name)"
}

# Download and extract
$tempDir = Join-Path ([System.IO.Path]::GetTempPath()) 'bt-install'
New-Item -ItemType Directory -Path $tempDir -Force | Out-Null
$zipPath = Join-Path $tempDir $assetName

Write-Host "Downloading $assetName ..." -ForegroundColor Cyan
Invoke-WebRequest -Uri $asset.browser_download_url -Headers $headers -OutFile $zipPath

# Verify SHA256 checksum if available
$shaAsset = $release.assets | Where-Object { $_.name -eq "$assetName.sha256" }
if ($shaAsset) {
    $expected = (Invoke-RestMethod -Uri $shaAsset.browser_download_url -Headers $headers).Trim().Split()[0]
    $actual = (Get-FileHash $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $expected) {
        Remove-Item $tempDir -Recurse -Force -ErrorAction SilentlyContinue
        throw "Checksum mismatch for $assetName (expected $expected, got $actual)"
    }
}

New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
Expand-Archive -Path $zipPath -DestinationPath $InstallDir -Force

# Clean up
Remove-Item $tempDir -Recurse -Force -ErrorAction SilentlyContinue

# Add to PATH for current session
if ($env:PATH -notlike "*$InstallDir*") {
    $env:PATH = "$InstallDir;$env:PATH"
}

$version = & $btExe --version 2>&1
Write-Host "Installed bt $version to $InstallDir" -ForegroundColor Green
Write-Host "Add $InstallDir to your PATH for future sessions." -ForegroundColor DarkGray
