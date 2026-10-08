# sbox Network Storage Server installer for Windows.
#
#   irm https://github.com/sbox-cool/sbox-network-storage-server/releases/latest/download/install.ps1 | iex
#
# Environment variables:
#   SBOX_NS_VERSION      Install this version (for example 0.3.0 or v0.3.0) instead of the latest release.
#   SBOX_NS_PRERELEASE   Set to 1 to resolve the newest release including prereleases.
#   SBOX_NS_NO_SETUP     Set to 1 to skip running `sbox-ns setup`.
#   GITHUB_TOKEN         Optional token used for GitHub API requests (avoids rate limits).
#
# Run from an elevated PowerShell to install for all users into Program Files;
# otherwise the server is installed for the current user only.
#
# Licensed under AGPL-3.0-only. https://github.com/sbox-cool/sbox-network-storage-server

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
Set-StrictMode -Version Latest

function Install-SboxNs {
    $repo = 'sbox-cool/sbox-network-storage-server'
    $binName = 'sbox-ns.exe'

    # Windows PowerShell 5.1 defaults to older TLS versions.
    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

    if (-not [Environment]::Is64BitOperatingSystem) {
        throw 'sbox-ns requires 64-bit Windows.'
    }
    $platform = 'win-x64'
    if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64' -or $env:PROCESSOR_ARCHITEW6432 -eq 'ARM64') {
        Write-Warning 'No native Windows ARM64 build is published; installing win-x64, which runs under x64 emulation.'
    }

    $apiHeaders = @{ 'User-Agent' = 'sbox-ns-installer'; 'Accept' = 'application/vnd.github+json' }
    if ($env:GITHUB_TOKEN) {
        $apiHeaders['Authorization'] = "Bearer $($env:GITHUB_TOKEN)"
    }

    if ($env:SBOX_NS_VERSION) {
        $version = $env:SBOX_NS_VERSION.TrimStart('v')
    }
    elseif ($env:SBOX_NS_PRERELEASE -eq '1') {
        $releases = Invoke-RestMethod -Headers $apiHeaders -Uri "https://api.github.com/repos/$repo/releases?per_page=1"
        $version = ([string]@($releases)[0].tag_name).TrimStart('v')
    }
    else {
        $release = Invoke-RestMethod -Headers $apiHeaders -Uri "https://api.github.com/repos/$repo/releases/latest"
        $version = ([string]$release.tag_name).TrimStart('v')
    }
    if (-not $version) {
        throw 'Could not resolve the latest release. Set SBOX_NS_VERSION to pick one.'
    }

    $isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)
    if ($isAdmin) {
        $installDir = Join-Path $env:ProgramFiles 'sbox-ns'
        $pathScope = 'Machine'
    }
    else {
        $installDir = Join-Path $env:LOCALAPPDATA 'Programs\sbox-ns'
        $pathScope = 'User'
    }
    $configDir = Join-Path $installDir 'config'
    $dataDir = Join-Path $installDir 'data'

    $archive = "sbox-ns-$version-$platform.zip"
    $baseUrl = "https://github.com/$repo/releases/download/v$version"

    Write-Host "Installing sbox Network Storage Server $version ($platform)"

    $tmp = Join-Path ([IO.Path]::GetTempPath()) ("sbox-ns-" + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $tmp | Out-Null
    try {
        $archivePath = Join-Path $tmp $archive
        $sumsPath = Join-Path $tmp 'SHA256SUMS'

        Write-Host "Downloading $archive"
        Invoke-WebRequest -UseBasicParsing -Uri "$baseUrl/$archive" -OutFile $archivePath
        Invoke-WebRequest -UseBasicParsing -Uri "$baseUrl/SHA256SUMS" -OutFile $sumsPath

        $expected = $null
        foreach ($line in Get-Content -Path $sumsPath) {
            $parts = $line -split '\s+', 2
            if ($parts.Count -eq 2 -and $parts[1].TrimStart('*') -eq $archive) {
                $expected = $parts[0].ToLowerInvariant()
                break
            }
        }
        if (-not $expected) {
            throw "$archive is not listed in SHA256SUMS; refusing to install."
        }
        $actual = (Get-FileHash -Algorithm SHA256 -Path $archivePath).Hash.ToLowerInvariant()
        if ($actual -ne $expected) {
            throw "Checksum mismatch for $archive (expected $expected, got $actual); refusing to install."
        }
        Write-Host "Checksum verified: $actual"

        $extractDir = Join-Path $tmp 'extract'
        Expand-Archive -Path $archivePath -DestinationPath $extractDir -Force
        $newBinary = Join-Path $extractDir $binName
        if (-not (Test-Path $newBinary)) {
            throw "Archive does not contain $binName."
        }

        New-Item -ItemType Directory -Force -Path $installDir, $configDir, $dataDir | Out-Null
        $target = Join-Path $installDir $binName
        if (Test-Path $target) {
            # A running executable cannot be overwritten but can be renamed.
            $old = "$target.old"
            Remove-Item -Force -ErrorAction SilentlyContinue $old
            Move-Item -Force $target $old
        }
        Copy-Item -Path (Join-Path $extractDir '*') -Destination $installDir -Recurse -Force
        Write-Host "Installed $target"
    }
    finally {
        Remove-Item -Recurse -Force -ErrorAction SilentlyContinue $tmp
    }

    $currentPath = [Environment]::GetEnvironmentVariable('Path', $pathScope)
    $entries = @()
    if ($currentPath) { $entries = $currentPath -split ';' | Where-Object { $_ } }
    if ($entries -notcontains $installDir) {
        [Environment]::SetEnvironmentVariable('Path', (($entries + $installDir) -join ';'), $pathScope)
        Write-Host "Added $installDir to the $pathScope PATH (open a new terminal to pick it up)."
    }
    if (($env:Path -split ';') -notcontains $installDir) {
        $env:Path = "$env:Path;$installDir"
    }

    $exe = Join-Path $installDir $binName
    $dirArgs = @('--config-dir', $configDir, '--data-dir', $dataDir)
    $configured = $false
    if (Test-Path (Join-Path $configDir 'server.toml')) {
        Write-Host "Existing configuration found in $configDir; keeping it."
        $configured = $true
    }
    elseif ($env:SBOX_NS_NO_SETUP -ne '1' -and [Environment]::UserInteractive -and -not [Console]::IsInputRedirected) {
        Write-Host ''
        Write-Host 'Starting interactive setup...'
        & $exe setup @dirArgs
        if ($LASTEXITCODE -ne 0) {
            Write-Warning "sbox-ns setup exited with code $LASTEXITCODE. Re-run it with: sbox-ns setup --config-dir `"$configDir`" --data-dir `"$dataDir`""
        }
        else {
            $configured = $true
        }
    }

    $quotedDirs = "--config-dir `"$configDir`" --data-dir `"$dataDir`""
    Write-Host ''
    Write-Host "sbox Network Storage Server $version is installed."
    Write-Host ''
    Write-Host "  Binary:  $exe"
    Write-Host "  Config:  $configDir"
    Write-Host "  Data:    $dataDir"
    Write-Host ''
    Write-Host 'Next steps:'
    $step = 1
    if (-not $configured) {
        Write-Host "  $step. Run setup:            sbox-ns setup $quotedDirs"
        $step++
    }
    Write-Host "  $step. Start the server:     sbox-ns start $quotedDirs"
    $step++
    if ($isAdmin) {
        Write-Host "  $step. Or run as a service:  sbox-ns service install $quotedDirs; sbox-ns service start"
        $step++
    }
    Write-Host "  $step. Create a project:     sbox-ns project create `"My Game`" $quotedDirs"
    $step++
    Write-Host "  $step. Create API keys:      sbox-ns key create <projectId> --type public $quotedDirs"
    Write-Host "                            sbox-ns key create <projectId> --type secret $quotedDirs"
    $step++
    Write-Host "  $step. Point your game at http://<this-host>:8080 (see docs/client-setup.md)"
    Write-Host ''
    Write-Host "Docs: https://github.com/$repo#readme"
}

Install-SboxNs
