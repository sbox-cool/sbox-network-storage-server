param(
    [string]$Postgres,
    [string]$Binary,
    [string]$Out,
    [int]$Port = 18421,
    [switch]$NoBuild
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$work = Join-Path ([IO.Path]::GetTempPath()) ('sbox-ns-smoke-' + [Guid]::NewGuid().ToString('N'))
$server = $null
function Invoke-Checked([string]$Exe, [string[]]$Arguments) {
    & $Exe @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Exe exited with $LASTEXITCODE" }
}
Push-Location $root
try {
    if (!$NoBuild) { Invoke-Checked dotnet @('build', 'SboxNetworkStorage.sln', '-c', 'Release') }
    $exe = 'dotnet'
    $prefix = @((Join-Path $root 'src/SboxNetworkStorage.Server/bin/Release/net8.0/sbox-ns.dll'))
    if ($Binary) {
        $Binary = [IO.Path]::GetFullPath($Binary)
        if ($Binary.EndsWith('.dll')) { $prefix = @($Binary) } else { $exe = $Binary; $prefix = @() }
    }
    New-Item -ItemType Directory -Path $work | Out-Null
    $common = @('--config-dir', (Join-Path $work 'config'), '--data-dir', (Join-Path $work 'data'))
    $setup = @('setup', '--non-interactive', '--listen', "127.0.0.1:$Port")
    if ($Postgres) { $setup += @('--database', 'postgres', '--pg-connection-string', $Postgres) }
    Invoke-Checked $exe ($prefix + $setup + $common)
    $created = Invoke-Checked $exe ($prefix + @('project', 'create', 'HTTP Parity Smoke', '--key-mode', 'public', '--require-sbox-auth', 'false') + $common)
    $project = ($created | Select-String '^Project ID: (.+)$').Matches.Groups[1].Value
    if (!$project) { throw 'Cannot parse project id' }
    function New-SmokeKey([string]$Type) {
        $lines = Invoke-Checked $exe ($prefix + @('key', 'create', $project, '--type', $Type) + $common)
        $match = $lines | Select-String '^\s+(sbox_\S+)\s*$'
        if (!$match) { throw 'Cannot parse CLI key output' }
        return $match.Matches.Groups[1].Value
    }
    $public = New-SmokeKey public
    $secret = New-SmokeKey secret
    # ProcessStartInfo.ArgumentList preserves paths containing spaces on all platforms.
    $info = [Diagnostics.ProcessStartInfo]::new($exe)
    $info.UseShellExecute = $false
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    foreach ($arg in ($prefix + @('start') + $common)) { $info.ArgumentList.Add($arg) }
    $server = [Diagnostics.Process]::Start($info)
    $stdout = $server.StandardOutput.ReadToEndAsync()
    $stderr = $server.StandardError.ReadToEndAsync()
    $healthy = $false
    for ($i = 0; $i -lt 120; $i++) {
        if ($server.HasExited) { break }
        try {
            $health = Invoke-WebRequest "http://127.0.0.1:$Port/health" -TimeoutSec 2
            if ($health.StatusCode -eq 200) { $healthy = $true; break }
        } catch { }
        Start-Sleep -Milliseconds 500
    }
    if (!$healthy) { throw 'Server did not become healthy' }
    $arguments = @((Join-Path $root 'tools/SboxNetworkStorage.Parity/bin/Release/net8.0/sbox-ns-parity.dll'),
        'check', '--target', "http://127.0.0.1:$Port", '--project-id', $project,
        '--public-key', $public, '--secret-key', $secret, '--corpus', (Join-Path $root 'tests/parity/corpus'))
    if ($Out) { $arguments += @('--out', $Out) }
    Invoke-Checked dotnet $arguments
} finally {
    if ($server) {
        if (!$server.HasExited) { $server.Kill($true) }
        $server.WaitForExit()
        Write-Host 'Server log:'
        Write-Host $stdout.GetAwaiter().GetResult()
        Write-Host $stderr.GetAwaiter().GetResult()
        $server.Dispose()
    }
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
    Pop-Location
}
