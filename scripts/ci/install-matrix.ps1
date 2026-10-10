# Real install of a candidate release with install.ps1 on a disposable Windows machine (CI only).
#
#   powershell.exe -File scripts/ci/install-matrix.ps1 -Fixture DIR -Candidate VERSION
#
# DIR comes from scripts/ci/build-release-fixture.sh. Run from an elevated shell: this trusts the fixture CA,
# points github.com, api.github.com and sboxcool.com at a local fixture server in the hosts file, and runs the
# candidate install.ps1 under the PowerShell that runs this script (use powershell.exe for Windows PowerShell 5.1).
param(
    [Parameter(Mandatory = $true)][string]$Fixture,
    [Parameter(Mandatory = $true)][string]$Candidate
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Step($text) { Write-Host "`n== $text" }
function Wait-Health($url) {
    for ($i = 0; $i -lt 60; $i++) {
        try { Invoke-WebRequest -UseBasicParsing -Uri "$url/health" -TimeoutSec 2 | Out-Null; return } catch { Start-Sleep -Seconds 1 }
    }
    throw "no health response from $url"
}

Write-Host "PowerShell $($PSVersionTable.PSVersion) ($($PSVersionTable.PSEdition))"
$Fixture = (Resolve-Path $Fixture).Path

Step 'Trust the fixture and route release hosts to it'
Import-Certificate -FilePath (Join-Path $Fixture 'tls\ca.crt') -CertStoreLocation Cert:\LocalMachine\Root | Out-Null
$hosts = Join-Path $env:SystemRoot 'System32\drivers\etc\hosts'
Copy-Item $hosts "$hosts.install-matrix" -Force
Add-Content -Path $hosts -Value "`r`n127.0.0.1 github.com api.github.com sboxcool.com"
$python = (Get-Command python).Source
$server = Start-Process -FilePath $python -PassThru -NoNewWindow `
    -RedirectStandardError (Join-Path $env:RUNNER_TEMP 'release-fixture.log') `
    -ArgumentList @((Join-Path $PSScriptRoot 'release_fixture.py'), 'serve', '--root', $Fixture,
        '--cert', (Join-Path $Fixture 'tls\server.crt'), '--key', (Join-Path $Fixture 'tls\server.key'))
try {
    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
    $installer = "https://github.com/sbox-cool/sbox-network-storage-server/releases/download/v$Candidate/install.ps1"
    for ($i = 0; $i -lt 30; $i++) {
        try { Invoke-WebRequest -UseBasicParsing -Uri $installer -TimeoutSec 2 | Out-Null; break } catch { Start-Sleep -Milliseconds 500 }
    }

    Step "Install candidate v$Candidate with install.ps1"
    $env:SBOX_NS_VERSION = $Candidate
    $env:SBOX_NS_PROJECT = 'Install Matrix'
    # Exactly the documented command: irm <url> | iex.
    Invoke-RestMethod -Uri $installer | Invoke-Expression
    $exe = Join-Path $env:ProgramFiles 'sbox-ns\sbox-ns.exe'
    if (-not (Test-Path $exe)) { throw "sbox-ns.exe was not installed to $exe" }
    $dirs = @('--config-dir', (Join-Path $env:ProgramFiles 'sbox-ns\config'), '--data-dir', (Join-Path $env:ProgramFiles 'sbox-ns\data'))

    Step 'Run it as a Windows service'
    & $exe service install @dirs
    if ($LASTEXITCODE -ne 0) { throw 'service install failed' }
    & $exe service start @dirs
    if ($LASTEXITCODE -ne 0) { throw 'service start failed' }
    Wait-Health 'http://127.0.0.1:8080'
    $info = Invoke-RestMethod -Uri 'http://127.0.0.1:8080/v3/server-info'
    if ($info.version -ne $Candidate) { throw "expected running version $Candidate, got $($info.version)" }
    $projects = & $exe project list @dirs | Out-String
    if ($projects -notmatch 'Install Matrix') { throw "project 'Install Matrix' is missing" }
    & $exe service stop @dirs
    Write-Host "INSTALL MATRIX PASS (Windows, PowerShell $($PSVersionTable.PSVersion), v$Candidate)"
}
finally {
    Stop-Process -Id $server.Id -Force -ErrorAction SilentlyContinue
    Copy-Item "$hosts.install-matrix" $hosts -Force
}
